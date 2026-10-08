using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTypeRig;

namespace ArcaneCore.Game.Tests.GameObjects;

/// <summary>
/// The type behaviours that cast spells or act on players nearby (vmangos GameObject::Use and GameObject::Update): goober gossip and spell,
/// questId -1, spell casters, linked traps, environmental traps, area damage and flag stands.
/// </summary>
public sealed class GameObjectSpellTypeTests
{
    [Fact]
    public void GooberWithoutPage_OpensItsGossipMenu()
    {
        GameObjectTypeRig rig = Create([GoSpawn(1, GossipGoober, 3, 0)]);
        (Player player, _) = rig.Join(1);
        GameObject goober = rig.Single(GossipGoober);

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, goober.Guid));
        Assert.Equal([(player, goober, GossipMenu)], rig.Gossip.Opened);
    }

    [Fact]
    public void GooberSpell_IsCastByTheObjectAtItsUser_AndItsPageComesFirst()
    {
        GameObjectTypeRig rig = Create([GoSpawn(1, SpellGoober, 3, 0)]);
        (Player player, FakeSession session) = rig.Join(1);
        GameObject goober = rig.Single(SpellGoober);
        session.Clear();

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, goober.Guid));
        Assert.Equal([(goober, GooberSpell, (Unit)player, (Unit?)null)], rig.Spells.Casts);
        Assert.Single(Packets(session, WorldOpcode.SmsgGameobjectPagetext));
        Assert.Empty(rig.Gossip.Opened); // a page wins over the gossip
    }

    [Fact]
    public void GooberQuestIdMinusOne_IsUsableAndSparklesForEveryone()
    {
        GameObjectTypeRig rig = Create([GoSpawn(1, EveryoneGoober, 3, 0)]);
        (Player player, _) = rig.Join(1);
        GameObject goober = rig.Single(EveryoneGoober);

        Assert.Equal(GameObjectDynFlags.Activate | GameObjectDynFlags.Sparkle, rig.System.QuestFlagsFor(goober, player));
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, goober.Guid));
    }

    [Fact]
    public void SpellCaster_CastsItsSpellAtTheUser_AndLocksItsFlags()
    {
        GameObjectTypeRig rig = Create([GoSpawn(1, Portal, 3, 0)]);
        (Player player, _) = rig.Join(1);
        GameObject portal = rig.Single(Portal);

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, portal.Guid));
        Assert.Equal([(portal, PortalSpell, (Unit)player, (Unit?)null)], rig.Spells.Casts);
        Assert.Equal(GameObjectFlags.Locked, portal.Flags);
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, portal.Guid)); // no charges: usable again
        Assert.Equal(2, rig.Spells.Casts.Count);
    }

    [Fact]
    public void PartyOnlySpellCaster_WithoutOwner_ServesOnlyTheCreatorsGroup_AndIsUsedUpByItsCharges()
    {
        GameObjectTypeRig rig = Create([]);
        (Player member, _) = rig.Join(1);
        (Player stranger, _) = rig.Join(2, 1, 0);
        GameObject lightwell = rig.System.Summon(PartyLightwell, 3, 0, 83.5f, 0)!;
        lightwell.OwnerGroupId = 7;
        rig.GroupIds[member.Guid] = 7;

        Assert.Equal(GameObjectUseResult.NotUsable, rig.System.Use(stranger, lightwell.Guid));
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(member, lightwell.Guid));
        rig.World.RunTick(50);
        Assert.True(lightwell.IsSpawned);
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(member, lightwell.Guid));
        rig.World.RunTick(50);
        rig.World.RunTick(50);

        // data1 = 2 charges: the second use spends the last one, the object is used up.
        Assert.Equal(2, rig.Spells.Casts.Count(c => c.Spell == LightwellSpell));
        Assert.Null(rig.System.Find(lightwell.Guid));
    }

    [Fact]
    public void PartyOnlySpellCaster_WithAnOwner_ServesTheOwnersRaid()
    {
        GameObjectTypeRig rig = Create([]);
        (Player priest, _) = rig.Join(1);
        (Player friend, _) = rig.Join(2, 1, 0);
        (Player stranger, _) = rig.Join(3, 0, 1);
        GameObject lightwell = rig.System.Summon(PartyLightwell, 3, 0, 83.5f, 0)!;
        lightwell.SetOwner(priest.Guid);
        rig.Raids.Add((priest.Guid, friend.Guid));

        Assert.Equal(GameObjectUseResult.NotUsable, rig.System.Use(stranger, lightwell.Guid));
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(friend, lightwell.Guid));
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(priest, lightwell.Guid));
    }

    [Fact]
    public void ClickingATrappedChest_SpringsItsLinkedTrap_WhichIsUsedUpAndRespawnsWithTheChest()
    {
        GameObjectTypeRig rig = Create([GoSpawn(1, TrappedChest, 3, 0, spawnTimeSeconds: 10), GoSpawn(2, ChestTrap, 3, 0, spawnTimeSeconds: 600)]);
        (Player player, _) = rig.Join(1);
        GameObject chest = rig.Single(TrappedChest);
        GameObject trap = rig.Single(ChestTrap);

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, chest.Guid));
        Assert.Equal([(trap, TrapSpell, (Unit)player, (Unit?)null)], rig.Spells.Casts);
        rig.World.RunTick(50);
        Assert.False(trap.IsSpawned); // one charge

        rig.LootOut(player, chest);
        rig.World.RunTick(50);
        Assert.False(chest.IsSpawned);
        rig.Seconds(12);
        Assert.True(chest.IsSpawned);
        Assert.True(trap.IsSpawned); // GameObject::RespawnLinkedGameObject, long before its own 600 s
    }

    [Theory]
    [InlineData(null, false)]   // no trap spell range known: the 0.5 yard default search misses a trap 6 yards away
    [InlineData(10f, true)]     // the trap spell's maximum range is the search radius
    public void ButtonsSpringTheirLinkedTrap_WithinTheTrapSpellRange(float? spellRange, bool sprung)
    {
        GameObjectTypeRig rig = Create([GoSpawn(1, TrappedButton, 3, 0), GoSpawn(2, ChestTrap, 3, 6)]);
        if (spellRange is { } range)
        {
            rig.Spells.Ranges[TrapSpell] = range;
        }

        (Player player, _) = rig.Join(1);
        GameObject button = rig.Single(TrappedButton);

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, button.Guid));
        Assert.Equal(sprung, rig.Spells.Casts.Any(c => c.Spell == TrapSpell && ReferenceEquals(c.Target, player)));
    }

    [Fact]
    public void OpeningATrappedChestWithTheOpenLockSpell_SpringsItsLinkedTrap()
    {
        // The usual client path to a locked or "Opening" chest is the open-lock spell: Spell::SendLoot (SpellEffects.cpp:2048-2068) runs
        // GameObject::Use on the chest, whose chest branch springs the linked trap (GameObject.cpp:1472-1479), before the loot is sent.
        GameObjectTypeRig rig = Create([GoSpawn(1, TrappedChest, 3, 0), GoSpawn(2, ChestTrap, 3, 0)]);
        (Player player, _) = rig.Join(1);
        GameObject chest = rig.Single(TrappedChest);
        GameObject trap = rig.Single(ChestTrap);

        Assert.Equal(GameObjectUseResult.Ok, rig.System.OpenLock(player, chest.Guid, LockType.Open));
        Assert.Equal([(trap, TrapSpell, (Unit)player, (Unit?)null)], rig.Spells.Casts);
        Assert.NotNull(chest.Loot);
    }

    [Fact]
    public void OpeningATrappedButtonWithTheOpenLockSpell_SpringsItsLinkedTrap()
    {
        // Spell::SendLoot runs GameObject::Use for a button too: it activates and springs its trap (GameObject.cpp:1441-1455).
        GameObjectTypeRig rig = Create([GoSpawn(1, TrappedButton, 3, 0), GoSpawn(2, ChestTrap, 3, 0)]);
        (Player player, _) = rig.Join(1);
        GameObject button = rig.Single(TrappedButton);
        GameObject trap = rig.Single(ChestTrap);

        Assert.Equal(GameObjectUseResult.Ok, rig.System.OpenLock(player, button.Guid, LockType.Open));
        Assert.Equal(GameObjectState.Active, button.State);
        Assert.Equal([(trap, TrapSpell, (Unit)player, (Unit?)null)], rig.Spells.Casts);
    }

    [Fact]
    public void TheNearestTrapOfTheLinkedEntryDecides_EvenWhileItIsDespawned()
    {
        // TriggerLinkedGameObject (GameObject.cpp:1304-1318) takes the nearest trap of the entry within range and only then asks whether it is
        // spawned: a despawned trap nearby is not replaced by a spawned one further away.
        GameObjectTypeRig rig = Create([GoSpawn(1, TrappedButton, 3, 0), GoSpawn(2, ChestTrap, 3, 1, spawnTimeSeconds: 600), GoSpawn(3, ChestTrap, 3, 6)]);
        rig.Spells.Ranges[TrapSpell] = 10;
        (Player player, _) = rig.Join(1);
        GameObject button = rig.Single(TrappedButton);
        GameObject near = rig.System.GameObjects.Single(g => g.Entry == ChestTrap && g.Y == 1);

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, button.Guid));
        Assert.Equal([(near, TrapSpell, (Unit)player, (Unit?)null)], rig.Spells.Casts);
        rig.World.RunTick(50);
        Assert.False(near.IsSpawned); // its one charge

        // The button is still active, but its use still asks for the linked trap (GameObject.cpp:1441-1455): the nearest one is despawned.
        rig.System.Use(player, button.Guid);
        Assert.Single(rig.Spells.Casts);
    }

    [Fact]
    public void ALinkedTrapKeepsItsOwnCooldown_LikeAnyUseOfTheTrap()
    {
        // GameObject::Use (GameObject.cpp:1421-1428) gates every use of an object that has a cooldown (GetCooldown: trap.cooldown, data5,
        // GameObjectDefines.h:632-640) and starts it; TriggerLinkedGameObject reaches the trap through Use, so a second click within the trap's
        // cooldown casts nothing.
        GameObjectTypeRig rig = Create([GoSpawn(1, CooldownTrapButton, 3, 0), GoSpawn(2, CooldownTrap, 3, 0)]);
        (Player player, _) = rig.Join(1);
        GameObject button = rig.Single(CooldownTrapButton);

        rig.System.Use(player, button.Guid);
        rig.System.Use(player, button.Guid);
        Assert.Single(rig.Spells.Casts);

        rig.Seconds(6);
        rig.System.Use(player, button.Guid);
        Assert.Equal(2, rig.Spells.Casts.Count);
    }

    [Fact]
    public void EnvironmentalTrap_ArmsThenFiresAtTheNearestPlayer_WithItsCooldown()
    {
        GameObjectTypeRig rig = Create([GoSpawn(1, FireTrap, 3, 0)]);
        (Player near, _) = rig.Join(1, 1, 0);
        (Player far, _) = rig.Join(2, 0, 0);
        Assert.NotNull(rig.Single(FireTrap));

        // The first updates armed it: the 1 s start delay must pass first.
        rig.Spells.Casts.Clear();
        rig.Seconds(2);
        Assert.Equal((Unit)near, Assert.Single(rig.Spells.Casts).Target);
        Assert.Null(rig.Spells.Casts[0].Caster); // no owner: the trap itself casts

        // Cooldown 2 s (data5): nothing for the next second, then it fires again.
        rig.World.RunTick(1000);
        Assert.Single(rig.Spells.Casts);
        rig.Seconds(2);
        Assert.Equal(2, rig.Spells.Casts.Count);
        Assert.NotEqual(far, rig.Spells.Casts[1].Target);
    }

    [Fact]
    public void EnvironmentalTrap_IgnoresDeadPlayersAndPlayersOutsideItsRadius()
    {
        GameObjectTypeRig rig = Create([GoSpawn(1, FireTrap, 3, 0)]);
        (Player outside, _) = rig.Join(1, 20, 0);
        (Player dead, _) = rig.Join(2, 3, 1);
        dead.Health = 0;
        rig.Seconds(4);
        Assert.Empty(rig.Spells.Casts);
    }

    [Fact]
    public void AreaDamage_IsOutOfReach_AndEvenStandingOnIt_IsAnUnhandledUse_ThatHurtsNobody()
    {
        GameObjectTypeRig rig = Create([GoSpawn(1, FirePit, 3, 0)]);
        (Player near, FakeSession session) = rig.Join(1);
        (Player onIt, _) = rig.Join(2, 3, 0);
        GameObject pit = rig.Single(FirePit);
        session.Clear();

        // GameObjectInfo::GetInteractionDistance is 0 for this type (GameObjectDefines.h:780): only a user standing on it reaches it.
        Assert.Equal(GameObjectUseResult.TooFar, rig.System.Use(near, pit.Guid));

        // vmangos GameObject::Use has no case for GAMEOBJECT_TYPE_AREADAMAGE: "unhandled GameObject type" (GameObject.cpp:1982-1984).
        Assert.Equal(GameObjectUseResult.Unsupported, rig.System.Use(onIt, pit.Guid));
        Assert.Equal(onIt.MaxHealth, onIt.Health);
        Assert.Equal(near.MaxHealth, near.Health);
        Assert.Equal(GameObjectState.Ready, pit.State);
        Assert.Empty(Packets(session, WorldOpcode.SmsgEnvironmentaldamagelog));
    }

    [Fact]
    public void FlagStand_HandsTheClickToTheBattleground_AndBreaksStealth_OnlyInsideOne()
    {
        GameObjectTypeRig rig = Create([GoSpawn(1, FlagStand, 3, 0)]);
        (Player player, _) = rig.Join(1);
        GameObject flag = rig.Single(FlagStand);

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, flag.Guid));
        Assert.Equal([(player, flag)], rig.FlagStands.Clicks);
        Assert.Equal([player], rig.FlagStands.StealthBroken);

        rig.FlagStands.InBattleground = false;
        Assert.Equal(GameObjectUseResult.NotUsable, rig.System.Use(player, flag.Guid));
        Assert.Single(rig.FlagStands.Clicks);
    }
}
