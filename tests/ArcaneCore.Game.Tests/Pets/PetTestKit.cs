using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>
/// A world with the real spell system, a real creature system on map 0 and the real
/// <see cref="SummonService"/> installed, so a spell cast ends in a creature in the map (the
/// RED/GREEN proofs of the pets area do not use fakes).
/// </summary>
internal sealed class PetTestKit : IDisposable
{
    public const uint TotemEntry = 5001;
    public const uint PetEntry = 5002;
    public const uint GuardianEntry = 5003;
    public const uint MiniPetEntry = 5004;
    public const uint WildEntry = 5005;
    public const uint NpcCasterEntry = 5006;
    public const uint MiniPetEntry2 = 5007;
    public const uint ImpEntry = 416;

    public const uint FireTotemSpell = 910001;
    public const uint EarthTotemSpell = 910002;
    public const uint SlotlessTotemSpell = 910003;
    public const uint ShortTotemSpell = 910004;
    public const uint PetSpell = 910005;
    public const uint GuardianSpell = 910010;
    public const uint TwoGuardiansSpell = 910011;
    public const uint TimedGuardianSpell = 910012;
    public const uint CooldownGuardianSpell = 910013;
    public const uint NpcRelativeLevelSpell = 910014;
    public const uint NpcFixedLevelSpell = 910015;
    public const uint ThreeGuardiansSpell = 910016;
    public const uint WildSpell = 910020;
    public const uint WildRadiusSpell = 910021;
    public const uint WildThreeSpell = 910022;
    public const uint TimedWildSpell = 910023;
    public const uint CritterSpell = 910030;
    public const uint Critter2Spell = 910031;
    public const uint PetBiteSpell = 910040;
    public const uint PetShieldSpell = 910041;
    public const uint PetPassiveSpell = 910042;
    public const uint ImpSpell = 910043;

    public const int TotemDurationMs = 30_000;

    public PetTestKit(IEnumerable<SpellInfo>? extraSpells = null)
    {
        Spells = new SpellTestKit([.. DefaultPetSpells(), .. extraSpells ?? []]);
        Map = Spells.World.GetMap(0);
        Content = CreatureTestSupport.Content(
            [
                .. new[] { TotemEntry, PetEntry, GuardianEntry, MiniPetEntry, WildEntry, MiniPetEntry2, ImpEntry }.Select(entry => CreatureTestSupport.Template(entry, b =>
                {
                    b.Name = $"Summon {entry}";
                    b.Faction = 14;
                    b.MinLevel = 5;
                    b.MaxLevel = 5;
                    b.MinLevelHealth = 100;
                    b.MaxLevelHealth = 100;
                    b.NpcFlags = entry == MiniPetEntry ? 2u : 0u; // some mini pets have quests
                })),
                CreatureTestSupport.Template(NpcCasterEntry, b =>
                {
                    b.Name = "Summoner";
                    b.Faction = 14;
                    b.MinLevel = 10;
                    b.MaxLevel = 10;
                    b.MinLevelHealth = 500;
                    b.MaxLevelHealth = 500;
                }),
            ],
            []);
        Creatures = new CreatureMapSystem(Map, Content, random: new Random(1));
        Map.AddUpdater(Creatures);
        Service = new SummonService(systems: map => ReferenceEquals(map, Map) ? Creatures : null, random: new Random(3));
        Spells.System.Units = new MapObjectResolver();
        Service.Install(Spells.System);
        Controller = new PetController(Service, () => Spells.System, new Random(5));
        Spells.System.Summons = Service;
    }

    public SpellTestKit Spells { get; }

    public Map Map { get; }

    public CreatureContent Content { get; }

    public CreatureMapSystem Creatures { get; }

    public SummonService Service { get; }

    public PetController Controller { get; }

    public (Player Player, FakeSession Session) AddPlayer(uint guid, float x = 0, float y = 0) => Spells.AddPlayer(guid, x, y);

    /// <summary>Cast a spell triggered at the caster itself (summon effects take the caster or the destination).</summary>
    public SpellCastResult Cast(Unit caster, uint spell, SpellCastTargets? targets = null)
        => Spells.System.CastSpell(caster, spell, targets ?? SpellCastTargets.ForSelf(), triggered: true);

    /// <summary>Run the world (creatures, pets, visibility) for <paramref name="ms"/> in 100 ms ticks.</summary>
    public void Run(uint ms, uint step = 100)
    {
        for (uint done = 0; done < ms; done += step)
        {
            Spells.World.RunTick(Math.Min(step, ms - done));
        }
    }

