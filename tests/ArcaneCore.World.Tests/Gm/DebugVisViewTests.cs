using System.Numerics;
using ArcaneCore.Game.DebugDraw;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm;

/// <summary>The <c>.debug vis botstall|leases|invasion</c> views (rendered from fixtures) and <c>.nameaura</c>.</summary>
public sealed class DebugVisViewTests
{
    private static readonly Vector3 Here = new(10f, 20f, 30f);

    [Fact]
    public void BotStall_MarksStalledBotsOnly_AndListsTheirCodes()
    {
        DebugVisView view = DebugVisViews.BotStall(
        [
            new BotStallEntry("Idler", "Running", "stall: no progress", new Vector3(1f, 2f, 3f)),
            new BotStallEntry("Walker", "Running", null, new Vector3(4f, 5f, 6f)),
            new BotStallEntry("Away", "Running", "death loop: 3 deaths", null),
        ]);

        Assert.Equal("botstall", view.Title);
        Assert.Equal("bot stall: 2 of 3 bot(s) stalled.", view.Lines[0]);
        Assert.Contains(view.Lines, line => line.StartsWith("Idler [Running]: stall: no progress at (1.0, 2.0, 3.0)", StringComparison.Ordinal));
        Assert.Contains(view.Lines, line => line.EndsWith("(not in a world)", StringComparison.Ordinal));
        DebugVisMarker marker = Assert.Single(view.Markers);
        Assert.Equal(DebugMarkerKind.PathCornerBad, marker.Kind);
        Assert.Equal(new Vector3(1f, 2f, 3f), marker.Position);
    }

    [Fact]
    public void BotStall_NoStall_SaysSo_AndDrawsNothing()
    {
        DebugVisView view = DebugVisViews.BotStall([new BotStallEntry("Walker", "Running", null, Here)]);

        Assert.Empty(view.Markers);
        Assert.Contains("no bot reports a stall or an error.", view.Lines);
    }

    [Fact]
    public void Leases_ShowHolderAndUnheldScopes_OneMarkerEach()
    {
        var now = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        var incarnation = Guid.Parse("11111111-2222-3333-4444-555555555555");
        DebugVisView view = DebugVisViews.Leases(
        [
            new LeaseEntry("economy", "node-a", incarnation, 7, 7, now.AddSeconds(90)),
            new LeaseEntry("social", null, null, 0, 4, null),
            new LeaseEntry("instances", "node-b", incarnation, 3, 3, now.AddSeconds(-5)),
        ], now, Here);

        Assert.Equal(4, view.Lines.Count);
        Assert.Contains("economy: held by node-a (incarnation 11111111222233334444555555555555), fencing token 7, expires in 1m 30s", view.Lines);
        Assert.Contains("social: unheld, highest token 4", view.Lines);
        Assert.Contains("instances: unheld, highest token 3", view.Lines);
        Assert.Equal(3, view.Markers.Count);
        Assert.Equal(DebugMarkerKind.PathCorner, view.Markers[0].Kind);
        Assert.Equal(DebugMarkerKind.PathCornerBad, view.Markers[1].Kind);
        Assert.Equal(Here.Z + 2f, view.Markers[0].Position.Z);
        Assert.Equal(Here.Z + 6f, view.Markers[2].Position.Z);
    }

