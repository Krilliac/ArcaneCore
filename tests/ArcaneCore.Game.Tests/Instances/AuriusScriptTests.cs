using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.Stratholme;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Game.Tests.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;
using static ArcaneCore.Game.Tests.Npc.QuestFlowKit;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>npc_aurius (spec 329-npc_aurius): gossip, quest 5122 through the real reward path, the Baron assist and the fake death.</summary>
public sealed class AuriusScriptTests
{
    private const uint Aurius = 10917, Baron = 10440;
    private const string JoinText = "Rivendare!  I come for you!", DeathText = "Argh!";

    private static CreatureAiEvent EventRow(uint id, EventAiEventType type, uint spell, int target, int p1, int p2, int p3, int p4) => new()
    {
        Id = id, CreatureId = Aurius, EventType = (byte)type, Chance = 100, Flags = 1025, Param1 = p1, Param2 = p2, Param3 = p3, Param4 = p4,
        Action1 = new CreatureAiAction((byte)EventAiActionType.Cast, (int)spell, target, 0),
    };

    /// <summary>The creature_ai_scripts rows of the fixture for entry 10917 (spec 2).</summary>
    private static CreatureAiEvent[] Rows() =>
    [
        EventRow(1091701, (EventAiEventType)9, 14517, 1, 0, 5, 6000, 9000),
        EventRow(1091702, EventAiEventType.TimerInCombat, 17149, 4, 6000, 14000, 8000, 11000),
        EventRow(1091703, (EventAiEventType)14, 13952, 12, 3000, 40, 12000, 16000),
        EventRow(1091704, EventAiEventType.HealthPercent, 13874, 0, 50, 0, 21000, 28000),
    ];

    private static CreatureAiText[] Texts() => [new(-1329021, JoinText, 1, 0, 0), new(-1329022, DeathText, 0, 0, 0)];

    private static CreatureTemplate AuriusTemplate() => Template(Aurius, t =>
    {
        t.Name = "Aurius";
        t.MinLevel = t.MaxLevel = 60;
        t.Faction = 1080;
        t.NpcFlags = 3;
        t.UnitFlags = 32768;
        t.AIName = "EventAI";
        t.MinLevelHealth = t.MaxLevelHealth = 50_000;
    }) with { ScriptName = AuriusScript.ScriptName, GossipMenuId = 3043 };

    private static DungeonNpcRig Rig(bool withBaron = true, bool realFactions = false)
    {
        List<CreatureTemplate> templates = [AuriusTemplate()];
        List<CreatureSpawn> spawns = [Spawn(1, Aurius, DungeonNpcRig.EntranceX + 3, DungeonNpcRig.EntranceY, DungeonNpcRig.EntranceZ, InstanceFixture.Dungeon)];
        if (withBaron)
        {
            templates.Add(Template(Baron, t => { t.Name = "Baron Rivendare"; t.Faction = 21; t.MinLevel = t.MaxLevel = 62; t.MinLevelHealth = t.MaxLevelHealth = 500_000; }));
            spawns.Add(Spawn(2, Baron, DungeonNpcRig.EntranceX + 25, DungeonNpcRig.EntranceY, DungeonNpcRig.EntranceZ, InstanceFixture.Dungeon));
        }

        return new DungeonNpcRig(map => new StratholmeInstance(map), templates, spawns, new CreatureAiContent(Rows(), Texts()),
            hostility: realFactions ? null : new AuriusFriendlyToPlayers(), factions: realFactions ? RealFactionDbc.Catalog : null);
    }

    private static NpcInfo Npc(Creature c) => new(c.Guid, c.Entry, c.Spawn?.Guid ?? 0, (NpcFlags)c.NpcFlags, 329, c.X, c.Y, c.Z, 0.5f, c.IsAlive, false, c.Combat.IsInCombat, false, 3043);

    private static uint HelloText(DungeonNpcRig rig, Creature aurius)
    {
        ScriptedGossipMenu menu = rig.Factory.NpcGossipOf(AuriusScript.ScriptName)!.Hello(rig.Player, Npc(aurius))!;
        Assert.Empty(menu.Items);
        return menu.NpcTextId;
    }

