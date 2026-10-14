using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ArcaneCore.World.Playerbots.AI.Actions;

/// <summary>Bot stands idle for <paramref name="DurationSec"/> seconds.</summary>
public readonly record struct IdleAction(float DurationSec);

/// <summary>Bot moves toward <paramref name="WorldPos"/> with normalized <paramref name="Urgency"/> (0..1).</summary>
public readonly record struct MoveToAction(Vector3 WorldPos, float Urgency);

/// <summary>Bot casts <paramref name="SpellId"/> on <paramref name="TargetGuid"/>.</summary>
public readonly record struct CastSpellAction(uint SpellId, ulong TargetGuid);

/// <summary>Bot issues pet command <paramref name="CommandId"/> against <paramref name="PetTargetGuid"/>.</summary>
public readonly record struct PetCommandAction(uint CommandId, ulong PetTargetGuid);

/// <summary>
/// Custom non-boxing C# 15 <see cref="UnionAttribute"/> value type for playerbot inference actions.
/// Overlaps all four unmanaged action payloads at <c>[FieldOffset(8)]</c> inside a fixed 24-byte layout
/// so 128+ actions fit on the stack (<c>stackalloc PlayerbotAction[128]</c> = 3 KB) and exhaustive
/// <c>switch</c> expressions dispatch via <c>TryGetValue</c> without boxing.
/// </summary>
[Union]
[StructLayout(LayoutKind.Explicit, Size = 24)]
public readonly struct PlayerbotAction
{
    public const byte IdleTag = 1;
    public const byte MoveToTag = 2;
    public const byte CastSpellTag = 3;
    public const byte PetCommandTag = 4;

    [FieldOffset(0)] private readonly byte _tag;

    [FieldOffset(8)] private readonly IdleAction _idle;
    [FieldOffset(8)] private readonly MoveToAction _move;
    [FieldOffset(8)] private readonly CastSpellAction _cast;
    [FieldOffset(8)] private readonly PetCommandAction _pet;

    public PlayerbotAction(IdleAction value)
    {
        Unsafe.SkipInit(out this);
        _tag = IdleTag;
        _idle = value;
    }

    public PlayerbotAction(MoveToAction value)
    {
        Unsafe.SkipInit(out this);
        _tag = MoveToTag;
        _move = value;
    }

    public PlayerbotAction(CastSpellAction value)
    {
        Unsafe.SkipInit(out this);
        _tag = CastSpellTag;
        _cast = value;
    }

    public PlayerbotAction(PetCommandAction value)
    {
        Unsafe.SkipInit(out this);
        _tag = PetCommandTag;
        _pet = value;
    }

    /// <summary>1-based discriminator tag (0 when uninitialized).</summary>
    public byte Tag => _tag;

    /// <summary>Whether this union holds an initialized action payload.</summary>
    public bool HasValue => _tag != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out IdleAction value)
    {
        value = _idle;
        return _tag == IdleTag;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out MoveToAction value)
    {
        value = _move;
        return _tag == MoveToTag;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out CastSpellAction value)
    {
        value = _cast;
        return _tag == CastSpellTag;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out PetCommandAction value)
    {
        value = _pet;
        return _tag == PetCommandTag;
    }

    /// <summary>Fallback boxed accessor required by the C# 15 Union pattern contract.</summary>
    public object? Value => _tag switch
    {
        IdleTag => _idle,
        MoveToTag => _move,
        CastSpellTag => _cast,
        PetCommandTag => _pet,
        _ => null,
    };

    public static implicit operator PlayerbotAction(IdleAction value) => new(value);
    public static implicit operator PlayerbotAction(MoveToAction value) => new(value);
    public static implicit operator PlayerbotAction(CastSpellAction value) => new(value);
    public static implicit operator PlayerbotAction(PetCommandAction value) => new(value);
}
