using R = ArcaneCore.Game.Spells.InertSpellReason;

namespace ArcaneCore.Game.Spells;

/// <summary>The deliberately inert spells of Blackrock raids and Upper Blackrock Spire (Molten Core, Blackwing Lair): content lanes add their entries here, never in the shared files.</summary>
public static partial class DeliberatelyInertSpells
{
    private const string WhyPresummon = "Blackhand Summoner Presummon Emote: the summoner's EventAI casts it on itself right before each reinforcement summon; its DUMMY effect has no handler in any core";
    private const string CitPresummon = "vmangos migration 20231222035911 creature_ai_scripts 981803 (16134) and 981804 (16135) cast it before Summon Blackhand Veteran/Dreadweaver; no core handles the effect";

    private static InertSpell[] BuildBlackrockRaids() =>
    [
        new(16404, R.Visual, "Banishment of Scale: in the UBRS Seal of Ascension event Lord Victor Nefarius casts it at Vaelastrasz the Red (channel beam); Vaelastrasz's EventAI reacts to being hit by it (passive, update entry, cast Vaelastrasz Spawn, despawn); the aura itself has no reader",
            "vmangos migration 20230924151636_world.sql: event_scripts 4623 (line 80) Lord Victor Nefarius casts 16404; spell_script_target (16404,1,10538) line 96; creature_ai_events 1034001 on creature 10340 (line 23, hit by spell 16404) runs script 4625; no core handles the aura; also vmangos boss_vaelastrasz.cpp:53/:239 uses it as the channel spell"),
        new(16354, R.Marker, "Vaelastrasz Spawn: a self-cast by Vaelastrasz the Red in the UBRS Seal of Ascension event right before it despawns (Call of Vaelastrasz script and the Banishment of Scale EventAI reaction); its effect has no handler in any core",
            "vmangos migration 20230924151636_world.sql: generic_scripts 4624 (line 62, 'Call of Vaelastrasz OOC: Vaelastrasz the Red - Cast Spell Vaelstrasz Spawn') and generic_scripts 4625 (line 91, run from creature_ai_events 1034001 on creature 10340) cast 16354; no core handles the effect"),
        new(16047, R.ScriptCarrier, "Emberseer Full Strength: Pyroguard Emberseer's AI casts it on himself when the twentieth growing stack lands; the DUMMY effect has no handler in any core",
            "mangos-classic boss_pyroguard_emberseer.cpp:44 SPELL_FULL_STRENGHT, cast in the growing handler; no core handles the effect"),
        new(15281, R.ScriptCarrier, "Encage Emberseer: the Blackhand Incarcerators cast it on Pyroguard Emberseer for the cage channel; the DUMMY aura has no reader",
            "mangos-classic boss_pyroguard_emberseer.cpp:39 SPELL_ENCAGE_EMBERSEER ('cast by Blackhand Incarcerator'), cast at :152; no core handles the aura"),
        new(15282, R.NeverCast, "Encaged Emberseer: a self aura of azerothcore's Emberseer script; the vmangos and mangos-classic Emberseer scripts never cast it",
            "no vmangos, mangos-classic or mangoszero source or world DB script casts it; azerothcore boss_pyroguard_emberseer.cpp:35 SPELL_ENCAGED_EMBERSEER ('Self on spawn')"),
        new(16134, R.ScriptCarrier, WhyPresummon, CitPresummon),
        new(16135, R.ScriptCarrier, WhyPresummon, CitPresummon),
    ];
}
