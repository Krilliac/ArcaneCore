using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

/// <summary>What a persisted cooldown row restarts.</summary>
public enum SpellCooldownKind : byte
{
    /// <summary>A spell's own cooldown (vmangos character_spell_cooldown).</summary>
    Spell = 0,

    /// <summary>A Spell.dbc Category cooldown.</summary>
    Category = 1,
}

/// <summary>A cooldown saved at logout, as an absolute wall-clock end (cooldowns keep running offline, vmangos _SaveSpellCooldowns).</summary>
public readonly record struct PersistedCooldown(SpellCooldownKind Kind, uint Id, long EndsAtUnixMs);

/// <summary>
/// An aura saved at logout (vmangos character_aura: caster_guid, spell, stackcount, remaincharges,
/// basepoints0-2, periodictime0-2, maxduration, remaintime, effIndexMask). The caster GUID is
/// provenance only: a restored aura never regains a foreign caster's ownership.
/// </summary>
public sealed record PersistedAura
{
    public uint SpellId { get; init; }

    public ObjectGuid CasterGuid { get; init; }

    public byte CasterLevel { get; init; }

    public byte StackAmount { get; init; } = 1;

    public int Charges { get; init; }

    /// <summary>-1 when permanent.</summary>
    public int MaxDurationMs { get; init; }

    /// <summary>-1 when permanent.</summary>
    public int RemainingMs { get; init; }

    /// <summary>Which effect indices carry an aura (bit i = effect i).</summary>
    public byte EffectMask { get; init; }

    /// <summary>Per effect index: the modifier amount (stack-multiplied, as applied).</summary>
    public IReadOnlyList<int> Amounts { get; init; } = [0, 0, 0];

    /// <summary>Per effect index: ms to the next periodic tick.</summary>
    public IReadOnlyList<int> PeriodicTimers { get; init; } = [0, 0, 0];

    /// <summary>Wall clock of the save; harmful auras keep counting down offline (cmangos Player::_LoadAuras).</summary>
    public long SavedAtUnixMs { get; init; }
}

/// <summary>The spell state of one character kept across logout.</summary>
public sealed record SpellStateSnapshot(IReadOnlyList<PersistedCooldown> Cooldowns, IReadOnlyList<PersistedAura> Auras)
{
    public static SpellStateSnapshot Empty { get; } = new([], []);

    public bool IsEmpty => Cooldowns.Count == 0 && Auras.Count == 0;
}

public sealed partial class SpellSystem
{
    /// <summary>
    /// Capture the state of <paramref name="unit"/> to persist at logout (vmangos Player::_SaveAuras +
    /// _SaveSpellCooldowns): running spell and category cooldowns as absolute wall-clock ends, and
    /// every saveable aura (not passive, not channeled, not a party area aura received from
    /// someone else) with remaining duration, stacks, charges, per-effect amounts and periodic
    /// timers. Global cooldowns and interrupt lockouts are not saved. World thread.
    /// </summary>
    public SpellStateSnapshot CaptureState(Unit unit, long nowUnixMs)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (GetState(unit.Guid) is not { } state || !ReferenceEquals(state.Unit, unit))
        {
            return SpellStateSnapshot.Empty;
        }

        uint now = NowMs;
        var cooldowns = new List<PersistedCooldown>();
        foreach ((uint spellId, uint until) in state.SpellCooldowns.OrderBy(c => c.Key))
        {
            if (until > now)
            {
                cooldowns.Add(new PersistedCooldown(SpellCooldownKind.Spell, spellId, nowUnixMs + (until - now)));
            }
        }

        foreach ((uint category, uint until) in state.CategoryCooldowns.OrderBy(c => c.Key))
        {
            if (until > now)
            {
                cooldowns.Add(new PersistedCooldown(SpellCooldownKind.Category, category, nowUnixMs + (until - now)));
            }
        }

        var auras = new List<PersistedAura>();
        foreach (SpellAuraHolder holder in state.Auras)
        {
            if (!holder.IsSaveable || (!holder.IsPermanent && holder.Duration <= 0))
            {
                continue;
            }

            byte mask = 0;
            int[] amounts = new int[SpellConstants.MaxEffects];
            int[] timers = new int[SpellConstants.MaxEffects];
            for (int i = 0; i < SpellConstants.MaxEffects; i++)
            {
                if (holder.Auras[i] is { } aura)
                {
                    mask |= (byte)(1 << i);
                    amounts[i] = aura.Amount;
                    timers[i] = aura.PeriodicTimer;
                }
            }

            auras.Add(new PersistedAura
            {
                SpellId = holder.Spell.Id,
                CasterGuid = holder.CasterGuid,
                CasterLevel = holder.CasterLevel,
                StackAmount = holder.StackAmount,
                Charges = holder.Charges,
                MaxDurationMs = holder.IsPermanent ? -1 : holder.MaxDuration,
                RemainingMs = holder.IsPermanent ? -1 : holder.Duration,
                EffectMask = mask,
                Amounts = amounts,
                PeriodicTimers = timers,
                SavedAtUnixMs = nowUnixMs,
            });
        }

