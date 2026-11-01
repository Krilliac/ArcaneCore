using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Procs;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.Diagnostics;

/// <summary>
/// The stack guards (<see cref="StackGuard"/>) on the real recursive paths: spell.proc, spell.cast-chain, spell.aura-apply, spell.aura-remove,
/// dbscript.relay, eventai.process and smartai.action. A real stack overflow ends the process, so every scenario runs on a thread with a small
/// stack. Two kinds of fixture, both through the production entry points:
/// <list type="bullet">
/// <item>Margin fixtures (one per site): the stack is used up until the runtime's safety margin is all that is left, then the real entry point is
/// called. RED: with the guard suspended on that thread the path runs on (the call proceeds on the last of the stack). GREEN: with the guard on, the
/// call is dropped and one <see cref="InsufficientExecutionStackException"/> is recorded under the site. The proc, SmartAI and EventAI paths are
/// capped at a few levels by their own depth limits (MaxProcDepth, MaxActionDepth), far below the stack, so a margin fixture is the only way to
/// reach their guard.</item>
/// <item>Recursion fixtures (aura apply, aura remove, DB script relay chain): the real nested path recurses until the guard cuts it, and the
/// fault is recorded. Without the guard these would end the process, so there is no in-process RED for them; the margin fixtures carry the RED.</item>
/// </list>
/// </summary>
public sealed class StackGuardPathTests
{
    private const int SmallStackBytes = 512 * 1024;
    private const int PadBytes = 2048;
    private const int BurnBytes = 48 * 1024;

    private const uint AuraSpell = 949001;
    private const uint CastSpell = 949002;
    private const uint ProcSpell = 949003;
    private const uint ChainBase = 949100;
    private const int ChainLength = 20;
    private const uint RelayBase = 948500;
    private const int RelayChainLength = 2000;
    private const uint EmoteDance = 10;

    // --- the guard itself ---------------------------------------------------------------------------------

    [Fact]
    public void ASelfRecursingChain_StopsAtTheGuard_AndRecordsOneFaultSite()
    {
        var ledger = new FaultLedger();
        int depth = 0;
        OnSmallStack(() => depth = Recurse(ledger, "test.stack-guard", 0));

        Assert.True(depth > 10, "the guard must allow ordinary nesting before it cuts");
        FaultSnapshot fault = Assert.Single(ledger.Snapshot());
        Assert.Equal("test.stack-guard", fault.Site);
        Assert.EndsWith(nameof(InsufficientExecutionStackException), fault.ExceptionType, StringComparison.Ordinal);
        Assert.True(fault.Count >= 1);
    }

    [Fact]
    public void WithRoomLeft_TheGuardAnswersTrue_AndRecordsNothing()
    {
        var ledger = new FaultLedger();

        Assert.True(StackGuard.TryEnter("test.stack-guard", ledger));
        Assert.Empty(ledger.Snapshot());
    }

    [Fact]
    public void ASuspendedGuard_AnswersTrue_OnlyOnItsOwnThread_AndOnlyUntilDisposed()
    {
        var ledger = new FaultLedger();
        bool answeredWhileSuspended = false;
        bool answeredAfter = true;
        bool reached = false;
        OnSmallStack(() =>
        {
            using (StackGuard.Suspend())
            {
                UntilExhausted(() => answeredWhileSuspended = StackGuard.TryEnter("test.stack-guard", ledger));
            }

            reached = UntilExhausted(() => answeredAfter = StackGuard.TryEnter("test.stack-guard", ledger));
        });

        Assert.True(reached);
        Assert.True(answeredWhileSuspended);
        Assert.False(answeredAfter);
        Assert.Equal(1L, ledger.Snapshot().Sum(s => s.Count));
    }

    // --- one margin fixture per guarded site (RED then GREEN) ------------------------------------------------

    [Fact]
    public void SpellProc_AtTheStackMargin_IsCutAndRecorded() => AssertCutAtTheMargin("spell.proc", ProcChain);

