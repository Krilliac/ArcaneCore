using System.Collections.Frozen;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// The resolved spell table plus the per-race/class starting spells and fixed teleport
/// destinations. Immutable after construction; shared by every map.
/// </summary>
public sealed class SpellStore
{
    public sealed record ScriptTarget(uint SpellId, uint Type, uint TargetEntry, uint InverseEffectMask);
    public static readonly SpellStore Empty = new([], [], []);

    private readonly FrozenDictionary<uint, SpellInfo> _spells;
    private readonly FrozenDictionary<(byte Race, byte Class), uint[]> _createSpells;
    private readonly FrozenDictionary<uint, SpellTargetPosition> _targetPositions;
    private readonly FrozenDictionary<uint, ScriptTarget[]> _scriptTargets;

    public SpellStore(
        IEnumerable<SpellInfo> spells,
        IEnumerable<(byte Race, byte Class, uint SpellId)> createSpells,
        IEnumerable<(uint SpellId, SpellTargetPosition Position)> targetPositions,
        IEnumerable<ScriptTarget>? scriptTargets = null)
    {
        ArgumentNullException.ThrowIfNull(spells);
        ArgumentNullException.ThrowIfNull(createSpells);
        ArgumentNullException.ThrowIfNull(targetPositions);
        _spells = spells.ToFrozenDictionary(s => s.Id);
        _createSpells = createSpells
            .GroupBy(c => (c.Race, c.Class))
            .ToFrozenDictionary(g => g.Key, g => g.Select(c => c.SpellId).Distinct().ToArray());
        _targetPositions = targetPositions.ToFrozenDictionary(t => t.SpellId, t => t.Position);
        _scriptTargets = (scriptTargets ?? []).GroupBy(t => t.SpellId)
            .ToFrozenDictionary(g => g.Key, g => g.ToArray());
    }

    public int Count => _spells.Count;

    public IEnumerable<SpellInfo> All => _spells.Values;

    public SpellInfo? Get(uint spellId) => _spells.GetValueOrDefault(spellId);

    /// <summary>
    /// Spells a new character of this race and class knows (cmangos-classic ObjectMgr::LoadPlayerInfo
    /// reads playercreateinfo_spell by exact race and class; Player::Create → learnDefaultSpells).
    /// </summary>
    public IReadOnlyList<uint> GetCreateSpells(byte race, byte cls)
        => _createSpells.TryGetValue((race, cls), out uint[]? spells) ? spells : [];

    /// <summary>The fixed destination of a TARGET_LOCATION_DATABASE teleport, if any.</summary>
    public SpellTargetPosition? GetTargetPosition(uint spellId)
        => _targetPositions.TryGetValue(spellId, out SpellTargetPosition position) ? position : null;

    public IReadOnlyList<ScriptTarget> GetScriptTargets(uint spellId)
        => _scriptTargets.TryGetValue(spellId, out ScriptTarget[]? targets) ? targets : [];
}
