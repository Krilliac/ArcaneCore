using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Generic;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>Lane g1-scripts: DUMMY, SCRIPT_EFFECT and HEAL_MECHANICAL behaviour that vmangos implements in C++.</summary>
public sealed class G1ScriptsTests
{
    private static SpellInfo Instant(SpellInfo spell) => spell with { StartRecoveryCategory = 0, StartRecoveryTime = 0, SpellVisual = 1 };

    private static SpellTestKit Kit(params SpellInfo[] spells)
    {
        var kit = new SpellTestKit(spells);
        SpellScriptDispatcher.Install(kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        return kit;
    }

    private static SpellInfo Dummy(uint id, SpellImplicitTarget target = SpellImplicitTarget.UnitCaster) =>
        Instant(Spell(id, Effect(SpellEffectName.Dummy, 0, target)) with { RangeIndex = 4, Range = new SpellRange(0, 40) });

    private static SpellInfo Script(uint id, SpellImplicitTarget target = SpellImplicitTarget.UnitCaster) =>
        Instant(Spell(id, Effect(SpellEffectName.ScriptEffect, 0, target)) with { RangeIndex = 4, Range = new SpellRange(0, 40) });

    private static SpellInfo Marker(uint id, SpellImplicitTarget target = SpellImplicitTarget.UnitCaster) =>
        Instant(Spell(id, Effect(SpellEffectName.ApplyAura, 0, target, AuraType.Dummy)) with
        {
            Duration = new SpellDuration(30_000, 0, 30_000),
            RangeIndex = 4,
            Range = new SpellRange(0, 40),
        });

    [Fact]
    public void HealMechanical_HasAHandler_AndHealsALivingTarget()
    {
        using SpellTestKit kit = Kit(Instant(Spell(15057, Effect(SpellEffectName.HealMechanical, 700, SpellImplicitTarget.UnitFriend))));
        (Player caster, FakeSession session) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        target.MaxHealth = 2000;
        target.Health = 100;
        session.Clear();

        Assert.True(kit.System.HasEffectHandler(SpellEffectName.HealMechanical));
        kit.System.CastSpell(caster, 15057, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        Assert.Equal(800u, target.Health); // vmangos EffectHealMechanical (SpellEffects.cpp:1835): the effect value, no crit
        Assert.Single(Packets(session, WorldOpcode.SmsgSpellheallog));
    }

    [Fact]
    public void HealMechanical_OnADeadTarget_DoesNothing()
    {
        using SpellTestKit kit = Kit(Instant(Spell(15057, Effect(SpellEffectName.HealMechanical, 700, SpellImplicitTarget.UnitFriend))));
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        target.Health = 0;

        kit.System.CastSpell(caster, 15057, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        Assert.Equal(0u, target.Health);
    }

    [Fact]
    public void EveryScriptedSpellHasItsScript()
    {
        SpellScriptRegistry registry = SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly);
        Assert.IsType<WolfsheadHelmScript>(registry.Find(17770));
        Assert.IsType<WarriorsWrathScript>(registry.Find(21977));
        Assert.IsType<BendingShinboneScript>(registry.Find(8856));
        Assert.IsType<NefarianCorruptedTotemsScript>(registry.Find(23424));
        Assert.IsType<ArcaneVacuumScript>(registry.Find(21147));
        Assert.IsType<RotateTriggerScript>(registry.Find(26137));
        Assert.IsType<DreamFogScript>(registry.Find(24781));
        Assert.IsType<ElunesCandleScript>(registry.Find(26374));
        foreach (uint id in new uint[] { 26532, 26541, 26469, 26528 }) Assert.IsType<WinterVeilMinipetKitScript>(registry.Find(id));
        foreach (uint id in new uint[] { 456, 2765 }) Assert.IsType<ShowLabelSpellScript>(registry.Find(id));
        foreach (uint id in new uint[] { 2763, 6147 }) Assert.IsType<InvisSpellScript>(registry.Find(id));
        Assert.IsType<ClearAllCooldownsScript>(registry.Find(29313));
    }

    [Fact]
    public void WolfsheadHelm_CastsItsEnergyOnTheCaster()
    {
        using SpellTestKit kit = Kit(Dummy(17770), Marker(WolfsheadHelmScript.HelmEnergy));
        (Player druid, _) = kit.AddPlayer(1);

        kit.System.CastSpell(druid, 17770, SpellCastTargets.ForSelf(), triggered: true);

        Assert.True(kit.System.HasAura(druid, WolfsheadHelmScript.HelmEnergy));
    }

    [Fact]
    public void WarriorsWrath_CastsTheSpellModOnEffectZero()
    {
        using SpellTestKit kit = Kit(Dummy(21977, SpellImplicitTarget.UnitFriend), Marker(WarriorsWrathScript.WrathMod));
        (Player warrior, _) = kit.AddPlayer(1);
        (Player ally, _) = kit.AddPlayer(2, 2);

        kit.System.CastSpell(warrior, 21977, SpellCastTargets.ForUnit(ally.Guid), triggered: true);

        Assert.True(kit.System.HasAura(warrior, WarriorsWrathScript.WrathMod));
    }

    [Fact]
    public void BendingShinbone_LeavesOneOfTheTwoResults()
    {
        using SpellTestKit kit = Kit(Script(8856), Marker(BendingShinboneScript.Break), Marker(BendingShinboneScript.Hold));
        (Player player, _) = kit.AddPlayer(1);

        kit.System.CastSpell(player, 8856, SpellCastTargets.ForSelf(), triggered: true);

        Assert.True(kit.System.HasAura(player, BendingShinboneScript.Break) ^ kit.System.HasAura(player, BendingShinboneScript.Hold));
    }

    [Fact]
    public void CorruptedTotems_CastsOneOfTheFourSummons()
    {
        using SpellTestKit kit = Kit(Dummy(23424), Marker(23419), Marker(23420), Marker(23422), Marker(23423));
        (Player shaman, _) = kit.AddPlayer(1);

        kit.System.CastSpell(shaman, 23424, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(1, new uint[] { 23419, 23420, 23422, 23423 }.Count(id => kit.System.HasAura(shaman, id)));
    }

    [Fact]
    public void RotateTrigger_MakesTheTargetSpin()
    {
        using SpellTestKit kit = Kit(Script(26137, SpellImplicitTarget.UnitFriend), Marker(26009), Marker(26136));
        (Player eye, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2);

        kit.System.CastSpell(eye, 26137, SpellCastTargets.ForUnit(victim.Guid), triggered: true);

        Assert.True(kit.System.HasAura(victim, 26009) ^ kit.System.HasAura(victim, 26136));
    }

    [Fact]
    public void ArcaneVacuum_CastsSummonPlayerOnTheTarget()
    {
        using SpellTestKit kit = Kit(Dummy(21147, SpellImplicitTarget.UnitFriend), Marker(ArcaneVacuumScript.SummonPlayer, SpellImplicitTarget.UnitFriend));
        (Player azuregos, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2);

        kit.System.CastSpell(azuregos, 21147, SpellCastTargets.ForUnit(victim.Guid), triggered: true);

        Assert.True(kit.System.HasAura(victim, ArcaneVacuumScript.SummonPlayer));
    }

    [Fact]
    public void ElunesCandle_ShootsTheFireworkAtAPlainTarget()
    {
        using SpellTestKit kit = Kit(Dummy(26374, SpellImplicitTarget.UnitFriend), Marker(ElunesCandleScript.Firework, SpellImplicitTarget.UnitFriend));
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);

        kit.System.CastSpell(caster, 26374, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        Assert.True(kit.System.HasAura(target, ElunesCandleScript.Firework));
    }

    [Theory]
    [InlineData(26532u, 26533u)]
    [InlineData(26541u, 26536u)]
    [InlineData(26469u, 26045u)]
    [InlineData(26528u, 26529u)]
    public void WinterVeilKit_CastsTheRealSummonWhenNoMinipetIsOut(uint kitSpell, uint summon)
    {
        using SpellTestKit kit = Kit(Script(kitSpell), Marker(summon));
        (Player player, _) = kit.AddPlayer(1);

        kit.System.CastSpell(player, kitSpell, SpellCastTargets.ForSelf(), triggered: true);

        Assert.True(kit.System.HasAura(player, summon));
    }

    [Fact]
    public void WinterVeilKit_TableMatchesVmangos()
    {
        Assert.Equal((15698u, 26533u), WinterVeilMinipetKitScript.For(26532));
        Assert.Equal((15705u, 26536u), WinterVeilMinipetKitScript.For(26541));
        Assert.Equal((15710u, 26045u), WinterVeilMinipetKitScript.For(26469));
        Assert.Equal((15706u, 26529u), WinterVeilMinipetKitScript.For(26528));
    }

    [Fact]
    public void InvisSpells_ToggleGmInvisibility_OnlyForAGameMaster()
    {
        using SpellTestKit kit = Kit(Script(2763), Script(6147));
        (Player gm, FakeSession session) = kit.AddPlayer(1, security: AccountSecurity.GameMaster);
        (Player plain, _) = kit.AddPlayer(2, 2);

        kit.System.CastSpell(plain, 2763, SpellCastTargets.ForSelf(), triggered: true);
        Assert.False(plain.GmInvisible);

        kit.System.CastSpell(gm, 2763, SpellCastTargets.ForSelf(), triggered: true);
        Assert.True(gm.GmInvisible);
        Assert.NotEmpty(Packets(session, WorldOpcode.SmsgNotification));

        kit.System.CastSpell(gm, 6147, SpellCastTargets.ForSelf(), triggered: true);
        Assert.False(gm.GmInvisible);
    }

    [Fact]
    public void ShowLabelSpells_ToggleTheGmChatBadge()
    {
        using SpellTestKit kit = Kit(Script(456), Script(2765));
        (Player gm, _) = kit.AddPlayer(1, security: AccountSecurity.Moderator);
        (Player plain, _) = kit.AddPlayer(2, 2);

        kit.System.CastSpell(plain, 2765, SpellCastTargets.ForSelf(), triggered: true);
        Assert.False(plain.GmChat);

        kit.System.CastSpell(gm, 2765, SpellCastTargets.ForSelf(), triggered: true);
        Assert.True(gm.GmChat);
        kit.System.CastSpell(gm, 456, SpellCastTargets.ForSelf(), triggered: true);
        Assert.False(gm.GmChat);
    }

    [Fact]
    public void CooldownAll_ClearsTheCastersCooldowns()
    {
        using SpellTestKit kit = Kit(Script(29313));
        (Player gm, _) = kit.AddPlayer(1, security: AccountSecurity.GameMaster);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(gm, SpellTestKit.CooldownSpell, SpellCastTargets.ForSelf(), triggered: false));
        Assert.NotEmpty(kit.System.GetActiveCooldowns(gm));

        kit.System.CastSpell(gm, 29313, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Empty(kit.System.GetActiveCooldowns(gm));
    }

    [Fact]
    public void BeastmasterSpells_ToggleTheNotAttackableFlag_OnlyForAGameMaster()
    {
        using SpellTestKit kit = Kit(Script(20115), Script(24675));
        (Player gm, _) = kit.AddPlayer(1, security: AccountSecurity.GameMaster);
        (Player plain, _) = kit.AddPlayer(2, 2);

        kit.System.CastSpell(plain, 20115, SpellCastTargets.ForSelf(), triggered: true);
        Assert.False(plain.UnitFlags.HasFlag(UnitFlags.NotAttackable1));

        kit.System.CastSpell(gm, 20115, SpellCastTargets.ForSelf(), triggered: true);
        Assert.True(gm.UnitFlags.HasFlag(UnitFlags.NotAttackable1));
        kit.System.CastSpell(gm, 24675, SpellCastTargets.ForSelf(), triggered: true);
        Assert.False(gm.UnitFlags.HasFlag(UnitFlags.NotAttackable1));
    }

    [Fact]
    public void GmModeSpells_SwitchGameMasterMode_OnlyForAGameMaster()
    {
        using SpellTestKit kit = Kit(Script(1509), Script(18139));
        (Player gm, _) = kit.AddPlayer(1, security: AccountSecurity.GameMaster);
        (Player plain, _) = kit.AddPlayer(2, 2);

        kit.System.CastSpell(plain, 18139, SpellCastTargets.ForSelf(), triggered: true);
        Assert.False(plain.IsGameMaster);

        kit.System.CastSpell(gm, 18139, SpellCastTargets.ForSelf(), triggered: true);
        Assert.True(gm.IsGameMaster);
        kit.System.CastSpell(gm, 1509, SpellCastTargets.ForSelf(), triggered: true);
        Assert.False(gm.IsGameMaster);
    }

    [Fact]
    public void UnstablePower_DropsOneStackAndTheAuraWithTheLastOne()
    {
        SpellInfo stacking = Marker(UnstablePowerScript.UnstablePower) with { StackAmount = 3 };
        using SpellTestKit kit = Kit(Script(24660), stacking);
        (Player player, _) = kit.AddPlayer(1);
        for (int i = 0; i < 3; i++) kit.System.AddAura(player, UnstablePowerScript.UnstablePower);
        SpellAuraHolder holder = Assert.Single(kit.System.GetAuras(player), h => h.Spell.Id == UnstablePowerScript.UnstablePower);
        Assert.Equal(3, holder.StackAmount);

        kit.System.CastSpell(player, 24660, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal(2, holder.StackAmount);
        kit.System.CastSpell(player, 24660, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(player, 24660, SpellCastTargets.ForSelf(), triggered: true);

        Assert.False(kit.System.HasAura(player, UnstablePowerScript.UnstablePower));
    }

    [Fact]
    public void TowerCaptureTest_MakesFriendlyPvpPlayersCastTowerCapture()
    {
        using SpellTestKit kit = Kit(Script(30882), Marker(TowerCaptureTestScript.TowerCapture));
        (Player caster, _) = kit.AddPlayer(1);
        (Player pvp, _) = kit.AddPlayer(2, 5);
        (Player passive, _) = kit.AddPlayer(3, 6);
        (Player far, _) = kit.AddPlayer(4, 500);
        pvp.Flags |= PlayerFlags.PvpDesired;
        far.Flags |= PlayerFlags.PvpDesired;

        kit.System.CastSpell(caster, 30882, SpellCastTargets.ForSelf(), triggered: true);

        Assert.True(kit.System.HasAura(pvp, TowerCaptureTestScript.TowerCapture));
        Assert.False(kit.System.HasAura(passive, TowerCaptureTestScript.TowerCapture)); // PvP not desired on a normal realm
        Assert.False(kit.System.HasAura(far, TowerCaptureTestScript.TowerCapture));    // beyond 100 yards
    }

    [Fact]
    public void CreateHouse_HasAHandler()
    {
        using SpellTestKit kit = Kit();
        Assert.True(kit.System.HasEffectHandler(SpellEffectName.CreateHouse));
    }
}
