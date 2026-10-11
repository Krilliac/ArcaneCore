using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots.Qa;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>A sink that keeps what it is given.</summary>
internal sealed class RecordingQaSink : IPlayerbotQaSink
{
    public List<QaSignal> Signals { get; } = [];

    public bool Enabled => true;

    public void Report(QaSignal signal) => Signals.Add(signal);

    public bool ReportFor(Unit unit, QaSignal signal)
    {
        Signals.Add(signal);
        return true;
    }
}

/// <summary>
/// A world with one bot and one real player, a spell system with the spells of a test, and the QA spell monitor registered on it. The monitor's bot
/// test is a seam here: <see cref="Bot"/> is a bot, <see cref="Human"/> is not (the production test of a managed session is
/// <see cref="PlayerbotQaBots.Identify"/>, covered on its own).
/// </summary>
internal sealed class QaSpellKit : IDisposable
{
    public const uint UnhandledEffect = 990001, UnsupportedAura = 990002, DummyOnly = 990003, ScriptOnly = 990004, DummyWithHeal = 990005,
        BadTarget = 990006, SelfHeal = 990007, SelfAura = 990008, SelfAbsorb = 990009, SelfNuke = 990010, ReferencedAura = 990011;

    /// <summary>An effect number no handler is registered for.</summary>
    public const SpellEffectName NoHandler = (SpellEffectName)250;

    /// <summary>An implicit target number the spell system does not select.</summary>
    public const SpellImplicitTarget NoTarget = (SpellImplicitTarget)200;

    public static readonly Guid BotKey = new("11111111-2222-3333-4444-555555555555");

