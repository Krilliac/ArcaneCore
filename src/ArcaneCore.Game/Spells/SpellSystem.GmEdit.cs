namespace ArcaneCore.Game.Spells;

/// <summary>GM edits of a live aura holder (<c>.alter auraduration</c> / <c>.alter aurastacks</c>), through the same paths the aura code uses.</summary>
public sealed partial class SpellSystem
{
    /// <summary>
    /// Set the remaining time of <paramref name="holder"/> to <paramref name="durationMs"/> (raising its maximum when longer) and tell the
    /// target's client. False for a removed or permanent holder. A duration of 0 lets the holder expire on the next aura update.
    /// </summary>
    public bool GmSetAuraDuration(SpellAuraHolder holder, int durationMs)
    {
        ArgumentNullException.ThrowIfNull(holder);
        if (holder.IsRemoved || holder.IsPermanent || holder.Duration < 0)
        {
            return false;
        }

        holder.Duration = Math.Max(0, durationMs);
        if (holder.Duration > holder.MaxDuration)
        {
            holder.MaxDuration = holder.Duration;
        }

        SendAuraDuration(holder);
        return true;
    }

    /// <summary>
    /// Move the stack count of <paramref name="holder"/> to <paramref name="stacks"/> within the spell's limit (vmangos SetStackAmount: the
    /// amounts scale, an increase refreshes the duration). Returns the stack count it ended at; 0 when the holder is removed or the spell
    /// does not stack.
    /// </summary>
    public int GmSetAuraStacks(SpellAuraHolder holder, int stacks)
    {
        ArgumentNullException.ThrowIfNull(holder);
        if (holder.IsRemoved || holder.Spell.StackAmount == 0 || stacks < 1)
        {
            return 0;
        }

        int delta = Math.Min(stacks, (int)holder.Spell.StackAmount) - holder.StackAmount;
        if (delta != 0 && ModStackAmount(holder, delta))
        {
            return 0;
        }

        return holder.StackAmount;
    }
}
