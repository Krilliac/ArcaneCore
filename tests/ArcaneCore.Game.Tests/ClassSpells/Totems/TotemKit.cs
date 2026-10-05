using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Game.Totems;
using ArcaneCore.Kernel.WorldData.Creatures;
using static ArcaneCore.Game.Tests.CreatureTestSupport;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.ClassSpells.Totems;

/// <summary>A world with a creature system, a spell system sharing its clock and a <see cref="TotemSystem"/>, and synthetic totem spells shaped like classic-db's.</summary>
internal sealed class TotemKit : IDisposable
{
    // Spell ids are synthetic; the shapes follow classic-db spell_template (ids in the comments).
    public const uint EarthTotemSummon = 930001;   // Stoneskin Totem 8071 shape: SUMMON_TOTEM_SLOT2, target 41, value 5
    public const uint FireTotemSummon = 930002;    // Searing/Magma shape: SUMMON_TOTEM_SLOT1, target 44
    public const uint WaterTotemSummon = 930003;   // Healing Stream shape: SUMMON_TOTEM_SLOT3, target 42
    public const uint AirTotemSummon = 930004;     // Windfury/Grace shape: SUMMON_TOTEM_SLOT4, target 43
    public const uint NoSlotSummon = 930005;       // SUMMON_TOTEM (74)
    public const uint DestroyAll = 930006;         // Totemic Call shape: DESTROY_ALL_TOTEMS
    public const uint PartyPassive = 930010;       // Healing Stream passive shape: APPLY_AREA_AURA_PARTY, permanent
    public const uint ImmediatePassive = 8145;     // Tremor Totem Passive: first tick immediately (SpellAuras.cpp:8086-8110)
    public const uint SlowPassive = 930012;        // a periodic passive with the ordinary first tick one amplitude later
    public const uint ActiveSummon = 930013;       // Searing Totem shape: an active, stationary fire totem
    public const uint ActiveBolt = 930014;         // Searing Bolt: cast-time fire damage with a 20 yd range

    public const uint BrokenSummonId = 930020;     // a summon whose creature entry has no template

    public const uint EarthEntry = 940001;
    public const uint FireEntry = 940002;
    public const uint WaterEntry = 940003;
    public const uint AirEntry = 940004;
    public const uint NoSlotEntry = 940005;
    public const uint TremorEntry = 940006;
    public const uint SlowEntry = 940007;
    public const uint SilentEntry = 940008;
    public const uint ActiveEntry = 940009;

