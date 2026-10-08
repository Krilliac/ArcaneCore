using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Honor;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Progression;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Game.Skills;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Casters.Bonus;
using ArcaneCore.Game.Talents;
using ArcaneCore.Game.Tests.Honor;
using ArcaneCore.Game.Tests.Skills;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Honor;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.Loot;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Rates;

/// <summary>
/// One test per rate family of docs/areas/rates.md: the knob is read where vmangos applies it, defaults to the retail 1 and scales what it names.
/// </summary>
public sealed class RateKnobTests
{
    // ---- Rate.Drop.Item.<quality> / Referenced (vmangos LootStoreItem::Roll, LootMgr.cpp:256-268) -------------------------------------

    private static LootService DropService(LootOptions options, uint quality, float chance, int seed = 7, bool reference = false)
    {
        LootStoreRow row = reference
            ? new LootStoreRow(1, 0, chance, 0, -2, 1)
            : new LootStoreRow(1, 100, chance, 0, 1, 1);
        var content = new LootContent([(LootTableKind.Creature, row), (LootTableKind.Reference, new LootStoreRow(2, 100, 100, 0, 1, 1))], []);
        return new LootService(content, options, new Random(seed))
        {
            Items = new ItemTemplateStore([new ItemTemplate { Entry = 100, Quality = quality }]),
        };
    }

    private static int Drops(LootService service, int rolls)
    {
        int drops = 0;
        for (int i = 0; i < rolls; i++)
        {
            drops += service.Generator.Roll(LootTableKind.Creature, 1).Count;
        }

        return drops;
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(2u)]
    [InlineData(4u)]
    public void TheQualityRate_ScalesTheChanceOfAnUngroupedRow(uint quality)
    {
        var scaled = new LootOptions();
        switch (quality)
        {
            case 0: scaled.DropItemPoorRate = 10.0f; break;
            case 2: scaled.DropItemUncommonRate = 10.0f; break;
            default: scaled.DropItemEpicRate = 10.0f; break;
        }

        Assert.InRange(Drops(DropService(new LootOptions(), quality, 5.0f), 2000), 50, 160);   // about 5 %
        Assert.InRange(Drops(DropService(scaled, quality, 5.0f), 2000), 900, 1100);            // about 50 %
        Assert.Equal(2000, Drops(DropService(scaled, quality, 20.0f), 2000));                  // 200 %: always
    }

    [Fact]
    public void AnotherQualitysRate_LeavesTheRowAlone_AndZeroStopsTheDrop()
    {
        Assert.InRange(Drops(DropService(new LootOptions { DropItemEpicRate = 10.0f }, 2, 5.0f), 2000), 50, 160);
        Assert.Equal(0, Drops(DropService(new LootOptions { DropItemUncommonRate = 0.0f }, 2, 50.0f), 500));
        Assert.Equal(500, Drops(DropService(new LootOptions { DropItemUncommonRate = 0.0f }, 2, 100.0f), 500)); // 100 % drops before the rate
    }

    [Fact]
    public void TheReferencedRate_ScalesAReferenceRow()
    {
        Assert.InRange(Drops(DropService(new LootOptions(), 0, 5.0f, reference: true), 2000), 50, 160);
        Assert.InRange(Drops(DropService(new LootOptions { DropItemReferencedRate = 10.0f }, 0, 5.0f, reference: true), 2000), 900, 1100);
    }

    [Fact]
    public void GroupedRows_AreNotScaled()
    {
        var content = new LootContent([(LootTableKind.Creature, new LootStoreRow(1, 100, 5.0f, 1, 1, 1))], []);
        var service = new LootService(content, new LootOptions { DropItemPoorRate = 20.0f }, new Random(3))
        {
            Items = new ItemTemplateStore([new ItemTemplate { Entry = 100, Quality = 0 }]),
        };

        Assert.InRange(Drops(service, 2000), 50, 160);
    }

    [Fact]
    public void LootOptions_Normalize_ReplacesANegativeRateByOne()
    {
        var options = new LootOptions { DropItemRareRate = -2.0f, DropItemReferencedRate = float.NaN };

        Assert.Equal(["DropItemRareRate", "DropItemReferencedRate"], options.Normalize());
        Assert.Equal(1.0f, options.DropItemRareRate);
        Assert.Equal(1.0f, options.DropItemReferencedRate);
    }

    // ---- Rate.Creature.<rank>.{HP,Damage,SpellDamage} (vmangos Creature.cpp:1802-1843, 1856-1911) ------------------------------------

    private static Creature Spawned(uint rank, CreatureStatRates? rates)
    {
        CreatureTemplate template = Template(5, b => { b.Rank = rank; b.MinLevelHealth = 100; b.MaxLevelHealth = 100; })
            with { MinMeleeDamage = 10, MaxMeleeDamage = 20, MinRangedDamage = 4, MaxRangedDamage = 8 };
        return new Creature(1, template, null, Content([template], []), new Random(1), statRates: rates);
    }