    public QaSpellKit(IPlayerbotQaSink? sink = null, bool brokenHeal = false, bool brokenDamage = false, SpellInfo[]? extra = null)
    {
        World = new WorldRuntime(new WorldRuntimeOptions { UpdateCompressionThreshold = 0, AutosaveIntervalMs = 0, CommandTimeBudgetMs = 0 }, new NoSaves(),
            NullLogger<WorldRuntime>.Instance);
        UnsupportedAuraType = AuraSupport.Entries.First(e => e.Level == AuraSupportLevel.Unsupported).Type;
        ReferencedAuraType = AuraSupport.Entries.First(e => e.Level == AuraSupportLevel.Referenced && !new SpellSystem(SpellStore.Empty, () => 0).HasAuraHandler(e.Type)).Type;
        System = new SpellSystem(new SpellStore(
        [
            Spell(UnhandledEffect, Effect(NoHandler, 1)),
            Spell(UnsupportedAura, Effect(SpellEffectName.ApplyAura, 3, aura: UnsupportedAuraType)),
            Spell(DummyOnly, Effect(SpellEffectName.Dummy, 0)),
            Spell(ScriptOnly, Effect(SpellEffectName.ScriptEffect, 0)),
            Spell(DummyWithHeal, Effect(SpellEffectName.Dummy, 0), Effect(SpellEffectName.Heal, 10)),
            Spell(BadTarget, Effect(SpellEffectName.Heal, 10, NoTarget)),
            Spell(SelfHeal, Effect(SpellEffectName.Heal, 20)),
            Spell(SelfAura, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ModThreat)) with { Duration = new SpellDuration(30_000, 0, 30_000) },
            Spell(SelfAbsorb, Effect(SpellEffectName.ApplyAura, 50, aura: AuraType.SchoolAbsorb, misc: 127)) with { Duration = new SpellDuration(30_000, 0, 30_000) },
            Spell(SelfNuke, Effect(SpellEffectName.SchoolDamage, 10)),
            Spell(ReferencedAura, Effect(SpellEffectName.ApplyAura, 1, aura: ReferencedAuraType)) with { Duration = new SpellDuration(30_000, 0, 30_000) },
            .. extra ?? [],
        ], [], []), () => 10_000) { MapUpdateIntervalMs = 0 };
        new ScriptEffectModule().Register(System);
        if (brokenHeal) System.RegisterEffect(SpellEffectName.Heal, static _ => { }); // a handler that silently does nothing
        if (brokenDamage) System.RegisterEffect(SpellEffectName.SchoolDamage, static _ => { });
        Bot = AddPlayer(1, Class.Mage);
        Human = AddPlayer(2, Class.Mage);
        Sink = sink ?? new RecordingQaSink();
        Monitor = new PlayerbotQaSpellMonitor(Sink, System, unit => ReferenceEquals(unit, Bot) ? new QaBot(BotKey, (byte)Class.Mage) : null, () => NowMs,
            (_, spell) => spell == SelfHeal);
        System.RegisterObserver(Monitor);
    }

    public AuraType UnsupportedAuraType { get; }

    /// <summary>An aura type with no handler that a rule reads (AuraSupport: Referenced): it works.</summary>
    public AuraType ReferencedAuraType { get; }

    public WorldRuntime World { get; }

    public SpellSystem System { get; }

    public Player Bot { get; }

    public Player Human { get; }

    public IPlayerbotQaSink Sink { get; }

    public List<QaSignal> Signals => ((RecordingQaSink)Sink).Signals;

    public PlayerbotQaSpellMonitor Monitor { get; }

    public long NowMs { get; set; } = 1_000_000;

    /// <summary>A bot's own cast (not triggered: the monitor skips triggered casts).</summary>
    public SpellCastResult Cast(Player caster, uint spell) => System.CastSpell(caster, spell, SpellCastTargets.ForSelf(), triggered: false);

    public SpellCastResult CastTriggered(Player caster, uint spell) => System.CastSpell(caster, spell, SpellCastTargets.ForSelf(), triggered: true);

    public void Dispose() => World.Dispose();

    private Player AddPlayer(uint guid, Class playerClass)
    {
        var character = new CharacterRecord
        {
            Id = (int)guid, AccountId = (int)guid, Name = $"P{guid}", Race = (byte)Race.Human, Class = (byte)playerClass, Gender = 0, Level = 10, MapId = 0,
            ZoneId = 12, X = guid * 3, Y = 0, Z = 83.5f,
        };
        var player = new Player(character, new PlayerAppearance(49, 1, PowerType.Mana, 100, 100, 100, 1000, 1000, 400), new Honor.HonorNullSession());
        World.AddPlayer(player);
        World.RunTick(0);
        return player;
    }

    public static SpellEffectInfo Effect(SpellEffectName effect, int value, SpellImplicitTarget targetA = SpellImplicitTarget.UnitCaster, AuraType aura = AuraType.None,
        int misc = 0)
        => new() { Effect = effect, BasePoints = value - 1, BaseDice = 1, DieSides = 1, TargetA = targetA, AuraType = aura, MiscValue = misc };

    public static SpellInfo Spell(uint id, params SpellEffectInfo[] effects) => new()
    {
        Id = id, Name = $"Spell {id}", Rank = "Rank 2", RangeIndex = SpellConstants.RangeIndexSelfOnly, Range = new SpellRange(0, 0), Effects = effects,
    };

    private sealed class NoSaves : ICharacterSaveQueue
    {
        public void Enqueue(CharacterState state)
        {
        }
    }
}

