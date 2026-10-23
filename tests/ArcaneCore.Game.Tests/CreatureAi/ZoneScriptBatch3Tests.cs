using System.Globalization;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Creatures.Scripts;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Stealth;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Game.Tests.Threat;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>Zone script batch 3: Prospector Remtravel and the Barrens' Venture Co. stealth-alert speakers.</summary>
public sealed class ZoneScriptBatch3Tests
{
    private const float Z = 83.5f;
    private const uint StealthSpell = 950001;

    [Theory]
    [InlineData(ProspectorRemtravelAI.Entry, typeof(ProspectorRemtravelAI))]
    [InlineData(GallywixAI.Entry, typeof(GallywixAI))]
    [InlineData(ForemanSilixizAI.Entry, typeof(ForemanSilixizAI))]
    [InlineData(VentureCoLookoutAI.Entry, typeof(VentureCoLookoutAI))]
    public void TheEntryRunsItsScript(uint entry, Type ai)
    {
        CreatureContent content = new([Template(entry)], [Spawn(1, entry, 0, 0)], [], [], [], new CreatureAiContent([], []));
        (WorldRuntime world, Map _, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices());
        using (world)
        {
            AddPlayer(world, 1, 5, 0);
            Assert.IsType(ai, Assert.Single(system.Creatures, c => c.Entry == entry).AI);
        }
    }

    [Fact]
    public void TheEscortQuest_IsListedAsAScriptedEventQuest()
        => Assert.Contains(ProspectorRemtravelAI.QuestAbsentMindedPt2, new CreatureAiFactory().ScriptedEventQuests);

    // --- Prospector Remtravel --------------------------------------------------------------------------------

    private sealed class EscortQuests : IScriptQuestEvents, IEventAiQuestEvents
    {
        public List<(Player Player, uint QuestId)> Completed { get; } = [];

        public List<(Player Player, uint QuestId)> Failed { get; } = [];

        public void AreaExploredOrEventHappens(Player player, uint questId) => Completed.Add((player, questId));

        public void FailQuest(Player player, uint questId) => Failed.Add((player, questId));

        public void KilledMonsterCredit(Player player, uint creatureEntry, ObjectGuid source)
        {
        }

        public void GroupEventFailHappens(Player player, uint questId) => Failed.Add((player, questId));

        public IReadOnlyList<Player> GroupMembersOf(Player player) => [];

        public void EventHappened(Player player, uint questId, Creature source, bool rewardGroup)
        {
            Assert.True(rewardGroup);
            Completed.Add((player, questId));
        }

        public void KillCredit(Player player, uint creatureEntry, Creature source)
        {
        }
    }

    /// <summary>Remtravel on a straight 40 point cmangos waypoint_path 2917 (three yards a point), a player beside him and a quest recorder.</summary>
    private sealed class EscortRig : IDisposable
    {
        public const uint ScoutEntry = 2158;

        /// <param name="withPath">Whether waypoint_path 2917 exists (classic-db Updates/4749 is not applied by the importer).</param>
        /// <param name="questStartScript">Whether the classic-db quest start script 731 (say 925) is loaded.</param>
        public EscortRig(bool withPath = true, bool questStartScript = false, bool hostile = false)
        {
            var broadcast = new List<BroadcastText>
            {
                new(ProspectorRemtravelAI.SayStart, "start line", string.Empty, 1, 0, 0, [0, 0, 0], [0, 0, 0]),
            };
            for (uint i = 0; i < 6; i++)
            {
                broadcast.Add(new BroadcastText(ProspectorRemtravelAI.SayAggro1 + i, $"aggro{i} $n", string.Empty, 1, 0, 0, [0, 0, 0], [0, 0, 0]));
            }

            RelayScriptStep[] steps = questStartScript
                ? [new RelayScriptStep(ProspectorRemtravelAI.QuestAbsentMindedPt2, 0, 0, 0, 0, 0, 0, 0, 0, 0, ProspectorRemtravelAI.SayStart, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)]
                : [];
            var aiContent = new CreatureAiContent([], [], broadcastTexts: new BroadcastTextCatalog(broadcast))
            {
                DbScripts = new DbScriptCatalog(steps.Select(s => (DbScriptKind.QuestStart, s))),
            };
            CreatureWaypoint[] path = withPath ? [.. Enumerable.Range(1, 40).Select(i => new CreatureWaypoint((uint)i, 3f * i, 0f, Z, 0f, 0u))] : [];
            CreatureContent content = new(
                [Template(ProspectorRemtravelAI.Entry, b => { b.NpcFlags = (uint)ArcaneCore.Game.Npc.NpcFlags.QuestGiver; b.UnitFlags = (uint)UnitFlags.ImmuneToNpc; }),
                    Template(ScoutEntry)],
                [Spawn(1, ProspectorRemtravelAI.Entry, 0, 0), Spawn(2, ScoutEntry, 300, 0)], [], [], [], aiContent,
                entryWaypoints: path.Select(p => (CreatureContent.WaypointPathEntry, CreatureContent.WaypointPathBit | ProspectorRemtravelAI.EscortPath, p)));
            (World, Map _, System) = CreateAiSystem(content,
                hostile ? new CreatureAiServices { ScriptQuests = Quests, QuestEvents = Quests, Hostility = new AlwaysHostile() }
                    : new CreatureAiServices { ScriptQuests = Quests, QuestEvents = Quests });
            (Player, Session) = AddPlayer(World, 1, 0, 0);
            Npc = Assert.Single(System.Creatures, c => c.Entry == ProspectorRemtravelAI.Entry);
            Scout = Assert.Single(System.Creatures, c => c.Entry == ScoutEntry);
            Ai = Assert.IsType<ProspectorRemtravelAI>(Npc.AI);
        }