    public TotemKit(TotemOptions? options = null, int summonDurationMs = 60_000)
    {
        CreatureContent content = Content(
        [
            Template(EarthEntry, t => t.AIName = "TotemAI"),
            Template(FireEntry, t => t.AIName = "TotemAI"),
            Template(WaterEntry, t => t.AIName = "TotemAI"),
            Template(AirEntry, t => t.AIName = "TotemAI"),
            Template(NoSlotEntry, t => t.AIName = "TotemAI"),
            Template(TremorEntry, t => t.AIName = "TotemAI"),
            Template(SlowEntry, t => t.AIName = "TotemAI"),
            Template(SilentEntry, t => t.AIName = "TotemAI"),
            Template(ActiveEntry, t => t.AIName = "TotemAI"),
            Template(WolfEntry),
        ], []);
        Content = content;
        (World, Map, Creatures) = CreateSystem(content);
        Store = new SpellStore(
            [
                Summon(EarthTotemSummon, SpellEffectName.SummonTotemSlot2, 41, EarthEntry, summonDurationMs),
                Summon(FireTotemSummon, SpellEffectName.SummonTotemSlot1, 44, FireEntry, summonDurationMs),
                Summon(WaterTotemSummon, SpellEffectName.SummonTotemSlot3, 42, WaterEntry, summonDurationMs),
                Summon(AirTotemSummon, SpellEffectName.SummonTotemSlot4, 43, AirEntry, summonDurationMs),
                Summon(NoSlotSummon, SpellEffectName.SummonTotem, 41, NoSlotEntry, summonDurationMs),
                Summon(930007, SpellEffectName.SummonTotemSlot2, 41, TremorEntry, summonDurationMs),
                Summon(930008, SpellEffectName.SummonTotemSlot2, 41, SlowEntry, summonDurationMs),
                Summon(930009, SpellEffectName.SummonTotemSlot2, 41, SilentEntry, summonDurationMs),
                Summon(ActiveSummon, SpellEffectName.SummonTotemSlot1, 44, ActiveEntry, summonDurationMs),
                Spell(ActiveBolt, Effect(SpellEffectName.SchoolDamage, 15, SpellImplicitTarget.UnitEnemy)) with
                {
                    School = SpellSchool.Fire,
                    CastTime = new SpellCastTime(1000, 0, 0),
                    RangeIndex = 4,
                    Range = new SpellRange(0, 20),
                    StartRecoveryCategory = 0,
                    StartRecoveryTime = 0,
                },
                Spell(DestroyAll, Effect(SpellEffectName.DestroyAllTotems, 0, SpellImplicitTarget.UnitCaster)) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 },
                Passive(PartyPassive, AuraType.Dummy, amplitude: 0, area: true),
                Passive(ImmediatePassive, AuraType.PeriodicTriggerSpell, amplitude: 3000, area: false),
                Passive(SlowPassive, AuraType.PeriodicTriggerSpell, amplitude: 3000, area: false),
                .. DefaultSpells(),
            ],
            [],
            []);
        Spellbook = new MemorySpellbook();
        Spells = new SpellSystem(Store, () => Now, spellbook: Spellbook, random: new Random(1)) { MapUpdateIntervalMs = 0 };
        Relations = new FakeRelations();
        Groups = new FakeGroups();
        Spells.Relations = Relations;
        Spells.Groups = new OwnerAwareGroupResolver(Groups);
        Totems = new TotemSystem(
            Spells,
            map => ReferenceEquals(map, Map) ? Creatures : null,
            entry => Content.FindTemplate(entry),
            entry => entry switch
            {
                EarthEntry or FireEntry or WaterEntry or AirEntry or NoSlotEntry => PartyPassive,
                TremorEntry => ImmediatePassive,
                SlowEntry => SlowPassive,
                ActiveEntry => ActiveBolt,
                _ => null,
            },
            options);
        Registered = Totems.Register();
        Totems.EnsureUpdater(Map);
    }

    public CreatureContent Content { get; }

    public WorldRuntime World { get; }

    public Map Map { get; }

    public CreatureMapSystem Creatures { get; }

    public SpellStore Store { get; }

    public MemorySpellbook Spellbook { get; }

    public SpellSystem Spells { get; }

    public FakeRelations Relations { get; }

    public FakeGroups Groups { get; }

    public TotemSystem Totems { get; }

    public bool Registered { get; }

    public uint Now { get; set; } = 10_000;

    public (Player Player, FakeSession Session) AddPlayer(uint guid, float x = 0, float y = 0)
    {
        var session = new FakeSession((int)guid);
        Player player = TestWorld.CreatePlayer(guid, x, y, session);
        World.AddPlayer(player);
        World.RunTick(0);
        session.Clear();
        return (player, session);
    }

    /// <summary>Advance the spell clock, the spell system and the world (map updaters) together.</summary>
    public void Advance(uint ms, uint step = 100)
    {
        for (uint done = 0; done < ms; done += step)
        {
            uint diff = Math.Min(step, ms - done);
            Now += diff;
            Spells.Update(diff);
            World.RunTick(diff);
        }
    }

    public SpellCastResult Cast(Unit caster, uint spellId)
        => Spells.CastSpell(caster, spellId, SpellCastTargets.ForSelf(), triggered: true);

    public void Dispose() => World.Dispose();

    public static SpellInfo BrokenSummonSpell() => Summon(BrokenSummonId, SpellEffectName.SummonTotemSlot2, 41, 999999, 60_000);

    private static SpellInfo Summon(uint id, SpellEffectName effect, uint target, uint entry, int durationMs)
        => Spell(id, Effect(effect, 5, (SpellImplicitTarget)target, misc: (int)entry) with { Radius = 2 }) with
        {
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
            Duration = new SpellDuration(durationMs, 0, durationMs),
        };

    private static SpellInfo Passive(uint id, AuraType aura, uint amplitude, bool area)
        => Spell(id, Effect(area ? SpellEffectName.ApplyAreaAuraParty : SpellEffectName.ApplyAura, 1, SpellImplicitTarget.UnitCaster, aura, amplitude: amplitude) with { Radius = 30 }) with
        {
            Attributes = SpellAttributes.Passive,
            Duration = new SpellDuration(-1, 0, -1),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };
}
