using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>Builders and fakes for the creature AI tests.</summary>
internal static class CreatureAiTestSupport
{
    public const string RecorderName = "RecorderAI";

    public static (WorldRuntime World, Map Map, CreatureMapSystem System) CreateAiSystem(
        CreatureContent content, CreatureAiServices? services = null, CreatureOptions? options = null, WorldRuntime? world = null, uint instanceId = 0)
    {
        world ??= TestWorld.CreateRuntime();
        Map map = world.GetMap(0, instanceId);
        var system = new CreatureMapSystem(map, content, options, random: new Random(1), aiServices: services ?? new CreatureAiServices());
        map.AddUpdater(system);
        return (world, map, system);
    }

    /// <summary>A factory with the recorder AI registered (it aggroes on sight like AggressorAI).</summary>
    public static CreatureAiFactory RecorderFactory()
    {
        var factory = new CreatureAiFactory();
        factory.Register(RecorderName, c => new RecorderAI(c));
        return factory;
    }

    public static (Player Player, FakeSession Session) AddPlayer(WorldRuntime world, uint guid, float x, float y)
    {
        var session = new FakeSession((int)guid);
        Player player = TestWorld.CreatePlayer(guid, x, y, session);
        world.AddPlayer(player);
        world.RunTick(0);
        session.Clear();
        return (player, session);
    }

    /// <summary>Run <paramref name="ms"/> of world time in <paramref name="step"/> ms ticks.</summary>
    public static void Run(WorldRuntime world, uint ms, uint step = 100)
    {
        for (uint done = 0; done < ms; done += step)
        {
            world.RunTick(Math.Min(step, ms - done));
        }
    }

    public static float Distance2D(WorldObject a, float x, float y) => MathF.Sqrt(((a.X - x) * (a.X - x)) + ((a.Y - y) * (a.Y - y)));

    public static float Distance2D(WorldObject a, WorldObject b) => Distance2D(a, b.X, b.Y);

    public static List<byte[]> Packets(FakeSession session, WorldOpcode opcode)
    {
        var list = new List<byte[]>();
        foreach ((WorldOpcode op, byte[] payload) in session.Sent)
        {
            if (op == opcode)
            {
                list.Add(payload);
            }
        }

        return list;
    }

    /// <summary>A parsed SMSG_MONSTER_MOVE (moving form).</summary>
    public sealed record PathMove(
        ulong Guid, Vector3 Start, uint SplineId, MonsterMoveType Type, float Angle, Vector3 Spot, ulong Target,
        uint Flags, uint Duration, IReadOnlyList<Vector3> Points);

    public static PathMove ParsePathMove(byte[] payload)
    {
        var r = new PacketReader(payload);
        ulong guid = r.ReadPackedGuid();
        var start = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        uint id = r.ReadUInt32();
        var type = (MonsterMoveType)r.ReadByte();
        float angle = 0;
        Vector3 spot = default;
        ulong target = 0;
        switch (type)
        {
            case MonsterMoveType.Stop:
                return new PathMove(guid, start, id, type, 0, default, 0, 0, 0, []);
            case MonsterMoveType.FacingAngle:
                angle = r.ReadSingle();
                break;
            case MonsterMoveType.FacingSpot:
                spot = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                break;
            case MonsterMoveType.FacingTarget:
                target = r.ReadUInt64();
                break;
        }

        uint flags = r.ReadUInt32();
        uint duration = r.ReadUInt32();
        uint count = r.ReadUInt32();
        var destination = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        var points = new List<Vector3>();
        for (uint i = 1; i < count; i++)
        {
            points.Add(destination - CreatureMovePackets.UnpackXYZ(r.ReadUInt32())); // retail layout: destination - point
        }

        points.Add(destination);
        return new PathMove(guid, start, id, type, angle, spot, target, flags, duration, points);
    }

    /// <summary>A parsed monster SMSG_MESSAGECHAT (say/yell/emote/whisper).</summary>
    public sealed record MonsterChat(ChatType Type, uint Language, string Name, ulong Target, string Message);

    public static MonsterChat ParseMonsterChat(byte[] payload)
    {
        var r = new PacketReader(payload);
        var type = (ChatType)r.ReadByte();
        uint language = r.ReadUInt32();
        if (type is ChatType.MonsterSay or ChatType.MonsterYell)
        {
            r.ReadUInt64();
        }

        string name = ReadSized(ref r);
        ulong target = r.ReadUInt64();
        string message = ReadSized(ref r);
        return new MonsterChat(type, language, name, target, message);

        static string ReadSized(ref PacketReader reader)
        {
            uint length = reader.ReadUInt32();
            byte[] bytes = new byte[length];
            for (int i = 0; i < length; i++)
            {
                bytes[i] = reader.ReadByte();
            }

            return System.Text.Encoding.UTF8.GetString(bytes).TrimEnd('\0');
        }
    }
}

/// <summary>Records every hook (aggroes on sight like AggressorAI).</summary>
internal sealed class RecorderAI(Creature creature) : AggressorAI(creature)
{
    public List<string> Calls { get; } = [];

    public Unit? KilledUnit { get; private set; }

