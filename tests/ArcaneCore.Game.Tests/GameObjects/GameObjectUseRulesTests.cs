using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectUseRig;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.GameObjects;

/// <summary>
/// Who may use an object: the immunity and mount rules of GameObject::Use (GameObject.cpp:1409-1416) and Spell::EffectOpenLock
/// (SpellEffects.cpp:2117-2118), the chest quest gate (data8) on the open-lock path as on the use path, and the looting-aura break
/// of HandleGameObjectUseOpcode (SpellHandler.cpp:241-258) before Use.
/// </summary>
public sealed class GameObjectUseRulesTests
{
    private const uint LootingBreaks = 940801;
    private const uint DamageStays = 940802;

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
        Assert.Empty(GameObjectTestKit.Packets(session, WorldOpcode.SmsgLootResponse));

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
        Assert.Empty(GameObjectTestKit.Packets(session, WorldOpcode.SmsgLootResponse));
        Assert.Null(herb.Loot);

        rig.Quests.Incomplete.Add((player.Guid, QuestId));
        Assert.Equal(GameObjectUseResult.Ok, rig.System.OpenLock(player, herb.Guid, LockType.Herbalism));
        Assert.Single(GameObjectTestKit.Packets(session, WorldOpcode.SmsgLootResponse));
    }

    /// <summary>
    /// SpellHandler.cpp:241-258 strips auras with AURA_INTERRUPT_LOOTING_CANCELS (Stealth is 0x3C07) after the object is in reach
    /// and before Use, so a use that is then refused still breaks them. Out of reach and dead never get there. An aura without the bit stays.
    /// </summary>
    [Fact]
    public void DirectUse_BreaksLootingAuras_WhenInReach_IncludingARefusedUse()
    {
        GameObjectUseRig rig = Create([GoSpawn(1, ChestEntry, 0, 0), GoSpawn(2, QuestHerbEntry, 0, 0)]);
        (Player player, _) = rig.Join(1);
        SpellSystem spells = new(new SpellStore(
            [
                SelfAura(LootingBreaks, AuraInterruptMask.Looting),
                SelfAura(DamageStays, AuraInterruptMask.Damage),
            ],
            [],
            []), () => 0u);
        rig.System.BreakLootingAuras = unit => spells.RemoveAurasWithInterruptFlags(unit, AuraInterruptMask.Looting);

        Give(spells, player, LootingBreaks);
        Give(spells, player, DamageStays);
        player.X = 30;
        Assert.Equal(GameObjectUseResult.TooFar, rig.System.Use(player, rig.Single(ChestEntry).Guid));
        Assert.True(spells.HasAura(player, LootingBreaks));
        Assert.True(spells.HasAura(player, DamageStays));

        player.X = 0;
        uint health = player.Health;
        player.Health = 0;
        Assert.Equal(GameObjectUseResult.Dead, rig.System.Use(player, rig.Single(ChestEntry).Guid));
        Assert.True(spells.HasAura(player, LootingBreaks));
        player.Health = health;

        // A herb the player has no quest for is refused, but the opcode already broke the aura (the break is before Use).
        Assert.Equal(GameObjectUseResult.NeedsQuest, rig.System.Use(player, rig.Single(QuestHerbEntry).Guid));
        Assert.False(spells.HasAura(player, LootingBreaks));
        Assert.True(spells.HasAura(player, DamageStays));

        Give(spells, player, LootingBreaks);
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, rig.Single(ChestEntry).Guid));
        Assert.False(spells.HasAura(player, LootingBreaks));
        Assert.True(spells.HasAura(player, DamageStays));

        Give(spells, player, LootingBreaks);
        player.UnitFlags |= UnitFlags.Immune;
        Assert.Equal(GameObjectUseResult.Immune, rig.System.Use(player, rig.Single(ChestEntry).Guid));
        Assert.False(spells.HasAura(player, LootingBreaks));
        Assert.True(spells.HasAura(player, DamageStays));
    }

    private static SpellInfo SelfAura(uint id, uint interrupt) => Spell(id, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
    {
        AuraInterruptFlags = (SpellAuraInterruptFlags)interrupt,
        Duration = new SpellDuration(60_000, 0, 60_000),
        SpellVisual = 1,
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static void Give(SpellSystem spells, Player player, uint spell)
        => Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(player, spell, SpellCastTargets.ForSelf(), triggered: true));
}
