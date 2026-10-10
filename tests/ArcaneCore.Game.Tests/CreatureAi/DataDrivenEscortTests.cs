using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Creatures.Scripts.Escorts;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using System.Globalization;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi;

public sealed class DataDrivenEscortTests
{
    [Theory]
    [InlineData(1978u, 435u, 14u)]
    [InlineData(2768u, 665u, 21u)]
    [InlineData(5644u, 1440u, 18u)]
    [InlineData(3465u, 898u, 54u)]
    [InlineData(3584u, 945u, 18u)]
    [InlineData(10427u, 4770u, 28u)]
    [InlineData(10646u, 4904u, 46u)]
    [InlineData(11856u, 6523u, 19u)]
    [InlineData(7784u, 648u, 35u)]
    [InlineData(7807u, 2767u, 38u)]
    [InlineData(12858u, 6544u, 21u)]
    public void ValidatedEscortCatalog_HasTheSourceQuestAndCompletionPoint(uint entry, uint quest, uint completionPoint)
    {
        EscortSpec spec = EscortSpecCatalog.Find(entry)!;
        Assert.NotNull(spec);
        Assert.Equal(quest, spec.QuestId);
        Assert.Contains(spec.Waypoints, p => p.Point == completionPoint && p.Actions.Any(a => a.Type == "quest_complete"));
    }

