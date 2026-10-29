using K = ArcaneCore.World.Playerbots.Qa.QaSpellConsumerKind;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>The G7 "unknowns" research entries (2026-10-29): spells ArcaneCore already consumes where the gap analyzer cannot see it.</summary>
internal static partial class QaSpellConsumers
{
    private static QaSpellConsumer[] BuildResearched() =>
    [
        new(17670, K.AuraReader, "ArcaneCore.World/Npc/ConditionFeature.cs", "AdCommissionVisual",
            "Argent Dawn Commission: the CONDITION_AD_COMMISSION_AURA check finds the aura by its SpellVisual (vmangos ConditionMgr AD commission aura, condition type 10)"),
        new(27420, K.AuraReader, "ArcaneCore.Game/Conditions/ConditionEvaluator.cs", "case ConditionType.Aura:",
            "Ectoplasmic Distiller: the world DB aura condition reads it (vmangos conditions entry 15, type 1 aura 27420; classic-db conditions 8922)"),
        new(23024, K.SpellHitHandler, "ArcaneCore.Game/Instances/Scripts/BlackwingLair/BlackwingOrbAI.cs", "ResetFireball = 23024",
            "Razorgore's reset Fireball: the Orb of Domination AI reacts on SpellHit; the DUMMY effects do nothing (cmangos blackwing_lair.h:73)"),
        new(28367, K.SpellHitHandler, "ArcaneCore.World/WorldState/ScourgeInvasionScripts.cs", "28367",
            "Scourge Invasion communique bolt: the receiving AI reacts on SpellHit (vmangos scourge_invasion.cpp:374,490,607,616)"),
        new(28449, K.SpellHitHandler, "ArcaneCore.World/WorldState/ScourgeInvasionScripts.cs", "28449",
            "Scourge Invasion camp communique: the Necrotic Shard AI reacts on SpellHit (vmangos scourge_invasion.cpp:754-757)"),
    ];
}
