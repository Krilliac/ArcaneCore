using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Casters.Bonus;
using ArcaneCore.Game.Spells.Hunter;
using ArcaneCore.Game.Spells.Procs;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

public sealed class PvpAssistAndTamingRodTests
{
    private const uint FriendlyHeal = 500;
    private const uint PoisonLow = 502;
    private const uint PoisonHigh = 503;

    [Fact]
    public void FriendlySpell_OnPvpFlaggedAllyOutOfCombat_FlagsCasterPvp()
    {
        SpellInfo heal = Spell(FriendlyHeal, Effect(SpellEffectName.Heal, 20, SpellImplicitTarget.UnitFriend)) with
        {
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
        };
        using var kit = new SpellTestKit(heal);
        (Player healer, _) = kit.AddPlayer(1, 0, 0);
        (Player ally, _) = kit.AddPlayer(2, 5, 0);
        kit.Spellbook.Teach(healer, FriendlyHeal);
        ally.Health = 50;

        Assert.Equal(UnitFlags.None, healer.UnitFlags & UnitFlags.Pvp);

        // Healing unflagged ally does not flag healer
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(healer, FriendlyHeal, SpellCastTargets.ForUnit(ally.Guid), triggered: true));
        Assert.Equal(UnitFlags.None, healer.UnitFlags & UnitFlags.Pvp);

