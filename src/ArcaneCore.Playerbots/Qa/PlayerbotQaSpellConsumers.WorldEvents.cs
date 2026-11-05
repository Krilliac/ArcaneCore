using K = ArcaneCore.World.Playerbots.Qa.QaSpellConsumerKind;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>The spell consumers of world events: content lanes add their entries here, never in the shared files.</summary>
internal static partial class QaSpellConsumers
{
    private static QaSpellConsumer[] BuildWorldEvents() =>
    [
        new(18350, K.UsePath, "ArcaneCore.Game/Spells/Shaman/ShamanSetBonusScripts.cs", "EarthshattererTick",
            "the Earthshatterer 8-piece check that vmangos runs from the 18350 Dummy Trigger (SpellEffects.cpp:902-919) is done directly by the 28820 periodic tick; 18350 is the shared trigger of 15 auras (13810, 18073, 18096, 20210, 20212-20215, 23551, 23572, 27522, 27811, 27815, 27816, 28820), 13810 and 28820 bypass the cast in periodic scripts (SpellSystem.Auras.cs) and the rest are special-cased in BuiltInProcHandlers.cs"),
        new(28349, K.UsePath, "ArcaneCore.World/WorldState/ScourgeInvasionFeature.cs", "TearDownNecropolis",
            "the Despawner a destroyed Necropolis Health unit casts (vmangos scourge_invasion.cpp:148-157, :449-457) is done directly by ScourgeInvasionFeature.TearDownNecropolis, which removes the Necropolis and its objects without respawn"),
    ];
}
