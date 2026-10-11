using System.Globalization;
using System.Text;
using ArcaneCore.Game;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.World.Playerbots.Combat;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>One gap of a spell bots can cast.</summary>
/// <param name="Rotation">The spell is a rotation ability or an escape (bots cast it in a fight), not only a class trainer spell.</param>
/// <param name="ClassMask">The classes that can cast it (bit c = <see cref="Class"/> c).</param>
internal sealed record QaCoverageGap(uint Spell, string Name, string Rank, int EffectIndex, string EffectName, int EffectValue, uint Aura, string Code,
    string Detail, uint ClassMask, bool Rotation);

/// <summary>A gap shared by several spells: the same missing effect, aura type or script, and how many spells have it.</summary>
internal sealed record QaCoverageOffender(string Code, string Detail, int Spells, int RotationSpells);

/// <summary>
/// The spell coverage of the bots (<see cref="PlayerbotQaSpellCoverage.Build"/>): of the spells bots can cast, how many the spell system fully handles,
/// and every gap. Immutable.
/// </summary>
internal sealed record QaSpellCoverageReport(DateTime ComputedUtc, int Castable, int RotationCastable, int SpellsWithGaps, int RotationSpellsWithGaps,
    int TrainerRows, IReadOnlyList<QaCoverageGap> Gaps)
{
    /// <summary>The gaps shared by the most spells (then rotation spells, then code and name), at most <paramref name="n"/>.</summary>
    public IReadOnlyList<QaCoverageOffender> TopOffenders(int n)
        => [.. Gaps.GroupBy(g => (g.Code, g.Detail))
            .Select(group => new QaCoverageOffender(group.Key.Code, group.Key.Detail, group.Select(g => g.Spell).Distinct().Count(),
                group.Where(g => g.Rotation).Select(g => g.Spell).Distinct().Count()))
            .OrderByDescending(o => o.Spells).ThenByDescending(o => o.RotationSpells).ThenBy(o => o.Code, StringComparer.Ordinal)
            .ThenBy(o => o.Detail, StringComparer.Ordinal).Take(Math.Max(0, n))];

    /// <summary>The 25 gaps shared by the most spells (report.json).</summary>
    public IReadOnlyList<QaCoverageOffender> Offenders => TopOffenders(25);

    /// <summary>The one-line summary (start-up log, chat).</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"Spell coverage: {Castable - SpellsWithGaps} of {Castable} spells bots can cast are fully handled ({RotationCastable - RotationSpellsWithGaps} of {RotationCastable} rotation and escape spells); {Gaps.Count} gaps in {SpellsWithGaps} spells ({RotationSpellsWithGaps} rotation spells)");

    /// <summary>The <c>## Spell coverage</c> section of <c>report.md</c>.</summary>
    public string RenderMarkdown(int maxRows = 300)
    {
        var sb = new StringBuilder();
        sb.Append("## Spell coverage\n\n");
        sb.Append(CultureInfo.InvariantCulture, $"Computed {ComputedUtc:u} from the spell table and the live handler registries ({TrainerRows} class trainer rows). ");
        sb.Append(Describe()).Append(".\n\n");
        sb.Append("Bots can cast the class spell books at trainer level: the class spells trainers teach, the start spells and the talent spells; the ones a ")
            .Append("class rotation or an escape names are the rotation spells. A gap is an effect with no handler, an aura type nothing implements, a DUMMY ")
            .Append("or OVERRIDE_CLASS_SCRIPTS aura nothing known reads (advisory: a rule may read it by spell id; never published), or a DUMMY / SCRIPT_EFFECT ")
            .Append("that is a spell's only behaviour with no script. Each gap is a `spell unsupported` finding; one is published only once a bot really casts it.\n\n");
        IReadOnlyList<QaCoverageOffender> top = TopOffenders(15);
        if (top.Count > 0)
        {
            sb.Append("| Missing | Spells | Rotation spells |\n|---|---|---|\n");
            foreach (QaCoverageOffender o in top)
            {
                sb.Append(CultureInfo.InvariantCulture, $"| {QaSpellGaps.Describe(o.Code, o.Detail)} | {o.Spells} | {o.RotationSpells} |\n");
            }

            sb.Append('\n');
        }

        if (Gaps.Count == 0)
        {
            sb.Append("No gaps.\n\n");
            return sb.ToString();
        }

        sb.Append("| Spell | Name | Classes | Rotation | Effect | Gap |\n|---|---|---|---|---|---|\n");
        foreach (QaCoverageGap g in Gaps.OrderByDescending(g => g.Rotation).ThenBy(g => g.Spell).ThenBy(g => g.EffectIndex).Take(maxRows))
        {
            string name = (g.Name + (g.Rank.Length != 0 ? " (" + g.Rank + ")" : string.Empty)).Replace("|", "\\|", StringComparison.Ordinal);
            sb.Append(CultureInfo.InvariantCulture,
                $"| {g.Spell} | {name} | {QaFindingView.ClassNamesOf(g.ClassMask)} | {(g.Rotation ? "yes" : "no")} | #{g.EffectIndex} {g.EffectName} | {QaSpellGaps.Describe(g.Code, g.Detail)} |\n");
        }

        if (Gaps.Count > maxRows) sb.Append(CultureInfo.InvariantCulture, $"\n{Gaps.Count - maxRows} more gaps are in report.json.\n");
        sb.Append('\n');
        return sb.ToString();
    }
}