        public CreatureMapSystem System { get; }

        public Creature Scout { get; }

        /// <summary>The lines said to the player since the last clear.</summary>
        public List<string> Heard() => [.. Packets(Session, WorldOpcode.SmsgMessagechat).Select(p => ParseMonsterChat(p).Message)];

        public WorldRuntime World { get; }

        public Player Player { get; }

        public FakeSession Session { get; }

        public Creature Npc { get; }

        public ProspectorRemtravelAI Ai { get; }

        public EscortQuests Quests { get; } = new();

        /// <summary>Tick with the player standing beside him until <paramref name="done"/> or <paramref name="limitMs"/>.</summary>
        public void Follow(Func<bool> done, int limitMs = 300_000)
        {
            for (int elapsed = 0; elapsed < limitMs && !done(); elapsed += 100)
            {
                Player.Relocate(Npc.X, Npc.Y, Npc.Z, 0, 0);
                World.RunTick(100);
            }
        }

        public void Dispose() => World.Dispose();
    }

    [Fact]
    public void Remtravel_AcceptingTheQuest_StartsTheEscort_WithThePassiveFaction_AndHisStartLine()
    {
        using var rig = new EscortRig();
        Assert.NotEqual(ProspectorRemtravelAI.FactionEscortAlliancePassive, rig.Npc.FactionTemplate);
        Assert.Equal(UnitFlags.ImmuneToNpc, rig.Npc.UnitFlags & UnitFlags.ImmuneToNpc);

        rig.Ai.OnQuestAccept(rig.Player, ProspectorRemtravelAI.QuestAbsentMindedPt2);

        Assert.True(rig.Ai.HasEscortState(EscortAI.EscortState.Escorting));
        Assert.Equal(774u, rig.Npc.FactionTemplate); // mangos-classic FACTION_ESCORT_A_PASSIVE
        Assert.True(rig.Npc.HasTemporaryFaction);
        Assert.Equal(TemporaryFactionFlags.RestoreRespawn, rig.Npc.TemporaryFactionFlags);
        Assert.Equal(UnitFlags.None, rig.Npc.UnitFlags & UnitFlags.ImmuneToNpc);
        Assert.Equal(["start line"], rig.Heard());

        rig.Follow(() => rig.Npc.X > 6f, 20_000);
        Assert.True(rig.Npc.X > 6f, "he walks the path");
    }

    [Fact]
    public void Remtravel_WithTheClassicDbQuestStartScript_SaysTheStartLineOnlyOnce()
    {
        // Updates/4749's quest start script 731 says 925 and removes the immunity itself, after the AI hook (CreatureQuestScripts).
        using var rig = new EscortRig(questStartScript: true);

        rig.Ai.OnQuestAccept(rig.Player, ProspectorRemtravelAI.QuestAbsentMindedPt2);
        Assert.True(rig.System.StartDbScript(DbScriptKind.QuestStart, ProspectorRemtravelAI.QuestAbsentMindedPt2, rig.Npc, rig.Player));
        Run(rig.World, 500);

        Assert.Equal(["start line"], rig.Heard());
        Assert.True(rig.Ai.HasEscortState(EscortAI.EscortState.Escorting));
        Assert.Equal(774u, rig.Npc.FactionTemplate);
    }