        return new SpellStateSnapshot(cooldowns, auras);
    }

    /// <summary>
    /// Restart persisted cooldowns on a freshly loaded unit (before SMSG_INITIAL_SPELLS so the client
    /// sees them). Expired rows and unknown spells are skipped. Returns the number restored.
    /// </summary>
    public int RestoreCooldowns(Unit unit, IEnumerable<PersistedCooldown> cooldowns, long nowUnixMs)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(cooldowns);
        UnitSpellState? state = null;
        int restored = 0;
        uint now = NowMs;
        foreach (PersistedCooldown cooldown in cooldowns)
        {
            long remaining = cooldown.EndsAtUnixMs - nowUnixMs;
            if (remaining <= 0 || (cooldown.Kind == SpellCooldownKind.Spell && Store.Get(cooldown.Id) is null))
            {
                continue;
            }

            state ??= GetOrCreateState(unit);
            uint until = (uint)Math.Min(uint.MaxValue, now + remaining);
            Dictionary<uint, uint> table = cooldown.Kind == SpellCooldownKind.Spell ? state.SpellCooldowns : state.CategoryCooldowns;
            table[cooldown.Id] = Math.Max(until, table.GetValueOrDefault(cooldown.Id));
            restored++;
        }

        return restored;
    }

    /// <summary>
    /// Re-apply persisted auras on a unit that has entered its map (vmangos Player::_LoadAuras,
    /// re-implemented). Ownership follows docs/integration/aura-caster-ownership.md: an aura the
    /// unit cast on itself gets the unit's fresh ownership token; an aura from any other caster
    /// keeps its caster GUID and level as provenance but receives a permanently revoked token, so
    /// periodic effects use the target fallback and a later cast by that caster (even the same GUID)
    /// replaces it instead of stacking. Harmful auras lose the offline time; beneficial auras keep
    /// their remaining time. Unknown or passive spells, expired rows, and effect indices whose
    /// spell effect is no longer an aura are skipped. Returns the holders restored.
    /// </summary>
    public IReadOnlyList<SpellAuraHolder> RestoreAuras(Unit unit, IEnumerable<PersistedAura> auras, long nowUnixMs)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(auras);
        var restored = new List<SpellAuraHolder>();
        foreach (PersistedAura saved in auras)
        {
            if (Store.Get(saved.SpellId) is not { IsPassive: false, IsChanneled: false } spell)
            {
                continue;
            }

            bool permanent = saved.MaxDurationMs == -1 || saved.RemainingMs == -1;
            int remaining = saved.RemainingMs;
            if (!permanent)
            {
                if (!spell.IsPositive)
                {
                    long offline = Math.Max(0, nowUnixMs - saved.SavedAtUnixMs);
                    remaining = (int)Math.Max(0, remaining - Math.Min(offline, int.MaxValue));
                }

                if (remaining <= 0)
                {
                    continue;
                }
            }

            bool selfCast = saved.CasterGuid == unit.Guid;
            AuraCasterOwner owner = selfCast
                ? _auraCasterOwners.GetValue(unit, static caster => new AuraCasterOwner(caster))
                : AuraCasterOwner.Orphaned();
            var holder = new SpellAuraHolder(spell, unit, saved.CasterGuid, selfCast ? unit.Level : saved.CasterLevel, owner,
                permanent ? -1 : Math.Max(saved.MaxDurationMs, remaining));
            for (int i = 0; i < SpellConstants.MaxEffects; i++)
            {
                SpellEffectInfo effect = spell.Effects[i];
                if ((saved.EffectMask & (1 << i)) == 0
                    || effect.Effect is not (SpellEffectName.ApplyAura or SpellEffectName.ApplyAreaAuraParty))
                {
                    continue;
                }

                var aura = new SpellAura(i, effect.AuraType, saved.Amounts.ElementAtOrDefault(i), effect.Amplitude, effect.MiscValue);
                int timer = saved.PeriodicTimers.ElementAtOrDefault(i);
                if (aura.IsPeriodic && timer > 0 && timer <= (int)aura.Amplitude)
                {
                    aura.PeriodicTimer = timer;
                }

                holder.SetAura(aura);
            }

            if (holder.IsEmpty)
            {
                continue;
            }

            if (!permanent)
            {
                holder.Duration = remaining;
            }

            holder.StackAmount = (byte)Math.Clamp((int)saved.StackAmount, 1, Math.Max(1, (int)spell.StackAmount));
            holder.Charges = Math.Max(0, saved.Charges);
            AddAuraHolder(holder);
            if (!holder.IsRemoved)
            {
                restored.Add(holder);
            }
        }

        return restored;
    }

    /// <summary>Whether a holder's caster ownership is a live, unrevoked token (tests and diagnostics).</summary>
    public static bool HasLiveCasterOwnership(SpellAuraHolder holder)
    {
        ArgumentNullException.ThrowIfNull(holder);
        return holder.CasterOwner.Caster is not null;
    }

    /// <summary>The unit an aura's periodic effects are attributed to now: its exact valid caster, else the target (fallback).</summary>
    public Unit ResolveAuraActor(SpellAuraHolder holder)
    {
        ArgumentNullException.ThrowIfNull(holder);
        return ResolveAuraCaster(holder) ?? holder.Target;
    }
}
