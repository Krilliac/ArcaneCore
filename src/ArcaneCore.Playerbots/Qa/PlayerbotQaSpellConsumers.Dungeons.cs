using K = ArcaneCore.World.Playerbots.Qa.QaSpellConsumerKind;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>The spell consumers of dungeons and dungeon-area quest events: content lanes add their entries here, never in the shared files.</summary>
internal static partial class QaSpellConsumers
{
    private const string EventAiFile = "ArcaneCore.Game/Creatures/AI/EventAi/EventAiEngine.cs";
    private const string DbScriptFile = "ArcaneCore.Game/Spells/SpellSystem.DbSpellScripts.cs";
    private const string AnubisathFile = "ArcaneCore.Game/Instances/Scripts/TempleOfAhnQiraj/AnubisathAIs.cs";
    private const string SkeramFile = "ArcaneCore.Game/Instances/Scripts/TempleOfAhnQiraj/SkeramAI.cs";

    private static QaSpellConsumer EventAiRow(uint spellId, string row)
        => new(spellId, K.EventAi, EventAiFile, "SpellHit", $"vmangos creature_ai_events has an event_type 8 (spell hit) row for the spell ({row}); EventAiEngine.SpellHit runs it");

    private static QaSpellConsumer DbScriptRow(uint spellId, string row)
        => new(spellId, K.UsePath, DbScriptFile, "CanEffectStartDbScript",
            $"classic-db dbscripts_on_spell has a row for the spell ({row}); the DUMMY effect starts it through SpellSystem.CanEffectStartDbScript, as no code claims the spell");