    [Fact]
    public void Invasion_ListsEveryZone_AndMarksTheMouthsOnTheGivenMap()
    {
        var snapshot = new InvasionSnapshot(true, 2, 440,
        [
            new InvasionZoneEntry(440, 1, 2, 3, 1_000_100, new Vector3(1f, 1f, 1f)),
            new InvasionZoneEntry(4, 0, 0, 2, 0, new Vector3(2f, 2f, 2f)),
            new InvasionZoneEntry(16, 1, 0, 2, 900, new Vector3(3f, 3f, 3f)),
        ]);

        DebugVisView view = DebugVisViews.Invasion(snapshot, 1_000_000, mapId: 1);

        Assert.Equal("scourge invasion: enabled, 2 battle(s) won, last attack Tanaris.", view.Lines[0]);
        Assert.Contains("Tanaris (440): 2/3 necropolis(es) standing, next attack in 1m 40s", view.Lines);
        Assert.Contains("Blasted Lands (4): 0/2 necropolis(es) standing", view.Lines);
        Assert.Contains("Azshara (16): 0/2 necropolis(es) standing, next attack due", view.Lines);
        // Only the attacked zone on map 1 gets a Mouth marker: Azshara has no necropolis left, Blasted Lands is on another map.
        DebugVisMarker marker = Assert.Single(view.Markers);
        Assert.Equal(DebugMarkerKind.PathCornerBad, marker.Kind);
        Assert.Equal(new Vector3(1f, 1f, 1f), marker.Position);

        var disabled = new InvasionSnapshot(false, 0, 0, snapshot.Zones);
        Assert.Empty(DebugVisViews.Invasion(disabled, 1_000_000, mapId: 1).Markers);
    }

    [Fact]
    public void Views_CapTheirMarkers()
    {
        BotStallEntry[] many = [.. Enumerable.Range(0, DebugVisViews.MaxMarkers + 20)
            .Select(i => new BotStallEntry($"Bot{i}", "Running", "stall", Here))];

        Assert.Equal(DebugVisViews.MaxMarkers, DebugVisViews.BotStall(many).Markers.Count);
    }

    [Fact]
    public void NameAuraCommand_IsRegisteredLikeRetail()
    {
        CommandTable table = ChatCommands.CreateTable();

        ChatCommand command = Assert.Single(table.Roots, c => c.Name == "nameaura");
        Assert.Equal(4, command.RequiredLevel(table.Gm));
        foreach (string leaf in new[] { "botstall", "leases", "invasion" })
        {
            Assert.NotNull(table.Resolve("debug vis " + leaf, AccountSecurity.GameMaster));
        }
    }

    [Fact]
    public async Task NameAura_AppliesTheAuraToTheNamedPlayer_ForTheGivenSeconds()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("NAMEAURAADM", "Nameauraadm", AccountSecurity.Administrator);
        await using WorldTestClient victim = await host.EnterWorldAsync("NAMEAURAVIC", "Nameauravic");
        Player target = await host.PlayerAsync("Nameauravic");
        await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<SpellFeature>().System.Store = new SpellStore([AuraSpell()], [], []));

        await admin.SendChatAsync(ChatType.Say, Language.Common, ".nameaura 991941 Nameauravic 30");

        SpellSystem system = host.WorldServices.GetRequiredService<SpellFeature>().System;
        await host.WaitForWorldAsync(() => system.HasAura(target, 991941), "named aura application");
        int duration = await host.OnWorldAsync(() => system.GetAuras(target).Single(h => h.Spell.Id == 991941).Duration);
        Assert.InRange(duration, 1, 30_000);

        // The client must be told the overridden timer (SMSG_UPDATE_AURA_DURATION: slot byte, duration uint32), not the spell's own 60 s.
        List<(WorldOpcode Opcode, byte[] Payload)> packets = await victim.CollectAsync();
        Assert.Contains(packets, p => p.Opcode == WorldOpcode.SmsgUpdateAuraDuration
            && p.Payload.Length >= 5
            && BitConverter.ToUInt32(p.Payload, 1) is > 20_000u and <= 30_000u);
    }

    private static SpellInfo AuraSpell() => new()
    {
        Id = 991941,
        Name = "Nameaura Aura",
        Duration = new SpellDuration(60_000, 0, 60_000),
        Effects = [new SpellEffectInfo
        {
            Effect = SpellEffectName.ApplyAura,
            AuraType = AuraType.ModStat,
            BasePoints = 3,
            BaseDice = 1,
            DieSides = 1,
            TargetA = SpellImplicitTarget.UnitCaster,
        }, new(), new()],
    };
}
