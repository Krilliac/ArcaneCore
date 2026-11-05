using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Creatures.Scripts;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>
/// Lane Y90 (not-ported creature script behaviours, part 1): Ruul's NPC immunity, Daphne's rifle swap on the script equipment primitive,
/// the Defias Traitor's defensive react state, Grark's levitating drake, Taelan's Scarlet Subterfuge scene on the quest-rewarded hook, and the
/// escort's assist of a player in combat and aggro of nearby enemies. Written without a build or test run in the lane (the full gate runs once
/// on the integration branch): none of these has been seen to pass or fail.
/// </summary>
public sealed class Y90CreatureScriptsTests
{
    private const float Z = 83.5f;
    private const uint RifleItem = 2511, SwordItem = 1899, ShieldItem = 143, BowItem = 2551;
    private const uint EnemyEntry = 3925;

    private static readonly ItemTemplate Rifle = new() { Entry = RifleItem, Class = 2, SubClass = 3, DisplayId = 7001, Material = 1, InventoryType = 15, Sheath = 2 };
    private static readonly ItemTemplate Sword = new() { Entry = SwordItem, Class = 2, SubClass = 7, DisplayId = 7420, Material = 1, InventoryType = 13, Sheath = 3 };
    private static readonly ItemTemplate Shield = new() { Entry = ShieldItem, Class = 4, SubClass = 6, DisplayId = 18730, Material = 1, InventoryType = 14, Sheath = 4 };
    private static readonly ItemTemplate Bow = new() { Entry = BowItem, Class = 2, SubClass = 2, DisplayId = 8105, Material = 2, InventoryType = 15, Sheath = 2 };

    private static ItemTemplate? Items(uint entry) => entry switch
    {
        RifleItem => Rifle,
        SwordItem => Sword,
        ShieldItem => Shield,
        BowItem => Bow,
        _ => null,
    };

    private sealed class RecordingCaster : ICreatureSpellCaster
    {
        public List<(Creature Caster, uint Spell, Unit? Target, bool Triggered)> Casts { get; } = [];

        public event Action<Unit, Unit, SpellInfo>? SpellHit
        {
            add { }
            remove { }
        }

        public CreatureCastResult Cast(Creature caster, uint spellId, Unit? target, bool triggered)
        {
            Casts.Add((caster, spellId, target, triggered));
            return CreatureCastResult.Ok;
        }

        public bool IsCasting(Creature caster) => false;

        public bool HasAura(Unit unit, uint spellId) => false;

        public void Interrupt(Creature caster)
        {
        }

        public void OnCreatureRemoved(Creature creature)
        {
        }
    }

    private sealed class Quests : IScriptQuestEvents, IEventAiQuestEvents
    {
        public bool IsQuestIncomplete(Player player, uint questId) => false;

        public void AreaExploredOrEventHappens(Player player, uint questId)
        {
        }

        public void FailQuest(Player player, uint questId)
        {
        }

        public void KilledMonsterCredit(Player player, uint creatureEntry, ObjectGuid source)
        {
        }

        public void GroupEventFailHappens(Player player, uint questId)
        {
        }

        public IReadOnlyList<Player> GroupMembersOf(Player player) => [];

        public void EventHappened(Player player, uint questId, Creature source, bool rewardGroup)
        {
        }

        public void KillCredit(Player player, uint creatureEntry, Creature source)
        {
        }
    }

    /// <summary>One script creature next to a player, with extra templates and spawns, a recording caster, a quest service and an item store.</summary>
    private sealed class Rig : IDisposable
    {
        public Rig(uint entry, Action<CreatureTemplateBuilder>? configure = null, int[]? textIds = null, bool hostile = false, float x = 0f, float y = 0f,
            float z = Z, CreatureTemplate[]? extraTemplates = null, CreatureSpawn[]? extraSpawns = null, int pathLength = 0, bool creaturesAggroCreatures = true,
            Func<CreatureTemplate, CreatureTemplate>? adjust = null, float playerOffset = 1f)
        {
            var aiContent = new CreatureAiContent([], [.. (textIds ?? []).Select(id => new CreatureAiText(id, $"text{id}", 6, 0, 0))]);
            CreatureTemplate template = Template(entry, configure);
            CreatureContent content = new([adjust?.Invoke(template) ?? template, .. extraTemplates ?? []], [Spawn(1, entry, x, y, z), .. extraSpawns ?? []], [], [], [],
                aiContent, scriptWaypoints: Enumerable.Range(1, pathLength).Select(i => (entry, EscortAI.EscortPathId, new CreatureWaypoint((uint)i, x + (i * 3f), y, z, 0f, 0))));
            var services = new CreatureAiServices
            {
                ScriptQuests = Quest, QuestEvents = Quest, Spells = Caster, ItemTemplateOf = Items,
                Hostility = hostile ? new AlwaysHostile() : FactionCreatureHostility.Empty,
            };
            (World, Map, System) = CreateAiSystem(content, services, new CreatureOptions { CreatureAggroOnCreatures = creaturesAggroCreatures });
            (Player, Session) = AddPlayer(World, 1, x + playerOffset, y);
            Npc = Assert.Single(System.Creatures, c => c.Template.Entry == entry);
        }