    private static QaSpellConsumer[] BuildDungeons() =>
    [
        new(10254, K.UsePath, "ArcaneCore.Game/Instances/Scripts/Uldaman/ArchaerasAddAI.cs", "DoCast(Me, SpellAwakenVisual)",
            "Stone Dwarf Awaken Visual: the waking minion casts it; what the cmangos dummy does (SpellEffects.cpp:430-439, clear root and immunity) is the removal of the Stoned aura (StonedDormancy)"),
        new(12639, K.UsePath, "ArcaneCore.Game/Instances/Scripts/SunkenTemple/SunkenTempleInstance.Events.cs", "UpdateEntry(shade, NpcAvatarOfHakkar)",
            "Summon Hakkar: the instance casts it on the Shade of Hakkar and turns it into the Avatar itself (cmangos sunken_temple.h:74, SummonHakkar::OnEffectExecute)"),
        new(12948, K.UsePath, "ArcaneCore.Game/Instances/Scripts/SunkenTemple/SunkenTempleInstance.Events.cs", "HakkarSummoned (spell 12948",
            "Avatar of Hakkar is summoned: the instance removes the evil circles when the Avatar arrives instead of casting it (vmangos world DB generic_scripts 850219)"),
        new(15064, K.AuraReader, "ArcaneCore.Game/Instances/Scripts/BlackrockDepths/BlackrockDepthsInstance.Events.cs", "HasAura(rocknot, NagmaraAI.SpellNagmaraRocknot)",
            "Nagmara + Rocknot: Rocknot and Nagmara are not despawned when the bar turns hostile while the aura is on Rocknot (vmangos instance_blackrock_depths.cpp:575)"),
        DbScriptRow(9096, "cast Rift Spawn Becomes Visible 9738"),
        DbScriptRow(11365, "despawn self"),
        DbScriptRow(21052, "Enthralled Deeprun Rat casts Suicide 8329"),
        EventAiRow(8894, "Stromgarde Vindicator vengeance, 258501"),
        EventAiRow(8603, "Thistlefur Avenger vengeance, 392502"),
        EventAiRow(9144, "Crushridge Warmonger enrage, 228704"),
        EventAiRow(11893, "Sul'lithuz Warder despawn, 814903"),
        EventAiRow(12938, "Fel Curse on the Razelikh, Grol, Allistarj and Sevine groups, 766502 to 767102"),
        EventAiRow(14247, "Blazerunner removes its aura, 937602"),
        EventAiRow(19032, "Human Skull starts its event, 1220201"),
        EventAiRow(19033, "Blighthound goes to Nathanos, 1188503"),
        new(18362, K.SpellHitHandler, "ArcaneCore.Game/Creatures/Scripts/AgedDyingAncientKodoAI.cs", "spell.Id != SpellKodoKomboGossip",
            "Kodo Kombo Gossip: the kodo AI casts it on itself and its spell-hit enables the gossip (vmangos creature_ai_events 470004, mangos-classic desolace.cpp:57)"),
        new(9173, K.UsePath, AnubisathFile, "Cast(ShadowStorm, null, triggered: true)",
            "Anubisath Guardian AOE: the Defender's trigger for Thunderclap or Shadow Storm; AnubisathDefenderAI casts those directly (vmangos creature_ai_events 1527703 and 1527704, mangos-classic mob_anubisath_sentinel.cpp:252-283)"),
        new(14291, K.UsePath, AnubisathFile, "Cast(Meteor, meteorTarget, triggered: true)",
            "Anubisath Guardian Targeted: the Defender's trigger for Plague or Meteor; AnubisathDefenderAI casts those directly (vmangos creature_ai_events 1527705 and 1527706)"),
        new(18476, K.UsePath, AnubisathFile, "Cast(Coin() ? SummonSwarmguard : SummonWarrior",
            "Summon Minion: the Defender's trigger for its summon; AnubisathDefenderAI casts the summon directly (vmangos creature_ai_events 1527709)"),
        new(24173, K.UsePath, "ArcaneCore.Game/Instances/Scripts/ZulGurub/ThekalAis.cs", "protected virtual bool Revive()",
            "Resurrection: the Thekal trio revive through the AI's resurrect timer and Revive instead of the cast (vmangos zulgurub.h:12, instance_zulgurub.cpp:420-436, mangos-classic boss_thekal.cpp:47)"),
        new(25813, K.UsePath, "ArcaneCore.Game/Creatures/Scripts/NightmareManifestsAI.cs", "DoCast(Me, SpellConjureRift, triggered: true)",
            "Conjure Dream Rift: the Eranikus event casts it and the AI does the summon's work (vmangos moonglade.cpp:32, mangos-classic moonglade.cpp:42)"),
        new(794, K.UsePath, SkeramFile, "images have phase dependent maximum health",
            "Initialize Images: SkeramAI.OnJustSummoned sets each image's phase-dependent maximum health and the boss's health percentage directly, as vmangos boss_skeram.cpp:298-299 does (cmangos boss_skeram.cpp:328-340)"),
        new(3730, K.UsePath, SkeramFile, "images have phase dependent maximum health",
            "Initialize Image: the per-image half of Initialize Images 794; the same direct health setup in SkeramAI.OnJustSummoned (cmangos boss_skeram.cpp:342-366)"),
        new(18969, K.UsePath, "ArcaneCore.Game/Creatures/Scripts/InDreamsAI.cs", "(taelan.AI as TaelanFordringAI)?.StruckDown()",
            "Taelan Death: the Isillien AI casts it and strikes Taelan down directly (vmangos creature_ai_events 184205, mangos-classic western_plaguelands.cpp:164)"),
        // The 2026-11 baseline triage (spell contract sweep): consumers ArcaneCore already has but the gap analyzer cannot see.
        DbScriptRow(16378, "Temperature Reading: set waypoint movement, pool search and kill credit of Krakle's Thermometer"),
        DbScriptRow(21914, "Celebras Quit Escort: remove Celebras Waiting, stop WP movement, set run, move"),
        DbScriptRow(21917, "Celebras Stone Trap: give quest completion 7046 after 25 s"),
        new(84, K.UsePath, AnubisathFile, "if (Cast(Enrage, Me))",
            "Low Health: the Defender's trigger for its Enrage or Explode below 10% health; AnubisathDefenderAI casts those directly (vmangos creature_ai_scripts 1527711 casts 84, creature_ai_events 1527708 reacts and runs 1527718 Explode or 1527728 Enrage)"),
        new(23852, K.UsePath, "ArcaneCore.Game/Spells/Generic/OpenWorldDummyScripts.cs", "StartItemSpellCooldown(context.Target, HatchEgg, UnhatchedJublingEgg)",
            "Jubling Cooldown (1 week): mangos-classic SpellEffects.cpp:3762-3764 casts it from 23853 as the cooldown marker; vmangos never casts it and starts the Hatch Egg 23851 item cooldown directly (SpellEffects.cpp:3745-3753), which JublingCooldownScript does"),
        new(24746, K.AuraReader, "ArcaneCore.Game/Conditions/ConditionEvaluator.cs", "case ConditionType.Aura:",
            "Twilight Cultist Disguise: the world DB aura condition reads it (vmangos conditions 887 and 888, type 1 aura 24746, which the gossip menu text rows of menus 6540, 6542 and 6543 test; classic-db conditions 887 and 888)"),
        new(18800, K.UsePath, "ArcaneCore.Game/Spells/Gm/CustomSpellRegistry.cs", "new(18800,",
            "Light Test: a GM staff tool taught by the GM spellbook (CustomSpellRegistry); its DUMMY aura is only the light visual"),
        new(22690, K.UsePath, "ArcaneCore.Game/Spells/Gm/CustomSpellRegistry.cs", "new(22690,",
            "Test Charge: a GM staff tool taught by the GM spellbook (CustomSpellRegistry); the charge effect does the work and the DUMMY aura carries nothing"),
    ];
}
