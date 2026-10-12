using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Instances.Scripts.Uldaman;

/// <summary>
/// ScriptName <c>mob_archaeras_add</c> (Uldaman, map 70): the stone minions of Archaedas, entries 7309 Earthen Custodian, 7077 Earthen
/// Hallshaper, 7076 Earthen Guardian and 10120 Vault Warder. Clean-room port of the behaviour spec <c>70-mob_archaeras_add</c>.
/// <para>
/// The script hosts the entry's EventAI rows (<see cref="StonedEventAI"/>, registered with <see cref="CreatureAiFactory.RegisterEventAiScript"/>):
/// Stoned (10255) on spawn and on reaching home, Whirlwind (17207) for the guardian, Trample (5568) for the warder and Earthen Guards Destroyed
/// (10666, triggered) on death keep firing from the database rows (spec 5). The Stoned aura is the dormant state (spec 3): no aggro, not
/// selectable, immune, immobile (<see cref="StonedDormancy"/>).
/// </para>
/// <para>
/// Waking (spec 6.1). A hit of Awaken Earthen Dwarf (10259) or Awaken Earthen Guardians (10252) starts the vmangos sequence: 1 s later the
/// Stone Dwarf Awaken Visual (10254) on itself, 3 s after that the minion joins combat if the Archaedas encounter is still InProgress, else it
/// stays dormant. A hit of Awaken Vault Warder (10258) on a Vault Warder joins combat at once. The existing <see cref="ArchaedasAi"/> wakes
/// minions directly (<see cref="ArchaedasAi.Awaken"/>: it removes the aura and orders an attack); that is an external awakening, and a minion
/// that is no longer dormant ignores every further awaken hit, so the sequence never runs twice (spec 7). This script adds no second pump:
/// Archaedas owns the cadence.
/// </para>
/// <para>
/// Join combat (spec 6.2): remove Stoned, then attack the nearest hostile within 80 yd, else Archaedas' victim. Evade replacement (spec 6.4): an
/// awake minion that would evade retargets to the nearest hostile in aggro range, else Archaedas' victim, and evades only when neither exists.
/// The Earthen Custodian casts Reconstruct (10260) on a wounded Archaedas every 10 s while awake (spec 6.3).
/// </para>
/// Ownership: one instance per creature, built by the factory; world thread only. Allocation: none per tick.
/// </summary>
public sealed class ArchaerasAddAI : StonedEventAI
{
    /// <summary>The template ScriptName of the four entries.</summary>
    public const string ScriptName = "mob_archaeras_add";

    public const uint SpellAwakenVisual = 10254, SpellAwakenDwarf = 10259, SpellAwakenGuardians = 10252, SpellAwakenWarders = 10258;
    public const uint SpellReconstruct = 10260, SpellEarthenGuardsDestroyed = 10666;

    /// <summary>Spec 6.1: the hit to the visual cast, and the visual cast to the join (4 s in total).</summary>
    public const uint WakeDelayMs = 1000, WakeVisualMs = 3000;

    /// <summary>Spec 6.2: how far the waking minion looks for a hostile.</summary>
    public const float JoinCombatRange = 80f;

    /// <summary>Spec 6.3: the Reconstruct period, and the health percentage below which it is cast.</summary>
    public const uint ReconstructPeriodMs = 10_000;
    public const uint ReconstructBelowPercent = 50;

    private enum WakeStage : byte
    {
        None,
        Delay,
        Visual,
    }

    private WakeStage _stage;
    private uint _wakeMs;
    private uint _reconstructMs = ReconstructPeriodMs;

    public ArchaerasAddAI(Creature creature, CreatureAiContent content)
        : base(creature, content)
    {
    }

    /// <summary>Register the script under <see cref="ScriptName"/> (a duplicate throws).</summary>
    public static void Register(CreatureAiFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        factory.RegisterEventAiScript(ScriptName, static (c, ai) => new ArchaerasAddAI(c, ai));
    }

    /// <summary>The state of the waking sequence, for tests: none, waiting for the visual, or waiting for the join.</summary>
    public bool IsWaking => _stage != WakeStage.None;

    private UldamanInstance? Instance => Me.Map?.FindUpdater<UldamanInstance>();

    private bool EncounterInProgress => Instance?.GetData(UldamanInstance.TypeArchaedas) == EncounterState.InProgress;

    private Unit? ArchaedasVictim => Instance?.ArchaedasCreature is { IsAlive: true } archaedas ? archaedas.Combat.Victim : null;