        public RecordingCaster Caster { get; } = new();

        public Quests Quest { get; } = new();

        public WorldRuntime World { get; }

        public Map Map { get; }

        public CreatureMapSystem System { get; }

        public Player Player { get; }

        public FakeSession Session { get; }

        public Creature Npc { get; }

        public T Ai<T>() where T : CreatureAI => Assert.IsType<T>(Npc.AI);

        public List<string> Heard() => [.. Packets(Session, WorldOpcode.SmsgMessagechat).Select(p => ParseMonsterChat(p).Message)];

        /// <summary>Ticks until <paramref name="done"/> (100 ms steps); false when it is still not true after <paramref name="limitMs"/>.</summary>
        public bool RunUntil(Func<bool> done, int limitMs = 30_000)
        {
            for (int elapsed = 0; elapsed < limitMs && !done(); elapsed += 100)
            {
                World.RunTick(100);
            }

            return done();
        }

        public void Dispose()
        {
        }
    }

    /// <summary>Puts the escort on <paramref name="point"/> as if it had walked there, and lets the escort AI hear of the arrival.</summary>
    private static void Reach(Rig rig, uint point)
    {
        var ai = Assert.IsAssignableFrom<EscortAI>(rig.Npc.AI);
        Assert.True(ai.SetNextWaypointByPointId(point));
        rig.Npc.Relocate(rig.Npc.X + 1f, rig.Npc.Y, rig.Npc.Z);
        ai.OnMovementInform(MovementGeneratorType.Point, point);
    }

    private static CreatureTemplate Hostile(uint entry) => Template(entry, b =>
    {
        b.Name = "Hostile" + entry;
        b.Faction = 14;
    });

    private static void AssertSlot(Creature creature, int slot, ItemTemplate item)
    {
        int info = UpdateFields.UnitVirtualItemInfo + (slot * 2);
        Assert.Equal(item.DisplayId, creature.GetUInt32(UpdateFields.UnitVirtualItemSlotDisplay + slot));
        Assert.Equal((byte)item.Class, creature.GetByte(info, 0));
        Assert.Equal((byte)item.SubClass, creature.GetByte(info, 1));
        Assert.Equal((byte)item.InventoryType, creature.GetByte(info, 3));
    }

    private static void AssertEmpty(Creature creature, int slot)
    {
        Assert.Equal(0u, creature.GetUInt32(UpdateFields.UnitVirtualItemSlotDisplay + slot));
        Assert.Equal(0u, creature.GetUInt32(UpdateFields.UnitVirtualItemInfo + (slot * 2)));
    }

    // --- script equipment primitive ----------------------------------------------------------------------------

    [Fact]
    public void SetScriptEquipment_ShowsTheItem_LeavesNoChangeSlotsAlone_AndTheDefaultComesBack()
    {
        const uint equipmentId = 68;
        CreatureTemplate template = Template(GuardEntry) with { EquipmentTemplateId = equipmentId };
        CreatureContent content = new([template], [Spawn(1, GuardEntry, 0, 0)], [], [], [], equipment: [new CreatureEquipment(equipmentId, SwordItem, ShieldItem, 0)]);
        var guard = new Creature(1, template, content.GetSpawns(0)[0], content, new Random(1), itemResolver: Items);
        AssertSlot(guard, 0, Sword);
        AssertSlot(guard, 1, Shield);
        AssertEmpty(guard, 2);

        // Putting the default back on a creature no script changed touches nothing.
        guard.SetScriptEquipment(Items, loadDefault: true);
        AssertSlot(guard, 0, Sword);
        Assert.Null(guard.ScriptEquipmentDefault);

        guard.SetScriptEquipment(Items, loadDefault: false, ranged: BowItem);
        AssertSlot(guard, 0, Sword);
        AssertSlot(guard, 1, Shield);
        AssertSlot(guard, 2, Bow);

        // 0 empties a slot; an item the store does not know leaves its slot as it was.
        guard.SetScriptEquipment(Items, loadDefault: false, mainHand: 0, offHand: 424242);
        AssertEmpty(guard, 0);
        AssertSlot(guard, 1, Shield);

        guard.SetScriptEquipment(Items, loadDefault: true);
        AssertSlot(guard, 0, Sword);
        AssertSlot(guard, 1, Shield);
        AssertEmpty(guard, 2);
        Assert.Null(guard.ScriptEquipmentDefault);
    }

