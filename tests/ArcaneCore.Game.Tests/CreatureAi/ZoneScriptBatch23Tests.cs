using System.Globalization;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Creatures.Scripts;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>
/// Zone scripts batch 23: <see cref="GuardianOfBlizzardAI"/> (5764, <c>npc_guardian</c>),
/// <see cref="LoramusThalipedesGossip"/> (7783, <c>npc_loramus_thalipedes</c>), and
/// <see cref="AnachronosTheAncientAI"/> (15381, <c>npc_anachronos_the_ancient</c>).
/// </summary>
public sealed class ZoneScriptBatch23Tests
{
    private sealed class Quests : IScriptQuestEvents, IEventAiQuestEvents
    {
        public HashSet<uint> Incomplete { get; } = [];
        public List<uint> Explored { get; } = [];
        public List<uint> GroupExplored { get; } = [];
        public void AreaExploredOrEventHappens(Player player, uint questId) => Explored.Add(questId);
        public void FailQuest(Player player, uint questId) { }
        public void KilledMonsterCredit(Player player, uint creatureEntry, ObjectGuid source) { }
        public void GroupEventFailHappens(Player player, uint questId) { }
        public IReadOnlyList<Player> GroupMembersOf(Player player) => [];
        public bool IsQuestIncomplete(Player player, uint questId) => Incomplete.Contains(questId);
        public void TalkedToCreature(Player player, uint creatureEntry, ObjectGuid source) { }
        public void EventHappened(Player player, uint questId, Creature source, bool rewardGroup) => GroupExplored.Add(questId);
        public void KillCredit(Player player, uint creatureEntry, Creature source) { }
    }

    [Fact]
    public void GuardianOfBlizzard_DisablesMelee_Says2077OnAggro_AndCastsDeathtouchTriggeredOnAttackReady()
    {
        var aiContent = new CreatureAiContent(
            [],
            [],
            new BroadcastTextCatalog(
            [
                new BroadcastText((uint)GuardianOfBlizzardAI.SayAggro, "Trespasser!", string.Empty, 0, 0, 0, [0, 0, 0], [0, 0, 0]),
            ]));
        CreatureContent content = new(
            [Template(GuardianOfBlizzardAI.Entry, b =>
            {
                b.Name = "Guardian of Blizzard";
                b.Faction = 14;
            }) with { ScriptName = GuardianOfBlizzardAI.ScriptName }],
            [Spawn(1, GuardianOfBlizzardAI.Entry, 0f, 0f, 0f)],
            [],
            [],
            [],
            aiContent);
        var caster = new FakeCaster();
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(
            content,
            new CreatureAiServices { Hostility = new AlwaysHostile(), Spells = caster });
        using (world)
        {
            (Player player, FakeSession session) = AddPlayer(world, 1, 30f, 0f);
            player.Relocate(2f, 0f, 0f, 0f, 0);
            map.OnObjectMoved(player);
            Creature guardian = Assert.Single(system.Creatures);
            guardian.Combat.SetAttackTime(ArcaneCore.Game.Combat.WeaponAttackType.BaseAttack, 1000, resetTimer: false);
            var ai = Assert.IsType<GuardianOfBlizzardAI>(guardian.AI);
            Assert.False(ai.MeleeEnabled);

            system.AttackStart(guardian, player);
            Assert.Contains(
                Packets(session, WorldOpcode.SmsgMessagechat).Select(ParseMonsterChat),
                m => m.Message == "Trespasser!");

            Run(world, 100);
            Assert.Contains(caster.Casts, c => c.Spell == GuardianOfBlizzardAI.SpellDeathtouch && ReferenceEquals(c.Target, player) && c.Triggered);
            int firstCount = caster.Casts.Count(c => c.Spell == GuardianOfBlizzardAI.SpellDeathtouch);

            // Attack timer was reset to BaseAttackTime (1000 ms), so 400 ms later it does not fire again yet.
            Run(world, 400);
            Assert.Equal(firstCount, caster.Casts.Count(c => c.Spell == GuardianOfBlizzardAI.SpellDeathtouch));

            // After the remainder of the 1000 ms timer, it fires again.
            Run(world, 700);
            Assert.True(caster.Casts.Count(c => c.Spell == GuardianOfBlizzardAI.SpellDeathtouch) > firstCount);
        }
    }

