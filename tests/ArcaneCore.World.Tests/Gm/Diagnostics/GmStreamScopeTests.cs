using ArcaneCore.Game;
using ArcaneCore.Game.Diagnostics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Gm.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Diagnostics;

/// <summary>
/// The scope filter of <see cref="GmStream"/>'s scoped publish (<see cref="GmStream.Publish{TState}"/>): target, near and all against the
/// viewer snapshot from <see cref="GmStream.RefreshViewers"/>, the formatter's zero cost when nobody matches, and the rate limit and timeout
/// on scoped publishes. The last tests run the real game producers (map broadcasts) into a real stream and the feature's tick refresh.
/// </summary>
public sealed class GmStreamScopeTests
{
    private static readonly ObjectGuid Gm = new(1);
    private static readonly ObjectGuid GmB = new(2);
    private static readonly ObjectGuid Selected = new(50);
    private static readonly ObjectGuid Stranger = new(60);
    private static readonly ObjectGuid Stranger2 = new(61);

    private const float Range = 100f;

    private sealed class CountingFormatter
    {
        public int Calls;

        public string Format(string text)
        {
            Calls++;
            return text;
        }
    }

    private static GmTraceSubject At(ObjectGuid actor, ObjectGuid other, float x, float y, uint map = 0, uint instance = 0, float z = 0f)
        => new(actor, other, map, instance, x, y, z, HasPosition: true);

    private static GmTraceSubject Nowhere(ObjectGuid actor, ObjectGuid other) => new(actor, other, 0, 0, 0f, 0f, 0f, HasPosition: false);

    private static GmStreamViewer ViewerAt(float x, float y, ObjectGuid selection = default, uint map = 0, uint instance = 0)
        => new(selection, map, instance, x, y, 0f, Range);

    private static void View(GmStream stream, GmStreamViewer? viewer) => stream.RefreshViewers(_ => viewer);

    private static List<GmStreamLine> Drain(GmStream stream)
    {
        var lines = new List<GmStreamLine>();
        stream.Drain(lines.Add);
        return lines;
    }

    private static int Offer(GmStream stream, GmTraceSubject subject, string text, CountingFormatter? counter = null)
        => stream.Publish(GmStreamChannels.Damage, in subject, text, counter is null ? static s => s : counter.Format);

    // --- zero cost -------------------------------------------------------------------------------------------

    [Fact]
    public void TheFormatter_IsNeverCalled_WithNoSubscriberAnotherChannelOrANonMatchingScope()
    {
        var stream = new GmStream();
        var counter = new CountingFormatter();
        GmTraceSubject far = At(Stranger, Stranger2, 5000, 5000);

        Assert.Equal(0, Offer(stream, far, "x", counter)); // nobody follows anything
        stream.Subscribe(Gm, GmStreamChannels.Moves, GmStreamScope.All);
        Assert.Equal(0, Offer(stream, far, "x", counter)); // only another channel
        stream.Subscribe(Gm, GmStreamChannels.Damage, GmStreamScope.Target);
        View(stream, ViewerAt(0, 0, Selected));
        Assert.Equal(0, Offer(stream, far, "x", counter)); // followed, but not the GM's selection
        stream.Subscribe(Gm, GmStreamChannels.Damage, GmStreamScope.Near);
        Assert.Equal(0, Offer(stream, far, "x", counter)); // followed near, but out of range

        Assert.Equal(0, counter.Calls);
        Assert.Empty(Drain(stream));
    }

    [Fact]
    public void TheFormatter_RunsOnce_ForEveryMatchingGm()
    {
        var stream = new GmStream();
        var counter = new CountingFormatter();
        stream.Subscribe(Gm, GmStreamChannels.Damage, GmStreamScope.All);
        stream.Subscribe(GmB, GmStreamChannels.Damage, GmStreamScope.All);

        Assert.Equal(2, Offer(stream, Nowhere(Stranger, default), "hit", counter));

        Assert.Equal(1, counter.Calls);
        Assert.Equal([new GmStreamLine(Gm, "[damage] hit"), new GmStreamLine(GmB, "[damage] hit")], Drain(stream).OrderBy(l => l.Gm.Value));
    }

