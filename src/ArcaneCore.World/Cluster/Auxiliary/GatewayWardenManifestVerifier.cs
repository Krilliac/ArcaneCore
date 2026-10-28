using System.Security.Cryptography;
using ArcaneCore.Kernel.Cluster.Auxiliary;

namespace ArcaneCore.World.Cluster.Auxiliary;

/// <summary>
/// Verifies gateway Warden readiness, realm-wide gateway Warden manifest equivalence, and session-generation /
/// owner-epoch verdict fencing (<c>docs/CLUSTERING_DESIGN.md</c> §8 Warden, §10 M15.6).
/// </summary>
public sealed class GatewayWardenManifestVerifier
{
    public const int RequiredRc4KeyLength = 16;

    // Replay guard. Session generations are only monotonic within one gateway process: GatewayServer counts from 1 per
    // process and never persists the counter, and a realm may run several gateways. So replay state is kept per
    // (generation source, character), where the source is the gateway node id plus its process incarnation. Each ledger
    // holds the highest generation that had a verdict accepted and the (check, action) pairs accepted in it. A newer
    // generation replaces the older generation's keys, and a lower generation from the SAME source is refused.
    // A ledger is kept for good (one per character per live source, holding one session's keys) until its source is
    // retired with RetireSource. Retired sources are remembered (one entry per gateway process ever retired) so a
    // late verdict from a dead process cannot reopen its sessions. Verdicts are rare (failed scans only), so one lock
    // guards all of it; that keeps retirement atomic with acceptance.
    private readonly object _replayLock = new();
    private readonly Dictionary<(ClusterWardenVerdictSource Source, ulong CharacterGuid), CharacterVerdictLedger> _verdictLedgers = [];
    private readonly HashSet<ClusterWardenVerdictSource> _retiredSources = [];

    public GatewayWardenManifestVerifier(GatewayWardenManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Manifest = manifest;
        IsGatewayWardenReady = EvaluateReadiness(manifest, out string? failureReason);
        FailureReason = failureReason;
    }

    public GatewayWardenManifest Manifest { get; }

    /// <summary>
    /// In cluster mode, a gateway with <c>Warden:Enabled</c> true is NOT ready unless its module manifest is usable
    /// and passes SHA-256 verification (<c>docs/CLUSTERING_DESIGN.md</c> §8 Warden, line 383–386, 569).
    /// </summary>
    public bool IsGatewayWardenReady { get; }

    public string? FailureReason { get; }

    /// <summary>Number of (character, check, action) replay keys currently held. Test and diagnostics only.</summary>
    internal int RetainedVerdictKeyCount
    {
        get
        {
            lock (_replayLock)
            {
                int count = 0;
                foreach (CharacterVerdictLedger ledger in _verdictLedgers.Values)
                {
                    count += ledger.Accepted.Count;
                }

                return count;
            }
        }
    }

    public static bool EvaluateReadiness(GatewayWardenManifest manifest, out string? failureReason)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (manifest.RealmId <= 0)
        {
            failureReason = $"invalid RealmId ({manifest.RealmId})";
            return false;
        }

        if (!manifest.Enabled)
        {
            failureReason = null;
            return true;
        }

        if (string.IsNullOrWhiteSpace(manifest.ModuleDigestHex))
        {
            failureReason = "Warden is enabled but ModuleDigestHex is empty";
            return false;
        }

        if (manifest.CheckRevision < 0)
        {
            failureReason = $"Warden CheckRevision ({manifest.CheckRevision}) cannot be negative";
            return false;
        }

        if (manifest.Modules is null || manifest.Modules.Count == 0)
        {
            failureReason = "Warden is enabled but no OS modules are present in the manifest";
            return false;
        }

        foreach (WardenModuleDescriptor module in manifest.Modules)
        {
            if (string.IsNullOrWhiteSpace(module.OsTag))
            {
                failureReason = "Warden module descriptor has an empty OsTag";
                return false;
            }

            if (module.ModulePayload.IsEmpty)
            {
                failureReason = $"Warden module '{module.OsTag}' has an empty payload";
                return false;
            }

            if (module.Rc4Key.Length != RequiredRc4KeyLength)
            {
                failureReason = $"Warden module '{module.OsTag}' RC4 key length ({module.Rc4Key.Length}) must be {RequiredRc4KeyLength} bytes";
                return false;
            }

            if (string.IsNullOrWhiteSpace(module.ExpectedSha256Hex))
            {
                failureReason = $"Warden module '{module.OsTag}' is missing ExpectedSha256Hex";
                return false;
            }

            string computedHex = Convert.ToHexStringLower(SHA256.HashData(module.ModulePayload.Span));
            if (!string.Equals(computedHex, module.ExpectedSha256Hex.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                failureReason =
                    $"Warden module '{module.OsTag}' failed SHA-256 digest verification (expected {module.ExpectedSha256Hex}, computed {computedHex})";
                return false;
            }
        }

        failureReason = null;
        return true;
    }