/// <summary>
/// The static spell coverage of the bots, after <c>TalentEffectCoverage</c>: which spells bots can cast hit an effect, aura type or script nothing
/// implements (<see cref="PlayerbotQaSpellGapAnalyzer"/>). The spells bots can cast are the class spell books at trainer level: every class spell a
/// class trainer teaches (<c>npc_trainer</c>: the teaching spell's LEARN_SPELL targets, or the row's spell itself), the spells a new character of each
/// race and class starts with (<c>playercreateinfo_spell</c>) and the talent rank spells; a class spell is one of a class's spell family, so an NPC
/// spell is never counted. Those whose name a class rotation or the escapes use are the rotation spells (a bot resolves its abilities from its book
/// by name). Passive spells are left out (bots do not cast them). Pure; run it off the world thread after start-up (the registries are complete then
/// and only read).
/// </summary>
internal static class PlayerbotQaSpellCoverage
{
    /// <summary>The spell family of a class's spells (SpellFamilyName: mage 3, warrior 4, warlock 5, priest 6, druid 7, rogue 8, hunter 9, paladin 10, shaman 11).</summary>
    internal static uint FamilyOf(Class playerClass) => playerClass switch
    {
        Class.Mage => 3,
        Class.Warrior => 4,
        Class.Warlock => 5,
        Class.Priest => 6,
        Class.Druid => 7,
        Class.Rogue => 8,
        Class.Hunter => 9,
        Class.Paladin => 10,
        Class.Shaman => 11,
        _ => 0,
    };

    /// <summary>The class of a spell family (0 for a family that is no class's).</summary>
    internal static Class? ClassOf(uint family) => family switch
    {
        3 => Class.Mage,
        4 => Class.Warrior,
        5 => Class.Warlock,
        6 => Class.Priest,
        7 => Class.Druid,
        8 => Class.Rogue,
        9 => Class.Hunter,
        10 => Class.Paladin,
        11 => Class.Shaman,
        _ => null,
    };

    /// <summary>The coverage of the production rotations and escapes, <paramref name="trainerRows"/>, the start spells and <paramref name="talentSpells"/>.</summary>
    internal static QaSpellCoverageReport Build(SpellSystem system, IReadOnlyList<TrainerSpell> trainerRows, IEnumerable<uint> talentSpells, DateTime now)
        => Build(system, [.. PlayerbotRotations.Every.Select(r => (r.Class, (IReadOnlyCollection<string>)[.. r.Abilities, .. PlayerbotEscapes.All]))], trainerRows,
            talentSpells, now);

