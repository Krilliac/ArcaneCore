using ArcaneCore.Game.Combat.Threat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;

namespace ArcaneCore.Game.Combat;

/// <summary>
/// One entry of a <see cref="ThreatList"/> (vmangos HostileReference). <see cref="Threat"/> already includes
/// <see cref="TempThreat"/>: like vmangos, the temporary (taunt / Fade) modifier is folded into the stored threat and only
/// remembered so it can be taken out again (Threat/ThreatManager.h setTempThreat / resetTempThreat).
/// </summary>
public sealed class ThreatEntry(Unit target, float threat)
{
    public Unit Target { get; } = target;

    public float Threat { get; internal set; } = threat;

    /// <summary>The temporary modifier currently folded into <see cref="Threat"/> (vmangos iTempThreatModifyer); 0 when unused.</summary>
    public float TempThreat { get; internal set; }

    /// <summary>
    /// False while the target cannot be attacked (a game master, a taxi passenger): the entry then lives in the offline list
    /// and is never chosen as victim (vmangos HostileReference::updateOnlineStatus, iOnline).
    /// </summary>
    public bool IsOnline { get; internal set; } = true;
}

/// <summary>
/// A non-player unit's threat list (vmangos ThreatManager / ThreatContainer). Entries are kept
/// sorted by threat, highest first. Every entry is mirrored in the target's
/// <see cref="UnitCombat.ThreatenedBy"/> set (vmangos HostileRefManager) so a dying or
/// leaving unit can be dropped from every list that holds it. Entries whose target is offline
/// (<see cref="ThreatEntry.IsOnline"/>) are held apart in <see cref="OfflineEntries"/>
/// (vmangos iThreatOfflineContainer).
/// </summary>
public sealed class ThreatList
{
    private readonly Unit _owner;
    private readonly List<ThreatEntry> _entries = [];
    private readonly List<ThreatEntry> _offline = [];
    private readonly List<ObjectGuid> _tauntCasters = [];
    private Unit? _currentVictim;
    private bool _dirty;

    internal ThreatList(Unit owner) => _owner = owner;

    /// <summary>The online entries, highest threat first (vmangos ThreatContainer::getThreatList after update()).</summary>
    public IReadOnlyList<ThreatEntry> Entries
    {
        get
        {
            Resort();
            return _entries;
        }
    }

    /// <summary>The offline entries (game master, taxi passenger); they keep their threat but are never selected.</summary>
    public IReadOnlyList<ThreatEntry> OfflineEntries => _offline;

    /// <summary>True when there is no entry at all, online or offline.</summary>
    public bool IsEmpty => _entries.Count == 0 && _offline.Count == 0;

    /// <summary>The victim chosen by the last <see cref="SelectVictim"/>.</summary>
    public Unit? CurrentVictim => _currentVictim;

    /// <summary>The threat held for <paramref name="target"/>, online or offline entry; 0 without an entry.</summary>
    public float GetThreat(Unit target) => (Find(_entries, target) ?? Find(_offline, target))?.Threat ?? 0f;

    /// <summary>vmangos ThreatManager::getThreat(victim, alsoSearchOfflineList=false): online entries only.</summary>
    public float GetOnlineThreat(Unit target) => Find(_entries, target)?.Threat ?? 0f;

    /// <summary>True when <paramref name="target"/> has an entry, online or offline.</summary>
    public bool Contains(Unit target) => Find(_entries, target) is not null || Find(_offline, target) is not null;

    /// <summary>The entry for <paramref name="target"/> (online first, then offline), or null.</summary>
    public ThreatEntry? FindEntry(Unit target) => Find(_entries, target) ?? Find(_offline, target);

    /// <summary>
    /// Add threat (vmangos Unit::AddThreat → ThreatManager::addThreat, Objects/Unit.cpp:7424-7432, Threat/ThreatManager.cpp:391-447):
    /// ignored for units that cannot hold a list (<see cref="ThreatRules.CanHaveThreatList"/>), for the owner itself, dead
    /// targets and game masters; a new target is inserted, an existing one accumulates (online entry first, then offline).
    /// Threat never drops below zero. A non-negative change also gives the target's owner an entry (a pet that attacks pulls
    /// its master onto the list, ThreatManager.cpp:117-122).
    /// </summary>
    public void AddThreat(Unit target, float threat) => AddThreat(target, threat, ThreatContext.Default);

