using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>Builds synthetic spells and a world with a controllable clock for the spell tests.</summary>
internal sealed class SpellTestKit : IDisposable
{
    public const uint InstantHeal = 100;
    public const uint CastBolt = 101;
    public const uint DotSpell = 102;
    public const uint Passive = 103;
    public const uint CooldownSpell = 104;
    public const uint RageEnergize = 105;
    public const uint Teacher = 106;
    public const uint HomeTeleport = 107;
    public const uint ChannelSpell = 108;
    public const uint StunSpell = 109;
    public const uint HotSpell = 110;
    public const uint DatabaseTeleport = 111;

    public SpellTestKit(params SpellInfo[] extra)
    {
        World = TestWorld.CreateRuntime();
        Store = new SpellStore(
            [.. DefaultSpells(), .. extra],
            [(1, 1, Passive), (1, 1, InstantHeal)],
            [(DatabaseTeleport, new SpellTargetPosition(0, 40, 50, 83.5f, 1.5f))]);
        Spellbook = new MemorySpellbook();
        System = new SpellSystem(Store, () => Now, spellbook: Spellbook, random: new Random(1)) { MapUpdateIntervalMs = 0 };
    }

    public uint Now { get; set; } = 10_000;

    public WorldRuntime World { get; }

    public SpellStore Store { get; }

    public MemorySpellbook Spellbook { get; }

    public SpellSystem System { get; }

    /// <summary>A player in the world (map 0) whose spawn packets are already cleared.</summary>
    public (Player Player, FakeSession Session) AddPlayer(uint guid, float x = 0, float y = 0)
    {
        var session = new FakeSession((int)guid);
        Player player = TestWorld.CreatePlayer(guid, x, y, session);
        World.AddPlayer(player);
        World.RunTick(0);
        session.Clear();
        return (player, session);
    }

    /// <summary>Advance the clock and the spell system.</summary>
    public void Advance(uint ms, uint step = 100)
    {
        for (uint done = 0; done < ms; done += step)
        {
            uint diff = Math.Min(step, ms - done);
            Now += diff;
            System.Update(diff);
        }
    }

    public void Dispose() => World.Dispose();

    public static SpellEffectInfo Effect(SpellEffectName effect, int value, SpellImplicitTarget targetA = SpellImplicitTarget.UnitCaster,
        AuraType aura = AuraType.None, uint amplitude = 0, int misc = 0, uint trigger = 0, SpellImplicitTarget targetB = SpellImplicitTarget.None) => new()
    {
        Effect = effect,
        BasePoints = value - 1,
        BaseDice = 1,
        DieSides = 1,
        TargetA = targetA,
        TargetB = targetB,
        AuraType = aura,
        Amplitude = amplitude,
        MiscValue = misc,
        TriggerSpell = trigger,
    };

    public static SpellInfo Spell(uint id, params SpellEffectInfo[] effects) => new()
    {
        Id = id,
        Name = $"Spell {id}",
        RangeIndex = SpellConstants.RangeIndexSelfOnly,
        Range = new SpellRange(0, 0),
        StartRecoveryCategory = SpellConstants.GlobalCooldownCategory,
        StartRecoveryTime = 1500,
        Effects = effects,
    };

    public static IEnumerable<SpellInfo> DefaultSpells() =>
    [
        Spell(InstantHeal, Effect(SpellEffectName.Heal, 20)),
        Spell(CastBolt, Effect(SpellEffectName.SchoolDamage, 15, SpellImplicitTarget.UnitEnemy)) with
        {
            School = SpellSchool.Fire,
            CastTime = new SpellCastTime(2000, 0, 0),
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            PowerType = (int)PowerType.Rage,
            ManaCost = 50,
            InterruptFlags = SpellInterruptFlags.Movement,
        },
        Spell(DotSpell, Effect(SpellEffectName.ApplyAura, 4, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicDamage, amplitude: 3000)) with
        {
            Duration = new SpellDuration(12000, 0, 12000),
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            SpellVisual = 1,
        },
        Spell(Passive, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
        {
            Attributes = SpellAttributes.Passive,
            Duration = new SpellDuration(-1, 0, -1),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        },
        Spell(CooldownSpell, Effect(SpellEffectName.Dummy, 0)) with { RecoveryTime = 10_000 },
        Spell(RageEnergize, Effect(SpellEffectName.Energize, 100, misc: (int)PowerType.Rage)),
        Spell(Teacher, Effect(SpellEffectName.LearnSpell, 0, trigger: CooldownSpell)),
        Spell(HomeTeleport, Effect(SpellEffectName.TeleportUnits, 0, targetB: SpellImplicitTarget.LocationCasterHomeBind)),
        Spell(DatabaseTeleport, Effect(SpellEffectName.TeleportUnits, 0, targetB: SpellImplicitTarget.LocationDatabase)),
        Spell(ChannelSpell, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
        {
            AttributesEx = SpellAttributesEx.IsChanneled,
            Duration = new SpellDuration(5000, 0, 5000),
            SpellVisual = 1,
        },
        Spell(StunSpell, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.ModStun)) with
        {
            Duration = new SpellDuration(4000, 0, 4000),
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            SpellVisual = 1,
        },
        Spell(HotSpell, Effect(SpellEffectName.ApplyAura, 5, aura: AuraType.PeriodicHeal, amplitude: 1000)) with
        {
            Duration = new SpellDuration(3000, 0, 3000),
            SpellVisual = 1,
        },
    ];

    /// <summary>Packets of one opcode a session received, in order.</summary>
    public static List<byte[]> Packets(FakeSession session, WorldOpcode opcode)
        => [.. session.Sent.Where(p => p.Opcode == opcode).Select(p => p.Payload)];

    public static List<WorldOpcode> Opcodes(FakeSession session) => [.. session.Sent.Select(p => p.Opcode)];
}

/// <summary>An in-memory spellbook.</summary>
internal sealed class MemorySpellbook : ISpellbook
{
    public Dictionary<ObjectGuid, HashSet<uint>> Spells { get; } = [];

    public bool HasSpell(Player player, uint spellId) => Spells.TryGetValue(player.Guid, out HashSet<uint>? book) && book.Contains(spellId);

    public bool LearnSpell(Player player, uint spellId)
    {
        if (!Spells.TryGetValue(player.Guid, out HashSet<uint>? book))
        {
            book = [];
            Spells[player.Guid] = book;
        }

        return book.Add(spellId);
    }

    public void Teach(Player player, params uint[] spells)
    {
        foreach (uint spell in spells)
        {
            LearnSpell(player, spell);
        }
    }
}
