using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Combat;

public sealed partial class MapCombat
{
    /// <summary>Raised after SELF_RESURRECT has restored all vitals and removed the body.</summary>
    public event Action<Player>? PlayerSelfResurrected;

    /// <summary>
    /// vmangos EffectSelfResurrect: a negative effect value means flat health and MiscValue
    /// mana; otherwise both are percentages. Dither, cap to current maxima, empty rage and
    /// fill energy. The shared revive clears ghost/root and pending external offers.
    /// </summary>
    public bool ResurrectSelf(Player player, int effectValue, int miscMana, Random random)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(random);
        // Upstream IsAlive tests the death state alone. Health can be zero for a 0% spell;
        // it must still count as a completed revival and must not replay the effect.
        if (IsAliveState(player) || !player.IsInWorld || !ReferenceEquals(player.Map, _map)
            || player.IsQuestSettlementPending)
        {
            return false;
        }

        float health = effectValue < 0 ? -(long)effectValue : effectValue / 100.0f * player.MaxHealth;
        float mana = effectValue < 0 ? miscMana : effectValue / 100.0f * GetMaxPower(player, PowerType.Mana);
        ResurrectPlayer(player, 0, applySickness: false);
        player.Health = DitherAndClamp(health, player.MaxHealth, random);
        SetPower(player, PowerType.Mana, DitherAndClamp(mana, GetMaxPower(player, PowerType.Mana), random));
        SetPower(player, PowerType.Rage, 0);
        SetPower(player, PowerType.Energy, GetMaxPower(player, PowerType.Energy));
        if (player.Combat.Corpse is { } corpse)
        {
            RemoveCorpse(corpse);
            player.Combat.Corpse = null;
        }

        PlayerSelfResurrected?.Invoke(player);
        return true;
    }

    private static uint DitherAndClamp(float value, uint maximum, Random random)
    {
        // Preserve fractional rand_ditheru rounding while avoiding signed-int conversion
        // and overflow for int.MinValue flat health or unusually large fixture values.
        double rounded = Math.Floor(Math.Max(value, 0f) + random.NextDouble());
        return (uint)Math.Min(rounded, maximum);
    }
}