    private static List<string> Said(DungeonNpcRig rig)
        => [.. rig.Fixture.Sent(rig.Player, WorldOpcode.SmsgMessagechat).Select(CreatureAiTestSupport.ParseMonsterChat).Select(c => c.Message)];

    private static float Distance(WorldObject a, WorldObject b) => MathF.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)) + ((a.Z - b.Z) * (a.Z - b.Z)));

    [Fact]
    public void TheScriptNameResolvesAsAGossipAndQuestScript_AndKeepsTheEventAiBehaviour()
    {
        var content = new CreatureContent([AuriusTemplate()], [], [], [], []);
        ScriptResolution r = new ScriptCatalog(new CreatureAiFactory(), content).Resolve(Aurius, 329);
        Assert.True(r.ScriptNameResolved);
        Assert.Equal(ScriptKind.GossipScript, r.Kind);

        using DungeonNpcRig rig = Rig(withBaron: false);
        Assert.IsAssignableFrom<CreatureEventAI>(rig.Creature(Aurius).AI); // the AIName AI is untouched
    }

    [Fact]
    public void HeKneelsOnSpawn_AndHelloShows3755_Then3756AfterTheQuest_Then3757AfterTheBaron()
    {
        using DungeonNpcRig rig = Rig();
        Creature aurius = rig.Creature(Aurius);
        var data = Assert.IsType<StratholmeInstance>(rig.Data);
        Assert.Equal(StandState.Kneel, aurius.StandState);
        Assert.NotEqual(0u, aurius.NpcFlags & (uint)NpcFlags.QuestGiver);
        Assert.Equal(3755u, HelloText(rig, aurius));

        data.SetData(StratholmeInstance.TypeAurius, EncounterState.Done);
        Assert.Equal(0u, aurius.NpcFlags & (uint)NpcFlags.QuestGiver); // no second turn-in this instance
        Assert.NotEqual(0u, aurius.NpcFlags & (uint)NpcFlags.Gossip);
        Assert.Equal(3756u, HelloText(rig, aurius));

        data.SetData(StratholmeInstance.TypeBaron, EncounterState.Done);
        Assert.Equal(3757u, HelloText(rig, aurius));
    }

    [Fact]
    public void QuestFiveOneTwoTwo_GoesThroughTheRealRewardPath_AndASecondRewardChangesNothing()
    {
        var factory = new CreatureAiFactory(); // carries the real registration
        QuestFlowKit kit = new([Task(AuriusScript.QuestMedallionOfFaith)], [Row(AuriusScript.QuestMedallionOfFaith, QuestStatus.Complete, kills: 2)],
            starters: [AuriusScript.QuestMedallionOfFaith], enders: [AuriusScript.QuestMedallionOfFaith], rewardable: [AuriusScript.QuestMedallionOfFaith],
            npcFlags: 3, scriptName: AuriusScript.ScriptName);
        using (kit)
        using (CreatureQuestScripts.Attach(kit.Services, _ => null, factory: () => factory))
        {
            var data = new StratholmeInstance(kit.Player.Map!);
            data.Initialize();
            kit.Player.Map!.AddUpdater(data);

            // gossip through the real hello path
            kit.Drain();
            kit.Services.GossipHello(kit.Player, kit.Creature.Guid);
            byte[] hello = Assert.Single(kit.Drain(), p => p.Opcode == WorldOpcode.SmsgGossipMessage).Payload;
            Assert.Equal(AuriusScript.TextBeforeQuest, BitConverter.ToUInt32(hello, 8));

            Assert.True(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, AuriusScript.QuestMedallionOfFaith, 0, out QuestRewardPlan? plan));
            kit.Drain();
            kit.Services.ApplyReward(plan!);
            Assert.Equal(EncounterState.Done, data.GetData(StratholmeInstance.TypeAurius));

            kit.Drain();
            kit.Services.GossipHello(kit.Player, kit.Creature.Guid);
            Assert.Equal(AuriusScript.TextAuriusDone, BitConverter.ToUInt32(Assert.Single(kit.Drain(), p => p.Opcode == WorldOpcode.SmsgGossipMessage).Payload, 8));

            // a reward after the Baron died must not arm Aurius, and another quest does nothing
            var script = factory.NpcQuestOf(AuriusScript.ScriptName)!;
            data.SetData(StratholmeInstance.TypeAurius, EncounterState.NotStarted);
            data.SetData(StratholmeInstance.TypeBaron, EncounterState.Done);
            script.OnQuestRewarded(kit.Player, kit.Creature, AuriusScript.QuestMedallionOfFaith);
            Assert.Equal(EncounterState.NotStarted, data.GetData(StratholmeInstance.TypeAurius));
            data.SetData(StratholmeInstance.TypeBaron, EncounterState.NotStarted);
            script.OnQuestRewarded(kit.Player, kit.Creature, 1234);
            Assert.Equal(EncounterState.NotStarted, data.GetData(StratholmeInstance.TypeAurius));
        }
    }

    [Fact]
    public void WithTheQuestDone_FiveSecondsAfterTheBaronEntersCombat_AuriusStandsBesideHimAndAttacks()
    {
        using DungeonNpcRig rig = Rig();
        Creature aurius = rig.Creature(Aurius), baron = rig.Creature(Baron);
        rig.Data.SetData(StratholmeInstance.TypeAurius, EncounterState.Done);
        (float x, float y) = (aurius.X, aurius.Y);

        Assert.True(rig.Creatures.AttackStart(baron, rig.Player));
        rig.Run(4900);
        Assert.Equal((x, y), (aurius.X, aurius.Y));
        Assert.Equal(StandState.Kneel, aurius.StandState);
        Assert.DoesNotContain(JoinText, Said(rig));

        rig.Run(300);
        Assert.InRange(Distance(aurius, baron), 0f, 4.5f);
        Assert.Equal(StandState.Stand, aurius.StandState);
        Assert.Equal(42u, aurius.FactionTemplate);
        Assert.Equal(0u, aurius.NpcFlags & (uint)(NpcFlags.QuestGiver | NpcFlags.Gossip));
        Assert.Contains(JoinText, Said(rig));
        Assert.Same(baron, aurius.Combat.Victim);
    }

    [RealFactionDbcFact]
    public void Faction42_ReactsToTheBaronOneWayOnly_AndIsFriendlyToPlayers_PerTheRealDbc()
    {
        var catalog = RealFactionDbc.Catalog;
        var t42 = catalog.Find(42)!;
        var t21 = catalog.Find(21)!;
        Assert.Equal((36u, 1u, 1u, 0u), (t42.Faction, t42.OwnMask, t42.FriendlyMask, t42.HostileMask));
        Assert.Equal((20u, 8u, 1u), (t21.Faction, t21.OwnMask, t21.HostileMask));
        Assert.False(t42.IsHostileTo(t21)); // 42 does not call the Baron an enemy ...
        Assert.True(t21.IsHostileTo(t42));  // ... the Baron's template does call 42 one

        using DungeonNpcRig rig = Rig(realFactions: true);
        Creature aurius = rig.Creature(Aurius), baron = rig.Creature(Baron);
        var hostility = new FactionCreatureHostility(catalog);
        CombatHooks hooks = rig.Map.Combat.Hooks;
        aurius.FactionTemplate = StratholmeInstance.FactionAuriusVictim;
        Assert.True(hostility.IsHostile(baron, aurius));
        Assert.False(hostility.IsHostile(aurius, baron)); // so he never re-acquires the Baron by sight: the script attacks him explicitly
        Assert.True(hooks.CanAttack(aurius, baron));      // attack validity is either direction for creature pairs
        Assert.True(hooks.CanAttack(baron, aurius));
        Assert.False(hostility.IsHostile(aurius, rig.Player));
        Assert.True(hooks.IsFriendly(aurius, rig.Player));
        Assert.False(hooks.CanAttack(rig.Player, aurius));
        Assert.False(hooks.CanAttack(aurius, rig.Player));
    }

    [RealFactionDbcFact]
    public void RealFactions_AuriusJoinsAndKeepsFightingTheBaron_ThenFakesDeathAtOneHealthAndStaysThere()
    {
        using DungeonNpcRig rig = Rig(realFactions: true);
        Creature aurius = rig.Creature(Aurius), baron = rig.Creature(Baron);
        rig.Data.SetData(StratholmeInstance.TypeAurius, EncounterState.Done);
        Assert.True(rig.Creatures.AttackStart(baron, rig.Player));
        rig.Run(5200);
        Assert.Equal(42u, aurius.FactionTemplate);
        Assert.Same(baron, aurius.Combat.Victim);
        Assert.InRange(Distance(aurius, baron), 0f, 4.5f);
        rig.Run(20_000); // no sight-based retarget exists (42 is not hostile to 21), yet he stays on the Baron
        Assert.Same(baron, aurius.Combat.Victim);
        Assert.True(aurius.Combat.IsInCombat);

        rig.Map.Combat.Kill(rig.Player, baron);
        rig.Run(100);
        Assert.True(aurius.IsAlive);
        Assert.Equal(1u, aurius.Health);
        Assert.Equal(StandState.Dead, aurius.StandState);
        rig.Run(30_000); // regeneration must not heal the "corpse"
        Assert.Equal(1u, aurius.Health);
        Assert.Equal(3757u, HelloText(rig, aurius));
    }

    [RealFactionDbcFact]
    public void RealFactions_WithTheQuestNotDone_AuriusStaysKneeling()
    {
        using DungeonNpcRig rig = Rig(realFactions: true);
        Creature aurius = rig.Creature(Aurius), baron = rig.Creature(Baron);
        (float x, float y) = (aurius.X, aurius.Y);
        Assert.True(rig.Creatures.AttackStart(baron, rig.Player));
        rig.Run(8000);
        Assert.Equal((x, y), (aurius.X, aurius.Y));
        Assert.Equal(StandState.Kneel, aurius.StandState);
        Assert.Null(aurius.Combat.Victim);
    }

    [Fact]
    public void CrusaderStrikeIsCastOnlyInMeleeRange()
    {
        // Spec 329 check 8: the range event (0-5 yd). Start him 25 yd from his victim: no cast while he closes in, a cast once he arrives.
        using DungeonNpcRig rig = Rig(withBaron: false);
        Creature aurius = rig.Creature(Aurius);
        rig.Run(100);
        rig.Creatures.NearTeleport(aurius, rig.Player.X + 25f, rig.Player.Y, rig.Player.Z, 0f);
        Assert.True(aurius.AI!.AttackStart(rig.Player));
        int seen = 0;
        bool castWhileFar = false;
        for (int i = 0; i < 400; i++)
        {
            float before = Distance(aurius, rig.Player);
            rig.Tick();
            int casts = rig.Spells.Of(aurius, 14517).Count();
            if (casts > seen && before > 6f && Distance(aurius, rig.Player) > 6f)
            {
                castWhileFar = true;
            }

            seen = casts;
        }

        Assert.False(castWhileFar);
        Assert.True(seen > 0, "Crusader Strike was never cast, even in melee range");
        Assert.True(Distance(aurius, rig.Player) < 8f);
    }

    [Fact]
    public void WithTheQuestNotDone_TheBaronFightDoesNotMoveAurius()
    {
        using DungeonNpcRig rig = Rig();
        Creature aurius = rig.Creature(Aurius), baron = rig.Creature(Baron);
        (float x, float y) = (aurius.X, aurius.Y);
        Assert.True(rig.Creatures.AttackStart(baron, rig.Player));
        rig.Run(8000);
        Assert.Equal((x, y), (aurius.X, aurius.Y));
        Assert.Equal(StandState.Kneel, aurius.StandState);
        Assert.Null(aurius.Combat.Victim);
    }

    [Fact]
    public void WhenTheBaronDiesWithAuriusAlive_HeFakesDeathAtOneHealth_AndTheGossipShows3757()
    {
        using DungeonNpcRig rig = Rig();
        Creature aurius = rig.Creature(Aurius), baron = rig.Creature(Baron);
        rig.Data.SetData(StratholmeInstance.TypeAurius, EncounterState.Done);
        Assert.True(rig.Creatures.AttackStart(baron, rig.Player));
        rig.Run(5200);
        Assert.Equal(0u, aurius.NpcFlags & (uint)NpcFlags.Gossip);

        rig.Map.Combat.Kill(rig.Player, baron);
        rig.Run(100);
        Assert.True(aurius.IsAlive);
        Assert.Equal(1u, aurius.Health);
        Assert.Equal(StandState.Dead, aurius.StandState);
        Assert.NotEqual(0u, aurius.NpcFlags & (uint)NpcFlags.QuestGiver);
        Assert.NotEqual(0u, aurius.NpcFlags & (uint)NpcFlags.Gossip);
        Assert.Contains(DeathText, Said(rig));
        Assert.Equal(3757u, HelloText(rig, aurius));
    }

    [Fact]
    public void WhenAuriusReallyDies_HeSaysTheDeathLine()
    {
        using DungeonNpcRig rig = Rig(withBaron: false);
        Creature aurius = rig.Creature(Aurius);
        rig.Map.Combat.Kill(rig.Player, aurius);
        Assert.False(aurius.IsAlive);
        Assert.Contains(DeathText, Said(rig));
    }

    [Fact]
    public void HisEventAiRowsStillFire_DivineShieldAtHalfHealth_ExorcismOnATimer()
    {
        using DungeonNpcRig rig = Rig(withBaron: false);
        Creature aurius = rig.Creature(Aurius);
        Assert.True(aurius.AI!.AttackStart(rig.Player));
        long start = rig.Spells.NowMs;
        rig.Run(100);
        Assert.DoesNotContain(rig.Spells.Casts, c => c.Spell == 13874);
        aurius.Health = aurius.MaxHealth * 4 / 10;
        rig.Run(1000);
        Assert.NotEmpty(rig.Spells.Of(aurius, 13874));
        rig.Run(20_000);
        long first = rig.Spells.Of(aurius, 17149).First().AtMs - start;
        Assert.InRange(first, 5900, 14600);
    }

    [WorldFixtureFact]
    public void TheEmbeddedRowsAndTextsAreTheFixtureOnes()
    {
        string Key(CreatureAiEvent e) => $"{e.CreatureId}/{e.EventType}/{e.Flags}/{e.Param1},{e.Param2},{e.Param3},{e.Param4}/{e.Action1}";
        Assert.Equal(Rows().Select(Key).Order(), WorldFixtureRows.AiEvents(Aurius).Select(Key).Order());
        var texts = WorldFixtureRows.Query("SELECT Entry, Content, Type FROM creature_ai_texts WHERE Entry IN (-1329021, -1329022)", r => (r.GetInt64(0), r.GetString(1), r.GetInt64(2)));
        Assert.Contains((-1329021L, JoinText, 1L), texts);
        Assert.Contains((-1329022L, DeathText, 0L), texts);
        var menu = WorldFixtureRows.Query("SELECT text_id FROM gossip_menu WHERE entry = 3043", r => r.GetInt64(0));
        Assert.Equal([3755L, 3756L, 3757L], menu.Order());
    }
}

/// <summary>
/// Everything is hostile to everything, except Aurius toward players and creatures while he has his template faction 1080 (a quest
/// giver, spec 1): he must not aggro on his own. Once the script gives him faction 42 he fights the Baron, which this allows.
/// </summary>
internal sealed class AuriusFriendlyToPlayers : ICreatureHostility
{
    public bool IsHostile(Creature creature, Unit target)
    {
        bool aurius = creature.Template.Entry == 10917;
        if (aurius && target is Player)
        {
            return false;
        }

        Creature? aurian = aurius ? creature : (target as Creature)?.Template.Entry == 10917 ? (Creature)target : null;
        return aurian is null || aurian.FactionTemplate == 42; // with his template faction 1080 he and the Baron are not enemies (spec 8.2: 42 is what makes it so)
    }

    public bool CanAssist(Creature helper, Creature caller) => helper.FactionTemplate == caller.FactionTemplate;
}
