using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// The Spitelash naga of Azshara (entries 6190 warrior, 6193 screamer, 6194 serpent guard, 6195 siren, 6196 myrmidon): mangos-classic ScriptDev2
/// <c>mobs_spitelashes</c> (kalimdor/azshara.cpp:37-192, registered :503-506). In combat each one uses its table of abilities (:61-71): a timer
/// fires when it is below the elapsed time and is otherwise counted down, and the first ability that fires and is cast ends the pass
/// (:154-166). The warrior disarms its victim (4 s in, every 10 s), the screamer screeches (7 s, 15 s), the serpent guard frost-shocks its victim
/// (7 s, 13 s), the myrmidon strikes its victim (3 s, 7 s); the siren renews the friend missing the most health within 10 yd (4 s, 7 s), shoots (3 s,
/// 9 s) and frost-shoots (7 s, 10 s) a random fighter, and casts Frost Nova on herself (10 s, 15 s). A player on "Fragmented Magic" (9364) who
/// polymorphs one (any rank of Polymorph) turns it into a sheep for 5 s of combat, then Polymorph Backfire (28406, triggered) goes off on
/// it and it is despawned (SpellHit :101-111, UpdateAI :134-152). Reset (evade, respawn) restores the timers and cancels the polymorph.
/// <para>
/// Differences: the source keeps the timers in an <c>unordered_map</c> whose iteration order is unspecified; they are walked in table order here.
/// Its random target is chosen among the threat-list units that are in line of sight and inside the spell's range
/// (<c>SELECT_FLAG_IN_LOS</c> plus the spell range test of <c>MeetsSelectAttackingRequirement</c>, Entities/Unit.cpp:12086-12200); here it is
/// chosen among those in line of sight and the cast itself refuses a target out of range (a refused cast leaves the timer expired, as in the
/// source, and the next pass picks again).
/// </para>
/// </summary>
public sealed class SpitelashAI(Creature creature) : ScriptedAI(creature)
{
    public const uint NpcWarrior = 6190, NpcScreamer = 6193, NpcSerpentGuard = 6194, NpcSiren = 6195, NpcMyrmidon = 6196;
    public const uint QuestFragmentedMagic = 9364, SpellPolymorphBackfire = 28406, MorphDelayMs = 5000;
    public const uint SpellDisarm = 6713, SpellScreech = 3589, SpellFrostShock = 12548, SpellRenew = 11640, SpellShoot = 6660,
        SpellFrostShot = 12551, SpellFrostNova = 11831, SpellStrike = 11976;
    public const float FriendlyRange = 10f;

    /// <summary>The Polymorph spells that sheep a Spitelash for the quest: the 5875 ranks 118, 12824, 12825 and 12826 (:104-105).</summary>
    public static readonly IReadOnlySet<uint> PolymorphSpells = new HashSet<uint> { 118, 12824, 12825, 12826 };

    /// <summary>What an ability is aimed at (TARGET_TYPE_*, :54-59).</summary>
    public enum AbilityTarget
    {
        Random,
        Victim,
        Self,
        Friendly,
    }

    /// <summary>One row of m_aSpitelashAbility (:61-71).</summary>
    public readonly record struct Ability(uint Entry, uint Spell, AbilityTarget Target, uint InitialMs, uint CooldownMs);

    public static readonly IReadOnlyList<Ability> Abilities =
    [
        new(NpcWarrior, SpellDisarm, AbilityTarget.Victim, 4000, 10000),
        new(NpcScreamer, SpellScreech, AbilityTarget.Self, 7000, 15000),
        new(NpcSerpentGuard, SpellFrostShock, AbilityTarget.Victim, 7000, 13000),
        new(NpcSiren, SpellRenew, AbilityTarget.Friendly, 4000, 7000),
        new(NpcSiren, SpellShoot, AbilityTarget.Random, 3000, 9000),
        new(NpcSiren, SpellFrostShot, AbilityTarget.Random, 7000, 10000),
        new(NpcSiren, SpellFrostNova, AbilityTarget.Self, 10000, 15000),
        new(NpcMyrmidon, SpellStrike, AbilityTarget.Victim, 3000, 7000),
    ];

    // The rows of this creature's entry, in table order, with the timer of each (the constructor's m_mSpellTimers).
    private readonly Ability[] _mine = [.. Abilities.Where(a => a.Entry == creature.Template.Entry)];
    private uint[] _timers = [.. Abilities.Where(a => a.Entry == creature.Template.Entry).Select(a => a.InitialMs)];
    private uint _morphMs;

    /// <summary>m_uiMorphTimer: the time left before the backfire, 0 when the creature is not polymorphed.</summary>
    public uint MorphMs => _morphMs;

    /// <summary>The creature's abilities in the order they are tried, with their remaining timers.</summary>
    public IReadOnlyList<(Ability Ability, uint TimerMs)> Timers => [.. _mine.Select((a, i) => (a, _timers[i]))];

    protected override void Reset()
    {
        _morphMs = 0;
        _timers = [.. _mine.Select(a => a.InitialMs)];
    }

    /// <summary>SpellHit (:101-111): a questing player's Polymorph starts the five-second countdown, once.</summary>
    public override void OnSpellHit(Unit caster, SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(spell);
        if (_morphMs != 0)
        {
            return;
        }

        if (caster is Player player && System is { } system && system.QuestIncomplete(player, QuestFragmentedMagic) && PolymorphSpells.Contains(spell.Id))
        {
            _morphMs = MorphDelayMs;
        }
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!UpdateVictim() || Victim is null)
        {
            return;
        }

        if (_morphMs != 0)
        {
            if (_morphMs <= diffMs)
            {
                if (DoCast(Me, SpellPolymorphBackfire, triggered: true) == CreatureCastResult.Ok)
                {
                    _morphMs = 0;
                    System?.ForcedDespawn(Me, 0);
                    return;
                }
            }
            else
            {
                _morphMs -= diffMs;
            }
        }

        for (int i = 0; i < _mine.Length; i++)
        {
            if (_timers[i] < diffMs)
            {
                if (CanUseSpecialAbility(_mine[i]))
                {
                    _timers[i] = _mine[i].CooldownMs;
                    break;
                }
            }
            else
            {
                _timers[i] -= diffMs;
            }
        }
    }

    /// <summary>CanUseSpecialAbility (:113-132): pick the target by the ability's type and cast; true when the cast went off.</summary>
    private bool CanUseSpecialAbility(Ability ability)
    {
        Unit? target = ability.Target switch
        {
            AbilityTarget.Self => Me,
            AbilityTarget.Victim => Victim,
            AbilityTarget.Random => RandomTargetInLineOfSight(),
            AbilityTarget.Friendly => SelectLowestHpFriendly(FriendlyRange),
            _ => null,
        };
        return target is not null && DoCast(target, ability.Spell) == CreatureCastResult.Ok;
    }

    private Unit? RandomTargetInLineOfSight()
    {
        if (!Me.Combat.HasThreatList || System is not { } system)
        {
            return null;
        }

        Unit[] candidates = [.. Me.Combat.Threat.Entries.Select(e => e.Target).Where(u => u.IsAlive && system.IsInLineOfSight(Me, u))];
        return candidates.Length == 0 ? null : candidates[system.RandomInt(0, candidates.Length - 1)];
    }
}
