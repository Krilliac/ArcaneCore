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
/// A saved form survives login: vmangos loads the auras first and restores the saved health and powers afterwards
/// (Player.cpp:15057-15070), so a cat druid keeps its energy, a bear its rage and a warrior its stance rage, and the form
/// handler's power reset (energy 0, rage cut to Tactical Mastery's) and the Furor roll of an applied form have no lasting
/// effect. Here the same outcome comes from the handler leaving the powers alone while
/// <see cref="SpellSystem.RestoreAuras"/> runs.
/// </summary>
public sealed class FormRestoreTests
{
    private const uint CatForm = 768;
    private const uint BearForm = 5487;
    private const uint WarriorBattle = 2457;
    private const uint FurorEnergy = 17099;
    private const uint FurorRage = 17057;
    private const uint Furor100 = 900801;

    private static SpellInfo Form(uint id, int form) => Spell(id, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.ModShapeshift, misc: form)) with
    {
        Attributes = (SpellAttributes)0x50010u,
        Duration = new SpellDuration(-1, 0, -1),
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private sealed class FixedRandom(int roll) : Random
    {
        public override int Next(int minValue, int maxValue) => roll;
    }

    private sealed class Rig : IDisposable
    {
        public Rig(Class playerClass, Race race = Race.NightElf)
        {
            Kit = new SpellTestKit(
                Form(CatForm, 1), Form(BearForm, 5), Form(WarriorBattle, 17) with { AttributesEx3 = 0x00100000 },
                Spell(FurorEnergy, Effect(SpellEffectName.Energize, 40, misc: (int)PowerType.Energy)) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 },
                Spell(FurorRage, Effect(SpellEffectName.Energize, 100, misc: (int)PowerType.Rage)) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 },
                Spell(Furor100, Effect(SpellEffectName.ApplyAura, 100, aura: AuraType.Dummy)) with
                {
                    Attributes = (SpellAttributes)0x1d0u, SpellIconId = 238, Duration = new SpellDuration(-1, 0, -1), StartRecoveryCategory = 0, StartRecoveryTime = 0,
                });
            var character = new CharacterRecord
            {
                Id = 7, AccountId = 1, Name = "Restored", Race = (byte)race, Class = (byte)playerClass, Gender = (byte)Gender.Female,
                Level = 60, MapId = 0, ZoneId = 12, Z = 83.5f,
            };
            var appearance = new PlayerAppearance(
                DisplayId: 2222, FactionTemplate: 4, playerClass == Class.Warrior ? PowerType.Rage : PowerType.Mana, BaseHealth: 60, BaseMana: 100,
                MaxHealth: 1000, MaxPower: 500, StartPower: 500, NextLevelXp: 400);
            Player = new Player(character, appearance, new FakeSession(1));
            Kit.World.AddPlayer(Player);
            Kit.World.RunTick(0);
            Service = new ShapeshiftService(Kit.System, ShapeshiftFormCatalog.Retail, new CombatOptions(), p => KnownSpells);
            Service.Install();
            Kit.System.Random = new FixedRandom(100);                         // every Furor roll of an applied form would succeed
        }

        public SpellTestKit Kit { get; }

        public Player Player { get; }

        public ShapeshiftService Service { get; }

        public List<uint> KnownSpells { get; } = [];

        public byte Form => Player.GetByte(UpdateFields.UnitFieldBytes1, 2);

        public uint Power(PowerType type) => MapCombat.GetPower(Player, type);

        /// <summary>The saved form aura as the aura store hands it back at login.</summary>
        public void Login(uint formSpell) => Kit.System.RestoreAuras(Player,
            [new PersistedAura { SpellId = formSpell, CasterGuid = Player.Guid, CasterLevel = 60, MaxDurationMs = -1, RemainingMs = -1, EffectMask = 1 }],
            1_800_000_000_000);

        public void Dispose() => Kit.Dispose();
    }

    [Fact]
    public void CatDruidWith60EnergySaved_LoggedIn_HasForm1_PowerEnergy_AndEnergy60()
    {
        using var rig = new Rig(Class.Druid);
        rig.KnownSpells.Add(Furor100);
        rig.Kit.System.CastLearnedPassive(rig.Player, Furor100);
        MapCombat.SetPower(rig.Player, PowerType.Energy, 60);                 // what PlayerLife.ApplyVitals wrote while loading

        rig.Login(CatForm);

        Assert.Equal(1, rig.Form);
        Assert.Equal(PowerType.Energy, rig.Player.PowerType);
        Assert.Equal(60u, rig.Power(PowerType.Energy));                       // no reset to 0, no Furor 40 on top
        Assert.Equal(100u, MapCombat.GetMaxPower(rig.Player, PowerType.Energy));
        Assert.Equal(892u, rig.Player.DisplayId);
    }

    [Fact]
    public void BearDruidWith150RageSaved_KeepsRage150()
    {
        using var rig = new Rig(Class.Druid);
        MapCombat.SetPower(rig.Player, PowerType.Rage, 150);

        rig.Login(BearForm);

        Assert.Equal(5, rig.Form);
        Assert.Equal(PowerType.Rage, rig.Player.PowerType);
        Assert.Equal(150u, rig.Power(PowerType.Rage));
    }

    [Fact]
    public void AnAppliedForm_StillResetsThePowerAndRollsFuror_OnlyRestoreSkipsIt()
    {
        using var rig = new Rig(Class.Druid);
        rig.KnownSpells.Add(Furor100);
        rig.Kit.System.CastLearnedPassive(rig.Player, Furor100);
        MapCombat.SetPower(rig.Player, PowerType.Energy, 60);

        rig.Kit.System.CastSpell(rig.Player, CatForm, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(40u, rig.Power(PowerType.Energy));                       // reset to 0, then Furor's 40
    }

    [Fact]
    public void WarriorWithMoreRageThanTacticalMasteryKeeps_LoggedInInBattleStance_KeepsItsSavedRage()
    {
        using var rig = new Rig(Class.Warrior, Race.Human);
        MapCombat.SetPower(rig.Player, PowerType.Rage, 400);

        rig.Login(WarriorBattle);

        Assert.Equal(17, rig.Form);
        Assert.Equal(400u, rig.Power(PowerType.Rage));                        // a stance cast would cut it to Tactical Mastery's rage
    }

    [Fact]
    public void SaveAndRestoreOfAHumanoidDruid_HasNoFormAndKeepsMana()
    {
        using var rig = new Rig(Class.Druid);

        rig.Kit.System.RestoreAuras(rig.Player, [], 1_800_000_000_000);

        Assert.Equal(0, rig.Form);
        Assert.Equal(PowerType.Mana, rig.Player.PowerType);
        Assert.Equal(500u, rig.Power(PowerType.Mana));
    }
}