    [Fact]
    public void Remtravel_WithoutThePath_LeavesHimUntouched()
    {
        // waypoint_path 2917 exists only in classic-db Updates/4749, which the importer does not apply: the escort cannot start.
        using var rig = new EscortRig(withPath: false);
        uint faction = rig.Npc.FactionTemplate;

        rig.Ai.OnQuestAccept(rig.Player, ProspectorRemtravelAI.QuestAbsentMindedPt2);

        Assert.False(rig.Ai.HasEscortState(EscortAI.EscortState.Escorting));
        Assert.Equal(faction, rig.Npc.FactionTemplate);
        Assert.False(rig.Npc.HasTemporaryFaction);
        Assert.Equal(UnitFlags.ImmuneToNpc, rig.Npc.UnitFlags & UnitFlags.ImmuneToNpc);
        Assert.Empty(rig.Heard());
    }

    [Fact]
    public void Remtravel_Aggro_SaysOneOfSixLines_OnlyTheFirstWithoutATarget()
    {
        using var rig = new EscortRig();
        HashSet<string> kinds = [];

        for (int i = 0; i < 60; i++)
        {
            rig.Ai.OnAggro(rig.Player);
        }

        foreach (string line in rig.Heard())
        {
            kinds.Add(line[..6]);
            if (line.StartsWith("aggro0", StringComparison.Ordinal))
            {
                Assert.Contains("$n", line, StringComparison.Ordinal); // 938 is said with no target to name
            }
            else
            {
                Assert.DoesNotContain("$n", line, StringComparison.Ordinal); // 939-943 name the attacker
            }
        }

        Assert.Equal(6, kinds.Count);
    }

    [Fact]
    public void Remtravel_CustomEventA_SendsTheSenderAtHim_AndOtherEventsDoNot()
    {
        using var rig = new EscortRig(hostile: true);

        rig.Ai.OnReceiveAiEvent(AiEventTypes.CustomEventAiB, rig.Scout, null, 0);
        Assert.Null(rig.Scout.Combat.Victim);

        rig.Ai.OnReceiveAiEvent(AiEventTypes.CustomEventAiA, rig.Scout, null, 0);
        Assert.Same(rig.Npc, rig.Scout.Combat.Victim);
    }

    [Fact]
    public void Remtravel_AnotherQuest_StartsNothing()
    {
        using var rig = new EscortRig();
        uint faction = rig.Npc.FactionTemplate;

        rig.Ai.OnQuestAccept(rig.Player, 999_999);

        Assert.False(rig.Ai.HasEscortState(EscortAI.EscortState.Escorting));
        Assert.Equal(faction, rig.Npc.FactionTemplate);
    }

    [Fact]
    public void Remtravel_Escort_CreditsTheQuestAtPoint38_AndEndsAtPoint39()
    {
        using var rig = new EscortRig();
        rig.Ai.OnQuestAccept(rig.Player, ProspectorRemtravelAI.QuestAbsentMindedPt2);

        rig.Follow(() => rig.Quests.Completed.Count > 0);

        Assert.Equal([(rig.Player, ProspectorRemtravelAI.QuestAbsentMindedPt2)], rig.Quests.Completed);
        Assert.InRange(rig.Npc.X, (3f * 38) - 2f, (3f * 38) + 4f); // credited at point 38 (x = 114), not at the end
        Assert.True(rig.Ai.HasEscortState(EscortAI.EscortState.Escorting));

        rig.Follow(() => !rig.Ai.HasEscortState(EscortAI.EscortState.Escorting), 60_000);

        Assert.False(rig.Ai.HasEscortState(EscortAI.EscortState.Escorting));
        Assert.InRange(rig.Npc.X, (3f * 39) - 2f, (3f * 39) + 2f); // stops where point 39 is (x = 117)
        Assert.Single(rig.Quests.Completed);
        Assert.Empty(rig.Quests.Failed);
    }

    // --- the stealth-alert speakers --------------------------------------------------------------------------

