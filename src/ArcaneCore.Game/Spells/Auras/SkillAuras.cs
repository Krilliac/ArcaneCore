using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Skills;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// Skill bonus auras, after vmangos Aura::HandleAuraModSkill (SpellAuras.cpp:2807-2831,
/// commit 0e3ff01e76d4758e8a7c3108b2717cc785ed56fa). MOD_SKILL writes the temporary signed
/// half of the skill bonus word; MOD_SKILL_TALENT writes the permanent half. An unknown skill
/// stays unapplied until learned. SetSkill un-applies before removal and re-applies after adding
/// the skill (Player.cpp:5549-5560, 5627-5645). Bonuses are never persisted as skill values.
/// </summary>
public sealed class SkillAuras : ISpellHandlerModule
{
    private readonly ConditionalWeakTable<Player, Bonuses> _players = new();

    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        var handler = new AuraHandler(Apply, null);
        system.RegisterAura(AuraType.ModSkill, handler);
        system.RegisterAura(AuraType.ModSkillTalent, handler);
    }

    private void Apply(SpellSystem system, SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        if (holder.Target is not Player player || aura.MiscValue is <= 0 or > ushort.MaxValue)
        {
            return;
        }

        if (apply)
        {
            _players.GetValue(player, static target => new Bonuses(target)).Add(holder, aura);
        }
        else if (_players.TryGetValue(player, out Bonuses? bonuses))
        {
            bonuses.Remove(aura);
            if (bonuses.IsEmpty)
            {
                bonuses.Disconnect();
                _players.Remove(player);
            }
        }
    }

    /// <summary>
    /// Contributions survive forgetting a skill, but only applied contributions may be subtracted.
    /// Snapshot the amount because stacking un-applies the old amount before changing it.
    /// The ledger and its subscriptions live only as long as the target has skill auras.
    /// </summary>
    private sealed class Bonuses
    {
        private sealed class Contribution(SpellAuraHolder holder, SpellAura aura)
        {
            public SpellAuraHolder Holder { get; } = holder;

            public uint Skill { get; } = (uint)aura.MiscValue;

            public short Amount { get; } = unchecked((short)aura.Amount);

            public bool Permanent { get; } = aura.Type == AuraType.ModSkillTalent;

            public bool Applied { get; set; }
        }

        private readonly Player _player;
        private readonly Dictionary<SpellAura, Contribution> _contributions = [];
        private PlayerSkills? _skills;

        public Bonuses(Player player)
        {
            _player = player;
            player.SkillsAttached += Attach;
            if (player.Skills is { } skills)
            {
                Attach(skills);
            }
        }

        public bool IsEmpty => _contributions.Count == 0;

        public void Add(SpellAuraHolder holder, SpellAura aura)
        {
            if (_contributions.ContainsKey(aura))
            {
                return;
            }

            var contribution = new Contribution(holder, aura);
            _contributions.Add(aura, contribution);
            Apply(contribution);
        }

        public void Remove(SpellAura aura)
        {
            if (_contributions.Remove(aura, out Contribution? contribution))
            {
                Unapply(contribution);
            }
        }

        public void Disconnect()
        {
            _player.SkillsAttached -= Attach;
            if (_skills is { } skills)
            {
                skills.SkillAdded -= OnAdded;
                skills.SkillRemoving -= OnRemoving;
            }
        }

        private void Attach(PlayerSkills skills)
        {
            _skills = skills;
            skills.SkillAdded += OnAdded;
            skills.SkillRemoving += OnRemoving;
            foreach (Contribution contribution in _contributions.Values.ToArray())
            {
                Apply(contribution);
            }
        }

        private void OnAdded(uint skill)
        {
            foreach (Contribution contribution in _contributions.Values.Where(c => c.Skill == skill).ToArray())
            {
                Apply(contribution);
            }
        }

        private void OnRemoving(uint skill)
        {
            foreach (Contribution contribution in _contributions.Values.Where(c => c.Skill == skill).ToArray())
            {
                Unapply(contribution);
            }
        }

        private void Apply(Contribution contribution)
        {
            if (!contribution.Holder.IsRemoved && !contribution.Applied
                && _skills is { } skills && skills.Has(contribution.Skill))
            {
                // Mark first: ModifyBonus raises SkillChanged, whose subscribers may remove an aura.
                contribution.Applied = true;
                skills.ModifyBonus(contribution.Skill, contribution.Amount, contribution.Permanent);
            }
        }

        private void Unapply(Contribution contribution)
        {
            if (contribution.Applied && _skills is { } skills)
            {
                contribution.Applied = false;
                skills.ModifyBonus(contribution.Skill, unchecked((short)-contribution.Amount), contribution.Permanent);
            }
        }
    }
}