    /// <summary>
    /// The coverage of the class spell books (<paramref name="trainerRows"/>, the store's start spells, <paramref name="otherBookSpells"/> such as the
    /// talent rank spells), with the names per class in <paramref name="rotations"/> marking the rotation spells.
    /// </summary>
    internal static QaSpellCoverageReport Build(SpellSystem system, IReadOnlyList<(Class Class, IReadOnlyCollection<string> Names)> rotations,
        IReadOnlyList<TrainerSpell> trainerRows, IEnumerable<uint> otherBookSpells, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(rotations);
        ArgumentNullException.ThrowIfNull(trainerRows);
        ArgumentNullException.ThrowIfNull(otherBookSpells);
        SpellStore store = system.Store;
        var castable = new Dictionary<uint, (SpellInfo Spell, uint Classes, bool Rotation)>();
        var names = new Dictionary<Class, HashSet<string>>();
        foreach ((Class playerClass, IReadOnlyCollection<string> list) in rotations)
        {
            if (!names.TryGetValue(playerClass, out HashSet<string>? set)) names[playerClass] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set.UnionWith(list);
        }

        void Book(SpellInfo? spell)
        {
            if (spell is null || spell.IsPassive || ClassOf(spell.SpellFamilyName) is not { } playerClass) return;
            Add(castable, spell, playerClass, names.TryGetValue(playerClass, out HashSet<string>? set) && set.Contains(spell.Name));
        }

        foreach (Class playerClass in Enum.GetValues<Class>())
        {
            foreach (Race race in Enum.GetValues<Race>())
            {
                foreach (uint id in store.GetCreateSpells((byte)race, (byte)playerClass)) Book(store.Get(id));
            }
        }

        foreach (uint id in otherBookSpells)
        {
            if (id != 0) Book(store.Get(id));
        }

        foreach (TrainerSpell row in trainerRows)
        {
            if (store.Get(row.Spell) is not { } teaching) continue;
            bool teaches = false;
            foreach (SpellEffectInfo effect in teaching.Effects)
            {
                if (effect.Effect != SpellEffectName.LearnSpell || effect.TriggerSpell == 0) continue;
                teaches = true;
                Book(store.Get(effect.TriggerSpell));
            }

            if (!teaches) Book(teaching);
        }

        var analyzer = new PlayerbotQaSpellGapAnalyzer(system);
        var gaps = new List<QaCoverageGap>();
        int withGaps = 0, rotationCastable = 0, rotationWithGaps = 0;
        foreach ((SpellInfo spell, uint classes, bool rotation) in castable.Values.OrderBy(v => v.Spell.Id))
        {
            if (rotation) rotationCastable++;
            QaSpellGap[] found = analyzer.Of(spell);
            if (found.Length == 0) continue;
            withGaps++;
            if (rotation) rotationWithGaps++;
            foreach (QaSpellGap gap in found)
            {
                gaps.Add(new QaCoverageGap(spell.Id, spell.Name, spell.Rank, gap.EffectIndex, gap.EffectName, gap.EffectValue, gap.Aura, gap.Code, gap.Detail,
                    classes, rotation));
            }
        }

        return new QaSpellCoverageReport(now, castable.Count, rotationCastable, withGaps, rotationWithGaps, trainerRows.Count, gaps);
    }

    /// <summary>The coverage gaps as signals for the aggregator: static facts (<see cref="QaSignal.FromCoverage"/>), keyed like a bot's cast of them.</summary>
    internal static IEnumerable<QaSignal> Signals(QaSpellCoverageReport report)
        => report.Gaps.Select(g => new QaSignal
        {
            Kind = QaSignalKind.SpellUnsupported, AtUtc = report.ComputedUtc, FromCoverage = true, Spell = g.Spell, SpellName = g.Name, SpellRank = g.Rank,
            EffectIndex = g.EffectIndex, EffectName = g.EffectName, EffectValue = g.EffectValue, Aura = g.Aura, Reason = g.Code, Detail = g.Detail,
            Message = "start-up spell coverage: " + QaSpellGaps.Describe(g.Code, g.Detail),
        });

    private static void Add(Dictionary<uint, (SpellInfo Spell, uint Classes, bool Rotation)> castable, SpellInfo spell, Class playerClass, bool rotation)
    {
        uint bit = 1u << (int)playerClass;
        castable[spell.Id] = castable.TryGetValue(spell.Id, out (SpellInfo Spell, uint Classes, bool Rotation) known)
            ? (spell, known.Classes | bit, known.Rotation || rotation)
            : (spell, bit, rotation);
    }
}
