using ArcaneCore.Game.Combat;
using System.Globalization;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.BlackrockDepths;
using ArcaneCore.Game.Instances.Scripts.Classic;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>
/// Blackrock Depths lane A1, from the behaviour specs 230-npc_ironhand_guardian, 230-npc_marshal_windsor, 230-npc_dughal_stormwing and
/// 230-npc_tobias_seecher: driven through the real instance script, creature system, escort engine and gossip handlers.
/// </summary>
public sealed class BlackrockDepthsJailBreakTests
{
    private const uint Guardian = 8982, Windsor = 9023, Reginald = 9682, Dughal = 9022, Tobias = 9679, Ograbisi = 9677, Shill = 9678, Crest = 9680, Jaz = 9681;
    private const uint Door = 170561, Crate = 166872;

    /// <summary>script_waypoint entry 9023 path 0 of the fixture (Point, X, Y, Z, WaitTimeMs).</summary>
    private static readonly (uint Point, float X, float Y, float Z, uint Wait)[] Path =
    [
        (1, 316.3360f, -225.5280f, -77.7258f, 2000),
        (2, 322.9600f, -207.1300f, -77.8700f, 0),
        (3, 281.0500f, -172.1600f, -75.1200f, 0),
        (4, 272.1900f, -139.1400f, -70.6100f, 0),
        (5, 283.6200f, -116.0900f, -70.2100f, 0),
        (6, 296.1800f, -94.3000f, -74.0800f, 0),
        (7, 294.5700f, -93.1100f, -74.0800f, 0),
        (8, 294.5700f, -93.1100f, -74.0800f, 10000),
        (9, 294.5700f, -93.1100f, -74.0800f, 3000),
        (10, 314.3100f, -74.3100f, -76.0900f, 0),
        (11, 360.2200f, -62.9300f, -66.7700f, 0),
        (12, 383.3800f, -69.4000f, -63.2500f, 0),
        (13, 389.9900f, -67.8600f, -62.5700f, 0),
        (14, 400.9800f, -72.0100f, -62.3100f, 0),
        (15, 404.2200f, -62.3000f, -63.5000f, 2000),
        (16, 404.2200f, -62.3000f, -63.5000f, 1500),
        (17, 407.6500f, -51.8600f, -63.9600f, 0),
        (18, 403.6100f, -51.7100f, -63.9200f, 1000),
        (19, 403.6100f, -51.7100f, -63.9200f, 2000),
        (20, 403.6100f, -51.7100f, -63.9200f, 1000),
        (21, 403.6100f, -51.7100f, -63.9200f, 1000),
        (22, 403.6100f, -52.7100f, -63.9200f, 4000),
        (23, 403.6100f, -52.7100f, -63.9200f, 4000),
        (24, 406.3300f, -54.8700f, -63.9500f, 0),
        (25, 403.8600f, -73.8800f, -62.0200f, 0),
        (26, 428.8000f, -81.3400f, -64.9100f, 0),
        (27, 557.0300f, -119.7100f, -61.8300f, 0),
        (28, 573.4000f, -124.3900f, -65.0700f, 0),
        (29, 593.9100f, -130.2900f, -69.2500f, 0),
        (30, 593.2100f, -132.1600f, -69.2500f, 0),
        (31, 593.2100f, -132.1600f, -69.2500f, 1000),
        (32, 593.2100f, -132.1600f, -69.2500f, 3000),
        (33, 622.8100f, -135.5500f, -71.9200f, 0),
        (34, 634.6800f, -151.2900f, -70.3200f, 0),
        (35, 635.0600f, -153.2500f, -70.3200f, 0),
        (36, 635.0600f, -153.2500f, -70.3200f, 3000),
        (37, 635.0600f, -153.2500f, -70.3200f, 5000),
        (38, 635.0600f, -153.2500f, -70.3200f, 2000),
        (39, 655.2500f, -172.3900f, -73.7200f, 0),
        (40, 654.7900f, -226.3000f, -83.0600f, 0),
        (41, 622.8500f, -268.8500f, -83.9600f, 0),
        (42, 579.4500f, -275.5600f, -80.4400f, 0),
        (43, 561.1900f, -266.8500f, -75.5900f, 0),
        (44, 547.9100f, -253.9200f, -70.3400f, 0),
        (45, 549.2000f, -252.4000f, -70.3400f, 0),
        (46, 549.2000f, -252.4000f, -70.3400f, 1000),
        (47, 549.2000f, -252.4000f, -70.3400f, 4000),
        (48, 555.3300f, -269.1600f, -74.4000f, 0),
        (49, 554.3100f, -270.8800f, -74.4000f, 0),
        (50, 554.3100f, -270.8800f, -74.4000f, 10000),
        (51, 554.3100f, -270.8800f, -74.4000f, 4000),
        (52, 536.1000f, -249.6000f, -67.4700f, 0),
        (53, 520.9400f, -216.6500f, -59.2800f, 0),
        (54, 505.9900f, -148.7400f, -62.1700f, 0),
        (55, 484.2100f, -56.2400f, -62.4300f, 0),
        (56, 470.3900f, -6.0100f, -70.1000f, 0),
        (57, 452.4500f, 29.8500f, -70.3700f, 1500),
        (58, 452.4500f, 29.8500f, -70.3700f, 15000)
    ];

