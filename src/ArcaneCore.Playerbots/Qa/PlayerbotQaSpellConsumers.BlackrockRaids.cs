using K = ArcaneCore.World.Playerbots.Qa.QaSpellConsumerKind;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>The spell consumers of Blackrock raids (Molten Core, Blackwing Lair): content lanes add their entries here, never in the shared files.</summary>
internal static partial class QaSpellConsumers
{
    private static QaSpellConsumer[] BuildBlackrockRaids() =>
    [
        new(23398, K.UsePath, "ArcaneCore.Game/Instances/Scripts/BlackwingLair/NefarianAI.cs", "23398",
            "Nefarian's Druid class call: the effect is aura 112 (OVERRIDE_CLASS_SCRIPTS), NefarianAI casts the call when a Druid is picked and BlackwingLairTargetModule narrows its targets to Druids (vmangos boss_nefarian.cpp:49 SPELL_DRUID, applied by AddAura at :245)"),
        new(23173, K.UsePath, "ArcaneCore.Game/Instances/Scripts/BlackwingLair/ChromaggusAI.cs", "private bool Afflict()",
            "Brood Affliction: Chromaggus's AI picks one of the five afflictions and applies it to eleven to fifteen threat-list targets itself, with the Chromatic Mutation upgrade when a player holds all five; the work mangos-classic SpellEffects.cpp case 23173 does from the DUMMY (vmangos boss_chromaggus.cpp:42 only names it)"),
        new(23195, K.UsePath, "ArcaneCore.Game/Instances/Scripts/BlackwingLair/ChromaggusAI.cs", "raid.SetData(9, Breaths[first]);",
            "Dragondog Breath Selection: Chromaggus's AI picks two different breaths of the five when it is built and stores them in the instance (data 9 and 10), the choice mangos-classic SpellEffects.cpp case 23195 makes by casting 23317-23326 at the instance script; the spell is never cast"),
        new(23343, K.UsePath, "ArcaneCore.Game/Instances/Scripts/BlackwingLair/VictorNefariusAI.cs", "raid.SetData(11, Spawners[first]);",
            "Nefarian Tunnel Selection: Lord Victor Nefarius's AI picks the two different drakonid kinds of the left and right tunnel when it is built and stores them in the instance (data 11 and 12), the choice mangos-classic SpellEffects.cpp case 23343 makes by casting 23344-23353 at the instance script (boss_victor_nefarius.cpp:84); the spell is never cast"),
        new(23642, K.AuraReader, "ArcaneCore.Game/Instances/Scripts/BlackwingLair/VaelastraszAI.cs", "System?.RemoveAuras(Me, 23642);",
            "Nefarius' Corruption: Vaelastrasz's intro puts the aura on him through Nefarius and removes it by hand when the red lightning strikes (vmangos boss_vaelastrasz.cpp:240 AddAura for 24 s); BlackwingLairTargetModule selects Vaelastrasz for it; the DUMMY aura has no other reader"),
        new(2400, K.UsePath, "ArcaneCore.Game/Instances/Scripts/TempleOfAhnQiraj/AnubisathAIs.cs", "CastSpellByUnit(buddy, Ability, buddy, triggered: true)",
            "Transfer Power: a dying Anubisath Sentinel hands its ability to each living sentinel of the group; the AI casts the ability and Heal Brethren at the buddy itself (a dead unit cannot cast the transfer) as mob_anubisath_sentinel.cpp:294-299 does after casting 2400, whose DUMMY has no handler in any core"),
        new(17131, K.UsePath, "ArcaneCore.Game/Instances/Scripts/BlackwingLair/NefarianAI.cs", "Me.AddMovementFlags(MovementFlags.Flying | MovementFlags.Hover);",
            "Hover: the DUMMY aura is only a flight marker (vmangos boss_victor_nefarius.cpp:433 casts it on Nefarian with SetFly, boss_nefarian.cpp:284 removes it, boss_onyxia.cpp:338 reads it); NefarianAI sets the Flying and Hover movement flags directly and clears them on landing"),
    ];
}