    [Fact]
    public void SpellCastChain_AtTheStackMargin_IsCutAndRecorded() => AssertCutAtTheMargin("spell.cast-chain", CastChain);

    [Fact]
    public void SpellAuraApply_AtTheStackMargin_IsCutAndRecorded() => AssertCutAtTheMargin("spell.aura-apply", AuraApply);

    [Fact]
    public void SpellAuraRemove_AtTheStackMargin_IsCutAndRecorded() => AssertCutAtTheMargin("spell.aura-remove", AuraRemove);

    [Fact]
    public void DbScriptRelay_AtTheStackMargin_IsCutAndRecorded() => AssertCutAtTheMargin("dbscript.relay", RelayEmote);

    [Fact]
    public void EventAiProcess_AtTheStackMargin_IsCutAndRecorded() => AssertCutAtTheMargin("eventai.process", EventAiPhase);

    [Fact]
    public void SmartAiAction_AtTheStackMargin_IsCutAndRecorded() => AssertCutAtTheMargin("smartai.action", SmartAiPhase);

    private static void AssertCutAtTheMargin(string site, Func<Probe> build)
    {
        // Run every code path once on a normal stack first (JIT, type loads, exception machinery), so the margin run measures stack, not first use.
        WarmUpFaultRecording();
        using (Probe warm = build())
        {
            warm.Invoke();
            Assert.True(warm.PathRan(), "the scenario must run the path to its end when the stack is fine");
        }

        // RED: nothing stops the path. It runs on, on the last of the stack: this is the call that would overflow had it needed more.
        using (Probe red = build())
        {
            bool marginGone = false;
            OnSmallStack(() =>
            {
                using StackGuard.SuspendScope scope = StackGuard.Suspend();
                marginGone = UntilExhausted(red.Invoke);
            });

            Assert.True(marginGone, "the call must be made with the runtime's stack margin used up");
            Assert.True(red.PathRan(), $"RED: without the guard the path of {site} runs on at the stack margin");
        }

        // GREEN: the guard on the real path cuts the call and records one fault under the site.
        using (Probe green = build())
        {
            long before = FaultCount(site);
            bool marginGone = false;
            OnSmallStack(() => marginGone = UntilExhausted(green.Invoke));

            Assert.True(marginGone);
            Assert.False(green.PathRan(), $"GREEN: the guard of {site} must drop the call");
            Assert.True(FaultCount(site) > before, $"a fault must be recorded under {site}");
            Assert.Contains(FaultLedger.Shared.Snapshot(), s => s.Site == site
                && s.ExceptionType.EndsWith(nameof(InsufficientExecutionStackException), StringComparison.Ordinal));
        }
    }

    // --- recursion fixtures: the real nested path, cut by the guard ---------------------------------------------

    [Fact]
    public void NestedAuraRemovals_RecurseOnTheRealPath_UntilTheGuardCuts()
    {
        using SpellTestKit kit = Kit();
        Player player = Caster(kit, [.. ChainSpells()]);
        SpellAuraHolder[] holders = [.. ChainSpells().Select(id => CastAura(kit, player, id))];
        kit.System.HolderRemoved += removed =>
        {
            int next = (int)(removed.Spell.Id - ChainBase) + 1;
            if (next < holders.Length)
            {
                Burn(() => kit.System.RemoveAuras(player, ChainBase + (uint)next));
            }
        };
        long before = FaultCount("spell.aura-remove");

        OnSmallStack(() => kit.System.RemoveAuras(player, ChainBase));

        Assert.True(holders[0].IsRemoved);
        Assert.Contains(holders, h => !h.IsRemoved); // the chain was cut before its end
        Assert.Contains(holders, h => h.IsRemoved && h != holders[0]); // and it did recurse through the real path first
        Assert.True(FaultCount("spell.aura-remove") > before);
        Assert.All(holders.Where(h => !h.IsRemoved), h => Assert.Contains(h, kit.System.GetAuras(player))); // a cut holder stays attached
    }

