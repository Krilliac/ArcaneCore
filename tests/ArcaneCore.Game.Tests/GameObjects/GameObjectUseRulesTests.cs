using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectUseRig;

namespace ArcaneCore.Game.Tests.GameObjects;

/// <summary>
/// Who may use an object: the immunity and mount rules of GameObject::Use (GameObject.cpp:1409-1416) and Spell::EffectOpenLock
/// (SpellEffects.cpp:2117-2118), and the chest quest gate (data8) on the open-lock path as on the use path.
/// </summary>
public sealed class GameObjectUseRulesTests
{
    [Fact]
    public void ImmunePlayer_CannotOpenAChest_ByUseOrByOpenLock()
    {
        GameObjectUseRig rig = Create([GoSpawn(1, ChestEntry, 3, 0), GoSpawn(2, QuestHerbEntry, 2, 0)]);
        (Player player, FakeSession session) = rig.Join(1);
        GameObject chest = rig.Single(ChestEntry);
        GameObject herb = rig.Single(QuestHerbEntry);
        rig.Quests.Incomplete.Add((player.Guid, QuestId));
        rig.System.SkillValue = (_, _) => 300;
        player.UnitFlags |= UnitFlags.Immune;
        session.Clear();

        Assert.Equal(GameObjectUseResult.Immune, rig.System.Use(player, chest.Guid));
        Assert.Equal(GameObjectUseResult.Immune, rig.System.OpenLock(player, herb.Guid, LockType.Herbalism));
        Assert.Empty(Packets(session, WorldOpcode.SmsgLootResponse));

        player.UnitFlags &= ~UnitFlags.Immune;
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, chest.Guid));
    }

    [Fact]
    public void MountedPlayer_IsDismounted_UnlessTheObjectAllowsMountedUse()
    {
        GameObjectUseRig rig = Create([GoSpawn(1, ChestEntry, 3, 0), GoSpawn(2, MailboxEntry, 2, 0)]);
        (Player player, _) = rig.Join(1);
        var dismounted = new List<Player>();
        rig.System.Dismount = dismounted.Add;
        player.SetUInt32(UpdateFields.UnitFieldMountdisplayid, 2410);

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, rig.Single(MailboxEntry).Guid));
        Assert.Empty(dismounted);

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, rig.Single(ChestEntry).Guid));
        Assert.Equal([player], dismounted);
    }

    [Fact]
    public void QuestHerb_OpenedBySpell_NeedsTheQuest_LikeAUse()
    {
        GameObjectUseRig rig = Create([GoSpawn(1, QuestHerbEntry, 3, 0)]);
        (Player player, FakeSession session) = rig.Join(1);
        GameObject herb = rig.Single(QuestHerbEntry);
        rig.System.SkillValue = (_, _) => 300;
        session.Clear();

        Assert.Equal(GameObjectUseResult.NeedsQuest, rig.System.OpenLock(player, herb.Guid, LockType.Herbalism));
        Assert.Empty(Packets(session, WorldOpcode.SmsgLootResponse));
        Assert.Null(herb.Loot);

        rig.Quests.Incomplete.Add((player.Guid, QuestId));
        Assert.Equal(GameObjectUseResult.Ok, rig.System.OpenLock(player, herb.Guid, LockType.Herbalism));
        Assert.Single(Packets(session, WorldOpcode.SmsgLootResponse));
    }
}
