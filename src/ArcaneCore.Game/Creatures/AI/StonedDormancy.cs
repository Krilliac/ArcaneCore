using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// The dormant "Stoned" state of a creature (spell 10255, the Uldaman Archaedas minions and similar statues): while the creature carries the
/// aura it is inert, and it wakes the moment the aura is gone. The aura is the single source of truth, so any script, spell or EventAI row that
/// adds or removes 10255 switches the state; <see cref="Sync"/> reads it.
/// <para>
/// Dormant means: not selectable and immune to players and NPCs (so nothing attacks it, <c>CombatHooks.CanAttack</c> refuses it, and
/// <see cref="CreatureMapSystem.CanInitiateAttack"/> refuses to let it aggro), and immobilized (<see cref="CreatureMapSystem.SetAiImmobilized"/>: no
/// movement, no swing). Only the flag bits this helper itself added are removed on waking, so flags the template carries are never lost.
/// </para>
/// Ownership: one instance per creature AI, world thread only. <see cref="Sync"/> costs one aura lookup.
/// </summary>
public sealed class StonedDormancy(Creature creature)
{
    /// <summary>Stoned (ClassicDB spell 10255): the dormant marker aura.</summary>
    public const uint StonedAura = 10255;

    /// <summary>The flags a dormant creature carries.</summary>
    public const UnitFlags DormantFlags = UnitFlags.NotSelectable | UnitFlags.ImmuneToPlayer | UnitFlags.ImmuneToNpc;

    private UnitFlags _addedFlags;
    private bool _addedImmobilize;

    /// <summary>Whether the creature is currently held dormant by this helper.</summary>
    public bool IsDormant { get; private set; }

    /// <summary>
    /// Bring the state in line with the aura: dormant when the living creature has <see cref="StonedAura"/>, awake otherwise. Returns true
    /// when the creature is dormant after the call. <see cref="Woke"/> is raised when this call ended the dormancy.
    /// </summary>
    public bool Sync()
    {
        bool stoned = creature.IsAlive && creature.System is { } system && system.HasAura(creature, StonedAura);
        if (stoned && !IsDormant)
        {
            Enter();
        }
        else if (!stoned && IsDormant)
        {
            Exit();
            Woke?.Invoke();
        }

        return IsDormant;
    }

    /// <summary>Raised by <see cref="Sync"/> when the aura was removed and the creature left dormancy.</summary>
    public event Action? Woke;

    /// <summary>
    /// Forget the state without touching the creature: a respawn re-initializes its flags and immobilization from the template
    /// (<c>CreatureMapSystem</c> respawn), so nothing is left to undo.
    /// </summary>
    public void Forget()
    {
        IsDormant = false;
        _addedFlags = UnitFlags.None;
        _addedImmobilize = false;
    }

    /// <summary>Leave dormancy now without waiting for the aura (the creature died, or a script wakes it by force). The aura itself is untouched.</summary>
    public void Release()
    {
        if (IsDormant)
        {
            Exit();
        }
    }

    private void Enter()
    {
        _addedFlags = DormantFlags & ~creature.UnitFlags;
        creature.UnitFlags |= DormantFlags;
        _addedImmobilize = !creature.AiImmobilized;
        creature.System?.SetAiImmobilized(creature, true, combatOnly: false);
        IsDormant = true;
    }

    private void Exit()
    {
        creature.UnitFlags &= ~_addedFlags;
        if (_addedImmobilize)
        {
            creature.System?.SetAiImmobilized(creature, false, combatOnly: false);
        }

        _addedFlags = UnitFlags.None;
        _addedImmobilize = false;
        IsDormant = false;
    }
}

/// <summary>
/// A ScriptName AI that hosts the creature's EventAI rows and keeps the creature dormant while it carries the Stoned aura
/// (<see cref="StonedDormancy"/>). The EventAI rows (for example "on spawn and on reaching home, cast Stoned on self") fire exactly as under the
/// plain EventAI, because every hook calls <c>base</c>. A dormant creature does not aggro on sight, cannot be attacked, ignores
/// <see cref="AttackStart"/> and attacked-by notifications, and moves and swings not at all; the first tick, attack order or sight event after the
/// aura is gone wakes it (<see cref="OnWoke"/>). Register a derived type with <see cref="CreatureAiFactory.RegisterEventAiScript"/>.
/// </summary>
public class StonedEventAI : CreatureEventAI
{
    public StonedEventAI(Creature creature, CreatureAiContent content)
        : base(creature, content)
    {
        Dormancy = new StonedDormancy(creature);
        Dormancy.Woke += OnWoke;
    }

    /// <summary>The dormant state of this creature.</summary>
    protected StonedDormancy Dormancy { get; }

    /// <summary>Whether the creature is dormant right now (re-reads the aura).</summary>
    public bool IsDormant => Dormancy.Sync();

    /// <summary>The Stoned aura went away and the creature is awake. Nothing by default.</summary>
    protected virtual void OnWoke()
    {
    }

    public override void OnRespawn()
    {
        Dormancy.Forget();
        base.OnRespawn();
        Dormancy.Sync();
    }

    public override void OnReachedHome()
    {
        base.OnReachedHome();
        Dormancy.Sync();
    }

    public override void OnDeath(Unit? killer)
    {
        Dormancy.Release();
        base.OnDeath(killer);
    }

    public override void OnUpdate(uint diffMs)
    {
        Dormancy.Sync();
        base.OnUpdate(diffMs);
    }

    public override void MoveInLineOfSight(Unit who)
    {
        if (Dormancy.Sync())
        {
            return;
        }

        base.MoveInLineOfSight(who);
    }

    public override void OnAttackedBy(Unit attacker)
    {
        if (Dormancy.Sync())
        {
            return;
        }

        base.OnAttackedBy(attacker);
    }

    public override bool AttackStart(Unit target) => !Dormancy.Sync() && base.AttackStart(target);

    public override void OnSpellHit(Unit caster, SpellInfo spell)
    {
        Dormancy.Sync();
        base.OnSpellHit(caster, spell);
    }
}
