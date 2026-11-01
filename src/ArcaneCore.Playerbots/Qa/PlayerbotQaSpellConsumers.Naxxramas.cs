using K = ArcaneCore.World.Playerbots.Qa.QaSpellConsumerKind;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>The spell consumers of Naxxramas: content lanes add their entries here, never in the shared files.</summary>
internal static partial class QaSpellConsumers
{
    private static QaSpellConsumer[] BuildNaxxramas() =>
    [
        new(28307, K.UsePath, "ArcaneCore.Game/Instances/Scripts/Naxxramas/ConstructBossAIs.cs", "return Cast(28308, target ?? tank);",
            "Hateful Strike Primer: Patchwerk's AI picks the target itself (the highest-health player among the first four in melee reach other than the tank) and casts Hateful Strike 28308 at it; vmangos boss_patchwerk.cpp DoHatefulStrike never casts the primer, mangos-classic uses it only as that target filter"),
        new(28526, K.UsePath, "ArcaneCore.Game/Instances/Scripts/Naxxramas/FrostwyrmBossAIs.cs", "Cast(28522, target)",
            "Icebolt (init): Sapphiron's air phase casts Icebolt 28522 and its immunity 31800 at up to five random players itself, the work mangos-classic boss_sapphiron.cpp PeriodicIceBolt does from 28526; vmangos never casts 28526"),
        new(28617, K.UsePath, "ArcaneCore.Game/Instances/Scripts/Naxxramas/NaxxramasBossAI.cs", "private bool WebWrap()",
            "Web Wrap (init): Maexxna's AI wraps up to three random non-tank players directly (vmangos boss_maexxna.cpp DoCastWebWrap); the init SCRIPT_EFFECT is only SPELL_WEB_WRAP_UNKNOWN there and the cmangos Invisible Man sync is not used"),
        new(28797, K.UsePath, "ArcaneCore.Game/Instances/Scripts/Naxxramas/NaxxramasBossAI.cs", "spell.Id != 28732",
            "Widow's Embrace cooldown: Faerlina's AI handles the 28732 hit itself (enrage removed, embrace timers restarted; vmangos boss_faerlinaAI::SpellHit); mangos-classic boss_faerlina.cpp:48 casts 28797 on herself to carry that cooldown, vmangos never does"),
        new(29422, K.UsePath, "ArcaneCore.Game/Instances/Scripts/Naxxramas/FrostwyrmBossAIs.cs", "Cast(29423, Me, triggered: true)",
            "Kel'Thuzad Channel Effect: the periodic trigger of Kel'Thuzad's channel 29423; his AI runs the add timeline itself (the soldier, soul weaver and abomination spawns), the work mangos-classic boss_kelthuzad.cpp:57 SPELL_CHANNEL_VISUAL_EFFECT does through the summoning auras"),
        new(28376, K.UsePath, "ArcaneCore.Game/Spells/Procs/BuiltInProcHandlers.cs", "[18137] = 28377",
            "Shadowguard (proc trigger): the priest aura's DBC trigger is this DUMMY spell; the built-in proc table casts the rank's damage spell 28377..28382 directly (vmangos UnitAuraProcHandler.cpp:1286-1306)"),
    ];
}
