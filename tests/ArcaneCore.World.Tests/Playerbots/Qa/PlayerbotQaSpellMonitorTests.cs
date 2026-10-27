using ArcaneCore.Data.ClientData;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Effects;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Tests.ClientData;
using ArcaneCore.World.Playerbots.Qa;
using ArcaneCore.World.Spells;
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
        World = new WorldRuntime(new WorldRuntimeOptions { UpdateCompressionThreshold = 0, AutosaveIntervalMs = 0, CommandTimeBudgetMs = 0 }, new NullSaveQueue(),
            NullLogger<WorldRuntime>.Instance);
        UnsupportedAuraType = AuraSupport.Entries.First(e => e.Level == AuraSupportLevel.Unsupported).Type;
        // AuraSupportBaseline has no Referenced entries since 69f0044b promoted them to Handler; any supported type the bare system has no handler for is the same case.
        ReferencedAuraType = AuraSupport.Entries.First(e => e.Level != AuraSupportLevel.Unsupported && !new SpellSystem(SpellStore.Empty, () => 0).HasAuraHandler(e.Type)).Type;
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
        System.SpellBatchWindowMs = 0; // the monitor is fed in the call that casts (observers, Hit); no update ever flushes a spell batch here
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
        var player = new Player(character, new PlayerAppearance(49, 1, PowerType.Mana, 100, 100, 100, 1000, 1000, 400), new NullPlayerSession());
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

    // Issue #239: 27965 "Suicide" (Spell.dbc: effect 1 INSTAKILL, effect 2 APPLY_AURA DUMMY, both on the caster) is what a Spirit of Redemption priest casts
    // at itself when the form ends (vmangos SpellAuras.cpp:5735). The kill removes the aura the spell applies, so the hit "left no aura".
    [Fact]
    public void AnInstakillOfTheTarget_IsADamageEffect_NotNoEffect()
    {
        const uint suicide = 27965;
        using var kit = new QaSpellKit(extra:
        [
            QaSpellKit.Spell(suicide, QaSpellKit.Effect(SpellEffectName.Instakill, 0), QaSpellKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy))
                with { Duration = new SpellDuration(30_000, 0, 30_000) },
        ]);
        kit.System.Damage = new MapCombatDamageSink();
        kit.World.GetMap(0).Combat.SpellMitigation = kit.System;
        new DirectCombatEffects().Register(kit.System);

        for (int i = 0; i < PlayerbotQaSpellMonitor.NoEffectRepeats * 2; i++)
        {
            kit.Cast(kit.Bot, suicide);
            Assert.False(kit.Bot.IsAlive, "the instakill must really kill the caster");
            kit.Bot.Health = kit.Bot.MaxHealth;
            kit.Bot.Combat.DeathState = DeathState.Alive;
        }

        Assert.DoesNotContain(kit.Signals, s => s.Kind == QaSignalKind.SpellNoEffect);
    }

    // A Suicide that does not kill (a Spirit of Redemption threshold that leaked and never ended) is a real bug and must be reported, with or without a
    // shield on the target: an INSTAKILL has no absorb step, so the absorb exemption does not apply to it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnInstakillThatLeavesTheTargetAlive_IsStillReported_EvenWithAnAbsorbShield(bool shielded)
    {
        const uint kill = 990100;
        using var kit = new QaSpellKit(extra: [QaSpellKit.Spell(kill, QaSpellKit.Effect(SpellEffectName.Instakill, 0))]);
        kit.System.Damage = new MapCombatDamageSink();
        kit.World.GetMap(0).Combat.SpellMitigation = kit.System;
        new DirectCombatEffects().Register(kit.System);
        if (shielded) kit.Cast(kit.Bot, QaSpellKit.SelfAbsorb);
        kit.Bot.InvincibilityHpThreshold = kit.Bot.MaxHealth; // the leaked threshold: the kill cannot take health

        for (int i = 0; i < PlayerbotQaSpellMonitor.NoEffectRepeats; i++) kit.Cast(kit.Bot, kill);

        Assert.True(kit.Bot.IsAlive);
        QaSignal signal = Assert.Single(kit.Signals, s => s.Kind == QaSignalKind.SpellNoEffect);
        Assert.Equal(kill, signal.Spell);
        Assert.Contains($"invincibility threshold {kit.Bot.MaxHealth}", signal.Message, StringComparison.Ordinal);
    }

    // Issue #240: 10946 Mind Blast (Rank 8). Its Spell.dbc row is SCHOOL_DAMAGE like every other rank and the server deals it; the bot QA saw hits that took no
    // health. Issue #240 was explained by the Blasted Lands quest creatures 7668/7669 (EventAI holds them at 1 health, so a creature at its threshold is excused).
    // A hit on a unit at its invincibility threshold takes none (vmangos Unit.cpp:825-850), which can be by design (a creature held by its script, Spirit of
    // Redemption) or a leaked/never-ending death prevention on a player: a held player with no Spirit of Redemption aura stays reported, and the report names
    // the target so the next report can be diagnosed.
    [RealClientDbcFact]
    public void MindBlastRank8_FromTheDbc_DealsDamage_AndANoEffectReportNamesTheThresholdTarget()
    {
        const uint mindBlast = 10946;
        DbcFile dbc = DbcFile.Load(Path.Combine(RealClientDbcFactAttribute.DbcDirectory, "Spell.dbc"));
        int row = Enumerable.Range(0, dbc.RecordCount).First(r => dbc.GetUInt32(r, 0) == mindBlast);
        SpellInfo rank8 = SpellStoreFactory.ToSpellInfo(SpellDbcImporter.ReadSpell(dbc, row), new Dictionary<uint, SpellCastTimeRow>(),
            new Dictionary<uint, SpellDurationRow>(), new Dictionary<uint, SpellRangeRow>(), new Dictionary<uint, SpellRadiusRow>());
        Assert.Equal((SpellEffectName.SchoolDamage, 424, 25), (rank8.Effects[0].Effect, rank8.Effects[0].BasePoints, rank8.Effects[0].DieSides));

        using var kit = new QaSpellKit(extra: [rank8]);
        kit.System.Damage = new MapCombatDamageSink();
        kit.World.GetMap(0).Combat.SpellMitigation = kit.System;

        uint before = kit.Human.Health;
        kit.System.CastSpell(kit.Bot, mindBlast, SpellCastTargets.ForUnit(kit.Human.Guid), triggered: true);
        Assert.True(kit.Human.Health < before, "Mind Blast rank 8 must damage a normal target");
        Assert.DoesNotContain(kit.Signals, s => s.Kind == QaSignalKind.SpellNoEffect);

        kit.Human.Combat.DeathState = DeathState.Alive; // the 100 health of the test player did not survive the hit
        kit.Human.Health = kit.Human.MaxHealth;
        kit.Human.InvincibilityHpThreshold = kit.Human.MaxHealth; // health cannot fall
        for (int i = 0; i < PlayerbotQaSpellMonitor.NoEffectRepeats; i++)
        {
            Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(kit.Bot, mindBlast, SpellCastTargets.ForUnit(kit.Human.Guid), triggered: true));
        }

        Assert.Equal(kit.Human.MaxHealth, kit.Human.Health);
        QaSignal signal = Assert.Single(kit.Signals, s => s.Kind == QaSignalKind.SpellNoEffect);
        Assert.Equal(mindBlast, signal.Spell);
        Assert.Equal("expected damage", signal.Detail); // the finding's key and detail do not change with the target
        Assert.Contains($"player, health {kit.Human.MaxHealth}/{kit.Human.MaxHealth}, invincibility threshold {kit.Human.MaxHealth}, absorb aura no", signal.Message,
            StringComparison.Ordinal);
    }

    private static Creature HeldCreature(uint health, uint threshold, uint low = 990_900)
    {
        var template = new CreatureTemplate { Entry = 7668, Name = "held", CreatureType = 1, MinLevelHealth = 2000, MaxLevelHealth = 2000 };
        var creature = new Creature(low, template, null, new CreatureContent([template], [], [], [], []), new Random(1));
        creature.Health = health;
        creature.InvincibilityHpThreshold = threshold;
        return creature;
    }

    /// <summary>A real cast of <paramref name="spell"/> by the bot at itself, for a <see cref="SpellCast"/> to hand to the monitor with a made-up outcome.</summary>
    private static SpellCast CastOf(QaSpellKit kit, uint spell)
    {
        var capture = new CastCapture();
        kit.System.RegisterObserver(capture);
        kit.Cast(kit.Bot, spell);
        kit.Signals.Clear();
        return capture.Last!;
    }

    private static void Hit(QaSpellKit kit, SpellCast cast, Unit target, int mask, int times)
    {
        for (int i = 0; i < times; i++)
        {
            kit.Monitor.OnTargetOutcome(cast, new SpellTargetOutcome(target, SpellMissInfo.None, Damage: 0, Healing: 0, Critical: false, EffectMask: mask));
        }
    }

    // The Blasted Lands quest creatures 7668-7671 (creature_ai_scripts 766801) sit at 1 health, invincible: every damage spell on them measures 0.
    [Fact]
    public void DamageOnACreatureHeldAtItsInvincibilityThreshold_IsNotNoEffect()
    {
        using var kit = new QaSpellKit();
        SpellCast cast = CastOf(kit, QaSpellKit.SelfNuke);

        Hit(kit, cast, HeldCreature(health: 1, threshold: 1), mask: 1, times: PlayerbotQaSpellMonitor.NoEffectRepeats);

        Assert.DoesNotContain(kit.Signals, s => s.Kind == QaSignalKind.SpellNoEffect);
    }

    [Fact]
    public void DamageOnAHeldCreature_IsReportedOnceAsAHeldTarget_AndNotAsNoEffect()
    {
        using var kit = new QaSpellKit();
        SpellCast cast = CastOf(kit, QaSpellKit.SelfNuke);

        Hit(kit, cast, HeldCreature(health: 1, threshold: 1), mask: 1, times: PlayerbotQaSpellMonitor.NoEffectRepeats * 2);

        Assert.DoesNotContain(kit.Signals, s => s.Kind == QaSignalKind.SpellNoEffect);
        QaSignal signal = Assert.Single(kit.Signals);
        Assert.Equal(QaSignalKind.Anomaly, signal.Kind);
        Assert.Equal(PlayerbotQaSpellMonitor.HeldTargetGoal, signal.Goal);
        Assert.Equal(PlayerbotQaSpellMonitor.HeldTargetReason + "7668", signal.Reason);
        Assert.Equal(7668u, signal.Target);
        Assert.Contains("entry 7668, threshold 1", signal.Detail, StringComparison.Ordinal);

        kit.NowMs += PlayerbotQaSpellMonitor.HeldTargetThrottleMs - 1;
        Hit(kit, cast, HeldCreature(health: 1, threshold: 1), mask: 1, times: 1);
        Assert.Single(kit.Signals);

        kit.NowMs += 1; // the 6 h window is over: the creature that stays held is visible again
        Hit(kit, cast, HeldCreature(health: 1, threshold: 1), mask: 1, times: 1);
        Assert.Equal(2, kit.Signals.Count);
    }

    // Spirit of Redemption (27827) holds a priest at its threshold by design; a player held without it is a leaked death prevention (GM god mode, Player.Cheats.cs).
    [Fact]
    public void DamageOnAHeldPlayerWithSpiritOfRedemption_IsNotReported()
    {
        using var kit = new QaSpellKit(extra: [QaSpellKit.Spell(SpellSystem.SpiritOfRedemptionSpell, QaSpellKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ModThreat))
            with { Duration = new SpellDuration(30_000, 0, 30_000) }]);
        SpellCast cast = CastOf(kit, QaSpellKit.SelfNuke);
        kit.CastTriggered(kit.Human, SpellSystem.SpiritOfRedemptionSpell);
        Assert.True(kit.System.HasAura(kit.Human, SpellSystem.SpiritOfRedemptionSpell));
        kit.Human.Health = kit.Human.MaxHealth;
        kit.Human.InvincibilityHpThreshold = kit.Human.MaxHealth;

        Hit(kit, cast, kit.Human, mask: 1, times: PlayerbotQaSpellMonitor.NoEffectRepeats);

        Assert.Empty(kit.Signals);
    }

    [Fact]
    public void DamageOnAHeldPlayerWithoutSpiritOfRedemption_IsReported()
    {
        using var kit = new QaSpellKit();
        SpellCast cast = CastOf(kit, QaSpellKit.SelfNuke);
        Assert.False(kit.System.HasAura(kit.Human, SpellSystem.SpiritOfRedemptionSpell));
        kit.Human.Health = kit.Human.MaxHealth;
        kit.Human.InvincibilityHpThreshold = kit.Human.MaxHealth;

        Hit(kit, cast, kit.Human, mask: 1, times: PlayerbotQaSpellMonitor.NoEffectRepeats);

        QaSignal signal = Assert.Single(kit.Signals);
        Assert.Equal((QaSignalKind.SpellNoEffect, QaSpellKit.SelfNuke), (signal.Kind, signal.Spell));
    }

    [Fact]
    public void ADamageHitThatDealsNothingToAHealthyCreature_IsStillReported_EvenWithAThreshold()
    {
        using var kit = new QaSpellKit();
        SpellCast cast = CastOf(kit, QaSpellKit.SelfNuke);

        Hit(kit, cast, HeldCreature(health: 2000, threshold: 1), mask: 1, times: PlayerbotQaSpellMonitor.NoEffectRepeats);

        QaSignal signal = Assert.Single(kit.Signals);
        Assert.Equal((QaSignalKind.SpellNoEffect, QaSpellKit.SelfNuke), (signal.Kind, signal.Spell));
    }

    [Fact]
    public void ADamageAndAuraSpellOnAHeldCreatureWhoseAuraDoesNotApply_IsStillReported()
    {
        const uint mixed = 990_300;
        using var kit = new QaSpellKit(extra:
        [
            QaSpellKit.Spell(mixed, QaSpellKit.Effect(SpellEffectName.SchoolDamage, 10), QaSpellKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ModThreat))
                with { Duration = new SpellDuration(30_000, 0, 30_000) },
        ]);
        SpellCast cast = CastOf(kit, mixed);

        Hit(kit, cast, HeldCreature(health: 1, threshold: 1), mask: 3, times: PlayerbotQaSpellMonitor.NoEffectRepeats);

        QaSignal signal = Assert.Single(kit.Signals);
        Assert.Equal(mixed, signal.Spell);
    }

    // Frenzied Regeneration tick (DruidScripts.FrenziedRegenerationTick) casts heal 22845 with rage * amount / 10 base points: 0 at 0 rage.
    [Fact]
    public void FrenziedRegenerationTicksOfADruidWithNoRage_AreNotNoEffect()
    {
        const uint frenziedHeal = 22845;
        using var kit = new QaSpellKit(extra: [QaSpellKit.Spell(frenziedHeal, QaSpellKit.Effect(SpellEffectName.Heal, 5))]);
        kit.Bot.Health = 30;

        for (int i = 0; i < PlayerbotQaSpellMonitor.NoEffectRepeats; i++)
        {
            Assert.Equal(SpellCastResult.CastOk, kit.System.CastCustomSpell(kit.Bot, frenziedHeal, SpellCastTargets.ForSelf(), 0));
        }

        Assert.Equal(30u, kit.Bot.Health);
        Assert.Empty(kit.Signals);
    }

    [Fact]
    public void ACustomHealWithAPositiveValueThatHealsNothing_IsStillReported()
    {
        const uint frenziedHeal = 22845;
        using var kit = new QaSpellKit(extra: [QaSpellKit.Spell(frenziedHeal, QaSpellKit.Effect(SpellEffectName.Heal, 5))]);
        kit.System.Damage = new NoHealSink();
        kit.Bot.Health = 30;

        for (int i = 0; i < PlayerbotQaSpellMonitor.NoEffectRepeats; i++)
        {
            Assert.Equal(SpellCastResult.CastOk, kit.System.CastCustomSpell(kit.Bot, frenziedHeal, SpellCastTargets.ForSelf(), 100));
        }

        QaSignal signal = Assert.Single(kit.Signals);
        Assert.Equal((QaSignalKind.SpellNoEffect, frenziedHeal, "expected healing"), (signal.Kind, signal.Spell, signal.Detail));
    }

    [Fact]
    public void CustomValueIsNonPositive_IsFalseOutsideACustomCast()
    {
        using var kit = new QaSpellKit(extra: [QaSpellKit.Spell(22845, QaSpellKit.Effect(SpellEffectName.Heal, 5))]);

        Assert.False(kit.System.CustomValueIsNonPositive(kit.Bot, kit.System.Store.Get(22845)!, 1));
    }

    /// <summary>Calls CustomValueIsNonPositive from inside a custom cast of <paramref name="spell"/> by the bot, through an observer.</summary>
    private static bool? InsideCustomCast(QaSpellKit kit, SpellInfo spell, int?[] basePoints, Unit asker, int mask)
    {
        var probe = new NonPositiveProbe(kit.System, asker, spell, mask);
        kit.System.RegisterObserver(probe);
        try
        {
            kit.System.CastCustomSpell(kit.Bot, spell.Id, SpellCastTargets.ForSelf(), basePoints[0], basePoints[1], basePoints[2]);
            return probe.Result;
        }
        finally
        {
            kit.System.UnregisterObserver(probe);
        }
    }

    private sealed class NonPositiveProbe(SpellSystem system, Unit asker, SpellInfo spell, int mask) : ISpellCastObserver
    {
        public bool? Result { get; private set; }

        public void OnTargetOutcome(SpellCast cast, SpellTargetOutcome outcome) => Result ??= system.CustomValueIsNonPositive(asker, spell, mask);
    }

    [Fact]
    public void CustomValueIsNonPositive_MatchesTheCasterAndTheValue()
    {
        using var kit = new QaSpellKit(extra: [QaSpellKit.Spell(22845, QaSpellKit.Effect(SpellEffectName.Heal, 5))]);
        SpellInfo spell = kit.System.Store.Get(22845)!;

        Assert.True(InsideCustomCast(kit, spell, [0, null, null], kit.Bot, 1));
        Assert.True(InsideCustomCast(kit, spell, [-1, null, null], kit.Bot, 1));
        Assert.False(InsideCustomCast(kit, spell, [100, null, null], kit.Bot, 1));
        Assert.False(InsideCustomCast(kit, spell, [0, null, null], kit.Human, 1)); // another caster than the scope's
        Assert.False(InsideCustomCast(kit, spell, [0, null, null], kit.Bot, 0)); // an empty mask
    }

    [Fact]
    public void CustomValueIsNonPositive_IsFalseForAnEffectWithItsOwnValueOrARandomDie()
    {
        const uint multi = 990_400, dice = 990_401;
        using var kit = new QaSpellKit(extra:
        [
            QaSpellKit.Spell(multi, QaSpellKit.Effect(SpellEffectName.Heal, 5), QaSpellKit.Effect(SpellEffectName.Heal, 5)),
            QaSpellKit.Spell(dice, QaSpellKit.Effect(SpellEffectName.Heal, 5) with { DieSides = 6 }),
        ]);

        // Effect 1 has no custom value (null): the spell's own value applies, so a mask holding it is not provably non-positive.
        Assert.False(InsideCustomCast(kit, kit.System.Store.Get(multi)!, [0, null, null], kit.Bot, 0b11));
        Assert.True(InsideCustomCast(kit, kit.System.Store.Get(multi)!, [0, null, null], kit.Bot, 0b01));
        // A die of more than one side is still rolled on top of the explicit value.
        Assert.False(InsideCustomCast(kit, kit.System.Store.Get(dice)!, [0, null, null], kit.Bot, 1));
    }

    /// <summary>A sink that heals and damages nothing.</summary>
    private sealed class NoHealSink : IDamageSink
    {
        public uint DealSpellDamage(Unit caster, Unit victim, SpellInfo spell, uint damage, bool periodic) => 0;

        public uint Heal(Unit caster, Unit target, SpellInfo spell, uint amount) => 0;
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
    public void ADebuffRejectedByTheSixteenVisibleSlotLimit_MarksOutcomeSlotLimited_AndIsNotNoEffect()
    {
        const uint deepWound = 12721;
        const uint firstCurse = 990_200;
        SpellInfo[] curses = Enumerable.Range(0, 16)
            .Select(i => QaSpellKit.Spell(firstCurse + (uint)i, QaSpellKit.Effect(SpellEffectName.ApplyAura, -1 - i, SpellImplicitTarget.UnitEnemy, AuraType.ModResistance)) with
            {
                Duration = new SpellDuration(60_000, 0, 60_000),
                RangeIndex = 4,
                Range = new SpellRange(0, 30),
                SpellVisual = 1,
                Attributes = SpellAttributes.AuraIsDebuff,
            })
            .ToArray();
        SpellInfo deepWoundSpell = QaSpellKit.Spell(deepWound, QaSpellKit.Effect(SpellEffectName.ApplyAura, 10, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicDamage)) with
        {
            Duration = new SpellDuration(12_000, 0, 12_000),
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            SpellVisual = 1,
            Attributes = SpellAttributes.AuraIsDebuff,
        };

        using var kit = new QaSpellKit(extra: [.. curses, deepWoundSpell]);
        var capture = new CastCapture();
        kit.System.RegisterObserver(capture);

        foreach (SpellInfo curse in curses)
        {
            Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(kit.Human, curse.Id, SpellCastTargets.ForUnit(kit.Human.Guid), triggered: true));
        }

        Assert.Equal(16, kit.System.GetAuras(kit.Human).Count(h => !h.IsPositive));

        for (int i = 0; i < PlayerbotQaSpellMonitor.NoEffectRepeats + 1; i++)
        {
            Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(kit.Bot, deepWound, SpellCastTargets.ForUnit(kit.Human.Guid), triggered: true));
        }

        Assert.False(kit.System.HasAura(kit.Human, deepWound));
        Assert.DoesNotContain(kit.Signals, s => s.Kind == QaSignalKind.SpellNoEffect);
        Assert.True(capture.Outcome?.SlotLimited, "outcome should be marked SlotLimited when the 16-slot visible debuff cap refuses the holder");
    }

    [Fact]
    public void ShieldBreakingHit_WithZeroDealtAndPositiveAbsorbed_AfterShieldAuraRemoved_IsNotCountedAsNoEffect()
    {
        const uint shoot = 5019;
        SpellInfo shootSpell = QaSpellKit.Spell(shoot, QaSpellKit.Effect(SpellEffectName.SchoolDamage, 50, SpellImplicitTarget.UnitEnemy)) with
        {
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
        };
        using var kit = new QaSpellKit(extra: [shootSpell]);
        kit.System.Damage = new MapCombatDamageSink();
        kit.World.GetMap(0).Combat.SpellMitigation = kit.System;
        var capture = new CastCapture();
        kit.System.RegisterObserver(capture);

        for (int i = 0; i < PlayerbotQaSpellMonitor.NoEffectRepeats; i++)
        {
            // Apply a 50-point shield to Human; a 50-damage Shoot consumes the entire shield and removes the aura inside AbsorbDamage before OnTargetOutcome runs.
            Assert.Equal(SpellCastResult.CastOk, kit.Cast(kit.Human, QaSpellKit.SelfAbsorb));
            Assert.True(kit.System.HasAura(kit.Human, QaSpellKit.SelfAbsorb));
            Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(kit.Bot, shoot, SpellCastTargets.ForUnit(kit.Human.Guid), triggered: false));
            Assert.False(kit.System.HasAura(kit.Human, QaSpellKit.SelfAbsorb), "the shield broke and was removed before OnTargetOutcome");
            Assert.Equal((0u, 50u), (capture.Outcome!.Value.Damage, capture.Outcome!.Value.Absorbed));
        }

        for (int i = 0; i < PlayerbotQaSpellMonitor.NoEffectRepeats; i++)
        {
            kit.Monitor.OnTargetOutcome(capture.Last!, new SpellTargetOutcome(kit.Human, SpellMissInfo.None, Damage: 0, Healing: 0, Critical: false, EffectMask: 1, Absorbed: 120));
        }

        Assert.DoesNotContain(kit.Signals, s => s.Kind == QaSignalKind.SpellNoEffect);
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
