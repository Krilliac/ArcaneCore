using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.World.Tests.Duel.DuelWorldHost;

namespace ArcaneCore.World.Tests.Duel;

/// <summary>
/// Duels over loopback sessions (vmangos DuelHandler.cpp:30-71, SpellEffects.cpp:4650-4761, Player.cpp:6726-6822): the challenge spell, the countdown on a
/// stepped clock, same-team melee once the flag is on, the 1 hp finish, /forfeit and a disconnect. The 1.12 client's own rendering of the flag, the
/// countdown and the hostile name plates is not covered: this lane is tested against the references and synthetic clients, not client-verified.
/// </summary>
public sealed class DuelEndToEndTests
{
    private static byte[] Guid(ulong guid) => BitConverter.GetBytes(guid);

    private static async Task<(WorldTestHost Host, ManualTimeProvider Clock, WorldTestClient A, WorldTestClient B, ulong GuidA, ulong GuidB)> StartAsync()
    {
        ManualTimeProvider clock = NewClock();
        WorldTestHost host = Start(clock);
        WorldTestClient a = await host.EnterWorldAsync("DUELA", "Duela");
        WorldTestClient b = await host.EnterWorldAsync("DUELB", "Duelb");
        await a.ReadUpdateAsync();
        await b.ReadUpdateAsync();
        ulong ga = await host.PlayerStateAsync("Duela", p => p.Guid.Value);
        ulong gb = await host.PlayerStateAsync("Duelb", p => p.Guid.Value);
        return (host, clock, a, b, ga, gb);
    }

    [Fact]
    public async Task ChallengeAcceptCountdownMelee_AndTheLethalHitEndsAtOneHealth()
    {
        (WorldTestHost host, ManualTimeProvider clock, WorldTestClient a, WorldTestClient b, ulong ga, ulong gb) = await StartAsync();
        await using WorldTestHost h = host;
        await using WorldTestClient ca = a;
        await using WorldTestClient cb = b;

        await a.SendAsync(WorldOpcode.CmsgCastSpell, CastPayload(DuelSpell, gb));
        byte[] requestedA = await a.ReadUntilAsync(WorldOpcode.SmsgDuelRequested);
        byte[] requestedB = await b.ReadUntilAsync(WorldOpcode.SmsgDuelRequested);
        Assert.Equal(requestedA, requestedB);
        ulong flag = await host.PlayerStateAsync("Duela", p => p.DuelArbiter);
        Assert.NotEqual(0ul, flag);
        Assert.Equal([.. Guid(flag), .. Guid(ga)], requestedA);

        // The initiator cannot accept its own challenge: nothing starts.
        await a.SendAsync(WorldOpcode.CmsgDuelAccepted, Guid(flag));
        Assert.Equal(0L, await host.PlayerStateAsync("Duela", p => p.Duel!.StartTimerSeconds));

        // A same-team swing is refused before the flag is on.
        await a.SendAsync(WorldOpcode.CmsgAttackswing, Guid(gb));
        await a.ReadUntilAsync(WorldOpcode.SmsgAttackstop);

        await b.SendAsync(WorldOpcode.CmsgDuelAccepted, Guid(flag));
        Assert.Equal([0xB8, 0x0B, 0, 0], await a.ReadUntilAsync(WorldOpcode.SmsgDuelCountdown));
        Assert.Equal([0xB8, 0x0B, 0, 0], await b.ReadUntilAsync(WorldOpcode.SmsgDuelCountdown));

        clock.Advance(3);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Duela")!.DuelTeam != 0, "the duel flag turned on");
        Assert.Equal([1u, 2u], new[]
        {
            await host.PlayerStateAsync("Duela", p => p.DuelTeam),
            await host.PlayerStateAsync("Duelb", p => p.DuelTeam),
        }.Order());

        await a.SendAsync(WorldOpcode.CmsgAttackswing, Guid(gb));
        byte[] attackStart = [.. Guid(ga), .. Guid(gb)];
        Assert.Equal(attackStart, await a.ReadUntilAsync(WorldOpcode.SmsgAttackstart));
        Assert.Equal(attackStart, await b.ReadUntilAsync(WorldOpcode.SmsgAttackstart));

        await host.OnWorldAsync(() =>
        {
            Player pa = host.World.FindOnlinePlayer("Duela")!;
            Player pb = host.World.FindOnlinePlayer("Duelb")!;
            pa.Map!.Combat.DealDamage(pa, pb, 1_000_000);
        });

        Assert.Equal(1u, await host.PlayerStateAsync("Duelb", p => p.Health));
        Assert.Equal([1], await a.ReadUntilAsync(WorldOpcode.SmsgDuelComplete));
        Assert.Equal([1], await b.ReadUntilAsync(WorldOpcode.SmsgDuelComplete));
        byte[] winner = [0, .. "Duela"u8, 0, .. "Duelb"u8, 0];
        Assert.Equal(winner, await a.ReadUntilAsync(WorldOpcode.SmsgDuelWinner));
        Assert.Equal(Guid(flag), (await a.ReadUntilAsync(WorldOpcode.SmsgDestroyObject))[..8]);
        Assert.Equal(0ul, await host.PlayerStateAsync("Duelb", p => p.DuelArbiter));
    }

