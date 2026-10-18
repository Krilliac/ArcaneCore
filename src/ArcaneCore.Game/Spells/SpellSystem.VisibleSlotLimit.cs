namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>A counter for <see cref="SpellAuraHolder.ApplyOrder"/> (vmangos m_applyTime, which is seconds; an order is exact).</summary>
    private long _auraApplyOrder;

    /// <summary>
    /// vmangos Unit::AddSpellAuraHolder's visible slot limit (Unit.cpp:3284-3305) with RemoveAuraDueToVisibleSlotLimit (:3321-3346): when
    /// <paramref name="holder"/> would be one visible aura of its polarity too many, the least important of them (<see cref="VisibleSlotLimit"/>)
    /// goes. False when that is <paramref name="holder"/> itself, which then is not added (vmangos applies it and removes it at once).
    /// </summary>
    private bool MakeRoomForVisibleAura(UnitSpellState state, SpellAuraHolder holder)
    {
        if (!holder.NeedsVisibleSlot)
        {
            return true;
        }

        bool positive = holder.IsPositive;
        int count = 1;
        foreach (SpellAuraHolder other in state.Auras)
        {
            if (!other.IsRemoved && other.NeedsVisibleSlot && other.IsPositive == positive)
            {
                count++;
            }
        }

        if (count <= VisibleSlotLimit.LimitFor(positive))
        {
            return true;
        }

        SpellAuraHolder useless = holder;
        int uselessScore = VisibleSlotLimit.Score(holder, Store);
        long uselessOrder = long.MaxValue; // the new holder is the latest
        foreach (SpellAuraHolder other in state.Auras)
        {
            if (other.IsRemoved || !other.NeedsVisibleSlot || other.IsPositive != positive)
            {
                continue;
            }

            int score = VisibleSlotLimit.Score(other, Store);
            if (VisibleSlotLimit.IsMoreImportant(useless, uselessScore, uselessOrder, other, score, other.ApplyOrder))
            {
                useless = other;
                uselessScore = score;
                uselessOrder = other.ApplyOrder;
            }
        }

        if (ReferenceEquals(useless, holder))
        {
            return false;
        }

        RemoveHolder(state, useless);
        return true;
    }
}