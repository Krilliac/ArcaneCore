using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Tests;

/// <summary>A scripted random source: queued values first, then fixed fallbacks.</summary>
internal sealed class ScriptedRandom : ICombatRandom
{
    public Queue<int> Ints { get; } = new();

    public Queue<float> Floats { get; } = new();

    /// <summary>Returned when no int is queued (9999 = past every table range → normal hit).</summary>
    public int DefaultInt { get; set; } = 9999;

    /// <summary>Fraction of the range returned when no float is queued.</summary>
    public float DefaultFraction { get; set; }

    public int Next(int minInclusive, int maxInclusive)
        => Math.Clamp(Ints.Count > 0 ? Ints.Dequeue() : DefaultInt, minInclusive, maxInclusive);

    public float NextFloat(float min, float max)
        => Floats.Count > 0 ? Floats.Dequeue() : min + ((max - min) * DefaultFraction);
}

/// <summary>A minimal non-player unit standing in for the creatures area's Creature.</summary>
internal class CombatTestUnit : Unit, ICombatCreature
{
    private static uint s_counter = 100;

    public CombatTestUnit(byte level = 60, uint health = 1000)
        : base(ObjectGuid.WithEntry(HighGuid.Unit, 1, Interlocked.Increment(ref s_counter)), Game.TypeId.Unit,
            TypeMask.Object | TypeMask.Unit, UpdateFields.UnitEnd)
    {
        Level = level;
        MaxHealth = health;
        Health = health;
        SetFloat(UpdateFields.UnitFieldCombatreach, 1.5f);
        SetFloat(UpdateFields.UnitFieldBoundingradius, 0.5f);
        SetUInt32(UpdateFields.UnitFieldBaseattacktime, 2000);
        SetFloat(UpdateFields.UnitFieldMindamage, 10);
        SetFloat(UpdateFields.UnitFieldMaxdamage, 10);
    }

    public bool IsInEvadeMode { get; set; }

    public bool CanParry { get; set; } = true;

    public bool CanBlock { get; set; } = true;

    public bool CanCrush { get; set; } = true;

    public bool IsWorldBoss { get; set; }

    public bool RegeneratesHealth { get; set; } = true;

    public List<Unit> AttackedBy { get; } = [];

    public int Deaths { get; private set; }

    public void OnAttackedBy(Unit attacker) => AttackedBy.Add(attacker);

    public void OnJustDied(Unit? killer) => Deaths++;

    /// <summary>Put the unit in a map at a position, tracked by its combat.</summary>
    public void Spawn(Map map, float x, float y, float z = 83.5f, float orientation = 0f)
    {
        Relocate(x, y, z, orientation, 0);
        MapId = map.MapId;
        Map = map;
        map.Combat.Track(this);
    }
}

/// <summary>Hooks with a dual-wield / kill-recording switch for tests.</summary>
internal sealed class TestCombatHooks : CombatHooks
{
    public bool DualWield { get; set; }

    public List<(Unit? Killer, Unit Victim)> Kills { get; } = [];

    public int GraveyardRepops { get; private set; }

    public override bool HasOffhandWeapon(Unit unit) => DualWield && unit is Player;

    public override void OnKill(Unit? killer, Unit victim) => Kills.Add((killer, victim));

    public override bool RepopAtGraveyard(Player player)
    {
        GraveyardRepops++;
        return false;
    }
}

internal static class CombatTestKit
{
    /// <summary>A world with one map (0) whose combat uses a scripted random and test hooks.</summary>
    public static (WorldRuntime World, Map Map, ScriptedRandom Random, TestCombatHooks Hooks) CreateWorld()
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var random = new ScriptedRandom();
        var hooks = new TestCombatHooks();
        map.Combat.Random = random;
        map.Combat.Hooks = hooks;
        return (world, map, random, hooks);
    }

    /// <summary>A level-60 player (warrior) added to the world at a position, facing east.</summary>
    public static Player AddPlayer(WorldRuntime world, uint guid, float x, float y, FakeSession session, Race race = Race.Human, byte level = 60, uint mapId = 0)
    {
        Player player = TestWorld.CreatePlayer(guid, x, y, session, mapId, race);
        player.Level = level;
        player.MaxHealth = 1000;
        player.Health = 1000;
        world.AddPlayer(player);
        session.Clear();
        return player;
    }

    public static IEnumerable<(WorldOpcode Opcode, byte[] Payload)> Drain(FakeSession session)
    {
        var list = new List<(WorldOpcode, byte[])>();
        while (session.Sent.TryDequeue(out var p))
        {
            list.Add(p);
        }

        return list;
    }

    public static byte[] Packed(ulong guid)
    {
        var w = new PacketWriter(9);
        w.WritePackedGuid(guid);
        return w.ToArray();
    }
}
