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
/// The forms that are not druid forms but share the form byte: Ghost Wolf (16), Shadowform (28) and Stealth (30), handled by
/// the same HandleAuraModShapeshift (vmangos SpellAuras.cpp:2420-2625) with the real 1.12.1 form table (flags1: Ghost Wolf 0,
/// Shadowform 8, Stealth 1). Spell values are classic-db: Stealth 1784 (aura 36 misc 30, plus Mod Stealth), Ambush 8676 /
/// Cheap Shot 1833 (Stances 0x20000000 = form 30), Ghost Wolf 2645 (Attributes 0x18000), Shadowform 15473 (Attributes
/// 0x2050000), Renew 139 (StancesNot 0x08000000 = form 28).
/// </summary>
public sealed class FormStealthShadowGhostWolfTests
{
    private const uint Stealth = 1784;
    private const uint Ambush = 8676;
    private const uint GhostWolf = 2645;
    private const uint Shadowform = 15473;
    private const uint Renew = 139;
    private const uint Fireball = 133;     // an ordinary spell: Attributes 0, no Stances

    private static SpellInfo FormSpell(uint id, int form, SpellAttributes attributes, SpellAttributesEx2 ex2 = 0) => Spell(id, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.ModShapeshift, misc: form)) with
    {
        Attributes = attributes,
        AttributesEx2 = ex2,
        Duration = new SpellDuration(-1, 0, -1),
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static IEnumerable<SpellInfo> Spells()
    {
        yield return FormSpell(Stealth, 30, 0);
        yield return FormSpell(GhostWolf, 16, (SpellAttributes)0x18000u, (SpellAttributesEx2)2);
        yield return FormSpell(Shadowform, 28, (SpellAttributes)0x2050000u, (SpellAttributesEx2)2);
        yield return Spell(Ambush, Effect(SpellEffectName.ApplyAura, 3, aura: AuraType.ModCritPercent)) with
        {
            Stances = 0x20000000,
            Duration = new SpellDuration(5000, 0, 5000),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };
        yield return Spell(Renew, Effect(SpellEffectName.ApplyAura, 3, aura: AuraType.ModCritPercent)) with
        {
            StancesNot = 0x08000000,
            Duration = new SpellDuration(5000, 0, 5000),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };
        yield return Spell(Fireball, Effect(SpellEffectName.ApplyAura, 3, aura: AuraType.ModCritPercent)) with
        {
            Attributes = (SpellAttributes)0x10000u,    // NOT_SHAPESHIFT, as 4,956 classic-db spells
            Duration = new SpellDuration(5000, 0, 5000),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };
    }

    private sealed class Rig : IDisposable
    {
        public Rig(Class playerClass, PowerType power)
        {
            Kit = new SpellTestKit([.. Spells()]);
            var character = new CharacterRecord
            {
                Id = 7, AccountId = 1, Name = "Shifter", Race = (byte)Race.Human, Class = (byte)playerClass, Gender = (byte)Gender.Female,
                Level = 60, MapId = 0, ZoneId = 12, Z = 83.5f,
            };
            var appearance = new PlayerAppearance(
                DisplayId: 2222, FactionTemplate: 1, power, BaseHealth: 60, BaseMana: 100,
                MaxHealth: 1000, MaxPower: 500, StartPower: 100, NextLevelXp: 400);
            Player = new Player(character, appearance, new FakeSession(1));
            Kit.World.AddPlayer(Player);
            Kit.World.RunTick(0);
            new ShapeshiftService(Kit.System, ShapeshiftFormCatalog.Retail, new CombatOptions(), _ => []).Install();
        }

        public SpellTestKit Kit { get; }

        public Player Player { get; }

        public SpellSystem System => Kit.System;

        public SpellCastResult Cast(uint spell)
        {
            Kit.Advance(1500);
            return System.CastSpell(Player, spell, SpellCastTargets.ForSelf(), triggered: false);
        }

        public bool Has(uint spell) => System.HasAura(Player, spell);

        public byte Form => Player.GetByte(UpdateFields.UnitFieldBytes1, 2);

        public void Dispose() => Kit.Dispose();
    }

    [Fact]
    public void RogueStealth_SetsForm30_KeepsEnergy_AndMakesTheStealthFormSpellsCastable()
    {
        using var rig = new Rig(Class.Rogue, PowerType.Energy);
        Assert.Equal(SpellCastResult.OnlyShapeshift, rig.Cast(Ambush));       // before: the form byte is 0

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(Stealth));

        Assert.Equal(30, rig.Form);
        Assert.Equal(PowerType.Energy, rig.Player.PowerType);
        Assert.Equal(SpellCastResult.CastOk, rig.Cast(Ambush));               // Stances bit 29 = form 30
    }

    [Fact]
    public void LeavingStealth_ClearsTheForm_AndAmbushIsBlockedAgain()
    {
        using var rig = new Rig(Class.Rogue, PowerType.Energy);
        rig.Cast(Stealth);

        rig.System.CancelAura(rig.Player, Stealth);

        Assert.Equal(0, rig.Form);
        Assert.Equal(PowerType.Energy, rig.Player.PowerType);
        Assert.Equal(SpellCastResult.OnlyShapeshift, rig.Cast(Ambush));
    }

    [Fact]
    public void StealthIsAStance_SoOrdinarySpellsStayCastable()
    {
        using var rig = new Rig(Class.Rogue, PowerType.Energy);
        rig.Cast(Stealth);

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(Fireball));             // flags1 Stance: not "shapeshifted", NOT_SHAPESHIFT does not bite
    }

    [Fact]
    public void Shadowform_SetsForm28_BlocksStancesNotSpells_AndNotShapeshiftSpells_UntilCancelled()
    {
        using var rig = new Rig(Class.Priest, PowerType.Mana);

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(Shadowform));

        Assert.Equal(28, rig.Form);
        Assert.Equal(PowerType.Mana, rig.Player.PowerType);
        Assert.Equal(SpellCastResult.NotShapeshift, rig.Cast(Renew));         // StancesNot bit 28 (Holy spells)
        Assert.Equal(SpellCastResult.NotShapeshift, rig.Cast(Fireball));      // flags1 0x8: acts as shifted, NOT_SHAPESHIFT
        Assert.Equal(rig.Player.NativeDisplayId, rig.Player.DisplayId);       // no model change

        rig.System.CancelAura(rig.Player, Shadowform);

        Assert.Equal(0, rig.Form);
        Assert.Equal(SpellCastResult.CastOk, rig.Cast(Renew));
        Assert.Equal(SpellCastResult.CastOk, rig.Cast(Fireball));
    }

    [Fact]
    public void GhostWolf_SetsForm16_Display4613_Scale0_8_AndLeavingRestoresThem()
    {
        using var rig = new Rig(Class.Shaman, PowerType.Mana);
        uint native = rig.Player.NativeDisplayId;

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(GhostWolf));

        Assert.Equal(16, rig.Form);
        Assert.Equal(4613u, rig.Player.DisplayId);
        Assert.Equal(0.8f, rig.Player.GetFloat(UpdateFields.ObjectFieldScaleX), 4);
        Assert.Equal(SpellCastResult.NotShapeshift, rig.Cast(Fireball));      // a normal cast is blocked in the wolf

        rig.System.CancelAura(rig.Player, GhostWolf);

        Assert.Equal(0, rig.Form);
        Assert.Equal(native, rig.Player.DisplayId);
        Assert.Equal(1.0f, rig.Player.GetFloat(UpdateFields.ObjectFieldScaleX), 4);
        Assert.Equal(SpellCastResult.CastOk, rig.Cast(Fireball));
    }

    [Fact]
    public void NoneOfThemCastsTheShapeshiftFormEffect()
    {
        foreach (ShapeshiftForm form in new[] { ShapeshiftForm.GhostWolf, ShapeshiftForm.Shadow, ShapeshiftForm.Stealth })
        {
            Assert.False(ShapeshiftService.CastsShapeshiftFormEffect(form));
        }
    }
}