    [Fact]
    public void Daphne_TakesTheRifleAtPointFive_AndPutsItAwayAtPointTwelve()
    {
        using var rig = new Rig(DaphneStilwellAI.Entry, b => b.Faction = 123, pathLength: 16);
        var ai = rig.Ai<DaphneStilwellAI>();
        ai.OnQuestAccept(rig.Player, DaphneStilwellAI.QuestTomeOfValor);
        AssertEmpty(rig.Npc, 2);

        Reach(rig, 5);

        AssertSlot(rig.Npc, 2, Rifle);
        Assert.Equal(2, rig.Npc.GetByte(UpdateFields.UnitFieldBytes2, 0)); // SHEATH_STATE_RANGED

        Reach(rig, 12);

        AssertEmpty(rig.Npc, 2);
        Assert.Equal(0, rig.Npc.GetByte(UpdateFields.UnitFieldBytes2, 0)); // SHEATH_STATE_UNARMED
    }

    // --- NPC immunity, react state, levitation -----------------------------------------------------------------

    [Fact]
    public void Ruul_IsImmuneToCreaturesFromPointThirtyTwoUntilHeLeavesAtThirtySix()
    {
        using var rig = new Rig(RuulSnowhoofAI.Entry, pathLength: 36);
        var ai = rig.Ai<RuulSnowhoofAI>();
        ai.OnQuestAccept(rig.Player, RuulSnowhoofAI.QuestFreedomToRuul);
        Assert.Equal(UnitFlags.None, rig.Npc.UnitFlags & UnitFlags.ImmuneToNpc);

        Reach(rig, 31);
        Assert.Equal(UnitFlags.None, rig.Npc.UnitFlags & UnitFlags.ImmuneToNpc);

        Reach(rig, 32);
        Assert.Equal(UnitFlags.ImmuneToNpc, rig.Npc.UnitFlags & UnitFlags.ImmuneToNpc);

        Reach(rig, 34);
        Assert.Equal(UnitFlags.ImmuneToNpc, rig.Npc.UnitFlags & UnitFlags.ImmuneToNpc);

        Reach(rig, 36);
        Assert.Equal(UnitFlags.None, rig.Npc.UnitFlags & UnitFlags.ImmuneToNpc);
    }

    [Fact]
    public void DefiasTraitor_IsDefensive_AtSpawnAndAfterEveryRespawn()
    {
        using var rig = new Rig(DefiasTraitorAI.Entry);
        var ai = rig.Ai<DefiasTraitorAI>();
        Assert.Equal(CreatureReactState.Defensive, rig.Npc.ReactState);

        rig.Npc.ReactState = CreatureReactState.Aggressive;
        ai.OnRespawn();
        Assert.Equal(CreatureReactState.Defensive, rig.Npc.ReactState);

        rig.Npc.ReactState = CreatureReactState.Aggressive;
        ai.OnRespawn();
        Assert.Equal(CreatureReactState.Defensive, rig.Npc.ReactState);
    }

    [Fact]
    public void Grark_OnlyTheFirstSearscaleDrakeLevitates()
    {
        using var rig = new Rig(GrarkLorkrubAI.Entry, extraTemplates: [Hostile(GrarkLorkrubAI.NpcSearscaleDrake)], pathLength: 30);
        rig.Ai<GrarkLorkrubAI>().OnQuestAccept(rig.Player, GrarkLorkrubAI.QuestPrecariousPredicament);

        Reach(rig, 28);

        List<Creature> drakes = [.. rig.System.Creatures.Where(c => c.Template.Entry == GrarkLorkrubAI.NpcSearscaleDrake)];
        Assert.Equal(3, drakes.Count);
        Creature flyer = Assert.Single(drakes, d => d.Movement.HasFlag(MovementFlags.Levitating));
        Assert.True(flyer.Z > 220f, "the drake summoned first, at z 233.4, is the one that flies");
    }