    private sealed class Quests : IScriptQuestEvents, IEventAiQuestEvents
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

    /// <summary>The quest seam of the map (the world's QuestNpcMapUpdater): quest 4322 is incomplete or not.</summary>
    private sealed class FakeQuestSeam(bool incomplete) : IInstanceQuestRewards
    {
        public bool? IsRewarded(Player player, uint questId) => false;
        public bool? IsIncomplete(Player player, uint questId) => incomplete && questId == 4322;
        public void Update(Map map, uint diffMs) { }
        public void OnPlayerRemoved(Map map, Player player) { }
    }

    private sealed class Caster : ICreatureSpellCaster
    {
        public List<(uint Spell, long AtMs)> Casts { get; } = [];
        public long NowMs;
        public Func<uint, bool> Result = _ => true;
        public HashSet<uint> Auras { get; } = [];
        public List<uint> Removed { get; } = [];
        public CreatureCastResult Cast(Creature caster, uint spellId, Unit? target, bool triggered)
        {
            if (!Result(spellId)) return CreatureCastResult.Failed;
            Casts.Add((spellId, NowMs));
            if (spellId == 15167) Auras.Add(spellId);
            return CreatureCastResult.Ok;
        }
        public bool IsCasting(Creature caster) => false;
        public bool HasAura(Unit unit, uint spellId) => Auras.Contains(spellId);
        public void RemoveAuras(Unit unit, uint spellId) => Removed.Add(spellId);
        public void Interrupt(Creature caster) { }
        public void OnCreatureRemoved(Creature creature) { }
        public event Action<Unit, Unit, ArcaneCore.Game.Spells.SpellInfo>? SpellHit { add { } remove { } }
    }

    private static DungeonScriptTestKit NewKit(Caster caster, Quests quests, bool incomplete = true)
    {
        var texts = new List<CreatureAiText>();
        for (int id = -1230035; id <= -1230010; id++) texts.Add(new CreatureAiText(id, id.ToString(CultureInfo.InvariantCulture), 0, 0, 0));
        texts.Add(new CreatureAiText(-3230000, "Release Dughal option", 0, 0, 0));
        texts.Add(new CreatureAiText(-3230001, "Release Tobias option", 0, 0, 0));
        var services = new CreatureAiServices { Spells = caster, ScriptQuests = quests, QuestEvents = quests };
        uint[] entries = [Guardian, Windsor, Reginald, Dughal, Tobias, Ograbisi, Shill, Crest, Jaz];
        var kit = new DungeonScriptTestKit(map => new BlackrockDepthsInstance(map), entries, [Guardian, Windsor, Dughal, Tobias, Ograbisi, Shill, Crest, Jaz],
            [(Door, GameObjectType.Door), (Crate, GameObjectType.Goober)], new CreatureAiContent([], texts),
            Path.Select(p => (Windsor, 0u, new CreatureWaypoint(p.Point, p.X, p.Y, p.Z, 0, p.Wait))), services);
        kit.Map.AddUpdater(new FakeQuestSeam(incomplete));
        return kit;
    }

