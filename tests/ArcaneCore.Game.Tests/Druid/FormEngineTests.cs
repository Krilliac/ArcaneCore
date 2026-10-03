using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Druid;

/// <summary>
/// The druid form handler against vmangos HandleAuraModShapeshift (SpellAuras.cpp:2420-2625), HandleShapeshiftBoosts
/// (:5433-5597) and InitDataForForm (Player.cpp:18271-18312), with the real 1.12.1 form table
/// (<see cref="ShapeshiftFormCatalog.Retail"/>) and classic-db spell values: form spells carry Attributes 0x50010 (includes
/// NOT_SHAPESHIFT), Furor's proc spells 17099 (Energize 40 energy) and 17057 (Energize 100 rage), Leader of the Pack 24932
/// Stances 0x91. The Furor roll is made deterministic (chance 0 or 100 only).
/// </summary>
public sealed class FormEngineTests
{
    private const uint CatForm = 768;
    private const uint BearForm = 5487;
    private const uint DireBearForm = 9634;
    private const uint TravelForm = 783;
    private const uint AquaticForm = 1066;
    private const uint MoonkinForm = 24858;
    private const uint FormEffect = 9033;

    private const uint CatBoost = 3025;
    private const uint BearBoost = 1178;
    private const uint BearBoost2 = 21178;
    private const uint DireBearBoost = 9635;
    private const uint TravelBoost = 5419;
    private const uint AquaticBoost = 5421;
    private const uint MoonkinBoost = 24905;

    private const uint FurorEnergy = 17099;
    private const uint FurorRage = 17057;
    private const uint Furor100 = 900801;      // the talent: Dummy aura, SpellIconID 238, 100 percent
    private const uint Furor0 = 900802;

    private const uint FelineSwiftness = 17002;
    private const uint LeaderKnown = 17007;
    private const uint LeaderEffect = 24932;
    private const uint TigersFury = 5217;       // a cat-only self buff (Stances 1)
    private const uint Claw = 1082;             // Stances cat
    private const uint HealingTouch = 5185;     // NOT_SHAPESHIFT
    private const uint ShiftCancelled = 900810; // AuraInterruptFlags SHAPESHIFTING_CANCELS
    private const uint WarriorBattle = 2457;
    private const uint WarriorBoost = 21156;

