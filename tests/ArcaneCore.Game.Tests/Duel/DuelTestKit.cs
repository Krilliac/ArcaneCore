using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Tests.Duel;

internal static class DuelTestKit
{
    /// <summary>Two same-team players side by side on map 0 (the test world has one map and scripted combat).</summary>
    public static (WorldRuntime World, Map Map, TestCombatHooks Hooks, Player A, Player B, FakeSession SessionA, FakeSession SessionB) TwoPlayers(Race raceA = Race.Human, Race raceB = Race.Human)
    {
        (WorldRuntime world, Map map, _, TestCombatHooks hooks) = CombatTestKit.CreateWorld();
        var sa = new FakeSession(1);
        var sb = new FakeSession(2);
        Player a = CombatTestKit.AddPlayer(world, 1, 10, 10, sa, raceA);
        Player b = CombatTestKit.AddPlayer(world, 2, 12, 10, sb, raceB);
        return (world, map, hooks, a, b, sa, sb);
    }

    /// <summary>Install the two crossed duel halves exactly as vmangos EffectDuel does (a challenges b); <paramref name="startTime"/> 0 = still pending.</summary>
    public static (DuelInfo A, DuelInfo B) Link(Player a, Player b, long startTime = 0)
    {
        var da = new DuelInfo(a, b) { StartTimeSeconds = startTime };
        var db = new DuelInfo(a, a) { StartTimeSeconds = startTime };
        a.Duel = da;
        b.Duel = db;
        return (da, db);
    }
}

/// <summary>A pet-like unit owned by a player through the duel seam interface.</summary>
internal sealed class OwnedUnit : Unit, IPlayerControlledUnit
{
    private static uint s_counter = 5000;

    public OwnedUnit()
        : base(ObjectGuid.WithEntry(HighGuid.Unit, 1, Interlocked.Increment(ref s_counter)), Game.TypeId.Unit,
            TypeMask.Object | TypeMask.Unit, UpdateFields.UnitEnd)
    {
        Level = 60;
        MaxHealth = 500;
        Health = 500;
        SetFloat(UpdateFields.UnitFieldCombatreach, 1.5f);
        SetFloat(UpdateFields.UnitFieldBoundingradius, 0.5f);
        SetUInt32(UpdateFields.UnitFieldBaseattacktime, 2000);
    }

    public Player? Owner { get; init; }

    public Player? ControllingPlayer => Owner;

    public void Spawn(Map map, float x, float y)
    {
        Relocate(x, y, 83.5f, 0f, 0);
        MapId = map.MapId;
        Map = map;
        map.Combat.Track(this);
    }
}
