using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using R = ArcaneCore.Game.Spells.InertSpellReason;

namespace ArcaneCore.Game.Spells;

/// <summary>Why a spell is deliberately inert (see <see cref="DeliberatelyInertSpells"/>).</summary>
public enum InertSpellReason
{
    /// <summary>An effect or aura type no reference core handles either (vmangos EffectUnused, EffectEmpty, HandleUnused): it has nothing to implement.</summary>
    UnusedEffect,

    /// <summary>A zzOLD, DONOTREUSE, NYI or "Copy of" row kept in Spell.dbc that no core handles.</summary>
    Deprecated,

    /// <summary>A test or debug spell no core handles.</summary>
    TestSpell,

    /// <summary>A server-side marker, trigger or despawn carrier whose effect does nothing in the reference cores.</summary>
    Marker,

    /// <summary>A visual effect or aura only the client renders.</summary>
    Visual,

    /// <summary>A creature spawn aura applied for its look.</summary>
    SpawnAura,

    /// <summary>Cast by a creature AI or a DB script for its visual or marker; the effect itself has no handler in any reference core.</summary>
    ScriptCarrier,

    /// <summary>Nothing casts it: no reference core, world DB script, EventAI row, spell list, item, game object or cast spell's trigger reaches it.</summary>
    NeverCast,
}

/// <summary>One deliberately inert spell: why, and the evidence.</summary>
/// <param name="SpellId">The Spell.dbc id.</param>
/// <param name="Reason">The category.</param>
/// <param name="Why">One line: why nothing needs to run for it.</param>
/// <param name="Citation">A reference file:line, or "no core handles it" (nothing in vmangos, cmangos or mangoszero handles the spell).</param>
/// <param name="Unhandled">The effect or aura type name this entry also covers when ArcaneCore has no handler for it (the QA gap code Effect or Aura); null when only DUMMY and SCRIPT_EFFECT gaps are covered.</param>
public sealed record InertSpell(uint SpellId, InertSpellReason Reason, string Why, string Citation, string? Unhandled = null)
{
    /// <summary>Whether this entry covers a missing handler for <paramref name="name"/> (an effect name or an aura type name).</summary>
    public bool CoversUnhandled(string name) => Unhandled is not null && string.Equals(Unhandled, name, StringComparison.Ordinal);
}

/// <summary>
/// The explicit list of spells that are inert by design: their DBC auras and effects still apply exactly as today, and no handler or script exists
/// because no reference core (vmangos, cmangos, mangoszero) implements one. The registry carries a reason and a citation only: it changes no
/// behaviour. The spell QA gap analyzer (<c>PlayerbotQaSpellGapAnalyzer</c>) reads it so a DUMMY, SCRIPT_EFFECT or unused effect that is deliberately
/// inert is not reported as an unimplemented spell.
/// <para>
/// A spell whose behaviour exists in a reference core is not listed here; neither is one ArcaneCore consumes elsewhere (those are
/// <c>QaSpellConsumers</c> entries naming the consumer). Entries come from the spell audit of 2026-10-29 and were re-checked against the
/// vmangos, cmangos and mangoszero source trees and the vmangos world DB. Immutable after type initialisation; safe from any thread.
/// </para>
/// </summary>
public static partial class DeliberatelyInertSpells
{
    private const string WhyDeprecated = "deprecated or not-yet-implemented spell kept in Spell.dbc (zzOLD, DONOTREUSE, NYI, Copy of)";
    private const string WhyCarrier = "cast by a creature AI or DB script for its visual or marker; the effect itself has no handler in any core";
    private const string WhyMarker = "server-side marker, trigger or despawn carrier; its effect does nothing in the reference cores";
    private const string WhyVisual = "visual effect or aura";
    private const string WhySpawnAura = "creature spawn aura applied for its look only";
    private const string WhyMissiles = "the DUMMY aura of this spell has no reader (the real work is its periodic trigger)";
    private const string WhyPlayerDummy = "the DUMMY aura of this player spell has no reader (the real work is in its other effects)";
    private const string NoCore = "no core handles it";
    private const string CitMissiles = "vmangos Spell.cpp:248 and 662 special-case the triggered missiles, not the DUMMY aura; cmangos and mangoszero have no reader";
    private const string CitMindSpells = "vmangos SpellAuras.cpp:7752 and 7820 list rank 1 only in CalculateForDebuffLimit (debuff slots), not as a DUMMY reader";
    private const string WhyTest = "test spell (the name says test)";

    private static readonly InertSpell[] s_all = [.. Build(), .. BuildResearched()];
    private static readonly FrozenDictionary<uint, InertSpell> s_byId = Index(s_all);

    /// <summary>Every entry, grouped by reason.</summary>
    public static IReadOnlyList<InertSpell> All => s_all;

    /// <summary>The entry of <paramref name="spellId"/>, if it is deliberately inert.</summary>
    public static bool TryGet(uint spellId, [NotNullWhen(true)] out InertSpell? entry) => s_byId.TryGetValue(spellId, out entry);

