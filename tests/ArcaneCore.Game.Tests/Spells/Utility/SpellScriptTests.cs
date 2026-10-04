using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells.Utility;

/// <summary>
/// The spell script dispatcher (vmangos SpellScript hooks: Spell.cpp:6480 OnCheckCast last, :3716-3724 OnCast after TakePower,
/// :5254-5257 OnEffectExecute, scripts/spells/spell_*.cpp for the id-keyed classes).
/// </summary>
public sealed class SpellScriptTests
{
    private const uint DummySpell = 960_001;
    private const uint CostSpell = 960_002;
    private const uint TeleportShape = 960_003;
    private const uint DispelSpell = 960_004;
    private const uint MagicDebuff = 960_005;
    private const uint UnclaimedDummy = 960_006;
    private const uint HealSpell = 960_007;
    private const uint SummonDemonSpell = 960_008;

    internal static readonly List<string> Log = [];

    private static SpellInfo Instant(uint id, params SpellEffectInfo[] effects)
        => SpellTestKit.Spell(id, effects) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 };

    private static SpellTestKit Kit() => new(
        Instant(DummySpell, SpellTestKit.Effect(SpellEffectName.Dummy, 5)),
        Instant(UnclaimedDummy, SpellTestKit.Effect(SpellEffectName.Dummy, 5)),
        Instant(HealSpell, SpellTestKit.Effect(SpellEffectName.Heal, 20)),
        Instant(SummonDemonSpell, SpellTestKit.Effect(SpellEffectName.SummonDemon, 1)),
        Instant(CostSpell, SpellTestKit.Effect(SpellEffectName.Dummy, 1)) with { PowerType = (int)PowerType.Rage, ManaCost = 10 },
        // The mage Teleport shape: a TELEPORT_UNITS-less first effect and a SCRIPT_EFFECT tail (spell_template carries one on all six ranks).
        Instant(TeleportShape, SpellTestKit.Effect(SpellEffectName.ScriptEffect, 0)),
        Instant(DispelSpell, SpellTestKit.Effect(SpellEffectName.Dispel, 1, SpellImplicitTarget.UnitCaster, misc: 1)),
        Instant(MagicDebuff, SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
        {
            Dispel = 1,
            Duration = new SpellDuration(60_000, 0, 60_000),
            SpellVisual = 1,
            Attributes = SpellAttributes.AuraIsDebuff,
        });

    private static SpellScriptDispatcher Install(SpellTestKit kit, params ISpellScript[] scripts)
        => SpellScriptDispatcher.Install(kit.System, new SpellScriptRegistry(scripts));

    [SpellScript(DummySpell)]
    private sealed class DummyScript : ISpellScript
    {
        public void OnEffectExecute(SpellEffectContext context) => Log.Add($"effect:{context.Value}");
    }

    [SpellScript(HealSpell, ExecuteEffects = new[] { SpellEffectName.Heal })]
    private sealed class HealScript : ISpellScript
    {
        public void OnEffectExecute(SpellEffectContext context) => Log.Add($"before-heal:{context.Target.Health}");
    }

    [SpellScript(SummonDemonSpell, ExecuteEffects = new[] { SpellEffectName.SummonDemon })]
    private sealed class UnhandledEffectScript : ISpellScript;

    [SpellScript(CostSpell)]
    private sealed class CostScript : ISpellScript
    {
        public SpellCastResult OnCheckCast(in SpellCastCheckContext context)
        {
            Log.Add("script-check");
            return Veto ? SpellCastResult.NotHere : SpellCastResult.CastOk;
        }

        public void OnCast(SpellCast cast) => Log.Add($"cast:rage={SpellSystem.GetPower(cast.Caster, PowerType.Rage)}");

        public void OnEffectExecute(SpellEffectContext context) => Log.Add($"effect:rage={SpellSystem.GetPower(context.Caster, PowerType.Rage)}");

        public static bool Veto { get; set; }
    }

    [SpellScript(DispelSpell)]
    private sealed class DispelScript : ISpellScript
    {
        public void OnSuccessfulDispel(SpellEffectContext context, int removedStacks) => Log.Add($"dispelled:{removedStacks}");
    }

    private sealed class LateCheck : ISpellCastCheck
    {
        public SpellCheckPhase Phase => SpellCheckPhase.Final;

        public int Order => 1000;

        public SpellCastResult Check(in SpellCastCheckContext context)
        {
            if (context.Spell.Id == CostSpell)
            {
                Log.Add("late-check");
            }

            return SpellCastResult.CastOk;
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add($"{logLevel}: {formatter(state, exception)}");
    }

    private static void Reset()
    {
        Log.Clear();
        CostScript.Veto = false;
    }

    [Fact]
    public void DummyEffect_RunsTheScriptOfTheSpell_AndOnlyThatSpell()
    {
        Reset();
        using SpellTestKit kit = Kit();
        Install(kit, new DummyScript());
        (Player player, _) = kit.AddPlayer(1);

        kit.System.CastSpell(player, DummySpell, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(player, UnclaimedDummy, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(["effect:5"], Log);
    }

    [Fact]
    public void WithoutTheDispatcher_NothingRuns()
    {
        Reset();
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);

        kit.System.CastSpell(player, DummySpell, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Empty(Log);
    }

    [Fact]
    public void OnCheckCast_RunsAfterEveryOtherCheck_AndItsVetoCancelsTheCastWithoutCost()
    {
        Reset();
        using SpellTestKit kit = Kit();
        Install(kit, new CostScript());
        kit.System.RegisterCastCheck(new LateCheck()); // registered after the dispatcher, same phase, lower order
        (Player player, _) = kit.AddPlayer(1);
        kit.Spellbook.Teach(player, CostSpell);
        SpellSystem.SetPower(player, PowerType.Rage, 100);
        CostScript.Veto = true;

        SpellCastResult result = kit.System.HandleCastRequest(player, CostSpell, SpellCastTargets.ForSelf());

        Assert.Equal(SpellCastResult.NotHere, result);
        Assert.Equal(["late-check", "script-check"], Log);
        Assert.Equal(100u, SpellSystem.GetPower(player, PowerType.Rage));
    }

    [Fact]
    public void OnCast_SeesThePowerAlreadyTaken_AndRunsBeforeTheEffects()
    {
        Reset();
        using SpellTestKit kit = Kit();
        Install(kit, new CostScript());
        (Player player, _) = kit.AddPlayer(1);
        kit.Spellbook.Teach(player, CostSpell);
        SpellSystem.SetPower(player, PowerType.Rage, 100);

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(player, CostSpell, SpellCastTargets.ForSelf()));

        Assert.Equal("script-check", Log[0]);
        Assert.Equal(["cast:rage=90", "effect:rage=90"], Log.Where(l => !l.StartsWith("script-check", StringComparison.Ordinal)).Take(2));
        Assert.Equal(90u, SpellSystem.GetPower(player, PowerType.Rage));
    }

    [Fact]
    public void ScriptEffect_WithNoScript_DoesNothingAndIsNotReportedAsUnsupported()
    {
        Reset();
        using SpellTestKit plainKit = Kit();
        var before = new CapturingLogger();
        var plain = new SpellSystem(plainKit.Store, () => plainKit.Now, spellbook: plainKit.Spellbook, random: new Random(1), logger: before);
        (Player player, _) = plainKit.AddPlayer(1);
        plain.CastSpell(player, TeleportShape, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Contains(before.Lines, l => l.Contains("is not implemented yet", StringComparison.Ordinal)); // the base behaviour this slice removes

        var after = new CapturingLogger();
        var scripted = new SpellSystem(plainKit.Store, () => plainKit.Now, spellbook: plainKit.Spellbook, random: new Random(1), logger: after);
        SpellScriptDispatcher.Install(scripted, new SpellScriptRegistry([]));
        scripted.CastSpell(player, TeleportShape, SpellCastTargets.ForSelf(), triggered: true);

        Assert.DoesNotContain(after.Lines, l => l.Contains("is not implemented yet", StringComparison.Ordinal));
        Assert.True(scripted.HasEffectHandler(SpellEffectName.ScriptEffect));
    }

    [Fact]
    public void OnSuccessfulDispel_RunsOnlyWhenAnAuraWasRemoved()
    {
        Reset();
        using SpellTestKit kit = Kit();
        kit.System.Relations = new FakeRelations();
        Install(kit, new DispelScript());
        (Player player, _) = kit.AddPlayer(1);

        kit.System.CastSpell(player, DispelSpell, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Empty(Log); // nothing to dispel

        kit.System.CastSpell(player, MagicDebuff, SpellCastTargets.ForSelf(), triggered: true);
        Assert.True(kit.System.HasAura(player, MagicDebuff));
        kit.System.CastSpell(player, DispelSpell, SpellCastTargets.ForSelf(), triggered: true);

        Assert.False(kit.System.HasAura(player, MagicDebuff)); // the built-in dispel still ran
        Assert.Equal(["dispelled:1"], Log);
    }

    [Fact]
    public void TwoScriptsClaimingOneSpell_FailClosed()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new SpellScriptRegistry([new DummyScript(), new SecondDummyScript()]));

        Assert.Contains(nameof(DummyScript), error.Message);
        Assert.Contains(nameof(SecondDummyScript), error.Message);
        Assert.Contains(DummySpell.ToString(), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AScriptWithoutAttributeOrIds_FailsClosed()
    {
        Assert.Throws<InvalidOperationException>(() => new SpellScriptRegistry([new UnmarkedScript()]));
        Assert.Throws<InvalidOperationException>(() => new SpellScriptRegistry([new EmptyScript()]));
    }

    [Fact]
    public void AScriptForAnUnknownSpell_IsIgnoredWithOneWarning()
    {
        using SpellTestKit kit = Kit();
        var logger = new CapturingLogger();

        SpellScriptDispatcher.Install(kit.System, new SpellScriptRegistry([new GhostScript()]), logger);

        string warning = Assert.Single(logger.Lines);
        Assert.Contains("Warning", warning, StringComparison.Ordinal);
        Assert.Contains("960999", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallingTwice_Throws()
    {
        using SpellTestKit kit = Kit();
        Install(kit);

        Assert.Throws<InvalidOperationException>(() => Install(kit));
    }

    [Fact]
    public void ADeclaredEffect_SeesTheTargetBeforeTheEffectRuns_AndOnlyForTheClaimingSpell()
    {
        Reset();
        using SpellTestKit kit = Kit();
        Install(kit, new HealScript());
        (Player player, _) = kit.AddPlayer(1);
        player.MaxHealth = 100;
        player.Health = 50;

        kit.System.CastSpell(player, HealSpell, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(["before-heal:50"], Log); // the script ran first, then the built-in heal
        Assert.Equal(70u, player.Health);
    }

    [Fact]
    public void ADeclaredEffectTheWorldDoesNotHandle_IsNotChained_SoItStaysReportedAsNotImplemented()
    {
        using SpellTestKit kit = Kit();
        Assert.False(kit.System.HasEffectHandler(SpellEffectName.SummonDemon));

        Install(kit, new UnhandledEffectScript());

        Assert.False(kit.System.HasEffectHandler(SpellEffectName.SummonDemon));
    }

    [Fact]
    public void HandlersRegisteredBeforeAndAfter_StillRun()
    {
        Reset();
        using SpellTestKit kit = Kit();
        int earlier = 0;
        int later = 0;
        kit.System.RegisterEffect(SpellEffectName.Dummy, _ => earlier++);
        Install(kit, new DummyScript());
        SpellEffectHandler? chained = kit.System.GetEffectHandler(SpellEffectName.Dummy);
        kit.System.RegisterEffect(SpellEffectName.Dummy, context =>
        {
            later++;
            chained!(context);
        });
        (Player player, _) = kit.AddPlayer(1);

        kit.System.CastSpell(player, DummySpell, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal((1, 1), (earlier, later));
        Assert.Equal(["effect:5"], Log);
    }

    [Fact]
    public void TheShippedScripts_AreConsistent()
    {
        SpellScriptRegistry registry = SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly); // throws on a duplicate id or a bad script

        Assert.True(registry.Count >= 0);
    }

    [SpellScript(DummySpell)]
    private sealed class SecondDummyScript : ISpellScript;

    private sealed class UnmarkedScript : ISpellScript;

    [SpellScript]
    private sealed class EmptyScript : ISpellScript;

    [SpellScript(960_999)]
    private sealed class GhostScript : ISpellScript;
}