    public override void OnRespawn()
    {
        _stage = WakeStage.None;
        _wakeMs = 0;
        _reconstructMs = ReconstructPeriodMs;
        base.OnRespawn();
    }

    /// <summary>The aura is gone (an external awakening, or our own join): the waking sequence is over, the minion is awake.</summary>
    protected override void OnWoke()
    {
        _stage = WakeStage.None;
        _wakeMs = 0;
        _reconstructMs = ReconstructPeriodMs;
    }

    public override void OnSpellHit(Unit caster, SpellInfo spell)
    {
        base.OnSpellHit(caster, spell); // the Stoned state is read first; the EventAI spell-hit rows run
        if (!Dormancy.IsDormant)
        {
            return; // already awake: a further awaken hit is ignored, so the sequence never runs twice (spec 7)
        }

        switch (spell.Id)
        {
            case SpellAwakenDwarf or SpellAwakenGuardians:
                if (_stage == WakeStage.None)
                {
                    _stage = WakeStage.Delay;
                    _wakeMs = WakeDelayMs;
                }

                break;
            case SpellAwakenWarders when Me.Template.Entry == UldamanInstance.VaultWarder && !UldamanInstance.IsHallwayWarder(Me):
                // Spec 6.1: the 5 s cast of 10258 is the animation, the warder joins at once. A hallway warder is never woken (spec 11.4).
                JoinCombat();
                break;
        }
    }

    public override void OnUpdate(uint diffMs)
    {
        base.OnUpdate(diffMs);
        if (!Me.IsAlive)
        {
            return;
        }

        if (_stage != WakeStage.None && Dormancy.IsDormant)
        {
            AdvanceWake(diffMs);
        }

        if (Me.Template.Entry == UldamanInstance.Custodian)
        {
            Reconstruct(diffMs);
        }
    }

    /// <summary>
    /// Spec 6.4. An awake minion does not walk home when its target is gone: nearest hostile in aggro range, else Archaedas' victim, else the
    /// engine's evade (return false). A dormant minion takes the engine's evade.
    /// </summary>
    public override bool OnEnterEvadeMode()
    {
        if (Dormancy.Sync() || System is not { } system || !Me.IsAlive)
        {
            return base.OnEnterEvadeMode();
        }

        Unit? next = system.SelectNearestTarget(Me, system.GetAttackDistance(Me, Me)) ?? ArchaedasVictim;
        if (next is not { IsAlive: true })
        {
            return base.OnEnterEvadeMode();
        }

        AttackStart(next);
        return Me.Combat.Victim is not null || base.OnEnterEvadeMode();
    }

    private void AdvanceWake(uint diffMs)
    {
        if (diffMs < _wakeMs)
        {
            _wakeMs -= diffMs;
            return;
        }

        if (_stage == WakeStage.Delay)
        {
            DoCast(Me, SpellAwakenVisual);
            _stage = WakeStage.Visual;
            _wakeMs = WakeVisualMs;
            return;
        }

        _stage = WakeStage.None;
        _wakeMs = 0;
        if (EncounterInProgress)
        {
            JoinCombat();
        }
    }

    /// <summary>Spec 6.2: remove Stoned (and with it the dormant flags), then the nearest hostile within 80 yd, else Archaedas' victim.</summary>
    private void JoinCombat()
    {
        if (System is not { } system)
        {
            return;
        }

        system.RemoveAuras(Me, StonedDormancy.StonedAura);
        Dormancy.Sync();
        if ((system.SelectNearestTarget(Me, JoinCombatRange) ?? ArchaedasVictim) is { } target)
        {
            AttackStart(target);
        }
    }

    /// <summary>
    /// Spec 6.3: every 10 s while awake, Reconstruct on Archaedas when he is alive and under 50 % health. The timer runs whether or not the
    /// cast happens, and rests while the minion is dormant.
    /// </summary>
    private void Reconstruct(uint diffMs)
    {
        if (Dormancy.IsDormant)
        {
            _reconstructMs = ReconstructPeriodMs;
            return;
        }

        if (diffMs < _reconstructMs)
        {
            _reconstructMs -= diffMs;
            return;
        }

        _reconstructMs = ReconstructPeriodMs;
        if (Instance?.ArchaedasCreature is { IsAlive: true } archaedas
            && (ulong)archaedas.Health * 100 < (ulong)archaedas.MaxHealth * ReconstructBelowPercent)
        {
            DoCast(archaedas, SpellReconstruct);
        }
    }
}
