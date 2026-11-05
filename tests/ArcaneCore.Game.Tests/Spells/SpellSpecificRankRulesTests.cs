using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Diagnostics;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// The strict rank test and the aura rule order of <see cref="SpellSpecificAuraRules"/> (vmangos SpellMgr::IsRankSpellDueToSpell,
/// SpellMgr.cpp:877-905, and Unit::RemoveNoStackAurasDueToAuraHolder, Unit.cpp:3355-3573): every comparison of
/// <see cref="SpellSpecifics.IsRankOf"/> on its own, the party area aura exception, a passive spell without an aura effect, an
/// OBS_MOD_HEALTH heal over time, the trigger skip, the CHANNEL_DEATH_ITEM skip, a holder whose handler is running, and a holder that a
/// displacement removed.
/// </summary>
public sealed class SpellSpecificRankRulesTests
{
    private const uint FamilyPriest = 6;
    private const uint FamilyWarrior = 4;
    private const uint FamilyHunter = 9;
    private const uint DispelPoison = 4;

    private const uint S1 = 951_001;
    private const uint S2 = 951_002;
    private const uint S3 = 951_003;
    private const uint SerpentSting = 951_101;
    private const uint ViperSting = 951_102;

    private static SpellInfo Ranked(uint id, SpellEffectInfo first, ulong flags = 0x20, uint icon = 5, uint visual = 7, uint family = FamilyPriest) =>
        Spell(id, first) with { Name = "Rank", SpellFamilyName = family, SpellFamilyFlags = flags, SpellIconId = icon, SpellVisual = visual };

    private static SpellEffectInfo Aura(AuraType type, int amount = 10, int misc = 0) => Effect(SpellEffectName.ApplyAura, amount, aura: type, misc: misc);

    [Fact]
    public void TwoSpellsOfOneNameAndFields_AreRanks()
    {
        Assert.True(SpellSpecifics.IsRankOf(Ranked(1, Aura(AuraType.Dummy)), Ranked(2, Aura(AuraType.Dummy, 20))));
    }

    [Theory]
    [InlineData("flags")]
    [InlineData("icon")]
    [InlineData("visual")]
    [InlineData("effect")]
    [InlineData("aura")]
    [InlineData("flat-misc")]
    [InlineData("name")]
    [InlineData("generic-family")]
    [InlineData("other-family")]
    public void ASingleDifferingComparison_MakesNoRank(string differs)
    {
        SpellInfo first = Ranked(1, Aura(AuraType.Dummy));
        SpellInfo second = differs switch
        {
            "flags" => Ranked(2, Aura(AuraType.Dummy), flags: 0x40),
            "icon" => Ranked(2, Aura(AuraType.Dummy), icon: 6),
            "visual" => Ranked(2, Aura(AuraType.Dummy), visual: 8),
            "effect" => Ranked(2, Effect(SpellEffectName.Heal, 10, aura: AuraType.Dummy)),
            "aura" => Ranked(2, Aura(AuraType.ModHealingPct)),
            "flat-misc" => Ranked(2, Aura(AuraType.AddFlatModifier, misc: 2)),
            "name" => Ranked(2, Aura(AuraType.Dummy)) with { Name = "Other" },
            "generic-family" => Ranked(2, Aura(AuraType.Dummy), family: 0),
            "other-family" => Ranked(2, Aura(AuraType.Dummy), family: FamilyWarrior),
            _ => throw new ArgumentOutOfRangeException(nameof(differs)),
        };
        if (differs == "flat-misc")
        {
            first = Ranked(1, Aura(AuraType.AddFlatModifier, misc: 1));
        }

        if (differs == "generic-family")
        {
            first = Ranked(1, Aura(AuraType.Dummy), family: 0);
        }

        Assert.False(SpellSpecifics.IsRankOf(first, second));
        Assert.False(SpellSpecifics.IsRankOf(second, first));
    }

    [Fact]
    public void AFlatModifierWithTheSameMisc_IsARank_AndOtherAurasIgnoreTheMisc()
    {
        Assert.True(SpellSpecifics.IsRankOf(Ranked(1, Aura(AuraType.AddFlatModifier, misc: 1)), Ranked(2, Aura(AuraType.AddFlatModifier, 20, misc: 1))));
        Assert.True(SpellSpecifics.IsRankOf(Ranked(1, Aura(AuraType.Dummy, misc: 1)), Ranked(2, Aura(AuraType.Dummy, 20, misc: 2))));
    }

