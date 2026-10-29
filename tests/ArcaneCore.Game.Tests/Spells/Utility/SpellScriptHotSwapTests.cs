using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Kernel.Diagnostics;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells.Utility;

/// <summary>
/// Hot swap of spell scripts (<see cref="SpellScriptRegistry.ValidateSwap"/> / <see cref="SpellScriptRegistry.CommitSwap"/>): all-or-nothing,
/// built-in ids cannot be claimed, a module's own scripts are replaced, and execute effects follow the live scripts through the dispatcher.
/// </summary>
public sealed class SpellScriptHotSwapTests
{
    private const uint BuiltInSpell = 961_001;
    private const uint HotSpell = 961_002;
    private const uint HealSpell = 961_003;
    private const uint SummonDemonSpell = 961_004;
    private const uint TeleportSpell = 961_005;
    private const uint SummonSpell = 961_006;

    private readonly List<string> _log = [];

    private static SpellInfo Instant(uint id, params SpellEffectInfo[] effects)
        => SpellTestKit.Spell(id, effects) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 };

    private static SpellTestKit Kit() => new(
        Instant(BuiltInSpell, SpellTestKit.Effect(SpellEffectName.Dummy, 1)),
        Instant(HotSpell, SpellTestKit.Effect(SpellEffectName.Dummy, 2)),
        Instant(HealSpell, SpellTestKit.Effect(SpellEffectName.Heal, 20)),
        Instant(SummonDemonSpell, SpellTestKit.Effect(SpellEffectName.SummonDemon, 1)));

    [SpellScript(BuiltInSpell)]
    private sealed class BuiltInScript(List<string> log) : ISpellScript
    {
        public void OnEffectExecute(SpellEffectContext context) => log.Add("built-in");
    }

    [SpellScript(HotSpell)]
    private sealed class HotScriptV1(List<string> log) : ISpellScript
    {
        public void OnEffectExecute(SpellEffectContext context) => log.Add("hot-v1");
    }

    [SpellScript(HotSpell)]
    private sealed class HotScriptV2(List<string> log) : ISpellScript
    {
        public void OnEffectExecute(SpellEffectContext context) => log.Add("hot-v2");
    }

    [SpellScript(BuiltInSpell, HotSpell)]
    private sealed class BuiltInClaimer : ISpellScript;

    [SpellScript(HealSpell, ExecuteEffects = new[] { SpellEffectName.Heal })]
    private sealed class HealScript(List<string> log) : ISpellScript
    {
        public void OnEffectExecute(SpellEffectContext context) => log.Add($"before-heal:{context.Target.Health}");
    }

    [SpellScript(SummonDemonSpell, ExecuteEffects = new[] { SpellEffectName.SummonDemon })]
    private sealed class UnhandledEffectScript : ISpellScript;

    private sealed class UnmarkedScript : ISpellScript;

    private void CastDummies(SpellTestKit kit, Player player)
    {
        kit.System.CastSpell(player, BuiltInSpell, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(player, HotSpell, SpellCastTargets.ForSelf(), triggered: true);
    }

    [Fact]
    public void AddReplaceRemove_TakeEffectOnTheNextCast()
    {
        using SpellTestKit kit = Kit();
        var registry = new SpellScriptRegistry([new BuiltInScript(_log)]);
        SpellScriptDispatcher.Install(kit.System, registry);
        (Player player, _) = kit.AddPlayer(1);

        var v1 = new HotScriptV1(_log);
        Assert.Null(registry.ValidateSwap([], [v1]));
        registry.CommitSwap([], [v1]);
        CastDummies(kit, player);
        Assert.Equal(["built-in", "hot-v1"], _log);
        Assert.Same(v1, Assert.Single(registry.HotScripts));

        _log.Clear();
        var v2 = new HotScriptV2(_log);
        Assert.Null(registry.ValidateSwap([v1], [v2]));
        registry.CommitSwap([v1], [v2]);
        CastDummies(kit, player);
        Assert.Equal(["built-in", "hot-v2"], _log);
        Assert.Same(v2, Assert.Single(registry.HotScripts));

        _log.Clear();
        registry.CommitSwap([v2], []);
        CastDummies(kit, player);
        Assert.Equal(["built-in"], _log);
        Assert.Empty(registry.HotScripts);
        Assert.Null(registry.Find(HotSpell));
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void AModuleScriptClaimingABuiltInId_IsRejected_AndNothingChanges()
    {
        var builtIn = new BuiltInScript(_log);
        var registry = new SpellScriptRegistry([builtIn]);
        var v1 = new HotScriptV1(_log);
        registry.CommitSwap([], [v1]);

        // BuiltInClaimer also claims HotSpell, which v1 (being removed) frees; only the built-in id makes the swap fail.
        string? error = registry.ValidateSwap([v1], [new BuiltInClaimer()]);

        Assert.NotNull(error);
        Assert.Contains(BuiltInSpell.ToString(), error!, StringComparison.Ordinal);
        Assert.Contains(nameof(BuiltInClaimer), error, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => registry.CommitSwap([v1], [new BuiltInClaimer()]));
        Assert.Same(builtIn, registry.Find(BuiltInSpell));
        Assert.Same(v1, registry.Find(HotSpell)); // the removal half of the refused swap did not happen
        Assert.True(registry.IsBuiltIn(BuiltInSpell));
        Assert.False(registry.IsBuiltIn(HotSpell));
    }

    [Fact]
    public void ABuiltInScript_CannotBeRemoved()
    {
        var builtIn = new BuiltInScript(_log);
        var registry = new SpellScriptRegistry([builtIn]);

        string? error = registry.ValidateSwap([builtIn], []);

        Assert.NotNull(error);
        Assert.Contains("built in", error!, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => registry.CommitSwap([builtIn], []));
        Assert.Same(builtIn, registry.Find(BuiltInSpell));
    }

    [Fact]
    public void AnAddedScriptClashingWithAHotScriptNotRemoved_IsRejected()
    {
        var registry = new SpellScriptRegistry([]);
        var v1 = new HotScriptV1(_log);
        registry.CommitSwap([], [v1]);

        string? error = registry.ValidateSwap([], [new HotScriptV2(_log)]);

        Assert.NotNull(error);
        Assert.Contains(nameof(HotScriptV1), error!, StringComparison.Ordinal);
        Assert.Contains(nameof(HotScriptV2), error, StringComparison.Ordinal);
        Assert.Same(v1, registry.Find(HotSpell));
    }

    [Fact]
    public void TwoAddedScriptsClaimingOneId_AreRejectedTogether()
    {
        var registry = new SpellScriptRegistry([]);

        string? error = registry.ValidateSwap([], [new HotScriptV1(_log), new HotScriptV2(_log)]);

        Assert.NotNull(error);
        Assert.Throws<InvalidOperationException>(() => registry.CommitSwap([], [new HotScriptV1(_log), new HotScriptV2(_log)]));
        Assert.Equal(0, registry.Count);
        Assert.Empty(registry.HotScripts);
    }

    [Fact]
    public void BadSwaps_AreRejectedWithAReason()
    {
        var registry = new SpellScriptRegistry([]);
        var v1 = new HotScriptV1(_log);

        Assert.Contains("not registered", registry.ValidateSwap([v1], [])!, StringComparison.Ordinal);
        Assert.Contains(nameof(SpellScriptAttribute), registry.ValidateSwap([], [new UnmarkedScript()])!, StringComparison.Ordinal);
        Assert.Contains("already registered", registry.ValidateSwap([], [v1, v1])!, StringComparison.Ordinal);
        registry.CommitSwap([], [v1]);
        Assert.Contains("already registered", registry.ValidateSwap([], [v1])!, StringComparison.Ordinal);
        Assert.Null(registry.ValidateSwap([v1], [v1])); // removing and re-adding one instance is a no-op swap, not a clash
    }

    [Fact]
    public void AnExecuteEffectDeclaredByAnAddedScript_IsChainedAfterInstall_AndStopsAfterRemoval()
    {
        using SpellTestKit kit = Kit();
        var registry = new SpellScriptRegistry([]);
        SpellScriptDispatcher.Install(kit.System, registry);
        (Player player, _) = kit.AddPlayer(1);
        player.MaxHealth = 100;
        player.Health = 50;
        Assert.Empty(registry.ExecuteEffects);

        var heal = new HealScript(_log);
        registry.CommitSwap([], [heal]);
        Assert.Equal(SpellEffectName.Heal, Assert.Single(registry.ExecuteEffects));
        kit.System.CastSpell(player, HealSpell, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(["before-heal:50"], _log); // the script ran first, then the built-in heal
        Assert.Equal(70u, player.Health);

        _log.Clear();
        registry.CommitSwap([heal], []);
        Assert.Empty(registry.ExecuteEffects);
        kit.System.CastSpell(player, HealSpell, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Empty(_log);
        Assert.Equal(90u, player.Health); // the wrapped built-in heal still runs

        // Re-adding chains nothing twice: the script runs exactly once per effect.
        registry.CommitSwap([], [heal]);
        player.Health = 10;
        kit.System.CastSpell(player, HealSpell, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal(["before-heal:10"], _log);
        Assert.Equal(30u, player.Health);
    }

    [Fact]
    public void AModuleDeclaringTeleportUnitsOrSummon_IsNotBuiltInWhileLoaded_AndIsBuiltInAgainAfterUnload()
    {
        using SpellTestKit kit = Kit();
        var registry = new SpellScriptRegistry([]);
        SpellScriptDispatcher.Install(kit.System, registry);
        Assert.True(kit.System.HasBuiltInEffectHandler(SpellEffectName.TeleportUnits));
        Assert.True(kit.System.HasBuiltInEffectHandler(SpellEffectName.Summon));

        var teleport = new TeleportScript();
        var summon = new SummonScript();
        registry.CommitSwap([], [teleport, summon]);
        Assert.False(kit.System.HasBuiltInEffectHandler(SpellEffectName.TeleportUnits)); // the module's code runs before the built-in one
        Assert.False(kit.System.HasBuiltInEffectHandler(SpellEffectName.Summon));

        registry.CommitSwap([teleport, summon], []);
        Assert.True(kit.System.HasBuiltInEffectHandler(SpellEffectName.TeleportUnits)); // as after a restart without the module
        Assert.True(kit.System.HasBuiltInEffectHandler(SpellEffectName.Summon));

        // Replacing the chained wrapper blind is refused (SpellScriptChainGuardTests); replacing it after reading it is still a replacement.
        Assert.Throws<InvalidOperationException>(() => kit.System.RegisterEffect(SpellEffectName.TeleportUnits, static _ => { }));
        Assert.True(kit.System.HasBuiltInEffectHandler(SpellEffectName.TeleportUnits));
        _ = kit.System.GetEffectHandler(SpellEffectName.TeleportUnits);
        kit.System.RegisterEffect(SpellEffectName.TeleportUnits, static _ => { });
        Assert.False(kit.System.HasBuiltInEffectHandler(SpellEffectName.TeleportUnits));
    }

    [SpellScript(TeleportSpell, ExecuteEffects = [SpellEffectName.TeleportUnits])]
    private sealed class TeleportScript : ISpellScript;

    [SpellScript(SummonSpell, ExecuteEffects = [SpellEffectName.Summon])]
    private sealed class SummonScript : ISpellScript;

    [Fact]
    public void AnAddedScriptDeclaringAnEffectTheWorldDoesNotHandle_DoesNotChainIt()
    {
        using SpellTestKit kit = Kit();
        var registry = new SpellScriptRegistry([]);
        SpellScriptDispatcher.Install(kit.System, registry);
        Assert.False(kit.System.HasEffectHandler(SpellEffectName.SummonDemon));

        registry.CommitSwap([], [new UnhandledEffectScript()]);

        Assert.Contains(SpellEffectName.SummonDemon, registry.ExecuteEffects);
        Assert.False(kit.System.HasEffectHandler(SpellEffectName.SummonDemon));
    }

    [Fact]
    public void ScriptsChanged_IsRaisedOnCommitOnly_AndAThrowingHandlerUndoesTheSwapAndFailsIt()
    {
        var registry = new SpellScriptRegistry([]);
        var kept = new HotScriptV1(_log);
        registry.CommitSwap([], [kept]);
        int raised = 0;
        registry.ScriptsChanged += () => throw new InvalidOperationException("handler fault");
        registry.ScriptsChanged += () => raised++;

        Assert.Null(registry.ValidateSwap([kept], [new HotScriptV2(_log)]));
        Assert.Equal(0, raised);

        var v2 = new HotScriptV2(_log);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => registry.CommitSwap([kept], [v2]));

        Assert.Contains("handler fault", error.Message, StringComparison.Ordinal);
        Assert.Contains("undone", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, raised); // every handler ran on the commit, then again on the undo
        Assert.Same(kept, registry.Find(HotSpell));
        Assert.Same(kept, Assert.Single(registry.HotScripts));
        Assert.Contains(FaultLedger.Shared.Snapshot(), s => s.Site == "spell.script" && s.ExceptionType == typeof(InvalidOperationException).FullName);
    }

    [Fact]
    public void Restore_PutsBackExactlyTheSavedState_AndRaisesScriptsChanged()
    {
        var registry = new SpellScriptRegistry([new BuiltInScript(_log)]);
        var v1 = new HotScriptV1(_log);
        registry.CommitSwap([], [v1]);
        SpellScriptRegistry.Checkpoint saved = registry.Save();
        int raised = 0;
        registry.ScriptsChanged += () => raised++;

        var heal = new HealScript(_log);
        registry.CommitSwap([v1], [new HotScriptV2(_log), heal]);
        Assert.Contains(SpellEffectName.Heal, registry.ExecuteEffects);

        registry.Restore(saved);

        Assert.Equal(2, raised);
        Assert.Same(v1, registry.Find(HotSpell));
        Assert.Same(v1, Assert.Single(registry.HotScripts));
        Assert.Null(registry.Find(HealSpell));
        Assert.DoesNotContain(SpellEffectName.Heal, registry.ExecuteEffects);
        Assert.True(registry.IsBuiltIn(BuiltInSpell));
        Assert.Throws<ArgumentException>(() => new SpellScriptRegistry([]).Restore(saved));
    }

    [Fact]
    public void DiscoverOverManyAssemblies_ReadsEachAssemblyOnce()
    {
        System.Reflection.Assembly game = typeof(SpellScriptRegistry).Assembly;

        SpellScriptRegistry single = SpellScriptRegistry.Discover(game);
        SpellScriptRegistry many = SpellScriptRegistry.Discover([game, game]);

        Assert.True(single.Count > 0);
        Assert.Equal(single.Count, many.Count);
        Assert.Equal(single.SpellIds.Order(), many.SpellIds.Order());
        Assert.Equal(single.ExecuteEffects.Order(), many.ExecuteEffects.Order());
        Assert.All(many.SpellIds, id => Assert.True(many.IsBuiltIn(id)));
    }
}
