using System.Reflection;
using System.Reflection.Emit;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Contracts;
using ArcaneCore.World.Features;
using ArcaneCore.World.HotCode;
using ArcaneCore.World.Tests.HotCode;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.FeatureLifecycle;

/// <summary>
/// Tier 2a lifecycle (docs/design/hot-reload-tiers.md, section 4.2): a detachable feature with subscriptions on the world
/// and the spell system is detached and re-attached in process through <see cref="FeatureHost"/>; the subscriptions are
/// gone after detach and back after attach, pinned features refuse, and a failure part-way through is rolled back.
/// </summary>
public sealed class FeatureLifecycleTests : IDisposable
{
    private readonly WorldRuntime _world = new(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, new NullSaveQueue(), NullLogger<WorldRuntime>.Instance);
    private readonly SpellSystem _spells = new(SpellStore.Empty, () => 0);
    private readonly GatedWorld _poster = new();
    private readonly RecordingReporter _reporter = new();
    private readonly string _auditPath = Path.Combine(Path.GetTempPath(), "arcane-feature-audit-" + Guid.NewGuid().ToString("N") + ".log");
    private readonly FeatureHost _host;

    public FeatureLifecycleTests()
    {
        var audit = new HotCodeAudit(_auditPath);
        _host = new FeatureHost(_poster, () => audit, () => _reporter, commitTimeout: TimeSpan.FromSeconds(10), quiesceTimeout: TimeSpan.FromSeconds(10));
    }

