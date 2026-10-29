using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using K = ArcaneCore.World.Playerbots.Qa.QaSpellConsumerKind;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>How ArcaneCore consumes a spell outside the registries <see cref="PlayerbotQaSpellGapAnalyzer"/> reads.</summary>
internal enum QaSpellConsumerKind
{
    /// <summary>A rule or script asks <c>HasAura</c> (or removes the aura) for the spell id.</summary>
    AuraReader,

    /// <summary>A creature AI, instance script or feature handles the spell hit (OnSpellHit, a spell-hit switch).</summary>
    SpellHitHandler,

    /// <summary>An aura holder event (apply, remove, expire) runs the spell's behaviour.</summary>
    HolderEvent,

    /// <summary>A use path replaces the cast: the feature does what the spell does without casting it, or reads the id in a rule.</summary>
    UsePath,

    /// <summary>A direct packet send replaces the effect.</summary>
    DirectSend,

    /// <summary>The creature EventAI engine runs a database spell-hit row for the spell.</summary>
    EventAi,

    /// <summary>The creature evade rule keeps the cosmetic aura (<c>SpellSystemCreatureCaster.KeptOnEvade</c>).</summary>
    EvadeKeep,
}

/// <summary>One spell ArcaneCore consumes somewhere the analyzer cannot see.</summary>
/// <param name="SpellId">The Spell.dbc id.</param>
/// <param name="Kind">How it is consumed.</param>
/// <param name="File">The consumer source file, relative to <c>src/</c>.</param>
/// <param name="Needle">Text the file must contain (the spell id, a constant or a method): the test that proves the consumer still exists.</param>
/// <param name="Why">What the consumer does, with the reference it mirrors.</param>
internal sealed record QaSpellConsumer(uint SpellId, QaSpellConsumerKind Kind, string File, string Needle, string Why);

/// <summary>
/// The spells whose DUMMY effect or aura ArcaneCore consumes in code <see cref="PlayerbotQaSpellGapAnalyzer"/> cannot see: <c>HasAura</c>
/// readers, AI spell-hit handlers, use paths, direct packet sends, aura holder events, EventAI spell-hit rows, the evade rule. Each entry names the
/// consumer file and a needle the file must contain; the tests check both against the source tree, so an entry cannot outlive its consumer.
/// Immutable after type initialisation; safe from any thread.
/// </summary>
internal static class QaSpellConsumers
{
    private static readonly FrozenDictionary<uint, QaSpellConsumer> s_byId = Index(Build());

    /// <summary>Every entry.</summary>
    internal static IReadOnlyCollection<QaSpellConsumer> All => s_byId.Values;

    /// <summary>The consumer of <paramref name="spellId"/>, if one is known.</summary>
    internal static bool TryGet(uint spellId, [NotNullWhen(true)] out QaSpellConsumer? consumer) => s_byId.TryGetValue(spellId, out consumer);

    private static FrozenDictionary<uint, QaSpellConsumer> Index(QaSpellConsumer[] all)
    {
        var map = new Dictionary<uint, QaSpellConsumer>(all.Length);
        foreach (QaSpellConsumer entry in all)
        {
            if (!map.TryAdd(entry.SpellId, entry))
            {
                throw new InvalidOperationException($"QaSpellConsumers lists spell {entry.SpellId} twice");
            }
        }

        return map.ToFrozenDictionary();
    }

