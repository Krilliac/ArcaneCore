using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Rules;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>Gnomish Mind Control Cap and Reckless Charge, which roll the PvP heartbeat without SPELL_ATTR_HEARTBEAT_RESIST (SpellAuras.cpp:8128-8130).</summary>
    private static bool HasPvpHeartbeat(SpellInfo spell) =>
        ((uint)spell.Attributes & SpellRuleFlags.HeartbeatResist) != 0 || spell.Id is 13181 or 13327;

    /// <summary>
    /// vmangos SpellInternal::IsPvEHeartBeat (SpellMgr.cpp:3413-3431): SPELL_ATTR_HEARTBEAT_RESIST and no fear, root, pacify-silence or
    /// confuse effect (those end through damage and their own break chances instead).
    /// </summary>
    internal static bool IsPveHeartbeat(SpellInfo spell) =>
        ((uint)spell.Attributes & SpellRuleFlags.HeartbeatResist) != 0
        && !spell.HasAura(AuraType.ModFear) && !spell.HasAura(AuraType.ModRoot)
        && !spell.HasAura(AuraType.ModPacifySilence) && !spell.HasAura(AuraType.ModConfuse);

    /// <summary>
    /// vmangos SpellAuraHolder::CalculateHeartBeat (SpellAuras.cpp:8114-8143, called from Aura::Aura for the holder's last aura when it is
    /// harmful, :303-307): a non-permanent holder whose undiminished duration is over 10 s gets a break roll. A player target of a heartbeat
    /// spell draws one random value in [0, 100) (the PvP curve); a creature target of a PvE heartbeat spell with a known caster rolls the
    /// caster's spell hit chance every 5 s.
    /// </summary>
    private void CalculateHeartbeat(SpellAuraHolder holder)
    {
        holder.HeartbeatRandValue = 0f;
        holder.PveHeartbeatTimer = -1;
        if (holder.IsPermanent)
        {
            return;
        }

        SpellAura? last = null;
        foreach (SpellAura? aura in holder.Auras)
        {
            last = aura ?? last;
        }

        int maxDuration = holder.UndiminishedMaxDuration > 0 ? holder.UndiminishedMaxDuration : holder.MaxDuration;
        if (last is null || last.IsPositive || maxDuration <= 10_000)
        {
            return;
        }

        SpellInfo spell = holder.Spell;
        if (HasPvpHeartbeat(spell) && holder.Target.IsLikePlayer())
        {
            holder.HeartbeatRandValue = Random.NextSingle() * 100f;
        }

        if (holder.Target is not Player && IsPveHeartbeat(spell) && ResolveAuraCaster(holder) is { } caster)
        {
            holder.PveHeartbeatTimer = 5000;
            holder.PveHeartbeatHitChance = CombatRules is VanillaSpellCombatRules rules
                ? (int)MathF.Round(rules.MagicHitPercent(this, caster, holder.Target, spell) * 100f)
                : 10_000;
        }
    }

    /// <summary>
    /// The PvP heartbeat curve (SpellAuras.cpp:7257-7264): the percent chance a crowd control has broken after <paramref name="elapsedSeconds"/>,
    /// 50 at 12 s times <paramref name="diminishingRate"/> and 99 at 15 s times it.
    /// </summary>
    internal static float PvpHeartbeatBreakChance(float diminishingRate, float elapsedSeconds)
    {
        float average = 12f * diminishingRate;
        float max = 15f * diminishingRate;
        float coeff = 1f / (max - average) * MathF.Log(99f);
        return 100f / (1f + MathF.Exp(coeff * (average - elapsedSeconds)));
    }

    /// <summary>
    /// The heartbeat step of vmangos SpellAuraHolder::Update (SpellAuras.cpp:7252-7295), before the duration step. PvP: the chance to have
    /// broken by now follows a logistic curve, 50% at 12 s and 99% at 15 s, both scaled by the diminishing rate (patch 1.2: "it is unlikely the
    /// effect will last more than 15 seconds"); the holder goes when its drawn value is at or under it. PvE: every 5 s the holder goes on a
    /// roll of urand(0, 10000) under 10000 minus the hit chance. True when the holder was removed.
    /// </summary>
    private bool UpdateHeartbeat(UnitSpellState state, SpellAuraHolder holder, uint diffMs)
    {
        if (holder.HeartbeatRandValue > 0f)
        {
            if (holder.HeartbeatRandValue <= PvpHeartbeatBreakChance(holder.DiminishingRate, (holder.MaxDuration - holder.Duration) / 1000f))
            {
                RemoveHolder(state, holder);
                return true;
            }
        }

        if (holder.PveHeartbeatTimer >= 0)
        {
            if (holder.PveHeartbeatTimer <= diffMs)
            {
                holder.PveHeartbeatTimer += 5000 - (int)diffMs;
                int missChance = 10_000 - holder.PveHeartbeatHitChance;
                if (Random.Next(0, 10_001) < missChance)
                {
                    holder.PveHeartbeatTimer = -1;
                    RemoveHolder(state, holder);
                    return true;
                }
            }
            else
            {
                holder.PveHeartbeatTimer -= (int)diffMs;
            }
        }

        return false;
    }
}