    [Fact]
    public void NestedAuraApplies_RecurseOnTheRealPath_UntilTheGuardCuts()
    {
        using SpellTestKit kit = Kit();
        Player player = Caster(kit);
        SpellAuraHolder[] holders = [.. ChainSpells().Select(id => NewHolder(kit, player, id))];
        kit.System.HolderAdded += added =>
        {
            int next = (int)(added.Spell.Id - ChainBase) + 1;
            if (next < holders.Length)
            {
                Burn(() => kit.System.AddAuraHolder(holders[next]));
            }
        };
        long before = FaultCount("spell.aura-apply");

        OnSmallStack(() => kit.System.AddAuraHolder(holders[0]));

        Assert.False(holders[0].IsRemoved);
        Assert.Contains(holders[0], kit.System.GetAuras(player));
        Assert.Contains(holders, h => h.IsRemoved); // dropped by the guard: marked removed, never attached
        Assert.DoesNotContain(holders.Where(h => h.IsRemoved), h => kit.System.GetAuras(player).Contains(h));
        Assert.Contains(holders[1], kit.System.GetAuras(player)); // and it did recurse through the real path first
        Assert.True(FaultCount("spell.aura-apply") > before);
    }

    [Fact]
    public void AChainOfRelayScripts_StartingTheNext_RecursesOnTheRealPath_UntilTheGuardCuts()
    {
        // Relay i runs START_RELAY_SCRIPT (45) for relay i + 1 with no delay, so each level runs the next one inside its own step.
        IEnumerable<RelayScriptStep> steps = Enumerable.Range(0, RelayChainLength)
            .Select(i => Step(RelayBase + (uint)i, command: 45, dataLong: RelayBase + (uint)i + 1));
        using Town town = BuildTown(steps);
        long before = FaultCount("dbscript.relay");

        OnSmallStack(() => Assert.True(town.System.StartRelayScript(RelayBase, town.Creature, town.Player)));

        Assert.True(FaultCount("dbscript.relay") > before);
    }

    // --- scenarios ------------------------------------------------------------------------------------------

    private sealed class Probe(Action invoke, Func<bool> pathRan, IDisposable owner) : IDisposable
    {
        public void Invoke() => invoke();

        public bool PathRan() => pathRan();

        public void Dispose() => owner.Dispose();
    }

    private static Probe ProcChain()
    {
        SpellTestKit kit = Kit();
        (Player actor, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 3, 0);
        bool processed = false;
        kit.System.ProcEventProcessed += (_, _) => processed = true;
        var hit = new ProcEvent
        {
            Victim = victim,
            AttackerFlags = ProcFlags.DealHarmfulSpell,
            VictimFlags = ProcFlags.TakeHarmfulSpell,
            Extra = ProcFlagsEx.NormalHit,
            Amount = 10,
            OriginalAmount = 10,
            ProcSpell = kit.Store.Get(ProcSpell),
        };

        return new Probe(() => kit.System.ProcDamageAndSpell(actor, hit), () => processed, kit);
    }

    private static Probe CastChain()
    {
        SpellTestKit kit = Kit();
        Player player = Caster(kit, CastSpell);
        SpellCastResult result = SpellCastResult.Error;

        return new Probe(() => result = kit.System.HandleCastRequest(player, CastSpell, SpellCastTargets.ForSelf()),
            () => result == SpellCastResult.CastOk, kit);
    }

    private static Probe AuraApply()
    {
        SpellTestKit kit = Kit();
        Player player = Caster(kit);
        SpellAuraHolder holder = NewHolder(kit, player, AuraSpell);

        return new Probe(() => kit.System.AddAuraHolder(holder), () => !holder.IsRemoved && kit.System.GetAuras(player).Contains(holder), kit);
    }

    private static Probe AuraRemove()
    {
        SpellTestKit kit = Kit();
        Player player = Caster(kit, AuraSpell);
        SpellAuraHolder holder = CastAura(kit, player, AuraSpell);

        return new Probe(() => kit.System.RemoveAuras(player, AuraSpell), () => holder.IsRemoved, kit);
    }

