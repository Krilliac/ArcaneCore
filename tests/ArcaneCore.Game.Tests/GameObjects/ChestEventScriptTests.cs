using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTypeRig;

namespace ArcaneCore.Game.Tests.GameObjects;

/// <summary>
/// chest.eventId (data6) starts dbscripts_on_event with the opener as the source and the chest as the target (mangos-classic GameObject::Use,
/// GAMEOBJECT_TYPE_CHEST, GameObject.cpp:1548-1560). Every ClassicDB z2815 chest whose event has script rows is locked (Trelane's chests,
/// Benedict's Chest, the Stratholme Blacksmithing Plans, the Ogre Tannin Basket), so it is opened through Spell::EffectOpenLock, whose
/// Spell::SendLoot hands the chest to that Use (SpellEffects.cpp:2142-2145). The event script here is one kill credit, so a start is visible.
/// </summary>
public sealed class ChestEventScriptTests
{
    private const uint CreditEntry = 2044;

    private sealed class Credits : IScriptQuestEvents
    {
        public List<uint> Entries { get; } = [];
        public void AreaExploredOrEventHappens(Player player, uint questId) { }
        public void FailQuest(Player player, uint questId) { }
        public void KilledMonsterCredit(Player player, uint creatureEntry, ObjectGuid source) => Entries.Add(creatureEntry);
        public void GroupEventFailHappens(Player player, uint questId) { }
        public IReadOnlyList<Player> GroupMembersOf(Player player) => [];
    }

    private static (GameObjectTypeRig Rig, Credits Credits) Create(uint chestEntry)
    {
        GameObjectTypeRig rig = GameObjectTypeRig.Create([GoSpawn(1, chestEntry, 3, 0)]);
        RelayScriptStep credit = new(ChestEvent, 0, 0, 8, CreditEntry, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var ai = new CreatureAiContent([], []) { DbScripts = new DbScriptCatalog([(DbScriptKind.Event, credit)]) };
        var credits = new Credits();
        rig.Map.AddUpdater(new CreatureMapSystem(rig.Map, new CreatureContent([], [], [], [], [], ai),
            aiServices: new CreatureAiServices { ScriptQuests = credits }));
        return (rig, credits);
    }

    [Fact]
    public void KeyOpening_ThroughTheSpellPath_StartsTheChestEvent()
    {
        (GameObjectTypeRig rig, Credits credits) = Create(EventChest);
        (Player player, _) = rig.Join(1);
        GameObject chest = rig.Single(EventChest);

        Assert.Equal(GameObjectUseResult.Ok, rig.System.OpenLock(player, chest.Guid, LockType.Open, keyItemId: PlainKey));

        Assert.Equal([CreditEntry], credits.Entries);
    }

    [Fact]
    public void ARefusedSpellOpening_StartsNoEvent()
    {
        (GameObjectTypeRig rig, Credits credits) = Create(EventChest);
        (Player player, _) = rig.Join(1);
        GameObject chest = rig.Single(EventChest);

        Assert.NotEqual(GameObjectUseResult.Ok, rig.System.OpenLock(player, chest.Guid, LockType.Open, keyItemId: ExpendableKey));

        Assert.Empty(credits.Entries);
    }

    [Fact]
    public void DirectUse_StartsTheEventOnlyOnceTheLockLetItOpen()
    {
        (GameObjectTypeRig rig, Credits credits) = Create(EventChest);
        (Player player, _) = rig.Join(1);
        GameObject chest = rig.Single(EventChest);

        Assert.Equal(GameObjectUseResult.MissingKey, rig.System.Use(player, chest.Guid));
        Assert.Empty(credits.Entries);

        ItemTestData.Give(player.Inventory, PlainKey);
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, chest.Guid));
        Assert.Equal([CreditEntry], credits.Entries);
    }

    [Fact]
    public void DirectUse_RefusedByTheChestQuestGate_StartsNoEvent()
    {
        (GameObjectTypeRig rig, Credits credits) = Create(QuestEventChest);
        (Player player, _) = rig.Join(1);
        GameObject chest = rig.Single(QuestEventChest);

        Assert.Equal(GameObjectUseResult.NeedsQuest, rig.System.Use(player, chest.Guid));
        Assert.Empty(credits.Entries);

        rig.Quests.Incomplete.Add((player.Guid, ChestQuest));
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, chest.Guid));
        Assert.Equal([CreditEntry], credits.Entries);
    }
}
