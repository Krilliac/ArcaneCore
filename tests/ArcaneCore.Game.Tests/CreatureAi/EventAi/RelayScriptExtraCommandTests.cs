using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi.EventAi;

/// <summary>
/// The relay commands z2815's <c>dbscripts_on_relay</c> uses that used to be skipped as unsupported: MOVE_DYNAMIC (37), SET_HOVER (39),
/// SET_EQUIPMENT_SLOTS (42) and SET_GOSSIP_MENU (52) (cmangos ScriptAction::ExecuteDbscriptCommand; field layout ScriptMgr.h). A wave at
/// Elly starts the relay with the player as the source and Elly as the target (REVERSE swaps them); rows and ids are synthetic.
/// </summary>
public sealed class RelayScriptExtraCommandTests
{
    private const uint TextEmoteWave = 101;
    private const uint Relay = 929100;
    private const uint FlagReverse = 0x002;
    private const uint FlagAdditional = 0x008;
    private const uint Sword = 2196;

    private static readonly ItemTemplate SwordTemplate = new()
    {
        Entry = Sword, Class = 2, SubClass = 7, DisplayId = 7420, Material = 1, InventoryType = 13, Sheath = 3,
    };

    // --- 21 SET_ACTIVEOBJECT -----------------------------------------------------------------

    /// <summary>
    /// vmangos Map::ScriptCommand_SetActiveObject (Maps/ScriptCommands.cpp:831-845): the creature becomes an active object, so it
    /// keeps being updated (and keeps its cells active) after it walks out of every player's active cells; datalong 0 ends it.
    /// </summary>
    [Fact]
    public void SetActiveObject_KeepsAScriptedCreatureUpdatedOutOfThePlayersCells_AndZeroEndsIt()
    {
        foreach (bool scripted in new[] { true, false })
        {
            using Town t = Start(scripted ? [Step(0, 21, dataLong: 1, flags: FlagReverse)] : []);
            t.Wave();
            Assert.Equal(scripted, t.Map.Grids.IsActive(t.Elly));
            t.System.MoveTo(t.Elly, t.Elly.X + 220, t.Elly.Y, t.Elly.Z, run: true, finalOrientation: null);
            float goal = t.Elly.X + 220;
            for (int i = 0; i < 400; i++) Run(t.World, 100); // 40 s: the 220-yard run (8 yd/s) is long over
            if (scripted)
                Assert.True(Math.Abs(goal - t.Elly.X) < 0.5f, $"start {goal - 220} goal {goal} at {t.Elly.X} spline {t.Elly.Spline is not null} active {t.Map.Grids.IsActive(t.Elly)} incell {t.Map.Grids.IsInActiveCell(t.Elly)}"); // updated all the way
            else
                Assert.True(t.Elly.X < goal - 50, $"an ordinary creature froze at the edge of the active cells, not at {t.Elly.X}");
        }

        using Town off = Start([Step(0, 21, dataLong: 1, flags: FlagReverse), Step(100, 21, dataLong: 0, flags: FlagReverse)]);
        off.Wave();
        Assert.True(off.Map.Grids.IsActive(off.Elly));
        Run(off.World, 200);
        Assert.False(off.Map.Grids.IsActive(off.Elly));
    }

    [Fact]
    public void SetActiveObject_TheMarkOutlivesADeathAndComesBackWithTheRespawn()
    {
        // vmangos keeps m_isActiveObject on the creature: SetActiveObjectState is not undone by a death.
        using Town t = Start([Step(0, 21, dataLong: 1, flags: FlagReverse)]);
        t.Wave();
        Assert.True(t.Map.Grids.IsActive(t.Elly));
        t.System.KillCreature(t.Elly);
        for (int i = 0; i < 4000 && !(t.Elly.IsAlive && t.Elly.IsInWorld); i++) Run(t.World, 100); // corpse decay, then the respawn
        Assert.True(t.Elly.IsAlive && t.Elly.IsInWorld, "Elly did not respawn");
        Run(t.World, 100); // the queued activation applies before the next creature update
        Assert.True(t.Map.Grids.IsActive(t.Elly));
    }