        // Flag ally PvP out of combat; healing ally now flags healer PvP
        ally.UnitFlags |= UnitFlags.Pvp;
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(healer, FriendlyHeal, SpellCastTargets.ForUnit(ally.Guid), triggered: true));
        Assert.Equal(UnitFlags.Pvp, healer.UnitFlags & UnitFlags.Pvp);
    }

    [Fact]
    public void FriendlySpell_OnInCombatContestedPvpAlly_PropagatesCombatPvpAndContestedFlag()
    {
        SpellInfo heal = Spell(FriendlyHeal, Effect(SpellEffectName.Heal, 20, SpellImplicitTarget.UnitFriend)) with
        {
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
        };
        using var kit = new SpellTestKit(heal);
        (Player healer, _) = kit.AddPlayer(1, 0, 0);
        (Player ally, _) = kit.AddPlayer(2, 5, 0);
        ally.Health = 50;

        ally.UnitFlags |= UnitFlags.Pvp;
        ally.Flags |= PlayerFlags.ContestedPvp;
        ally.Map!.Combat.SetInCombatState(ally, CombatConstants.PvpCombatTimerMs);

        Assert.False(healer.Combat.IsInCombat);
        Assert.Equal(UnitFlags.None, healer.UnitFlags & UnitFlags.Pvp);
        Assert.Equal(0u, (uint)(healer.Flags & PlayerFlags.ContestedPvp));

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(healer, FriendlyHeal, SpellCastTargets.ForUnit(ally.Guid), triggered: false));
        kit.Advance(kit.System.SpellBatchWindowMs); // with spell batching on, the held go resolves at the next boundary (0: no-op)

        Assert.True(healer.Combat.IsInCombat);
        Assert.Equal(UnitFlags.Pvp, healer.UnitFlags & UnitFlags.Pvp);
        Assert.NotEqual(0u, (uint)(healer.Flags & PlayerFlags.ContestedPvp));
    }

    [Fact]
    public void TamingRodChannel_RejectsSecondTamer_CompletesOnFullChannelEvenWhenExpendableItemDestroyed_AndSkipsOnInterrupt()
    {
        SpellInfo tamingChannel = Spell(19548, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.Dummy)) with
        {
            AttributesEx = SpellAttributesEx.IsChanneled,
            AttributesEx2 = SpellAttributesEx2.SpecialTamingFlag,
            Duration = new SpellDuration(20_000, 0, 20_000),
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };
        SpellInfo tameTriggered = Spell(19597,
            Effect(SpellEffectName.TriggerSpell, 0, SpellImplicitTarget.UnitCaster, trigger: 19598)) with
        {
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };
        SpellInfo questCompleteMarker = Spell(19598, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Dummy)) with
        {
            Duration = new SpellDuration(60_000, 0, 60_000),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };

        using var kit = new SpellTestKit(tamingChannel, tameTriggered, questCompleteMarker);

        (Player hunter1, _) = kit.AddPlayer(1, 0, 0);
        (Player hunter2, _) = kit.AddPlayer(2, 2, 0);
        (Player beastTarget, _) = kit.AddPlayer(3, 5, 0, race: Race.Orc);
        kit.Spellbook.Teach(hunter2, 19548);

        // Give hunter1 an expendable 1-charge Taming Rod item so ConsumeItemUse destroys it on cast start
        hunter1.Inventory.Templates = new ItemTemplateStore([
            new ItemTemplate
            {
                Entry = 15911,
                Class = 0,
                Stackable = 1,
                Spells = [new ItemSpell(19548, 0, -1, 0, 0, 0, 0)],
            }]);
        hunter1.Inventory.GuidAllocator = new ItemGuidAllocator();
        hunter1.Inventory.Load([]);
        Assert.Equal(InventoryResult.Ok, hunter1.Inventory.AddItem(15911, 1, out Item? rod));

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleItemUse(hunter1, rod!.BagSlot, rod.Slot, 0, SpellCastTargets.ForUnit(beastTarget.Guid)));
        // Expendable 1-charge item was destroyed on cast start
        Assert.Equal(0u, hunter1.Inventory.GetItemCount(15911));

        // Second hunter attempting to tame the same target while channel is active gets AlreadyBeingTamed
        Assert.Equal(SpellCastResult.AlreadyBeingTamed, kit.System.HandleCastRequest(hunter2, 19548, SpellCastTargets.ForUnit(beastTarget.Guid)));

        // Advance full 20s channel -> AuraRemoveMode.Channel triggers 19597 -> triggers 19598 on hunter1
        kit.Advance(20_000, step: 1000);
        Assert.True(kit.System.HasAura(hunter1, 19598));

        // Now test interrupted channel does NOT trigger 19597/19598 on hunter2
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(hunter2, 19548, SpellCastTargets.ForUnit(beastTarget.Guid)));
        kit.Advance(5_000, step: 1000);
        kit.System.CancelChannel(hunter2);
        kit.Advance(20_000, step: 1000);
        Assert.False(kit.System.HasAura(hunter2, 19598));
    }

    [Fact]
    public void AntiVenom_RejectsHighLevelPoisonWithHighlevel_AndDispelsEligiblePoison()
    {
        SpellInfo antiVenom = Spell(7932, Effect(SpellEffectName.Dispel, 1, SpellImplicitTarget.UnitFriend, misc: 4)) with
        {
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };
        SpellInfo lowPoison = Spell(PoisonLow, Effect(SpellEffectName.ApplyAura, 5, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicDamage, amplitude: 3000)) with
        {
            Dispel = 4,
            SpellLevel = 25,
            Duration = new SpellDuration(15_000, 0, 15_000),
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
        };
        SpellInfo highPoison = Spell(PoisonHigh, Effect(SpellEffectName.ApplyAura, 5, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicDamage, amplitude: 3000)) with
        {
            Dispel = 4,
            SpellLevel = 30,
            Duration = new SpellDuration(15_000, 0, 15_000),
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
        };

        using var kit = new SpellTestKit(antiVenom, lowPoison, highPoison);
        (Player player, _) = kit.AddPlayer(1, 0, 0);
        (Player spider, _) = kit.AddPlayer(2, 5, 0, race: Race.Orc);
        kit.Spellbook.Teach(player, 7932);

        // Apply level 30 poison -> Anti-Venom (max level 25) fails with Highlevel
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(spider, PoisonHigh, SpellCastTargets.ForUnit(player.Guid), triggered: true));
        Assert.True(kit.System.HasAura(player, PoisonHigh));
        Assert.Equal(SpellCastResult.Highlevel, kit.System.HandleCastRequest(player, 7932, SpellCastTargets.ForUnit(player.Guid)));
        Assert.True(kit.System.HasAura(player, PoisonHigh));

        // Add level 25 poison -> Anti-Venom succeeds and dispels the level 25 poison while leaving level 30 poison
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(spider, PoisonLow, SpellCastTargets.ForUnit(player.Guid), triggered: true));
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(player, 7932, SpellCastTargets.ForUnit(player.Guid)));
        kit.Advance(kit.System.SpellBatchWindowMs); // with spell batching on, the held go resolves at the next boundary (0: no-op)
        Assert.False(kit.System.HasAura(player, PoisonLow));
        Assert.True(kit.System.HasAura(player, PoisonHigh));
    }

    [Fact]
    public void OverrideClassScripts_AndDeadlySwiftness_ApplyRelicBonusAndLowHealthProc()
    {
        SpellInfo earthShock = Spell(8042, Effect(SpellEffectName.SchoolDamage, 50, SpellImplicitTarget.UnitEnemy)) with
        {
            School = SpellSchool.Nature,
            SpellFamilyName = 11, // SPELLFAMILY_SHAMAN
            SpellFamilyFlags = 0x0000000090100000UL,
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
        };
        SpellInfo totemOfRage = Spell(23199, new SpellEffectInfo
        {
            Effect = SpellEffectName.ApplyAura,
            BasePoints = 29,
            BaseDice = 1,
            DieSides = 1,
            TargetA = SpellImplicitTarget.UnitCaster,
            AuraType = AuraType.OverrideClassScripts,
            MiscValue = 4418,
            ItemType = 0x90100000u,
        }) with
        {
            SpellFamilyName = 11,
            Attributes = SpellAttributes.Passive,
            Duration = new SpellDuration(-1, 0, -1),
        };
        SpellInfo deadlySwiftnessAura = Spell(31255, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.ProcTriggerSpell)) with
        {
            Attributes = SpellAttributes.Passive,
            Duration = new SpellDuration(-1, 0, -1),
            ProcFlags = ProcFlags.DealMeleeSwing,
            ProcChance = 100,
        };
        SpellInfo deadlySwiftnessBuff = Spell(22588, Effect(SpellEffectName.ApplyAura, 20, SpellImplicitTarget.UnitCaster, AuraType.ModIncreaseSpeed)) with
        {
            Duration = new SpellDuration(6000, 0, 6000),
        };

        using var kit = new SpellTestKit(earthShock, totemOfRage, deadlySwiftnessAura, deadlySwiftnessBuff);
        var bonusModule = new SpellBonusModule(kit.System);
        (Player shaman, _) = kit.AddPlayer(1, 0, 0);
        (Player target, _) = kit.AddPlayer(2, 5, 0, race: Race.Orc);
        shaman.Level = 60;
        target.Level = 60;

        float baseDone = bonusModule.Modify(SpellAmountStage.DirectDamage, shaman, target, earthShock, 0, 50f, 1);
        kit.System.CastSpell(shaman, 23199, SpellCastTargets.ForSelf(), triggered: true);
        float boostedDone = bonusModule.Modify(SpellAmountStage.DirectDamage, shaman, target, earthShock, 0, 50f, 1);
        Assert.Equal(baseDone + 30f, boostedDone, 3);

        (Player rogue, _) = kit.AddPlayer(3, 0, 0);
        kit.System.CastSpell(rogue, 31255, SpellCastTargets.ForSelf(), triggered: true);

        // Victim above 20% health -> does not proc 22588
        target.MaxHealth = 100;
        target.Health = 50;
        kit.System.ProcDamageAndSpell(rogue, new ProcEvent
        {
            Victim = target,
            AttackerFlags = ProcFlags.DealMeleeSwing,
            Extra = ProcFlagsEx.NormalHit,
            Amount = 10,
        });
        Assert.False(kit.System.HasAura(rogue, 22588));

        // Victim at <= 20% health -> procs 22588 on the rogue
        target.Health = 20;
        kit.System.ProcDamageAndSpell(rogue, new ProcEvent
        {
            Victim = target,
            AttackerFlags = ProcFlags.DealMeleeSwing,
            Extra = ProcFlagsEx.NormalHit,
            Amount = 10,
        });
        Assert.True(kit.System.HasAura(rogue, 22588));
    }
}