    private static BlackrockDepthsInstance Depths(DungeonScriptTestKit kit) => (BlackrockDepthsInstance)kit.Script;

    private static List<int> Said(DungeonScriptTestKit kit)
        => [.. kit.Sent(WorldOpcode.SmsgMessagechat).Select(p => int.Parse(ParseMonsterChat(p).Message, CultureInfo.InvariantCulture))];

    // --- npc_ironhand_guardian -----------------------------------------------------------------

    [Fact]
    public void IronhandGuardian_IdlesWhileNotStarted_NeverEntersCombat_AndHasNoMovement()
    {
        var caster = new Caster();
        using DungeonScriptTestKit kit = NewKit(caster, new Quests());
        Creature g = kit.Creature(Guardian);
        var ai = Assert.IsType<IronhandGuardianAI>(g.AI);
        kit.Player.Relocate(g.X + 1, g.Y, g.Z, 0, 0);
        for (int i = 0; i < 1200; i++) kit.Tick(50); // 60 s
        Assert.Empty(caster.Casts);
        Assert.False(g.Combat.IsInCombat);
        Assert.False(ai.AggroesOnSight);
        Assert.False(ai.CombatMovement);
        Assert.False(ai.MeleeEnabled);
        Assert.False(ai.AttackStart(kit.Player));
        Assert.False(g.Combat.IsInCombat);
    }

    [Fact]
    public void IronhandGuardian_WakesOnTheFirstUpdate_ThenFlamesOnItsTimers_AndKeepsFlamingAfterFailAndDone_AndSleepsAgainWhenNotStarted()
    {
        var caster = new Caster();
        using DungeonScriptTestKit kit = NewKit(caster, new Quests());
        Creature g = kit.Creature(Guardian);
        var ai = Assert.IsType<IronhandGuardianAI>(g.AI);
        long now = 0;
        void Run(int ms) { for (int t = 0; t < ms; t += 50) { now += 50; caster.NowMs = now; kit.Tick(50); } }

        Depths(kit).SetData(BlackrockDepthsInstance.TypeIronHall, EncounterState.InProgress);
        Run(50);
        Assert.Equal([IronhandGuardianAI.SpellStonedChannel], caster.Casts.Select(c => c.Spell));
        Assert.Contains(IronhandGuardianAI.SpellStoned, caster.Removed);
        Assert.True(ai.IsFlaming);
        Run(100_000);
        long[] gouts = [.. caster.Casts.Where(c => c.Spell == IronhandGuardianAI.SpellGoutOfFlame).Select(c => c.AtMs)];
        Assert.NotEmpty(gouts);
        Assert.InRange(gouts[0] - 100, 4000, 30_000 + 50); // first Gout 4-30 s after the reset (the wake-up update came first)
        for (int i = 1; i < gouts.Length; i++)
        {
            long gap = gouts[i] - gouts[i - 1];
            Assert.InRange(gap - 50, 20_000, 40_000);
            Assert.Equal(0, (gap - 50) % 1000); // whole seconds
        }

        Depths(kit).SetData(BlackrockDepthsInstance.TypeIronHall, EncounterState.Fail);
        int before = caster.Casts.Count;
        Run(100_000);
        Assert.True(caster.Casts.Count > before); // keeps flaming after Fail
        Depths(kit).SetData(BlackrockDepthsInstance.TypeIronHall, EncounterState.Done);
        before = caster.Casts.Count;
        Run(100_000);
        Assert.True(caster.Casts.Count > before); // and after Done
        Assert.Equal(1, caster.Casts.Count(c => c.Spell == IronhandGuardianAI.SpellStonedChannel));

        // A fresh instance: NOT_STARTED returns it to Idle, and it casts nothing.
        Depths(kit).SetData(BlackrockDepthsInstance.TypeIronHall, EncounterState.NotStarted);
        Run(50);
        Assert.False(ai.IsFlaming);
        before = caster.Casts.Count;
        Run(100_000);
        Assert.Equal(before, caster.Casts.Count);
    }

