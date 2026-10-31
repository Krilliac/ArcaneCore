using K = ArcaneCore.World.Playerbots.Qa.QaSpellConsumerKind;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>The spell consumers of Blackrock raids (Molten Core, Blackwing Lair): content lanes add their entries here, never in the shared files.</summary>
internal static partial class QaSpellConsumers
{
    private static QaSpellConsumer[] BuildBlackrockRaids() =>
    [
        new(23398, K.UsePath, "ArcaneCore.Game/Instances/Scripts/BlackwingLair/NefarianAI.cs", "23398",
            "Nefarian's Druid class call: the effect is aura 112 (OVERRIDE_CLASS_SCRIPTS), NefarianAI casts the call when a Druid is picked and BlackwingLairTargetModule narrows its targets to Druids (vmangos boss_nefarian.cpp:49 SPELL_DRUID, applied by AddAura at :245)"),
    ];
}