    [Fact]
    public void AScopedPublishNobodyMatches_AllocatesNothing()
    {
        var stream = new GmStream();
        stream.Subscribe(Gm, GmStreamChannels.Damage, GmStreamScope.Near);
        View(stream, ViewerAt(0, 0));
        GmTraceSubject far = At(Stranger, Stranger2, 5000, 5000);
        Func<int, string> format = static i => i.ToString(System.Globalization.CultureInfo.InvariantCulture);

        void Run(int n)
        {
            for (int i = 0; i < n; i++)
            {
                stream.Publish(GmStreamChannels.Damage, in far, i, format);
                stream.Publish(GmStreamChannels.Procs, in far, i, format);
                stream.HasSubscribers(GmStreamChannels.Damage);
            }
        }

        Run(50);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Run(1000);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Empty(Drain(stream));
    }

    // --- target ----------------------------------------------------------------------------------------------

    [Fact]
    public void Target_DeliversTheGmsOwnEvents_EvenWithNoViewerOrPosition()
    {
        var stream = new GmStream();
        stream.Subscribe(Gm, GmStreamChannels.Damage); // target is the default scope

        Assert.Equal(GmStreamScope.Target, Assert.Single(stream.Details(Gm)).Scope);
        Assert.Equal(1, Offer(stream, At(Gm, Stranger, 9000, 9000), "gm hits"));
        Assert.Equal(1, Offer(stream, Nowhere(Stranger, Gm), "gm is hit"));
        Assert.Equal(0, Offer(stream, At(Stranger, Stranger2, 0, 0), "not the gm, no viewer"));
    }

    [Fact]
    public void Target_DeliversTheSelectionAsActorOrOther_WithinRangeOnTheSameMapAndInstance_Only()
    {
        var stream = new GmStream(new GmStreamOptions { MaxLinesPerSecond = 100 });
        stream.Subscribe(Gm, GmStreamChannels.Damage, GmStreamScope.Target);
        View(stream, ViewerAt(10, 10, Selected, map: 1, instance: 7));

        Assert.Equal(1, Offer(stream, At(Selected, Stranger, 20, 20, 1, 7), "selection acts"));
        Assert.Equal(1, Offer(stream, At(Stranger, Selected, 20, 20, 1, 7), "selection is hit"));
        Assert.Equal(1, Offer(stream, Nowhere(Stranger, Selected), "selection, no place")); // matched by GUID alone
        Assert.Equal(0, Offer(stream, At(Stranger, Stranger2, 11, 11, 1, 7), "unrelated, near"));
        Assert.Equal(0, Offer(stream, At(Selected, Stranger, 500, 10, 1, 7), "selection, out of range"));
        Assert.Equal(0, Offer(stream, At(Selected, Stranger, 20, 20, 0, 7), "selection, other map"));
        Assert.Equal(0, Offer(stream, At(Selected, Stranger, 20, 20, 1, 8), "selection, other instance"));

        Assert.Equal(["[damage] selection acts", "[damage] selection is hit", "[damage] selection, no place"], Drain(stream).Select(l => l.Text));
    }

    [Fact]
    public void Target_WithNothingSelected_MatchesOnlyTheGmsOwnEvents()
    {
        var stream = new GmStream();
        stream.Subscribe(Gm, GmStreamChannels.Damage, GmStreamScope.Target);
        View(stream, ViewerAt(0, 0, ObjectGuid.Empty));

        Assert.Equal(0, Offer(stream, At(ObjectGuid.Empty, Stranger, 1, 1), "an empty actor never equals an empty selection"));
        Assert.Equal(1, Offer(stream, At(Gm, ObjectGuid.Empty, 1, 1), "own"));
    }

    // --- near ------------------------------------------------------------------------------------------------

