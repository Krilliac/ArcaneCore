// Ported from Krilliac/Sonder-Inference src/cache/include/sonder/inference/cache/types.hpp @1c4c152 (Apache-2.0)
// Ported from Krilliac/Sonder-Inference src/cache/types.cpp @1c4c152 (Apache-2.0)
// Ported from Krilliac/Sonder-Inference include/sonder/inference/model_architecture.hpp @1c4c152 (Apache-2.0) (enum only)
//
// KV-cache module: shared identifiers and fingerprints. Block ids are uint, sequence ids ulong
// (the scheduler request id), token ids int, priorities byte (higher = more important).

namespace ArcaneCore.World.Playerbots.Chat.Inference.Scheduling.Cache;

/// <summary>
/// Model family as far as KV reuse is concerned. Attention-only models (llama, qwen2) can reuse
/// any whole cached block; hybrid/recurrent models carry a recurrent state that is only reusable
/// at an exact saved checkpoint.
/// </summary>
public enum ModelArchitecture : byte
{
    AttentionOnly,
    Hybrid,
    Recurrent,
}

/// <summary>Memory pressure level derived from pinned (referenced) block utilisation.</summary>
public enum PressureLevel : byte
{
    Normal,
    High,
    Critical,
}

/// <summary>Errors reported by <see cref="KvCacheManager"/> operations.</summary>
public enum KvCacheError : byte
{
    Ok,

    /// <summary>Bad arguments (e.g. truncate beyond length).</summary>
    InvalidArgument,

    /// <summary>Sequence id already exists, or an operation needs state that is missing.</summary>
    InvalidState,

    /// <summary>Unknown sequence id.</summary>
    NotFound,

    /// <summary>Not enough free/evictable blocks (nothing changed).</summary>
    Unavailable,
}

/// <summary>Outcome of a cache operation. The message is only built on failure.</summary>
public readonly record struct KvCacheStatus(KvCacheError Code, string? Message)
{
    public static KvCacheStatus Success => default;

    public bool IsOk => Code == KvCacheError.Ok;

    public static KvCacheStatus Fail(KvCacheError code, string message) => new(code, message);
}

/// <summary>
/// Compatibility fingerprint for prefix reuse: callers fold model identity/revision, tokenizer,
/// rope configuration, cache format and backend compatibility into it. Two sequences may only
/// share cached prefix blocks when fingerprints match.
/// </summary>
public readonly record struct CacheFingerprint(ulong Value);

/// <summary>Builds a fingerprint from arbitrary components (FNV-1a based, order sensitive).</summary>
public struct FingerprintBuilder
{
    private const ulong FnvPrime = 0x100000001b3UL;
    private const ulong FnvOffset = 0xcbf29ce484222325UL;

    private ulong _state;
    private bool _started;

    private ulong State => _started ? _state : FnvOffset;

    public FingerprintBuilder Add(string component)
    {
        ArgumentNullException.ThrowIfNull(component);
        ulong state = State;
        // FNV over the UTF-8 bytes, matching the source's byte-wise std::string_view hashing.
        int max = System.Text.Encoding.UTF8.GetMaxByteCount(component.Length);
        Span<byte> bytes = max <= 256 ? stackalloc byte[256] : new byte[max];
        int written = System.Text.Encoding.UTF8.GetBytes(component, bytes);
        foreach (byte b in bytes[..written])
        {
            state ^= b;
            state *= FnvPrime;
        }

        // Component separator so ("ab","c") != ("a","bc").
        state ^= 0xffU;
        state *= FnvPrime;
        _state = state;
        _started = true;
        return this;
    }

    public FingerprintBuilder Add(ulong component)
    {
        ulong state = State;
        for (int i = 0; i < 8; ++i)
        {
            state ^= (component >> (i * 8)) & 0xffU;
            state *= FnvPrime;
        }

        state ^= 0xfeU;
        state *= FnvPrime;
        _state = state;
        _started = true;
        return this;
    }

    public readonly CacheFingerprint Build() => new(_started ? _state : FnvOffset);
}

public static class CacheNames
{
    public const uint InvalidBlock = uint.MaxValue;

    public static string ToName(this PressureLevel level) => level switch
    {
        PressureLevel.Normal => "normal",
        PressureLevel.High => "high",
        PressureLevel.Critical => "critical",
        _ => "unknown",
    };
}