    // --- Taelan Fordring: The Scarlet Subterfuge ---------------------------------------------------------------

    [Fact]
    public void Taelan_RewardingTheScarletSubterfuge_PlaysTheKneelingSceneAndTheCavaliersTurnOnHim()
    {
        const float high = 170f;
        int[] texts = [-1001072, -1001073, -1001074, -1001075, TaelanFordringAI.SayScarletComplete1, TaelanFordringAI.SayScarletComplete2];
        using var rig = new Rig(TaelanFordringAI.Entry, b =>
            {
                b.Faction = 35;
                b.NpcFlags = (uint)NpcFlags.QuestGiver;
            }, texts, hostile: true, z: high, creaturesAggroCreatures: false,
            extraTemplates: [Hostile(TaelanFordringAI.NpcScarletCavalier)],
            extraSpawns:
            [
                Spawn(2, TaelanFordringAI.NpcScarletCavalier, 3f, 0f, high), Spawn(3, TaelanFordringAI.NpcScarletCavalier, -3f, 1f, high),
                Spawn(4, TaelanFordringAI.NpcScarletCavalier, 0f, 4f, high),
                Spawn(5, TaelanFordringAI.NpcScarletCavalier, 0f, -4f, 150f), // the floor below: not part of the scene
            ]);
        var ai = rig.Ai<TaelanFordringAI>();
        List<Creature> cavaliers = [.. rig.System.Creatures.Where(c => c.Template.Entry == TaelanFordringAI.NpcScarletCavalier)];
        Assert.Equal(4, cavaliers.Count);
        Creature downstairs = Assert.Single(cavaliers, c => c.Z < 160f);
        rig.Session.Clear();

        // A different quest does nothing.
        ai.OnQuestRewarded(rig.Player, 999_999);
        Assert.Equal(StandState.Stand, rig.Npc.StandState);

        ai.OnQuestRewarded(rig.Player, TaelanFordringAI.QuestScarletSubterfuge);

        Assert.Equal(StandState.Kneel, rig.Npc.StandState);
        Assert.Equal(0u, rig.Npc.NpcFlags & (uint)NpcFlags.QuestGiver);
        List<string> heard = rig.Heard();
        Assert.Equal(["text-1001072", "text-1001073", "text-1001074"], heard.Order().ToList()); // one yell per cavaliers' floor, none from the floor below

        // He stands up with the spell that knocks him down, then forces the cavaliers to attack him.
        Assert.True(rig.RunUntil(() => rig.Npc.StandState == StandState.Stand), "he stands up after the cavaliers gathered");
        Assert.Contains(rig.Caster.Casts, c => ReferenceEquals(c.Caster, rig.Npc) && c.Spell == TaelanFordringAI.SpellTaelanSuffering);
        Assert.True(rig.RunUntil(() => rig.Npc.HasTemporaryFaction), "the cavaliers are set on him");
        Assert.Equal(TaelanFordringAI.FactionEscortNeutralFriendPassive, rig.Npc.FactionTemplate);
        Assert.All(cavaliers.Where(c => !ReferenceEquals(c, downstairs)), c => Assert.Same(rig.Npc, c.Combat.Victim));
        Assert.Null(downstairs.Combat.Victim);

        // Back home he casts his aura, then gets his quest-giver flag back.
        ai.OnReachedHome();
        Assert.Contains(rig.Caster.Casts, c => ReferenceEquals(c.Caster, rig.Npc) && c.Spell == TaelanFordringAI.SpellDevotionAura);
        Assert.Equal(0u, rig.Npc.NpcFlags & (uint)NpcFlags.QuestGiver);
        Assert.True(rig.RunUntil(() => (rig.Npc.NpcFlags & (uint)NpcFlags.QuestGiver) != 0));

        // Arriving home again does not play the scene again.
        int casts = rig.Caster.Casts.Count;
        ai.OnReachedHome();
        Assert.Equal(casts, rig.Caster.Casts.Count);
    }

    // --- assist of a player in combat, aggro of nearby enemies -------------------------------------------------