    [Fact]
    public void TheRankRates_ScaleHealthAndWeaponDamage()
    {
        var rates = new CreatureStatRates { NormalHp = 2.5f, NormalDamage = 3.0f, EliteHp = 4.0f, WorldBossDamage = 0.5f };

        Creature normal = Spawned((uint)CreatureRank.Normal, rates);
        Assert.Equal(250u, normal.MaxHealth);
        Assert.Equal(250u, normal.Health);
        Assert.Equal(250u, normal.GetUInt32(UpdateFields.UnitFieldBaseHealth));
        Assert.Equal(30.0f, normal.GetFloat(UpdateFields.UnitFieldMindamage));
        Assert.Equal(60.0f, normal.GetFloat(UpdateFields.UnitFieldMaxdamage));
        Assert.Equal(12.0f, normal.GetFloat(UpdateFields.UnitFieldMinrangeddamage));

        Assert.Equal(400u, Spawned((uint)CreatureRank.Elite, rates).MaxHealth);
        Assert.Equal(10.0f, Spawned((uint)CreatureRank.Elite, rates).GetFloat(UpdateFields.UnitFieldMindamage));
        Assert.Equal(5.0f, Spawned((uint)CreatureRank.WorldBoss, rates).GetFloat(UpdateFields.UnitFieldMindamage));
        Assert.Equal(100u, Spawned((uint)CreatureRank.Rare, rates).MaxHealth);
        Assert.Equal(400u, Spawned(9, rates).MaxHealth); // an unknown rank uses the elite rates (Creature.cpp:1870-1871)
    }

    [Fact]
    public void WithoutRates_ACreatureKeepsItsTemplateStats()
    {
        Creature plain = Spawned((uint)CreatureRank.Normal, null);
        Assert.Equal(100u, plain.MaxHealth);
        Assert.Equal(10.0f, plain.GetFloat(UpdateFields.UnitFieldMindamage));
        Assert.Equal(100u, Spawned((uint)CreatureRank.Normal, new CreatureStatRates()).MaxHealth);
    }

    [Fact]
    public void TheHealthRate_NeverLeavesLessThanOne()
    {
        Assert.Equal(1u, Spawned((uint)CreatureRank.Normal, new CreatureStatRates { NormalHp = 0.0f }).MaxHealth);
    }

    [Fact]
    public void TheMapSystem_SpawnsWithTheConfiguredRates()
    {
        CreatureTemplate template = Template(5, b => { b.Rank = (uint)CreatureRank.Elite; b.MinLevelHealth = 100; b.MaxLevelHealth = 100; });
        var options = new CreatureOptions();
        options.Rates.EliteHp = 3.0f;
        (WorldRuntime world, _, CreatureMapSystem system) = CreateSystem(Content([template], [Spawn(1, template.Entry, 30, 0)]), options);
        world.AddPlayer(TestWorld.CreatePlayer(1, 0, 0, new FakeSession()));
        world.RunTick(50);

        Assert.Equal(300u, Assert.Single(system.Creatures).MaxHealth);
    }

    [Fact]
    public void TheSpellDamageRate_ScalesACreaturesDamageSpells_NotAPlayers()
    {
        using var kit = new SpellTestKit();
        (Player player, _) = kit.AddPlayer(1);
        var module = new SpellBonusModule(kit.System);
        SpellInfo bolt = SpellTestKit.Spell(952001, SpellTestKit.Effect(SpellEffectName.SchoolDamage, 100)) with { DamageClass = SpellDamageClass.Magic };
        Creature elite = Spawned((uint)CreatureRank.Elite, new CreatureStatRates { EliteSpellDamage = 2.0f, NormalSpellDamage = 7.0f });

        Assert.Equal(200.0f, module.Modify(SpellAmountStage.DirectDamage, elite, player, bolt, 0, 100.0f, 1), 3);
        Assert.Equal(200.0f, module.Modify(SpellAmountStage.DamageOverTimeSnapshot, elite, player, bolt, 0, 100.0f, 1), 3);
        Assert.Equal(100.0f, module.Modify(SpellAmountStage.DirectDamage, Spawned((uint)CreatureRank.Elite, null), player, bolt, 0, 100.0f, 1), 3);
        Assert.Equal(100.0f, module.Modify(SpellAmountStage.DirectDamage, player, player, bolt, 0, 100.0f, 1), 3);
    }

    [Fact]
    public void CreatureStatRates_Normalize_ReplacesANegativeRateByOne()
    {
        var rates = new CreatureStatRates { RareHp = -1.0f, NormalSpellDamage = 2.0f };

        Assert.Equal(["RareHp"], rates.Normalize());
        Assert.Equal(1.0f, rates.RareHp);
        Assert.Equal(2.0f, rates.NormalSpellDamage);
    }

