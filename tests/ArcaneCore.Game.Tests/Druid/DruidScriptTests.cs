using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Druid;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Druid;

/// <summary>
/// CastCustomSpell (vmangos SpellCaster.cpp:1147-1215, 2279-2330) and the druid scripts on it: Frenzied Regeneration
/// (SpellAuras.cpp:1417-1433), Enrage (spell_druid.cpp:75-100) and Heart of the Wild (SpellAuras.cpp:5505-5530). Spell data are
/// the classic-db rows: Enrage 5229 (Stances 0x90, effect 1 Dummy), its aura 25503 (ModResistancePct on effect index 1),
/// Frenzied Regeneration 22842/22895/22896 (PeriodicTriggerSpell, amounts 10/15/20, 1 s) with its heal 22845 (base points 0), Heart
/// of the Wild 17003 (ModTotalStatPercentage misc 3 amount 4) and its effects 24900 (cat, strength) / 24899 (bear, stamina).
/// </summary>
public sealed class DruidScriptTests : IDisposable
{
    private const uint CatForm = 768;
    private const uint BearForm = 5487;
    private const uint DireBearForm = 9634;
    private const uint Enrage = 5229;
    private const uint EnrageArmor = 25503;
    private const uint FrenziedRank1 = 22842;
    private const uint FrenziedRank2 = 22895;
    private const uint FrenziedRank3 = 22896;
    private const uint FrenziedHeal = 22845;
    private const uint HotwTalent = 17003;
    private const uint HotwCat = 24900;
    private const uint HotwBear = 24899;
    private const uint PlainHeal = 900901;
    private const uint DiceHeal = 900902;
    private const uint ResistSpell = 900903;

