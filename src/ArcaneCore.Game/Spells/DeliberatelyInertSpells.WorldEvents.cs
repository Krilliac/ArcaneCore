using R = ArcaneCore.Game.Spells.InertSpellReason;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// The deliberately inert spells of world events: content lanes add their entries here, never in the shared files. Checked 2026-10-31 against the
/// vmangos and mangos-classic (cmangos) source trees, mangoszero and classic-db (ACID and Updates): every id below is either only cast or applied as a
/// marker or visual, or (the NeverCast entries) nothing in those trees or the vmangos migrations and classic-db Full_DB reaches it. The spells of the lane that DO have a handler are scripts:
/// Lunar Teleport Caps 26406, 26408-26410, 26412 and 26414 (<c>LunarTeleportCapScript</c>) and Muglash's Brazier Trap 20863
/// (<c>MuglashBrazierTrapScript</c>). Celebras' spells 21914 and 21917 are not listed: classic-db Full_DB dbscripts_on_spell has rows for both, which run through
/// <c>SpellSystem.StartDbSpellScript</c> (no code script claims them). 24746 is not listed either: vmangos conditions 887 and 888 test for its aura, so something applies it.
/// </summary>
public static partial class DeliberatelyInertSpells
{
    private const string WhyWorldCarrier = "cast or applied by a creature AI or script for its visual or marker; the spell's own effect has no handler in any reference core";
    private const string WhyNoHandler = "no reference core handles or casts it, so its DUMMY effect has nothing to run";
    private const string WhyHitTrigger = "only read as the SPELLHIT trigger of an EventAI row; the spell's own effect has no handler in any reference core";
    private const string WhyItemCast = "cast by an item for its look or marker; the spell's own effect has no handler in any reference core";
    private const string CitNeverCastScan = "no spell reference in vmangos sql/migrations (every statement outside locale, spawn and loot tables) or classic-db Full_DB (creature_ai_scripts, creature_template_addon, dbscripts_on_*, item_template spellid_1-5, quest_template), nor in the vmangos, mangos-classic and mangoszero source (2026-10-31); the bare number only appears as an item, creature or game object id";

