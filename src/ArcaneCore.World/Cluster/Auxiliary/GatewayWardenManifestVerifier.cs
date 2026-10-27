using System.Collections.Concurrent;
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

    // Replay guard, one ledger per character: the highest session generation that had a verdict accepted, and the
    // (check, action) pairs accepted in that generation. A newer generation replaces the older generation's keys, and a
    // verdict for a generation below the ledger's is refused, so the guard holds O(characters x checks in one session)
    // instead of growing with every session generation for the life of the node.
    private readonly ConcurrentDictionary<ulong, CharacterVerdictLedger> _verdictLedgers = new();

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
            int count = 0;
            foreach (CharacterVerdictLedger ledger in _verdictLedgers.Values)
            {
                lock (ledger)
                {
                    count += ledger.Accepted.Count;
                }
            }

            return count;
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
    /// Session generations come from the gateway's process-wide monotonic counter (<c>GatewayServer.NextGeneration</c>),
    /// so a character's later session always has a higher generation than its earlier ones for the life of this verifier.
    /// Once a character has an accepted verdict in generation G, any verdict for that character below G is refused even if
    /// the caller's active-generation snapshot still names the older generation, so dropping the older keys never reopens
    /// an ended session to replay.
    /// </remarks>
    public bool TryAcceptVerdict(
        ClusterWardenVerdict? verdict,
        long activeSessionGeneration,
        long activeOwnerEpoch,
        out string? discardReason)
    {
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

        CharacterVerdictLedger ledger = _verdictLedgers.GetOrAdd(verdict.CharacterGuid, static _ => new CharacterVerdictLedger());
        lock (ledger)
        {
            if (verdict.SessionGeneration < ledger.Generation)
            {
                discardReason =
                    $"superseded session generation on Warden verdict: verdict generation {verdict.SessionGeneration} < latest {ledger.Generation} for this character";
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

    /// <summary>Per-character replay state. Guarded by locking the instance.</summary>
    private sealed class CharacterVerdictLedger
    {
        public long Generation = long.MinValue;

        public readonly HashSet<(ushort CheckId, ClusterWardenAction Action)> Accepted = [];
    }
}