    [Theory]
    [InlineData(1978u)]
    [InlineData(2768u)]
    [InlineData(5644u)]
    [InlineData(3465u)]
    [InlineData(3584u)]
    [InlineData(10427u)]
    [InlineData(10646u)]
    [InlineData(11856u)]
    [InlineData(7784u)]
    [InlineData(7807u)]
    [InlineData(12858u)]
    public void ClassicDbPath_FiresEveryDeclaredWaypointAction_ThenCompletes(uint entry)
    {
        using Rig rig = Setup(entry);
        EscortSpec spec = EscortSpecCatalog.Find(entry)!;
        Assert.NotNull(spec);
        var ai = Assert.IsType<DataDrivenEscortAI>(rig.Escort.AI);
        ai.OnQuestAccept(rig.Player, spec.QuestId);
        Assert.True(ai.HasEscortState(EscortAI.EscortState.Escorting));
        if (spec.Faction != 0)
        {
            Assert.Equal(spec.Faction, rig.Escort.FactionTemplate);
        }
        if (spec.ClearImmuneToNpc)
        {
            Assert.Equal(UnitFlags.None, rig.Escort.UnitFlags & UnitFlags.ImmuneToNpc);
        }
        Assert.Equal(RealPath(entry).Count, ai.WaypointCount);
        if ((spec.StartStandState ?? spec.AcceptStandState) is { } startStand)
        {
            Assert.Equal((StandState)startStand, rig.Escort.StandState);
        }

        var seenSummons = new HashSet<ObjectGuid>();
        int lastTextCount = spec.StartText == 0 ? 0 : 1;
        var spoken = new List<(string Text, float X, float Y)>();
        (float X, float Y)? creditAt = null;
        uint completionPoint = spec.Waypoints.Single(w => w.Actions.Any(a => a.Type == "quest_complete")).Point;
        bool hasPostCreditActions = spec.Waypoints.Any(w => w.Point > completionPoint);
        for (int elapsed = 0; elapsed < 2_400_000 && (creditAt is null || (hasPostCreditActions && ai.CurrentWaypointIndex < ai.WaypointCount)); elapsed += 100)
        {
            rig.Player.Relocate(rig.Escort.X, rig.Escort.Y, rig.Escort.Z, 0, 0);
            rig.World.RunTick(100);
            if (creditAt is null && rig.Quests.Completed.Count > 0)
            {
                creditAt = (rig.Escort.X, rig.Escort.Y);
            }
            var chats = Packets(rig.Session, WorldOpcode.SmsgMessagechat);
            for (int i = lastTextCount; i < chats.Count; i++)
            {
                spoken.Add((ParseMonsterChat(chats[i]).Message, rig.Escort.X, rig.Escort.Y));
            }

            lastTextCount = chats.Count;
            foreach (Creature summon in rig.System.Creatures.Where(c => rig.SummonTemplateEntries.Contains(c.Entry) && seenSummons.Add(c.Guid)))
            {
                rig.Summons.Add((summon.Entry, summon.X, summon.Y, summon.Z, rig.Escort.X, rig.Escort.Y));
                // The ambush fights end here so the simulated escort can continue to its next point.
                rig.Map.Combat.Kill(null, summon);
            }
        }

        Assert.True(rig.Quests.Completed.Count == 1,
            $"entry {entry}: state {ai.State} point index {ai.CurrentWaypointIndex}/{ai.WaypointCount} " +
            $"escort {rig.Escort.X},{rig.Escort.Y},{rig.Escort.Z} alive {rig.Escort.IsAlive} " +
            $"failed {rig.Quests.Failed.Count} spoken {spoken.Count} summons {rig.Summons.Count} victim {rig.Escort.Combat.Victim?.GetType().Name}/{(rig.Escort.Combat.Victim as Creature)?.Entry} threat {rig.Escort.Combat.Threat.Entries.Count()} motion {rig.Escort.Motion.CurrentType} live {string.Join(';', rig.System.Creatures.Where(c => c.IsAlive).Select(c => c.Entry))}");
        Assert.Equal([(rig.Player, spec.QuestId)], rig.Quests.Completed);
        CreatureWaypoint completion = Assert.Single(RealPath(entry), p => spec.Waypoints.Any(w => w.Point == p.Point
            && w.Actions.Any(a => a.Type == "quest_complete")));
        Assert.InRange(MathF.Abs(creditAt!.Value.X - completion.X), 0f, 2f);
        Assert.InRange(MathF.Abs(creditAt.Value.Y - completion.Y), 0f, 2f);
        foreach (EscortWaypointSpec waypoint in spec.Waypoints)
        {
            CreatureWaypoint pathPoint = Assert.Single(RealPath(entry), p => p.Point == waypoint.Point);
            foreach (EscortActionSpec action in waypoint.Actions)
            {
                if (action.Type is "say" or "say_nearby")
                {
                    Assert.Contains(spoken, line => line.Text == action.Id.ToString(CultureInfo.InvariantCulture)
                        && MathF.Abs(line.X - pathPoint.X) < 2f && MathF.Abs(line.Y - pathPoint.Y) < 2f);
                }
                else if (action.Type == "summon")
                {
                    foreach (float[] position in action.Positions)
                    {
                        var summoned = Assert.Single(rig.Summons, s => s.Entry == (uint)action.Id
                            && MathF.Abs(s.X - position[0]) < 0.02f && MathF.Abs(s.Y - position[1]) < 0.02f);
                        Assert.InRange(MathF.Abs(summoned.EscortX - pathPoint.X), 0f, 2f);
                        Assert.InRange(MathF.Abs(summoned.EscortY - pathPoint.Y), 0f, 2f);
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData(1978u)]
    [InlineData(2768u)]
    [InlineData(5644u)]
    [InlineData(3465u)]
    [InlineData(3584u)]
    [InlineData(10427u)]
    [InlineData(10646u)]
    [InlineData(11856u)]
    [InlineData(7784u)]
    [InlineData(7807u)]
    [InlineData(12858u)]
    public void ClassicDbEscort_FailsItsQuestWhenThePlayerLeaves(uint entry)
    {
        using Rig rig = Setup(entry);
        EscortSpec spec = EscortSpecCatalog.Find(entry)!;
        Assert.NotNull(spec);
        var ai = Assert.IsType<DataDrivenEscortAI>(rig.Escort.AI);
        ai.OnQuestAccept(rig.Player, spec.QuestId);
        rig.Player.Relocate(rig.Escort.X + 300, rig.Escort.Y, rig.Escort.Z, 0, 0);
        Run(rig.World, 1_200);
        Assert.Equal([(rig.Player, spec.QuestId)], rig.Quests.Failed);
    }

    [Fact]
    public void Gilthares_AggroTextRequiresMerchantCoastAndANonPlayerTarget()
    {
        using Rig outside = Setup(3465);
        var outsideAi = Assert.IsType<DataDrivenEscortAI>(outside.Escort.AI);
        outside.Session.Clear();
        for (int i = 0; i < 64; i++) outsideAi.OnAggro(outside.Escort);
        Assert.Empty(Packets(outside.Session, WorldOpcode.SmsgMessagechat));

        using Rig coast = Setup(3465, areaId: 391);
        var coastAi = Assert.IsType<DataDrivenEscortAI>(coast.Escort.AI);
        coast.Session.Clear();
        for (int i = 0; i < 64; i++) coastAi.OnAggro(coast.Player);
        Assert.Empty(Packets(coast.Session, WorldOpcode.SmsgMessagechat));
        for (int i = 0; i < 64; i++) coastAi.OnAggro(coast.Escort);
        Assert.NotEmpty(Packets(coast.Session, WorldOpcode.SmsgMessagechat));
    }

    [Theory]
    [InlineData(10427u)] // npc_paoka_swiftmountainAI constructor: SetReactState(REACT_DEFENSIVE)
    [InlineData(10646u)] // npc_lakota_windsongAI constructor: SetReactState(REACT_DEFENSIVE)
    public void PaokaAndLakota_AreDefensive_FromSpawnAndAgainAfterARespawn(uint entry)
    {
        using Rig rig = Setup(entry);
        Assert.Equal(CreatureReactState.Defensive, rig.Escort.ReactState);

        rig.Map.Combat.Kill(null, rig.Escort);
        rig.System.ForceRespawn(rig.Escort);
        Assert.True(rig.Escort.IsAlive);
        Assert.Equal(CreatureReactState.Defensive, rig.Escort.ReactState);
    }

    [Fact]
    public void AnEscortWithoutAReactState_KeepsTheTemplates()
    {
        using Rig rig = Setup(1978);
        Assert.Equal(CreatureReactState.Aggressive, rig.Escort.ReactState);
    }

    [Theory]
    [InlineData(1978u, true)]  // npc_deathstalker_erlandAI::Aggro: DoScriptText(SAY_AGGRO_n, m_creature, who)
    [InlineData(2768u, false)] // npc_professor_phizzlethorpeAI::Aggro: DoScriptText(SAY_AGGRO, m_creature)
    public void AggroText_NamesTheAggressorOnlyWhereTheSourceDoes(uint entry, bool targeted)
    {
        using Rig rig = Setup(entry);
        var ai = Assert.IsType<DataDrivenEscortAI>(rig.Escort.AI);
        rig.Session.Clear();
        ai.OnAggro(rig.Player);
        var chat = ParseMonsterChat(Assert.Single(Packets(rig.Session, WorldOpcode.SmsgMessagechat)));
        Assert.Equal(targeted ? rig.Player.Guid.Value : 0ul, chat.Target);
    }

    [Theory]
    [InlineData(1978u)] // npc_deathstalker_erlandAI::WaypointReached: if (!pPlayer) return; (also guards the untargeted lines)
    [InlineData(3465u)] // npc_giltharesAI::WaypointReached: the same guard
    public void PlayerGuardedEscorts_SayNothingAtTheirPoints_WithoutTheEscortPlayer(uint entry)
    {
        using Rig rig = Setup(entry);
        EscortSpec spec = EscortSpecCatalog.Find(entry)!;
        Assert.True(spec.RequirePlayerAtWaypoint);
        var ai = Assert.IsType<DataDrivenEscortAI>(rig.Escort.AI);
        ai.MaxPlayerDistance = 0; // keep walking with the player gone, so only the waypoint guard is under test
        ai.OnQuestAccept(rig.Player, spec.QuestId);
        rig.Map.RemovePlayer(rig.Player);
        (Player bystander, FakeSession watcher) = AddPlayer(rig.World, 2, rig.Escort.X, rig.Escort.Y);
        for (int elapsed = 0; elapsed < 900_000 && ai.CurrentWaypointIndex < ai.WaypointCount - 1; elapsed += 100)
        {
            bystander.Relocate(rig.Escort.X, rig.Escort.Y, rig.Escort.Z, 0, 0); // within earshot of every point
            rig.World.RunTick(100);
        }

        Assert.True(ai.CurrentWaypointIndex >= ai.WaypointCount - 1, $"stopped at index {ai.CurrentWaypointIndex}");
        Assert.Empty(Packets(watcher, WorldOpcode.SmsgMessagechat));
        Assert.Empty(rig.Quests.Completed);
    }

    [Fact]
    public void Erland_SayNearby_IsNotAnsweredByADeadRaneOrQuinnWhoseCorpseIsStillThere()
    {
        // silverpine_forest.cpp:80/:93 use GetClosestCreatureWithEntry(m_creature, NPC_RANE/NPC_QUINN, 45.0f), whose onlyAlive defaults
        // to true (sc_grid_searchers.h:37): a corpse in range is passed over and nobody answers.
        using Rig rig = Setup(1978);
        EscortSpec spec = EscortSpecCatalog.Find(1978)!;
        EscortActionSpec[] nearby = [.. spec.Waypoints.SelectMany(p => p.Actions).Where(a => a.Type == "say_nearby")];
        Assert.Equal([1950u, 1951u], nearby.Select(a => a.SpeakerEntry).Order());
        Creature[] speakers = [.. nearby.Select(a => Assert.Single(rig.System.Creatures, c => c.Entry == a.SpeakerEntry))];
        var ai = Assert.IsType<DataDrivenEscortAI>(rig.Escort.AI);
        ai.OnQuestAccept(rig.Player, spec.QuestId);

        var corpseThereOnArrival = new HashSet<uint>();
        for (int elapsed = 0; elapsed < 900_000 && ai.CurrentWaypointIndex < ai.WaypointCount - 1; elapsed += 100)
        {
            rig.Player.Relocate(rig.Escort.X, rig.Escort.Y, rig.Escort.Z, 0, 0);
            foreach (Creature speaker in speakers)
            {
                float dx = speaker.X - rig.Escort.X, dy = speaker.Y - rig.Escort.Y;
                float distSq = (dx * dx) + (dy * dy);
                if (speaker.IsAlive && distSq < 15f * 15f)
                {
                    rig.Map.Combat.Kill(null, speaker); // shortly before Erland reaches the point the speaker stands on
                }
                else if (!speaker.IsAlive && distSq < 2f * 2f && rig.System.Creatures.Contains(speaker))
                {
                    corpseThereOnArrival.Add(speaker.Entry);
                }
            }

            rig.World.RunTick(100);
        }

        Assert.True(ai.CurrentWaypointIndex >= ai.WaypointCount - 1, $"stopped at index {ai.CurrentWaypointIndex}");
        // Without the corpses in range this would pass whatever the speaker filter did.
        Assert.Equal([1950u, 1951u], corpseThereOnArrival.Order());
        var spokenIds = Packets(rig.Session, WorldOpcode.SmsgMessagechat).Select(chat => ParseMonsterChat(chat).Message).ToHashSet();
        Assert.Contains(spec.Waypoints.Single(w => w.Point == 16).Actions.Single().Id.ToString(CultureInfo.InvariantCulture), spokenIds);
        foreach (EscortActionSpec action in nearby)
        {
            Assert.DoesNotContain(action.Id.ToString(CultureInfo.InvariantCulture), spokenIds);
        }
    }

    [Fact]
    public void Gilthares_StandsUpOnQuestAccept_EvenWhenTheEscortDoesNotStart()
    {
        // the_barrens.cpp:132-138: SetStandState(UNIT_STAND_STATE_STAND) sits in QuestAccept before Start, not in JustStartedEscort.
        using Rig rig = Setup(3465);
        var ai = Assert.IsType<DataDrivenEscortAI>(rig.Escort.AI);
        rig.Escort.StandState = StandState.Sit;
        rig.Map.Combat.SetInCombatState(rig.Escort, 60_000); // npc_escortAI::Start refuses an escort in combat
        Assert.True(rig.Escort.Combat.IsInCombat);

        ai.OnQuestAccept(rig.Player, 898);

        Assert.False(ai.HasEscortState(EscortAI.EscortState.Escorting));
        Assert.Equal(StandState.Stand, rig.Escort.StandState);
        Assert.Equal(232u, rig.Escort.FactionTemplate);
    }

    [Theory]
    [InlineData("\"combatSpells\": [{ \"spell\": 0, \"repeatMs\": 1000 }],", "")]
    [InlineData("\"combatSpells\": [{ \"spell\": 5, \"repeatMs\": 0 }],", "")]
    [InlineData("", "{ \"type\": \"summon\", \"id\": 3, \"despawnMs\": 1, \"summonSay\": -1, \"positions\": [[0,0,0,0],[1,1,1,1]] },")]
    [InlineData("", "{ \"type\": \"say\", \"id\": -1, \"summonSay\": -1 },")]
    public void TheCatalog_RefusesBadCombatSpellsAndSummonSays(string specField, string action)
    {
        string json = "[{ \"entry\": 1, \"questId\": 2, \"source\": \"x\", " + specField
            + " \"waypoints\": [{ \"point\": 1, \"actions\": [" + action + "{ \"type\": \"quest_complete\", \"id\": 2 }] }] }]";
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.Throws<InvalidOperationException>(() => EscortSpecCatalog.Parse(stream));
    }

    [Fact]
    public void Oox17_DespawnsItsAmbushersWhenItDies_AndTheBanditAnswers()
    {
        // npc_oox17tnAI::JustDied (tanaris.cpp): ForcedDespawn every summon; point 29's last Scofflaw says SAY_OOX17_AMBUSH_REPLY.
        using Rig rig = Setup(7784);
        var ai = Assert.IsType<DataDrivenEscortAI>(rig.Escort.AI);
        ai.OnQuestAccept(rig.Player, 648);
        for (int elapsed = 0; elapsed < 2_400_000 && ai.Summons.Count < 3; elapsed += 100)
        {
            rig.Player.Relocate(rig.Escort.X, rig.Escort.Y, rig.Escort.Z, 0, 0);
            rig.World.RunTick(100);
        }

        Assert.Equal(3, ai.Summons.Count);
        Creature[] ambush = [.. ai.Summons];
        Assert.All(ambush, c => Assert.Contains(c, rig.System.Creatures));
        rig.Map.Combat.Kill(null, rig.Escort);
        rig.World.RunTick(100);
        Assert.All(ambush, c => Assert.DoesNotContain(c, rig.System.Creatures));
        Assert.Contains(EscortSpecCatalog.Find(7784)!.Waypoints.Single(w => w.Point == 29).Actions, a => a.SummonSay == -1000291);
    }

    [Fact]
    public void Torek_RunsFromTheStart_AndCarriesRendAndThunderclap()
    {
        EscortSpec spec = EscortSpecCatalog.Find(12858)!;
        Assert.True(spec.StartRun);
        Assert.Equal([(11977u, false, 5000u, 20000u), (8078u, true, 8000u, 30000u)],
            spec.CombatSpells.Select(c => (c.Spell, c.Self, c.InitialMs, c.RepeatMs)));
    }

    [Fact]
    public void TheCatalog_RefusesAStartTextBeforeAFactionThatComesAfterStart()
    {
        const string Json = """
            [{ "entry": 1, "questId": 2, "source": "x", "faction": 10, "startText": -1, "factionAfterStart": true, "startTextBeforeFaction": true,
               "waypoints": [{ "point": 1, "actions": [{ "type": "quest_complete", "id": 2 }] }] }]
            """;
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Json));
        Assert.Throws<InvalidOperationException>(() => EscortSpecCatalog.Parse(stream));
    }

    [Fact]
    public void TheCatalog_RefusesAMisspeltField_RatherThanLoadingItsDefault()
    {
        const string Json = """
            [{ "entry": 1, "questId": 2, "source": "x", "requirePlayerAtWayPoints": true,
               "waypoints": [{ "point": 1, "actions": [{ "type": "quest_complete", "id": 2 }] }] }]
            """;
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Json));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => EscortSpecCatalog.Parse(stream));
    }

    [Fact]
    public void TheCatalog_RefusesAnUnknownReactState()
    {
        const string Json = """
            [{ "entry": 1, "questId": 2, "source": "x", "reactState": 7,
               "waypoints": [{ "point": 1, "actions": [{ "type": "quest_complete", "id": 2 }] }] }]
            """;
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Json));
        Assert.Throws<InvalidOperationException>(() => EscortSpecCatalog.Parse(stream));
    }

    [Fact]
    public void Therylune_StillRunsAtPoint20_WithoutTheEscortPlayer_ButDoesNotSpeak()
    {
        // npc_theryluneAI::WaypointReached case 20: the text only for a present player, SetRun() regardless.
        using Rig rig = Setup(3584);
        var ai = Assert.IsType<DataDrivenEscortAI>(rig.Escort.AI);
        ai.MaxPlayerDistance = 0;
        ai.OnQuestAccept(rig.Player, 945);
        rig.Map.RemovePlayer(rig.Player);
        (Player bystander, FakeSession watcher) = AddPlayer(rig.World, 2, rig.Escort.X, rig.Escort.Y);
        for (int elapsed = 0; elapsed < 900_000 && !ai.IsRunning; elapsed += 100)
        {
            bystander.Relocate(rig.Escort.X, rig.Escort.Y, rig.Escort.Z, 0, 0);
            rig.World.RunTick(100);
        }

        Assert.True(ai.IsRunning);
        Assert.Empty(Packets(watcher, WorldOpcode.SmsgMessagechat));
    }

    [Fact]
    public void AMe01_StartsFromDead_WithHerTeamFaction_AndCompletesAtPoint38()
    {
        // npc_ame01AI / QuestAccept_npc_ame01 (ungoro_crater.cpp): dead until accepted, team passive faction, credit at point 38.
        const uint entry = ArcaneCore.Game.Creatures.Scripts.AMe01AI.Entry;
        IReadOnlyList<CreatureWaypoint> path = RealPath(entry);
        Assert.Equal(40, path.Count);
        CreatureWaypoint first = path[0];
        int[] texts = [-1000446, -1000447, -1000448, -1000449, -1000450, -1000451];
        var aiContent = new CreatureAiContent([], [.. texts.Select(id => new CreatureAiText(id, id.ToString(CultureInfo.InvariantCulture), 0, 0, 0))]);
        CreatureContent content = new(
            [Template(entry, b => { b.NpcFlags = (uint)NpcFlags.QuestGiver; b.UnitFlags = (uint)UnitFlags.ImmuneToNpc; })],
            [Spawn(1, entry, first.X, first.Y, first.Z)], [], [], [], aiContent, scriptWaypoints: path.Select(p => (entry, 0u, p)));
        var quests = new EscortQuests();
        (WorldRuntime world, Map _, CreatureMapSystem system) = CreateAiSystem(content,
            new CreatureAiServices { ScriptQuests = quests, QuestEvents = quests });
        using (world)
        {
            (Player player, FakeSession _) = AddPlayer(world, 1, first.X, first.Y);
            Creature ame = Assert.Single(system.Creatures, c => c.Entry == entry);
            var ai = Assert.IsType<ArcaneCore.Game.Creatures.Scripts.AMe01AI>(ame.AI);
            Assert.Equal(StandState.Dead, ame.StandState);

            ai.OnQuestAccept(player, ArcaneCore.Game.Creatures.Scripts.AMe01AI.QuestChasingAMe);
            Assert.Equal(StandState.Stand, ame.StandState);
            Assert.Equal(player.Team == Team.Alliance ? 774u : 775u, ame.FactionTemplate);
            Assert.Equal(UnitFlags.None, ame.UnitFlags & UnitFlags.ImmuneToNpc);
            for (int elapsed = 0; elapsed < 900_000 && quests.Completed.Count == 0; elapsed += 100)
            {
                player.Relocate(ame.X, ame.Y, ame.Z, 0, 0);
                world.RunTick(100);
            }

            Assert.Equal([(player, ArcaneCore.Game.Creatures.Scripts.AMe01AI.QuestChasingAMe)], quests.Completed);
            CreatureWaypoint end = Assert.Single(path, p => p.Point == 38);
            Assert.InRange(MathF.Abs(ame.X - end.X), 0f, 2f);
        }
    }

    [Fact]
    public void Rinji_AmbushedTwice_CompletesAt18_ThenSaysHerTwoClosingLines()
    {
        // npc_rinjiAI (hinterlands.cpp): ranger + two outrunners at 8 and 14, credit at 18, progress lines 3 s apart.
        const uint entry = ArcaneCore.Game.Creatures.Scripts.RinjiAI.Entry;
        IReadOnlyList<CreatureWaypoint> path = RealPath(entry);
        CreatureWaypoint first = path[0];
        int[] texts = [-1000403, -1000404, -1000405, -1000406, -1000407, -1000408, -1000409];
        var aiContent = new CreatureAiContent([], [.. texts.Select(id => new CreatureAiText(id, id.ToString(CultureInfo.InvariantCulture), 0, 0, 0))]);
        CreatureContent content = new(
            [Template(entry, b => { b.NpcFlags = (uint)NpcFlags.QuestGiver; b.UnitFlags = (uint)UnitFlags.ImmuneToNpc; }), Template(2694), Template(2691)],
            [Spawn(1, entry, first.X, first.Y, first.Z)], [], [], [], aiContent, scriptWaypoints: path.Select(p => (entry, 0u, p)));
        var quests = new EscortQuests();
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content,
            new CreatureAiServices { ScriptQuests = quests, QuestEvents = quests });
        using (world)
        {
            (Player player, FakeSession session) = AddPlayer(world, 1, first.X, first.Y);
            Creature rinji = Assert.Single(system.Creatures, c => c.Entry == entry);
            var ai = Assert.IsType<ArcaneCore.Game.Creatures.Scripts.RinjiAI>(rinji.AI);
            ai.OnQuestAccept(player, ArcaneCore.Game.Creatures.Scripts.RinjiAI.QuestRinjiTrapped);
            Assert.True(ai.HasEscortState(EscortAI.EscortState.Escorting));
            Assert.Equal(UnitFlags.None, rinji.UnitFlags & UnitFlags.ImmuneToNpc);
            for (int elapsed = 0; elapsed < 2_400_000 && quests.Completed.Count == 0; elapsed += 100)
            {
                player.Relocate(rinji.X, rinji.Y, rinji.Z, 0, 0);
                world.RunTick(100);
                foreach (Creature ambusher in ai.Summoned.Where(c => c.IsAlive))
                {
                    map.Combat.Kill(null, ambusher);
                }
            }

            Assert.Equal([(player, ArcaneCore.Game.Creatures.Scripts.RinjiAI.QuestRinjiTrapped)], quests.Completed);
            Assert.Equal(6, ai.Summoned.Count);
            Assert.Equal([2694u, 2691u, 2691u, 2694u, 2691u, 2691u], ai.Summoned.Select(c => c.Entry));
            for (int i = 0; i < 80; i++)
            {
                player.Relocate(rinji.X, rinji.Y, rinji.Z, 0, 0);
                world.RunTick(100);
            }

            string[] said = [.. Packets(session, WorldOpcode.SmsgMessagechat).Select(p => ParseMonsterChat(p).Message)];
            Assert.Contains("-1000407", said);
            Assert.Contains("-1000408", said);
            Assert.Contains("-1000409", said);
        }
    }

    [Fact]
    public void Muglash_WaitsAtTheBrazier_ThenTwoWavesAndVorsha_ThenCredit()
    {
        // npc_muglashAI + GOUse_go_naga_brazier (ashenvale.cpp at e27966cec7): pause at 25, waves 10 s apart once the brazier is out.
        const uint entry = ArcaneCore.Game.Creatures.Scripts.MuglashAI.Entry;
        IReadOnlyList<CreatureWaypoint> path = RealPath(entry);
        CreatureWaypoint first = path[0];
        CreatureWaypoint brazierPoint = Assert.Single(path, p => p.Point == 25);
        int[] texts = [-1000501, -1000502, -1000503, -1000504, -1000505, -1000507, -1000508, -1000509, -1000510];
        uint[] naga = [3713, 3717, 3712, 3944, 3711, 3715, 12940];
        var aiContent = new CreatureAiContent([], [.. texts.Select(id => new CreatureAiText(id, id.ToString(CultureInfo.InvariantCulture), 0, 0, 0))]);
        CreatureContent content = new(
            [Template(entry, b => b.NpcFlags = (uint)NpcFlags.QuestGiver), .. naga.Select(e => Template(e))],
            [Spawn(1, entry, first.X, first.Y, first.Z)], [], [], [], aiContent, scriptWaypoints: path.Select(p => (entry, 0u, p)));
        var quests = new EscortQuests();
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content,
            new CreatureAiServices { ScriptQuests = quests, QuestEvents = quests });
        using (world)
        {
            var objects = new ArcaneCore.Game.GameObjects.GameObjectMapSystem(map, new ArcaneCore.Kernel.WorldData.GameObjects.GameObjectContent(
                [ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit.GoTemplate(178247, ArcaneCore.Game.GameObjects.GameObjectType.Goober)],
                [], [], [], []));
            map.AddUpdater(objects);
            Assert.NotNull(objects.Summon(178247, brazierPoint.X + 2, brazierPoint.Y, brazierPoint.Z, 0f));
            var brazier = Assert.Single(objects.GameObjects);
            brazier.Flags |= ArcaneCore.Game.GameObjects.GameObjectFlags.NoInteract;

            (Player player, FakeSession _) = AddPlayer(world, 1, first.X, first.Y);
            Creature muglash = Assert.Single(system.Creatures, c => c.Entry == entry);
            var ai = Assert.IsType<ArcaneCore.Game.Creatures.Scripts.MuglashAI>(muglash.AI);
            ai.OnQuestAccept(player, ArcaneCore.Game.Creatures.Scripts.MuglashAI.QuestVorsha);
            Assert.Equal(775u, muglash.FactionTemplate);
            for (int elapsed = 0; elapsed < 2_400_000 && !ai.HasEscortState(EscortAI.EscortState.Paused); elapsed += 100)
            {
                player.Relocate(muglash.X, muglash.Y, muglash.Z, 0, 0);
                world.RunTick(100);
            }

            Assert.True(ai.HasEscortState(EscortAI.EscortState.Paused));
            Assert.Equal(ArcaneCore.Game.GameObjects.GameObjectFlags.None, brazier.Flags & ArcaneCore.Game.GameObjects.GameObjectFlags.NoInteract);
            world.RunTick(20_000);
            Assert.Empty(ai.Summoned); // nothing happens until the brazier is put out
            player.Relocate(brazier.X, brazier.Y, brazier.Z, 0, 0);
            objects.Use(player, brazier.Guid);
            Assert.True(ai.BrazierExtinguished);
            for (int elapsed = 0; elapsed < 600_000 && quests.Completed.Count == 0; elapsed += 100)
            {
                world.RunTick(100);
                foreach (Creature summoned in ai.Summoned.Where(c => c.IsAlive))
                {
                    map.Combat.Kill(null, summoned);
                }

                player.Relocate(muglash.X, muglash.Y, muglash.Z, 0, 0);
            }

            Assert.Equal(naga, ai.Summoned.Select(c => c.Entry));
            Assert.Equal([(player, ArcaneCore.Game.Creatures.Scripts.MuglashAI.QuestVorsha)], quests.Completed);
        }
    }

    [Theory]
    [InlineData(994u, 15u)]
    [InlineData(995u, 24u)]
    public void Volcor_ForceFightsOutToPoint15_StealthWalksPoints16To24(uint quest, uint creditPoint)
    {
        // npc_volcorAI (darkshore.cpp at d6d00d8a46): force = points 1-15 with ambushes, stealth = run from point 16 to 24, friendly.
        const uint entry = ArcaneCore.Game.Creatures.Scripts.VolcorAI.Entry;
        IReadOnlyList<CreatureWaypoint> path = RealPath(entry);
        Assert.Equal(24, path.Count);
        CreatureWaypoint first = path[0];
        int[] texts = [-1000789, -1000790, -1000791, -1000792, -1000793, -1000794, -1000195];
        var aiContent = new CreatureAiContent([], [.. texts.Select(id => new CreatureAiText(id, id.ToString(CultureInfo.InvariantCulture), 0, 0, 0))]);
        CreatureContent content = new(
            [Template(entry, b => b.NpcFlags = (uint)NpcFlags.QuestGiver), Template(2171), Template(2170)],
            [Spawn(1, entry, first.X, first.Y, first.Z)], [], [], [], aiContent, scriptWaypoints: path.Select(p => (entry, 0u, p)));
        var quests = new EscortQuests();
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content,
            new CreatureAiServices { ScriptQuests = quests, QuestEvents = quests });
        using (world)
        {
            (Player player, FakeSession _) = AddPlayer(world, 1, first.X, first.Y);
            Creature volcor = Assert.Single(system.Creatures, c => c.Entry == entry);
            var ai = Assert.IsType<ArcaneCore.Game.Creatures.Scripts.VolcorAI>(volcor.AI);
            ai.OnQuestAccept(player, quest);
            for (int elapsed = 0; elapsed < 2_400_000 && quests.Completed.Count == 0; elapsed += 100)
            {
                player.Relocate(volcor.X, volcor.Y, volcor.Z, 0, 0);
                world.RunTick(100);
                foreach (Creature summoned in ai.Summoned.Where(c => c.IsAlive))
                {
                    map.Combat.Kill(null, summoned);
                }
            }

            Assert.Equal([(player, quest)], quests.Completed);
            CreatureWaypoint credit = Assert.Single(path, p => p.Point == creditPoint);
            Assert.InRange(MathF.Abs(volcor.X - credit.X), 0f, 2f);
            if (quest == 994)
            {
                Assert.Equal(8, ai.Summoned.Count); // 2 at 5, 4 at 11 (the source falls through into 13), 2 at 13
            }
            else
            {
                Assert.Empty(ai.Summoned);
                Assert.Equal(35u, volcor.FactionTemplate);
            }
        }
    }

    [Fact]
    public void Bartleby_TurnsHostileOnAccept_AndAt15PercentGivesCreditAndEvades()
    {
        // npc_bartlebyAI / QuestAccept_npc_bartleby (stormwind_city.cpp).
        const uint entry = ArcaneCore.Game.Creatures.Scripts.BartlebyAI.Entry;
        CreatureContent content = new([Template(entry, b => b.NpcFlags = (uint)NpcFlags.QuestGiver)], [Spawn(1, entry, 0, 0, 0)], [], [], [],
            new CreatureAiContent([], []));
        var quests = new EscortQuests();
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content,
            new CreatureAiServices { ScriptQuests = quests, QuestEvents = quests });
        using (world)
        {
            (Player player, FakeSession _) = AddPlayer(world, 1, 1, 0);
            Creature bartleby = Assert.Single(system.Creatures);
            var ai = Assert.IsType<ArcaneCore.Game.Creatures.Scripts.BartlebyAI>(bartleby.AI);
            uint faction = bartleby.FactionTemplate;
            ai.OnQuestAccept(player, ArcaneCore.Game.Creatures.Scripts.BartlebyAI.QuestBeat);
            Assert.Equal(168u, bartleby.FactionTemplate);
            Assert.Same(player, bartleby.Combat.Victim);
            map.Combat.DealDamage(player, bartleby, bartleby.Health); // a lethal hit
            Assert.True(bartleby.IsAlive);
            world.RunTick(100);
            Assert.Equal([(player, ArcaneCore.Game.Creatures.Scripts.BartlebyAI.QuestBeat)], quests.Completed);
            world.RunTick(100);
            Assert.False(bartleby.Combat.IsInCombat);
            Assert.Equal(faction, bartleby.FactionTemplate);
        }
    }

    [Fact]
    public void Dashel_AttacksWithTwoThugsAfterThreeSeconds_GivesUpAt15Percent_AndCreditsFiveSecondsLater()
    {
        // npc_dashel_stonefistAI (stormwind_city.cpp at 3e8597afe7).
        const uint entry = ArcaneCore.Game.Creatures.Scripts.DashelStonefistAI.Entry;
        int[] texts = [-1001274, -1001275, -1001276];
        CreatureContent content = new([Template(entry, b => b.NpcFlags = (uint)NpcFlags.QuestGiver), Template(4969)], [Spawn(1, entry, 0, 0, 0)],
            [], [], [], new CreatureAiContent([], [.. texts.Select(id => new CreatureAiText(id, id.ToString(CultureInfo.InvariantCulture), 0, 0, 0))]));
        var quests = new EscortQuests();
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content,
            new CreatureAiServices { ScriptQuests = quests, QuestEvents = quests });
        using (world)
        {
            (Player player, FakeSession _) = AddPlayer(world, 1, 1, 0);
            Creature dashel = Assert.Single(system.Creatures);
            var ai = Assert.IsType<ArcaneCore.Game.Creatures.Scripts.DashelStonefistAI>(dashel.AI);
            Assert.Equal(CreatureReactState.Passive, dashel.ReactState);
            ai.OnQuestAccept(player, ArcaneCore.Game.Creatures.Scripts.DashelStonefistAI.QuestMissingDiploPt8);
            Assert.Equal(168u, dashel.FactionTemplate);
            Assert.Equal(0u, dashel.NpcFlags & (uint)NpcFlags.QuestGiver);
            world.RunTick(2900);
            Assert.Empty(ai.Thugs);
            world.RunTick(200);
            Assert.Equal(2, ai.Thugs.Count);
            Assert.Same(player, dashel.Combat.Victim);
            map.Combat.DealDamage(player, dashel, dashel.Health);
            Assert.True(dashel.IsAlive);
            world.RunTick(100);
            Assert.Empty(quests.Completed);
            Assert.Equal(CreatureReactState.Passive, dashel.ReactState);
            world.RunTick(5000);
            world.RunTick(100);
            Assert.Equal([(player, ArcaneCore.Game.Creatures.Scripts.DashelStonefistAI.QuestMissingDiploPt8)], quests.Completed);
        }
    }

    [Fact]
    public void SquireRowe_SignalsWindsor_WhoDismountsWelcomesAndBecomesAQuestGiver()
    {
        // npc_squire_roweAI (stormwind_city.cpp at 3e8597afe7) on the z2815 path (7 points).
        const uint entry = ArcaneCore.Game.Creatures.Scripts.SquireRoweAI.Entry;
        IReadOnlyList<CreatureWaypoint> path = RealPath(entry);
        Assert.Equal(7, path.Count);
        CreatureWaypoint first = path[0];
        int[] texts = [-1000822, -1000823, -1000824];
        CreatureContent content = new([Template(entry), Template(12580)], [Spawn(1, entry, first.X, first.Y, first.Z)], [], [], [],
            new CreatureAiContent([], [.. texts.Select(id => new CreatureAiText(id, id.ToString(CultureInfo.InvariantCulture), 0, 0, 0))]),
            scriptWaypoints: path.Select(p => (entry, 0u, p)));
        (WorldRuntime world, Map _, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices());
        using (world)
        {
            (Player player, FakeSession session) = AddPlayer(world, 1, first.X, first.Y);
            Creature rowe = Assert.Single(system.Creatures);
            var ai = Assert.IsType<ArcaneCore.Game.Creatures.Scripts.SquireRoweAI>(rowe.AI);
            Assert.True(ai.StartFromGossip(player));
            for (int elapsed = 0; elapsed < 300_000 && (ai.Windsor is null || (ai.Windsor.NpcFlags & (uint)NpcFlags.QuestGiver) == 0); elapsed += 100)
            {
                player.Relocate(rowe.X, rowe.Y, rowe.Z, 0, 0);
                world.RunTick(100);
            }

            Creature windsor = Assert.IsType<Creature>(ai.Windsor);
            Assert.True(ai.EventInProgress, $"windsor at {windsor.X},{windsor.Y} paused {ai.HasEscortState(EscortAI.EscortState.Paused)} rowe {rowe.X},{rowe.Y} motion {windsor.Motion.CurrentType}");
            Assert.NotEqual(0u, windsor.NpcFlags & (uint)NpcFlags.QuestGiver);
            Assert.False(ai.HasEscortState(EscortAI.EscortState.Paused));
            string[] said = [.. Packets(session, WorldOpcode.SmsgMessagechat).Select(p => ParseMonsterChat(p).Message)];
            Assert.Equal(["-1000822", "-1000823", "-1000824"], said);
        }
    }

    [Fact]
    public void Windsor_AcceptPlaysHisLines_TheGateScene_ThenWaitsForTheWordToEnterTheKeep()
    {
        // npc_reginald_windsorAI (stormwind_city.cpp at 3e8597afe7) on the z2815 path (27 points).
        const uint entry = ArcaneCore.Game.Creatures.Scripts.ReginaldWindsorAI.Entry;
        IReadOnlyList<CreatureWaypoint> path = RealPath(entry);
        Assert.Equal(27, path.Count);
        CreatureWaypoint first = path[0];
        int[] texts = [.. Enumerable.Range(825, 50).Select(i => -1000000 - i)];
        CreatureWaypoint throne = Assert.Single(path, p => p.Point == 26);
        CreatureContent content = new([Template(entry), Template(466), Template(1749), Template(1756), Template(1747), Template(1748), Template(12739)],
            [Spawn(1, entry, first.X, first.Y, first.Z), Spawn(2, 466, path[1].X + 5, path[1].Y, path[1].Z),
             Spawn(3, 1749, first.X - 5, first.Y, first.Z), Spawn(4, 1747, first.X - 32, first.Y, first.Z),
             Spawn(5, 1748, first.X - 34, first.Y, first.Z), Spawn(6, 1756, first.X - 36, first.Y, first.Z, respawnSeconds: 3600),
             Spawn(7, 1756, first.X - 38, first.Y, first.Z, respawnSeconds: 3600)], [], [], [],
            new CreatureAiContent([], [.. texts.Select(id => new CreatureAiText(id, id.ToString(CultureInfo.InvariantCulture), 0, 0, 0))]),
            scriptWaypoints: path.Select(p => (entry, 0u, p)));
        var quests = new EscortQuests();
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content,
            new CreatureAiServices { ScriptQuests = quests, QuestEvents = quests });
        using (world)
        {
            (Player player, FakeSession session) = AddPlayer(world, 1, first.X, first.Y);
            Creature windsor = Assert.Single(system.Creatures, c => c.Entry == entry);
            Creature[] throneGuards = [.. system.Creatures.Where(c => c.Entry == 1756)];
            var ai = Assert.IsType<ArcaneCore.Game.Creatures.Scripts.ReginaldWindsorAI>(windsor.AI);
            ai.OnQuestAccept(player, ArcaneCore.Game.Creatures.Scripts.ReginaldWindsorAI.QuestTheGreatMasquerade);
            int guards = 0; // the gate guards are timed summons (180 s), gone before the keep
            for (int elapsed = 0; elapsed < 1_200_000 && !ai.KeepEventReady; elapsed += 100)
            {
                player.Relocate(windsor.X, windsor.Y, windsor.Z, 0, 0);
                world.RunTick(100);
                guards = Math.Max(guards, system.Creatures.Count(c => c.Entry == 1756));
            }

            Assert.True(ai.KeepEventReady, $"point {ai.CurrentWaypointIndex} step {ai.DialogueStep} state paused {ai.HasEscortState(EscortAI.EscortState.Paused)}");
            Assert.True(ai.HasEscortState(EscortAI.EscortState.Paused));
            Assert.Equal(8, guards); // six gate summons plus the two throne room guards (spawned near the start: their real spot is far off)
            Assert.NotEqual(0u, windsor.NpcFlags & (uint)NpcFlags.Gossip);
            string[] said = [.. Packets(session, WorldOpcode.SmsgMessagechat).Select(p => ParseMonsterChat(p).Message)];
            Assert.Contains("-1000825", said);
            Assert.Contains("-1000841", said); // the gate scene
            Assert.Contains("-1000849", said); // before the keep

            // The throne room cast stands by (their spawn is far off): Prestor, Wrynn, Bolvar and two royal guards.
            (uint Entry, float Dx, float Dy)[] cast = [(1749, 4, 0), (1747, 6, 2), (1748, 3, -3)];
            foreach ((uint castEntry, float dx, float dy) in cast)
            {
                system.NearTeleport(Assert.Single(system.Creatures, c => c.Entry == castEntry), throne.X + dx, throne.Y + dy, throne.Z, 0f);
            }

            system.NearTeleport(throneGuards[0], throne.X + 8, throne.Y + 4, throne.Z, 0f);
            system.NearTeleport(throneGuards[1], throne.X + 8, throne.Y, throne.Z, 0f);
            ai.StartKeepEvent();
            for (int elapsed = 0; elapsed < 30_000 && ai.HasEscortState(EscortAI.EscortState.Paused); elapsed += 100)
            {
                world.RunTick(100);
            }

            Assert.False(ai.HasEscortState(EscortAI.EscortState.Paused));
            Assert.Equal(0u, windsor.NpcFlags & (uint)NpcFlags.Gossip);

            // The throne room: Prestor shows herself, the guards turn and fall, Bolvar kneels and the quest is done.
            Creature bolvar = Assert.Single(system.Creatures, c => c.Entry == 1748);
            string[] all0() => [.. Packets(session, WorldOpcode.SmsgMessagechat).Select(p => ParseMonsterChat(p).Message)];
            for (int elapsed = 0; elapsed < 600_000 && quests.Completed.Count == 0; elapsed += 100)
            {
                player.Relocate(windsor.X, windsor.Y, windsor.Z, 0, 0);
                world.RunTick(100);
                // Players kill the guards after Onyxia has gone (a kill in the first seconds cuts her last line, as in the source).
                if (!all0().Contains("-1000868"))
                {
                    continue;
                }

                foreach (Creature turned in throneGuards.Where(g => g.IsAlive && g.Entry == 12739))
                {
                    map.Combat.Kill(null, turned);
                }
            }

            string[] all = [.. Packets(session, WorldOpcode.SmsgMessagechat).Select(p => ParseMonsterChat(p).Message)];
            Assert.True(quests.Completed.Count == 1, $"point {ai.CurrentWaypointIndex} step {ai.DialogueStep} alive {windsor.IsAlive} bolvar {bolvar.IsAlive} royal {string.Join(" ", ai.RoyalGuards.Select(g => $"{g.Entry}/{g.IsAlive}/{throneGuards.Contains(g)}"))} said {string.Join(' ', all)}");
            Assert.Equal((player, ArcaneCore.Game.Creatures.Scripts.ReginaldWindsorAI.QuestTheGreatMasquerade), quests.Completed[0]);
            Assert.All(throneGuards, g => Assert.Equal(12739u, g.Entry));
            Assert.Contains("-1000827", all); // SAY_PRESTOR_SIEZE at the start
            Assert.Contains("-1000868", all); // SAY_PRESTOR_KEEP_14
            Assert.Contains("-1000870", all); // SAY_WINDSOR_KEEP_16, with the credit
            Assert.Equal(StandState.Dead, windsor.StandState);
            Assert.Equal(StandState.Kneel, bolvar.StandState);
            Assert.Equal(0u, bolvar.NpcFlags & (uint)NpcFlags.QuestGiver); // reset comes with the next step
        }
    }

    [Fact]
    public void InDreams_TaelanRidesOut_IsillienStrikesHimDown_TirionAvengesHim_AndTheEpilogueCredits()
    {
        // npc_taelan_fordringAI / npc_isillienAI / npc_tirion_fordringAI (western_plaguelands.cpp at 3e8597afe7) on the z2815 paths.
        const uint entry = ArcaneCore.Game.Creatures.Scripts.TaelanFordringAI.Entry;
        IReadOnlyList<CreatureWaypoint> path = RealPath(entry);
        Assert.Equal(57, path.Count);
        CreatureWaypoint first = path[0];
        int[] texts = [.. Enumerable.Range(1078, 28).Select(i => -1000000 - i)];
        CreatureContent content = new([Template(entry), Template(1840, t => t.Faction = 14), Template(12126), Template(12128, t => t.Faction = 14)],
            [Spawn(1, entry, first.X, first.Y, first.Z)], [], [], [],
            new CreatureAiContent([], [.. texts.Select(id => new CreatureAiText(id, id.ToString(CultureInfo.InvariantCulture), 0, 0, 0))]),
            scriptWaypoints: new[] { entry, 1840u, 12126u }.SelectMany(e => RealPath(e).Select(p => (e, 0u, p))));
        var quests = new EscortQuests();
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content,
            new CreatureAiServices { ScriptQuests = quests, QuestEvents = quests });
        using (world)
        {
            (Player player, FakeSession session) = AddPlayer(world, 1, first.X, first.Y);
            Creature taelan = Assert.Single(system.Creatures, c => c.Entry == entry);
            var ai = Assert.IsType<ArcaneCore.Game.Creatures.Scripts.TaelanFordringAI>(taelan.AI);
            world.RunTick(100); // the player sees Taelan before he speaks
            ai.OnQuestAccept(player, ArcaneCore.Game.Creatures.Scripts.TaelanFordringAI.QuestInDreams);
            Assert.Equal(ArcaneCore.Game.Creatures.Scripts.TaelanFordringAI.FactionEscortNeutralFriendPassive, taelan.FactionTemplate);
            taelan.FactionTemplate = 35; // the test faction table has no 290; 35 is hostile to the Scarlets' 14 here as 290 is to 67 live
            string[] Said() => [.. Packets(session, WorldOpcode.SmsgMessagechat).Select(p => ParseMonsterChat(p).Message)];
            bool hurt = false, isillienKilled = false;
            for (int elapsed = 0; elapsed < 2_400_000 && quests.Completed.Count == 0; elapsed += 100)
            {
                player.Relocate(taelan.X, taelan.Y, taelan.Z, 0, 0);
                world.RunTick(100);
                string[] said = Said();
                if (!hurt && ai.Isillien is not null && said.Contains("-1001089"))
                {
                    taelan.Health = taelan.MaxHealth / 3; // Isillien's blows, below half
                    hurt = true;
                }

                if (!isillienKilled && said.Contains("-1001098") && ai.Isillien is { IsAlive: true } isillien)
                {
                    map.Combat.Kill(ai.Tirion, isillien); // Tirion's work
                    isillienKilled = true;
                }
            }

            string[] all = Said();
            Assert.True(quests.Completed.Count == 1, $"point {ai.CurrentWaypointIndex} step {ai.DialogueStep} tirion {(ai.Tirion is { } tt ? $"{tt.IsInWorld} {tt.X},{tt.Y} motion {tt.Motion.CurrentType} d {MathF.Sqrt(((tt.X - taelan.X) * (tt.X - taelan.X)) + ((tt.Y - taelan.Y) * (tt.Y - taelan.Y)))}" : "none")} dead {ai.TaelanDead} tevade {taelan.IsEvading} tworld {taelan.IsInWorld} iworld {ai.Isillien?.IsInWorld} hp {taelan.Health}/{taelan.MaxHealth} tflags {taelan.UnitFlags} iflags {ai.Isillien?.UnitFlags} ihp {ai.Isillien?.Health} dist {(ai.Isillien is { } ii ? MathF.Sqrt(((ii.X - taelan.X) * (ii.X - taelan.X)) + ((ii.Y - taelan.Y) * (ii.Y - taelan.Y))) : -1)} said {string.Join(' ', all)}");
            Assert.Equal((player, ArcaneCore.Game.Creatures.Scripts.TaelanFordringAI.QuestInDreams), quests.Completed[0]);
            Assert.True(ai.TaelanDead);
            Assert.Equal(StandState.Dead, taelan.StandState);
            Assert.Contains("-1001090", all); // SAY_KILL_TAELAN_1
            Assert.Contains("-1001094", all); // SAY_TIRION_1
            Assert.Contains("-1001105", all); // SAY_EPILOG_5
            Assert.Equal(5, system.Creatures.Count(c => c.Entry == 12128)); // two elites with Isillien, three more at the fight
            Assert.NotEqual(0u, Assert.IsType<Creature>(ai.Tirion).NpcFlags & (uint)NpcFlags.QuestGiver);
        }
    }

    [Fact]
    public void Melizza_TwoAmbushes_CreditAt12_ThenHerLinesAndHornizzAt19()
    {
        // npc_melizza_brimbuzzleAI (desolace.cpp at 46a0597873) on the z2815 path.
        const uint entry = ArcaneCore.Game.Creatures.Scripts.MelizzaBrimbuzzleAI.Entry;
        IReadOnlyList<CreatureWaypoint> path = RealPath(entry);
        CreatureWaypoint first = path[0];
        CreatureWaypoint last = Assert.Single(path, p => p.Point == 19); // Hornizz waits where her scene ends
        int[] texts = [-1000784, -1000785, -1000786, -1000787, -1000788, -1010030, -1010031];
        CreatureContent content = new(
            [Template(entry, b => b.NpcFlags = (uint)NpcFlags.QuestGiver), Template(4659), Template(4660), Template(4655), Template(6019)],
            [Spawn(1, entry, first.X, first.Y, first.Z), Spawn(2, 6019, first.X + 2, first.Y, first.Z)], [], [], [],
            new CreatureAiContent([], [.. texts.Select(id => new CreatureAiText(id, id.ToString(CultureInfo.InvariantCulture), 0, 0, 0))]),
            scriptWaypoints: path.Select(p => (entry, 0u, p)));
        var quests = new EscortQuests();
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content,
            new CreatureAiServices { ScriptQuests = quests, QuestEvents = quests });
        using (world)
        {
            (Player player, FakeSession session) = AddPlayer(world, 1, first.X, first.Y);
            Creature melizza = Assert.Single(system.Creatures, c => c.Entry == entry);
            var ai = Assert.IsType<ArcaneCore.Game.Creatures.Scripts.MelizzaBrimbuzzleAI>(melizza.AI);
            Creature hornizzSpawn = Assert.Single(system.Creatures, c => c.Entry == 6019);
            ai.OnQuestAccept(player, ArcaneCore.Game.Creatures.Scripts.MelizzaBrimbuzzleAI.QuestGetMeOutOfHere);
            string[] Said() => [.. Packets(session, WorldOpcode.SmsgMessagechat).Select(p => ParseMonsterChat(p).Message)];
            for (int elapsed = 0; elapsed < 2_400_000 && !Said().Contains("-1010031"); elapsed += 100)
            {
                player.Relocate(melizza.X, melizza.Y, melizza.Z, 0, 0);
                system.NearTeleport(hornizzSpawn, melizza.X + 3, melizza.Y, melizza.Z, 0f); // Hornizz stands by (his spawn is far off)
                world.RunTick(100);
                foreach (Creature summoned in ai.Summoned.Where(c => c.IsAlive))
                {
                    map.Combat.Kill(null, summoned);
                }
            }

            Assert.Equal([(player, ArcaneCore.Game.Creatures.Scripts.MelizzaBrimbuzzleAI.QuestGetMeOutOfHere)], quests.Completed);
            Assert.Equal(10, ai.Summoned.Count);
            Creature hornizz = Assert.Single(system.Creatures, c => c.Entry == 6019);
            Assert.True(Said().Contains("-1010031"), $"hornizz alive {hornizz.IsAlive} at {hornizz.X},{hornizz.Y} melizza {melizza.X},{melizza.Y} escorting {ai.HasEscortState(EscortAI.EscortState.Escorting)} wp {ai.CurrentWaypointIndex}");
            Assert.Equal(["-1000784", "-1000785", "-1000786", "-1000787", "-1000788", "-1010030", "-1010031"], Said());
        }
    }

    [Fact]
    public void Eris_ArchersThenWaves_AWaveEndsWhenItsPeasantsAreDone_AndFifteenDeadFails()
    {
        // npc_eris_havenfireAI (eastern_plaguelands.cpp).
        const uint entry = ArcaneCore.Game.Creatures.Scripts.ErisHavenfireAI.Entry;
        int[] texts = [-1000815, -1000816, -1000817, -1000818, -1000819, -1000820, -1000821];
        CreatureContent content = new(
            [Template(entry, b => b.NpcFlags = (uint)NpcFlags.QuestGiver), Template(14484), Template(14485), Template(14489), Template(14486)],
            [Spawn(1, entry, 3340f, -3000f, 162f)], [], [], [],
            new CreatureAiContent([], [.. texts.Select(id => new CreatureAiText(id, id.ToString(CultureInfo.InvariantCulture), 0, 0, 0))]));
        var quests = new EscortQuests();
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content,
            new CreatureAiServices { ScriptQuests = quests, QuestEvents = quests });
        using (world)
        {
            (Player player, FakeSession _) = AddPlayer(world, 1, 3341f, -3000f);
            Creature eris = Assert.Single(system.Creatures);
            var ai = Assert.IsType<ArcaneCore.Game.Creatures.Scripts.ErisHavenfireAI>(eris.AI);
            ai.OnQuestAccept(player, ArcaneCore.Game.Creatures.Scripts.ErisHavenfireAI.QuestBalanceOfLightAndShadow);
            Assert.Equal(0u, eris.NpcFlags & (uint)NpcFlags.QuestGiver);
            world.RunTick(5000);
            world.RunTick(100);
            Assert.Equal(8, ai.Summons.Count(c => c.Entry == 14489));
            world.RunTick(5000);
            world.RunTick(100);
            Creature[] wave1 = [.. ai.Summons.Where(c => c.Entry is 14484 or 14485)];
            Assert.Equal(11, wave1.Length);
            Assert.Equal(1, ai.Wave);
            foreach (Creature peasant in wave1)
            {
                map.Combat.Kill(null, peasant);
            }

            world.RunTick(100);
            Assert.Equal(11, ai.Killed);
            Assert.Equal(2, ai.Wave); // the wave ended: the next one is out
            Creature[] wave2 = [.. ai.Summons.Where(c => c.Entry is 14484 or 14485).Except(wave1)];
            Assert.Equal(12, wave2.Length);
            Assert.Empty(quests.Failed);
            foreach (Creature peasant in wave2.Take(4))
            {
                map.Combat.Kill(null, peasant);
            }

            world.RunTick(100);
            Assert.Equal([(player, ArcaneCore.Game.Creatures.Scripts.ErisHavenfireAI.QuestBalanceOfLightAndShadow)], quests.Failed);
        }
    }

    [Fact]
    public void Ranshalla_WaitsAtEachTorch_ThenTheAltarScene_AndCredit()
    {
        // npc_ranshallaAI (winterspring.cpp at a57aa7f074) on the z2815 path; the torches are lit through ContinueEscort (go_elune_fire).
        const uint entry = ArcaneCore.Game.Creatures.Scripts.RanshallaAI.Entry;
        IReadOnlyList<CreatureWaypoint> path = RealPath(entry);
        CreatureWaypoint first = path[0];
        int[] texts = [.. Enumerable.Range(707, 33).Select(i => -1000000 - i)];
        CreatureContent content = new(
            [Template(entry, b => b.NpcFlags = (uint)NpcFlags.QuestGiver), Template(12116), Template(12152), Template(12140)],
            [Spawn(1, entry, first.X, first.Y, first.Z)], [], [], [],
            new CreatureAiContent([], [.. texts.Select(id => new CreatureAiText(id, id.ToString(CultureInfo.InvariantCulture), 0, 0, 0))]),
            scriptWaypoints: path.Select(p => (entry, 0u, p)));
        var quests = new EscortQuests();
        (WorldRuntime world, Map _, CreatureMapSystem system) = CreateAiSystem(content,
            new CreatureAiServices { ScriptQuests = quests, QuestEvents = quests });
        using (world)
        {
            (Player player, FakeSession _) = AddPlayer(world, 1, first.X, first.Y);
            Creature ranshalla = Assert.Single(system.Creatures, c => c.Entry == entry);
            var ai = Assert.IsType<ArcaneCore.Game.Creatures.Scripts.RanshallaAI>(ranshalla.AI);
            ai.OnQuestAccept(player, ArcaneCore.Game.Creatures.Scripts.RanshallaAI.QuestGuardiansAltar);
            int lit = 0;
            for (int elapsed = 0; elapsed < 3_600_000 && quests.Completed.Count == 0; elapsed += 100)
            {
                player.Relocate(ranshalla.X, ranshalla.Y, ranshalla.Z, 0, 0);
                world.RunTick(100);
                if (ai.HasEscortState(EscortAI.EscortState.Paused) && ai.DialogueStep < 0 && lit < 6)
                {
                    world.RunTick(1000);
                    if (ai.HasEscortState(EscortAI.EscortState.Paused) && ai.DialogueStep < 0)
                    {
                        ai.ContinueEscort(altar: lit == 5);
                        lit++;
                        world.RunTick(2100);
                    }
                }
            }

            Assert.True(quests.Completed.Count == 1, $"lit {lit} step {ai.DialogueStep} wp {ai.CurrentWaypointIndex} paused {ai.HasEscortState(EscortAI.EscortState.Paused)} escorting {ai.HasEscortState(EscortAI.EscortState.Escorting)} summons {string.Join(',', system.Creatures.Select(c => c.Entry + "@" + c.X.ToString("F0", CultureInfo.InvariantCulture) + "," + c.Y.ToString("F0", CultureInfo.InvariantCulture)))}");
            Assert.Equal(6, lit); // five torches and the altar
            Assert.Equal([(player, ArcaneCore.Game.Creatures.Scripts.RanshallaAI.QuestGuardiansAltar)], quests.Completed);
            Assert.Equal(StandState.Kneel, ranshalla.StandState);
        }
    }

    private static IReadOnlyList<CreatureWaypoint> RealPath(uint entry) => File.ReadLines(Path.Combine(AppContext.BaseDirectory, "validated-escort-waypoints.csv"))
        .Where(line => line.StartsWith($"{entry},", StringComparison.Ordinal))
        .Select(line => line.Split(','))
        .Select(parts => new CreatureWaypoint(uint.Parse(parts[2], CultureInfo.InvariantCulture),
            float.Parse(parts[3], CultureInfo.InvariantCulture), float.Parse(parts[4], CultureInfo.InvariantCulture),
            float.Parse(parts[5], CultureInfo.InvariantCulture), float.Parse(parts[6], CultureInfo.InvariantCulture),
            uint.Parse(parts[7], CultureInfo.InvariantCulture)))
        .ToArray();

    private static Rig Setup(uint entry, uint areaId = 0)
    {
        EscortSpec spec = EscortSpecCatalog.Find(entry)!;
        Assert.NotNull(spec);
        IReadOnlyList<CreatureWaypoint> path = RealPath(entry);
        CreatureWaypoint first = path[0];
        uint[] summons = spec.Waypoints.SelectMany(p => p.Actions).Where(a => a.Type == "summon").Select(a => (uint)a.Id).Distinct().ToArray();
        var aiContent = new CreatureAiContent([], spec.Waypoints.SelectMany(p => p.Actions)
            .Where(a => a.Type is "say" or "say_nearby").Select(a => a.Id).Append(spec.StartText)
            .Concat(spec.Aggro?.TextIds ?? []).Where(id => id < 0).Distinct()
            .Select(id => new CreatureAiText(id, id.ToString(CultureInfo.InvariantCulture), 0, 0, 0)).ToArray());
        var nearbyEntries = spec.Waypoints.SelectMany(p => p.Actions).Where(a => a.Type == "say_nearby")
            .Select(a => a.SpeakerEntry).Distinct().ToArray();
        var nearbySpawns = spec.Waypoints.SelectMany(p => p.Actions.Where(a => a.Type == "say_nearby")
            .Select(a => (a.SpeakerEntry, Point: Assert.Single(path, w => w.Point == p.Point)))).ToArray();
        CreatureContent content = new(
            [Template(entry, b => { b.NpcFlags = (uint)NpcFlags.QuestGiver; if (spec.ClearImmuneToNpc) b.UnitFlags = (uint)UnitFlags.ImmuneToNpc; }), ..summons.Select(id => Template(id)), ..nearbyEntries.Select(id => Template(id))],
            [Spawn(1, entry, first.X, first.Y, first.Z), ..nearbySpawns.Select((near, index) => Spawn((uint)index + 2, near.SpeakerEntry, near.Point.X, near.Point.Y, near.Point.Z))], [], [], [], aiContent,
            scriptWaypoints: path.Select(p => (entry, 0u, p)));
        var quests = new EscortQuests();
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content,
            new CreatureAiServices { ScriptQuests = quests, QuestEvents = quests, ZoneAndAreaOf = _ => (0, areaId) });
        (Player player, FakeSession session) = AddPlayer(world, 1, first.X, first.Y);
        player.Relocate(first.X, first.Y, first.Z, 0, 0);
        return new Rig(world, map, system, player, session, Assert.Single(system.Creatures, c => c.Entry == entry), quests,
            summons);
    }

    private sealed record Rig(WorldRuntime World, Map Map, CreatureMapSystem System, Player Player,
        FakeSession Session, Creature Escort, EscortQuests Quests, uint[] SummonTemplateEntries) : IDisposable
    {
        public List<(uint Entry, float X, float Y, float Z, float EscortX, float EscortY)> Summons { get; } = [];
        public void Dispose() => World.Dispose();
    }

    private sealed class EscortQuests : IScriptQuestEvents, IEventAiQuestEvents
    {
        public List<(Player, uint)> Completed { get; } = [];
        public List<(Player, uint)> Failed { get; } = [];
        public void AreaExploredOrEventHappens(Player player, uint questId) => Completed.Add((player, questId));
        public void FailQuest(Player player, uint questId) => Failed.Add((player, questId));
        public void KilledMonsterCredit(Player player, uint creatureEntry, ObjectGuid source) { }
        public void GroupEventFailHappens(Player player, uint questId) => Failed.Add((player, questId));
        public IReadOnlyList<Player> GroupMembersOf(Player player) => [];
        public void EventHappened(Player player, uint questId, Creature source, bool rewardGroup)
        {
            Assert.True(rewardGroup);
            Completed.Add((player, questId));
        }
        public void KillCredit(Player player, uint creatureEntry, Creature source) { }
    }
}