    [Fact]
    public void IronhandGuardian_RetriesAFailedVisualAndAFailedGoutEveryUpdate()
    {
        var caster = new Caster { Result = _ => false };
        using DungeonScriptTestKit kit = NewKit(caster, new Quests());
        var ai = Assert.IsType<IronhandGuardianAI>(kit.Creature(Guardian).AI);
        Depths(kit).SetData(BlackrockDepthsInstance.TypeIronHall, EncounterState.InProgress);
        for (int i = 0; i < 5; i++) kit.Tick(50);
        Assert.False(ai.IsFlaming); // the visual failed: retried (the aura removed again) rather than going on
        Assert.True(caster.Removed.Count >= 5);
        caster.Result = s => s == IronhandGuardianAI.SpellStonedChannel;
        kit.Tick(50);
        Assert.True(ai.IsFlaming);
        for (int i = 0; i < 700; i++) kit.Tick(50); // 35 s: the timer expired, every Gout cast fails
        Assert.Single(caster.Casts); // only the visual so far
        Assert.True(ai.GoutRemainingMs < 50); // and it stays expired
        caster.Result = _ => true;
        kit.Tick(50);
        Assert.Single(caster.Casts, c => c.Spell == IronhandGuardianAI.SpellGoutOfFlame); // the very next update casts
    }

    // --- npc_dughal_stormwing / npc_tobias_seecher ----------------------------------------------

    [Fact]
    public void Gossip_ScriptNamesAreRegistered_AndOfferTheReleaseOnlyForAnIncompleteJailBreak()
    {
        var factory = new CreatureAiFactory();
        Assert.IsType<DughalStormwingGossip>(factory.NpcGossipOf("npc_dughal_stormwing"));
        Assert.IsType<TobiasSeecherGossip>(factory.NpcGossipOf("npc_tobias_seecher"));
        Assert.Contains(4322u, factory.ScriptedEventQuests);

        using DungeonScriptTestKit kit = NewKit(new Caster(), new Quests());
        ScriptedGossipMenu dughal = new DughalStormwingGossip().Hello(kit.Player, kit.Npc(Dughal))!;
        Assert.Equal(2846u, dughal.NpcTextId);
        ScriptedGossipItem line = Assert.Single(dughal.Items);
        Assert.Equal("Release Dughal option", line.Text);
        Assert.Equal(1001u, line.Action);
        ScriptedGossipMenu tobias = new TobiasSeecherGossip().Hello(kit.Player, kit.Npc(Tobias))!;
        Assert.Equal(2847u, tobias.NpcTextId);
        Assert.Equal("Release Tobias option", Assert.Single(tobias.Items).Text);

        using DungeonScriptTestKit none = NewKit(new Caster(), new Quests(), incomplete: false);
        Assert.Equal(2846u, new DughalStormwingGossip().Hello(none.Player, none.Npc(Dughal))!.NpcTextId);
        Assert.Empty(new DughalStormwingGossip().Hello(none.Player, none.Npc(Dughal))!.Items);
        Assert.Empty(new TobiasSeecherGossip().Hello(none.Player, none.Npc(Tobias))!.Items);
    }