    private static Rig AssistRig(bool canAssist, float enemyDistance, bool enemyFightsPlayer, out Creature enemy)
    {
        var rig = new Rig(RuulSnowhoofAI.Entry, hostile: true, pathLength: 36, creaturesAggroCreatures: false,
            adjust: t => canAssist ? t with { TypeFlags = CreatureMapSystem.TypeFlagCanAssist } : t,
            extraTemplates: [Hostile(EnemyEntry)], extraSpawns: [Spawn(2, EnemyEntry, enemyDistance, 0f)]);
        enemy = Assert.Single(rig.System.Creatures, c => c.Template.Entry == EnemyEntry);
        if (enemyFightsPlayer)
        {
            Assert.True(rig.System.AttackStart(enemy, rig.Player));
            Assert.Same(rig.Player, enemy.Combat.Victim);
        }

        return rig;
    }

    [Fact]
    public void AnEscortWithTheCanAssistFlag_TakesOnAnEnemyFarOutsideItsAggroRadiusThatFightsAPlayer()
    {
        using Rig rig = AssistRig(canAssist: true, enemyDistance: 70f, enemyFightsPlayer: true, out Creature enemy);
        var ai = rig.Ai<RuulSnowhoofAI>();
        ai.OnQuestAccept(rig.Player, RuulSnowhoofAI.QuestFreedomToRuul);
        Assert.True(ai.HasEscortState(EscortAI.EscortState.Escorting));

        ai.MoveInLineOfSight(enemy);

        Assert.Same(enemy, rig.Npc.Combat.Victim);
    }

    [Fact]
    public void TheAssist_NeedsTheFlag()
    {
        using Rig rig = AssistRig(canAssist: false, enemyDistance: 70f, enemyFightsPlayer: true, out Creature enemy);
        rig.Ai<RuulSnowhoofAI>().OnQuestAccept(rig.Player, RuulSnowhoofAI.QuestFreedomToRuul);

        rig.Ai<RuulSnowhoofAI>().MoveInLineOfSight(enemy);

        Assert.Null(rig.Npc.Combat.Victim);
    }

    [Fact]
    public void TheAssist_NeedsAnEscortInProgress()
    {
        using Rig rig = AssistRig(canAssist: true, enemyDistance: 70f, enemyFightsPlayer: true, out Creature enemy);

        rig.Ai<RuulSnowhoofAI>().MoveInLineOfSight(enemy);

        Assert.Null(rig.Npc.Combat.Victim);
    }

    [Fact]
    public void TheAssist_NeedsTheEnemyToFightAPlayer()
    {
        using Rig rig = AssistRig(canAssist: true, enemyDistance: 70f, enemyFightsPlayer: false, out Creature enemy);
        rig.Ai<RuulSnowhoofAI>().OnQuestAccept(rig.Player, RuulSnowhoofAI.QuestFreedomToRuul);

        rig.Ai<RuulSnowhoofAI>().MoveInLineOfSight(enemy);

        Assert.Null(rig.Npc.Combat.Victim);
    }

    [Fact]
    public void TheAssist_StopsAtMaxPlayerDistance()
    {
        using Rig rig = AssistRig(canAssist: true, enemyDistance: CreatureMapSystem.PlayerAssistRange + 20f, enemyFightsPlayer: true, out Creature enemy);
        rig.Ai<RuulSnowhoofAI>().OnQuestAccept(rig.Player, RuulSnowhoofAI.QuestFreedomToRuul);

        rig.Ai<RuulSnowhoofAI>().MoveInLineOfSight(enemy);

        Assert.Null(rig.Npc.Combat.Victim);
    }

    [Fact]
    public void APassiveEscort_HasTheNearestHostileCreatureNotInCombatAttackIt()
    {
        using var rig = new Rig(RuulSnowhoofAI.Entry, hostile: true, pathLength: 36, creaturesAggroCreatures: false, playerOffset: 60f,
            extraTemplates: [Hostile(EnemyEntry)], extraSpawns: [Spawn(2, EnemyEntry, 6f, 0f)]);
        Creature enemy = Assert.Single(rig.System.Creatures, c => c.Template.Entry == EnemyEntry);
        var ai = rig.Ai<RuulSnowhoofAI>();

        // Not escorting: the enemy leaves him alone.
        rig.RunUntil(() => enemy.Combat.Victim is not null, 3_000);
        Assert.Null(enemy.Combat.Victim);

        ai.OnQuestAccept(rig.Player, RuulSnowhoofAI.QuestFreedomToRuul);
        Assert.True(rig.RunUntil(() => enemy.Combat.Victim is not null, 5_000), "the enemy went for the escort");
        Assert.Same(rig.Npc, enemy.Combat.Victim);
    }
}
