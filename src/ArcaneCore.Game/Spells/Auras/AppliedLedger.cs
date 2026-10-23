using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// What an aura contributed when it was applied, remembered per aura so its removal takes back exactly that. Weak on the aura, so a
/// dropped aura frees its entry. One instance per handler module, held in a readonly static. World thread only.
/// </summary>
internal sealed class AppliedLedger<T>
{
    private readonly ConditionalWeakTable<SpellAura, StrongBox<T>> _entries = new();

    /// <summary>Remember <paramref name="value"/> for the aura, replacing any earlier record.</summary>
    public void Record(SpellAura aura, T value) => _entries.AddOrUpdate(aura, new StrongBox<T>(value));

    /// <summary>The recorded value, leaving the record in place; false when nothing is recorded.</summary>
    public bool TryGet(SpellAura aura, [MaybeNullWhen(false)] out T value)
    {
        if (_entries.TryGetValue(aura, out StrongBox<T>? box))
        {
            value = box.Value!;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>The recorded value, removing the record; false when nothing is recorded.</summary>
    public bool TryTake(SpellAura aura, [MaybeNullWhen(false)] out T value)
    {
        if (!_entries.TryGetValue(aura, out StrongBox<T>? box))
        {
            value = default;
            return false;
        }

        _entries.Remove(aura);
        value = box.Value!;
        return true;
    }
}
