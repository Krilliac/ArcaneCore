using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Combat;

/// <summary>
/// A player's combo points (vmangos Player::AddComboPoints / ClearComboPoints / SetComboPoints, Player.cpp:19032-19093):
/// up to five points on one target, shown to the client through PLAYER_FIELD_COMBO_TARGET and PLAYER_FIELD_BYTES byte 1
/// (1.12 has no combo point packet). A warrior has one point at most, which is only Overpower's marker: the client shows
/// none for warriors and the server uses it to allow the cast (Spell.cpp:7035-7038).
/// </summary>
public sealed class ComboPointService
{
    /// <summary>The most combo points a player can hold (Player.cpp:19070-19071).</summary>
    public const int MaxComboPoints = 5;

    /// <summary>PLAYER_FIELD_BYTES_OFFSET_COMBO_POINTS (vmangos Player.h:361).</summary>
    private const int ComboPointsByte = 1;

    private sealed class State
    {
        public ObjectGuid Target;
        public int Points;
    }

    private readonly SpellSystem _spells;
    private readonly Func<Player, ObjectGuid, Unit?> _findUnit;
    private readonly ConditionalWeakTable<Player, State> _states = new();
    private readonly Dictionary<ObjectGuid, HashSet<Player>> _holders = [];
    private readonly HashSet<MapCombat> _observed = new(ReferenceEqualityComparer.Instance);

    /// <param name="spells">The spell system (SPELL_AURA_RETAIN_COMBO_POINTS auras are removed on every change).</param>
    /// <param name="findUnit">vmangos ObjectAccessor::GetUnit(*player, guid): the unit with a GUID near the player, or null.</param>
    public ComboPointService(SpellSystem spells, Func<Player, ObjectGuid, Unit?> findUnit)
    {
        _spells = spells ?? throw new ArgumentNullException(nameof(spells));
        _findUnit = findUnit ?? throw new ArgumentNullException(nameof(findUnit));
    }

    /// <summary>
    /// Wire combo points into the spell system: the finishing move check, the value and duration scaling (installed before
    /// any other value modifier is registered), the spend-on-finish observer and SPELL_EFFECT_ADD_COMBO_POINTS.
    /// </summary>
    public void Install()
    {
        _spells.RegisterValueModifier(new ComboValueModifier(this));
        _spells.RegisterCastCheck(new ComboPointCastCheck(this));
        _spells.RegisterObserver(new ComboFinishObserver(this));
        _spells.RegisterEffect(SpellEffectName.AddComboPoints, EffectAddComboPoints);
    }

    /// <summary>vmangos Spell::EffectAddComboPoints (SpellEffects.cpp:4618-4630): a player's spell adds its value in points on the target.</summary>
    private void EffectAddComboPoints(SpellEffectContext context)
    {
        if (context.Caster is not Player player || context.Value <= 0)
        {
            return;
        }

        AddComboPoints(player, context.Target, context.Value);
        player.SetUInt64(UpdateFields.PlayerFieldComboTarget, context.Target.Guid.Value);
    }

    /// <summary>vmangos Player::GetComboPoints.</summary>
    public int GetComboPoints(Player player) => _states.TryGetValue(player, out State? state) ? state.Points : 0;

    /// <summary>vmangos Player::GetComboTargetGuid (empty = none).</summary>
    public ObjectGuid GetComboTarget(Player player) => _states.TryGetValue(player, out State? state) ? state.Target : default;

    /// <summary>
    /// vmangos Player::AddComboPoints: points on the current target add up, a new target restarts the count; capped to
    /// 0-5. Combo-retaining auras end first.
    /// </summary>
    public void AddComboPoints(Player player, Unit target, int count)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(target);
        if (count == 0)
        {
            return;
        }

        RemoveRetainAuras(player);
        State state = _states.GetOrCreateValue(player);
        if (target.Guid == state.Target)
        {
            state.Points += count;
        }
        else
        {
            if (!state.Target.IsEmpty)
            {
                RemoveHolder(state.Target, player);
            }

            state.Target = target.Guid;
            state.Points = count;
            AddHolder(target.Guid, player);
        }

