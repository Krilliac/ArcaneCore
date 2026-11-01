using R = ArcaneCore.Game.Spells.InertSpellReason;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// The deliberately inert spells of dungeons and dungeon-area quest events: content lanes add their entries here, never in the shared files.
/// Checked 2026-10-31 against vmangos (source and world DB), mangos-classic (cmangos) and classic-db. The spells of the lane that DO act are
/// scripts (<c>BanishExileScript</c> 4130-4132, <c>UnsummonMechanicalYetiScript</c> 17163) or are named consumers in <c>QaSpellConsumers</c>.
/// </summary>
public static partial class DeliberatelyInertSpells
{
    private const string WhyQuestCast = "cast by a quest-end DB script for the casting look only; the DUMMY effect has no handler in any reference core";
    private const string WhyDbCast = "cast by a creature, game object or DB script for its visual or marker; the DUMMY effect has no handler in any reference core";
    private const string WhyChannel = "channel visual aura: only the client renders it, no reference core reads the dummy aura";
    private const string CitNeverCast = "no spell reference in vmangos source or world DB scripts, classic-db Full_DB scripts, items, game objects or creature addons (2026-10-31)";

    private static InertSpell[] BuildDungeons() =>
    [
        new(10347, R.ScriptCarrier, "Archaedas Awaken Visual: only cast by Archaedas for the look", "vmangos uldaman.h:62 (constant only); mangos-classic boss_archaedas.cpp:31 casts it as a visual"),
        new(14928, R.ScriptCarrier, "Nagmara's Love Potion: Nagmara casts it on herself for the look, then Nagmara and Rocknot kiss", "vmangos blackrock_depths.cpp:704 and :792, mangos-classic blackrock_depths.cpp:696 and :792: DoCast only, nothing reads the dummy"),
        new(16051, R.ScriptCarrier, "Barrier of Light: cast by Vaelan's gossip script for its look", "vmangos world DB gossip_scripts 10296 (CAST_SPELL 16051); no C++ reads it (blackrock_depths.cpp:220 is a creature entry)"),
        new(8655, R.NeverCast, "Riverpaw Death: listed by cmangos with no creature to react (target entry 0) and cast by nothing", "mangos-classic spell_scripts.cpp:232-258 TribalDeath (entry 0 for 8655); " + CitNeverCast),
        new(15782, R.NeverCast, "Frostsaber Cub Death: only the cmangos TribalDeath script names it; nothing casts it", "mangos-classic spell_scripts.cpp:237-262; " + CitNeverCast),
        new(9156, R.ScriptCarrier, WhyQuestCast, "vmangos world DB quest_end_scripts 1957 (Tabetha casts Create Mage's Orb); no C++ reader"),
        new(9157, R.ScriptCarrier, WhyQuestCast, "vmangos world DB quest_end_scripts 1921, 1940, 1961 (Create Mage's Robe); no C++ reader"),
        new(9200, R.ScriptCarrier, WhyQuestCast, "vmangos world DB quest_end_scripts 1525 (Telf Joolam casts Create Sapta); no C++ reader"),
        new(20528, R.ScriptCarrier, WhyQuestCast, "vmangos world DB quest_end_scripts 6421 (Mor'rogal Enchant); no C++ reader"),
        new(9457, R.ScriptCarrier, "Tharnariun's Heal: the spell of the Tharnarun's Cure game object (177187)", "vmangos world DB gameobject_template 177187 data3 = 9457; no C++ reader (9457 in RegtharDeathgateAI is a creature entry)"),
        new(8995, R.ScriptCarrier, WhyDbCast, "vmangos world DB creature_ai_scripts 252303 (Dark Iron Rifleman casts Shoot); no C++ reader"),
        new(8997, R.ScriptCarrier, WhyDbCast, "vmangos world DB creature_ai_scripts 252305 (Dark Iron Rifleman 3 casts Shoot); no C++ reader"),
        new(12198, R.ScriptCarrier, "Marksman Hit: the Dark Iron Marksman's shot at Dorius, cast by a relay script", "vmangos world DB generic_scripts 336702 and classic-db dbscripts_on_relay (CAST_SPELL 12198); mangos-classic searing_gorge.cpp:91 only ends its escort on the hit"),
        new(13236, R.Visual, WhyChannel, "vmangos world DB creature_ai_scripts 367102 and 1188201 and quest_end_scripts 985 cast it; no C++ reader"),
        new(13540, R.Visual, WhyChannel, "vmangos boss_jeklik.cpp:35 and :62 cast it for the idle look; mangos-classic boss_jeklik.cpp:57 and :113"),
        new(13619, R.Marker, "Wracking Pains: the dummy aura of the Wracking Pains Proc 13616 and 23771 trigger and of a creature spell list", "vmangos world DB creature_spells 145190; Spell.dbc trigger links 13616 and 23771; no C++ reader"),
        new(13280, R.Marker, "Gnomish Death Ray: dummy self-cast triggered by the Gnomish Death Ray channel 13278", "Spell.dbc 13278 trigger link; no C++ reader in vmangos or mangos-classic"),
        new(18113, R.ScriptCarrier, WhyDbCast, "vmangos world DB creature_movement_scripts 1317201 (Manifestation of Water casts it); no C++ reader"),
        new(18793, R.NeverCast, "Kodo Kombobulator (script effect): a spell only in the classic DBC, never cast", "mangos-classic desolace.cpp:58 (constant only); " + CitNeverCast),
        new(19473, R.ScriptCarrier, WhyDbCast, "vmangos world DB gameobject_template 177705 (Naga Beam) data3 = 19473 and gameobject_scripts 12609; no C++ reader"),
        new(24593, R.NeverCast, "Pagle's Point Cast - Create Splash and Quake: fishing event spell nothing casts", CitNeverCast),
        new(6924, R.ScriptCarrier, WhyDbCast, "vmangos world DB generic_scripts 619001 (Spitelash Warrior casts Remove Polymorph); no C++ reader"),
        new(8345, R.Marker, "Control Machine: the real effect is its MOD_POSSESS aura; the second effect is a dummy aura nothing reads", "vmangos SpellEffects.cpp:530 and mangos-classic SpellEffects.cpp:393-398 only cast it from the Universal Remote"),
        new(15713, R.ScriptCarrier, "Boomerang: cast back at the caster by the hit of Linken's Boomerang 15712 for the look", "mangos-classic item_scripts.cpp:412-421 casts it; nothing handles its dummy"),
    ];
}
