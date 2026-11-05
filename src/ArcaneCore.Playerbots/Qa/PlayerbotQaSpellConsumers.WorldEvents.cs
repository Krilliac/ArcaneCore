using K = ArcaneCore.World.Playerbots.Qa.QaSpellConsumerKind;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>The spell consumers of world events: content lanes add their entries here, never in the shared files.</summary>
internal static partial class QaSpellConsumers
{
    private static QaSpellConsumer[] BuildWorldEvents() =>
    [
        new(18350, K.UsePath, "ArcaneCore.Game/Spells/Shaman/ShamanSetBonusScripts.cs", "EarthshattererTick",
            "the Earthshatterer 8-piece check that vmangos runs from the 18350 Dummy Trigger (SpellEffects.cpp:902-919, triggered by 28820) is done directly by the 28820 periodic tick, so 18350 is never cast"),
        new(19502, K.EventAi, "ArcaneCore.Game/Creatures/AI/EventAi/EventAiEngine.cs", "SpellHit",
            "the Sickly Deer and Sickly Gazelle carry the aura from their spawn; classic-db ACID creature_ai_scripts rows 1229601 and 1229801 (spell hit 19512) remove it with ACTION_T_REMOVEAURASFROMSPELL (RemoveAurasFromSpellAction); vmangos npcs_special.cpp:1796 removes it the same way"),
        new(28349, K.UsePath, "ArcaneCore.World/WorldState/ScourgeInvasionFeature.cs", "TearDownNecropolis",
            "the Despawner a destroyed Necropolis Health unit casts (vmangos scourge_invasion.cpp:148-157, :449-457) is done directly by ScourgeInvasionFeature.TearDownNecropolis, which removes the Necropolis and its objects without respawn"),
    ];
}
