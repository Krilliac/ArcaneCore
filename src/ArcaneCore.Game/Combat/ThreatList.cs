using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Combat;

/// <summary>One entry of a <see cref="ThreatList"/>.</summary>
public sealed class ThreatEntry(Unit target, float threat)
{
    public Unit Target { get; } = target;

    public float Threat { get; internal set; } = threat;
}

/// <summary>
/// A non-player unit's threat list (vmangos ThreatManager / ThreatContainer). Entries are kept
/// sorted by threat, highest first. Every entry is mirrored in the target's
/// <see cref="UnitCombat.ThreatenedBy"/> set (vmangos HostileRefManager) so a dying or
/// leaving unit can be dropped from every list that holds it.
/// </summary>
public sealed class ThreatList
{
    private readonly Unit _owner;
    private readonly List<ThreatEntry> _entries = [];
    private Unit? _currentVictim;

    internal ThreatList(Unit owner) => _owner = owner;

    public IReadOnlyList<ThreatEntry> Entries => _entries;

    public bool IsEmpty => _entries.Count == 0;

    /// <summary>The victim chosen by the last <see cref="SelectVictim"/>.</summary>
    public Unit? CurrentVictim => _currentVictim;

    public float GetThreat(Unit target) => Find(target)?.Threat ?? 0f;

    /// <summary>
    /// Add threat (vmangos ThreatManager::addThreat): ignored for the owner itself, dead
    /// targets and game masters; a new target is inserted, an existing one accumulates.
    /// Threat never drops below zero.
    /// </summary>
    public void AddThreat(Unit target, float threat)
    {
        if (ReferenceEquals(target, _owner) || !target.IsAlive || target is Player { IsGameMaster: true })
        {
            return;
        }

        ThreatEntry? entry = Find(target);
        if (entry is null)
        {
            entry = new ThreatEntry(target, Math.Max(0f, threat));
            _entries.Add(entry);
            target.Combat.ThreatenedByInternal.Add(_owner);
        }
        else
        {
            entry.Threat = Math.Max(0f, entry.Threat + threat);
        }

        Sort();
    }

    /// <summary>Drop one target (vmangos HostileReference removal).</summary>
    public void Remove(Unit target)
    {
        ThreatEntry? entry = Find(target);
        if (entry is null)
        {
            return;
        }

        _entries.Remove(entry);
        target.Combat.ThreatenedByInternal.Remove(_owner);
        if (ReferenceEquals(_currentVictim, target))
        {
            _currentVictim = null;
        }
    }

    /// <summary>Empty the list (vmangos Unit::DeleteThreatList).</summary>
    public void Clear()
    {
        foreach (ThreatEntry entry in _entries)
        {
            entry.Target.Combat.ThreatenedByInternal.Remove(_owner);
        }

        _entries.Clear();
        _currentVictim = null;
    }

    /// <summary>
    /// Pick the victim (vmangos ThreatContainer::selectNextVictim): the current victim keeps
    /// aggro until another target exceeds 110% of its threat while in melee range of the owner
    /// or 130% at any range. Targets failing <paramref name="isValid"/> are skipped. When <paramref name="isOutOfArea"/> is
    /// given and answers true for a target reached in list order before a victim is found, the selection is abandoned and null is
    /// returned (vmangos ThreatContainer::selectNextVictim, Threat/ThreatManager.cpp:305-312: an out-of-threat-area target
    /// makes the creature evade).
    /// </summary>
    public Unit? SelectVictim(Func<Unit, bool> isValid, Func<Unit, bool> isInMeleeRange, Func<Unit, bool>? isOutOfArea = null)
    {
        ArgumentNullException.ThrowIfNull(isValid);
        ArgumentNullException.ThrowIfNull(isInMeleeRange);

        ThreatEntry? current = _currentVictim is null ? null : Find(_currentVictim);
        if (current is not null && !isValid(current.Target))
        {
            current = null;
        }

        ThreatEntry? result = null;
        foreach (ThreatEntry entry in _entries)
        {
            if (isOutOfArea is not null && isOutOfArea(entry.Target))
            {
                _currentVictim = null;
                return null;
            }

            if (!isValid(entry.Target))
            {
                continue;
            }

            if (current is null)
            {
                result = entry; // highest valid
                break;
            }

            if (ReferenceEquals(entry, current))
            {
                result = current; // nobody above it overtook it
                break;
            }

            float factor = isInMeleeRange(entry.Target) ? 1.1f : 1.3f;
            if (entry.Threat > current.Threat * factor)
            {
                result = entry;
                break;
            }
        }

        _currentVictim = result?.Target;
        return _currentVictim;
    }

    private ThreatEntry? Find(Unit target)
    {
        foreach (ThreatEntry entry in _entries)
        {
            if (ReferenceEquals(entry.Target, target))
            {
                return entry;
            }
        }

        return null;
    }

    private void Sort()
    {
        // Stable: equal threat keeps insertion order (List.Sort is not stable).
        ThreatEntry[] sorted = [.. _entries.OrderByDescending(static e => e.Threat)];
        _entries.Clear();
        _entries.AddRange(sorted);
    }
}