    // ---- Rate.InstanceResetTime (vmangos ObjectMgr.cpp:6809-6811) ----------------------------------------------------------------------

    [Theory]
    [InlineData(1.0f, 7u, 7u)]
    [InlineData(2.0f, 7u, 14u)]
    [InlineData(0.5f, 7u, 3u)]
    [InlineData(0.01f, 3u, 1u)]
    [InlineData(3.0f, 0u, 0u)]
    [InlineData(-1.0f, 5u, 5u)]
    public void TheInstanceResetRate_ScalesTheRaidPeriod_AtLeastOneDay(float rate, uint delay, uint expected)
        => Assert.Equal(expected, new InstanceOptions { RateResetTime = rate }.EffectiveResetDelayDays(delay));

    // ---- Rate.XP.Personal (vmangos Player::GiveXP, Player.cpp:3018-3019) ------------------------------------------------------------

    [Fact]
    public void APersonalXpRate_ScalesEveryGain_AndIsOffByDefault()
    {
        using var kit = new SpellTestKit();
        (Player player, _) = kit.AddPlayer(1);
        var progression = new PlayerProgression(new ProgressionOptions());
        progression.InitializeLoadedPlayer(player);

        Assert.Equal(-1.0f, player.PersonalXpRate);
        Assert.Equal(100u, progression.GiveXp(player, 100, ObjectGuid.Empty));

        player.PersonalXpRate = 2.5f;
        Assert.Equal(250u, progression.GiveXp(player, 100, ObjectGuid.Empty));
        Assert.Equal(350u, PlayerProgression.CurrentXp(player));

        player.PersonalXpRate = 0.0f;
        Assert.Equal(0u, progression.GiveXp(player, 100, ObjectGuid.Empty));
        Assert.Equal(350u, PlayerProgression.CurrentXp(player));
    }

    // ---- Rate.Honor (the MaNGOS Zero fork's key, cmangos/TrinityCore semantics) ------------------------------------------------------

    [Fact]
    public void TheHonorRate_ScalesEarnedHonor_NotDishonorOrAGmAmount()
    {
        var honor = new HonorService(new HonorOptions { Rate = 2.0f }, new FixedHonorClock(20_000), () => 19_997);

        Assert.Equal(20.0f, honor.Scaled(10.0f, HonorKind.Honorable));
        Assert.Equal(20.0f, honor.Scaled(10.0f, HonorKind.Bonus));
        Assert.Equal(20.0f, honor.Scaled(10.0f, HonorKind.Quest));
        Assert.Equal(10.0f, honor.Scaled(10.0f, HonorKind.Dishonorable));
        Assert.Equal(10.0f, honor.Scaled(10.0f, HonorKind.Other));
        Assert.Equal(10.0f, new HonorService(new HonorOptions(), new FixedHonorClock(20_000), () => 19_997).Scaled(10.0f, HonorKind.Honorable));
    }

    // ---- Existing knobs: Rate.Reputation.Gain, Rate.Talent, SkillGain.Gathering --------------------------------------------------------

    [Fact]
    public void TheReputationGainRate_ScalesGainsAndLosses_AsVmangosCalculateReputationGain()
    {
        var rates = new ReputationRates { Gain = 3.0f };

        Assert.Equal(300.0f, ReputationMath.GainBeforeDither(ReputationSource.Spell, 100, 60, 60, rates));
        Assert.Equal(-300.0f, ReputationMath.GainBeforeDither(ReputationSource.Spell, -100, 60, 60, rates));
        Assert.Equal(100.0f, ReputationMath.GainBeforeDither(ReputationSource.Spell, 100, 60, 60, new ReputationRates()));
    }

    [Fact]
    public void TheTalentRate_ScalesTheFreePoints()
    {
        Assert.Equal(51u, TalentRules.DecideFreePoints(60, 0, 1.0, resetIfNeed: false, administrator: false).FreePoints);
        Assert.Equal(102u, TalentRules.DecideFreePoints(60, 0, 2.0, resetIfNeed: false, administrator: false).FreePoints);
    }

    [Fact]
    public void TheGatheringSkillGain_IsTheStepOfAGatheringSkillUp()
    {
        using var kit = new SpellTestKit();
        (Player player, _) = kit.AddPlayer(1);
        var random = new ScriptedSkillRandom();
        random.Ints.Enqueue(0);
        var skills = new PlayerSkills(player, SkillTestKit.Catalog(), new SkillOptions { GainGathering = 3 }, new FakeSkillSpellHost(), random);
        Assert.True(skills.Set(SkillIds.Fishing, 1, 75));

        Assert.True(skills.UpdateFishing());

        Assert.Equal(4, skills.GetValuePure(SkillIds.Fishing));
    }
}