    [Fact]
    public void Gossip_TheOptionTextIsNeverInvented_ItIsOmittedWhenTheTextStoreLacksIt()
    {
        var services = new CreatureAiServices { Spells = new Caster() };
        using var kit = new DungeonScriptTestKit(map => new BlackrockDepthsInstance(map), [Dughal], [Dughal], [], new CreatureAiContent([], []), aiServices: services);
        kit.Map.AddUpdater(new FakeQuestSeam(true));
        ScriptedGossipMenu menu = new DughalStormwingGossip().Hello(kit.Player, kit.Npc(Dughal))!;
        Assert.Equal(2846u, menu.NpcTextId);
        Assert.Empty(menu.Items);
    }

    [Fact]
    public void Dughal_Release_SetsSpecial_SaysTheLine_RemovesTheFlag_AndClosesTheMenu_AndOtherActionsDoNothing()
    {
        using DungeonScriptTestKit kit = NewKit(new Caster(), new Quests());
        Creature dughal = kit.Creature(Dughal);
        dughal.NpcFlags |= (uint)NpcFlags.Gossip;
        var gossip = new DughalStormwingGossip();
        Assert.Equal(default, gossip.SelectReply(kit.Player, kit.Npc(Dughal), 1, 1002));
        Assert.Equal(EncounterState.NotStarted, Depths(kit).GetData(BlackrockDepthsInstance.TypeQuestJailBreak));
        Assert.Empty(Said(kit));

        ScriptedGossipReply reply = gossip.SelectReply(kit.Player, kit.Npc(Dughal), 1, 1001);

        Assert.True(reply.Close);
        Assert.Equal(EncounterState.Special, Depths(kit).GetData(BlackrockDepthsInstance.TypeQuestJailBreak));
        Assert.Equal([-1230010], Said(kit));
        Assert.Equal(0u, dughal.NpcFlags & (uint)NpcFlags.Gossip);
    }

    [Fact]
    public void Tobias_Release_SaysOneOfTwoLines_AndBothOccurOver200Selections()
    {
        using DungeonScriptTestKit kit = NewKit(new Caster(), new Quests());
        Creature tobias = kit.Creature(Tobias);
        var gossip = new TobiasSeecherGossip();
        Assert.Equal(default, gossip.SelectReply(kit.Player, kit.Npc(Tobias), 1, 5));
        for (int i = 0; i < 200; i++)
        {
            tobias.NpcFlags |= (uint)NpcFlags.Gossip;
            Assert.True(gossip.SelectReply(kit.Player, kit.Npc(Tobias), 1, 1001).Close);
            Assert.Equal(0u, tobias.NpcFlags & (uint)NpcFlags.Gossip);
        }

        List<int> said = Said(kit);
        Assert.Equal(200, said.Count);
        Assert.All(said, id => Assert.True(id is -1230032 or -1230033));
        Assert.Contains(-1230032, said);
        Assert.Contains(-1230033, said);
        Assert.Equal(EncounterState.Special, Depths(kit).GetData(BlackrockDepthsInstance.TypeQuestJailBreak));
    }

    // --- npc_marshal_windsor --------------------------------------------------------------------

    private static (Creature Windsor, MarshalWindsorAI Ai) Start(DungeonScriptTestKit kit)
    {
        Creature w = kit.Creature(Windsor);
        var ai = Assert.IsType<MarshalWindsorAI>(w.AI);
        kit.Creatures.NearTeleport(w, Path[0].X, Path[0].Y, Path[0].Z, 1.257f);
        kit.Player.Relocate(w.X, w.Y, w.Z, 0, 0);
        ai.OnQuestAccept(kit.Player, 4322);
        return (w, ai);
    }

    private static void Step(DungeonScriptTestKit kit, Creature w, uint ms = 100)
    {
        kit.Player.Relocate(w.X, w.Y, w.Z, 0, 0);
        kit.Tick(ms);
    }