    /// <summary>
    /// <see cref="AddThreat(Unit, float)"/> with the context of the threat source (vmangos addThreat's isAssistThreat and
    /// the EX_NO_THREAT "never create a new reference" rule, ThreatManager.cpp:414-424).
    /// </summary>
    public void AddThreat(Unit target, float threat, ThreatContext context)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (ReferenceEquals(target, _owner) || !target.IsAlive || target is Player { IsGameMaster: true })
        {
            return;
        }

        if (!ThreatRules.CanHaveThreatList(_owner))
        {
            return;
        }

        if (context.IsAssist && ThreatRules.IsAssistThreatSuppressed(_owner))
        {
            threat = 0f;
        }

        ThreatEntry? entry = Find(_entries, target) ?? Find(_offline, target);
        if (entry is null)
        {
            if (context.NoNewEntry)
            {
                return;
            }

            entry = new ThreatEntry(target, 0f);
            _entries.Add(entry);
            target.Combat.ThreatenedByInternal.Add(_owner);
            _dirty = true;
        }

        ApplyChange(entry, threat);
    }

    /// <summary>
    /// vmangos HostileReference::addThreat: add, clamp at zero, put an offline entry back online when its target is
    /// attackable again, and (for a non-negative change) give the target's owner an entry of its own.
    /// </summary>
    private void ApplyChange(ThreatEntry entry, float change)
    {
        entry.Threat = Math.Max(0f, entry.Threat + change);
        if (!entry.IsOnline)
        {
            UpdateOnlineStatus(entry);
        }

        if (change != 0f)
        {
            _dirty = true;
        }

        if (change >= 0f && entry.IsOnline && entry.Target.GetOwner() is { } owner && !ReferenceEquals(owner, _owner))
        {
            AddThreat(owner, 0f);
        }
    }

    /// <summary>Drop one target (vmangos HostileReference removal).</summary>
    public void Remove(Unit target)
    {
        ThreatEntry? entry = Find(_entries, target);
        if (entry is not null)
        {
            _entries.Remove(entry);
        }
        else
        {
            entry = Find(_offline, target);
            if (entry is null)
            {
                return;
            }

            _offline.Remove(entry);
        }

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

        foreach (ThreatEntry entry in _offline)
        {
            entry.Target.Combat.ThreatenedByInternal.Remove(_owner);
        }

        _entries.Clear();
        _offline.Clear();
        _currentVictim = null;
        _dirty = false;
    }

    /// <summary>
    /// vmangos ThreatContainer::modifyThreatPercent (Threat/ThreatManager.cpp:250-262): below -100 the entry is removed, -100
    /// zeroes it exactly, anything else adds that percent of the stored threat. A target without an entry is left alone.
    /// </summary>
    public void ModifyThreatPercent(Unit target, int percent)
    {
        ThreatEntry? entry = Find(_entries, target);
        if (entry is null)
        {
            return;
        }

        if (percent < -100)
        {
            Remove(target);
            return;
        }

        ApplyChange(entry, percent == -100 ? -entry.Threat : entry.Threat * percent / 100.0f);
    }

    /// <summary>
    /// vmangos HostileReference::addThreatPercent (Threat/ThreatManager.h): add <paramref name="percent"/> of the stored threat
    /// (-100 zeroes exactly); unlike <see cref="ModifyThreatPercent"/> a value below -100 only clamps at zero.
    /// </summary>
    public void ScaleThreat(Unit target, int percent)
    {
        if (Find(_entries, target) is { } entry)
        {
            ApplyChange(entry, percent == -100 ? -entry.Threat : entry.Threat * percent / 100.0f);
        }
    }

    /// <summary>
    /// vmangos ThreatManager::tauntApply (Threat/ThreatManager.cpp:481-492): when the taunter holds less threat than the
    /// current victim and has no temporary modifier yet, lift it to the victim's threat. A taunter that is already ahead, or
    /// that already carries a temporary modifier, changes nothing (no stacking).
    /// </summary>
    public void TauntApply(Unit taunter)
    {
        ThreatEntry? entry = Find(_entries, taunter);
        ThreatEntry? current = _currentVictim is null ? null : Find(_entries, _currentVictim);
        if (entry is null || current is null || entry.Threat >= current.Threat || entry.TempThreat != 0f)
        {
            return;
        }

        SetTempThreat(entry, current.Threat);
    }

    /// <summary>vmangos ThreatManager::tauntFadeOut (Threat/ThreatManager.cpp:496-500): take the temporary modifier out again.</summary>
    public void TauntFadeOut(Unit taunter)
    {
        if (Find(_entries, taunter) is { } entry)
        {
            ResetTempThreat(entry);
        }
    }

    /// <summary>
    /// vmangos HostileReference::setTempThreatModifier (Threat/ThreatManager.h): fold <paramref name="modifier"/> into the
    /// threat of one target and remember it. Used for Fade-style "reduce all threat" auras (HostileRefManager::addTempThreat).
    /// A target that already carries a modifier is left alone.
    /// </summary>
    public void ApplyTempThreatModifier(Unit target, float modifier)
    {
        if (Find(_entries, target) is { } entry && entry.TempThreat == 0f)
        {
            entry.TempThreat = modifier;
            ApplyChange(entry, modifier);
        }
    }

    /// <summary>vmangos HostileReference::resetTempThreat: remove the remembered modifier.</summary>
    public void ResetTempThreat(Unit target)
    {
        if (Find(_entries, target) is { } entry)
        {
            ResetTempThreat(entry);
        }
    }

    private void SetTempThreat(ThreatEntry entry, float threat)
    {
        // HostileReference::setTempThreat: iTempThreatModifyer = threat - getThreat(); addThreat(modifier).
        float modifier = threat - entry.Threat;
        entry.TempThreat = modifier;
        if (modifier != 0f)
        {
            ApplyChange(entry, modifier);
        }
    }

    private void ResetTempThreat(ThreatEntry entry)
    {
        if (entry.TempThreat != 0f)
        {
            float modifier = entry.TempThreat;
            entry.TempThreat = 0f;
            ApplyChange(entry, -modifier);
        }
    }

    /// <summary>vmangos ThreatManager::setCurrentVictimIfCan: make an entry the current victim without any comparison.</summary>
    public void SetCurrentVictimIfCan(Unit target)
    {
        if (Find(_entries, target) is not null)
        {
            _currentVictim = target;
        }
    }

    /// <summary>
    /// Re-check one entry's online state (vmangos HostileReference::updateOnlineStatus, ThreatManager.cpp:128-149): a game
    /// master target is offline, so is a target for which <see cref="ThreatRules.IsTargetUnreachable"/> holds. Moves the entry
    /// between the online and the offline list; an offline current victim is dropped (ThreatManager.cpp:526-543).
    /// </summary>
    public void UpdateOnlineStatus(Unit target)
    {
        ThreatEntry? entry = Find(_entries, target) ?? Find(_offline, target);
        if (entry is not null)
        {
            UpdateOnlineStatus(entry);
        }
    }

    private void UpdateOnlineStatus(ThreatEntry entry)
    {
        bool online = !ThreatRules.IsTargetUnreachable(entry.Target);
        if (online == entry.IsOnline)
        {
            return;
        }

        entry.IsOnline = online;
        if (!online)
        {
            if (ReferenceEquals(_currentVictim, entry.Target))
            {
                _currentVictim = null;
            }

            _entries.Remove(entry);
            _offline.Add(entry);
        }
        else
        {
            _offline.Remove(entry);
            _entries.Add(entry);
        }

        _dirty = true;
    }

    // --- taunt caster list (vmangos Unit::m_tauntGuids) -------------------------------

    /// <summary>True while any MOD_TAUNT aura of this creature is active (vmangos HasAuraType(SPELL_AURA_MOD_TAUNT) on the list).</summary>
    public bool HasTauntCasters => _tauntCasters.Count > 0;

    /// <summary>
    /// vmangos Unit::AddTauntCaster (m_tauntGuids.push_back): the latest taunter sits last and is preferred. GUIDs, like
    /// vmangos, so a taunter that left the world can still be taken out when its aura ends.
    /// </summary>
    public void AddTauntCaster(ObjectGuid taunter) => _tauntCasters.Add(taunter);

    /// <summary>vmangos Unit::RemoveTauntCaster: removes one occurrence, from the front.</summary>
    public void RemoveTauntCaster(ObjectGuid taunter) => _tauntCasters.Remove(taunter);

    /// <summary>
    /// vmangos Unit::GetTauntTarget (Objects/Unit.cpp:7529-7542): the latest taunter that can be found (<paramref name="resolve"/>
    /// is the map's unit lookup) and still is a valid attack target.
    /// </summary>
    public Unit? GetTauntTarget(Func<ObjectGuid, Unit?> resolve, Func<Unit, bool> isValidAttackTarget)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(isValidAttackTarget);
        for (int i = _tauntCasters.Count - 1; i >= 0; i--)
        {
            if (resolve(_tauntCasters[i]) is { } taunter && isValidAttackTarget(taunter))
            {
                return taunter;
            }
        }

        return null;
    }

    /// <summary>
    /// Pick the victim (vmangos ThreatContainer::selectNextVictim, Threat/ThreatManager.cpp:286-370). Two passes: the first
    /// skips second-choice targets (<paramref name="isLowPriority"/>: damage immune, feared and other secondary-threat
    /// targets, unreachable for a rooted attacker); when it finds nobody the second pass accepts them. The current victim keeps
    /// aggro until another target exceeds 110% of its threat while in melee range of the owner or 130% at any range.
    /// Targets failing <paramref name="isValid"/> are skipped (and an invalid or low-priority current victim stops being the
    /// comparison baseline). When <paramref name="isOutOfArea"/> answers true for a target reached before a victim is found, the
    /// selection is abandoned and null is returned (ThreatManager.cpp:308-312: an out-of-threat-area target makes the creature
    /// evade).
    /// </summary>
    public Unit? SelectVictim(Func<Unit, bool> isValid, Func<Unit, bool> isInMeleeRange, Func<Unit, bool>? isOutOfArea = null, Func<Unit, bool>? isLowPriority = null)
    {
        ArgumentNullException.ThrowIfNull(isValid);
        ArgumentNullException.ThrowIfNull(isInMeleeRange);

        Resort();
        ThreatEntry? current = _currentVictim is null ? null : Find(_entries, _currentVictim);
        ThreatEntry? result = null;
        for (int attempt = 0; attempt < 2 && result is null; attempt++)
        {
            bool allowLowPriority = attempt == 1;
            foreach (ThreatEntry entry in _entries)
            {
                if (isOutOfArea is not null && isOutOfArea(entry.Target))
                {
                    _currentVictim = null;
                    return null;
                }

                if (!isValid(entry.Target))
                {
                    if (ReferenceEquals(entry, current))
                    {
                        current = null;
                    }

                    continue;
                }

                if (!allowLowPriority && isLowPriority is not null && isLowPriority(entry.Target))
                {
                    // the current victim is a second-choice target: do not compare threat with it below
                    if (ReferenceEquals(entry, current))
                    {
                        current = null;
                    }

                    continue;
                }

                if (current is null)
                {
                    result = entry; // highest valid
                    break;
                }

                if (ReferenceEquals(entry, current) || entry.Threat <= current.Threat * 1.1f)
                {
                    result = current; // nobody above it overtook it
                    break;
                }

                if (entry.Threat > current.Threat * 1.3f || (entry.Threat > current.Threat * 1.1f && isInMeleeRange(entry.Target)))
                {
                    result = entry;
                    break;
                }
            }
        }

        _currentVictim = result?.Target;
        return _currentVictim;
    }

    private static ThreatEntry? Find(List<ThreatEntry> list, Unit target)
    {
        foreach (ThreatEntry entry in list)
        {
            if (ReferenceEquals(entry.Target, target))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>
    /// vmangos ThreatContainer::update (Threat/ThreatManager.cpp:275-280): re-sort only when something changed. A stable
    /// insertion sort (equal threat keeps insertion order); the list is nearly sorted after one change, so this is O(n) per
    /// resort in the common case.
    /// </summary>
    private void Resort()
    {
        if (!_dirty)
        {
            return;
        }

        _dirty = false;
        for (int i = 1; i < _entries.Count; i++)
        {
            ThreatEntry item = _entries[i];
            int j = i - 1;
            while (j >= 0 && _entries[j].Threat < item.Threat)
            {
                _entries[j + 1] = _entries[j];
                j--;
            }

            _entries[j + 1] = item;
        }
    }
}