    // --- 52 SET_GOSSIP_MENU ---------------------------------------------------------------

    [Fact]
    public void SetGossipMenu_ChangesTheTargetCreaturesDefaultMenu()
    {
        using Town t = Start([Step(0, 52, dataLong: 6688), Step(100, 52, dataLong: 6687)]);
        uint original = t.Elly.DefaultGossipMenuId;
        t.Wave();
        Assert.Equal(6688u, t.Elly.DefaultGossipMenuId);
        Assert.NotEqual(original, t.Elly.DefaultGossipMenuId);
        Run(t.World, 100);
        Assert.Equal(6687u, t.Elly.DefaultGossipMenuId);
    }

    [Fact]
    public void SetGossipMenu_OnAPlayerTarget_ChangesNothing()
    {
        using Town t = Start([Step(0, 52, dataLong: 6688, flags: FlagReverse)]); // target is the player
        uint original = t.Elly.DefaultGossipMenuId;
        t.Wave();
        Assert.Equal(original, t.Elly.DefaultGossipMenuId);
        Assert.Null(t.Elly.ScriptGossipMenuId);
    }

    // --- 42 SET_EQUIPMENT_SLOTS -----------------------------------------------------------

    [Fact]
    public void SetEquipment_EquipsEmptiesAndKeeps_ThenResetsToTheCreaturesOwn()
    {
        using Town t = Start(
            [Step(0, 42, flags: FlagReverse, dataInt: (int)Sword, dataInt2: 0, dataInt3: -1), Step(100, 42, dataLong: 1, flags: FlagReverse)],
            items: e => e == Sword ? SwordTemplate : null);
        t.Elly.SetUInt32(UpdateFields.UnitVirtualItemSlotDisplay + 1, 111);
        t.Elly.SetUInt32(UpdateFields.UnitVirtualItemSlotDisplay + 2, 222);

        t.Wave();

        Assert.Equal(7420u, t.Elly.GetUInt32(UpdateFields.UnitVirtualItemSlotDisplay));
        Assert.Equal((byte)2, t.Elly.GetByte(UpdateFields.UnitVirtualItemInfo, 0));
        Assert.Equal((byte)7, t.Elly.GetByte(UpdateFields.UnitVirtualItemInfo, 1));
        Assert.Equal((byte)1, t.Elly.GetByte(UpdateFields.UnitVirtualItemInfo, 2));
        Assert.Equal((byte)13, t.Elly.GetByte(UpdateFields.UnitVirtualItemInfo, 3));
        Assert.Equal((byte)3, t.Elly.GetByte(UpdateFields.UnitVirtualItemInfo + 1, 0));
        Assert.Equal(0u, t.Elly.GetUInt32(UpdateFields.UnitVirtualItemSlotDisplay + 1)); // 0 empties the off hand
        Assert.Equal(222u, t.Elly.GetUInt32(UpdateFields.UnitVirtualItemSlotDisplay + 2)); // -1 keeps the ranged slot

        Run(t.World, 100);
        Assert.Equal(0u, t.Elly.GetUInt32(UpdateFields.UnitVirtualItemSlotDisplay));
        Assert.Equal(111u, t.Elly.GetUInt32(UpdateFields.UnitVirtualItemSlotDisplay + 1));
        Assert.Equal(222u, t.Elly.GetUInt32(UpdateFields.UnitVirtualItemSlotDisplay + 2));
    }

    [Fact]
    public void SetEquipment_WithAnItemTheWorldDoesNotKnow_LeavesTheSlot()
    {
        using Town t = Start([Step(0, 42, flags: FlagReverse, dataInt: 99999, dataInt2: -1, dataInt3: -1)], items: _ => null);
        t.Elly.SetUInt32(UpdateFields.UnitVirtualItemSlotDisplay, 333);
        t.Wave();
        Assert.Equal(333u, t.Elly.GetUInt32(UpdateFields.UnitVirtualItemSlotDisplay));
        Assert.Null(t.Elly.ScriptEquipmentDefault);
    }