        state.Points = Math.Clamp(state.Points, 0, MaxComboPoints);
        WriteFields(player, state);
    }

    /// <summary>vmangos Player::ClearComboPoints.</summary>
    public void ClearComboPoints(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (!_states.TryGetValue(player, out State? state) || state.Target.IsEmpty)
        {
            return;
        }

        RemoveRetainAuras(player);
        state.Points = 0;
        WriteFields(player, state);
        RemoveHolder(state.Target, player);
        state.Target = default;
    }

    /// <summary>
    /// A unit died or left the world: every player with combo points on it loses them (vmangos
    /// Unit::ClearComboPointHolders, Unit.cpp:9410-9422).
    /// </summary>
    public void OnTargetGone(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (!_holders.TryGetValue(unit.Guid, out HashSet<Player>? players))
        {
            return;
        }

        foreach (Player player in players.ToArray())
        {
            if (GetComboTarget(player) == unit.Guid)
            {
                ClearComboPoints(player);
            }
            else
            {
                players.Remove(player);
            }
        }

        if (players.Count == 0)
        {
            _holders.Remove(unit.Guid);
        }
    }

    /// <summary>The player's own death or logout: points on any target are lost (Player.cpp:1519).</summary>
    public void OnPlayerGone(Player player) => ClearComboPoints(player);

    /// <summary>
    /// Follow the deaths of one map: the victim's holders lose their points, and a dying player loses their own
    /// (<see cref="OnTargetGone"/>, <see cref="OnPlayerGone"/>). Observing a map twice is harmless.
    /// </summary>
    public void Observe(MapCombat combat)
    {
        ArgumentNullException.ThrowIfNull(combat);
        if (!_observed.Add(combat))
        {
            return;
        }

        combat.UnitKilled += (_, victim) =>
        {
            OnTargetGone(victim);
            if (victim is Player player)
            {
                OnPlayerGone(player);
            }
        };
    }

    /// <summary>vmangos Player::SetComboPoints: the client fields are only written while the target can still be found.</summary>
    private void WriteFields(Player player, State state)
    {
        if (_findUnit(player, state.Target) is null)
        {
            return;
        }

        player.SetUInt64(UpdateFields.PlayerFieldComboTarget, state.Target.Value);
        player.SetByte(UpdateFields.PlayerFieldBytes, ComboPointsByte, (byte)state.Points);
    }

    /// <summary>vmangos RemoveSpellsCausingAura(SPELL_AURA_RETAIN_COMBO_POINTS).</summary>
    private void RemoveRetainAuras(Player player)
    {
        foreach (SpellAuraHolder holder in _spells.GetAuras(player).Where(h => !h.IsRemoved && h.HasAura(AuraType.RetainComboPoints)).ToArray())
        {
            _spells.RemoveAuras(player, holder.Spell.Id);
        }
    }

    private void AddHolder(ObjectGuid target, Player player)
    {
        if (!_holders.TryGetValue(target, out HashSet<Player>? players))
        {
            _holders[target] = players = new HashSet<Player>(ReferenceEqualityComparer.Instance);
        }

        players.Add(player);
    }

    private void RemoveHolder(ObjectGuid target, Player player)
    {
        if (_holders.TryGetValue(target, out HashSet<Player>? players))
        {
            players.Remove(player);
            if (players.Count == 0)
            {
                _holders.Remove(target);
            }
        }
    }
}

/// <summary>
/// A finishing move needs combo points on its explicit target (vmangos Spell::CheckPower, Spell.cpp:7035-7038): without
/// them a rogue gets NO_COMBO_POINTS and a warrior BAD_TARGETS. Triggered casts skip it.
/// </summary>
public sealed class ComboPointCastCheck(ComboPointService combos) : ISpellCastCheck
{
    public SpellCheckPhase Phase => SpellCheckPhase.Power;

    public int Order => SpellCastCheckOrder.ComboPoints;

    public SpellCastResult Check(in SpellCastCheckContext context)
    {
        if (context.Triggered || context.Caster is not Player player || !context.Spell.NeedsComboPoints
            || !SpellSystem.IsExplicitUnitTarget(context.Spell.Effects[0].TargetA))
        {
            return SpellCastResult.CastOk;
        }

        if (context.Target is { } target && target.Guid == combos.GetComboTarget(player))
        {
            return SpellCastResult.CastOk;
        }

        return player.Class == Class.Warrior ? SpellCastResult.BadTargets : SpellCastResult.NoComboPoints;
    }
}

/// <summary>
/// Combo points scale spells: an effect adds <c>EffectPointsPerComboPoint x points</c> on the combo target (vmangos
/// SpellCaster::CalculateSpellEffectValue, SpellCaster.cpp:1190-1192), and a duration stretches toward its maximum by
/// points / 5 (SpellEntry::CalculateDuration, SpellEntry.cpp:731-735). Register it before other value modifiers.
/// The effect bonus is added to the already truncated value, so a fractional base and a fractional bonus can differ by 1
/// from vmangos' single float sum.
/// </summary>
public sealed class ComboValueModifier(ComboPointService combos) : ISpellValueModifier
{
    private const int StretchSteps = 5;

    public int Modify(SpellValueKind kind, in SpellValueContext context, int value)
    {
        if (context.Caster is not Player player)
        {
            return value;
        }

        switch (kind)
        {
            case SpellValueKind.EffectValue when context.EffectIndex is >= 0 and < SpellConstants.MaxEffects:
                float perPoint = context.Spell.Effects[context.EffectIndex].PointsPerComboPoint;
                if (perPoint != 0 && context.Target is { } target && target.Guid == combos.GetComboTarget(player))
                {
                    return value + (int)(perPoint * combos.GetComboPoints(player));
                }

                return value;
            case SpellValueKind.Duration:
                int max = context.Spell.GetMaxDuration();
                return value != max ? value + ((max - value) * combos.GetComboPoints(player) / StretchSteps) : value;
            default:
                return value;
        }
    }
}

/// <summary>
/// A finishing move spends the combo points when the cast ends (vmangos Spell::finish, Spell.cpp:4374-4395), except a
/// harmful one that missed (or was dodged, parried...) a target other than the caster: the points are kept for another try.
/// </summary>
public sealed class ComboFinishObserver(ComboPointService combos) : ISpellCastObserver
{
    private readonly ConditionalWeakTable<SpellCast, object> _missed = new();

    public void OnTargetOutcome(SpellCast cast, SpellTargetOutcome outcome)
    {
        if (cast.Spell.NeedsComboPoints && outcome.Miss != SpellMissInfo.None && !ReferenceEquals(outcome.Target, cast.Caster))
        {
            _missed.TryAdd(cast, new object());
        }
    }

    public void OnFinished(SpellCast cast, bool completed)
    {
        if (!completed || cast.Caster is not Player player || !cast.Spell.NeedsComboPoints)
        {
            return;
        }

        bool drop = cast.Spell.IsPositive || !_missed.TryGetValue(cast, out _);
        _missed.Remove(cast);
        if (drop)
        {
            combos.ClearComboPoints(player);
        }
    }
}
