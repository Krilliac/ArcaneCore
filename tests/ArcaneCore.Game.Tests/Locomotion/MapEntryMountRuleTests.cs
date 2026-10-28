using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Locomotion;

/// <summary>
/// The mount allow check on entering a map (vmangos HandleMoveWorldportAckOpcode, MovementHandler.cpp:189-191): a mounted player
/// arriving in a dungeon or raid loses the Mounted aura unless the map is Zul'Gurub-style allowed (209, 269, 309, 509).
/// </summary>
public sealed class MapEntryMountRuleTests
{
    private const uint BrownHorse = 960101;
    private const uint Deadmines = 36;

    private sealed class Displays : IMountDisplaySource
    {
        public uint? FindMountDisplay(uint creatureEntry) => creatureEntry == 2402 ? 14337u : null;
    }

    private static readonly MapContent Content = new(
        [
            new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
            new MapTemplate(Deadmines, 0, MapType.Instance, 0, 5, 0, 0, -11208f, 1672f, "Deadmines", ""),
            new MapTemplate(209, 0, MapType.Instance, 0, 5, 0, 0, 0f, 0f, "Zul'Farrak", ""),
            new MapTemplate(269, 0, MapType.Instance, 0, 5, 0, 0, 0f, 0f, "Opening of the Dark Portal", ""),
            new MapTemplate(309, 0, MapType.Raid, 0, 20, 3, 0, 0f, 0f, "Zul'Gurub", ""),
            new MapTemplate(509, 0, MapType.Raid, 0, 20, 3, 0, 0f, 0f, "Ruins of Ahn'Qiraj", ""),
        ],
        [], [], [], []);

    private static SpellTestKit Kit()
    {
        var kit = new SpellTestKit(Spell(BrownHorse, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Mounted, misc: 2402)) with
        {
            Duration = new SpellDuration(-1, 0, -1),
            SpellVisual = 1,
        });
        LocomotionEnvironment.RegisterMountDisplays(kit.World, new Displays());
        WorldMaps.Of(kit.World).Load(Content);
        return kit;
    }

    private static TeleportService Teleports(SpellTestKit kit)
    {
        var teleports = new TeleportService(kit.World, _ => { }, player => MapEntryMountRule.Enforce(kit.System, player));
        kit.World.PlayerLoggingOut += teleports.Forget;
        return teleports;
    }

    private static Player MountedPlayer(SpellTestKit kit)
    {
        (Player player, _) = kit.AddPlayer(1);
        player.Level = 60;
        kit.System.CastSpell(player, BrownHorse, SpellCastTargets.ForSelf(), triggered: true);
        Assert.True(kit.System.HasAura(player, BrownHorse));
        Assert.Equal(14337u, player.GetUInt32(UpdateFields.UnitFieldMountdisplayid));
        return player;
    }

    private static void FarTeleport(SpellTestKit kit, TeleportService teleports, Player player, uint mapId)
    {
        Assert.True(teleports.TeleportTo(player, mapId, 10f, 10f, 10f, 0));
        kit.World.RunTick(50);
        Assert.Equal(TeleportStage.Far, teleports.StageOf(player));
        Assert.True(teleports.HandleWorldportAck(player));
        kit.World.RunTick(50);
        Assert.Equal(mapId, player.Map!.MapId);
    }

    [Fact]
    public void ATeleportIntoADungeonRemovesTheMountedAuraAndDisplay()
    {
        using SpellTestKit kit = Kit();
        TeleportService teleports = Teleports(kit);
        Player player = MountedPlayer(kit);

        FarTeleport(kit, teleports, player, Deadmines);

        Assert.False(kit.System.HasAura(player, BrownHorse));
        Assert.Equal(0u, player.GetUInt32(UpdateFields.UnitFieldMountdisplayid));
    }

    [Theory]
    [InlineData(209u)]
    [InlineData(269u)]
    [InlineData(309u)]
    [InlineData(509u)]
    public void ATeleportIntoAMountAllowedInstanceKeepsTheMount(uint mapId)
    {
        using SpellTestKit kit = Kit();
        TeleportService teleports = Teleports(kit);
        Player player = MountedPlayer(kit);

        FarTeleport(kit, teleports, player, mapId);

        Assert.True(kit.System.HasAura(player, BrownHorse));
        Assert.Equal(14337u, player.GetUInt32(UpdateFields.UnitFieldMountdisplayid));
    }

    [Fact]
    public void EnforceOnADungeonMapRemovesTheMountedAura()
    {
        using SpellTestKit kit = Kit();
        Player player = MountedPlayer(kit);
        kit.World.RemovePlayer(player);
        player.MapId = Deadmines;
        kit.World.AddPlayer(player);
        Assert.Equal(Deadmines, player.Map!.MapId);

        Assert.True(MapEntryMountRule.Enforce(kit.System, player));

        Assert.False(kit.System.HasAura(player, BrownHorse));
        Assert.Equal(0u, player.GetUInt32(UpdateFields.UnitFieldMountdisplayid));
        Assert.False(MapEntryMountRule.Enforce(kit.System, player));
    }

    [Fact]
    public void ANearTeleportInsideADungeonKeepsATriggeredMount()
    {
        using SpellTestKit kit = Kit();
        TeleportService teleports = Teleports(kit);
        (Player player, _) = kit.AddPlayer(1);
        player.Level = 60;
        FarTeleport(kit, teleports, player, Deadmines);
        kit.System.CastSpell(player, BrownHorse, SpellCastTargets.ForSelf(), triggered: true);
        Assert.True(kit.System.HasAura(player, BrownHorse));

        Assert.True(teleports.TeleportTo(player, Deadmines, 20f, 20f, 20f, 0));
        kit.World.RunTick(50);
        Assert.Equal(TeleportStage.Near, teleports.StageOf(player));
        Assert.True(teleports.HandleTeleportAck(player, player.Guid.Value));
        kit.World.RunTick(50);

        Assert.True(kit.System.HasAura(player, BrownHorse));
        Assert.Equal(14337u, player.GetUInt32(UpdateFields.UnitFieldMountdisplayid));
    }

    [Fact]
    public void ADisplayOnlyMountWithoutAMountedAuraIsLeftAlone()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        kit.World.RemovePlayer(player);
        player.MapId = Deadmines;
        kit.World.AddPlayer(player);
        Assert.True(MountService.Mount(kit.System, player, 14337u));

        Assert.False(MapEntryMountRule.Enforce(kit.System, player));

        Assert.Equal(14337u, player.GetUInt32(UpdateFields.UnitFieldMountdisplayid));
    }

    [Fact]
    public void EnforceOnACommonMapLeavesTheMountAlone()
    {
        using SpellTestKit kit = Kit();
        Player player = MountedPlayer(kit);

        Assert.False(MapEntryMountRule.Enforce(kit.System, player));

        Assert.True(kit.System.HasAura(player, BrownHorse));
    }
}