    public (MovementGeneratorType Type, uint Id)? LastInform { get; private set; }

    public override void OnAggro(Unit target) => Calls.Add("aggro");

    public override void OnDeath(Unit? killer) => Calls.Add("death");

    public override void OnKilledUnit(Unit victim)
    {
        KilledUnit = victim;
        Calls.Add("killed");
    }

    public override void OnSpellHit(Unit caster, SpellInfo spell) => Calls.Add($"spellhit:{spell.Id}");

    public override void OnEvade() => Calls.Add("evade");

    public override void OnReachedHome() => Calls.Add("home");

    public override void OnRespawn() => Calls.Add("respawn");

    public override void OnMovementInform(MovementGeneratorType type, uint pointId)
    {
        LastInform = (type, pointId);
        Calls.Add($"inform:{type}:{pointId}");
    }
}

/// <summary>Every creature is hostile to every unit; assistance within one faction template.</summary>
internal sealed class AlwaysHostile : ICreatureHostility
{
    public bool IsHostile(Creature creature, Unit target) => true;

    public bool CanAssist(Creature helper, Creature caller) => helper.FactionTemplate == caller.FactionTemplate;
}

/// <summary>An <see cref="ILineOfSight"/> where every segment is blocked (installed with WorldCollision).</summary>
internal sealed class BlockedSight : ILineOfSight
{
    public bool Enabled => true;

    public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => false;

    public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
    {
        hit = from;
        return true;
    }

    public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => null;

    public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
    {
        info = default;
        return false;
    }
}

/// <summary>An <see cref="IPathfinder"/> that detours through one corner 5 yd to the side of the straight line.</summary>
internal sealed class DetourPathfinder : IPathfinder
{
    public int Calls { get; private set; }

    public bool Enabled => true;

    public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
    {
        Calls++;
        Vector3 middle = ((start + end) / 2f) + new Vector3(0, 5, 0);
        return new PathResult(PathType.Normal, [start, middle, end]);
    }
}

/// <summary>An <see cref="IPathfinder"/> that finds nothing (vmangos PATHFIND_NOPATH).</summary>
internal sealed class NoPathPathfinder : IPathfinder
{
    public bool Enabled => true;

    public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null) => PathResult.None(start);
}

/// <summary>Records casts; raises spell hits on demand.</summary>
internal sealed class FakeCaster : ICreatureSpellCaster
{
    public List<(uint Spell, Unit? Target, bool Triggered)> Casts { get; } = [];

    public HashSet<(Unit, uint)> Auras { get; } = [];

    public List<Creature> Removed { get; } = [];

    public int Interrupts { get; private set; }

    public bool Casting { get; set; }

    public event Action<Unit, Unit, SpellInfo>? SpellHit;

    public CreatureCastResult Cast(Creature caster, uint spellId, Unit? target, bool triggered)
    {
        Casts.Add((spellId, target, triggered));
        return CreatureCastResult.Ok;
    }

    /// <summary>Casts a script made another unit perform (<see cref="ICreatureSpellCaster.CastByUnit"/>).</summary>
    public List<(Unit Caster, uint Spell, Unit? Target, bool Triggered)> UnitCasts { get; } = [];

    public CreatureCastResult CastByUnit(Unit caster, uint spellId, Unit? target, bool triggered)
    {
        UnitCasts.Add((caster, spellId, target, triggered));
        return CreatureCastResult.Ok;
    }

    public bool IsCasting(Creature caster) => Casting;

    public bool HasAura(Unit unit, uint spellId) => Auras.Contains((unit, spellId));

    public List<(Unit Unit, uint Spell)> RemovedAuras { get; } = [];

    /// <summary>Auras added without a cast (vmangos Unit::AddAura), with the ADD_AURA_PERMANENT flag.</summary>
    public List<(Unit Unit, uint Spell, bool Permanent)> AddedAuras { get; } = [];

    public CreatureCastResult AddAura(Unit unit, uint spellId, bool permanent)
    {
        AddedAuras.Add((unit, spellId, permanent));
        Auras.Add((unit, spellId));
        return CreatureCastResult.Ok;
    }

    /// <summary>Auras put on with another unit as their caster (<see cref="ICreatureSpellCaster.AddAuraFrom"/>).</summary>
    public List<(Unit Unit, uint Spell, Unit Caster)> AddedAurasFrom { get; } = [];

    public CreatureCastResult AddAuraFrom(Unit unit, uint spellId, Unit caster)
    {
        AddedAurasFrom.Add((unit, spellId, caster));
        Auras.Add((unit, spellId));
        return CreatureCastResult.Ok;
    }

    public void RemoveAuras(Unit unit, uint spellId)
    {
        RemovedAuras.Add((unit, spellId));
        Auras.Remove((unit, spellId));
    }

    public List<Creature> Interrupted { get; } = [];

    public void Interrupt(Creature caster)
    {
        Interrupts++;
        Interrupted.Add(caster);
    }

    public void OnCreatureRemoved(Creature creature) => Removed.Add(creature);

    public void RaiseHit(Unit caster, Unit target, SpellInfo spell) => SpellHit?.Invoke(caster, target, spell);
}
