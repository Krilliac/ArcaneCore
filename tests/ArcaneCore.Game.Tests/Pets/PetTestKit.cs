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

    public const uint FireTotemSpell = 910001;
    public const uint EarthTotemSpell = 910002;
    public const uint SlotlessTotemSpell = 910003;
    public const uint ShortTotemSpell = 910004;
    public const uint PetSpell = 910005;

    public const int TotemDurationMs = 30_000;

    public PetTestKit(IEnumerable<SpellInfo>? extraSpells = null)
    {
        Spells = new SpellTestKit([.. DefaultPetSpells(), .. extraSpells ?? []]);
        Map = Spells.World.GetMap(0);
        Content = CreatureTestSupport.Content(
            [
                .. new[] { TotemEntry, PetEntry, GuardianEntry, MiniPetEntry, WildEntry }.Select(entry => CreatureTestSupport.Template(entry, b =>
                {
                    b.Name = $"Summon {entry}";
                    b.Faction = 14;
                    b.MinLevel = 5;
                    b.MaxLevel = 5;
                    b.MinLevelHealth = 100;
                    b.MaxLevelHealth = 100;
                })),
            ],
            []);
        Creatures = new CreatureMapSystem(Map, Content, random: new Random(1));
        Map.AddUpdater(Creatures);
        Service = new SummonService(systems: map => ReferenceEquals(map, Map) ? Creatures : null);
        Service.Install(Spells.System);
        Spells.System.Summons = Service;
    }

    public SpellTestKit Spells { get; }

    public Map Map { get; }

    public CreatureContent Content { get; }

    public CreatureMapSystem Creatures { get; }

    public SummonService Service { get; }

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
    ];
}
