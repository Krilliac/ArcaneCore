using K = ArcaneCore.World.Playerbots.Qa.QaSpellConsumerKind;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>The spell consumers of Ahn'Qiraj: content lanes add their entries here, never in the shared files.</summary>
internal static partial class QaSpellConsumers
{
    private static QaSpellConsumer[] BuildAhnQiraj() =>
    [
        new(25896, K.UsePath, "ArcaneCore.Game/Instances/Scripts/TempleOfAhnQiraj/ViscidusAIs.cs", "DoCast(summoner, 25896, triggered: true)",
            "Rejoin Viscidus: an arriving glob casts it at the boss and ViscidusAI.GlobRejoined does what the spell stands for (the boss regrows, the glob goes); the SCRIPT_EFFECT itself is empty in vmangos (boss_viscidus.cpp:333) and cmangos (RejoinViscidus)"),
        new(27934, K.UsePath, "ArcaneCore.Game/Instances/Scripts/TempleOfAhnQiraj/ViscidusAIs.cs", "Cast(27934, Me, triggered: true)",
            "Viscidus Shrinks: the boss AI takes the five percent of health for a killed glob itself when it casts the marker (vmangos boss_viscidus.cpp:304-318, 'should be a spell script')"),
        new(26205, K.UsePath, "ArcaneCore.Game/Instances/Scripts/TempleOfAhnQiraj/CthunTentacleAIs.cs", "System?.NearTeleport(Me, target.X + 0.5f",
            "Teleport Giant Hook Tentacle Trigger: the claw tentacle AI relocates itself next to an out-of-reach victim directly; the cmangos script (boss_cthun.cpp:674-700) returns early for a creature caster, so the spell never acts there"),
    ];
}
