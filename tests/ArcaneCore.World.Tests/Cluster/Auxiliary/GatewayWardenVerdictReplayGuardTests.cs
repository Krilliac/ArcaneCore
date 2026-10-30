using System.Collections.Concurrent;
using System.Security.Cryptography;
using ArcaneCore.Kernel.Cluster.Auxiliary;
using ArcaneCore.World.Cluster.Auxiliary;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Auxiliary;

/// <summary>
/// The verdict replay guard in <see cref="GatewayWardenManifestVerifier"/> must stay bounded on a long-running node
/// without weakening replay protection: duplicates in the current generation and verdicts for an older generation of
/// the same character are still discarded.
/// </summary>
public sealed class GatewayWardenVerdictReplayGuardTests
{
    private const long OwnerEpoch = 9;

    private static GatewayWardenManifestVerifier NewVerifier()
    {
        byte[] payload = [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00];
        byte[] rc4Key = new byte[GatewayWardenManifestVerifier.RequiredRc4KeyLength];
        RandomNumberGenerator.Fill(rc4Key);
        string hash = Convert.ToHexStringLower(SHA256.HashData(payload));
        var manifest = new GatewayWardenManifest(
            RealmId: 1,
            Enabled: true,
            ModuleDigestHex: hash,
            OptionsSummary: "enabled=1;action=kick",
            CheckRevision: 12,
            Modules: [new WardenModuleDescriptor("Win", payload, rc4Key, hash)]);
        var verifier = new GatewayWardenManifestVerifier(manifest);
        Assert.True(verifier.IsGatewayWardenReady);
        return verifier;
    }

    private static ClusterWardenVerdict Verdict(ulong character, long generation, ushort checkId, ClusterWardenAction action = ClusterWardenAction.Kick) =>
        new(generation, OwnerEpoch, AccountId: 77, CharacterGuid: character, Reason: "MEM_CHECK failed", Action: action, CheckId: checkId);

    private static readonly ClusterWardenVerdictSource GatewayA = new("gateway-a", Guid.NewGuid());

    private static bool Accept(GatewayWardenManifestVerifier verifier, ClusterWardenVerdict verdict, out string? reason) =>
        Accept(verifier, GatewayA, verdict, out reason);

    private static bool Accept(GatewayWardenManifestVerifier verifier, ClusterWardenVerdictSource source, ClusterWardenVerdict verdict, out string? reason) =>
        verifier.TryAcceptVerdict(verdict, source, activeSessionGeneration: verdict.SessionGeneration, activeOwnerEpoch: OwnerEpoch, out reason);

    [Fact]
    public void ReplayGuard_StaysBounded_AcrossManySessionGenerationsOfOneCharacter()
    {
        GatewayWardenManifestVerifier verifier = NewVerifier();
        const int generations = 500;
        const int checksPerGeneration = 6;

        for (long generation = 1; generation <= generations; generation++)
        {
            for (ushort check = 0; check < checksPerGeneration; check++)
            {
                Assert.True(Accept(verifier, Verdict(1200UL, generation, check), out string? reason), reason);
            }

            // Only the latest generation's keys are retained; the old code kept generations * checks.
            Assert.True(
                verifier.RetainedVerdictKeyCount <= checksPerGeneration,
                $"after generation {generation} the replay guard retains {verifier.RetainedVerdictKeyCount} keys (bound {checksPerGeneration})");
        }

        Assert.Equal(checksPerGeneration, verifier.RetainedVerdictKeyCount);
    }

