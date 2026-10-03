using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Druid;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Druid;

/// <summary>
/// The script effect of spell 9033 (vmangos SpellEffects.cpp:4442-4480, Unit.cpp:543-556) and the registry behind
/// SPELL_EFFECT_SCRIPT_EFFECT. Mechanic ids are the vmangos Mechanics enum (SpellDefines.h:660-691): 5 fear, 7 root,
/// 11 snare, 12 stun, 3 disarm (a mechanic that is neither kept nor a snare, for the daze-like case).
/// </summary>
public sealed class ShapeshiftFormEffectTests : IDisposable
{
    private const uint Root = 900601;
    private const uint RootNoMechanic = 900602;
    private const uint Snare = 900603;
    private const uint SnareNoMechanic = 900604;
    private const uint SnareAndStun = 900605;
    private const uint DazeLike = 900606;
    private const uint DazeLikeWithSnare = 900607;
    private const uint Stun = 900608;
    private const uint Fear = 900609;
    private const uint OtherSnareWithDispel = 900610;
    private const uint FormEffect = ShapeshiftFormEffectRules.SpellId;

    private readonly SpellTestKit _kit;
    private readonly Player _player;

    public ShapeshiftFormEffectTests()
    {
        _kit = new SpellTestKit(
            Debuff(Root, AuraType.ModRoot, mechanic: 7),
            Debuff(RootNoMechanic, AuraType.ModRoot, mechanic: 0),
            Debuff(Snare, AuraType.ModDecreaseSpeed, mechanic: 11, value: -50),
            Debuff(SnareNoMechanic, AuraType.ModDecreaseSpeed, mechanic: 0, value: -50),
            Debuff(SnareAndStun, AuraType.ModDecreaseSpeed, mechanic: 11, value: -50) with
            {
                Effects = [Effect(SpellEffectName.ApplyAura, -50, aura: AuraType.ModDecreaseSpeed) with { Mechanic = 11 }, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ModStun) with { Mechanic = 12 }],
            },
            Debuff(DazeLike, AuraType.ModDecreaseSpeed, mechanic: 3, value: -50) with { SpellIconId = 15, Dispel = 0 },
            Debuff(DazeLikeWithSnare, AuraType.ModDecreaseSpeed, mechanic: 11, value: -50) with { SpellIconId = 15, Dispel = 0 },
            Debuff(Stun, AuraType.ModStun, mechanic: 12),
            Debuff(Fear, AuraType.ModFear, mechanic: 5),
            Debuff(OtherSnareWithDispel, AuraType.ModDecreaseSpeed, mechanic: 3, value: -50) with { SpellIconId = 15, Dispel = 1 },
            Spell(FormEffect, Effect(SpellEffectName.ScriptEffect, 0)) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 });
        (_player, _) = _kit.AddPlayer(1);
    }

    public void Dispose() => _kit.Dispose();

    private static SpellInfo Debuff(uint id, AuraType aura, uint mechanic, int value = 0)
        => Spell(id, Effect(SpellEffectName.ApplyAura, value, aura: aura)) with
        {
            Mechanic = mechanic,
            Duration = new SpellDuration(30000, 0, 30000),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
            SpellVisual = 1,
        };

    private bool Has(uint spell) => _kit.System.GetAuras(_player).Any(h => h.Spell.Id == spell && !h.IsRemoved);

    private void Apply(params uint[] spells)
    {
        foreach (uint spell in spells)
        {
            Assert.Equal(SpellCastResult.CastOk, _kit.System.CastSpell(_player, spell, SpellCastTargets.ForSelf(), triggered: true));
            Assert.True(Has(spell), $"setup: {spell} applied");
        }
    }

    [Fact]
    public void Casting9033_RemovesARootWithAMechanic_ButNotOneWithoutAny()
    {
        Apply(Root, RootNoMechanic);

        Assert.Equal(SpellCastResult.CastOk, _kit.System.CastSpell(_player, FormEffect, SpellCastTargets.ForSelf(), triggered: true));

        Assert.False(Has(Root));
        Assert.True(Has(RootNoMechanic));       // "aurMechMask == 0": not a shapeshift-removable spell
    }

    [Fact]
    public void Casting9033_RemovesASnare_ButKeepsCrowdControlDazeAndMechanicLessSlows()
    {
        Apply(Snare, SnareNoMechanic, SnareAndStun, DazeLike, DazeLikeWithSnare, OtherSnareWithDispel, Stun, Fear);

        Assert.Equal(SpellCastResult.CastOk, _kit.System.CastSpell(_player, FormEffect, SpellCastTargets.ForSelf(), triggered: true));

        Assert.False(Has(Snare));                // mechanic snare: removed
        Assert.False(Has(DazeLikeWithSnare));    // icon 15 but it carries the snare bit: removed
        Assert.True(Has(SnareNoMechanic));       // mask 0: kept
        Assert.True(Has(SnareAndStun));          // a stun mechanic on the same spell: crowd control, kept
        Assert.True(Has(DazeLike));              // icon 15, dispel 0, no snare mechanic: a daze, kept
        Assert.False(Has(OtherSnareWithDispel)); // icon 15 but dispel 1: not the daze shape, removed
    }

    [Fact]
    public void Casting9033_NeverTouchesStunsOrFears()
    {
        Apply(Stun, Fear);

        _kit.System.CastSpell(_player, FormEffect, SpellCastTargets.ForSelf(), triggered: true);

        Assert.True(Has(Stun));
        Assert.True(Has(Fear));
    }

    [Fact]
    public void TheRegistry_RejectsADuplicateSpellId_AndIsSharedPerSystem()
    {
        ScriptEffectRegistry registry = ScriptEffectRegistry.For(_kit.System);

        Assert.Same(registry, ScriptEffectRegistry.For(_kit.System));
        Assert.Contains(FormEffect, registry.SpellIds);
        Assert.Throws<InvalidOperationException>(() => registry.Add(FormEffect, _ => { }));
        Assert.DoesNotContain(900999u, registry.SpellIds);
        registry.Add(900999, _ => { });
        Assert.Contains(900999u, registry.SpellIds);
    }

    [Fact]
    public void ASecondModuleClaimingScriptEffect_FailsStartup()
        => Assert.Throws<InvalidOperationException>(() => _kit.System.RegisterModules([typeof(RivalScriptEffectModule)]));

    [Fact]
    public void AScriptEffectOfASpellWithoutAnEntry_DoesNothing()
    {
        var unscripted = Spell(900700, Effect(SpellEffectName.ScriptEffect, 0)) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 };
        using var kit = new SpellTestKit(unscripted, Debuff(Root, AuraType.ModRoot, mechanic: 7));
        (Player player, _) = kit.AddPlayer(1);
        kit.System.CastSpell(player, Root, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 900700, SpellCastTargets.ForSelf(), triggered: true));

        Assert.Contains(kit.System.GetAuras(player), h => h.Spell.Id == Root);
    }
}

/// <summary>A second owner of effect 77: the module rule must refuse it.</summary>
internal sealed class RivalScriptEffectModule : ISpellHandlerModule
{
    public void Register(SpellSystem system) => system.RegisterEffect(SpellEffectName.ScriptEffect, _ => { });
}
