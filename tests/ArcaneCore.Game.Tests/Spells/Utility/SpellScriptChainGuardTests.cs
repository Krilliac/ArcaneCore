using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells.Utility;

/// <summary>
/// The spell script dispatcher wraps the effect handler installed when it is installed. A later <see cref="SpellSystem.RegisterEffect"/> that replaced
/// such a wrapper without reading it would silently drop every script hook of the effect, so it throws; a registration that reads the installed handler
/// first and calls it (the chaining the dungeon event and quest NPC features do) keeps the scripts.
/// </summary>
public sealed class SpellScriptChainGuardTests
{
    private const uint DummySpell = 962_001;
    private const uint HealSpell = 962_002;

    private readonly List<string> _log = [];

    [SpellScript(DummySpell)]
    private sealed class DummyScript(List<string> log) : ISpellScript
    {
        public void OnEffectExecute(SpellEffectContext context) => log.Add("script");
    }

    [SpellScript(HealSpell, ExecuteEffects = new[] { SpellEffectName.Heal })]
    private sealed class HealScript(List<string> log) : ISpellScript
    {
        public void OnEffectExecute(SpellEffectContext context) => log.Add("heal-script");
    }

    private SpellTestKit NewKit()
    {
        var kit = new SpellTestKit(
            SpellTestKit.Spell(DummySpell, SpellTestKit.Effect(SpellEffectName.Dummy, 1)) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 },
            SpellTestKit.Spell(HealSpell, SpellTestKit.Effect(SpellEffectName.Heal, 5)) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 });
        SpellScriptDispatcher.Install(kit.System, new SpellScriptRegistry([new DummyScript(_log), new HealScript(_log)]));
        return kit;
    }

    [Fact]
    public void ReplacingAScriptChainedEffectWithoutReadingIt_Throws_AndKeepsTheScripts()
    {
        using SpellTestKit kit = NewKit();
        (Player player, _) = kit.AddPlayer(1);

        InvalidOperationException dummy = Assert.Throws<InvalidOperationException>(
            () => kit.System.RegisterEffect(SpellEffectName.Dummy, _ => _log.Add("replacement")));
        Assert.Contains("Dummy", dummy.Message);
        Assert.Throws<InvalidOperationException>(() => kit.System.RegisterEffect(SpellEffectName.Heal, _ => _log.Add("replacement")));
        Assert.Throws<InvalidOperationException>(() => kit.System.RegisterEffect(SpellEffectName.Dispel, _ => _log.Add("replacement")));

        kit.System.CastSpell(player, DummySpell, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal(["script"], _log);
    }

    [Fact]
    public void ReplacingAScriptChainedEffectAfterReadingIt_IsAllowed_AndTheNewHandlerChainsTheScripts()
    {
        using SpellTestKit kit = NewKit();
        (Player player, _) = kit.AddPlayer(1);
        SpellEffectHandler previous = kit.System.GetEffectHandler(SpellEffectName.Dummy)!;

        kit.System.RegisterEffect(SpellEffectName.Dummy, context =>
        {
            _log.Add("feature");
            previous(context);
        });

        kit.System.CastSpell(player, DummySpell, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal(["feature", "script"], _log);
    }

    [Fact]
    public void AnEffectNoScriptChains_StaysReplaceable()
    {
        using SpellTestKit kit = NewKit();

        kit.System.RegisterEffect(SpellEffectName.SchoolDamage, _ => { });
        kit.System.RegisterEffect(SpellEffectName.Energize, _ => { });
    }
}