    [Fact]
    public void LoramusThalipedesGossip_WalksBothQuestChains_AndCompletesQuests2744And3141()
    {
        var factory = new CreatureAiFactory();
        Assert.Contains(LoramusThalipedesGossip.QuestTheDemonHunter, factory.ScriptedEventQuests);
        Assert.Contains(LoramusThalipedesGossip.QuestLoramus, factory.ScriptedEventQuests);
        INpcGossipScript? registered = factory.NpcGossipOf(LoramusThalipedesGossip.ScriptName);
        Assert.IsType<LoramusThalipedesGossip>(registered);

        var incomplete = new HashSet<uint>();
        var completed = new List<uint>();
        var gossip = new LoramusThalipedesGossip(
            (_, q) => incomplete.Contains(q),
            (_, q) =>
            {
                incomplete.Remove(q);
                completed.Add(q);
            });

        Player player = TestWorld.CreatePlayer(1, 0f, 0f, new FakeSession(1));
        var npc = new NpcInfo(
            ObjectGuid.WithEntry(HighGuid.Unit, LoramusThalipedesGossip.Entry, 1),
            LoramusThalipedesGossip.Entry,
            1,
            NpcFlags.Gossip | NpcFlags.QuestGiver,
            0,
            0f,
            0f,
            0f,
            0f,
            true,
            false,
            false,
            false,
            0);

        // Neither quest incomplete -> greeting 1796 with no gossip options.
        ScriptedGossipMenu? idleMenu = gossip.Hello(player, npc);
        Assert.NotNull(idleMenu);
        Assert.Equal(LoramusThalipedesGossip.TextGreeting, idleMenu!.NpcTextId);
        Assert.Empty(idleMenu.Items);

        // Both quests incomplete -> both initial options offered.
        incomplete.Add(LoramusThalipedesGossip.QuestTheDemonHunter);
        incomplete.Add(LoramusThalipedesGossip.QuestLoramus);
        ScriptedGossipMenu? bothMenu = gossip.Hello(player, npc);
        Assert.NotNull(bothMenu);
        Assert.Equal(2, bothMenu!.Items.Count);
        Assert.Equal(LoramusThalipedesGossip.ActionDemonHunter1, bothMenu.Items[0].Action);
        Assert.Equal(LoramusThalipedesGossip.ActionLoramus1, bothMenu.Items[1].Action);

        // Walk quest 2744 ("The Demon Hunter"): 1000 -> 1812 (1001) -> close & complete 2744.
        ScriptedGossipReply step2744 = gossip.SelectReply(player, npc, LoramusThalipedesGossip.SenderMain, LoramusThalipedesGossip.ActionDemonHunter1);
        Assert.Equal(LoramusThalipedesGossip.TextDemonHunter, step2744.NpcTextId);
        Assert.Equal(LoramusThalipedesGossip.ActionDemonHunter2, Assert.Single(step2744.NextMenu!.Items).Action);

        ScriptedGossipReply done2744 = gossip.SelectReply(player, npc, LoramusThalipedesGossip.SenderMain, LoramusThalipedesGossip.ActionDemonHunter2);
        Assert.True(done2744.Close);
        Assert.Equal([LoramusThalipedesGossip.QuestTheDemonHunter], completed);

        // Walk quest 3141 ("Loramus"): 1002 -> 1813 (1003) -> 1814 (1004) -> 1815 (1005) -> 1816 (1006) -> 1817 (1007) -> close & complete 3141.
        (uint Action, uint ExpectedText, uint NextAction)[] chain =
        [
            (LoramusThalipedesGossip.ActionLoramus1, LoramusThalipedesGossip.TextStory1, LoramusThalipedesGossip.ActionLoramus2),
            (LoramusThalipedesGossip.ActionLoramus2, LoramusThalipedesGossip.TextStory2, LoramusThalipedesGossip.ActionLoramus3),
            (LoramusThalipedesGossip.ActionLoramus3, LoramusThalipedesGossip.TextStory3, LoramusThalipedesGossip.ActionLoramus4),
            (LoramusThalipedesGossip.ActionLoramus4, LoramusThalipedesGossip.TextStory4, LoramusThalipedesGossip.ActionLoramus5),
            (LoramusThalipedesGossip.ActionLoramus5, LoramusThalipedesGossip.TextStory5, LoramusThalipedesGossip.ActionLoramus6),
        ];
        foreach ((uint action, uint expectedText, uint nextAction) in chain)
        {
            ScriptedGossipReply reply = gossip.SelectReply(player, npc, LoramusThalipedesGossip.SenderMain, action);
            Assert.Equal(expectedText, reply.NpcTextId);
            Assert.Equal(nextAction, Assert.Single(reply.NextMenu!.Items).Action);
        }

        ScriptedGossipReply done3141 = gossip.SelectReply(player, npc, LoramusThalipedesGossip.SenderMain, LoramusThalipedesGossip.ActionLoramus6);
        Assert.True(done3141.Close);
        Assert.Equal([LoramusThalipedesGossip.QuestTheDemonHunter, LoramusThalipedesGossip.QuestLoramus], completed);
    }