    [Fact]
    public void Windsor_AcceptingTheQuest_TakesTheEscortFactionAndStartsALinkedWalk_SayingTheStartLineAtPointOne()
    {
        using DungeonScriptTestKit kit = NewKit(new Caster(), new Quests());
        (Creature w, MarshalWindsorAI ai) = Start(kit);
        Assert.Equal(231u, w.FactionTemplate);
        Assert.True(ai.HasEscortState(EscortAI.EscortState.Escorting));
        Assert.False(ai.IsRunning);
        for (int i = 0; i < 400 && !Said(kit).Contains(-1230014); i++) Step(kit, w);
        Assert.Contains(-1230014, Said(kit));
        Assert.Equal(EncounterState.InProgress, Depths(kit).GetData(BlackrockDepthsInstance.TypeQuestJailBreak));
    }

    [Fact]
    public void Windsor_PausesAtDughalsCell_UntilSpecial_ThenResumes_AndTheDataReadsInProgressAgain()
    {
        using DungeonScriptTestKit kit = NewKit(new Caster(), new Quests());
        (Creature w, MarshalWindsorAI ai) = Start(kit);
        for (int i = 0; i < 6000 && !Said(kit).Contains(-1230015); i++) Step(kit, w);
        Assert.Contains(-1230015, Said(kit));
        Assert.Equal(1, ai.Counter);
        Assert.NotEqual(0u, kit.Creature(Dughal).NpcFlags & (uint)NpcFlags.Gossip);
        Assert.True(ai.HasEscortState(EscortAI.EscortState.Paused));
        int index = ai.CurrentWaypointIndex;
        for (int i = 0; i < 300; i++) Step(kit, w); // 30 s of waiting
        Assert.Equal(index, ai.CurrentWaypointIndex);
        Assert.True(ai.HasEscortState(EscortAI.EscortState.Paused));

        new DughalStormwingGossip().SelectReply(kit.Player, kit.Npc(Dughal), 1, 1001);
        Step(kit, w);
        Assert.False(ai.HasEscortState(EscortAI.EscortState.Paused));
        Assert.Equal(EncounterState.InProgress, Depths(kit).GetData(BlackrockDepthsInstance.TypeQuestJailBreak));
    }

    [Fact]
    public void Windsor_APrisonerDeathSetsSpecialOnlyWhileInProgress()
    {
        using DungeonScriptTestKit kit = NewKit(new Caster(), new Quests());
        kit.Kill(Ograbisi);
        Assert.Equal(EncounterState.NotStarted, Depths(kit).GetData(BlackrockDepthsInstance.TypeQuestJailBreak));
        foreach (uint prisoner in new[] { Jaz, Shill, Crest })
        {
            Depths(kit).SetData(BlackrockDepthsInstance.TypeQuestJailBreak, EncounterState.InProgress);
            kit.Kill(prisoner);
            Assert.Equal(EncounterState.Special, Depths(kit).GetData(BlackrockDepthsInstance.TypeQuestJailBreak));
        }

        Depths(kit).SetData(BlackrockDepthsInstance.TypeQuestJailBreak, EncounterState.InProgress);
        kit.Kill(Dughal); // Dughal and Tobias release through gossip, not through their death
        Assert.Equal(EncounterState.InProgress, Depths(kit).GetData(BlackrockDepthsInstance.TypeQuestJailBreak));
    }