    private static SpellInfo Form(uint id, int form) => Spell(id, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.ModShapeshift, misc: form)) with
    {
        Attributes = (SpellAttributes)0x50010u,
        Duration = new SpellDuration(-1, 0, -1),
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static SpellInfo Frenzied(uint id, int amount) => Spell(id, Effect(SpellEffectName.ApplyAura, amount, aura: AuraType.PeriodicTriggerSpell, amplitude: 1000)) with
    {
        Attributes = (SpellAttributes)0x40010u,
        Stances = 0x90,
        Duration = new SpellDuration(10000, 0, 10000),
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static IEnumerable<SpellInfo> Spells()
    {
        yield return Form(CatForm, 1);
        yield return Form(BearForm, 5);
        yield return Form(DireBearForm, 8);
        yield return Spell(Enrage,
            Effect(SpellEffectName.ApplyAura, 20, aura: AuraType.PeriodicEnergize, amplitude: 1000, misc: (int)PowerType.Rage),
            Effect(SpellEffectName.Dummy, -75),
            Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.InterruptRegen)) with
        {
            Attributes = (SpellAttributes)0x40110u,
            Stances = 0x90,
            Duration = new SpellDuration(10000, 0, 10000),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };
        yield return Spell(EnrageArmor, new SpellEffectInfo(), Effect(SpellEffectName.ApplyAura, -27, aura: AuraType.ModResistancePct, misc: 1)) with
        {
            Attributes = (SpellAttributes)0x40190u,
            Stances = 0x90,
            Duration = new SpellDuration(10000, 0, 10000),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };
        yield return Frenzied(FrenziedRank1, 10);
        yield return Frenzied(FrenziedRank2, 15);
        yield return Frenzied(FrenziedRank3, 20);
        yield return Spell(FrenziedHeal, Effect(SpellEffectName.Heal, 0)) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 };
        yield return Spell(HotwTalent, Effect(SpellEffectName.ApplyAura, 4, aura: AuraType.ModTotalStatPercentage, misc: 3)) with
        {
            Attributes = (SpellAttributes)0x1d0u,
            SpellIconId = 240,
            Duration = new SpellDuration(-1, 0, -1),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };
        yield return Spell(HotwCat, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ModTotalStatPercentage, misc: 0)) with
        {
            Attributes = (SpellAttributes)0x190u,
            Stances = 1,
            SpellIconId = 240,
            Duration = new SpellDuration(-1, 0, -1),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };
        yield return Spell(HotwBear, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ModTotalStatPercentage, misc: 2)) with
        {
            Attributes = (SpellAttributes)0x190u,
            Stances = 0x90,
            SpellIconId = 240,
            Duration = new SpellDuration(-1, 0, -1),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };
        yield return Spell(PlainHeal, Effect(SpellEffectName.Heal, 10), Effect(SpellEffectName.ApplyAura, 5, aura: AuraType.ModResistancePct, misc: 1)) with
        {
            Duration = new SpellDuration(10000, 0, 10000),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };
        yield return Spell(DiceHeal, Effect(SpellEffectName.Heal, 0) with { BasePoints = 49, BaseDice = 1, DieSides = 11 }) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 };
        yield return Spell(ResistSpell, Effect(SpellEffectName.ApplyAura, 5, aura: AuraType.ModResistancePct, misc: 1)) with
        {
            Duration = new SpellDuration(10000, 0, 10000),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };
    }

    private readonly SpellTestKit _kit;
    private readonly Player _player;
    private readonly List<uint> _known = [];

    public DruidScriptTests()
    {
        _kit = new SpellTestKit([.. Spells()]);
        var character = new CharacterRecord
        {
            Id = 7, AccountId = 1, Name = "Druid", Race = (byte)Race.NightElf, Class = (byte)Class.Druid, Gender = (byte)Gender.Female,
            Level = 60, MapId = 0, ZoneId = 12, Z = 83.5f,
        };
        var appearance = new PlayerAppearance(
            DisplayId: 2222, FactionTemplate: 4, PowerType.Mana, BaseHealth: 60, BaseMana: 100,
            MaxHealth: 1000, MaxPower: 500, StartPower: 500, NextLevelXp: 400);
        _player = new Player(character, appearance, new FakeSession(1));
        _kit.World.AddPlayer(_player);
        _kit.World.RunTick(0);
        new ShapeshiftService(_kit.System, ShapeshiftFormCatalog.Retail, new CombatOptions(), _ => _known).Install();
    }

    public void Dispose() => _kit.Dispose();

    private SpellCastResult CastSelf(uint spell) => _kit.System.CastSpell(_player, spell, SpellCastTargets.ForSelf(), triggered: true);

    private SpellAura? Aura(uint spell, AuraType type) => _kit.System.GetAuras(_player)
        .Where(h => h.Spell.Id == spell && !h.IsRemoved).SelectMany(h => h.Auras).FirstOrDefault(a => a is not null && a.Type == type);

    private uint Health => _player.GetUInt32(UpdateFields.UnitFieldHealth);

    private static uint Rage(Player player) => MapCombat.GetPower(player, PowerType.Rage);

    // --- the custom base points seam ----------------------------------------------------------------------------

    [Fact]
    public void CastCustomSpell_ReplacesTheDirectAndTheAuraValue_ForThatCastOnly()
    {
        _player.Health = 100;

        Assert.Equal(SpellCastResult.CastOk, _kit.System.CastCustomSpell(_player, PlainHeal, SpellCastTargets.ForSelf(), 77, -27));

        Assert.Equal(177u, Health);                                                   // Heal effect 0 replaced: 77, not 10
        Assert.Equal(-27, Aura(PlainHeal, AuraType.ModResistancePct)!.Amount);        // effect 1 replaced: -27, not 5

        _kit.System.RemoveAuras(_player, PlainHeal);
        CastSelf(PlainHeal);                                                          // an ordinary cast afterwards is untouched
        Assert.Equal(5, Aura(PlainHeal, AuraType.ModResistancePct)!.Amount);
        Assert.Equal(187u, Health);
    }

    [Fact]
    public void CastCustomSpell_NullKeepsTheSpellsOwnBasePoints()
    {
        _player.Health = 100;

        _kit.System.CastCustomSpell(_player, PlainHeal, SpellCastTargets.ForSelf(), null, -9);

        Assert.Equal(110u, Health);                                                   // effect 0 stays 10
        Assert.Equal(-9, Aura(PlainHeal, AuraType.ModResistancePct)!.Amount);
    }

    [Fact]
    public void CastCustomSpell_WithADie_StillRollsTheDieOnTopOfTheExplicitBasePoints()
    {
        for (int i = 0; i < 20; i++)
        {
            _player.Health = 100;

            _kit.System.CastCustomSpell(_player, DiceHeal, SpellCastTargets.ForSelf(), 500);

            uint healed = Health - 100;
            Assert.InRange(healed, 500u, 510u);                                       // bp 500 - dice 1 + roll(1..11)
        }
    }

    [Fact]
    public void CastCustomSpell_OfAnUnknownSpell_IsNotFound()
        => Assert.Equal(SpellCastResult.NotFound, _kit.System.CastCustomSpell(_player, 1, SpellCastTargets.ForSelf(), 5));

    [Fact]
    public void ThePeriodicTriggerScriptRegistry_RejectsADuplicate()
        => Assert.Throws<InvalidOperationException>(() => _kit.System.RegisterPeriodicTriggerScript(FrenziedRank1, (_, _, _) => { }));

    // --- Enrage -------------------------------------------------------------------------------------------------

    [Fact]
    public void Enrage_InBear_AddsAura25503AmountMinus27_InDireBearMinus16()
    {
        CastSelf(BearForm);
        Assert.Equal(SpellCastResult.CastOk, CastSelf(Enrage));
        Assert.Equal(-27, Aura(EnrageArmor, AuraType.ModResistancePct)!.Amount);

        _kit.System.CancelAura(_player, BearForm);
        _kit.System.RemoveAuras(_player, Enrage);
        _kit.System.RemoveAuras(_player, EnrageArmor);
        CastSelf(DireBearForm);
        CastSelf(Enrage);

        Assert.Equal(-16, Aura(EnrageArmor, AuraType.ModResistancePct)!.Amount);
    }

    // --- Frenzied Regeneration ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(FrenziedRank1, 100u, 100u)]    // 100 stored rage (10 rage) x 10 life per rage... amount 10: 100 * 10 / 10
    [InlineData(FrenziedRank2, 100u, 150u)]    // amount 15
    [InlineData(FrenziedRank3, 100u, 200u)]    // amount 20
    [InlineData(FrenziedRank3, 30u, 60u)]      // less rage: 30 * 20 / 10
    [InlineData(FrenziedRank3, 250u, 200u)]    // more than 100 stored rage: the tick takes 100
    [InlineData(FrenziedRank3, 0u, 0u)]        // no rage: a heal of 0
    public void FrenziedRegeneration_Tick_SpendsUpTo100StoredRage_AndHealsRageTimesAmountOverTen(uint rank, uint storedRage, uint heal)
    {
        PowerTypeSwitch.EnsureFeralPowerCaps(_player);
        MapCombat.SetPower(_player, PowerType.Rage, storedRage);
        _player.Health = 300;
        CastSelf(rank);

        _kit.Advance(1000);

        Assert.Equal(300u + heal, Health);
        Assert.Equal(storedRage - Math.Min(storedRage, 100u), Rage(_player));
    }

    // --- Heart of the Wild --------------------------------------------------------------------------------------

    [Fact]
    public void HeartOfTheWild_InCat_AddsEffect24900WithTheTalentAmount_InBear24899_NotWithoutTheTalent()
    {
        CastSelf(CatForm);
        Assert.Null(Aura(HotwCat, AuraType.ModTotalStatPercentage));                  // no talent, no cast
        _kit.System.CancelAura(_player, CatForm);

        _known.Add(HotwTalent);
        _kit.System.CastLearnedPassive(_player, HotwTalent);
        CastSelf(CatForm);

        Assert.Equal(4, Aura(HotwCat, AuraType.ModTotalStatPercentage)!.Amount);
        Assert.Null(Aura(HotwBear, AuraType.ModTotalStatPercentage));

        _kit.System.CancelAura(_player, CatForm);
        Assert.Null(Aura(HotwCat, AuraType.ModTotalStatPercentage));                  // bound to the form by Stances

        CastSelf(BearForm);
        Assert.Equal(4, Aura(HotwBear, AuraType.ModTotalStatPercentage)!.Amount);
    }
}