    private static QaSpellConsumer[] Build() =>
    [
        // AuraReader (22)
        new(2584, K.AuraReader, "ArcaneCore.Game/Spells/Effects/SpiritHealEffect.cs", "2584",
            "Spirit Healer resurrect checks HasAura(Waiting to Resurrect); BattlegroundHandlers.cs and BattlegroundFeature.cs cast and remove it (vmangos SpellEffects.cpp:5830)"),
        new(10255, K.AuraReader, "ArcaneCore.Game/Creatures/AI/StonedDormancy.cs", "10255",
            "StonedDormancy reads the aura"),
        new(11206, K.AuraReader, "ArcaneCore.Game/Battlegrounds/AlteracValleyScripts/AvWorldBossAI.cs", "11206",
            "the Alterac Valley boss AI removes the aura; AvSummonerAddAI.cs checks HasAura (vmangos battleground_alterac.cpp:4385,4460)"),
        new(12623, K.AuraReader, "ArcaneCore.Game/Instances/Scripts/SunkenTemple/SunkenTempleInstance.Events.cs", "12623",
            "the Shade of Hakkar event checks HasAura(Suppression) (vmangos SpellAuras.cpp:1692)"),
        new(18172, K.AuraReader, "ArcaneCore.Game/Creatures/Scripts/AgedDyingAncientKodoAI.cs", "18172",
            "AgedDyingAncientKodoAI reads the aura"),
        new(20585, K.AuraReader, "ArcaneCore.Game/Death/Ghost/GhostForm.cs", "20585",
            "HasSpell decides the wisp ghost"),
        new(20939, K.AuraReader, "ArcaneCore.World/Gm/Teleport/UnstuckCommands.cs", "20939",
            "the unstuck command reads the aura (vmangos SpellAuras.cpp:1919)"),
        new(21354, K.AuraReader, "ArcaneCore.Game/Spells/Holidays/HolidaySpellScripts.cs", "21354",
            "HasAura(Snowball Resistant) blocks the snowball"),
        new(21708, K.AuraReader, "ArcaneCore.Game/Instances/Scripts/Maraudon/NoxxionAI.cs", "21708",
            "NoxxionAI checks HasAura"),
        new(22008, K.AuraReader, "ArcaneCore.Game/Spells/Mods/SpellModAuraHandlers.cs", "22008",
            "the one-charge rule, all vmangos does with it (SpellAuras.cpp:1095); no reader of OVERRIDE_CLASS_SCRIPTS misc 4081 in vmangos either"),
        new(22683, K.AuraReader, "ArcaneCore.Game/Instances/Scripts/BlackwingLair/ShadowFlameScript.cs", "22683",
            "Shadow Flame checks HasAura(Onyxia Scale Cloak) (vmangos SpellEffects.cpp:3738)"),
        new(23182, K.AuraReader, "ArcaneCore.Game/Spells/Generic/RaidDummyScripts.cs", "23182",
            "the Mark of Frost and Mark of Nature HasAura checks (vmangos SpellEffects.cpp:1303,1306)"),
        new(25040, K.AuraReader, "ArcaneCore.Game/Spells/Generic/RaidDummyScripts.cs", "25040",
            "the Mark of Frost and Mark of Nature HasAura checks (vmangos SpellEffects.cpp:1303,1306)"),
        new(26013, K.AuraReader, "ArcaneCore.World/Battlegrounds/BattlegroundHandlers.cs", "26013",
            "HasAura(Deserter) blocks queueing (vmangos Player.cpp:18706)"),
        new(26680, K.AuraReader, "ArcaneCore.Game/Spells/Generic/WorldEventDummyScripts.cs", "26680",
            "the Love is in the Air dummy scripts check the Adored and Heartbroken auras (vmangos SpellEffects.cpp:1360,4083,4114)"),
        new(26898, K.AuraReader, "ArcaneCore.Game/Spells/Generic/WorldEventDummyScripts.cs", "26898",
            "the Love is in the Air dummy scripts check the Adored and Heartbroken auras (vmangos SpellEffects.cpp:1360,4083,4114)"),
        new(28775, K.AuraReader, "ArcaneCore.Game/Spells/Paladin/PaladinProcScripts.cs", "28775",
            "the tier 3 bonus checks HasAura (vmangos spell_paladin.cpp:146)"),
        new(28832, K.AuraReader, "ArcaneCore.Game/Instances/Scripts/Naxxramas/NaxxramasPartTwoSpellScripts.cs", "28832",
            "the Four Horsemen mark scripts"),
        new(28833, K.AuraReader, "ArcaneCore.Game/Instances/Scripts/Naxxramas/NaxxramasPartTwoSpellScripts.cs", "28833",
            "the Four Horsemen mark scripts"),
        new(28834, K.AuraReader, "ArcaneCore.Game/Instances/Scripts/Naxxramas/NaxxramasPartTwoSpellScripts.cs", "28834",
            "the Four Horsemen mark scripts"),
        new(28835, K.AuraReader, "ArcaneCore.Game/Instances/Scripts/Naxxramas/NaxxramasPartTwoSpellScripts.cs", "28835",
            "the Four Horsemen mark scripts"),
        new(29154, K.AuraReader, "ArcaneCore.Game/Instances/Scripts/Naxxramas/NaxxramasTrashAIs.cs", "29154",
            "Naxxramas trash AI checks HasAura"),
        // SpellHitHandler (34)
        new(7079, K.SpellHitHandler, "ArcaneCore.Game/Creatures/Scripts/PrivateHendelAI.cs", "7079",
            "PrivateHendelAI.OnSpellHit"),
        new(9439, K.SpellHitHandler, "ArcaneCore.Game/Creatures/Scripts/RabidBearAI.cs", "9439",
            "RabidBearAI.OnSpellHit"),
        new(10113, K.SpellHitHandler, "ArcaneCore.Game/Creatures/Scripts/TaskmasterFizzuleAI.cs", "10113",
            "TaskmasterFizzuleAI spell-hit switch"),
        new(10137, K.SpellHitHandler, "ArcaneCore.Game/Creatures/Scripts/TaskmasterFizzuleAI.cs", "10137",
            "TaskmasterFizzuleAI spell-hit switch"),
        new(10259, K.SpellHitHandler, "ArcaneCore.Game/Instances/Scripts/Uldaman/ArchaerasAddAI.cs", "10259",
            "ArchaerasAddAI spell-hit switch"),
        new(11402, K.SpellHitHandler, "ArcaneCore.Game/Creatures/Scripts/FollowerQuestsAI.cs", "11402",
            "FollowerQuestsAI.OnSpellHit"),
        new(14250, K.SpellHitHandler, "ArcaneCore.Game/Creatures/Scripts/GrarkLorkrubAI.cs", "14250",
            "GrarkLorkrubAI.OnSpellHit"),
        new(15591, K.SpellHitHandler, "ArcaneCore.Game/Creatures/Scripts/RingoAI.cs", "15591",
            "RingoAI.OnSpellHit"),
        new(17536, K.SpellHitHandler, "ArcaneCore.Game/Creatures/Scripts/KerlonianAI.cs", "17536",
            "KerlonianAI.OnSpellHit"),
        new(19515, K.SpellHitHandler, "ArcaneCore.Game/Instances/Scripts/MoltenCore/MoltenCoreBossAI.cs", "19515",
            "the Molten Core boss spell hit casts 19516"),
        new(20275, K.SpellHitHandler, "ArcaneCore.Game/Creatures/Scripts/CityGuardAI.cs", "20275",
            "CityGuardAI.OnSpellHit"),
        new(20804, K.SpellHitHandler, "ArcaneCore.Game/Creatures/Scripts/TriageAI.cs", "20804",
            "TriageAI.OnSpellHit"),
        new(24734, K.SpellHitHandler, "ArcaneCore.Game/GameObjects/GameObjectMapSystem.SpellActions.cs", "24734",
            "the Silithus wind-stone summons (vmangos silithus.cpp:76-90)"),
        new(24744, K.SpellHitHandler, "ArcaneCore.Game/GameObjects/GameObjectMapSystem.SpellActions.cs", "24744",
            "the Silithus wind-stone summons (vmangos silithus.cpp:76-90)"),
        new(24756, K.SpellHitHandler, "ArcaneCore.Game/GameObjects/GameObjectMapSystem.SpellActions.cs", "24756",
            "the Silithus wind-stone summons (vmangos silithus.cpp:76-90)"),
        new(24758, K.SpellHitHandler, "ArcaneCore.Game/GameObjects/GameObjectMapSystem.SpellActions.cs", "24758",
            "the Silithus wind-stone summons (vmangos silithus.cpp:76-90)"),
        new(24760, K.SpellHitHandler, "ArcaneCore.Game/GameObjects/GameObjectMapSystem.SpellActions.cs", "24760",
            "the Silithus wind-stone summons (vmangos silithus.cpp:76-90)"),
        new(24763, K.SpellHitHandler, "ArcaneCore.Game/GameObjects/GameObjectMapSystem.SpellActions.cs", "24763",
            "the Silithus wind-stone summons (vmangos silithus.cpp:76-90)"),
        new(24765, K.SpellHitHandler, "ArcaneCore.Game/GameObjects/GameObjectMapSystem.SpellActions.cs", "24765",
            "the Silithus wind-stone summons (vmangos silithus.cpp:76-90)"),
        new(24768, K.SpellHitHandler, "ArcaneCore.Game/GameObjects/GameObjectMapSystem.SpellActions.cs", "24768",
            "the Silithus wind-stone summons (vmangos silithus.cpp:76-90)"),
        new(24770, K.SpellHitHandler, "ArcaneCore.Game/GameObjects/GameObjectMapSystem.SpellActions.cs", "24770",
            "the Silithus wind-stone summons (vmangos silithus.cpp:76-90)"),
        new(24772, K.SpellHitHandler, "ArcaneCore.Game/GameObjects/GameObjectMapSystem.SpellActions.cs", "24772",
            "the Silithus wind-stone summons (vmangos silithus.cpp:76-90)"),
        new(24784, K.SpellHitHandler, "ArcaneCore.Game/GameObjects/GameObjectMapSystem.SpellActions.cs", "24784",
            "the Silithus wind-stone summons (vmangos silithus.cpp:76-90)"),
        new(24786, K.SpellHitHandler, "ArcaneCore.Game/GameObjects/GameObjectMapSystem.SpellActions.cs", "24786",
            "the Silithus wind-stone summons (vmangos silithus.cpp:76-90)"),
        new(24788, K.SpellHitHandler, "ArcaneCore.Game/GameObjects/GameObjectMapSystem.SpellActions.cs", "24788",
            "the Silithus wind-stone summons (vmangos silithus.cpp:76-90)"),
        new(24789, K.SpellHitHandler, "ArcaneCore.Game/GameObjects/GameObjectMapSystem.SpellActions.cs", "24789",
            "the Silithus wind-stone summons (vmangos silithus.cpp:76-90)"),
        new(24790, K.SpellHitHandler, "ArcaneCore.Game/GameObjects/GameObjectMapSystem.SpellActions.cs", "24790",
            "the Silithus wind-stone summons (vmangos silithus.cpp:76-90)"),
        new(26374, K.SpellHitHandler, "ArcaneCore.Game/Holidays/LunarFestivalScripts.cs", "26374",
            "the Lunar Festival spell hit on Omen"),
        new(28281, K.SpellHitHandler, "ArcaneCore.World/WorldState/ScourgeInvasionScripts.cs", "28281",
            "the Scourge Invasion communique spell-hit switches"),
        new(28326, K.SpellHitHandler, "ArcaneCore.World/WorldState/ScourgeInvasionScripts.cs", "28326",
            "the Scourge Invasion communique spell-hit switches"),
        new(28351, K.SpellHitHandler, "ArcaneCore.World/WorldState/ScourgeInvasionFeature.cs", "CampDeathCommunique",
            "the Necropolis health creature reacts to the camp death communique spell hit"),
        new(28365, K.SpellHitHandler, "ArcaneCore.World/WorldState/ScourgeInvasionScripts.cs", "28365",
            "the Scourge Invasion communique spell-hit switches"),
        new(28366, K.SpellHitHandler, "ArcaneCore.World/WorldState/ScourgeInvasionScripts.cs", "28366",
            "the Scourge Invasion communique spell-hit switches"),
        new(28441, K.SpellHitHandler, "ArcaneCore.Game/Instances/Scripts/ScarletMonastery/MograineWhitemaneAi.cs", "28441",
            "MograineWhitemaneAi spell hit"),
        // HolderEvent (4)
        new(21094, K.HolderEvent, "ArcaneCore.Game/Instances/Scripts/MoltenCore/MoltenCoreAuras.cs", "21094",
            "the Molten Core aura holder events"),
        new(23487, K.HolderEvent, "ArcaneCore.Game/Instances/Scripts/MoltenCore/MoltenCoreAuras.cs", "23487",
            "the Molten Core aura holder events"),
        new(24314, K.HolderEvent, "ArcaneCore.Game/Instances/Scripts/ZulGurub/OtherBossAis.cs", "24314",
            "the Threatening Gaze aura module reacts to the holder event"),
        new(25937, K.HolderEvent, "ArcaneCore.Game/Instances/Scripts/TempleOfAhnQiraj/ViscidusAIs.cs", "25937",
            "the freeze aura holder event"),
        // UsePath (18)
        new(6119, K.UsePath, "ArcaneCore.Game/Spells/SpellSystem.WeaponSwap.cs", "6119",
            "the weapon-switch cooldown carrier (vmangos SharedDefines.h:1175-1176 uses the id only for the cooldown)"),
        new(6123, K.UsePath, "ArcaneCore.Game/Spells/SpellSystem.WeaponSwap.cs", "6123",
            "the weapon-switch cooldown carrier (vmangos SharedDefines.h:1175-1176 uses the id only for the cooldown)"),
        new(20608, K.UsePath, "ArcaneCore.Game/Death/Resurrection/SelfResurrection.cs", "20608",
            "the self-resurrection spell choice reads the aura or passive (vmangos Player.cpp:19868-19932)"),
        new(20707, K.UsePath, "ArcaneCore.Game/Death/Resurrection/SelfResurrection.cs", "20707",
            "the self-resurrection spell choice reads the aura or passive (vmangos Player.cpp:19868-19932)"),
        new(20762, K.UsePath, "ArcaneCore.Game/Death/Resurrection/SelfResurrection.cs", "20762",
            "the self-resurrection spell choice reads the aura or passive (vmangos Player.cpp:19868-19932)"),
        new(20763, K.UsePath, "ArcaneCore.Game/Death/Resurrection/SelfResurrection.cs", "20763",
            "the self-resurrection spell choice reads the aura or passive (vmangos Player.cpp:19868-19932)"),
        new(20764, K.UsePath, "ArcaneCore.Game/Death/Resurrection/SelfResurrection.cs", "20764",
            "the self-resurrection spell choice reads the aura or passive (vmangos Player.cpp:19868-19932)"),
        new(20765, K.UsePath, "ArcaneCore.Game/Death/Resurrection/SelfResurrection.cs", "20765",
            "the self-resurrection spell choice reads the aura or passive (vmangos Player.cpp:19868-19932)"),
        new(23383, K.UsePath, "ArcaneCore.Game/GameObjects/GameObjectMapSystem.Types.cs", "OnFlagClicked",
            "the flag pickup runs from the game-object use path (OnFlagClicked, WarsongGulch.EventPlayerClickedOnFlag) instead of casting the spell; vmangos SpellEffects.cpp:421 calls the same battleground event"),
        new(23384, K.UsePath, "ArcaneCore.Game/GameObjects/GameObjectMapSystem.Types.cs", "OnFlagClicked",
            "the flag pickup runs from the game-object use path (OnFlagClicked, WarsongGulch.EventPlayerClickedOnFlag) instead of casting the spell; vmangos SpellEffects.cpp:421 calls the same battleground event"),
        new(23701, K.UsePath, "ArcaneCore.Game/Death/Resurrection/SelfResurrection.cs", "23701",
            "the self-resurrection spell choice reads the aura or passive (vmangos Player.cpp:19868-19932)"),
        new(24171, K.UsePath, "ArcaneCore.Game/Spells/Checks/LocationCastCheck.cs", "24171",
            "the only vmangos rule for the spell is the battleground-only cast check (SpellMgr.cpp:2687), implemented here; the DUMMY itself is visual"),
        new(25938, K.UsePath, "ArcaneCore.Game/Instances/Scripts/TempleOfAhnQiraj/ViscidusAIs.cs", "25938",
            "the AI does the explode, kill-below-5% and glob summons itself (vmangos SpellEffects.cpp:1204-1226)"),
        new(26003, K.UsePath, "ArcaneCore.Game/Instances/Scripts/TempleOfAhnQiraj/ViscidusAIs.cs", "Combat.Kill(Me, Me)",
            "the AI kills Viscidus directly below 5% health, which is what the suicide spell does (vmangos SpellEffects.cpp:1232-1240 casts 26002)"),
        new(27861, K.UsePath, "ArcaneCore.Game/Instances/Scripts/Stratholme/StratholmeInstance.cs", "27861",
            "the instance casts and removes it and keeps the Baron's timer itself"),
        new(27965, K.UsePath, "ArcaneCore.Game/Spells/SpellSystem.SpiritOfRedemption.cs", "27965",
            "Spirit of Redemption suicide (vmangos Unit.cpp:1108)"),
        new(28851, K.UsePath, "ArcaneCore.Game/Spells/Paladin/PaladinSpellScripts.cs", "28851",
            "Libram of Divinity and Libram of Light flash of light bonus (vmangos SpellEffects.cpp:4494-4497)"),
        new(28853, K.UsePath, "ArcaneCore.Game/Spells/Paladin/PaladinSpellScripts.cs", "28853",
            "Libram of Divinity and Libram of Light flash of light bonus (vmangos SpellEffects.cpp:4494-4497)"),
        // DirectSend (1)
        new(17251, K.DirectSend, "ArcaneCore.Game/Npc/QuestNpcServices.BankSpirit.cs", "17251",
            "sends SMSG_SPIRIT_HEALER_CONFIRM directly instead of casting the spell, which is all vmangos SpellEffects.cpp:856 does"),
        // EventAi (8)
        new(9012, K.EventAi, "ArcaneCore.Game/Creatures/AI/EventAi/EventAiEngine.cs", "SpellHit",
            "classic-db creature_ai_scripts has an event_type 8 (spell hit) row for the spell; EventAiEngine.SpellHit runs it"),
        new(16007, K.EventAi, "ArcaneCore.Game/Creatures/AI/EventAi/EventAiEngine.cs", "SpellHit",
            "classic-db creature_ai_scripts has an event_type 8 (spell hit) row for the spell; EventAiEngine.SpellHit runs it"),
        new(19512, K.EventAi, "ArcaneCore.Game/Creatures/AI/EventAi/EventAiEngine.cs", "SpellHit",
            "classic-db creature_ai_scripts has an event_type 8 (spell hit) row for the spell; EventAiEngine.SpellHit runs it"),
        new(21332, K.EventAi, "ArcaneCore.Game/Creatures/AI/EventAi/EventAiEngine.cs", "SpellHit",
            "classic-db creature_ai_scripts has an event_type 8 (spell hit) row for the spell; EventAiEngine.SpellHit runs it"),
        new(21960, K.EventAi, "ArcaneCore.Game/Creatures/AI/EventAi/EventAiEngine.cs", "SpellHit",
            "classic-db creature_ai_scripts has an event_type 8 (spell hit) row for the spell; EventAiEngine.SpellHit runs it"),
        new(23107, K.EventAi, "ArcaneCore.Game/Creatures/AI/EventAi/EventAiEngine.cs", "SpellHit",
            "classic-db creature_ai_scripts has an event_type 8 (spell hit) row for the spell; EventAiEngine.SpellHit runs it"),
        new(24062, K.EventAi, "ArcaneCore.Game/Creatures/AI/EventAi/EventAiEngine.cs", "SpellHit",
            "classic-db creature_ai_scripts has an event_type 8 (spell hit) row for the spell; EventAiEngine.SpellHit runs it"),
        new(28035, K.EventAi, "ArcaneCore.Game/Creatures/AI/EventAi/EventAiEngine.cs", "SpellHit",
            "classic-db creature_ai_scripts has an event_type 8 (spell hit) row for the spell; EventAiEngine.SpellHit runs it"),
        // EvadeKeep (8)
        new(9617, K.EvadeKeep, "ArcaneCore.Game/Creatures/AI/SpellSystemCreatureCaster.cs", "KeptOnEvade",
            "a cosmetic aura that survives evade: SpellSystemCreatureCaster.KeptOnEvade mirrors cmangos IsSpellRemovedOnEvade (SpellMgr.h:469-600); no core reads the aura otherwise"),
        new(12898, K.EvadeKeep, "ArcaneCore.Game/Creatures/AI/SpellSystemCreatureCaster.cs", "KeptOnEvade",
            "a cosmetic aura that survives evade: SpellSystemCreatureCaster.KeptOnEvade mirrors cmangos IsSpellRemovedOnEvade (SpellMgr.h:469-600); no core reads the aura otherwise"),
        new(21862, K.EvadeKeep, "ArcaneCore.Game/Creatures/AI/SpellSystemCreatureCaster.cs", "KeptOnEvade",
            "a cosmetic aura that survives evade: SpellSystemCreatureCaster.KeptOnEvade mirrors cmangos IsSpellRemovedOnEvade (SpellMgr.h:469-600); no core reads the aura otherwise"),
        new(22578, K.EvadeKeep, "ArcaneCore.Game/Creatures/AI/SpellSystemCreatureCaster.cs", "KeptOnEvade",
            "a cosmetic aura that survives evade: SpellSystemCreatureCaster.KeptOnEvade mirrors cmangos IsSpellRemovedOnEvade (SpellMgr.h:469-600); no core reads the aura otherwise"),
        new(22650, K.EvadeKeep, "ArcaneCore.Game/Creatures/AI/SpellSystemCreatureCaster.cs", "KeptOnEvade",
            "a cosmetic aura that survives evade: SpellSystemCreatureCaster.KeptOnEvade mirrors cmangos IsSpellRemovedOnEvade (SpellMgr.h:469-600); no core reads the aura otherwise"),
        new(24313, K.EvadeKeep, "ArcaneCore.Game/Creatures/AI/SpellSystemCreatureCaster.cs", "KeptOnEvade",
            "a cosmetic aura that survives evade: SpellSystemCreatureCaster.KeptOnEvade mirrors cmangos IsSpellRemovedOnEvade (SpellMgr.h:469-600); no core reads the aura otherwise"),
        new(25039, K.EvadeKeep, "ArcaneCore.Game/Creatures/AI/SpellSystemCreatureCaster.cs", "KeptOnEvade",
            "a cosmetic aura that survives evade: SpellSystemCreatureCaster.KeptOnEvade mirrors cmangos IsSpellRemovedOnEvade (SpellMgr.h:469-600); no core reads the aura otherwise"),
        new(28126, K.EvadeKeep, "ArcaneCore.Game/Creatures/AI/SpellSystemCreatureCaster.cs", "KeptOnEvade",
            "a cosmetic aura that survives evade: SpellSystemCreatureCaster.KeptOnEvade mirrors cmangos IsSpellRemovedOnEvade (SpellMgr.h:469-600); no core reads the aura otherwise"),
    ];
}