/// <summary>The QA spell monitor: unsupported spells, implicit targets, spells that land with no effect and refused casts, of bots only.</summary>
public sealed class PlayerbotQaSpellMonitorTests
{
    [Fact]
    public void ABotsCastOfASpellWithAGap_IsReported_WithTheSpellTheEffectAndTheGap()
    {
        using var kit = new QaSpellKit();

        foreach (uint spell in new[] { QaSpellKit.UnhandledEffect, QaSpellKit.UnsupportedAura, QaSpellKit.DummyOnly, QaSpellKit.ScriptOnly,
                     QaSpellKit.DummyWithHeal, QaSpellKit.SelfHeal })
        {
            kit.Cast(kit.Bot, spell);
        }

        Assert.All(kit.Signals, s => Assert.Equal(QaSignalKind.SpellUnsupported, s.Kind));
        Assert.Equal(
            [
                (QaSpellKit.UnhandledEffect, 0, QaSpellGaps.Effect, "250"),
                (QaSpellKit.UnsupportedAura, 0, QaSpellGaps.Aura, kit.UnsupportedAuraType.ToString()),
                (QaSpellKit.DummyOnly, 0, QaSpellGaps.Dummy, "Dummy"),
                (QaSpellKit.ScriptOnly, 0, QaSpellGaps.Script, "ScriptEffect"),
            ],
            kit.Signals.Select(s => (s.Spell, s.EffectIndex, s.Reason, s.Detail)));
        QaSignal aura = kit.Signals[1];
        Assert.Equal((QaSpellKit.BotKey, (byte)Class.Mage, "Spell 990002", "Rank 2", 3, (uint)kit.UnsupportedAuraType, "ApplyAura"),
            (aura.BotKey!.Value, aura.BotClass, aura.SpellName, aura.SpellRank, aura.EffectValue, aura.Aura, aura.EffectName));
    }

    [Fact]
    public void ATriggeredCast_AndAReferencedAuraWithNoHandler_AreNotReported()
    {
        using var kit = new QaSpellKit();

        foreach (uint spell in new[] { QaSpellKit.UnhandledEffect, QaSpellKit.UnsupportedAura, QaSpellKit.DummyOnly, QaSpellKit.BadTarget })
        {
            kit.CastTriggered(kit.Bot, spell); // a proc, item or script cast: not the bot's choice, and outside its spell book
        }

        kit.Cast(kit.Bot, QaSpellKit.ReferencedAura); // no handler, but a rule reads it: it works

        Assert.Empty(kit.Signals);
    }

    [Fact]
    public void AnUnsupportedEffect_IsReportedOnlyFromTheFailingPath_NotFromTheRegistryAlone()
    {
        using var kit = new QaSpellKit();
        kit.Cast(kit.Bot, QaSpellKit.UnhandledEffect);
        Assert.Single(kit.Signals);

        // The live server has the handler (a feature installed it after the facts were cached): the next cast meets no gap and reports nothing.
        kit.System.RegisterEffect(QaSpellKit.NoHandler, static _ => { });
        kit.NowMs += PlayerbotQaSpellMonitor.ThrottleMs;
        kit.Cast(kit.Bot, QaSpellKit.UnhandledEffect);

        Assert.Single(kit.Signals);
    }

    [Fact]
    public void AFullyAbsorbedDamageSpell_IsNotNoEffect()
    {
        using var shielded = new QaSpellKit(brokenDamage: true);
        shielded.Cast(shielded.Bot, QaSpellKit.SelfAbsorb);
        for (int i = 0; i < 5; i++) shielded.Cast(shielded.Bot, QaSpellKit.SelfNuke); // looks like no damage: the shield could have taken it
        Assert.DoesNotContain(shielded.Signals, s => s.Kind == QaSignalKind.SpellNoEffect);

        using var bare = new QaSpellKit(brokenDamage: true);
        for (int i = 0; i < PlayerbotQaSpellMonitor.NoEffectRepeats; i++) bare.Cast(bare.Bot, QaSpellKit.SelfNuke);
        Assert.Contains(bare.Signals, s => s.Kind == QaSignalKind.SpellNoEffect && s.Spell == QaSpellKit.SelfNuke);
    }

    [Fact]
    public void TheSameCastsOfARealPlayer_ReportNothing()
    {
        using var kit = new QaSpellKit();

        foreach (uint spell in new[] { QaSpellKit.UnhandledEffect, QaSpellKit.UnsupportedAura, QaSpellKit.DummyOnly, QaSpellKit.ScriptOnly, QaSpellKit.BadTarget })
        {
            kit.Cast(kit.Human, spell);
        }

        kit.Monitor.OnCastRefused(kit.Human, QaSpellKit.SelfHeal, SpellCastResult.Error);
        for (int i = 0; i < 20; i++) kit.Monitor.OnCastRefused(kit.Human, QaSpellKit.SelfHeal, SpellCastResult.NoPower);

        Assert.Empty(kit.Signals);
    }