    [Fact]
    public void SetEquipment_ResetWithoutAnyChange_DoesNothing()
    {
        using Town t = Start([Step(0, 42, dataLong: 1, flags: FlagReverse)]);
        t.Elly.SetUInt32(UpdateFields.UnitVirtualItemSlotDisplay, 444);
        t.Wave();
        Assert.Equal(444u, t.Elly.GetUInt32(UpdateFields.UnitVirtualItemSlotDisplay));
    }

    [Fact]
    public void SetEquipment_ByAPlayerSource_TouchesNoCreature()
    {
        using Town t = Start([Step(0, 42, dataInt: (int)Sword)], items: e => e == Sword ? SwordTemplate : null); // source is the player
        t.Wave();
        Assert.Equal(0u, t.Elly.GetUInt32(UpdateFields.UnitVirtualItemSlotDisplay));
    }

    // --- 39 SET_HOVER ---------------------------------------------------------------------

    [Fact]
    public void SetHover_SetsAndClearsTheHoverFlag_AndTellsObservers()
    {
        using Town t = Start([Step(0, 39, dataLong: 1, flags: FlagReverse), Step(100, 39, dataLong: 0, flags: FlagReverse)]);
        t.Wave();
        Assert.True(t.Elly.Movement.HasFlag(MovementFlags.Hover));
        Assert.NotEmpty(Packets(t.Session, WorldOpcode.SmsgSplineMoveSetHover));
        Run(t.World, 100);
        Assert.False(t.Elly.Movement.HasFlag(MovementFlags.Hover));
        Assert.NotEmpty(Packets(t.Session, WorldOpcode.SmsgSplineMoveUnsetHover));
    }

    [Fact]
    public void SetHover_ByAPlayerSource_IsSkipped()
    {
        using Town t = Start([Step(0, 39, dataLong: 1)]);
        t.Wave();
        Assert.False(t.Elly.Movement.HasFlag(MovementFlags.Hover));
        Assert.False(t.Player.Movement.HasFlag(MovementFlags.Hover));
    }

    // --- 37 MOVE_DYNAMIC ------------------------------------------------------------------

    [Fact]
    public void MoveDynamic_WithoutAMaxDistance_GoesToTheTargetsContactPoint()
    {
        // Elly at (0, 0), the player at (0, 10): the contact point is on the line back to Elly, fixedDist plus both radii short of him.
        using Town t = Start([Step(0, 37, dataLong3: 1, flags: FlagReverse, dataInt: 2)]);
        t.Wave();
        float distance = 1f + t.Player.BoundingRadius + t.Elly.BoundingRadius;
        Assert.NotNull(t.Elly.Spline);
        Assert.Equal(0f, t.Elly.Spline!.EndX, 0.01f);
        Assert.Equal(10f - distance, t.Elly.Spline.EndY, 0.01f);
    }

    [Fact]
    public void MoveDynamic_WithoutRadii_UsesTheFixedDistanceAlone()
    {
        using Town t = Start([Step(0, 37, dataLong3: 3, flags: FlagReverse | FlagAdditional)]);
        t.Wave();
        Assert.Equal(7f, t.Elly.Spline!.EndY, 0.01f);
    }

    [Fact]
    public void MoveDynamic_WithAMaxDistance_PicksAPointBetweenMinAndMaxAroundTheTarget()
    {
        using Town t = Start([Step(0, 37, dataLong: 20, dataLong2: 2, flags: FlagReverse)]);
        t.Wave();
        Assert.NotNull(t.Elly.Spline);
        float dx = t.Elly.Spline!.EndX - t.Player.X;
        float dy = t.Elly.Spline.EndY - t.Player.Y;
        float distance = MathF.Sqrt((dx * dx) + (dy * dy));
        Assert.InRange(distance, 2f - 0.01f, 20f + 0.01f);
    }