    [Fact]
    public void Near_DeliversUnrelatedEventsInRange_NotFarOtherMapOrPlaceless_AndRangeIs2DInclusive()
    {
        var stream = new GmStream(new GmStreamOptions { MaxLinesPerSecond = 100 });
        stream.Subscribe(Gm, GmStreamChannels.Damage, GmStreamScope.Near);
        View(stream, ViewerAt(0, 0));

        Assert.Equal(1, Offer(stream, At(Stranger, Stranger2, 30, 40), "near"));
        Assert.Equal(1, Offer(stream, At(Stranger, Stranger2, 60, 80), "at the edge")); // exactly 100 yards
        Assert.Equal(1, Offer(stream, At(Stranger, Stranger2, 0, 50, z: 900), "high above")); // height is not range
        Assert.Equal(0, Offer(stream, At(Stranger, Stranger2, 60, 80.01f), "just past"));
        Assert.Equal(0, Offer(stream, At(Stranger, Stranger2, 1, 1, map: 1), "other map"));
        Assert.Equal(0, Offer(stream, At(Stranger, Stranger2, 1, 1, instance: 3), "other instance"));
        Assert.Equal(0, Offer(stream, Nowhere(Stranger, Stranger2), "no place"));
        Assert.Equal(1, Offer(stream, Nowhere(Gm, Stranger2), "own, no place"));

        Assert.Equal(["[damage] near", "[damage] at the edge", "[damage] high above", "[damage] own, no place"], Drain(stream).Select(l => l.Text));
    }

    [Fact]
    public void Near_WithNoViewer_OrAViewerClearedByRefresh_DeliversOnlyOwnEvents()
    {
        var stream = new GmStream();
        stream.Subscribe(Gm, GmStreamChannels.Damage, GmStreamScope.Near);
        Assert.Equal(0, Offer(stream, At(Stranger, Stranger2, 0, 0), "before any refresh"));

        View(stream, ViewerAt(0, 0));
        Assert.Equal(1, Offer(stream, At(Stranger, Stranger2, 0, 0), "seen"));

        View(stream, null); // the GM left the world
        Assert.Equal(0, Offer(stream, At(Stranger, Stranger2, 0, 0), "after logout"));
    }

    [Fact]
    public void RefreshViewers_ResolvesEverySubscribedGm_AndNoneWhenNobodySubscribes()
    {
        var stream = new GmStream();
        var asked = new List<ObjectGuid>();
        stream.RefreshViewers(g => { asked.Add(g); return null; });
        Assert.Empty(asked);

        stream.Subscribe(Gm, GmStreamChannels.Damage, GmStreamScope.Near);
        stream.Subscribe(GmB, GmStreamChannels.Moves, GmStreamScope.Near);
        stream.RefreshViewers(g => { asked.Add(g); return g == Gm ? ViewerAt(0, 0) : ViewerAt(1000, 1000); });

        Assert.Equal([Gm, GmB], asked.OrderBy(g => g.Value));
        Assert.Equal(1, Offer(stream, At(Stranger, Stranger2, 5, 5), "only GM A is near"));
        Assert.Equal(Gm, Assert.Single(Drain(stream)).Gm);
        Assert.Throws<ArgumentNullException>(() => stream.RefreshViewers(null!));
    }

    // --- all, resubscribe, global publish --------------------------------------------------------------------

    [Fact]
    public void All_DeliversEverything_WithoutAViewer()
    {
        var stream = new GmStream();
        stream.Subscribe(Gm, GmStreamChannels.Damage, GmStreamScope.All);

        Assert.Equal(1, Offer(stream, At(Stranger, Stranger2, 9000, 9000, map: 1, instance: 4), "anywhere"));
        Assert.Equal(1, Offer(stream, Nowhere(Stranger, Stranger2), "nowhere"));
    }

    [Fact]
    public void Resubscribing_ChangesTheScope_WithoutAddingAChannel()
    {
        var stream = new GmStream();
        stream.Subscribe(Gm, GmStreamChannels.Damage, GmStreamScope.Target);
        View(stream, ViewerAt(0, 0));
        GmTraceSubject near = At(Stranger, Stranger2, 3, 3);
        Assert.Equal(0, Offer(stream, near, "a"));

        Assert.True(stream.Subscribe(Gm, GmStreamChannels.Damage, GmStreamScope.Near));
        Assert.Equal(GmStreamScope.Near, Assert.Single(stream.Details(Gm)).Scope);
        Assert.Equal(1, Offer(stream, near, "b"));

        Assert.True(stream.Subscribe(Gm, "DAMAGE", GmStreamScope.Target)); // names are case-insensitive
        Assert.Equal(GmStreamScope.Target, Assert.Single(stream.Details(Gm)).Scope);
        Assert.Equal(0, Offer(stream, near, "c"));
    }