    private static SpellInfo Hot(uint id, int amount, ulong flags = 0x20, uint icon = 5, uint visual = 7, AuraType first = AuraType.PeriodicHeal) =>
        Spell(id, Effect(SpellEffectName.ApplyAura, amount, aura: first, amplitude: 3000),
            Effect(SpellEffectName.ApplyAura, amount, aura: AuraType.PeriodicHeal, amplitude: 3000)) with
        {
            Name = "Renew",
            SpellFamilyName = FamilyPriest,
            SpellFamilyFlags = flags,
            SpellIconId = icon,
            SpellVisual = visual,
            Duration = new SpellDuration(15_000, 0, 15_000),
        };

    [Theory]
    [InlineData("flags")]
    [InlineData("icon")]
    [InlineData("visual")]
    [InlineData("aura")]
    public void AHealOverTimeThatDiffersInOneRankField_IsNotRefusedByTheStrongerOne(string differs)
    {
        SpellInfo weak = differs switch
        {
            "flags" => Hot(S2, 10, flags: 0x40),
            "icon" => Hot(S2, 10, icon: 6),
            "visual" => Hot(S2, 10, visual: 8),
            _ => Hot(S2, 10, first: AuraType.ModHealingPct),
        };
        using var kit = new SpellTestKit(Hot(S1, 20), weak);
        (Player caster, _) = kit.AddPlayer(1, 0, 0);
        (Player other, _) = kit.AddPlayer(3, -2, 0);
        (Player target, _) = kit.AddPlayer(2, 3, 0);

        Assert.True(kit.System.AddAura(target, S1, caster: other));
        Assert.True(kit.System.AddAura(target, S2, caster: caster));

        Assert.True(kit.System.HasAura(target, S1));
        Assert.True(kit.System.HasAura(target, S2));
    }

    [Fact]
    public void AHealOverTimeOfTheSameRankFields_IsRefusedWhenWeaker()
    {
        using var kit = new SpellTestKit(Hot(S1, 20), Hot(S2, 10));
        (Player caster, _) = kit.AddPlayer(1, 0, 0);
        (Player other, _) = kit.AddPlayer(3, -2, 0);
        (Player target, _) = kit.AddPlayer(2, 3, 0);

        Assert.True(kit.System.AddAura(target, S1, caster: other));
        Assert.False(kit.System.AddAura(target, S2, caster: caster));

        Assert.True(kit.System.HasAura(target, S1));
        Assert.False(kit.System.HasAura(target, S2));
    }

    private static SpellInfo ObsHot(uint id, int amount) =>
        Spell(id, Effect(SpellEffectName.ApplyAura, amount, aura: AuraType.ObsModHealth, amplitude: 3000)) with
        {
            Name = "Rank",
            SpellFamilyName = FamilyPriest,
            SpellVisual = 7,
            Duration = new SpellDuration(15_000, 0, 15_000),
        };

    [Fact]
    public void AnObsModHealthHealOverTime_IsSinglePerTarget_LikeAPeriodicHeal()
    {
        using var kit = new SpellTestKit(ObsHot(S1, 20), ObsHot(S2, 10), ObsHot(S3, 30));
        (Player caster, _) = kit.AddPlayer(1, 0, 0);
        (Player other, _) = kit.AddPlayer(3, -2, 0);
        (Player target, _) = kit.AddPlayer(2, 3, 0);
        target.MaxHealth = 1_000_000;
        target.Health = 500_000;

        Assert.True(kit.System.AddAura(target, S1, caster: other));
        Assert.False(kit.System.AddAura(target, S2, caster: caster));
        Assert.True(kit.System.AddAura(target, S3, caster: caster));

        Assert.False(kit.System.HasAura(target, S1));
        Assert.False(kit.System.HasAura(target, S2));
        Assert.True(kit.System.HasAura(target, S3));
    }

    [Fact]
    public void AHealOverTimeThatTheOtherTriggers_IsNotRefusedByIt()
    {
        SpellInfo parent = Hot(S1, 20) with
        {
            Effects = [Aura(AuraType.PeriodicHeal, 20) with { Amplitude = 3000 }, Effect(SpellEffectName.TriggerSpell, 0, trigger: S2)],
        };
        using var kit = new SpellTestKit(parent, Hot(S2, 10));
        (Player caster, _) = kit.AddPlayer(1, 0, 0);
        (Player other, _) = kit.AddPlayer(3, -2, 0);
        (Player target, _) = kit.AddPlayer(2, 3, 0);

        Assert.True(kit.System.AddAura(target, S1, caster: other));
        Assert.True(kit.System.AddAura(target, S2, caster: caster));

        Assert.True(kit.System.HasAura(target, S1));
        Assert.True(kit.System.HasAura(target, S2));
    }