    [Fact]
    public void MoveDynamic_ByAPlayerSource_MovesNobody()
    {
        using Town t = Start([Step(0, 37, dataLong3: 1)]);
        t.Wave();
        Assert.Null(t.Elly.Spline);
    }

    // --- 18 DESPAWN_SELF (database spawn) -------------------------------------------------

    /// <summary>
    /// cmangos SCRIPT_COMMAND_DESPAWN_SELF (ScriptMgr.cpp:2257-2272) on a database spawn is Creature::ForcedDespawn:
    /// the creature leaves at once and comes back after its respawn delay. Bot QA #285 and #393 reported the step skipped.
    /// </summary>
    [Fact]
    public void DespawnSelf_DatabaseSpawn_LeavesNowAndRespawnsAfterItsDelay()
    {
        using Town t = Start([Step(0, 18)]);
        Assert.NotNull(t.Elly.Spawn);
        t.Elly.RespawnDelayOnceSeconds = 1;
        t.Wave();

        Assert.False(t.Elly.IsInWorld);
        Assert.Equal(CreatureDeathState.Dead, t.Elly.DeathState);
        long? wait = t.System.RespawnRemainingMs(t.Elly);
        Assert.NotNull(wait);
        Assert.InRange(wait!.Value, 1, 1000);

        for (int i = 0; i < 30 && !(t.Elly.IsAlive && t.Elly.IsInWorld); i++) Run(t.World, 100);
        Assert.True(t.Elly.IsAlive && t.Elly.IsInWorld);
        Assert.Null(t.Elly.RespawnDelayOnceSeconds);
    }

    [Fact]
    public void DespawnSelf_DatabaseSpawn_WaitsOutDatalongBeforeLeaving()
    {
        using Town t = Start([Step(0, 18, dataLong: 1000)]);
        t.Wave();
        Assert.True(t.Elly.IsAlive && t.Elly.IsInWorld);

        Run(t.World, 500);
        Assert.True(t.Elly.IsInWorld, "datalong is a delay in milliseconds, not an instant despawn");

        Run(t.World, 600);
        Assert.False(t.Elly.IsInWorld);
        Assert.NotNull(t.System.RespawnRemainingMs(t.Elly));
    }

    private static RelayScriptStep Step(uint delay, uint command, uint dataLong = 0, uint dataLong2 = 0, uint dataLong3 = 0, uint flags = 0,
        int dataInt = 0, int dataInt2 = 0, int dataInt3 = 0)
        => new(Relay, delay, 0, command, dataLong, dataLong2, dataLong3, 0, 0, flags, dataInt, dataInt2, dataInt3, 0, 0, 0, 0, 0, 0, 0, 0);

    private static CreatureAiEvent WaveRow()
        => new()
        {
            Id = 1,
            CreatureId = WolfEntry,
            EventType = 22,
            Flags = 1,
            Param1 = (int)TextEmoteWave,
            Action1 = new CreatureAiAction((byte)EventAiActionType.StartRelayScript, (int)Relay, 7, 0),
        };

    private sealed record Town(WorldRuntime World, Map Map, CreatureMapSystem System, Creature Elly, Player Player, FakeSession Session) : IDisposable
    {
        public void Dispose() => World.Dispose();

        public void Wave() => Elly.ReceiveEmote(Player, TextEmoteWave);
    }

    private static Town Start(IEnumerable<RelayScriptStep> steps, Func<uint, ItemTemplate?>? items = null)
    {
        var ai = new CreatureAiContent([WaveRow()], [], new BroadcastTextCatalog([]))
        {
            RelayScripts = new RelayScriptCatalog(steps, []),
        };
        CreatureContent content = new(
            [Template() with { AIName = CreatureAiFactory.EventAIName, Civilian = true }],
            [Spawn(1, WolfEntry, 0, 0)], [], [], [], ai);
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content,
            new CreatureAiServices { Spells = new FakeCaster(), ItemTemplateOf = items });
        (Player player, FakeSession session) = AddPlayer(world, 1, 0, 10);
        Creature elly = Assert.Single(system.Creatures);
        return new Town(world, map, system, elly, player, session);
    }
}