    [Fact]
    public void TheGlobalPublish_IgnoresScope()
    {
        var stream = new GmStream();
        stream.Subscribe(Gm, GmStreamChannels.Damage, GmStreamScope.Target); // no viewer, no selection

        Assert.Equal(1, stream.Publish(GmStreamChannels.Damage, "server-wide"));
    }

    // --- rate limit and timeout on scoped publishes ----------------------------------------------------------

    [Fact]
    public void ScopedPublishes_AreRateLimited_AndTheSuppressedNoticeFollowsTheRefill()
    {
        var clock = new ManualTimeProvider(1_000_000);
        var stream = new GmStream(new GmStreamOptions { MaxLinesPerSecond = 2 }, clock);
        stream.Subscribe(Gm, GmStreamChannels.Damage, GmStreamScope.Near);
        View(stream, ViewerAt(0, 0));
        var counter = new CountingFormatter();

        int queued = Enumerable.Range(0, 5).Sum(i => Offer(stream, At(Stranger, Stranger2, 1, 1), "m" + i, counter));
        Assert.Equal(2, queued);
        Assert.Equal(2, counter.Calls); // a line dropped by the limit is never formatted

        clock.Advance(1);
        Offer(stream, At(Stranger, Stranger2, 1, 1), "next");
        Assert.Equal(
            ["[damage] m0", "[damage] m1", "[stream] 3 lines suppressed (limit 2/s)", "[damage] next"],
            Drain(stream).Select(l => l.Text));
    }

    [Fact]
    public void NonMatchingEvents_SpendNoTokens_AndCountNoSuppressedLines()
    {
        var clock = new ManualTimeProvider(1_000_000);
        var stream = new GmStream(new GmStreamOptions { MaxLinesPerSecond = 1 }, clock);
        stream.Subscribe(Gm, GmStreamChannels.Damage, GmStreamScope.Near);
        View(stream, ViewerAt(0, 0));

        for (int i = 0; i < 20; i++)
        {
            Assert.Equal(0, Offer(stream, At(Stranger, Stranger2, 5000, 0), "far" + i));
        }

        Assert.Equal(1, Offer(stream, At(Stranger, Stranger2, 1, 1), "near")); // the bucket is still full
        clock.Advance(1);
        stream.Sweep();
        Assert.Equal(["[damage] near"], Drain(stream).Select(l => l.Text)); // no "suppressed" notice for the far events
    }

    [Fact]
    public void AScopedSubscription_TimesOutThroughSweep_AndThenNothingIsFormatted()
    {
        var clock = new ManualTimeProvider(1_000_000);
        var stream = new GmStream(new GmStreamOptions { AutoOffAfter = TimeSpan.FromMinutes(5) }, clock);
        stream.Subscribe(Gm, GmStreamChannels.Damage, GmStreamScope.All);
        var counter = new CountingFormatter();

        clock.Advance(5 * 60);
        Assert.Equal(1, stream.Sweep());

        Assert.False(stream.HasSubscribers(GmStreamChannels.Damage));
        Assert.Equal(0, Offer(stream, Nowhere(Stranger, Stranger2), "late", counter));
        Assert.Equal(0, counter.Calls);
        Assert.Equal(["[stream] damage turned off (timed out after 5 min)"], Drain(stream).Select(l => l.Text));
    }

    // --- the real producers into a real stream ---------------------------------------------------------------

