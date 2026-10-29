using System.Reflection;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Net;
using ArcaneCore.World.Net.SessionTrace;
using Xunit;

namespace ArcaneCore.World.Tests.Net.SessionTrace;

/// <summary>
/// "Zero cost when nothing is traced" (session-trace spec, "Cost"): the entry points the world drives for every packet, tick, aura and kill
/// allocate nothing while the registry is empty, and nothing for a character that is not the traced one while another is traced.
/// Deterministic: each window is warmed once by <see cref="AllocationProbe"/> and must be GC-free-clean at exactly 0 bytes.
/// </summary>
public sealed class SessionTraceAllocationTests
{
    private const int Iterations = 200;

    // CapturePacket is private; a ReadOnlySpan parameter cannot go through MethodInfo.Invoke, so bind an open-instance delegate of a matching custom type.
    private delegate void CapturePacketDelegate(WorldSession session, bool fromClient, WorldOpcode opcode, ReadOnlySpan<byte> payload);

    private static readonly CapturePacketDelegate CapturePacket = (CapturePacketDelegate)Delegate.CreateDelegate(
        typeof(CapturePacketDelegate),
        typeof(WorldSession).GetMethod("CapturePacket", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException("WorldSession.CapturePacket moved"));

    private static readonly byte[] PingPayload = [0x34, 0x12, 0, 0, 0x2A, 0, 0, 0];

    private static async Task<(WorldTestHost Host, WorldTestClient Gm, WorldTestClient Other, SessionTraceFeature Feature, SpellAuraHolder Holder, Player Victim, WorldSession Session)> SetUpAsync(CapturingLoggerProvider logs)
    {
        WorldTestHost host = SessionTraceTestKit.Start(new FakeTimeProvider(), logs);
        WorldTestClient gm = await host.EnterWorldAsync("STAL0", "Allocgm", AccountSecurity.GameMaster);
        WorldTestClient other = await host.EnterWorldAsync("STAL1", "Allocother");
        SpellSystem spells = await SessionTraceTestKit.InstallAuraAsync(host);
        (Player victim, SpellAuraHolder holder) = await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Allocother")!;
            Assert.True(spells.AddAura(player, SessionTraceTestKit.AuraSpell));
            return (player, spells.GetAuras(player).Single(h => h.Spell.Id == SessionTraceTestKit.AuraSpell));
        });
        return (host, gm, other, SessionTraceTestKit.FeatureOf(host), holder, victim, SessionTraceTestKit.SessionOf(host, "Allocother"));
    }

    /// <summary>Everything the world feeds a trace feature and a session, for a character that is not traced.</summary>
    private static Action Window(SessionTraceFeature feature, WorldSession session, SpellAuraHolder holder, Player victim, bool tick) => () =>
    {
        for (int i = 0; i < Iterations; i++)
        {
            CapturePacket(session, true, WorldOpcode.CmsgPing, PingPayload); // the inbound hook in CapturePacket
            _ = session.SessionTrace;                                       // the guard read at every session site
            if (tick) feature.OnUpdated(16);                                // the world.Updated subscription (only with an empty registry: a traced one polls on the world thread)
            feature.OnHolderAdded(holder);                                  // SpellSystem.HolderAdded
            feature.OnHolderRemoved(holder);                                // SpellSystem.HolderRemoved
            feature.OnUnitKilled(null, victim);                             // MapCombat.UnitKilled
        }
    };

    [Fact]
    public async Task WhenNothingIsTraced_EveryEntryPointAllocatesNothing()
    {
        var logs = new CapturingLoggerProvider();
        (WorldTestHost host, WorldTestClient gm, WorldTestClient other, SessionTraceFeature feature, SpellAuraHolder holder, Player victim, WorldSession session) = await SetUpAsync(logs);
        await using (host)
        await using (gm)
        await using (other)
        {
            Assert.Empty(await host.OnWorldAsync(() => feature.Active.ToArray()));
            Assert.Null(session.SessionTrace);

            AllocationProbe.AssertEveryCleanWindowAtMost(Window(feature, session, holder, victim, tick: true), 0, "untraced session-trace entry points, empty registry");

            Assert.Empty(SessionTraceTestKit.Lines(logs));
        }
    }

    [Fact]
    public async Task WhileAnotherCharacterIsTraced_AnUntracedCharactersEntryPointsStillAllocateNothing()
    {
        var logs = new CapturingLoggerProvider();
        (WorldTestHost host, WorldTestClient gm, WorldTestClient other, SessionTraceFeature feature, SpellAuraHolder holder, Player victim, WorldSession session) = await SetUpAsync(logs);
        await using (host)
        await using (gm)
        await using (other)
        {
            await gm.CollectAsync();
            Assert.StartsWith("Session trace on for Allocgm", await SessionTraceTestKit.CommandAsync(gm, ".debug trace session on"), StringComparison.Ordinal);
            Assert.Single(await host.OnWorldAsync(() => feature.Active.ToArray()));
            Assert.Null(session.SessionTrace); // the traced one is the GM, not Allocother

            AllocationProbe.AssertEveryCleanWindowAtMost(Window(feature, session, holder, victim, tick: false), 0, "untraced character's entry points, registry holds another character");

            Assert.DoesNotContain(SessionTraceTestKit.Lines(logs), l => l.Character == "Allocother"); // and none of it reached the log
        }
    }
}
