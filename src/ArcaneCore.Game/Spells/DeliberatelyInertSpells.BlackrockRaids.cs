using R = ArcaneCore.Game.Spells.InertSpellReason;

namespace ArcaneCore.Game.Spells;

/// <summary>The deliberately inert spells of Blackrock raids and Upper Blackrock Spire (Molten Core, Blackwing Lair): content lanes add their entries here, never in the shared files.</summary>
public static partial class DeliberatelyInertSpells
{
    private const string WhyPresummon = "Blackhand Summoner Presummon Emote: the summoner's EventAI casts it on itself right before each reinforcement summon; its DUMMY effect has no handler in any core";
    private const string CitPresummon = "vmangos migration 20231222035911 creature_ai_scripts 981803 (16134) and 981804 (16135) cast it before Summon Blackhand Veteran/Dreadweaver; no core handles the effect";

    private static InertSpell[] BuildBlackrockRaids() =>
    [
        new(16404, R.Visual, "Banishment of Scale: only the channel visual of Lord Victor Nefarius's beam at Vaelastrasz; the aura is never applied for any effect",
            "vmangos boss_vaelastrasz.cpp:53 SPELL_BANISHEMENT_OF_SCALE, used at :239 only as SetUInt32Value(UNIT_CHANNEL_SPELL) on the summoned Nefarius; no core handles the aura"),
        new(16354, R.NeverCast, "Vaelastrasz Spawn: a self-cast despawn marker of azerothcore's Vaelastrasz and Nefarian scripts; ArcaneCore's Vaelastrasz intro and Nefarian use no such cast",
            "no vmangos, mangos-classic or mangoszero source or world DB script casts it; azerothcore boss_nefarian.cpp:135 and instance_blackrock_spire.cpp:825 name it"),
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