    [Fact]
    public void AnachronosTheAncient_StartsFromQuestAccept_RunsAll55Stages_SealsGates_AndCompletesQuest8519()
    {
        uint[] entries =
        [
            AnachronosTheAncientAI.Entry,
            AnachronosTheAncientAI.NpcAnachronosQuestTrigger,
            AnachronosTheAncientAI.NpcFandralStaghelm,
            AnachronosTheAncientAI.NpcMerithraOfTheDream,
            AnachronosTheAncientAI.NpcCaelestrasz,
            AnachronosTheAncientAI.NpcArygos,
            AnachronosTheAncientAI.NpcKaldoreiInfantry,
            AnachronosTheAncientAI.NpcQirajiWasp,
            AnachronosTheAncientAI.NpcQirajiDrone,
            AnachronosTheAncientAI.NpcQirajiTank,
            AnachronosTheAncientAI.NpcAnubisathConqueror,
        ];

        CreatureTemplate[] templates =
        [
            .. entries.Select(e =>
            {
                CreatureTemplate t = Template(e, b =>
                {
                    b.Name = $"Entry{e}";
                    b.Faction = 35;
                });
                return e == AnachronosTheAncientAI.Entry
                    ? t with { ScriptName = AnachronosTheAncientAI.ScriptName }
                    : t;
            })
        ];

        int[] textIds =
        [
            AnachronosTheAncientAI.SayAnachronosIntro1,
            AnachronosTheAncientAI.SayFandralIntro2,
            AnachronosTheAncientAI.SayMerithraIntro3,
            AnachronosTheAncientAI.EmoteArygosNod,
            AnachronosTheAncientAI.SayCaelestraszIntro4,
            AnachronosTheAncientAI.EmoteMerithraBow,
            AnachronosTheAncientAI.SayMerithraIntro5,
            AnachronosTheAncientAI.SayMerithraAttack1,
            AnachronosTheAncientAI.SayArygosAttack2,
            AnachronosTheAncientAI.SayArygosAttack3,
            AnachronosTheAncientAI.SayCaelestraszAttack4,
            AnachronosTheAncientAI.SayCaelestraszAttack5,
            AnachronosTheAncientAI.SayAnachronosSeal1,
            AnachronosTheAncientAI.SayFandralSeal2,
            AnachronosTheAncientAI.SayAnachronosSeal3,
            AnachronosTheAncientAI.SayFandralSeal6,
            AnachronosTheAncientAI.EmoteFandralExhausted,
            AnachronosTheAncientAI.SayAnachronosEpilogue1,
            AnachronosTheAncientAI.SayAnachronosEpilogue2,
            AnachronosTheAncientAI.SayAnachronosEpilogue3,
            AnachronosTheAncientAI.EmoteAnachronosScepter,
            AnachronosTheAncientAI.SayFandralEpilogue4,
            AnachronosTheAncientAI.SayFandralEpilogue5,
            AnachronosTheAncientAI.EmoteFandralShatter,
            AnachronosTheAncientAI.SayAnachronosEpilogue6,
            AnachronosTheAncientAI.SayFandralEpilogue7,
            AnachronosTheAncientAI.EmoteAnachronosDisappointed,
            AnachronosTheAncientAI.EmoteFandralHateful,
            AnachronosTheAncientAI.SayAnachronosEpilogue8,
        ];
        CreatureAiText[] texts = [.. textIds.Select(id => new CreatureAiText(id, id.ToString(CultureInfo.InvariantCulture), 0, 0, 0))];

        CreatureContent content = new(
            templates,
            [Spawn(1, AnachronosTheAncientAI.NpcAnachronosQuestTrigger, -8042f, 1535f, 2.61f)],
            [],
            [],
            [],
            new CreatureAiContent([], texts));

        var quests = new Quests();
        var caster = new FakeCaster();
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(
            content,
            new CreatureAiServices { Spells = caster, ScriptQuests = quests, QuestEvents = quests });

        using (world)
        {
            GameObjectTemplate[] goTemplates =
            [
                GameObjectTestKit.GoTemplate(AnachronosTheAncientAI.GoCrystallineTear, GameObjectType.QuestGiver),
                GameObjectTestKit.GoTemplate(AnachronosTheAncientAI.GoMerithraWake, GameObjectType.Trap),
                GameObjectTestKit.GoTemplate(AnachronosTheAncientAI.GoAqBarrier, GameObjectType.Door),
                GameObjectTestKit.GoTemplate(AnachronosTheAncientAI.GoAqGateRunes, GameObjectType.Door),
                GameObjectTestKit.GoTemplate(AnachronosTheAncientAI.GoAqGateRoots, GameObjectType.Door),
            ];
            GameObjectSpawn[] goSpawns =
            [
                GameObjectTestKit.GoSpawn(1, AnachronosTheAncientAI.GoCrystallineTear, -8032f, 1535f, 60) with { MapId = 0, Z = 2.61f },
                GameObjectTestKit.GoSpawn(2, AnachronosTheAncientAI.GoAqBarrier, -8045f, 1535f, 60) with { MapId = 0, Z = 2.61f },
                GameObjectTestKit.GoSpawn(3, AnachronosTheAncientAI.GoAqGateRunes, -8046f, 1535f, 60) with { MapId = 0, Z = 2.61f },
                GameObjectTestKit.GoSpawn(4, AnachronosTheAncientAI.GoAqGateRoots, -8047f, 1535f, 60) with { MapId = 0, Z = 2.61f },
            ];
            var goSystem = new GameObjectMapSystem(map, new GameObjectContent(goTemplates, goSpawns, [], [], []));
            map.AddUpdater(goSystem);

            (Player player, _) = AddPlayer(world, 1, -8030f, 1535f);
            GameObject tear = Assert.Single(goSystem.GameObjects, g => g.Entry == AnachronosTheAncientAI.GoCrystallineTear);
            GameObject barrier = Assert.Single(goSystem.GameObjects, g => g.Entry == AnachronosTheAncientAI.GoAqBarrier);
            GameObject runes = Assert.Single(goSystem.GameObjects, g => g.Entry == AnachronosTheAncientAI.GoAqGateRunes);
            GameObject roots = Assert.Single(goSystem.GameObjects, g => g.Entry == AnachronosTheAncientAI.GoAqGateRoots);

            // Start the event via DungeonScriptHooks.OnQuestAccepted for quest 8519.
            Assert.True(DungeonScriptHooks.OnQuestAccepted(player, tear.Guid, AnachronosTheAncientAI.QuestAPawnOnTheEternalBoard));
            Creature anachronos = Assert.Single(system.Creatures, c => c.Entry == AnachronosTheAncientAI.Entry);
            var ai = Assert.IsType<AnachronosTheAncientAI>(anachronos.AI);
            Assert.Equal(player.Guid, ai.EventPlayerGuid);

            // Second accept while Anachronos is alive does not duplicate him.
            Assert.True(AnachronosTheAncientAI.TryStartFromQuestAccept(player, tear.Guid));
            Assert.Single(system.Creatures, c => c.Entry == AnachronosTheAncientAI.Entry);

            // Advance the scene through stage 0 & 1: dragons and armies spawn and engage.
            Run(world, 3000, step: 200);
            Assert.Equal(20, ai.KaldoreiArmy.Count);
            Assert.Equal(21, ai.QirajiArmy.Count);
            Assert.False(ai.FandralGuid.IsEmpty);
            Assert.False(ai.MerithraGuid.IsEmpty);
            Assert.False(ai.CaelestraszGuid.IsEmpty);
            Assert.False(ai.ArygosGuid.IsEmpty);

            // Advance through the rest of the 55-stage vision (approx. 160 seconds of virtual world time).
            bool sawBarrierReady = false;
            for (int tick = 0; tick < 450 && anachronos.IsAlive && system.FindCreature(anachronos.Guid) is not null; tick++)
            {
                Run(world, 500, step: 250);
                if (barrier.State == GameObjectState.Ready && runes.State == GameObjectState.Ready && roots.State == GameObjectState.Ready)
                {
                    sawBarrierReady = true;
                }
            }

            // Verify key dragon transforms, breaths, time stop, barrier/glyph/roots, scepter shatter/throw, and quest 8519 completion!
            Assert.True(sawBarrierReady);
            Assert.Contains(caster.Casts, c => c.Spell == AnachronosTheAncientAI.SpellGreenDragonTransform);
            Assert.Contains(caster.Casts, c => c.Spell == AnachronosTheAncientAI.SpellBlueDragonTransform);
            Assert.Contains(caster.Casts, c => c.Spell == AnachronosTheAncientAI.SpellRedDragonTransform);
            Assert.Contains(caster.Casts, c => c.Spell == AnachronosTheAncientAI.SpellNoxiousBreath);
            Assert.Contains(caster.Casts, c => c.Spell == AnachronosTheAncientAI.SpellFrostBreath);
            Assert.Contains(caster.Casts, c => c.Spell == AnachronosTheAncientAI.SpellFlameBreath);
            Assert.Contains(caster.Casts, c => c.Spell == AnachronosTheAncientAI.SpellTimeStop);
            Assert.Contains(caster.Casts, c => c.Spell == AnachronosTheAncientAI.SpellPrismaticBarrier);
            Assert.Contains(caster.Casts, c => c.Spell == AnachronosTheAncientAI.SpellGlyphOfWarding);
            Assert.Contains(caster.Casts, c => c.Spell == AnachronosTheAncientAI.SpellCallAncients);
            Assert.Contains(caster.Casts, c => c.Spell == AnachronosTheAncientAI.SpellShatterHammer);
            Assert.Contains(caster.Casts, c => c.Spell == AnachronosTheAncientAI.SpellThrowHammer);
            Assert.Contains(caster.Casts, c => c.Spell == AnachronosTheAncientAI.SpellBronzeDragonTransform);
            Assert.Contains(AnachronosTheAncientAI.QuestAPawnOnTheEternalBoard, quests.Explored);
            Assert.Contains(AnachronosTheAncientAI.QuestAPawnOnTheEternalBoard, quests.GroupExplored);
            Assert.Equal(GameObjectState.Active, barrier.State);
            Assert.Equal(GameObjectState.Active, runes.State);
            Assert.Equal(GameObjectState.Active, roots.State);
        }
    }