    public void Dispose() => Spells.Dispose();

    public static IEnumerable<SpellInfo> DefaultPetSpells() =>
    [
        Spell(FireTotemSpell, Effect(SpellEffectName.SummonTotemSlot1, 5, misc: (int)TotemEntry)) with
        {
            Duration = new SpellDuration(TotemDurationMs, 0, TotemDurationMs),
        },
        Spell(EarthTotemSpell, Effect(SpellEffectName.SummonTotemSlot2, 5, misc: (int)TotemEntry)) with
        {
            Duration = new SpellDuration(TotemDurationMs, 0, TotemDurationMs),
        },
        Spell(SlotlessTotemSpell, Effect(SpellEffectName.SummonTotem, 5, misc: (int)TotemEntry)) with
        {
            Duration = new SpellDuration(TotemDurationMs, 0, TotemDurationMs),
        },
        Spell(ShortTotemSpell, Effect(SpellEffectName.SummonTotemSlot3, 5, misc: (int)TotemEntry)) with
        {
            Duration = new SpellDuration(1_000, 0, 1_000),
        },
        Spell(PetSpell, Effect(SpellEffectName.Summon, 0, misc: (int)PetEntry)) with
        {
            Duration = new SpellDuration(-1, 0, -1),
        },
        Spell(GuardianSpell, Effect(SpellEffectName.SummonGuardian, 1, misc: (int)GuardianEntry)),
        Spell(TwoGuardiansSpell, Effect(SpellEffectName.SummonGuardian, 2, misc: (int)GuardianEntry)),
        Spell(ThreeGuardiansSpell, Effect(SpellEffectName.SummonGuardian, 3, misc: (int)GuardianEntry) with { Radius = 5f }),
        Spell(TimedGuardianSpell, Effect(SpellEffectName.SummonGuardian, 1, misc: (int)GuardianEntry)) with
        {
            Duration = new SpellDuration(1_000, 0, 1_000),
        },
        Spell(CooldownGuardianSpell, Effect(SpellEffectName.SummonGuardian, 1, misc: (int)GuardianEntry)) with
        {
            Duration = new SpellDuration(30_000, 0, 30_000),
            Category = 77,
        },
        Spell(NpcRelativeLevelSpell, Effect(SpellEffectName.SummonGuardian, 1, misc: (int)GuardianEntry) with { MultipleValue = -3f }),
        Spell(NpcFixedLevelSpell, Effect(SpellEffectName.SummonGuardian, 1, misc: (int)GuardianEntry) with { MultipleValue = 2f }),
        Spell(WildSpell, Effect(SpellEffectName.SummonWild, 1, misc: (int)WildEntry)),
        Spell(WildRadiusSpell, Effect(SpellEffectName.SummonWild, 1, misc: (int)WildEntry) with { Radius = 4f }),
        Spell(WildThreeSpell, Effect(SpellEffectName.SummonWild, 3, misc: (int)WildEntry) with { Radius = 5f }),
        Spell(TimedWildSpell, Effect(SpellEffectName.SummonWild, 1, misc: (int)WildEntry)) with
        {
            Duration = new SpellDuration(1_000, 0, 1_000),
        },
        Spell(CritterSpell, Effect(SpellEffectName.SummonCritter, 1, misc: (int)MiniPetEntry)),
        Spell(Critter2Spell, Effect(SpellEffectName.SummonCritter, 1, misc: (int)MiniPetEntry2)),
        Spell(PetBiteSpell, Effect(SpellEffectName.SchoolDamage, 8, SpellImplicitTarget.UnitEnemy)) with
        {
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
        },
        Spell(PetShieldSpell, Effect(SpellEffectName.Heal, 5, SpellImplicitTarget.UnitFriend)),
        Spell(ImpSpell, Effect(SpellEffectName.Summon, 0, misc: (int)ImpEntry)),
        Spell(PetPassiveSpell, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
        {
            Attributes = SpellAttributes.Passive,
            Duration = new SpellDuration(-1, 0, -1),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        },
    ];
}

/// <summary>The world daemon's unit resolver (creatures and players through the map's object registry), for the tests that cast at creatures.</summary>
internal sealed class MapObjectResolver : ISpellUnitResolver
{
    public Unit? Find(Unit reference, ObjectGuid guid)
        => guid.IsEmpty ? null : reference.Guid == guid ? reference : reference.Map?.FindObject(guid) as Unit;
}