    public void Dispose()
    {
        _poster.Dispose();
        _world.Dispose();
        try
        {
            File.Delete(_auditPath);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    [Fact]
    public async Task DetachRemovesEverySubscription_AttachRestoresThemFromTheHandover()
    {
        var probe = new ProbeFeature(_spells, DetachKind.Handover);
        _host.AttachAll(_world, [probe]);
        _world.RunTick(50);
        Assert.Equal(1, probe.Ticks);
        Assert.Single(_spells.Observers, o => ReferenceEquals(o, probe.Observer));
        Assert.Equal(3, _host.Find(nameof(ProbeFeature))!.Subscriptions);

        FeatureResult detached = await _host.DetachAsync(nameof(ProbeFeature));

        Assert.True(detached.Ok, detached.Detail);
        Assert.True(probe.Quiesced);
        _world.RunTick(50);
        Assert.Equal(1, probe.Ticks); // the Updated handler is gone
        Assert.DoesNotContain(_spells.Observers, o => ReferenceEquals(o, probe.Observer));
        FeatureStatus status = _host.Find(nameof(ProbeFeature))!;
        Assert.Equal(FeatureState.Detached, status.State);
        Assert.Equal(0, status.Subscriptions);
        Assert.Equal(1, status.HandoverValues);
        Assert.True(_host.IsDetached(probe));

        FeatureResult attached = await _host.AttachAsync(nameof(ProbeFeature));

        Assert.True(attached.Ok, attached.Detail);
        Assert.Equal(1, probe.RestoredTicks);
        _world.RunTick(50);
        Assert.Equal(2, probe.Ticks);
        Assert.Single(_spells.Observers, o => ReferenceEquals(o, probe.Observer));
        Assert.Equal(FeatureState.Attached, _host.Find(nameof(ProbeFeature))!.State);
        Assert.Equal(3, _host.Find(nameof(ProbeFeature))!.Subscriptions);

        // Reported and audited like a module swap.
        Assert.Contains(_reporter.Events, e => e.Lane == HotSwapLane.Feature && e.Action == "detach" && e.Result == HotSwapResult.Applied);
        Assert.Contains(_reporter.Events, e => e.Lane == HotSwapLane.Feature && e.Action == "attach" && e.Result == HotSwapResult.Applied);
        HotCodeAudit.FlushAll(); // the audit lines are written by a background appender
        string audit = await File.ReadAllTextAsync(_auditPath);
        Assert.Contains("\tfeature\tconsole detach ProbeFeature: applied", audit, StringComparison.Ordinal);
        Assert.Contains("\tfeature\tconsole attach ProbeFeature: applied", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DetachAndAttach_RunOnTheWorldThread()
    {
        var probe = new ProbeFeature(_spells, DetachKind.Handover);
        _host.AttachAll(_world, [probe]);

        Assert.True((await _host.DetachAsync(nameof(ProbeFeature))).Ok);
        Assert.True((await _host.AttachAsync(nameof(ProbeFeature))).Ok);

        Assert.Equal(_poster.ThreadId, probe.DetachThread);
        Assert.Equal(_poster.ThreadId, probe.AttachThread);
        Assert.NotEqual(_poster.ThreadId, probe.QuiesceThread);
    }

    [Fact]
    public async Task Cycle_DetachesAndAttachesInOneCommit()
    {
        var probe = new ProbeFeature(_spells, DetachKind.Handover);
        _host.AttachAll(_world, [probe]);
        _world.RunTick(50);
        int postsBefore = _poster.Posts;

        FeatureResult cycled = await _host.CycleAsync(nameof(ProbeFeature));

        Assert.True(cycled.Ok, cycled.Detail);
        Assert.Equal(postsBefore + 1, _poster.Posts);
        Assert.Equal(1, probe.RestoredTicks);
        Assert.Equal(2, probe.Attaches);
        _world.RunTick(50);
        Assert.Equal(2, probe.Ticks);
        Assert.Single(_spells.Observers, o => ReferenceEquals(o, probe.Observer));
    }

    [Fact]
    public async Task Stateless_IsReattachedCold()
    {
        var probe = new ProbeFeature(_spells, DetachKind.Stateless);
        _host.AttachAll(_world, [probe]);
        _world.RunTick(50);

        Assert.True((await _host.DetachAsync(nameof(ProbeFeature))).Ok);
        Assert.Equal(0, _host.Find(nameof(ProbeFeature))!.HandoverValues);
        Assert.True((await _host.AttachAsync(nameof(ProbeFeature))).Ok);

        Assert.False(probe.LastAttachHadHandover);
        Assert.Null(probe.RestoredTicks);
    }

    [Fact]
    public async Task PinnedFeatures_RefuseDetach_AndKeepTheirSubscriptions()
    {
        var plain = new PlainFeature();
        var pinned = new ProbeFeature(_spells, DetachKind.Pinned);
        _host.AttachAll(_world, [plain, pinned]);

        FeatureResult plainResult = await _host.DetachAsync(nameof(PlainFeature));
        FeatureResult pinnedResult = await _host.DetachAsync(nameof(ProbeFeature));

        Assert.False(plainResult.Ok);
        Assert.Equal(HotSwapResult.Rejected, plainResult.Result);
        Assert.Contains("pinned", plainResult.Detail, StringComparison.Ordinal);
        Assert.False(pinnedResult.Ok);
        Assert.Equal(HotSwapResult.Rejected, pinnedResult.Result);
        Assert.False(pinned.Quiesced);
        Assert.Equal(0, pinned.Detaches);
        _world.RunTick(50);
        Assert.Equal(1, plain.Ticks);
        Assert.Equal(1, pinned.Ticks);
        Assert.Single(_spells.Observers, o => ReferenceEquals(o, pinned.Observer));
        Assert.Equal(DetachKind.Pinned, _host.Find(nameof(PlainFeature))!.Kind);
        Assert.Equal(FeatureState.Attached, _host.Find(nameof(ProbeFeature))!.State);
        Assert.Contains(_reporter.Events, e => e.Lane == HotSwapLane.Feature && e.Result == HotSwapResult.Rejected);
    }

    [Fact]
    public async Task FailureMidDetach_RollsBack_ReattachedFromTheSameHandover_WithNoDuplicateSubscriptions()
    {
        var probe = new ProbeFeature(_spells, DetachKind.Handover);
        _host.AttachAll(_world, [probe]);
        _world.RunTick(50);
        probe.ThrowInDetach = true; // writes the handover, then throws

        FeatureResult result = await _host.DetachAsync(nameof(ProbeFeature));

        Assert.False(result.Ok);
        Assert.Equal(HotSwapResult.Failed, result.Result);
        Assert.Contains("rolled back", result.Detail, StringComparison.Ordinal);
        Assert.Equal(FeatureState.Attached, _host.Find(nameof(ProbeFeature))!.State);
        Assert.False(_host.IsDetached(probe));
        Assert.Equal(1, probe.RestoredTicks); // re-attached from the handover Detach wrote before it threw
        Assert.Equal(3, _host.Find(nameof(ProbeFeature))!.Subscriptions);
        _world.RunTick(50);
        Assert.Equal(2, probe.Ticks); // exactly one Updated handler: the old one was removed before the re-attach
        Assert.Single(_spells.Observers, o => ReferenceEquals(o, probe.Observer));
        Assert.NotNull(_host.Find(nameof(ProbeFeature))!.LastError);
    }

    [Fact]
    public async Task FailingUnsubscribe_RollsBackTheDetach()
    {
        var probe = new ProbeFeature(_spells, DetachKind.Handover) { ExtraFailingUndo = true };
        _host.AttachAll(_world, [probe]);

        FeatureResult result = await _host.DetachAsync(nameof(ProbeFeature));

        Assert.False(result.Ok);
        Assert.Contains("rolled back", result.Detail, StringComparison.Ordinal);
        Assert.Equal(FeatureState.Attached, _host.Find(nameof(ProbeFeature))!.State);
        _world.RunTick(50);
        Assert.Equal(1, probe.Ticks);
        Assert.Single(_spells.Observers, o => ReferenceEquals(o, probe.Observer));
    }

    [Fact]
    public async Task FailingAttach_UndoesWhatItSubscribed_AndStaysDetachedWithItsHandover()
    {
        var probe = new ProbeFeature(_spells, DetachKind.Handover);
        _host.AttachAll(_world, [probe]);
        _world.RunTick(50);
        Assert.True((await _host.DetachAsync(nameof(ProbeFeature))).Ok);
        probe.ThrowInAttach = true; // subscribes everything, then throws

        FeatureResult failed = await _host.AttachAsync(nameof(ProbeFeature));

        Assert.False(failed.Ok);
        Assert.Equal(HotSwapResult.Failed, failed.Result);
        FeatureStatus status = _host.Find(nameof(ProbeFeature))!;
        Assert.Equal(FeatureState.Detached, status.State);
        Assert.Equal(1, status.HandoverValues);
        _world.RunTick(50);
        Assert.Equal(1, probe.Ticks);
        Assert.DoesNotContain(_spells.Observers, o => ReferenceEquals(o, probe.Observer));

        probe.ThrowInAttach = false;
        Assert.True((await _host.AttachAsync(nameof(ProbeFeature))).Ok);
        _world.RunTick(50);
        Assert.Equal(2, probe.Ticks);
        Assert.Single(_spells.Observers, o => ReferenceEquals(o, probe.Observer));
    }

    [Fact]
    public async Task FailingQuiesce_DetachesNothing_AndRestartsTheFeature()
    {
        var probe = new ProbeFeature(_spells, DetachKind.Handover) { ThrowInQuiesce = true };
        _host.AttachAll(_world, [probe]);

        FeatureResult result = await _host.DetachAsync(nameof(ProbeFeature));

        Assert.False(result.Ok);
        Assert.Contains("nothing was detached", result.Detail, StringComparison.Ordinal);
        Assert.Equal(FeatureState.Attached, _host.Find(nameof(ProbeFeature))!.State);
        Assert.Equal(2, probe.Attaches);
        _world.RunTick(50);
        Assert.Equal(1, probe.Ticks);
        Assert.Single(_spells.Observers, o => ReferenceEquals(o, probe.Observer));
    }

    [Fact]
    public async Task CommitTheWorldThreadNeverRuns_LeavesTheFeatureQuiescedButAttached_AndAttachRestartsIt()
    {
        var host = new FeatureHost(_poster, commitTimeout: TimeSpan.FromMilliseconds(200));
        var probe = new ProbeFeature(_spells, DetachKind.Handover);
        host.AttachAll(_world, [probe]);
        _poster.Dropping = true;

        FeatureResult result = await host.DetachAsync(nameof(ProbeFeature));

        Assert.False(result.Ok);
        Assert.Equal(FeatureState.Quiesced, host.Find(nameof(ProbeFeature))!.State);
        Assert.Equal(0, probe.Detaches);
        _world.RunTick(50);
        Assert.Equal(1, probe.Ticks); // still attached

        _poster.Dropping = false;
        FeatureResult restarted = await host.AttachAsync(nameof(ProbeFeature));

        Assert.True(restarted.Ok, restarted.Detail);
        Assert.Equal(FeatureState.Attached, host.Find(nameof(ProbeFeature))!.State);
        _world.RunTick(50);
        Assert.Equal(2, probe.Ticks);
        Assert.Single(_spells.Observers, o => ReferenceEquals(o, probe.Observer));
    }

    [Fact]
    public async Task UnknownFeature_AndWrongState_AreRejected()
    {
        var probe = new ProbeFeature(_spells, DetachKind.Handover);
        Assert.Equal(HotSwapResult.Rejected, (await _host.DetachAsync(nameof(ProbeFeature))).Result); // not attached yet
        _host.AttachAll(_world, [probe]);

        Assert.Equal(HotSwapResult.Rejected, (await _host.DetachAsync("NoSuchFeature")).Result);
        Assert.Equal(HotSwapResult.Rejected, (await _host.AttachAsync(nameof(ProbeFeature))).Result); // already attached
        Assert.True((await _host.DetachAsync(nameof(ProbeFeature))).Ok);
        Assert.Equal(HotSwapResult.Rejected, (await _host.DetachAsync(nameof(ProbeFeature))).Result); // already detached
        Assert.Throws<InvalidOperationException>(() => _host.AttachAll(_world, []));
    }

    [Fact]
    public async Task ListAndFormat_ShowDetachableFeaturesAndCountPinnedOnes()
    {
        Assert.Equal("No detachable features; 1 features are pinned.", FeatureCommands.Format([new FeatureStatus("A", "X.A", DetachKind.Pinned, FeatureState.Attached, 0, 0, null, null)]));
        var probe = new ProbeFeature(_spells, DetachKind.Handover);
        _host.AttachAll(_world, [new PlainFeature(), probe]);
        Assert.True((await _host.DetachAsync(nameof(ProbeFeature))).Ok);

        string text = FeatureCommands.Format(_host.List());

        Assert.StartsWith("ProbeFeature: handover, detached, 0 subscriptions, handover 1 values, changed ", text, StringComparison.Ordinal);
        Assert.EndsWith("\n1 other features are pinned.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AddWorldFeatures_AttachesThroughTheHost_AndRegistersTheSeams()
    {
        using ServiceProvider services = new ServiceCollection().AddLogging().AddWorldFeatures().BuildServiceProvider();

        Assert.Same(services.GetRequiredService<TeleportWaiverRegistry>(), services.GetRequiredService<ITeleportWaiverRegistry>());
        Assert.Same(services.GetRequiredService<WorldLifecycleHooks>(), services.GetRequiredService<IWorldLifecycleHooks>());
        Assert.Contains(typeof(TeleportWaiverRegistry), WorldFeatures.FeatureTypes);
        Assert.NotNull(services.GetRequiredService<FeatureHost>());
    }

    [Fact]
    public void ContractsVersion_AcceptsOnlyTheCurrentVersion()
    {
        Assert.True(ContractsVersion.IsCompatible(ContractsVersion.Current));
        Assert.False(ContractsVersion.IsCompatible(ContractsVersion.Current + 1));
        Assert.Equal(ContractsVersion.Current, new FeatureHandover().ContractsVersion);
        Assert.Equal("ArcaneCore.World.Contracts", typeof(IWorldFeature).Assembly.GetName().Name);
        Assert.Equal("ArcaneCore.World.Contracts", typeof(IDetachableWorldFeature).Assembly.GetName().Name);
    }

    /// <summary>A plain (pinned) feature.</summary>
    internal sealed class PlainFeature : IWorldFeature
    {
        public int Ticks { get; private set; }

        public void Attach(WorldRuntime world) => world.Updated += _ => Ticks++;
    }

    /// <summary>A detachable feature with an Updated handler, a login handler and a spell observer, all through the bag.</summary>
    internal sealed class ProbeFeature(SpellSystem spells, DetachKind kind) : IDetachableWorldFeature
    {
        public ISpellCastObserver Observer { get; } = new ProbeObserver();

        public DetachKind DetachKind => kind;

        public int Ticks { get; private set; }

        public int Attaches { get; private set; }

        public int Detaches { get; private set; }

        public bool Quiesced { get; private set; }

        public int? RestoredTicks { get; private set; }

        public bool LastAttachHadHandover { get; private set; }

        public bool ThrowInDetach { get; set; }

        public bool ThrowInAttach { get; set; }

        public bool ThrowInQuiesce { get; set; }

        public bool ExtraFailingUndo { get; set; }

        public int DetachThread { get; private set; }

        public int AttachThread { get; private set; }

        public int QuiesceThread { get; private set; }

        public Task QuiesceAsync(CancellationToken cancellationToken)
        {
            QuiesceThread = Environment.CurrentManagedThreadId;
            if (ThrowInQuiesce)
            {
                throw new InvalidOperationException("quiesce failed");
            }

            Quiesced = true;
            return Task.CompletedTask;
        }

        public void Detach(FeatureHandover handover)
        {
            DetachThread = Environment.CurrentManagedThreadId;
            Detaches++;
            handover.Set("ticks", Ticks);
            if (ThrowInDetach)
            {
                throw new InvalidOperationException("detach failed half-way");
            }
        }

        public void AttachFrom(WorldRuntime world, FeatureHandover? handover, FeatureSubscriptions subscriptions)
        {
            AttachThread = Environment.CurrentManagedThreadId;
            Attaches++;
            LastAttachHadHandover = handover is not null;
            if (handover is not null && handover.TryGet("ticks", out int ticks))
            {
                RestoredTicks = ticks;
            }

            subscriptions.OnUpdated(world, _ => Ticks++);
            subscriptions.OnPlayerLoggedIn(world, _ => { });
            subscriptions.Observe(spells, Observer);
            if (ExtraFailingUndo)
            {
                ExtraFailingUndo = false; // only the first attach gets an undo that throws
                subscriptions.Add(new ThrowingRegistration());
            }

            if (ThrowInAttach)
            {
                throw new InvalidOperationException("attach failed after subscribing");
            }
        }

        private sealed class ProbeObserver : ISpellCastObserver;

        private sealed class ThrowingRegistration : IDisposable
        {
            public void Dispose() => throw new InvalidOperationException("unsubscribe failed");
        }
    }

    /// <summary>A world thread that can stop picking up posts (a stalled tick).</summary>
    internal sealed class GatedWorld : IHotCodeWorld, IDisposable
    {
        private readonly ThreadedWorld _inner = new();
        private int _posts;

        public bool Dropping { get; set; }

        public int Posts => Volatile.Read(ref _posts);

        public int ThreadId => _inner.ThreadId;

        public void Post(Action command)
        {
            Interlocked.Increment(ref _posts);
            if (!Dropping)
            {
                _inner.Post(command);
            }
        }

        public void Dispose() => _inner.Dispose();
    }
}

/// <summary>The bag, the handover, the hooks, the waiver registry and <see cref="SpellSystem.UnregisterObserver"/>.</summary>
public sealed class FeatureSeamTests
{
    [Fact]
    public void Subscriptions_DisposeUndoesEverything_NewestFirst_AndRefusesLaterSubscribes()
    {
        var order = new List<string>();
        var bag = new FeatureSubscriptions("probe");
        Action<int>? target = null;
        Action<int> handler = _ => { };
        bag.Subscribe(handler, h => target += h, h => { target -= h; order.Add("event"); });
        bag.Add(new Undo(() => order.Add("registration")));
        Assert.Equal(2, bag.Count);
        Assert.NotNull(target);

        bag.Dispose();
        bag.Dispose(); // idempotent

        Assert.Null(target);
        Assert.Equal(new[] { "registration", "event" }, order);
        Assert.True(bag.IsDisposed);
        Assert.Equal(0, bag.Count);
        bool added = false;
        Assert.Throws<ObjectDisposedException>(() => bag.Subscribe(handler, h => added = true, h => { }));
        Assert.False(added);
        var late = new Undo(() => order.Add("late"));
        Assert.Throws<ObjectDisposedException>(() => bag.Add(late));
        Assert.Equal("late", order[^1]); // a registration handed to a disposed bag is undone at once
    }

    [Fact]
    public void Subscriptions_AFailingUndo_DoesNotStopTheOthers()
    {
        var bag = new FeatureSubscriptions("probe");
        int undone = 0;
        bag.Add(new Undo(() => undone++));
        bag.Add(new Undo(() => throw new InvalidOperationException("boom")));
        bag.Add(new Undo(() => undone++));

        AggregateException error = Assert.Throws<AggregateException>(bag.Dispose);

        Assert.Single(error.InnerExceptions);
        Assert.Equal(2, undone);
    }

    [Fact]
    public void Subscriptions_ObserveRegistersAndUnregistersTheSpellObserver()
    {
        var spells = new SpellSystem(SpellStore.Empty, () => 0);
        var observer = new NullObserver();
        var bag = new FeatureSubscriptions("probe");

        bag.Observe(spells, observer);
        Assert.Contains(observer, spells.Observers);
        bag.Dispose();

        Assert.DoesNotContain(observer, spells.Observers);
    }

    [Fact]
    public void UnregisterObserver_RemovesOnlyThatObserver_KeepingTheOrder()
    {
        var spells = new SpellSystem(SpellStore.Empty, () => 0);
        int before = spells.Observers.Count;
        var a = new NullObserver();
        var b = new NullObserver();
        var c = new NullObserver();
        spells.RegisterObserver(a);
        spells.RegisterObserver(b);
        spells.RegisterObserver(c);

        Assert.True(spells.UnregisterObserver(b));
        Assert.False(spells.UnregisterObserver(b));

        Assert.Equal(before + 2, spells.Observers.Count);
        Assert.Equal(new ISpellCastObserver[] { a, c }, spells.Observers.Skip(before));
        spells.RegisterObserver(b); // may be registered again
        Assert.Equal(new ISpellCastObserver[] { a, c, b }, spells.Observers.Skip(before));
    }

    [Fact]
    public void Handover_KeepsCoreValues_AndRefusesTypesFromCollectibleAssemblies()
    {
        var handover = new FeatureHandover();
        handover.Set("count", 7);
        handover.Set("names", new List<string> { "a" });

        Assert.True(handover.TryGet("count", out int count));
        Assert.Equal(7, count);
        Assert.False(handover.TryGet("count", out string? _)); // wrong type
        Assert.False(handover.TryGet("missing", out int _));
        Assert.Equal(2, handover.Count);

        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("PackProbe" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.RunAndCollect);
        Type packType = assembly.DefineDynamicModule("PackProbe").DefineType("PackState", TypeAttributes.Public | TypeAttributes.Class).CreateType();
        Assert.True(packType.Assembly.IsCollectible);
        object packValue = Activator.CreateInstance(packType)!;
        Array packArray = Array.CreateInstance(packType, 1);

        Assert.Throws<ArgumentException>(() => handover.Set("pack", packValue));
        Assert.Throws<ArgumentException>(() => handover.Set("packs", packArray));
        Assert.Equal(2, handover.Count);
    }

    [Fact]
    public async Task Hooks_RunInStageOrder_AllRunDespiteAFailure_AndDisposeRemoves()
    {
        var hooks = new WorldLifecycleHooks();
        var ran = new List<string>();
        using IDisposable a = hooks.Register(WorldLifecycleStage.AfterWorldStart, "a", _ => { ran.Add("start-a"); return Task.CompletedTask; });
        using IDisposable b = hooks.Register(WorldLifecycleStage.AfterWorldStart, "b", _ => { ran.Add("start-b"); return Task.CompletedTask; });
        IDisposable stopA = hooks.Register(WorldLifecycleStage.BeforeWorldStop, "a", _ => { ran.Add("stop-a"); return Task.CompletedTask; });
        using IDisposable stopB = hooks.Register(WorldLifecycleStage.BeforeWorldStop, "b", _ => throw new InvalidOperationException("stop-b failed"));
        Assert.Throws<InvalidOperationException>(() => hooks.Register(WorldLifecycleStage.AfterWorldStart, "a", _ => Task.CompletedTask));

        await hooks.RunAsync(WorldLifecycleStage.AfterWorldStart);
        await Assert.ThrowsAsync<AggregateException>(() => hooks.RunAsync(WorldLifecycleStage.BeforeWorldStop));

        Assert.Equal(new[] { "start-a", "start-b", "stop-a" }, ran);
        Assert.Equal(new[] { "b", "a" }, hooks.Registered(WorldLifecycleStage.BeforeWorldStop));

        stopA.Dispose();
        stopA.Dispose();
        Assert.Equal(new[] { "b" }, hooks.Registered(WorldLifecycleStage.BeforeWorldStop));
    }

    [Fact]
    public void WaiverRegistry_WaivesNothingByDefault_UnionsRegisteredWaivers_AndForgetsDisposedOnes()
    {
        var registry = new TeleportWaiverRegistry();
        Player player = null!;
        AreaTriggerTeleport teleport = null!;
        Assert.Equal(AreaTriggerWaivers.None, registry.Waives(player, teleport));

        IDisposable item = registry.Register(new FixedWaiver(AreaTriggerWaivers.Item));
        using IDisposable quest = registry.Register(new FixedWaiver(AreaTriggerWaivers.Quest));
        Assert.Equal(AreaTriggerWaivers.Item | AreaTriggerWaivers.Quest, registry.Waives(player, teleport));

        item.Dispose();

        Assert.Equal(AreaTriggerWaivers.Quest, registry.Waives(player, teleport));
        Assert.Single(registry.Waivers);
        Assert.Throws<ArgumentException>(() => registry.Register(registry));
    }

    [Fact]
    public void WaiverRegistration_ThroughTheBag_EndsWithTheBag()
    {
        var registry = new TeleportWaiverRegistry();
        var bag = new FeatureSubscriptions("probe");
        bag.Add(registry.Register(new FixedWaiver(AreaTriggerWaivers.Condition)));
        Assert.Equal(AreaTriggerWaivers.Condition, registry.Waives(null!, null!));

        bag.Dispose();

        Assert.Equal(AreaTriggerWaivers.None, registry.Waives(null!, null!));
    }

    private sealed class Undo(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    private sealed class NullObserver : ISpellCastObserver;

    private sealed class FixedWaiver(AreaTriggerWaivers waives) : IAreaTriggerWaiver
    {
        public AreaTriggerWaivers Waives(Player player, AreaTriggerTeleport teleport) => waives;
    }
}