    [Fact]
    public void HurleyBlackbreath_RefusesToAttackRibbly_ClearsCronyImmunityAtWaypoint6_AndCastsFlameBreathAndDrunkenRage()
    {
        var caster = new FakeCaster();
        CreatureTemplate hurleyTemplate = Template(ArcaneCore.Game.Instances.Scripts.Classic.HurleyBlackbreathAI.Entry, b =>
        {
            b.Name = "Hurley Blackbreath";
            b.Faction = 54;
        }) with { ScriptName = ArcaneCore.Game.Instances.Scripts.Classic.HurleyBlackbreathAI.ScriptName };

        CreatureTemplate cronyTemplate = Template(ArcaneCore.Game.Instances.Scripts.Classic.HurleyBlackbreathAI.NpcBlackbreathCrony, b =>
        {
            b.Name = "Blackbreath Crony";
            b.Faction = 54;
        });

        CreatureTemplate ribblyTemplate = Template(ArcaneCore.Game.Instances.Scripts.Classic.HurleyBlackbreathAI.NpcRibblyScrewspigot, b =>
        {
            b.Name = "Ribbly Screwspigot";
            b.Faction = 54;
        });

        CreatureContent content = new(
            [hurleyTemplate, cronyTemplate, ribblyTemplate],
            [
                Spawn(1, hurleyTemplate.Entry, 0f, 0f, 0f),
                Spawn(2, cronyTemplate.Entry, 2f, 0f, 0f),
                Spawn(3, ribblyTemplate.Entry, 4f, 0f, 0f),
            ],
            [],
            [],
            [],
            new CreatureAiContent(
                [],
                [
                    new CreatureAiText(ArcaneCore.Game.Instances.Scripts.Classic.HurleyBlackbreathAI.YellHurleySpawn, "Who broke my keg?!", 0, 0, 0),
                    new CreatureAiText(ArcaneCore.Game.Instances.Scripts.Classic.HurleyBlackbreathAI.SayHurleyAggro, "Get away from those kegs!", 0, 0, 0),
                ]));

        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Spells = caster });
        using (world)
        {
            (Player player, _) = AddPlayer(world, 1, 1f, 0f);
            Creature hurley = Assert.Single(system.Creatures, c => c.Entry == hurleyTemplate.Entry);
            hurley.MaxHealth = 1000;
            hurley.Health = 1000;
            Creature crony = Assert.Single(system.Creatures, c => c.Entry == cronyTemplate.Entry);
            Creature ribbly = Assert.Single(system.Creatures, c => c.Entry == ribblyTemplate.Entry);

            var ai = Assert.IsType<ArcaneCore.Game.Instances.Scripts.Classic.HurleyBlackbreathAI>(hurley.AI);
            hurley.UnitFlags |= UnitFlags.ImmuneToPlayer | UnitFlags.ImmuneToNpc;
            crony.UnitFlags |= UnitFlags.ImmuneToPlayer | UnitFlags.ImmuneToNpc;

            // Waypoint 2 yells YellHurleySpawn; Waypoint 6 removes ImmuneToPlayer | ImmuneToNpc from Hurley and his cronies.
            ai.NotifyWaypointReached(2);
            Assert.NotEqual(UnitFlags.None, crony.UnitFlags & (UnitFlags.ImmuneToPlayer | UnitFlags.ImmuneToNpc));
            ai.NotifyWaypointReached(6);
            Assert.Equal(UnitFlags.None, hurley.UnitFlags & (UnitFlags.ImmuneToPlayer | UnitFlags.ImmuneToNpc));
            Assert.Equal(UnitFlags.None, crony.UnitFlags & (UnitFlags.ImmuneToPlayer | UnitFlags.ImmuneToNpc));

            // Hurley refuses to attack Ribbly or Ribbly's Crony.
            Assert.False(ai.AttackStart(ribbly));
            Assert.Null(hurley.Combat.Victim);

            // Against a player, Hurley engages, says SayHurleyAggro, casts Flame Breath, and casts Drunken Rage below 31% HP.
            Assert.True(ai.AttackStart(player));
            Assert.Same(player, hurley.Combat.Victim);

            Run(world, 13000, step: 500);
            Assert.Contains(caster.Casts, c => c.Spell == ArcaneCore.Game.Instances.Scripts.Classic.HurleyBlackbreathAI.SpellFlameBreath && ReferenceEquals(c.Target, player));
            Assert.DoesNotContain(caster.Casts, c => c.Spell == ArcaneCore.Game.Instances.Scripts.Classic.HurleyBlackbreathAI.SpellDrunkenRage);

            hurley.Health = 250;
            Run(world, 1000, step: 250);
            Assert.Contains(caster.Casts, c => c.Spell == ArcaneCore.Game.Instances.Scripts.Classic.HurleyBlackbreathAI.SpellDrunkenRage && ReferenceEquals(c.Target, hurley));
        }
    }
}

