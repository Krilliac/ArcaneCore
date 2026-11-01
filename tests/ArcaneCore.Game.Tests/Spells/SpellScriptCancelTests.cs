using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>The cancelling OnEffectExecute overload of <see cref="ISpellScript"/>.</summary>
public sealed class SpellScriptCancelTests
{
    private sealed class VoidOnly : ISpellScript
    {
        public int Calls { get; private set; }

        public void OnEffectExecute(SpellEffectContext context) => Calls++;
    }

    private sealed class Canceller : ISpellScript
    {
        public bool OnEffectExecute(SpellEffectContext context, bool runByDefault) => false;
    }

    [Fact]
    public void DefaultOverload_RunsTheVoidHookAndLetsTheEffectRun()
    {
        var script = new VoidOnly();

        Assert.True(((ISpellScript)script).OnEffectExecute(null!, true));
        Assert.Equal(1, script.Calls);
    }

    [Fact]
    public void AnOverridingScript_CanCancelTheEffect()
        => Assert.False(((ISpellScript)new Canceller()).OnEffectExecute(null!, true));
}
