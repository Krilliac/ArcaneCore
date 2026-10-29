using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// A hot spell handler module that installs an aura handler: unloading it must revert what its live auras did. Aura removal looks the handler up when it
/// runs (<c>ApplyAuraModifier</c>), so a handler taken out first would leave the stat change behind for good; <see cref="SpellSystem.CommitModuleSwap"/>
/// removes the live auras of the module's types while the handler is still installed.
/// </summary>
public sealed class SpellHandlerModuleUnloadTests
{
    private const uint ModuleSpell = 962201;
    private const uint OtherSpell = 962202;
    private const uint Bonus = 500;

    /// <summary>HandleUnused in vmangos (ModParrySkill, SpellAuras.cpp:111); the server has no handler for it, so a module may claim it.</summary>
    private const AuraType ModuleAura = AuraType.ModParrySkill;

    public sealed class StatModule : ISpellHandlerModule
    {
        public void Register(SpellSystem system) => system.RegisterAura(ModuleAura, new AuraHandler(static (_, holder, _, apply) =>
        {
            holder.Target.MaxHealth = apply ? holder.Target.MaxHealth + Bonus : holder.Target.MaxHealth - Bonus;
        }, null));
    }

    private static SpellInfo Buff(uint id, AuraType aura) =>
        Spell(id, Effect(SpellEffectName.ApplyAura, 5, SpellImplicitTarget.UnitCaster, aura)) with
        {
            Duration = new SpellDuration(60_000, 0, 60_000),
            SpellVisual = 1,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };

    private static SpellTestKit Kit() => new(Buff(ModuleSpell, ModuleAura), Buff(OtherSpell, AuraType.ModResistance));

    [Fact]
    public void UnloadingTheModule_RevertsTheStatItsLiveAuraChanged_AndRemovesTheAura()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        player.MaxHealth = 1_000;
        kit.System.RegisterModules([typeof(StatModule)]);
        kit.System.CastSpell(player, ModuleSpell, SpellCastTargets.ForSelf(), triggered: true);
        Assert.True(kit.System.HasAura(player, ModuleSpell));
        Assert.Equal(1_000 + Bonus, player.MaxHealth);

        kit.System.CommitModuleSwap([typeof(StatModule)], []);

        Assert.Equal(1_000u, player.MaxHealth);
        Assert.False(kit.System.HasAura(player, ModuleSpell));
        Assert.False(kit.System.HasAuraHandler(ModuleAura));
    }

    [Fact]
    public void UnloadingTheModule_LeavesTheAurasOfOtherTypesAlone()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        kit.System.RegisterModules([typeof(StatModule)]);
        kit.System.CastSpell(player, OtherSpell, SpellCastTargets.ForSelf(), triggered: true);

        kit.System.CommitModuleSwap([typeof(StatModule)], []);

        Assert.True(kit.System.HasAura(player, OtherSpell));
    }
}