    /// <summary>
    /// Verifies that another gateway in the same realm advertises the exact same Warden posture, module digest,
    /// options summary, and check revision so clients cannot select an unscanned gateway.
    /// </summary>
    public bool VerifyPeerGatewayManifest(GatewayWardenManifest? peer, out string? mismatchReason)
    {
        if (peer is null)
        {
            mismatchReason = "peer Warden manifest is null";
            return false;
        }

        if (peer.RealmId != Manifest.RealmId)
        {
            mismatchReason = $"Warden realm mismatch: peer {peer.RealmId} != local {Manifest.RealmId}";
            return false;
        }

        if (peer.Enabled != Manifest.Enabled)
        {
            mismatchReason = $"Warden enabled mismatch: peer Enabled={peer.Enabled} != local Enabled={Manifest.Enabled}";
            return false;
        }

        if (!string.Equals(peer.ModuleDigestHex, Manifest.ModuleDigestHex, StringComparison.OrdinalIgnoreCase))
        {
            mismatchReason = $"Warden module digest mismatch: peer '{peer.ModuleDigestHex}' != local '{Manifest.ModuleDigestHex}'";
            return false;
        }

        if (!string.Equals(peer.OptionsSummary, Manifest.OptionsSummary, StringComparison.Ordinal))
        {
            mismatchReason = $"Warden options mismatch: peer '{peer.OptionsSummary}' != local '{Manifest.OptionsSummary}'";
            return false;
        }

        if (peer.CheckRevision != Manifest.CheckRevision)
        {
            mismatchReason = $"Warden check revision mismatch: peer {peer.CheckRevision} != local {Manifest.CheckRevision}";
            return false;
        }

        mismatchReason = null;
        return true;
    }

    /// <summary>
    /// Validates and records a gateway Warden verdict against the active session generation and worker owner epoch.
    /// Discards stale-epoch, stale-generation, superseded-generation, or duplicate verdicts.
    /// </summary>
    /// <remarks>
    /// <paramref name="source"/> names the generation counter the verdict came from: take it from the admitted link's peer
    /// manifest (<c>NodeId</c>, <c>NodeIncarnation</c>), never from the verdict body. Generations are monotonic only
    /// within one source (<c>GatewayServer.NextGeneration</c> is a per-process counter that starts again at 1 after a
    /// restart, and each gateway has its own), so the generation floor applies per (source, character). Within a source,
    /// once a character has an accepted verdict in generation G, any verdict for that character below G is refused even
    /// if the caller's active-generation snapshot still names the older generation, so dropping the older keys never
    /// reopens an ended session to replay. A verdict from a source passed to <see cref="RetireSource"/> is refused.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="source"/> has no incarnation; pooling unidentified counters
    /// would let one gateway's high generation suppress another's verdicts.</exception>
    public bool TryAcceptVerdict(
        ClusterWardenVerdict? verdict,
        ClusterWardenVerdictSource source,
        long activeSessionGeneration,
        long activeOwnerEpoch,
        out string? discardReason)
    {
        if (source.NodeIncarnation == Guid.Empty)
        {
            throw new ArgumentException("a Warden verdict source must carry the gateway's node incarnation", nameof(source));
        }

        if (verdict is null)
        {
            discardReason = "verdict is null";
            return false;
        }

        if (verdict.OwnerEpoch != activeOwnerEpoch)
        {
            discardReason = $"stale owner epoch on Warden verdict: verdict epoch {verdict.OwnerEpoch} != active {activeOwnerEpoch}";
            return false;
        }

        if (verdict.SessionGeneration != activeSessionGeneration)
        {
            discardReason =
                $"stale session generation on Warden verdict: verdict generation {verdict.SessionGeneration} != active {activeSessionGeneration}";
            return false;
        }

        lock (_replayLock)
        {
            if (_retiredSources.Contains(source))
            {
                discardReason = $"Warden verdict from a retired gateway incarnation ({source.NodeId} {source.NodeIncarnation})";
                return false;
            }

            var key = (source, verdict.CharacterGuid);
            if (!_verdictLedgers.TryGetValue(key, out CharacterVerdictLedger? ledger))
            {
                ledger = new CharacterVerdictLedger();
                _verdictLedgers.Add(key, ledger);
            }

            if (verdict.SessionGeneration < ledger.Generation)
            {
                discardReason =
                    $"superseded session generation on Warden verdict: verdict generation {verdict.SessionGeneration} < latest {ledger.Generation} for this character from {source.NodeId}";
                return false;
            }

            if (verdict.SessionGeneration > ledger.Generation)
            {
                ledger.Generation = verdict.SessionGeneration;
                ledger.Accepted.Clear();
            }

            if (!ledger.Accepted.Add((verdict.CheckId, verdict.Action)))
            {
                discardReason = "duplicate Warden verdict for the same session generation and check";
                return false;
            }
        }

        discardReason = null;
        return true;
    }

    /// <summary>
    /// Forgets the replay state of a gateway process that is gone for good (for example when a new incarnation of the
    /// same node id is admitted) and refuses any later verdict from it, so its ended sessions cannot be replayed.
    /// </summary>
    public void RetireSource(ClusterWardenVerdictSource source)
    {
        if (source.NodeIncarnation == Guid.Empty)
        {
            throw new ArgumentException("a Warden verdict source must carry the gateway's node incarnation", nameof(source));
        }

        lock (_replayLock)
        {
            if (!_retiredSources.Add(source))
            {
                return;
            }

            List<(ClusterWardenVerdictSource, ulong)> dead = [];
            foreach ((ClusterWardenVerdictSource Source, ulong CharacterGuid) key in _verdictLedgers.Keys)
            {
                if (key.Source == source)
                {
                    dead.Add(key);
                }
            }

            foreach ((ClusterWardenVerdictSource, ulong) key in dead)
            {
                _verdictLedgers.Remove(key);
            }
        }
    }

    /// <summary>Replay state for one character from one generation source. Guarded by <c>_replayLock</c>.</summary>
    private sealed class CharacterVerdictLedger
    {
        public long Generation = long.MinValue;

        public readonly HashSet<(ushort CheckId, ClusterWardenAction Action)> Accepted = [];
    }
}
