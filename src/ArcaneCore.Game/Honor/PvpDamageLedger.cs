namespace ArcaneCore.Game.Honor;

/// <summary>
/// The damage a player took, by the player who dealt it (vmangos Unit::m_damageTakenHistory / UnitDamaged,
/// Unit.cpp:342-344, 994): damage from a pet, totem or charm is credited to its controlling player, damage from
/// anything else under guid 0 (it dilutes the total and credits nobody). The history is dropped when more than 60
/// seconds pass without any damage taken; vmangos ages it with a per-unit update timer, here the age is checked
/// lazily against the time of the last hit, which gives the same answer without a per-unit tick.
/// </summary>
public sealed class PvpDamageLedger
{
    /// <summary>The idle time after which the history is cleared (Unit.cpp:343).</summary>
    public const long ExpiryMs = 60_000;

    private readonly Dictionary<ulong, uint> _damage = [];
    private long _lastDamageMs;

    /// <summary>Record damage taken now; a history that has been idle for over a minute starts afresh first.</summary>
    public void Record(ulong attackerGuid, uint damage, long nowMs)
    {
        ExpireIfIdle(nowMs);
        _damage[attackerGuid] = _damage.GetValueOrDefault(attackerGuid) + damage;
        _lastDamageMs = nowMs;
    }

    /// <summary>The current history (empty once expired).</summary>
    public IReadOnlyDictionary<ulong, uint> Snapshot(long nowMs)
    {
        ExpireIfIdle(nowMs);
        return new Dictionary<ulong, uint>(_damage);
    }

    /// <summary>Forget everything (after a death settlement).</summary>
    public void Clear() => _damage.Clear();

    private void ExpireIfIdle(long nowMs)
    {
        if (_damage.Count > 0 && nowMs - _lastDamageMs > ExpiryMs)
        {
            _damage.Clear();
        }
    }
}