    [Fact]
    public void AHealOverTimeThatTriggersTheOther_IsNotRefusedByIt()
    {
        SpellInfo child = Hot(S2, 10) with
        {
            Effects = [Aura(AuraType.PeriodicHeal, 10) with { Amplitude = 3000 }, Effect(SpellEffectName.TriggerSpell, 0, trigger: S1)],
        };
        using var kit = new SpellTestKit(Hot(S1, 20), child);
        (Player caster, _) = kit.AddPlayer(1, 0, 0);
        (Player other, _) = kit.AddPlayer(3, -2, 0);
        (Player target, _) = kit.AddPlayer(2, 3, 0);

        Assert.True(kit.System.AddAura(target, S1, caster: other));
        Assert.True(kit.System.AddAura(target, S2, caster: caster));

        Assert.True(kit.System.HasAura(target, S1));
        Assert.True(kit.System.HasAura(target, S2));
    }

    private static SpellInfo Passive(uint id, int amount, bool withAura = true, bool withArea = false, bool lateAura = false)
    {
        // lateAura: the first effect is a Dummy effect and the aura sits in the second slot, so IsRankOf (first effect) matches a spell with no aura.
        var effects = new List<SpellEffectInfo>();
        if (lateAura)
        {
            effects.Add(Effect(SpellEffectName.Dummy, amount));
        }

        effects.Add(withAura ? Effect(SpellEffectName.ApplyAura, amount, aura: AuraType.Dummy) : Effect(SpellEffectName.Dummy, amount));
        if (withArea)
        {
            effects.Add(Effect(SpellEffectName.ApplyAreaAuraParty, 0, aura: AuraType.Dummy) with { Radius = 30 });
        }

        return Spell(id, effects.ToArray()) with
        {
            Name = "Passive Rank",
            SpellFamilyName = FamilyWarrior,
            SpellVisual = 1,
            Attributes = SpellAttributes.Passive,
            Duration = new SpellDuration(-1, 0, -1),
        };
    }

    private static bool Refused(SpellTestKit kit, SpellInfo spell, Unit target, Unit caster)
    {
        var holder = new SpellAuraHolder(spell, target, caster, new AuraCasterOwner(caster), -1);
        return kit.System.HolderAddRefusals.Any(refuses => refuses(holder));
    }

    [Fact]
    public void APassiveSpellWithoutAnAuraEffect_StacksWithItsStrongerRank_ButOneWithAnAuraIsRefused()
    {
        // All three have a Dummy first effect, so IsRankOf is true and the weaker ranks compare negative: only the no-aura early return spares S2.
        using var kit = new SpellTestKit(Passive(S1, 20, lateAura: true), Passive(S2, 10, withAura: false), Passive(S3, 10, lateAura: true));
        (Player caster, _) = kit.AddPlayer(1, 0, 0);
        (Player target, _) = kit.AddPlayer(2, 3, 0);
        Assert.True(kit.System.AddAura(target, S1, permanent: true, caster: caster));
        Assert.True(SpellSpecifics.IsRankOf(kit.Store.Get(S2)!, kit.Store.Get(S1)!));
        Assert.True(SpellSpecifics.CompareAuraRanks(kit.Store.Get(S2)!, kit.Store.Get(S1)!) < 0);

        Assert.False(Refused(kit, kit.Store.Get(S2)!, target, caster));
        Assert.True(Refused(kit, kit.Store.Get(S3)!, target, caster));
    }

    [Fact]
    public void ANewPartyAreaAura_IsRefusedByAnotherCastersStrongerPassiveRank()
    {
        using var kit = new SpellTestKit(Passive(S1, 20), Passive(S2, 10, withArea: true), Passive(S3, 10));
        (Player caster, _) = kit.AddPlayer(1, 0, 0);
        (Player other, _) = kit.AddPlayer(3, -2, 0);
        (Player target, _) = kit.AddPlayer(2, 3, 0);

        Assert.True(kit.System.AddAura(target, S1, permanent: true, caster: other));

        Assert.False(kit.System.AddAura(target, S2, permanent: true, caster: caster));
        Assert.True(kit.System.AddAura(target, S3, permanent: true, caster: caster));
    }