    private static SpellInfo Form(uint id, int form) => Spell(id, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.ModShapeshift, misc: form)) with
    {
        Attributes = (SpellAttributes)0x50010u,
        Duration = new SpellDuration(-1, 0, -1),
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static SpellInfo PassiveSpell(uint id, uint stances = 0, AuraType aura = AuraType.Dummy, int value = 0) => Spell(id, Effect(SpellEffectName.ApplyAura, value, aura: aura)) with
    {
        Attributes = (SpellAttributes)0x1d0u,
        Stances = stances,
        Duration = new SpellDuration(-1, 0, -1),
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static IEnumerable<SpellInfo> Spells()
    {
        yield return Form(CatForm, 1);
        yield return Form(BearForm, 5);
        yield return Form(DireBearForm, 8);
        yield return Form(TravelForm, 3);
        yield return Form(AquaticForm, 4);
        yield return Form(MoonkinForm, 31);
        yield return Form(WarriorBattle, 17) with { AttributesEx3 = 0x00100000 };
        yield return Spell(FormEffect, Effect(SpellEffectName.ScriptEffect, 0)) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 };
        yield return PassiveSpell(CatBoost);
        yield return PassiveSpell(BearBoost);
        yield return PassiveSpell(BearBoost2);
        yield return PassiveSpell(DireBearBoost);
        yield return PassiveSpell(TravelBoost);
        yield return PassiveSpell(AquaticBoost);
        yield return PassiveSpell(MoonkinBoost);
        yield return PassiveSpell(WarriorBoost);
        yield return Spell(FurorEnergy, Effect(SpellEffectName.Energize, 40, misc: (int)PowerType.Energy)) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 };
        yield return Spell(FurorRage, Effect(SpellEffectName.Energize, 100, misc: (int)PowerType.Rage)) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 };
        yield return PassiveSpell(Furor100, value: 100) with { SpellIconId = 238 };
        yield return PassiveSpell(Furor0, value: 0) with { SpellIconId = 238 };
        yield return PassiveSpell(FelineSwiftness, stances: 1, aura: AuraType.ModIncreaseSpeed, value: 15);
        yield return PassiveSpell(LeaderKnown);
        yield return Spell(LeaderEffect, Effect(SpellEffectName.ApplyAura, 3, aura: AuraType.ModCritPercent)) with
        {
            Stances = 0x91,
            Duration = new SpellDuration(-1, 0, -1),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };
        yield return Spell(TigersFury, Effect(SpellEffectName.ApplyAura, 3, aura: AuraType.ModCritPercent)) with
        {
            Stances = 1,
            Duration = new SpellDuration(6000, 0, 6000),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };
        yield return Spell(Claw, Effect(SpellEffectName.ApplyAura, 3, aura: AuraType.ModCritPercent)) with
        {
            Stances = 1,
            Duration = new SpellDuration(6000, 0, 6000),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };
        yield return Spell(HealingTouch, Effect(SpellEffectName.ApplyAura, 3, aura: AuraType.ModCritPercent)) with
        {
            Attributes = (SpellAttributes)0x10000u,
            Duration = new SpellDuration(6000, 0, 6000),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };
        yield return Spell(ShiftCancelled, Effect(SpellEffectName.ApplyAura, 3, aura: AuraType.ModCritPercent)) with
        {
            AuraInterruptFlags = (SpellAuraInterruptFlags)0x8000u,
            Duration = new SpellDuration(60000, 0, 60000),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };
    }

    /// <summary>A Random whose Next(min, max) always answers <c>roll</c>.</summary>
    private sealed class FixedRandom(int roll) : Random
    {
        public override int Next(int minValue, int maxValue) => roll;
    }

    private sealed class CastCounter : ISpellCastObserver
    {
        public Dictionary<uint, int> Casts { get; } = [];

        public void OnCast(SpellCast cast) => Casts[cast.Spell.Id] = Casts.GetValueOrDefault(cast.Spell.Id) + 1;
    }

    private sealed class FormListener : IFormChangeListener
    {
        public List<(byte Old, byte New)> Changes { get; } = [];

        public void OnFormChanged(Unit unit, byte oldForm, byte newForm) => Changes.Add((oldForm, newForm));
    }

    private sealed class Rig : IDisposable
    {
        public Rig(Race race = Race.NightElf, Class playerClass = Class.Druid, float scale = 1.0f)
        {
            Kit = new SpellTestKit([.. Spells()]);
            var character = new CharacterRecord
            {
                Id = 7, AccountId = 1, Name = "Druid", Race = (byte)race, Class = (byte)playerClass, Gender = (byte)Gender.Female,
                Level = 60, MapId = 0, ZoneId = 12, Z = 83.5f,
            };
            var appearance = new PlayerAppearance(
                DisplayId: 2222, FactionTemplate: 4, PowerType.Mana, BaseHealth: 60, BaseMana: 100,
                MaxHealth: 1000, MaxPower: 500, StartPower: 500, NextLevelXp: 400);
            Player = new Player(character, appearance, new FakeSession(1));
            Player.SetFloat(UpdateFields.ObjectFieldScaleX, scale);
            Kit.World.AddPlayer(Player);
            Kit.World.RunTick(0);
            Service = new ShapeshiftService(Kit.System, ShapeshiftFormCatalog.Retail, new CombatOptions(),
                p => KnownSpells.TryGetValue(p.Guid, out List<uint>? list) ? list : []);
            Service.Install();
            Service.AddListener(Listener);
            Kit.System.RegisterObserver(Counter);
            Kit.System.Random = new FixedRandom(100);
        }

        public SpellTestKit Kit { get; }

        public Player Player { get; }

        public ShapeshiftService Service { get; }

        public CastCounter Counter { get; } = new();

        public FormListener Listener { get; } = new();

        public Dictionary<ObjectGuid, List<uint>> KnownSpells { get; } = [];

        public SpellSystem System => Kit.System;

        public SpellCastResult Cast(uint spell)
        {
            Kit.Advance(1500);
            return System.CastSpell(Player, spell, SpellCastTargets.ForSelf(), triggered: false);
        }

        /// <summary>A server-side cast (GM .aura, a script): the client would have cancelled the old form first, because every form spell is NOT_SHAPESHIFT.</summary>
        public SpellCastResult CastTriggered(uint spell) => System.CastSpell(Player, spell, SpellCastTargets.ForSelf(), triggered: true);

        public void Know(params uint[] spells)
        {
            KnownSpells[Player.Guid] = [.. spells];
            foreach (uint spell in spells)
            {
                System.CastLearnedPassive(Player, spell);
            }
        }

        public bool Has(uint spell) => System.HasAura(Player, spell);

        public byte Form => Player.GetByte(UpdateFields.UnitFieldBytes1, 2);

        public uint Power(PowerType type) => MapCombat.GetPower(Player, type);

        public uint MaxPower(PowerType type) => MapCombat.GetMaxPower(Player, type);

        public void Dispose() => Kit.Dispose();
    }

    // --- cat form -----------------------------------------------------------------------------------------------

    [Fact]
    public void CatForm_WritesForm1_Display892_Scale0_8_Energy100Max0Value_AndAddsBoost3025()
    {
        using var rig = new Rig();
        uint native = rig.Player.NativeDisplayId;

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(CatForm));

        Assert.Equal(1, rig.Form);
        Assert.Equal(892u, rig.Player.DisplayId);                          // Alliance cat model (SpellAuras.cpp:2325-2332)
        Assert.Equal(native, rig.Player.NativeDisplayId);
        Assert.Equal(0.8f, rig.Player.GetFloat(UpdateFields.ObjectFieldScaleX), 4);
        Assert.Equal(PowerType.Energy, rig.Player.PowerType);
        Assert.Equal(100u, rig.MaxPower(PowerType.Energy));
        Assert.Equal(0u, rig.Power(PowerType.Energy));
        Assert.True(rig.Has(CatForm));
        Assert.True(rig.Has(CatBoost));
        Assert.Equal(1, rig.Counter.Casts[FormEffect]);                    // cast once (SpellAuras.cpp:2436-2445)
    }

    [Fact]
    public void TaurenCat_UsesTheHordeModel8571_AndLeavingRestoresTheNativeDisplayAndExactScale()
    {
        using var rig = new Rig(Race.Tauren, scale: 1.35f);
        uint native = rig.Player.NativeDisplayId;

        rig.Cast(CatForm);

        Assert.Equal(8571u, rig.Player.DisplayId);
        Assert.Equal(0.8f, rig.Player.GetFloat(UpdateFields.ObjectFieldScaleX), 4);

        rig.System.CancelAura(rig.Player, CatForm);

        Assert.Equal(native, rig.Player.DisplayId);
        Assert.Equal(1.35f, rig.Player.GetFloat(UpdateFields.ObjectFieldScaleX), 4);
        Assert.Equal(0, rig.Form);
    }

    [Fact]
    public void LeavingCat_BackToMana_FormByteZero_BoostAndFormSelfBuffsGone_AndListenerHeardBoth()
    {
        using var rig = new Rig();
        rig.Know(FelineSwiftness);
        rig.Cast(CatForm);
        rig.Cast(TigersFury);
        Assert.True(rig.Has(FelineSwiftness));      // a known passive that needs the form
        Assert.True(rig.Has(TigersFury));

        rig.System.CancelAura(rig.Player, CatForm);

        Assert.Equal(0, rig.Form);
        Assert.Equal(PowerType.Mana, rig.Player.PowerType);
        Assert.False(rig.Has(CatForm));
        Assert.False(rig.Has(CatBoost));
        Assert.False(rig.Has(FelineSwiftness));     // IsRemovedOnShapeLost
        Assert.False(rig.Has(TigersFury));
        Assert.Equal([((byte)0, (byte)1), ((byte)1, (byte)0)], rig.Listener.Changes);
    }

    // --- bear, dire bear ----------------------------------------------------------------------------------------

    [Fact]
    public void CatToBear_RemovesCatBoost_PowerRage_Max1000_Rage0_AndTheBearModelAndBoostsStay()
    {
        using var rig = new Rig();
        rig.Cast(CatForm);
        Assert.Equal(SpellCastResult.NotShapeshift, rig.Cast(BearForm));   // cast while shifted: the client cancels the form first

        rig.CastTriggered(BearForm);

        Assert.Equal(5, rig.Form);
        Assert.False(rig.Has(CatForm));
        Assert.False(rig.Has(CatBoost));
        Assert.True(rig.Has(BearBoost));
        Assert.True(rig.Has(BearBoost2));
        Assert.Equal(PowerType.Rage, rig.Player.PowerType);
        Assert.Equal(1000u, rig.MaxPower(PowerType.Rage));
        Assert.Equal(0u, rig.Power(PowerType.Rage));
        Assert.Equal(2281u, rig.Player.DisplayId);                         // the display was not undone by the old form's removal
        Assert.Equal(1.0f, rig.Player.GetFloat(UpdateFields.ObjectFieldScaleX), 4);
    }

    [Fact]
    public void DireBear_UsesItsOwnBoost9635_AndTheSharedBoost21178()
    {
        using var rig = new Rig();

        rig.Cast(DireBearForm);

        Assert.Equal(8, rig.Form);
        Assert.True(rig.Has(DireBearBoost));
        Assert.True(rig.Has(BearBoost2));
        Assert.False(rig.Has(BearBoost));
        Assert.Equal(PowerType.Rage, rig.Player.PowerType);
    }

    [Fact]
    public void LeavingBear_PowerMana_RageZero_FormByteZero_AndBoostsRemoved()
    {
        using var rig = new Rig();
        rig.Cast(BearForm);
        MapCombat.SetPower(rig.Player, PowerType.Rage, 300);

        rig.System.CancelAura(rig.Player, BearForm);

        Assert.Equal(0, rig.Form);
        Assert.Equal(PowerType.Mana, rig.Player.PowerType);
        Assert.Equal(0u, rig.Power(PowerType.Rage));
        Assert.Equal(500u, rig.Power(PowerType.Mana));
        Assert.False(rig.Has(BearBoost));
        Assert.False(rig.Has(BearBoost2));
        Assert.Equal(rig.Player.NativeDisplayId, rig.Player.DisplayId);
    }

    [Fact]
    public void RageAHumanoidDruidHas_IsCarriedIntoBear()
    {
        using var rig = new Rig();
        PowerTypeSwitchCaps(rig.Player);
        MapCombat.SetPower(rig.Player, PowerType.Rage, 300);

        rig.Cast(BearForm);

        Assert.Equal(300u, rig.Power(PowerType.Rage));                     // SetPower(POWER_RAGE, powaa), SpellAuras.cpp:2527
    }

    private static void PowerTypeSwitchCaps(Player player) => ArcaneCore.Game.Spells.Druid.PowerTypeSwitch.EnsureFeralPowerCaps(player);

    // --- Furor --------------------------------------------------------------------------------------------------

    [Fact]
    public void Furor_CastsTheEnergyProcOnCat_TheRageProcOnBear_OnlyWhenTheChanceHolds()
    {
        using var certain = new Rig();
        certain.Know(Furor100);
        certain.Cast(CatForm);
        Assert.True(certain.Power(PowerType.Energy) > 0);                  // Furor 17099 energized after the reset to 0
        Assert.Equal(1, certain.Counter.Casts[FurorEnergy]);

        using var certainBear = new Rig();
        certainBear.Know(Furor100);
        certainBear.Cast(BearForm);
        Assert.True(certainBear.Power(PowerType.Rage) > 0);
        Assert.Equal(1, certainBear.Counter.Casts[FurorRage]);

        using var never = new Rig();
        never.Know(Furor0);
        never.Cast(CatForm);
        Assert.Equal(0u, never.Power(PowerType.Energy));
        Assert.False(never.Counter.Casts.ContainsKey(FurorEnergy));

        using var untalented = new Rig();
        untalented.Cast(BearForm);
        Assert.Equal(0u, untalented.Power(PowerType.Rage));
        Assert.False(untalented.Counter.Casts.ContainsKey(FurorRage));
    }

    // --- travel, aquatic, moonkin, tree -------------------------------------------------------------------------

    [Theory]
    [InlineData(TravelForm, 3, 632u, TravelBoost)]
    [InlineData(AquaticForm, 4, 2428u, AquaticBoost)]
    [InlineData(MoonkinForm, 31, 15374u, MoonkinBoost)]
    public void NonPowerForms_SetFormDisplayAndBoost_AndKeepManaAsThePower(uint spell, byte form, uint display, uint boost)
    {
        using var rig = new Rig();

        rig.Cast(spell);

        Assert.Equal(form, rig.Form);
        Assert.Equal(display, rig.Player.DisplayId);
        Assert.True(rig.Has(boost));
        Assert.Equal(PowerType.Mana, rig.Player.PowerType);
        Assert.Equal(1, rig.Counter.Casts[FormEffect]);
    }

    [Fact]
    public void TheShapeshiftFormEffect_IsNotCastByWarriorStances()
    {
        using var rig = new Rig(Race.Human, Class.Warrior);

        rig.Cast(WarriorBattle);

        Assert.Equal(17, rig.Form);
        Assert.False(rig.Counter.Casts.ContainsKey(FormEffect));
        Assert.Equal(rig.Player.NativeDisplayId, rig.Player.DisplayId);    // a stance has no model
        Assert.Equal(PowerType.Rage, rig.Player.PowerType);
    }

    // --- passives and Leader of the Pack ------------------------------------------------------------------------

    [Fact]
    public void LeaderOfThePack_IsCastInCatAndBear_NotInTravel_AndOnlyWhenKnown()
    {
        using var rig = new Rig();
        rig.Know(LeaderKnown);

        rig.Cast(CatForm);
        Assert.True(rig.Has(LeaderEffect));
        rig.System.CancelAura(rig.Player, CatForm);
        Assert.False(rig.Has(LeaderEffect));                               // Stances-bound: removed with the form
        rig.Cast(TravelForm);
        Assert.False(rig.Has(LeaderEffect));

        using var unknown = new Rig();
        unknown.Cast(CatForm);
        Assert.False(unknown.Has(LeaderEffect));
    }

    // --- gating -------------------------------------------------------------------------------------------------

    [Fact]
    public void HealingTouch_FailsNotShapeshift_InCat_OkAfterCancelAura()
    {
        using var rig = new Rig();
        rig.Cast(CatForm);

        Assert.Equal(SpellCastResult.NotShapeshift, rig.Cast(HealingTouch));

        rig.System.CancelAura(rig.Player, CatForm);
        Assert.Equal(SpellCastResult.CastOk, rig.Cast(HealingTouch));
    }

    [Fact]
    public void Claw_FailsOnlyShapeshift_InHumanoid_OkInCat()
    {
        using var rig = new Rig();

        Assert.Equal(SpellCastResult.OnlyShapeshift, rig.Cast(Claw));

        rig.Cast(CatForm);
        Assert.Equal(SpellCastResult.CastOk, rig.Cast(Claw));
    }

    [Fact]
    public void ANonStanceForm_RemovesAurasThatShapeshiftingCancels_AWarriorStanceDoesNot()
    {
        using var druid = new Rig();
        druid.System.CastSpell(druid.Player, ShiftCancelled, SpellCastTargets.ForSelf(), triggered: true);
        Assert.True(druid.Has(ShiftCancelled));
        druid.Cast(CatForm);
        Assert.False(druid.Has(ShiftCancelled));

        using var warrior = new Rig(Race.Human, Class.Warrior);
        warrior.System.CastSpell(warrior.Player, ShiftCancelled, SpellCastTargets.ForSelf(), triggered: true);
        warrior.Cast(WarriorBattle);
        Assert.True(warrior.Has(ShiftCancelled));                          // flags1 has the Stance bit
    }

    [Fact]
    public void Death_RemovesTheForm_AndTheDruidIsBackOnMana()
    {
        using var rig = new Rig();
        rig.Cast(CatForm);
        Assert.Equal(PowerType.Energy, rig.Player.PowerType);

        (Player enemy, _) = rig.Kit.AddPlayer(2, 2);
        rig.Player.Map!.FindUpdater<MapCombat>()!.Kill(enemy, rig.Player);
        rig.Kit.System.OnUnitDied(rig.Player);

        Assert.Equal(0, rig.Form);
        Assert.False(rig.Has(CatForm));
        Assert.Equal(PowerType.Mana, rig.Player.PowerType);
        Assert.Equal(rig.Player.NativeDisplayId, rig.Player.DisplayId);
    }

    [Fact]
    public void TheRetailCatalogLinksThroughTheServiceFlags()
    {
        using var rig = new Rig();

        Assert.Equal(0u, rig.Service.GetFormFlags(ShapeshiftForm.Cat));
        Assert.Equal(7u, rig.Service.GetFormFlags(ShapeshiftForm.BattleStance));
        Assert.True(ShapeshiftService.CastsShapeshiftFormEffect(ShapeshiftForm.Moonkin));
        Assert.False(ShapeshiftService.CastsShapeshiftFormEffect(ShapeshiftForm.GhostWolf));
        Assert.False(ShapeshiftService.CastsShapeshiftFormEffect(ShapeshiftForm.Stealth));
    }
}