    [Fact]
    public void TheProductionBotTest_IgnoresAPlayerOfAnUnmanagedSession()
    {
        using var kit = new QaSpellKit();
        var sink = new RecordingQaSink();
        var monitor = new PlayerbotQaSpellMonitor(sink, kit.System); // the production PlayerbotQaBots.Identify
        kit.System.RegisterObserver(monitor);

        kit.Cast(kit.Bot, QaSpellKit.UnhandledEffect); // a Player of a non-WorldSession session: not a bot for production
        monitor.OnCastRefused(kit.Bot, QaSpellKit.SelfHeal, SpellCastResult.Error);

        Assert.Empty(sink.Signals);
    }

    [Fact]
    public void AnImplicitTargetTheSystemCannotSelect_IsReported_OnlyForABot()
    {
        using var kit = new QaSpellKit();

        kit.Cast(kit.Human, QaSpellKit.BadTarget);
        Assert.Empty(kit.Signals);
        kit.Cast(kit.Bot, QaSpellKit.BadTarget);

        QaSignal signal = Assert.Single(kit.Signals);
        Assert.Equal((QaSignalKind.SpellUnsupported, QaSpellKit.BadTarget, 0, QaSpellGaps.Target, "Heal"),
            (signal.Kind, signal.Spell, signal.EffectIndex, signal.Reason, signal.EffectName));
        Assert.StartsWith("200 ", signal.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameGap_IsReportedOncePerThrottleWindow()
    {
        using var kit = new QaSpellKit();

        for (int i = 0; i < 5; i++) kit.Cast(kit.Bot, QaSpellKit.UnhandledEffect);
        Assert.Single(kit.Signals);
        kit.NowMs += PlayerbotQaSpellMonitor.ThrottleMs;
        kit.Cast(kit.Bot, QaSpellKit.UnhandledEffect);

        Assert.Equal(2, kit.Signals.Count);
    }

    [Fact]
    public void ASpellThatLandsWithNoEffect_IsReportedAfterTheRepeats_OnlyForABot()
    {
        using var kit = new QaSpellKit(brokenHeal: true);
        kit.Bot.Health = 30;
        kit.Human.Health = 30;

        for (int i = 0; i < PlayerbotQaSpellMonitor.NoEffectRepeats; i++) kit.Cast(kit.Human, QaSpellKit.SelfHeal);
        for (int i = 0; i < PlayerbotQaSpellMonitor.NoEffectRepeats - 1; i++) kit.Cast(kit.Bot, QaSpellKit.SelfHeal);
        Assert.Empty(kit.Signals);
        kit.Cast(kit.Bot, QaSpellKit.SelfHeal);

        QaSignal signal = Assert.Single(kit.Signals);
        Assert.Equal((QaSignalKind.SpellNoEffect, QaSpellKit.SelfHeal, "expected healing"), (signal.Kind, signal.Spell, signal.Detail));
    }

    [Fact]
    public void AWorkingHeal_AnOverhealOfAFullTarget_AndAnAppliedAura_AreNotNoEffect()
    {
        using var working = new QaSpellKit();
        working.Bot.Health = 30;
        for (int i = 0; i < 5; i++) working.Cast(working.Bot, QaSpellKit.SelfHeal);
        for (int i = 0; i < 5; i++) working.Cast(working.Bot, QaSpellKit.SelfAura);
        Assert.Empty(working.Signals);

        using var broken = new QaSpellKit(brokenHeal: true);
        broken.Bot.Health = broken.Bot.MaxHealth; // nothing to heal: an overheal is not a failure
        for (int i = 0; i < 5; i++) broken.Cast(broken.Bot, QaSpellKit.SelfHeal);
        Assert.Empty(broken.Signals);
    }

    [Fact]
    public void AnExpectedRefusal_IsReportedOnlyWhenOneBotRepeatsItInsideTheWindow()
    {
        using var kit = new QaSpellKit();

        for (int i = 0; i < PlayerbotQaSpellMonitor.RepeatThreshold - 1; i++) kit.Monitor.OnCastRefused(kit.Bot, QaSpellKit.SelfHeal, SpellCastResult.NoPower);
        kit.NowMs += PlayerbotQaSpellMonitor.RepeatWindowMs + 1; // the window ran out: the count starts again
        for (int i = 0; i < PlayerbotQaSpellMonitor.RepeatThreshold - 1; i++) kit.Monitor.OnCastRefused(kit.Bot, QaSpellKit.SelfHeal, SpellCastResult.NoPower);
        Assert.Empty(kit.Signals);
        kit.Monitor.OnCastRefused(kit.Bot, QaSpellKit.SelfHeal, SpellCastResult.NoPower);

        QaSignal signal = Assert.Single(kit.Signals);
        Assert.Equal((QaSignalKind.SpellRefused, QaSpellKit.SelfHeal, "NoPower", "Spell 990007"), (signal.Kind, signal.Spell, signal.Reason, signal.SpellName));
        Assert.StartsWith($"{PlayerbotQaSpellMonitor.RepeatThreshold} times in ", signal.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ASuspiciousRefusal_IsReportedAtOnce_AndCastOkNever()
    {
        using var kit = new QaSpellKit();

        kit.Monitor.OnCastRefused(kit.Bot, QaSpellKit.SelfHeal, SpellCastResult.CastOk);
        kit.Monitor.OnCastRefused(kit.Bot, QaSpellKit.SelfHeal, SpellCastResult.NotKnown);
        kit.Monitor.OnCastRefused(kit.Bot, QaSpellKit.SelfAura, SpellCastResult.Error);
        kit.Monitor.OnCastRefused(kit.Bot, QaSpellKit.SelfAura, SpellCastResult.BadTargets); // a rule of play: only repeats count

        Assert.Equal([("NotKnown", "the spell is in the bot's spellbook"), ("Error", "")], kit.Signals.Select(s => (s.Reason, s.Detail)));
    }

    /// <summary>The refusal table (PlayerbotQaSpellRefusals): what is a rule of play and what a bot should never get.</summary>
    [Theory]
    [InlineData(SpellCastResult.NoPower, false)]
    [InlineData(SpellCastResult.NotReady, false)]
    [InlineData(SpellCastResult.OutOfRange, false)]
    [InlineData(SpellCastResult.LineOfSight, false)]
    [InlineData(SpellCastResult.Moving, false)]
    [InlineData(SpellCastResult.Interrupted, false)]
    [InlineData(SpellCastResult.TargetsDead, false)]
    [InlineData(SpellCastResult.SpellInProgress, false)]
    [InlineData(SpellCastResult.Silenced, false)]
    [InlineData(SpellCastResult.NoComboPoints, false)]
    [InlineData(SpellCastResult.Reagents, false)]
    [InlineData(SpellCastResult.Immune, false)]
    [InlineData(SpellCastResult.Error, true)]
    [InlineData(SpellCastResult.Unknown, true)]
    [InlineData(SpellCastResult.NotFound, true)]
    [InlineData(SpellCastResult.SpellUnavailable, true)]
    [InlineData(SpellCastResult.NotKnown, true)]
    [InlineData(SpellCastResult.BadTargets, false)]
    [InlineData(SpellCastResult.BadImplicitTargets, false)]
    [InlineData(SpellCastResult.TrainingPoints, false)]
    [InlineData(SpellCastResult.TargetAurastate, false)]
    [InlineData(SpellCastResult.LevelRequirement, true)]
    public void TheRefusalTable(SpellCastResult result, bool suspicious)
    {
        QaRefusalClass expected = suspicious ? QaRefusalClass.Suspicious : QaRefusalClass.Expected;
        QaRefusalRule rule = PlayerbotQaSpellRefusals.Of(result);
        Assert.Equal(expected, rule.Class);
        Assert.Equal(rule, PlayerbotQaSpellRefusals.Of(result.ToString()));
        Assert.False(string.IsNullOrWhiteSpace(rule.Why));
        Assert.Equal(expected == QaRefusalClass.Expected ? QaCause.BehaviourBug : rule.Cause, rule.Cause);
    }

    [Fact]
    public void TheRefusalTable_ListsExactlyTheSuspiciousResults_AndReadsNoOtherName()
    {
        Assert.Equal(
            [SpellCastResult.Error, SpellCastResult.LevelRequirement, SpellCastResult.NotKnown, SpellCastResult.SpellUnavailable, SpellCastResult.Unknown,
                SpellCastResult.NotFound],
            PlayerbotQaSpellRefusals.Suspicious.Keys.Order());
        Assert.Null(PlayerbotQaSpellRefusals.Of("NotAResult"));
        Assert.Null(PlayerbotQaSpellRefusals.Of("200"));
    }

    /// <summary>
    /// The observer runs on the world thread for every bot cast: once a spell's facts are cached, a bot's cast of a working spell, a throttled repeat of a
    /// gap, a hit that worked, a real player's cast and an expected refusal below the threshold allocate nothing.
    /// </summary>
    [Fact]
    public void TheHotPath_AllocatesNothing()
    {
        using var kit = new QaSpellKit();
        var capture = new CastCapture();
        kit.System.RegisterObserver(capture);
        kit.Bot.Health = 30;
        kit.Cast(kit.Bot, QaSpellKit.SelfHeal);
        SpellCast heal = capture.Last!;
        SpellTargetOutcome healed = capture.Outcome!.Value;
        kit.Cast(kit.Bot, QaSpellKit.UnhandledEffect);
        SpellCast gap = capture.Last!;
        kit.Cast(kit.Human, QaSpellKit.SelfHeal);
        SpellCast human = capture.Last!;
        SpellTargetOutcome humanHealed = capture.Outcome!.Value;
        Assert.Single(kit.Signals); // the gap, once; then throttled

        void HotPath()
        {
            for (int i = 0; i < 1000; i++)
            {
                kit.Monitor.OnCast(heal);
                kit.Monitor.OnTargetOutcome(heal, healed);
                kit.Monitor.OnCast(gap);
                kit.Monitor.OnCast(human);
                kit.Monitor.OnTargetOutcome(human, humanHealed);
                ((ISpellCastObserver)kit.Monitor).OnFinished(heal, true);
                if (i % 100 == 0) kit.Monitor.OnCastRefused(kit.Bot, QaSpellKit.SelfHeal, SpellCastResult.NotReady);
            }
        }

        for (int i = 0; i < PlayerbotQaSpellMonitor.RepeatThreshold; i++) kit.Monitor.OnCastRefused(kit.Bot, QaSpellKit.SelfHeal, SpellCastResult.NotReady);
        HotPath(); // warm: the JIT, the dictionaries' buckets; the repeats above reported once, the next ones are throttled
        int signals = kit.Signals.Count;
        Assert.Equal(2, signals);
        long before = GC.GetAllocatedBytesForCurrentThread();
        HotPath();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(signals, kit.Signals.Count);
    }

    private sealed class CastCapture : ISpellCastObserver
    {
        public SpellCast? Last { get; private set; }

        public SpellTargetOutcome? Outcome { get; private set; }

        public void OnCast(SpellCast cast) => Last = cast;

        public void OnTargetOutcome(SpellCast cast, SpellTargetOutcome outcome) => Outcome = outcome;
    }
}