    [Fact]
    public void Windsor_FullJailBreak_PausesAtEveryCell_RunsTheScene_AndCompletesTheQuest()
    {
        var caster = new Caster();
        var quests = new Quests();
        using DungeonScriptTestKit kit = NewKit(caster, quests);
        GameObject door = kit.Object(Door), crate = kit.Object(Crate); // looked up before the party leaves their grid
        foreach (uint entry in new[] { Dughal, Tobias, Ograbisi, Shill, Crest, Jaz }) kit.Map.SetActive(kit.Creature(entry), true);
        kit.Map.SetActive(door, true);
        kit.Map.SetActive(crate, true);
        (Creature w, MarshalWindsorAI ai) = Start(kit);
        var acted = new HashSet<int>();
        bool sawReginald = false, doorOpen = false, crateUsed = false;
        for (int i = 0; i < 60_000 && !Said(kit).Contains(-1230031); i++)
        {
            Step(kit, w);
            sawReginald |= w.Template.Entry == Reginald;
            doorOpen |= door.State == GameObjectState.Active;
            crateUsed |= crate.LootState == GameObjectLootState.Activated;
            if (!ai.HasEscortState(EscortAI.EscortState.Paused) || !acted.Add(ai.Counter)) continue;
            switch (ai.Counter)
            {
                case 1: new DughalStormwingGossip().SelectReply(kit.Player, kit.Npc(Dughal), 1, 1001); break;
                case 2: kit.Kill(Ograbisi); break;
                case 3: kit.Kill(Jaz); break; // the second release of the shared cell
                case 4: kit.Kill(Shill); break;
                case 5: kit.Kill(Crest); break;
                case 6: new TobiasSeecherGossip().SelectReply(kit.Player, kit.Npc(Tobias), 1, 1001); break;
            }
        }

        Assert.Equal([1, 2, 3, 4, 5, 6], acted.Order());
        Assert.True(sawReginald);
        Assert.True(doorOpen);
        Assert.True(crateUsed);
        Assert.Single(caster.Casts, c => c.Spell == 15167); // not recast while the aura is present
        List<int> said = Said(kit);
        int[] expected = [-1230014, -1230015, -1230016, -1230017, -1230018, -1230019, -1230020, -1230021, -1230022, -1230023, -1230024,
            -1230025, -1230026, -1230027, -1230028, -1230029, -1230030, -1230031];
        foreach (int id in expected) Assert.Contains(id, said);
        Assert.Equal(EncounterState.Done, Depths(kit).GetData(BlackrockDepthsInstance.TypeQuestJailBreak));
        Assert.Equal([(kit.Player, 4322u)], quests.Completed);
        Assert.Empty(quests.Failed);
    }

    [Fact]
    public void Windsor_APrisonerKilledBeforeItsCell_SetsSpecialThatIsConsumedUnused()
    {
        using DungeonScriptTestKit kit = NewKit(new Caster(), new Quests());
        (Creature w, MarshalWindsorAI ai) = Start(kit);
        // Spec section 11, item 2 (reference behaviour kept): the next update consumes the Special without resuming anything.
        Depths(kit).SetData(BlackrockDepthsInstance.TypeQuestJailBreak, EncounterState.InProgress);
        kit.Kill(Crest);
        Assert.Equal(EncounterState.Special, Depths(kit).GetData(BlackrockDepthsInstance.TypeQuestJailBreak));
        Step(kit, w);
        Assert.Equal(EncounterState.InProgress, Depths(kit).GetData(BlackrockDepthsInstance.TypeQuestJailBreak));
        Assert.False(ai.HasEscortState(EscortAI.EscortState.Paused));
    }

    [Fact]
    public void Windsor_DyingMidEscort_FailsTheQuestForTheGroup()
    {
        var quests = new Quests();
        using DungeonScriptTestKit kit = NewKit(new Caster(), quests);
        (Creature w, _) = Start(kit);
        for (int i = 0; i < 50; i++) Step(kit, w);
        kit.Map.Combat.Kill(null, w);
        Assert.Equal([(kit.Player, 4322u)], quests.Failed);
        Assert.Empty(quests.Completed);
    }

    [Fact]
    public void Windsor_OnAggro_SaysExactlyOneOfThreeLines_AllOfThemOccur()
    {
        using DungeonScriptTestKit kit = NewKit(new Caster(), new Quests());
        Creature w = kit.Creature(Windsor);
        var ai = Assert.IsType<MarshalWindsorAI>(w.AI);
        for (int i = 0; i < 90; i++)
        {
            int before = Said(kit).Count;
            ai.OnAggro(kit.Player);
            Assert.Equal(before + 1, Said(kit).Count);
        }

        List<int> said = Said(kit);
        Assert.All(said, id => Assert.True(id is -1230011 or -1230012 or -1230013));
        Assert.Equal(3, said.Distinct().Count());
    }
}