    [Fact]
    public async Task Forfeit_AfterTheStart_GivesTheOpponentTheWin()
    {
        (WorldTestHost host, ManualTimeProvider clock, WorldTestClient a, WorldTestClient b, ulong _, ulong gb) = await StartAsync();
        await using WorldTestHost h = host;
        await using WorldTestClient ca = a;
        await using WorldTestClient cb = b;
        await a.SendAsync(WorldOpcode.CmsgCastSpell, CastPayload(DuelSpell, gb));
        await a.ReadUntilAsync(WorldOpcode.SmsgDuelRequested);
        ulong flag = await host.PlayerStateAsync("Duela", p => p.DuelArbiter);
        await b.SendAsync(WorldOpcode.CmsgDuelAccepted, Guid(flag));
        await b.ReadUntilAsync(WorldOpcode.SmsgDuelCountdown);
        clock.Advance(3);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Duela")!.DuelTeam != 0, "started");

        await a.SendAsync(WorldOpcode.CmsgDuelCancelled, Guid(flag)); // /forfeit

        Assert.Equal([1], await b.ReadUntilAsync(WorldOpcode.SmsgDuelComplete));
        byte[] winner = [0, .. "Duelb"u8, 0, .. "Duela"u8, 0];
        Assert.Equal(winner, await b.ReadUntilAsync(WorldOpcode.SmsgDuelWinner));
        Assert.Equal(0ul, await host.PlayerStateAsync("Duela", p => p.DuelArbiter));
        Assert.Equal(0u, await host.PlayerStateAsync("Duelb", p => p.DuelTeam));
    }

    [Fact]
    public async Task DiscardingTheRequest_CompletesInterrupted()
    {
        (WorldTestHost host, _, WorldTestClient a, WorldTestClient b, ulong _, ulong gb) = await StartAsync();
        await using WorldTestHost h = host;
        await using WorldTestClient ca = a;
        await using WorldTestClient cb = b;
        await a.SendAsync(WorldOpcode.CmsgCastSpell, CastPayload(DuelSpell, gb));
        await b.ReadUntilAsync(WorldOpcode.SmsgDuelRequested);
        ulong flag = await host.PlayerStateAsync("Duela", p => p.DuelArbiter);

        await b.SendAsync(WorldOpcode.CmsgDuelCancelled, Guid(flag));

        Assert.Equal([0], await a.ReadUntilAsync(WorldOpcode.SmsgDuelComplete));
        Assert.Equal([0], await b.ReadUntilAsync(WorldOpcode.SmsgDuelComplete));
        Assert.Equal(Guid(flag), (await a.ReadUntilAsync(WorldOpcode.SmsgDestroyObject))[..8]);
    }

    [Fact]
    public async Task ShortPayloads_AndPacketsWithoutADuel_AreIgnored()
    {
        (WorldTestHost host, _, WorldTestClient a, WorldTestClient b, ulong _, ulong gb) = await StartAsync();
        await using WorldTestHost h = host;
        await using WorldTestClient ca = a;
        await using WorldTestClient cb = b;

        await a.SendAsync(WorldOpcode.CmsgDuelAccepted, Guid(1)); // no duel at all
        await a.SendAsync(WorldOpcode.CmsgDuelCancelled, Guid(1));
        await a.SendAsync(WorldOpcode.CmsgDuelAccepted, [1, 2, 3]); // short
        await a.SendAsync(WorldOpcode.CmsgDuelCancelled, []);
        await a.SendAsync(WorldOpcode.CmsgCastSpell, CastPayload(DuelSpell, gb));
        await b.ReadUntilAsync(WorldOpcode.SmsgDuelRequested);
        await b.SendAsync(WorldOpcode.CmsgDuelAccepted, [9]); // short: dropped
        await b.SendAsync(WorldOpcode.CmsgDuelCancelled, [9]);

        // the session keeps working and the duel is untouched
        Assert.Equal(0L, await host.PlayerStateAsync("Duelb", p => p.Duel!.StartTimerSeconds));
        Assert.False(await host.PlayerStateAsync("Duelb", p => p.Duel!.Finished));
    }

    [Fact]
    public async Task ADisconnect_InTheCountdown_CompletesInterrupted_AndLeavesNoFlagBehind()
    {
        (WorldTestHost host, _, WorldTestClient a, WorldTestClient b, ulong _, ulong gb) = await StartAsync();
        await using WorldTestHost h = host;
        await using WorldTestClient ca = a;
        await a.SendAsync(WorldOpcode.CmsgCastSpell, CastPayload(DuelSpell, gb));
        await b.ReadUntilAsync(WorldOpcode.SmsgDuelRequested);
        ulong flag = await host.PlayerStateAsync("Duela", p => p.DuelArbiter);
        await b.SendAsync(WorldOpcode.CmsgDuelAccepted, Guid(flag));
        await b.ReadUntilAsync(WorldOpcode.SmsgDuelCountdown);

        await b.DisposeAsync(); // the connection drops

        Assert.Equal([0], await a.ReadUntilAsync(WorldOpcode.SmsgDuelComplete));
        Assert.Equal(Guid(flag), (await a.ReadUntilAsync(WorldOpcode.SmsgDestroyObject))[..8]);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Duelb") is null, "B logged out");
        Assert.Equal(0ul, await host.PlayerStateAsync("Duela", p => p.DuelArbiter));
    }
}