    /// <summary>A level 1 rogue at the origin, a level 4 speaker 6 yd west-facing from it (inside the sniffed 8.3 yd alert band), real stealth.</summary>
    private sealed class StealthRig : IDisposable
    {
        public StealthRig(uint entry, params int[] textIds)
        {
            Arena = new ThreatArena(SpellTestKit.Spell(StealthSpell, SpellTestKit.Effect(SpellEffectName.ApplyAura, 5, aura: AuraType.ModStealth))
                with { Duration = new SpellDuration(-1, 0, -1), SpellVisual = 1 });
            var registry = new StealthRegistry();
            Arena.Kit.System.RegisterAura(AuraType.ModStealth, StealthAuras.Handler(registry));
            StealthServices.Install(Arena.Map, new StealthServices(Arena.Kit.System, registry, StealthOptions.Default));
            Arena.Tank.Level = 1;
            var aiContent = new CreatureAiContent([], [.. textIds.Select(id => new CreatureAiText(id, id.ToString(CultureInfo.InvariantCulture), 0, 0, 0))]);
            System = new CreatureMapSystem(Arena.Map, new CreatureContent([Template(entry)], [Spawn(90, entry, 6f, 0f)], [], [], [], aiContent),
                new CreatureOptions { AiRelocationNotifyDelayMs = 3_600_000 }, random: new Random(1),
                aiServices: new CreatureAiServices { Hostility = new AlwaysHostile() });
            Arena.Map.AddUpdater(System);
            Arena.Kit.World.RunTick(50);
            Speaker = Assert.Single(System.Creatures, c => c.Entry == entry);
            Speaker.Level = 4;
            Speaker.Orientation = MathF.PI - 0.3f;
            Arena.TankSession.Clear();
        }

        public ThreatArena Arena { get; }

        public CreatureMapSystem System { get; }

        public Creature Speaker { get; }

        public Player Rogue => Arena.Tank;

        public void Hide() => Arena.Cast(Rogue, Rogue, StealthSpell);

        /// <summary>The creature moved near the rogue: vmangos CallAIMoveLOS.</summary>
        public void Notice() => System.CallAiMoveInLineOfSight(Speaker, Rogue);

        /// <summary>What the rogue has been told by the speaker since the last <see cref="Arena"/> session clear.</summary>
        public List<string> Heard() => [.. Packets(Arena.TankSession, WorldOpcode.SmsgMessagechat).Select(p => ParseMonsterChat(p).Message)];

        public void Dispose() => Arena.Dispose();
    }

    public static TheoryData<uint, int[]> Speakers => new()
    {
        { GallywixAI.Entry, [GallywixAI.SayStealthAlert] },
        { ForemanSilixizAI.Entry, [ForemanSilixizAI.SayStealthAlert] },
        { VentureCoLookoutAI.Entry, [VentureCoLookoutAI.SayStealthAlert1, VentureCoLookoutAI.SayStealthAlert2] },
    };

    [Fact]
    public void TheStealthTexts_AreTheScriptDevIds()
    {
        Assert.Equal(-1010005, GallywixAI.SayStealthAlert);
        Assert.Equal(-1010006, ForemanSilixizAI.SayStealthAlert);
        Assert.Equal(-1010007, VentureCoLookoutAI.SayStealthAlert1);
        Assert.Equal(-1010008, VentureCoLookoutAI.SayStealthAlert2);
    }

    [Theory]
    [MemberData(nameof(Speakers))]
    public void AStealthedPlayerInTheAlertBand_MakesTheSpeakerCallItOut(uint entry, int[] texts)
    {
        using var rig = new StealthRig(entry, texts);
        rig.Hide();

        rig.Notice();

        string said = Assert.Single(rig.Heard());
        Assert.Contains(said, texts.Select(id => id.ToString(CultureInfo.InvariantCulture)));
        Assert.Null(rig.Speaker.Combat.Victim); // an alert, not an attack
    }

    [Theory]
    [MemberData(nameof(Speakers))]
    public void APlayerWhoIsNotStealthed_GetsNoAlertLine(uint entry, int[] texts)
    {
        using var rig = new StealthRig(entry, texts);

        rig.Notice();

        Assert.Empty(rig.Heard());
    }

    [Fact]
    public void ASpeakerOnlyCallsOutOncePerAlertCooldown()
    {
        using var rig = new StealthRig(GallywixAI.Entry, GallywixAI.SayStealthAlert);
        rig.Hide();

        rig.Notice();
        rig.Notice(); // within the 10 s cooldown (and while distracted)

        Assert.Single(rig.Heard());
    }

    [Fact]
    public void TheLookout_SaysBothOfItsLines_OverRepeatedAlerts()
    {
        using var rig = new StealthRig(VentureCoLookoutAI.Entry, VentureCoLookoutAI.SayStealthAlert1, VentureCoLookoutAI.SayStealthAlert2);
        rig.Hide();
        HashSet<string> heard = [];

        for (int i = 0; i < 16; i++)
        {
            rig.Notice();
            heard.UnionWith(rig.Heard());
            Run(rig.Arena.Kit.World, 11_000); // the 10 s cooldown and the 5 s distraction pass
            rig.Speaker.Orientation = MathF.PI - 0.3f;
            rig.Arena.TankSession.Clear();
        }

        Assert.Equal(2, heard.Count);
    }
}