    private static Player NewPlayer(uint id, float x, float y)
    {
        var character = new CharacterRecord
        {
            Id = (int)id, AccountId = (int)id, Name = $"Scope{id}", Race = 1, Class = 1, Gender = 0, Level = 1, MapId = 0, ZoneId = 12, X = x, Y = y, Z = 83.5f,
        };
        var appearance = new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400);
        return new Player(character, appearance, new RecordingPlayerSession((int)id));
    }

    private static GmStreamViewer? ViewerOf(Player player, float range = Range)
        => player.Map is { } map ? new GmStreamViewer(player.Selection, map.MapId, map.InstanceId, player.X, player.Y, player.Z, range) : null;

    [Fact]
    public void RealDamageBroadcasts_ReachTheGmByScope()
    {
        using var world = new WorldRuntime(new WorldRuntimeOptions { UpdateCompressionThreshold = 0, AutosaveIntervalMs = 0, CommandTimeBudgetMs = 0 },
            new NullSaveQueue(), NullLogger<WorldRuntime>.Instance);
        Player gm = NewPlayer(1, 0, 0);
        Player a = NewPlayer(2, 5, 0);
        Player b = NewPlayer(3, 10, 0);
        Player c = NewPlayer(4, 20, 0);
        Player far = NewPlayer(5, 400, 0);
        foreach (Player p in new[] { gm, a, b, c, far })
        {
            world.AddPlayer(p);
        }

        world.RunTick(0);
        var stream = new GmStream(new GmStreamOptions { MaxLinesPerSecond = 100 });
        world.GmTrace = stream;

        gm.Selection = b.Guid;
        var other = new ObjectGuid(3_000); // a second GM, near nobody and selecting nothing
        stream.Subscribe(gm.Guid, GmStreamChannels.Damage, GmStreamScope.Target);
        stream.Subscribe(other, GmStreamChannels.Damage, GmStreamScope.Near);
        stream.RefreshViewers(g => g == gm.Guid ? ViewerOf(gm) : new GmStreamViewer(default, 0, 0, 5000, 5000, 0, Range));

        void Hit(Player from, Player to, uint amount)
            => SpellSystem.SendToSet(from, WorldOpcode.SmsgSpellnonmeleedamagelog, SpellPackets.BuildSpellNonMeleeDamageLog(to.Guid, from.Guid, 133, amount, SpellSchool.Fire), includeSelf: true);

        Hit(a, b, 1); // on the GM's selection: delivered
        Hit(c, a, 2); // unrelated: not for target
        Hit(far, gm, 3); // on the GM itself, from far away: delivered
        stream.Subscribe(gm.Guid, GmStreamChannels.Damage, GmStreamScope.Near);
        Hit(c, a, 4); // unrelated but near: delivered now
        Hit(far, far, 5); // far: never

        List<GmStreamLine> lines = Drain(stream);
        Assert.All(lines, l => Assert.Equal(gm.Guid, l.Gm));
        Assert.Equal(
            [
                "[damage] Scope2(Player:2) -> Scope3(Player:3) spell 133 1 Fire (absorb 0, resist 0, block 0)",
                "[damage] Scope5(Player:5) -> Scope1(Player:1) spell 133 3 Fire (absorb 0, resist 0, block 0)",
                "[damage] Scope4(Player:4) -> Scope2(Player:2) spell 133 4 Fire (absorb 0, resist 0, block 0)",
            ],
            lines.Select(l => l.Text));
    }

    [Fact]
    public async Task TheFeatureTick_RefreshesTheViewer_SoANearEventStartsMatching()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("GMSCOPE", "Gmscope", AccountSecurity.GameMaster);
        await client.CollectAsync();
        GmStream stream = host.WorldServices.GetRequiredService<GmStreamFeature>().Stream;
        Assert.Same(stream, host.World.GmTrace); // the feature wired the game's producers to its stream
        Player gm = await host.PlayerAsync("Gmscope");
        (float x, float y, uint map, uint instance) = await host.PlayerStateAsync("Gmscope", p => (p.X, p.Y, p.Map!.MapId, p.Map.InstanceId));
        GmTraceSubject near = new(Stranger, Stranger2, map, instance, x + 3, y, 0f, HasPosition: true);

        // Subscribed and offered inside one world-thread callback: no tick ran in between, so there is no viewer yet.
        int before = await host.OnWorldAsync(() =>
        {
            stream.Subscribe(gm.Guid, GmStreamChannels.Damage, GmStreamScope.Near);
            return stream.Publish(GmStreamChannels.Damage, in near, "early", static s => s);
        });
        Assert.Equal(0, before);

        await host.WaitForWorldAsync(() => stream.Publish(GmStreamChannels.Damage, in near, "refreshed", static s => s) > 0, "the tick refreshes the viewer");
        Assert.Contains("[damage] refreshed", await client.CollectChatLinesAsync(1));
    }
}