    private static FrozenDictionary<uint, InertSpell> Index(InertSpell[] all)
    {
        var map = new Dictionary<uint, InertSpell>(all.Length);
        foreach (InertSpell entry in all)
        {
            if (!map.TryAdd(entry.SpellId, entry))
            {
                throw new InvalidOperationException($"DeliberatelyInertSpells lists spell {entry.SpellId} twice");
            }
        }

        return map.ToFrozenDictionary();
    }

    private static InertSpell[] Build() =>
    [
        // UnusedEffect (14)
        new(978, R.UnusedEffect, "effect or aura SharePetTracking is unused in the reference cores", "vmangos SpellAuras.cpp:184 HandleUnused (useless); no core handles spell 978", "SharePetTracking"),
        new(1492, R.UnusedEffect, "effect or aura GuaranteeHit is unused in the reference cores", "vmangos SpellEffects.cpp:112 EffectUnused (unused from pre-1.2.1); no core handles spell 1492", "GuaranteeHit"),
        new(1941, R.UnusedEffect, "effect or aura PeriodicManaFunnel is unused in the reference cores", "vmangos SpellAuras.cpp:128 HandleUnused (obsolete?); no core handles spell 1941", "PeriodicManaFunnel"),
        new(2426, R.UnusedEffect, "effect or aura Stealth is unused in the reference cores", "vmangos SpellEffects.cpp:108 EffectUnused (one spell: Base Stealth); no core handles spell 2426", "Stealth"),
        new(8127, R.UnusedEffect, "effect or aura ThreatAll is unused in the reference cores", "vmangos SpellEffects.cpp:151 EffectUnused (one spell: zzOLDBrainwash); no core handles spell 8127", "ThreatAll"),
        new(8194, R.UnusedEffect, "effect or aura ThreatAll is unused in the reference cores", "vmangos SpellEffects.cpp:151 EffectUnused (one spell: zzOLDBrainwash); no core handles spell 8194", "ThreatAll"),
        new(8429, R.UnusedEffect, "effect or aura SummonPhantasm is unused in the reference cores", "vmangos SpellEffects.cpp:153 EffectUnused; no core handles spell 8429", "SummonPhantasm"),
        new(8479, R.UnusedEffect, "effect or aura SummonPhantasm is unused in the reference cores", "vmangos SpellEffects.cpp:153 EffectUnused; no core handles spell 8479", "SummonPhantasm"),
        new(8480, R.UnusedEffect, "effect or aura SummonPhantasm is unused in the reference cores", "vmangos SpellEffects.cpp:153 EffectUnused; no core handles spell 8480", "SummonPhantasm"),
        new(8481, R.UnusedEffect, "effect or aura SummonPhantasm is unused in the reference cores", "vmangos SpellEffects.cpp:153 EffectUnused; no core handles spell 8481", "SummonPhantasm"),
        new(10654, R.UnusedEffect, "effect or aura Unk164 is unused in the reference cores", "vmangos SpellAuras.cpp:229 HandleUnused (useless, only one test spell); no core handles spell 10654", "Unk164"),
        new(13912, R.UnusedEffect, "effect or aura Portal is unused in the reference cores", "vmangos SpellEffects.cpp:72 EffectUnused (unused from pre-1.2.1); no core handles spell 13912", "Portal"),
        new(18523, R.UnusedEffect, "effect or aura Persuaded is unused in the reference cores", "vmangos SpellAuras.cpp:211 HandleUnused (obsolete?); no core handles spell 18523", "Persuaded"),
        new(23685, R.UnusedEffect, "effect or aura Unk126 is unused in the reference cores", "vmangos SpellEffects.cpp:186 EffectUnused (now only used one test spell); no core handles spell 23685", "Unk126"),
        // Deprecated (105)
        new(110, R.Deprecated, WhyDeprecated, NoCore),
        new(469, R.Deprecated, WhyDeprecated, NoCore),
        new(831, R.Deprecated, WhyDeprecated, NoCore),
        new(966, R.Deprecated, WhyDeprecated, NoCore),
        new(974, R.Deprecated, WhyDeprecated, NoCore),
        new(1485, R.Deprecated, WhyDeprecated, NoCore),
        new(1522, R.Deprecated, WhyDeprecated, NoCore),
        new(1523, R.Deprecated, WhyDeprecated, NoCore),
        new(1524, R.Deprecated, WhyDeprecated, NoCore),
        new(1525, R.Deprecated, WhyDeprecated, NoCore),
        new(1526, R.Deprecated, WhyDeprecated, NoCore),
        new(1527, R.Deprecated, WhyDeprecated, NoCore),
        new(1575, R.Deprecated, WhyDeprecated, NoCore),
        new(1609, R.Deprecated, WhyDeprecated, NoCore),
        new(1645, R.Deprecated, WhyDeprecated, NoCore),
        new(1648, R.Deprecated, WhyDeprecated, NoCore),
        new(1739, R.Deprecated, WhyDeprecated, NoCore),
        new(1825, R.Deprecated, WhyDeprecated, NoCore),
        new(2583, R.Deprecated, WhyDeprecated, NoCore),
        new(2587, R.Deprecated, WhyDeprecated, NoCore),
        new(2856, R.Deprecated, WhyDeprecated, NoCore),
        new(2861, R.Deprecated, WhyDeprecated, NoCore),
        new(2866, R.Deprecated, WhyDeprecated, NoCore),
        new(2894, R.Deprecated, WhyDeprecated, NoCore),
        new(2895, R.Deprecated, WhyDeprecated, NoCore),
        new(5155, R.Deprecated, WhyDeprecated, NoCore),
        new(5157, R.Deprecated, WhyDeprecated, NoCore),
        new(5371, R.Deprecated, WhyDeprecated, NoCore),
        new(5375, R.Deprecated, WhyDeprecated, NoCore),
        new(5739, R.Deprecated, WhyDeprecated, NoCore),
        new(5938, R.Deprecated, WhyDeprecated, NoCore),
        new(5939, R.Deprecated, WhyDeprecated, NoCore),
        new(5940, R.Deprecated, WhyDeprecated, NoCore),
        new(6120, R.Deprecated, WhyDeprecated, NoCore),
        new(6124, R.Deprecated, WhyDeprecated, NoCore),
        new(6154, R.Deprecated, WhyDeprecated, NoCore),
        new(6184, R.Deprecated, WhyDeprecated, NoCore),
        new(6542, R.Deprecated, WhyDeprecated, NoCore),
        new(6561, R.Deprecated, WhyDeprecated, NoCore),
        new(6562, R.Deprecated, WhyDeprecated, NoCore),
        new(6963, R.Deprecated, WhyDeprecated, NoCore),
        new(6967, R.Deprecated, WhyDeprecated, NoCore),
        new(7377, R.Deprecated, WhyDeprecated, NoCore),
        new(8128, R.Deprecated, WhyDeprecated, NoCore),
        new(8878, R.Deprecated, WhyDeprecated, NoCore),
        new(9161, R.Deprecated, WhyDeprecated, NoCore),
        new(10727, R.Deprecated, WhyDeprecated, NoCore),
        new(11372, R.Deprecated, WhyDeprecated, NoCore),
        new(11411, R.Deprecated, WhyDeprecated, NoCore),
        new(11412, R.Deprecated, WhyDeprecated, NoCore),
        new(14815, R.Deprecated, WhyDeprecated, NoCore),
        new(15227, R.Deprecated, WhyDeprecated, NoCore),
        new(16841, R.Deprecated, WhyDeprecated, NoCore),
        new(16842, R.Deprecated, WhyDeprecated, NoCore),
        new(18280, R.Deprecated, WhyDeprecated, "vmangos scripts/spells/spell_warlock.cpp:173 guards its body with SUPPORTED_CLIENT_BUILD <= CLIENT_BUILD_1_10_2, so it never runs for build 5875"),
        new(18734, R.Deprecated, WhyDeprecated, NoCore),
        new(19744, R.Deprecated, WhyDeprecated, NoCore),
        new(19976, R.Deprecated, WhyDeprecated, NoCore),
        new(20183, R.Deprecated, WhyDeprecated, NoCore),
        new(20211, R.Deprecated, WhyDeprecated, NoCore),
        new(20231, R.Deprecated, WhyDeprecated, NoCore),
        new(20411, R.Deprecated, WhyDeprecated, NoCore),
        new(20412, R.Deprecated, WhyDeprecated, NoCore),
        new(20413, R.Deprecated, WhyDeprecated, NoCore),
        new(20414, R.Deprecated, WhyDeprecated, NoCore),
        new(20415, R.Deprecated, WhyDeprecated, NoCore),
        new(20416, R.Deprecated, WhyDeprecated, NoCore),
        new(20417, R.Deprecated, WhyDeprecated, NoCore),
        new(20418, R.Deprecated, WhyDeprecated, NoCore),
        new(20721, R.Deprecated, WhyDeprecated, NoCore),
        new(20723, R.Deprecated, WhyDeprecated, NoCore),
        new(21792, R.Deprecated, WhyDeprecated, NoCore),
        new(22918, R.Deprecated, WhyDeprecated, NoCore),
        new(23575, R.Deprecated, WhyDeprecated, NoCore),
        new(23579, R.Deprecated, WhyDeprecated, NoCore),
        new(24116, R.Deprecated, WhyDeprecated, NoCore),
        new(24117, R.Deprecated, WhyDeprecated, NoCore),
        new(24224, R.Deprecated, WhyDeprecated, NoCore),
        new(24248, R.Deprecated, "\"Copy of\" spell kept in Spell.dbc", NoCore),
        new(24398, R.Deprecated, WhyDeprecated, NoCore),
        new(24663, R.Deprecated, WhyDeprecated, NoCore),
        new(24783, R.Deprecated, WhyDeprecated, NoCore),
        new(25061, R.Deprecated, WhyDeprecated, NoCore),
        new(25745, R.Deprecated, WhyDeprecated, NoCore),
        new(25759, R.Deprecated, WhyDeprecated, NoCore),
        new(25760, R.Deprecated, WhyDeprecated, NoCore),
        new(25761, R.Deprecated, WhyDeprecated, NoCore),
        new(25762, R.Deprecated, WhyDeprecated, NoCore),
        new(26219, R.Deprecated, WhyDeprecated, NoCore),
        new(26380, R.Deprecated, WhyDeprecated, NoCore),
        new(26531, R.Deprecated, WhyDeprecated, NoCore),
        new(26621, R.Deprecated, WhyDeprecated, NoCore),
        new(26640, R.Deprecated, WhyDeprecated, NoCore),
        new(27731, R.Deprecated, WhyDeprecated, NoCore),
        new(27765, R.Deprecated, WhyDeprecated, NoCore),
        new(28180, R.Deprecated, WhyDeprecated, NoCore),
        new(28309, R.Deprecated, "\"Copy of\" spell kept in Spell.dbc", NoCore),
        new(28436, R.Deprecated, WhyDeprecated, NoCore),
        new(28671, R.Deprecated, WhyDeprecated, NoCore),
        new(29070, R.Deprecated, WhyDeprecated, NoCore),
        new(29071, R.Deprecated, WhyDeprecated, NoCore),
        new(29072, R.Deprecated, WhyDeprecated, NoCore),
        new(29218, R.Deprecated, WhyDeprecated, NoCore),
        new(31466, R.Deprecated, WhyDeprecated, NoCore),
        // TestSpell (15)
        new(5581, R.TestSpell, WhyTest, NoCore),
        new(7033, R.TestSpell, WhyTest, NoCore),
        new(11593, R.TestSpell, WhyTest, NoCore),
        new(12379, R.TestSpell, WhyTest, NoCore),
        new(12565, R.TestSpell, WhyTest, NoCore),
        new(18800, R.TestSpell, WhyTest, NoCore),
        new(21342, R.TestSpell, WhyTest, NoCore),
        new(22050, R.TestSpell, WhyTest, NoCore),
        new(22690, R.TestSpell, WhyTest, NoCore),
        new(23387, R.TestSpell, WhyTest, NoCore),
        new(23388, R.TestSpell, WhyTest, NoCore),
        new(24401, R.TestSpell, WhyTest, NoCore),
        new(29530, R.TestSpell, WhyTest, NoCore),
        new(29715, R.TestSpell, WhyTest, NoCore),
        new(29820, R.TestSpell, "Spell.dbc description: DEBUG, for test realms only", NoCore),
        // Marker (65)
        new(605, R.Marker, WhyPlayerDummy, CitMindSpells),
        new(1002, R.Marker, WhyPlayerDummy, "no core handles the DUMMY aura"),
        new(2096, R.Marker, WhyPlayerDummy, CitMindSpells),
        new(5143, R.Marker, WhyMissiles, CitMissiles),
        new(5144, R.Marker, WhyMissiles, CitMissiles),
        new(5145, R.Marker, WhyMissiles, CitMissiles),
        new(8416, R.Marker, WhyMissiles, CitMissiles),
        new(8417, R.Marker, WhyMissiles, CitMissiles),
        new(10211, R.Marker, WhyMissiles, CitMissiles),
        new(10212, R.Marker, WhyMissiles, CitMissiles),
        new(10909, R.Marker, WhyPlayerDummy, CitMindSpells),
        new(10911, R.Marker, WhyPlayerDummy, CitMindSpells),
        new(10912, R.Marker, WhyPlayerDummy, CitMindSpells),
        new(11878, R.Marker, WhyMarker, NoCore),
        new(12681, R.Marker, WhyMarker, NoCore),
        new(12682, R.Marker, WhyMarker, NoCore),
        new(12689, R.Marker, WhyMarker, NoCore),
        new(12690, R.Marker, WhyMarker, NoCore),
        new(12728, R.Marker, WhyMarker, NoCore),
        new(15735, R.Marker, WhyMissiles, CitMissiles),
        new(15790, R.Marker, WhyMissiles, CitMissiles),
        new(16078, R.Marker, WhyMarker, NoCore),
        new(18282, R.Marker, WhyMarker, NoCore),
        new(18349, R.Marker, WhyMarker, NoCore),
        new(18380, R.Marker, WhyMarker, NoCore),
        new(18433, R.Marker, WhyMarker, NoCore),
        new(18936, R.Marker, WhyMarker, NoCore),
        new(20001, R.Marker, WhyMarker, NoCore),
        new(20546, R.Marker, WhyMarker, NoCore),
        new(20862, R.Marker, WhyMarker, NoCore),
        new(21338, R.Marker, WhyMarker, NoCore),
        new(21836, R.Marker, WhyMarker, NoCore),
        new(21865, R.Marker, WhyMarker, NoCore),
        new(21934, R.Marker, WhyMarker, NoCore),
        new(22272, R.Marker, WhyMissiles, CitMissiles),
        new(22838, R.Marker, WhyMarker, NoCore),
        new(22969, R.Marker, WhyMarker, NoCore),
        new(22970, R.Marker, WhyMarker, NoCore),
        new(23137, R.Marker, WhyMarker, NoCore),
        new(24172, R.Marker, WhyMarker, NoCore),
        new(25194, R.Marker, WhyMarker, NoCore),
        new(25345, R.Marker, WhyMissiles, CitMissiles),
        new(25800, R.Marker, WhyMarker, NoCore),
        new(25802, R.Marker, WhyMarker, NoCore),
        new(25803, R.Marker, WhyMarker, NoCore),
        new(25842, R.Marker, WhyMarker, NoCore),
        new(26104, R.Marker, WhyMarker, NoCore),
        new(26340, R.Marker, WhyMarker, NoCore),
        new(26447, R.Marker, WhyMarker, NoCore),
        new(26618, R.Marker, WhyMarker, NoCore),
        new(26838, R.Marker, WhyMarker, NoCore),
        new(28020, R.Marker, WhyMarker, NoCore),
        new(28082, R.Marker, WhyMarker, NoCore),
        new(28083, R.Marker, WhyMarker, NoCore),
        new(28116, R.Marker, WhyMarker, NoCore),
        new(28117, R.Marker, WhyMarker, NoCore),
        new(28316, R.Marker, WhyMarker, NoCore),
        new(28392, R.Marker, WhyMarker, NoCore),
        new(28446, R.Marker, WhyMarker, NoCore),
        new(28452, R.Marker, WhyMarker, NoCore),
        new(29509, R.Marker, WhyMarker, NoCore),
        new(29945, R.Marker, WhyMarker, NoCore),
        new(30097, R.Marker, WhyMarker, NoCore),
        new(30676, R.Marker, WhyMarker, NoCore),
        new(31563, R.Marker, WhyMarker, NoCore),
        // Visual (31)
        new(6844, R.Visual, WhyVisual, NoCore),
        new(7742, R.Visual, WhyVisual, NoCore),
        new(7923, R.Visual, WhyVisual, NoCore),
        new(8596, R.Visual, WhyVisual, NoCore),
        new(8609, R.Visual, WhyVisual, NoCore),
        new(10263, R.Visual, WhyVisual, NoCore),
        new(12564, R.Visual, WhyVisual, NoCore),
        new(15651, R.Visual, WhyVisual, NoCore),
        new(16003, R.Visual, WhyVisual, NoCore),
        new(16532, R.Visual, WhyVisual, NoCore),
        new(18970, R.Visual, WhyVisual, NoCore),
        new(19823, R.Visual, WhyVisual, NoCore),
        new(20567, R.Visual, WhyVisual, NoCore),
        new(20708, R.Visual, WhyVisual, NoCore),
        new(21389, R.Visual, WhyVisual, NoCore),
        new(21392, R.Visual, WhyVisual, NoCore),
        new(21649, R.Visual, WhyVisual, NoCore),
        new(22205, R.Visual, WhyVisual, NoCore),
        new(22848, R.Visual, WhyVisual, NoCore),
        new(24222, R.Visual, WhyVisual, NoCore),
        new(24258, R.Visual, WhyVisual, NoCore),
        new(24948, R.Visual, WhyVisual, NoCore),
        new(25996, R.Visual, WhyVisual, NoCore),
        new(26063, R.Visual, WhyVisual, NoCore),
        new(27575, R.Visual, WhyVisual, NoCore),
        new(28078, R.Visual, WhyVisual, NoCore),
        new(28087, R.Visual, WhyVisual, NoCore),
        new(28663, R.Visual, WhyVisual, NoCore),
        new(29172, R.Visual, WhyVisual, NoCore),
        new(29531, R.Visual, WhyVisual, NoCore),
        new(31726, R.Visual, WhyVisual, NoCore),
        // SpawnAura (9)
        new(6431, R.SpawnAura, WhySpawnAura, NoCore),
        new(7083, R.SpawnAura, WhySpawnAura, NoCore),
        new(7974, R.SpawnAura, WhySpawnAura, NoCore),
        new(12508, R.SpawnAura, WhySpawnAura, NoCore),
        new(14775, R.SpawnAura, WhySpawnAura, NoCore),
        new(16606, R.SpawnAura, WhySpawnAura, NoCore),
        new(26000, R.SpawnAura, WhySpawnAura, NoCore),
        new(28330, R.SpawnAura, WhySpawnAura, NoCore),
        new(31748, R.SpawnAura, WhySpawnAura, "cmangos World.cpp:607 applies it only through GM.InvisibleAura as a look; no core reads it"),
        // ScriptCarrier (121)
        new(2691, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script creature_ai_scripts:380301; no core handles the effect"),
        new(3644, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script quest_end_scripts:1240; no core handles the effect"),
        new(3659, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script quest_end_scripts:602; no core handles the effect"),
        new(3660, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script quest_end_scripts:591; no core handles the effect"),
        new(4093, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script quest_end_scripts:640; no core handles the effect"),
        new(4983, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script quest_end_scripts:748; no core handles the effect"),
        new(4985, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script quest_end_scripts:724; no core handles the effect"),
        new(5001, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script quest_start_scripts:778; no core handles the effect"),
        new(5002, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script quest_start_scripts:778; no core handles the effect"),
        new(5017, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script quest_end_scripts:827; no core handles the effect"),
        new(5026, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script quest_end_scripts:771; no core handles the effect"),
        new(6238, R.ScriptCarrier, WhyCarrier, "cast by a DB script (dbscripts_on_quest_end); no core handles the effect"),
        new(6245, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script quest_end_scripts:1220; no core handles the effect"),
        new(6270, R.ScriptCarrier, WhyCarrier, "cast at src/ArcaneCore.Game/Instances/Scripts/WailingCaverns/DiscipleOfNaralexAi.cs:165; vmangos casts at src/scripts/kalimdor/the_barrens/wailing_caverns/wailing_caverns.cpp:42; no core handles the effect"),
        new(6354, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script quest_end_scripts:1011; no core handles the effect"),
        new(6355, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script quest_end_scripts:1011; no core handles the effect"),
        new(6421, R.ScriptCarrier, WhyCarrier, "cast at src/ArcaneCore.Game/Instances/Scripts/ShadowfangKeep/ShadowfangPrisonerAi.cs:30; cast by vmangos DB script creature_movement_scripts:385012; no core handles the effect"),
        new(6422, R.ScriptCarrier, WhyCarrier, "cast at src/ArcaneCore.Game/Instances/Scripts/ShadowfangKeep/ShadowfangPrisonerAi.cs:70; vmangos casts at src/scripts/eastern_kingdoms/burning_steppes/blackrock_depths/blackrock_depths.cpp:75; cast by vmangos DB script creature_movement_scripts:385012; no core handles the effect"),
        new(6584, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script quest_end_scripts:1058; no core handles the effect"),
        new(6657, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script quest_start_scripts:1064; no core handles the effect"),
        new(6668, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script quest_start_scripts:2381; no core handles the effect"),
        new(6741, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script quest_end_scripts:1169; no core handles the effect"),
        new(6767, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script gossip_scripts:73; no core handles the effect"),
        new(6866, R.ScriptCarrier, WhyCarrier, "cast by a DB script (creature_template_spells); no core handles the effect"),
        new(6870, R.ScriptCarrier, WhyCarrier, "cast by a DB script (creature_template_spells); no core handles the effect"),
        new(6903, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script quest_end_scripts:1116; no core handles the effect"),
        new(7078, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script generic_scripts:132402; no core handles the effect; vmangos spell_effect_mod only retargets it"),
        new(7081, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script generic_scripts:132403; no core handles the effect"),
        new(7437, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script quest_end_scripts:1445; no core handles the effect"),
        new(7791, R.ScriptCarrier, WhyCarrier, "vmangos casts at src/scripts/world/npc_j_eevee.cpp:21; cast by vmangos DB script generic_scripts:57020; no core handles the effect"),
        new(7794, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script generic_scripts:573402; no core handles the effect"),
        new(8268, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script creature_ai_scripts:451201; no core handles the effect"),
        new(8675, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script event_scripts:1428; no core handles the effect"),
        new(8734, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script creature_ai_scripts:480905; no core handles the effect"),
        new(8912, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script quest_start_scripts:1806; no core handles the effect"),
        new(8996, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script creature_ai_scripts:252304; no core handles the effect"),
        new(9008, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script creature_ai_scripts:566901; no core handles the effect"),
        new(9744, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script quest_end_scripts:5804; no core handles the effect"),
        new(9795, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script quest_end_scripts:2204; no core handles the effect"),
        new(10387, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script generic_scripts:25210; no core handles the effect"),
        new(10666, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script creature_ai_scripts:707604; no core handles the effect"),
        new(10747, R.ScriptCarrier, WhyCarrier, "cast by a DB script (creature_ai_scripts); no core handles the effect"),
        new(11540, R.ScriptCarrier, WhyCarrier, "cast at src/ArcaneCore.Game/Creatures/Scripts/SquireRoweAI.cs:16; no core handles the effect"),
        new(11542, R.ScriptCarrier, WhyCarrier, "cast at src/ArcaneCore.Game/Instances/Scripts/Gnomeregan/EmiShortfuseAi.cs:218; vmangos casts at src/scripts/eastern_kingdoms/dun_morogh/gnomeregan/gnomeregan.cpp:70; no core handles the effect"),
        new(12021, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script creature_spells:52610; no core handles the effect"),
        new(12218, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script creature_movement_scripts:828474; no core handles the effect"),
        new(12244, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script generic_scripts:336702; no core handles the effect"),
        new(12380, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script creature_ai_scripts:278301; no core handles the effect"),
        new(12511, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script quest_start_scripts:3453; no core handles the effect"),
        new(12512, R.ScriptCarrier, WhyCarrier, "cast by a DB script (dbscripts_on_gossip); no core handles the effect"),
        new(12535, R.ScriptCarrier, WhyCarrier, "cast by a DB script (creature_ai_scripts); no core handles the effect"),
        new(12655, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script quest_start_scripts:3508; no core handles the effect"),
        new(12816, R.ScriptCarrier, WhyCarrier, "cast at src/ArcaneCore.Game/Instances/Scripts/RazorfenDowns/BelnistraszAi.cs:135; vmangos casts at src/scripts/eastern_kingdoms/stranglethorn_vale/zulgurub/instance_zulgurub.cpp:458; no core handles the effect"),
        new(13727, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script quest_end_scripts:902; no core handles the effect"),
        new(14867, R.ScriptCarrier, WhyCarrier, "cast at src/ArcaneCore.World/Talents/TalentFeature.cs:40; vmangos casts at src/game/Handlers/SkillHandler.cpp:56; no core handles the effect"),
        new(15050, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script quest_end_scripts:4129; no core handles the effect"),
        new(15341, R.ScriptCarrier, WhyCarrier, "cast at src/ArcaneCore.Game/Instances/Scripts/BlackrockDepths/BlackrockDepthsInstance.Events.cs:434; cast by a DB script (creature_template_spells); no core handles the effect"),
        new(15533, R.ScriptCarrier, WhyCarrier, "cast at src/ArcaneCore.Game/Instances/Scripts/BlackrockDepths/IronhandGuardianAI.cs:20; cast by vmangos DB script creature_ai_scripts:898202; no core handles the effect"),
        new(15690, R.ScriptCarrier, WhyCarrier, "cast at src/ArcaneCore.Game/Instances/Scripts/Stratholme/StratholmeBossAis.cs:250; no core handles the effect"),
        new(15737, R.ScriptCarrier, WhyCarrier, "cast at src/ArcaneCore.Game/Instances/Scripts/BlackrockDepths/GrimstoneAI.cs:120; no core handles the effect"),
        new(15739, R.ScriptCarrier, WhyCarrier, "cast at src/ArcaneCore.Game/Instances/Scripts/BlackrockDepths/GrimstoneAI.cs:121; no core handles the effect"),
        new(15740, R.ScriptCarrier, WhyCarrier, "cast at src/ArcaneCore.Game/Instances/Scripts/BlackrockDepths/GrimstoneAI.cs:145; no core handles the effect"),
        new(15741, R.ScriptCarrier, WhyCarrier, "cast at src/ArcaneCore.Game/Instances/Scripts/BlackrockDepths/GrimstoneAI.cs:146; no core handles the effect"),
        new(15742, R.ScriptCarrier, WhyCarrier, "cast at src/ArcaneCore.Game/Instances/Scripts/BlackrockDepths/GrimstoneAI.cs:106; no core handles the effect"),
        new(16037, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script creature_ai_scripts:1029901; no core handles the effect"),
        new(16052, R.ScriptCarrier, WhyCarrier, "cast at src/ArcaneCore.Game/Instances/Scripts/BlackrockSpire/BlackrockSpireBossAis.cs:117; no core handles the effect"),
        new(16074, R.ScriptCarrier, WhyCarrier, "cast by a DB script (creature_ai_scripts); no core handles the effect"),
        new(16572, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script generic_scripts:5541; no core handles the effect"),
        new(16633, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script quest_end_scripts:1027; no core handles the effect"),
        new(16767, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script generic_scripts:5541; no core handles the effect"),
        new(16768, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script generic_scripts:5541; no core handles the effect"),
        new(16772, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script generic_scripts:5541; no core handles the effect"),
        new(16775, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script generic_scripts:5541; no core handles the effect"),
        new(16776, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script generic_scripts:5541; no core handles the effect"),
        new(16777, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script generic_scripts:5541; no core handles the effect"),
        new(16778, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script generic_scripts:5541; no core handles the effect"),
        new(16779, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script generic_scripts:5541; no core handles the effect"),
        new(16780, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script generic_scripts:5541; no core handles the effect"),
        new(17117, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script quest_end_scripts:5062; no core handles the effect"),
        new(17285, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script quest_end_scripts:5154; no core handles the effect"),
        new(17370, R.ScriptCarrier, WhyCarrier, "cast at src/ArcaneCore.Game/Creatures/Scripts/RestlessSoulAI.cs:15; no core handles the effect"),
        new(17673, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script quest_end_scripts:502; no core handles the effect"),
        new(18951, R.ScriptCarrier, WhyCarrier, "cast at src/ArcaneCore.Game/Creatures/Scripts/KalimdorMiscAI.cs:15; vmangos casts at src/scripts/kalimdor/desolace/desolace.cpp:310; cast by a DB script (creature_ai_scripts); no core handles the effect"),
        new(18994, R.ScriptCarrier, WhyCarrier, "cast at src/ArcaneCore.Game/Creatures/Scripts/RanshallaAI.cs:18; cast by vmangos DB script creature_movement_scripts:1030061; no core handles the effect"),
        new(19645, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script creature_spells:104640; no core handles the effect"),
        new(19774, R.ScriptCarrier, WhyCarrier, "cast at src/ArcaneCore.Game/Instances/Scripts/MoltenCore/MajordomoAI.cs:148; vmangos casts at src/scripts/eastern_kingdoms/burning_steppes/molten_core/boss_majordomo_executus.cpp:54; no core handles the effect"),
        new(20041, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script quest_end_scripts:6301; no core handles the effect"),
        new(20436, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script generic_scripts:443001; no core handles the effect"),
        new(20466, R.ScriptCarrier, WhyCarrier, "cast at src/ArcaneCore.Game/Creatures/Scripts/ReginaldWindsorAI.cs:25; no core handles the effect"),
        new(20568, R.ScriptCarrier, WhyCarrier, "cast at src/ArcaneCore.Game/Instances/Scripts/MoltenCore/RagnarosAI.cs:119; no core handles the effect"),
        new(21157, R.ScriptCarrier, WhyCarrier, "vmangos casts at src/scripts/kalimdor/feralas/dire_maul/instance_dire_maul.cpp:1665; cast by vmangos DB script creature_movement_scripts:1179001; no core handles the effect"),
        new(21227, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script creature_movement_scripts:13485402; no core handles the effect"),
        new(21336, R.ScriptCarrier, WhyCarrier, "cast by a DB script (creature_ai_scripts); no core handles the effect"),
        new(22313, R.ScriptCarrier, "only a vmangos DB script casts it; classic-db has no such row", "cast by vmangos DB script creature_movement_scripts:401 (Blackwing Spellbinder); no core handles the effect"),
        new(23017, R.ScriptCarrier, WhyCarrier, "cast at src/ArcaneCore.Game/Creatures/Scripts/NightmareManifestsAI.cs:402; vmangos casts at src/scripts/kalimdor/moonglade/moonglade.cpp:957; cast by vmangos DB script creature_ai_scripts:1146601; no core handles the effect"),
        new(23053, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script generic_scripts:14469; no core handles the effect; vmangos spell_effect_mod only retargets it"),
        new(23140, R.ScriptCarrier, WhyCarrier, "vmangos casts at src/scripts/world/npc_j_eevee.cpp:20; cast by a DB script (dbscripts_on_creature_movement); no core handles the effect"),
        new(23488, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script creature_movement_scripts:1473702; no core handles the effect"),
        new(23949, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script generic_scripts:1491001; no core handles the effect"),
        new(23950, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script generic_scripts:1491001; no core handles the effect"),
        new(23969, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script creature_ai_scripts:1494302; no core handles the effect"),
        new(24085, R.ScriptCarrier, WhyCarrier, "cast at src/ArcaneCore.Game/Battlegrounds/AlteracValleyScripts/AvEventAI.cs:36; vmangos casts at src/scripts/battlegrounds/battleground_alterac.cpp:1329; cast by vmangos DB script quest_end_scripts:489; no core handles the effect"),
        new(24203, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script quest_end_scripts:8183; no core handles the effect"),
        new(24204, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script quest_end_scripts:8183; no core handles the effect"),
        new(24207, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script quest_end_scripts:8183; no core handles the effect"),
        new(24214, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script quest_end_scripts:8183; no core handles the effect"),
        new(24217, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script generic_scripts:1508001; no core handles the effect"),
        new(24997, R.ScriptCarrier, WhyCarrier, "cast by a DB script (creature_ai_scripts); no core handles the effect"),
        new(25004, R.ScriptCarrier, WhyCarrier, "vmangos casts at src/scripts/kalimdor/moonglade/moonglade.cpp:39; cast by a DB script (dbscripts_on_quest_start); no core handles the effect"),
        new(25100, R.ScriptCarrier, WhyCarrier, "vmangos casts at src/game/Spells/SpellAuras.cpp:5724; cast by vmangos DB script quest_end_scripts:8425; no core handles the effect"),
        new(25182, R.ScriptCarrier, WhyCarrier, "cast at src/ArcaneCore.Game/Creatures/Scripts/AnachronosTheAncientAI.cs:78; vmangos casts at src/scripts/kalimdor/silithus/silithus.cpp:1152; no core handles the effect"),
        new(25824, R.ScriptCarrier, WhyCarrier, "cast by a DB script (creature_ai_scripts); no core handles the effect"),
        new(25952, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script gossip_scripts:6763; no core handles the effect"),
        new(26271, R.ScriptCarrier, WhyCarrier, "cast by a DB script (creature_ai_scripts); no core handles the effect"),
        new(26638, R.ScriptCarrier, WhyCarrier, "cast at src/ArcaneCore.Game/Instances/Scripts/TempleOfAhnQiraj/TwinEmperorAIs.cs:89; cast by vmangos DB script quest_end_scripts:1482; no core handles the effect"),
        new(26639, R.ScriptCarrier, WhyCarrier, "cast at src/ArcaneCore.Game/Spells/Generic/RaidDummyScripts.cs:99; vmangos casts at src/game/Spells/SpellEffects.cpp:4001; no core handles the effect"),
        new(26646, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script creature_ai_scripts:1596401; no core handles the effect"),
        new(27825, R.ScriptCarrier, WhyCarrier, "cast by vmangos DB script creature_spells:161250; no core handles the effect"),
        new(27826, R.ScriptCarrier, WhyCarrier, "cast by a DB script (creature_ai_scripts); no core handles the effect"),
        new(29403, R.ScriptCarrier, WhyCarrier, "cast by a DB script (creature_ai_scripts); no core handles the effect"),
        new(30101, R.ScriptCarrier, WhyCarrier, "cast at src/ArcaneCore.Game/Instances/Scripts/Naxxramas/FrostwyrmBossAIs.cs:97; vmangos casts at src/scripts/eastern_kingdoms/eastern_plaguelands/naxxramas/boss_sapphiron.cpp:35; no core handles the effect"),
    ];
}