    private static InertSpell[] BuildWorldEvents() =>
    [
        new(26005, R.Marker, "Mistletoe marker aura: only cast by Mistletoe 26004, nothing reads it", "vmangos SpellEffects.cpp:4016-4023 casts 26005 on the target and nothing else references it"),
        new(24755, R.Marker, "Tricked or Treated marker aura: only cast by Trick or Treat 24751", "vmangos SpellEffects.cpp:3976-3988 casts 24755; its only reader is the cmangos ScriptDev2 innkeeper gossip (npcs_special.cpp:807), which is not a spell handler"),
        new(23770, R.Marker, "Sayge's Dark Fortune of *: the effect is skipped, only a common cooldown was ever imagined", "mangos-classic SpellEffects.cpp:1552-1554 and mangoszero SpellEffectDamageTeleport.cpp:407-409: just skip ('not exist, common cooldown can be implemented in scripts if need'); vmangos has no case"),
        new(23356, R.Marker, "Taming Lesson: only exempted from the target creature type mask, its DUMMY has no handler", "vmangos Spell.cpp:7595-7596 (mask ignored for 2641 and 23356); no SpellEffects case; ArcaneCore SpellSystem.TargetCreatureType.cs TamingLessonId"),
        new(26636, R.ScriptCarrier, "Elune's Candle firework: only cast by Elune's Candle 26374", "vmangos spell_item.cpp:553-574 (ElunesCandleScript) casts 26636; nothing handles it"),
        new(24999, R.ScriptCarrier, WhyWorldCarrier, "vmangos moonglade.cpp:268 AddAura(24999) on the summoned Malfurion"),
        new(17327, R.ScriptCarrier, WhyWorldCarrier, "vmangos OutdoorPvPEP.h:72 and desolace.cpp:309 (SPELL_BLUE_AURA, aura on the Magrami Spectre); mangos-classic spell_scripts.cpp:47; nothing handles the DUMMY"),
        new(31309, R.ScriptCarrier, WhyWorldCarrier, "vmangos OutdoorPvPEP.h:73 (SPELL_SPIRIT_PARTICLES_RED_BIG, Eastern Plaguelands tower visual); mangos-classic OutdoorPvPEP.h:61"),
        new(31951, R.ScriptCarrier, WhyWorldCarrier, "vmangos OutdoorPvPEP.h:75 (SPELL_SPIRIT_PARTICLES_RED_SUPER_BIG_DND, Eastern Plaguelands tower visual)"),
        new(31954, R.ScriptCarrier, WhyWorldCarrier, "vmangos OutdoorPvPEP.h:74 (SPELL_SPIRIT_PARTICLES_SUPER_BIG_DND, Eastern Plaguelands tower visual)"),
        new(20358, R.ScriptCarrier, WhyWorldCarrier, "vmangos quest_stormwind_rendezvous.cpp:595 (Windsor reads the tablets, cast at Onyxia)"),
        new(20465, R.ScriptCarrier, WhyWorldCarrier, "vmangos quest_stormwind_rendezvous.cpp:655 (Onyxia casts Windsor Death on Windsor)"),
        new(21896, R.ScriptCarrier, WhyWorldCarrier, "classic-db ACID/acid_classic.sql creature_ai_scripts row 1223801 (Zaetar's Spirit casts it on spawn); no core handles the spell"),
        new(27766, R.ScriptCarrier, WhyWorldCarrier, "vmangos spell_special.cpp:68 (Darkmoon Steam Tonk Cannon 24933 casts it on the target)"),
        new(27771, R.ScriptCarrier, WhyWorldCarrier, "vmangos spell_special.cpp:452 (SPELL_DAMAGED_TONK cast on the target of the Tonk script)"),
        new(7077, R.ScriptCarrier, WhyWorldCarrier, "vmangos dustwallow_marsh.cpp:47 and :153 (Tervosh teleport visual on his return)"),
        new(7141, R.ScriptCarrier, WhyWorldCarrier, "vmangos dustwallow_marsh.cpp:46 and :112 (Tervosh teleport visual on his arrival)"),
        new(20370, R.ScriptCarrier, WhyWorldCarrier, "vmangos PlayerBotMgr.cpp:1953-1966 (SPELL_RED_GLOW cast on a bot waypoint marker)"),
        new(7104, R.SpawnAura, WhySpawnAura, "classic-db Updates/4714_TDB-0542_quest_the_attack.sql creature_template_addon auras row for creature entry 1755; no core handles the spell"),
        new(6602, R.ScriptCarrier, WhyWorldCarrier, "vmangos sql/migrations/20220107235502_world.sql:37 (creature_ai_scripts 425202 casts 6602 for the Mirage Race) and classic-db Full_DB creature_template_addon auras '6602' for entry 4252"),
        new(7143, R.ScriptCarrier, WhyWorldCarrier, "vmangos sql/migrations/20240206152221_world.sql:104 (generic_scripts 132402 'Lady Jaina Proudmoore - Cast Spell Simple Teleport Group')"),
        new(7939, R.SpawnAura, WhySpawnAura, "vmangos sql/migrations/20210717172913_world.sql:266 (creature_template auras '7939 22766' for entry 11730), 20210712143257_world.sql:71 (creature_ai_scripts 377001 casts it) and classic-db Full_DB creature_ai_scripts 'Syndicate Spy - Cast Sneak OOC' plus creature_template_addon aura rows"),
        new(11013, R.ScriptCarrier, WhyWorldCarrier, "vmangos sql/migrations/20230214215859_world.sql:95 (creature_movement_scripts 777930 step 6 'Tyrion's Spybot - Cast Spell Sneak')"),
        new(11067, R.ScriptCarrier, WhyWorldCarrier, "vmangos sql/migrations/20230214215859_world.sql:93 (creature_movement_scripts 777930 step 2 'Tyrion's Spybot - Cast Spell Perm. Illusion Tyrion')"),
        new(11048, R.ScriptCarrier, WhyWorldCarrier, "vmangos sql/migrations/20230214215859_world.sql:24 (quest_start_scripts 434 step 7 'Tyrion's Spybot - Add Aura Perm. Illusion Bishop Tyriona')"),
        new(21795, R.Marker, WhyHitTrigger, "vmangos sql/migrations/20220801022620_world.sql:72 (creature_ai_events 1098101, event type 8 SPELLHIT on 21795: 'Frostwolf - Follow Player on Hit By Muzzle Effect Spell')"),
        new(21867, R.Marker, WhyHitTrigger, "vmangos sql/migrations/20220801022620_world.sql:39 (creature_ai_events 1099001, event type 8 SPELLHIT on 21867: 'Alterac Ram - Follow Player on Hit By Collar Effect Spell')"),
        new(24748, R.Marker, WhyItemCast, "vmangos sql/migrations/20220422044303_world.sql:204 (item_template 20421 spellid_1 24748, spelltrigger 1) and classic-db Full_DB item_template 20422 spellid_1"),
        new(24782, R.Marker, WhyItemCast, "classic-db Full_DB item_template 20451 'Twilight Cultist Ring of Lordship' spellid_1 24782"),
        new(26924, R.ScriptCarrier, WhyWorldCarrier, "classic-db Full_DB dbscripts_on_gossip 284901, 1286101 and 1286201 (command 15 CAST_SPELL 26924 on the player, 'Cast valentine Undercity Guardian on player'); vmangos 20230308021711_world.sql:3397 carries the same row commented out ('UNKNOWN - Cast Valentine on Player'); no core handles the spell's SCRIPT_EFFECT"),
        new(21830, R.NeverCast, WhyNoHandler, CitNeverCastScan),
        new(22518, R.NeverCast, WhyNoHandler, CitNeverCastScan),
        new(22581, R.NeverCast, WhyNoHandler, CitNeverCastScan),
        new(22805, R.NeverCast, WhyNoHandler, CitNeverCastScan),
        new(24803, R.NeverCast, WhyNoHandler, CitNeverCastScan),
    ];
}