    private sealed record Town(WorldRuntime World, CreatureMapSystem System, Creature Creature, Player Player) : IDisposable
    {
        public void Dispose() => World.Dispose();
    }

    private static RelayScriptStep Step(uint id, uint command, uint dataLong = 0, uint flags = 0)
        => new(id, 0, 0, command, dataLong, 0, 0, 0, 0, flags, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static Town BuildTown(IEnumerable<RelayScriptStep> steps)
    {
        var ai = new CreatureAiContent(Array.Empty<CreatureAiEvent>(), [])
        {
            RelayScripts = new RelayScriptCatalog(steps, []),
        };
        CreatureContent content = new([Template() with { Civilian = true }], [Spawn(1, WolfEntry, 0, 0)], [], [], [], ai);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Spells = new FakeCaster() });
        (Player player, _) = AddPlayer(world, 1, 0, 10);
        Creature creature = system.Creatures.Single(c => c.Template.Entry == WolfEntry);
        return new Town(world, system, creature, player);
    }

    private static Probe RelayEmote()
    {
        // The Elly Langston pattern of RelayScriptTests: EMOTE (1) with data_flags 2 on the relay's source creature.
        Town town = BuildTown([Step(RelayBase, command: 1, dataLong: EmoteDance, flags: 2)]);

        return new Probe(() => town.System.StartRelayScript(RelayBase, town.Creature, town.Player),
            () => town.Creature.GetUInt32(UpdateFields.UnitNpcEmotestate) == EmoteDance, town);
    }

    private static Probe EventAiPhase()
    {
        CreatureAiEvent row = new()
        {
            Id = 1,
            CreatureId = WolfEntry,
            EventType = (byte)EventAiEventType.Aggro,
            Flags = 0,
            Chance = 100,
            Action1 = new CreatureAiAction((byte)EventAiActionType.SetPhase, 4, 0, 0),
        };
        CreatureContent content = new(
            [Template(configure: t => t.AIName = CreatureAiFactory.EventAIName)], [Spawn(1, WolfEntry, 5, 0)], [], [], [],
            new CreatureAiContent([row], []));
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Spells = new FakeCaster() });
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        var ai = (CreatureEventAI)Assert.Single(system.Creatures).AI!;

        return new Probe(() => ai.OnAggro(player), () => ai.Phase == 4, world);
    }

    private static Probe SmartAiPhase()
    {
        SmartScriptRow row = new()
        {
            EntryOrGuid = (int)WolfEntry,
            Id = 0,
            EventType = (byte)SmartEvent.Aggro,
            EventChance = 100,
            ActionType = (byte)SmartAction.SetEventPhase,
            ActionParam1 = 3,
            TargetType = (byte)SmartTarget.Self,
        };
        var content = new CreatureContent(
            [Template(configure: t => t.AIName = CreatureAiFactory.SmartAIName)],
            [Spawn(1, WolfEntry, 5, 0)], [], [], [],
            new CreatureAiContent([], [], new BroadcastTextCatalog([new BroadcastText(9001, "Grr, $N!", "", 0, 0, 0, [0, 0, 0], [0, 0, 0])]),
                [], EventAiDialect.CMangos, [])
            { SmartScripts = new SmartScriptCatalog([row]) });
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Spells = new FakeCaster() });
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        Creature wolf = system.Creatures.Single(c => c.Entry == WolfEntry);
        var ai = Assert.IsType<CreatureSmartAI>(wolf.AI);

        return new Probe(() => ai.OnAggro(player), () => ai.Script.Phase == 3u, world);
    }

    // --- spell fixtures -----------------------------------------------------------------------------------------

    private static IEnumerable<uint> ChainSpells() => Enumerable.Range(0, ChainLength).Select(i => ChainBase + (uint)i);

    private static SpellInfo Instant(uint id, SpellEffectInfo effect) => SpellTestKit.Spell(id, effect) with
    {
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static SpellInfo SelfHot(uint id) => SpellTestKit.Spell(id,
        SpellTestKit.Effect(SpellEffectName.ApplyAura, 5, SpellImplicitTarget.UnitCaster, AuraType.PeriodicHeal, amplitude: 1000)) with
    {
        Duration = new SpellDuration(60_000, 0, 60_000),
        SpellVisual = 1,
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
        StackAmount = 0,
    };

    private static SpellTestKit Kit() => new([
        SelfHot(AuraSpell),
        Instant(CastSpell, SpellTestKit.Effect(SpellEffectName.Heal, 20)),
        Instant(ProcSpell, SpellTestKit.Effect(SpellEffectName.Heal, 1)),
        .. ChainSpells().Select(SelfHot)]);

    private static Player Caster(SpellTestKit kit, params uint[] spells)
    {
        (Player player, _) = kit.AddPlayer(1);
        kit.Spellbook.Teach(player, spells);
        kit.World.RunTick(0);
        return player;
    }

    private static SpellAuraHolder CastAura(SpellTestKit kit, Player player, uint spell)
    {
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(player, spell, SpellCastTargets.ForSelf()));
        kit.Advance(kit.System.SpellBatchWindowMs); // with spell batching on, the held go resolves at the next boundary (0: no-op)
        return Assert.Single(kit.System.GetAuras(player), h => !h.IsRemoved && h.Spell.Id == spell);
    }

    private static SpellAuraHolder NewHolder(SpellTestKit kit, Player player, uint spellId)
    {
        SpellInfo spell = kit.Store.Get(spellId)!;
        var holder = new SpellAuraHolder(spell, player, player, new AuraCasterOwner(player), 60_000);
        holder.SetAura(new SpellAura(0, AuraType.PeriodicHeal, 5, 1000, 0, player.PowerType));
        return holder;
    }

    // --- stack helpers ------------------------------------------------------------------------------------------

    private static long FaultCount(string site) => FaultLedger.Shared.Snapshot().Where(s => s.Site == site).Sum(s => s.Count);

    private static void WarmUpFaultRecording()
    {
        try
        {
            throw new InsufficientExecutionStackException("warm-up");
        }
        catch (InsufficientExecutionStackException ex)
        {
            new FaultLedger().Record("test.stack-guard-warmup", ex); // a private ledger: the shared one keeps only real sites
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Recurse(FaultLedger ledger, string site, int depth)
    {
        if (!StackGuard.TryEnter(site, ledger))
        {
            return depth;
        }

        return Recurse(ledger, site, depth + 1);
    }

    /// <summary>
    /// Use up the stack of the calling thread, 2 KB a frame, until <see cref="RuntimeHelpers.TryEnsureSufficientExecutionStack"/> says only the
    /// runtime's margin is left, then call <paramref name="action"/> there. True once the action was called with the margin gone.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool UntilExhausted(Action action)
    {
        if (RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            Span<byte> pad = stackalloc byte[PadBytes];
            pad[0] = 1;
            Keep(pad);
            bool reached = UntilExhausted(action);
            Keep(pad); // not a tail call: the frame stays
            return reached;
        }

        action();
        return true;
    }

    /// <summary>Use 48 KB of stack, then call <paramref name="next"/> (a nested step of a recursion fixture).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Burn(Action next)
    {
        Span<byte> pad = stackalloc byte[BurnBytes];
        pad[0] = 1;
        Keep(pad);
        next();
        Keep(pad);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Keep(Span<byte> pad) => pad[0]++;

    /// <summary>Run <paramref name="action"/> on a thread with a small stack and wait for it; a failure on that thread is rethrown here.</summary>
    private static void OnSmallStack(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failure = ex;
            }
        }, SmallStackBytes)
        {
            IsBackground = true,
        };
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(60)))
        {
            throw new TimeoutException("the recursion did not end within 60 s");
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