    [Fact]
    public void ReplayGuard_StillRejectsReplayInTheCurrentGeneration_AfterManyGenerations()
    {
        GatewayWardenManifestVerifier verifier = NewVerifier();
        for (long generation = 1; generation <= 50; generation++)
        {
            ClusterWardenVerdict verdict = Verdict(1200UL, generation, checkId: 243);
            Assert.True(Accept(verifier, verdict, out string? accepted), accepted);
            Assert.Null(accepted);

            Assert.False(Accept(verifier, verdict, out string? dup));
            Assert.Contains("duplicate Warden verdict", dup, StringComparison.OrdinalIgnoreCase);

            // Same check, different action is a distinct verdict (key includes the action), then also deduplicated.
            ClusterWardenVerdict ban = verdict with { Action = ClusterWardenAction.Ban };
            Assert.True(Accept(verifier, ban, out string? banAccepted), banAccepted);
            Assert.False(Accept(verifier, ban, out string? banDup));
            Assert.Contains("duplicate Warden verdict", banDup, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ReplayGuard_RejectsVerdictsForAnOlderGeneration_EvenWhenTheCallerPassesThatGenerationAsActive()
    {
        GatewayWardenManifestVerifier verifier = NewVerifier();
        ClusterWardenVerdict gen5 = Verdict(1200UL, generation: 5, checkId: 243);
        Assert.True(Accept(verifier, gen5, out string? first), first);

        ClusterWardenVerdict gen6 = Verdict(1200UL, generation: 6, checkId: 244);
        Assert.True(Accept(verifier, gen6, out string? second), second);

        // Normal path: caller's active generation is 6, a generation-5 verdict is stale.
        Assert.False(verifier.TryAcceptVerdict(gen5, GatewayA, activeSessionGeneration: 6, activeOwnerEpoch: OwnerEpoch, out string? stale));
        Assert.Contains("stale session generation", stale, StringComparison.OrdinalIgnoreCase);

        // The pruned generation-5 key must not reopen generation 5: a replay, or a new check for that ended session,
        // is rejected even when a caller with a stale snapshot still says generation 5 is active.
        Assert.False(Accept(verifier, gen5, out string? replay));
        Assert.Contains("superseded session generation", replay, StringComparison.OrdinalIgnoreCase);

        Assert.False(Accept(verifier, Verdict(1200UL, generation: 5, checkId: 999), out string? newCheckOldGen));
        Assert.Contains("superseded session generation", newCheckOldGen, StringComparison.OrdinalIgnoreCase);

        // The current generation is still protected and still accepts new checks.
        Assert.False(Accept(verifier, gen6, out string? dup6));
        Assert.Contains("duplicate Warden verdict", dup6, StringComparison.OrdinalIgnoreCase);
        Assert.True(Accept(verifier, Verdict(1200UL, generation: 6, checkId: 245), out string? newCheck), newCheck);
    }

    [Fact]
    public void ReplayGuard_IsPerCharacter_OtherCharactersAreUnaffected()
    {
        GatewayWardenManifestVerifier verifier = NewVerifier();
        ClusterWardenVerdict bob = Verdict(2000UL, generation: 3, checkId: 243);
        Assert.True(Accept(verifier, bob, out string? bobAccepted), bobAccepted);

        // Alice moves far ahead; that must neither prune Bob's keys nor make Bob's (lower) generation look stale.
        for (long generation = 10; generation <= 40; generation++)
        {
            Assert.True(Accept(verifier, Verdict(1000UL, generation, checkId: 243), out string? aliceAccepted), aliceAccepted);
        }

        Assert.False(Accept(verifier, bob, out string? bobDup));
        Assert.Contains("duplicate Warden verdict", bobDup, StringComparison.OrdinalIgnoreCase);
        Assert.True(Accept(verifier, Verdict(2000UL, generation: 3, checkId: 244), out string? bobNewCheck), bobNewCheck);

        // A third character starting at generation 1 is accepted although other characters are far higher.
        Assert.True(Accept(verifier, Verdict(3000UL, generation: 1, checkId: 243), out string? carolAccepted), carolAccepted);

        // Alice: 1 key, Bob: 2 keys, Carol: 1 key.
        Assert.Equal(4, verifier.RetainedVerdictKeyCount);
    }

    [Fact]
    public async Task ReplayGuard_ConcurrentDuplicates_AcceptExactlyOnce()
    {
        GatewayWardenManifestVerifier verifier = NewVerifier();
        ClusterWardenVerdict verdict = Verdict(1200UL, generation: 7, checkId: 243);
        int accepted = 0;
        using var start = new ManualResetEventSlim(false);

        Task[] workers = Enumerable.Range(0, 32).Select(worker => Task.Run(() =>
        {
            start.Wait();
            for (int i = 0; i < 200; i++)
            {
                if (Accept(verifier, verdict, out _))
                {
                    Interlocked.Increment(ref accepted);
                }
            }
        })).ToArray();

        start.Set();
        await Task.WhenAll(workers);

        Assert.Equal(1, accepted);
        Assert.Equal(1, verifier.RetainedVerdictKeyCount);
    }

    [Fact]
    public async Task ReplayGuard_ConcurrentGenerationsAndCharacters_NeverAcceptTwice_AndStayBounded()
    {
        GatewayWardenManifestVerifier verifier = NewVerifier();
        const int characters = 4;
        const int generations = 200;
        const int checks = 3;
        var acceptedCounts = new ConcurrentDictionary<(ulong Character, long Generation, ushort Check), int>();
        using var start = new ManualResetEventSlim(false);

        // Several threads per character race over the same generation sequence, each submitting every verdict
        // (so duplicates and generation advances interleave). Callers pass the verdict's own generation as active,
        // which is the weakest caller snapshot: only the guard itself stands between a replay and acceptance.
        Task[] workers = Enumerable.Range(0, characters * 4).Select(worker => Task.Run(() =>
        {
            ulong character = 5000UL + (ulong)(worker % characters);
            start.Wait();
            for (long generation = 1; generation <= generations; generation++)
            {
                for (ushort check = 0; check < checks; check++)
                {
                    if (Accept(verifier, Verdict(character, generation, check), out _))
                    {
                        acceptedCounts.AddOrUpdate((character, generation, check), 1, static (_, n) => n + 1);
                    }
                }
            }
        })).ToArray();

        start.Set();
        await Task.WhenAll(workers);

        Assert.All(acceptedCounts, kv => Assert.Equal(1, kv.Value));
        Assert.True(
            verifier.RetainedVerdictKeyCount <= characters * checks,
            $"replay guard retains {verifier.RetainedVerdictKeyCount} keys (bound {characters * checks})");

        // After the race every older generation is closed and the latest is deduplicated, per character.
        for (int c = 0; c < characters; c++)
        {
            ulong character = 5000UL + (ulong)c;
            Assert.False(Accept(verifier, Verdict(character, generations - 1, checkId: 0), out _));
            Assert.False(Accept(verifier, Verdict(character, generations, checkId: 0), out _));
        }
    }

    [Fact]
    public void ReplayGuard_ARestartedGatewayCountingFromOne_IsNotRefusedBecauseOfThePreviousIncarnation()
    {
        GatewayWardenManifestVerifier verifier = NewVerifier();
        var before = new ClusterWardenVerdictSource("gateway-a", Guid.NewGuid());
        var after = new ClusterWardenVerdictSource("gateway-a", Guid.NewGuid());

        Assert.True(Accept(verifier, before, Verdict(1200UL, generation: 500, checkId: 243), out string? old), old);

        // Same node id, new process: its counter restarted, so generation 3 is a fresh session and its kick must land.
        ClusterWardenVerdict fresh = Verdict(1200UL, generation: 3, checkId: 243);
        Assert.True(Accept(verifier, after, fresh, out string? accepted), accepted);

        // Replay protection still holds inside the new incarnation.
        Assert.False(Accept(verifier, after, fresh, out string? dup));
        Assert.Contains("duplicate Warden verdict", dup, StringComparison.OrdinalIgnoreCase);
        Assert.True(Accept(verifier, after, Verdict(1200UL, generation: 4, checkId: 243), out string? next), next);
        Assert.False(Accept(verifier, after, fresh, out string? superseded));
        Assert.Contains("superseded session generation", superseded, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReplayGuard_TwoGatewaysInterleaved_EachKeepsItsOwnGenerationFloor()
    {
        GatewayWardenManifestVerifier verifier = NewVerifier();
        var gw1 = new ClusterWardenVerdictSource("gateway-1", Guid.NewGuid());
        var gw2 = new ClusterWardenVerdictSource("gateway-2", Guid.NewGuid());

        // The character hops between gateways whose counters are far apart; every fresh session's verdict is accepted.
        (ClusterWardenVerdictSource Source, long Generation)[] sessions =
            [(gw1, 900), (gw2, 2), (gw1, 901), (gw2, 3), (gw1, 950), (gw2, 7)];
        foreach ((ClusterWardenVerdictSource source, long generation) in sessions)
        {
            ClusterWardenVerdict verdict = Verdict(1200UL, generation, checkId: 243);
            Assert.True(Accept(verifier, source, verdict, out string? accepted), $"{source.NodeId} gen {generation}: {accepted}");
            Assert.False(Accept(verifier, source, verdict, out string? dup));
            Assert.Contains("duplicate Warden verdict", dup, StringComparison.OrdinalIgnoreCase);
        }

        // Older generations stay closed per gateway.
        Assert.False(Accept(verifier, gw1, Verdict(1200UL, generation: 901, checkId: 999), out string? gw1Old));
        Assert.Contains("superseded session generation", gw1Old, StringComparison.OrdinalIgnoreCase);
        Assert.False(Accept(verifier, gw2, Verdict(1200UL, generation: 3, checkId: 999), out string? gw2Old));
        Assert.Contains("superseded session generation", gw2Old, StringComparison.OrdinalIgnoreCase);

        // One ledger per (gateway incarnation, character), each holding only its latest generation's key.
        Assert.Equal(2, verifier.RetainedVerdictKeyCount);
    }

    [Fact]
    public void ReplayGuard_RetiredSource_DropsItsState_AndRefusesItsLateVerdicts_WithoutTouchingOtherSources()
    {
        GatewayWardenManifestVerifier verifier = NewVerifier();
        var live = new ClusterWardenVerdictSource("gateway-1", Guid.NewGuid());
        Assert.True(Accept(verifier, live, Verdict(1200UL, generation: 5, checkId: 1), out string? liveAccepted), liveAccepted);

        // Fifty gateway restarts: each incarnation is retired once its process is gone, so state does not pile up.
        for (int restart = 0; restart < 50; restart++)
        {
            var incarnation = new ClusterWardenVerdictSource("gateway-2", Guid.NewGuid());
            ClusterWardenVerdict last = Verdict(2000UL, generation: 1, checkId: 0);
            for (ushort check = 0; check < 4; check++)
            {
                last = Verdict(2000UL, generation: 1, checkId: check);
                Assert.True(Accept(verifier, incarnation, last, out string? accepted), $"restart {restart} check {check}: {accepted}");
            }

            Assert.Equal(1 + 4, verifier.RetainedVerdictKeyCount);
            verifier.RetireSource(incarnation);
            Assert.Equal(1, verifier.RetainedVerdictKeyCount);

            // A late replay, or a late new check, from the retired process cannot reopen its ended sessions.
            Assert.False(Accept(verifier, incarnation, last, out string? retired));
            Assert.Contains("retired gateway incarnation", retired, StringComparison.OrdinalIgnoreCase);
            Assert.False(Accept(verifier, incarnation, Verdict(2000UL, generation: 1, checkId: 77), out string? retiredNew));
            Assert.Contains("retired gateway incarnation", retiredNew, StringComparison.OrdinalIgnoreCase);
        }

        Assert.False(Accept(verifier, live, Verdict(1200UL, generation: 5, checkId: 1), out string? liveDup));
        Assert.Contains("duplicate Warden verdict", liveDup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReplayGuard_RefusesToPoolVerdictsWithoutASourceIncarnation()
    {
        GatewayWardenManifestVerifier verifier = NewVerifier();
        Assert.Throws<ArgumentException>(() =>
            Accept(verifier, new ClusterWardenVerdictSource("gateway-1", Guid.Empty), Verdict(1200UL, 1, 243), out _));
    }

    [Fact]
    public async Task ReplayGuard_ConcurrentGatewaysForOneCharacter_NeverAcceptTwice()
    {
        GatewayWardenManifestVerifier verifier = NewVerifier();
        ClusterWardenVerdictSource[] sources =
            [new("gateway-1", Guid.NewGuid()), new("gateway-2", Guid.NewGuid()), new("gateway-3", Guid.NewGuid())];
        var acceptedCounts = new ConcurrentDictionary<(Guid Source, long Generation, ushort Check), int>();
        using var start = new ManualResetEventSlim(false);

        Task[] workers = Enumerable.Range(0, sources.Length * 4).Select(worker => Task.Run(() =>
        {
            ClusterWardenVerdictSource source = sources[worker % sources.Length];
            start.Wait();
            for (long generation = 1; generation <= 150; generation++)
            {
                for (ushort check = 0; check < 2; check++)
                {
                    if (Accept(verifier, source, Verdict(1200UL, generation, check), out _))
                    {
                        acceptedCounts.AddOrUpdate((source.NodeIncarnation, generation, check), 1, static (_, n) => n + 1);
                    }
                }
            }
        })).ToArray();

        start.Set();
        await Task.WhenAll(workers);

        Assert.All(acceptedCounts, kv => Assert.Equal(1, kv.Value));
        Assert.True(verifier.RetainedVerdictKeyCount <= sources.Length * 2, $"{verifier.RetainedVerdictKeyCount} keys");
        foreach (ClusterWardenVerdictSource source in sources)
        {
            // Each gateway's last generation is reached by every racer, so one of them accepted it, exactly once.
            Assert.Contains((source.NodeIncarnation, 150L, (ushort)0), acceptedCounts.Keys);
            Assert.False(Accept(verifier, source, Verdict(1200UL, 150, 0), out _));
            Assert.False(Accept(verifier, source, Verdict(1200UL, 149, 0), out _));
        }
    }

    [Fact]
    public void ReplayGuard_ParallelAcceptAndRetire_NeverThrows_AndAcceptsEachIdAtMostOnce()
    {
        GatewayWardenManifestVerifier verifier = NewVerifier();
        const int ids = 4000;
        var sources = Enumerable.Range(0, 8).Select(i => new ClusterWardenVerdictSource("gateway-stress", Guid.NewGuid())).ToArray();
        int[] acceptedPerId = new int[ids];

        Parallel.For(0, ids * 4, new ParallelOptions { MaxDegreeOfParallelism = 16 }, n =>
        {
            int id = n % ids;
            ClusterWardenVerdictSource source = sources[id % sources.Length];
            // One character per id, so ids never share a generation floor; every id is offered four times from different threads.
            if (Accept(verifier, source, Verdict(10_000UL + (ulong)id, generation: 1, checkId: 243), out _))
            {
                Interlocked.Increment(ref acceptedPerId[id]);
            }

            // Retire sources while others are being accepted; retiring is idempotent.
            if (n % 97 == 0)
            {
                verifier.RetireSource(sources[(n / 97) % sources.Length]);
            }
        });

        for (int id = 0; id < ids; id++)
        {
            Assert.InRange(acceptedPerId[id], 0, 1);
        }

        // Every source is retired by now or refuses fresh verdicts after we retire it: nothing can be accepted afterwards.
        foreach (ClusterWardenVerdictSource source in sources)
        {
            verifier.RetireSource(source);
            Assert.False(Accept(verifier, source, Verdict(99_999UL, generation: 1, checkId: 1), out string? reason));
            Assert.Contains("retired gateway incarnation", reason, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(0, verifier.RetainedVerdictKeyCount);
    }

    [Fact]
    public void ReplayGuard_RetiringOneIncarnationOfANodeId_LeavesTheLiveIncarnationAcceptingAndRefusingReplays()
    {
        GatewayWardenManifestVerifier verifier = NewVerifier();
        var oldIncarnation = new ClusterWardenVerdictSource("gateway-a", Guid.NewGuid());
        var liveIncarnation = new ClusterWardenVerdictSource("gateway-a", Guid.NewGuid());

        ClusterWardenVerdict oldVerdict = Verdict(1200UL, generation: 1, checkId: 243);
        ClusterWardenVerdict liveFirst = Verdict(1200UL, generation: 1, checkId: 244);
        Assert.True(Accept(verifier, oldIncarnation, oldVerdict, out string? oldAccepted), oldAccepted);
        Assert.True(Accept(verifier, liveIncarnation, liveFirst, out string? liveAccepted), liveAccepted);

        verifier.RetireSource(oldIncarnation);

        // The retired incarnation is refused, even for a fresh verdict.
        Assert.False(Accept(verifier, oldIncarnation, Verdict(1200UL, generation: 1, checkId: 245), out string? retired));
        Assert.Contains("retired gateway incarnation", retired, StringComparison.OrdinalIgnoreCase);

        // The live incarnation of the same node id is untouched: fresh verdicts accepted, replays refused.
        Assert.False(Accept(verifier, liveIncarnation, liveFirst, out string? replay));
        Assert.Contains("duplicate Warden verdict", replay, StringComparison.OrdinalIgnoreCase);
        Assert.True(Accept(verifier, liveIncarnation, Verdict(1200UL, generation: 1, checkId: 246), out string? fresh), fresh);
        Assert.True(Accept(verifier, liveIncarnation, Verdict(1200UL, generation: 2, checkId: 244), out string? nextGeneration), nextGeneration);
        // Generation 2 replaced the live incarnation's generation-1 keys; the retired incarnation's key is gone.
        Assert.Equal(1, verifier.RetainedVerdictKeyCount);
    }
}
