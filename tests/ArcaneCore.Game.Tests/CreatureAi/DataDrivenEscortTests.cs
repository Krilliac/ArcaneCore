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