    [Fact]
    public void ANewPassiveRank_IsRefusedByAnotherCastersStrongerPartyAreaAura()
    {
        using var kit = new SpellTestKit(Passive(S1, 20, withArea: true), Passive(S2, 10));
        (Player caster, _) = kit.AddPlayer(1, 0, 0);
        (Player other, _) = kit.AddPlayer(3, -2, 0);
        (Player target, _) = kit.AddPlayer(2, 3, 0);

        Assert.True(kit.System.AddAura(target, S1, permanent: true, caster: other));

        Assert.False(kit.System.AddAura(target, S2, permanent: true, caster: caster));
        Assert.True(kit.System.HasAura(target, S1));
    }

    private static SpellInfo Sting(uint id, string name, bool deathItem = false) =>
        Spell(id, Effect(SpellEffectName.ApplyAura, 10, SpellImplicitTarget.UnitEnemy, AuraType.Dummy),
            deathItem ? Effect(SpellEffectName.ApplyAura, 1, SpellImplicitTarget.UnitEnemy, AuraType.ChannelDeathItem) : new SpellEffectInfo()) with
        {
            Name = name,
            SpellFamilyName = FamilyHunter,
            Dispel = DispelPoison,
            Attributes = SpellAttributes.AuraIsDebuff,
            SpellVisual = 1,
            Duration = new SpellDuration(15_000, 0, 15_000),
        };

    private static (SpellTestKit Kit, Player Caster, Player Victim) StingKit(bool deathItem)
    {
        var kit = new SpellTestKit(Sting(SerpentSting, "Serpent Sting", deathItem), Sting(ViperSting, "Viper Sting"));
        (Player caster, _) = kit.AddPlayer(1, 0, 0);
        (Player victim, _) = kit.AddPlayer(2, 3, 0);
        kit.System.Relations = new FakeRelations { Hostile = { victim.Guid } };
        return (kit, caster, victim);
    }

    [Fact]
    public void AHolderWithAChannelDeathItemAura_IsNotReplacedByTheSameCastersSting()
    {
        (SpellTestKit kit, Player caster, Player victim) = StingKit(deathItem: true);
        using SpellTestKit owner = kit;
        Assert.True(kit.System.AddAura(victim, SerpentSting, caster: caster));

        Assert.True(kit.System.AddAura(victim, ViperSting, caster: caster));

        Assert.True(kit.System.HasAura(victim, SerpentSting));
        Assert.True(kit.System.HasAura(victim, ViperSting));
    }

    [Fact]
    public void AStingWithoutAChannelDeathItemAura_IsReplacedByTheSameCastersSting()
    {
        (SpellTestKit kit, Player caster, Player victim) = StingKit(deathItem: false);
        using SpellTestKit owner = kit;
        Assert.True(kit.System.AddAura(victim, SerpentSting, caster: caster));

        Assert.True(kit.System.AddAura(victim, ViperSting, caster: caster));

        Assert.False(kit.System.HasAura(victim, SerpentSting));
        Assert.True(kit.System.HasAura(victim, ViperSting));
    }

    [Fact]
    public void AHolderWhoseHandlerIsRunning_IsKeptAndReported()
    {
        (SpellTestKit kit, Player caster, Player victim) = StingKit(deathItem: false);
        using SpellTestKit owner = kit;
        Assert.True(kit.System.AddAura(victim, SerpentSting, caster: caster));
        SpellAuraHolder old = kit.System.GetAuras(victim).Single(h => h.Spell.Id == SerpentSting);
        SpellAura running = old.Auras.First(a => a is not null)!;
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == "spell/stack-rule-parent-in-use" && n.Subject == SpellSystem.FormatSpellSubject(SerpentSting));
        running.InUse = true;

        try
        {
            Assert.True(kit.System.AddAura(victim, ViperSting, caster: caster));
        }
        finally
        {
            running.InUse = false;
        }

        Assert.True(kit.System.HasAura(victim, SerpentSting));
        Assert.True(kit.System.HasAura(victim, ViperSting));
        Assert.Equal(1, capture.Count("spell/stack-rule-parent-in-use"));
    }

    [Fact]
    public void ANewHolderThatADisplacementRemoved_IsNotAdded()
    {
        (SpellTestKit kit, Player caster, Player victim) = StingKit(deathItem: false);
        using SpellTestKit owner = kit;
        kit.System.HolderAddDisplacements.Add(holder => holder.IsRemoved = true);

        Assert.False(kit.System.AddAura(victim, ViperSting, caster: caster));

        Assert.False(kit.System.HasAura(victim, ViperSting));
    }
}
