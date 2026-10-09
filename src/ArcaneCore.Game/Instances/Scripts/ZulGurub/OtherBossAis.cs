using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.Classic;
using ArcaneCore.Game.Instances.Scripts.Raids;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Instances.Scripts.ZulGurub;

/// <summary>mangos-classic zulgurub/boss_arlokk.cpp boss_arlokkAI::Aggro,
/// ExecuteAction and ArlokkVanish::OnEffectExecute.</summary>
public sealed class ArlokkAI : RaidBossAI
{
    private bool _panther;
    private uint _vanishMs;
    private Creature? _leftTrigger, _rightTrigger;
    public ArlokkAI(Creature creature) : base(creature, 4)
    {
        AddAction(30000, ChangeForm, () => _panther ? 120000u : 30000u);
        // ClassicDB z2815 creature_spell_list 1451501/1451502.
        AddAction(5000, () => !_panther && Cast(24210, RandomTarget()), () => 30000);
        AddAction(8000, () => !_panther && Cast(24212, RandomTarget()), () => 15000);
        AddAction(14000, () => !_panther && Cast(12540, Victim), () => RandomDelay(17000, 27000));
        AddAction(12000, () => _panther && Cast(24213, Victim), () => RandomDelay(10000, 15000));
        AddAction(20000, () => _panther && Cast(3391, Me), () => RandomDelay(13000, 15000));
        AddAction(15000, () => _panther && Cast(24236, Me), () => RandomDelay(17000, 27000));
        AddAction(5000, 10000, () => _panther && Vanish(), () => 85000);
    }
    protected override void ResetActions() { base.ResetActions(); _panther = false; _vanishMs = 0; }
    public override void OnAggro(Unit target)
    {
        base.OnAggro(target);
        System?.SayText(Me, 10461);
        if (System is not { } system) return;
        _leftTrigger = system.Creatures.FirstOrDefault(c => c.Entry == 15091 && c.Y < -1626);
        _rightTrigger = system.Creatures.FirstOrDefault(c => c.Entry == 15091 && c.Y >= -1626);
        if (_leftTrigger is { } left) system.CastSpell(left, 24247, left, triggered: false);
        if (_rightTrigger is { } right) system.CastSpell(right, 24247, right, triggered: false);
    }
    private void StopProwlers()
    {
        if (_leftTrigger is { } left) System?.RemoveAuras(left, 24247);
        if (_rightTrigger is { } right) System?.RemoveAuras(right, 24247);
        _leftTrigger = _rightTrigger = null;
    }
    public override void OnEvade() { StopProwlers(); base.OnEvade(); }
    public override void OnReachedHome() { StopProwlers(); base.OnReachedHome(); }
    private bool ChangeForm()
    {
        if (!Cast(_panther ? 24085u : 24190u, Me)) return false;
        _panther = !_panther;
        if (_panther) ResetThreat();
        else System?.RemoveAuras(Me, 24190);
        return true;
    }
    private bool Vanish()
    {
        if (!Cast(24223, Me)) return false;
        Cast(24228, Me, triggered: true);
        Cast(24235, Me, triggered: true);
        ResetThreat();
        _vanishMs = 50000;
        return true;
    }
    public override void OnUpdate(uint diffMs)
    {
        if (_vanishMs != 0)
        {
            _vanishMs = _vanishMs > diffMs ? _vanishMs - diffMs : 0;
            if (_vanishMs == 0) System?.RemoveAuras(Me, 24235);
        }
        base.OnUpdate(diffMs);
    }
    public override void OnDeath(Unit? killer) { StopProwlers(); base.OnDeath(killer); System?.SayText(Me, 10450); }
}

/// <summary>vmangos zulgurub/boss_jindo.cpp boss_jindoAI::Reset/UpdateAI. The summoning
/// spells create shades, Brain Wash totems and healing wards from imported spell content.</summary>
public sealed class JindoAI : RaidBossAI
{
    public JindoAI(Creature creature) : base(creature, null)
    {
        AddAction(10000, 20000, () => Cast(24262, Me), () => RandomDelay(10000, 30000));
        AddAction(20000, 30000, () => Cast(24309, Me), () => RandomDelay(20000, 30000));
        AddAction(20000, 50000, () => Cast(17172, Victim), () => RandomDelay(20000, 60000));
        AddAction(3000, 6000, () => Cast(24306, RandomTarget()), () => RandomDelay(3000, 9000));
        AddAction(6000, 8000, () => Cast(24308, RandomTarget()), () => RandomDelay(7000, 8000));
        AddAction(15000, 30000, () => Cast(24466, RandomTarget()), () => RandomDelay(15000, 35000));
    }
    public override void OnAggro(Unit target) => System?.SayText(Me, 10449);
}

/// <summary>mangos-classic zulgurub/boss_mandokir.cpp boss_mandokirAI::Aggro, EnterEvadeMode, JustDied, SpellHitTarget, ReceiveAIEvent,
/// KilledUnit, JustSummoned, SummonedCreatureJustDied and MovementInform; the combat cadence of vmangos boss_mandokirAI::Reset/UpdateAI.
/// Threatening Gaze (24314) reaches the AI through <see cref="ThreateningGazeAuraModule"/>, as the reference ThreateningGaze AuraScript does.</summary>
public sealed class MandokirAI : RaidBossAI
{
    /// <summary>cmangos AI_EVENT_CUSTOM_A / AI_EVENT_CUSTOM_B: Threatening Gaze put on / taken off a player.</summary>
    public const uint AiEventGazeApplied = 1000, AiEventGazeRemoved = 1001;
    /// <summary>POINT_DOWNSTAIRS: the instance sends him down the stairs on TYPE_OHGAN SPECIAL (zulgurub.cpp SetData).</summary>
    public const uint PointDownstairs = 1;
    private const uint ChainedSpirit = 15117, Ohgan = 14988;
    private bool _ohganDead;
    private int _killCount;
    private float _gazeThreat;
    private uint _mountDisplay;
    private readonly List<Creature> _spirits = [];
    // mangos-classic boss_mandokir.cpp aSpirits: fixed in-world positions.
    private static readonly (float X, float Y, float Z, float O)[] SpiritPositions =
    [
        (-12150.9f, -1956.24f, 133.407f, 2.57835f), (-12157.1f, -1972.78f, 133.947f, 2.64903f),
        (-12172.3f, -1982.63f, 134.061f, 1.48664f), (-12194.0f, -1979.54f, 132.194f, 1.45916f),
        (-12211.3f, -1978.49f, 133.580f, 1.35705f), (-12228.4f, -1977.10f, 132.728f, 1.25495f),
        (-12250.0f, -1964.78f, 135.066f, 0.92901f), (-12264.0f, -1953.08f, 134.072f, 0.62663f),
        (-12289.0f, -1924.00f, 132.620f, 5.37829f), (-12267.3f, -1902.26f, 131.328f, 5.32724f),
        (-12255.3f, -1893.53f, 134.026f, 5.06413f), (-12229.9f, -1891.39f, 134.704f, 4.40047f),
        (-12215.9f, -1889.09f, 137.273f, 4.70285f), (-12200.5f, -1890.69f, 135.777f, 4.84422f),
        (-12186.0f, -1890.12f, 134.261f, 4.36513f), (-12246.3f, -1890.09f, 135.475f, 4.73427f),
        (-12170.7f, -1894.85f, 133.852f, 3.51690f), (-12279.0f, -1931.92f, 136.130f, 0.04151f),
        (-12266.1f, -1940.72f, 132.606f, 0.70910f)
    ];
    public MandokirAI(Creature creature) : base(creature, 5)
    {
        AddAction(33000, () => Cast(24314, RandomTarget()), () => 20000);
        AddAction(15000, () => Cast(24408, RandomTarget()), () => RandomDelay(30000, 35000));
        AddAction(20000, () => Cast(13736, Me), () => 18000);
        AddAction(1000, () => Cast(19134, Me), () => 24000);
        AddAction(1000, () => Cast(16856, Victim), () => 15000);
    }
    public override void OnAggro(Unit target)
    {
        base.OnAggro(target);
        System?.SayText(Me, 10446);
        if (System is { } system && system.Content.FindTemplate(15117) is { } template)
        {
            foreach (var p in SpiritPositions)
                _spirits.Add(system.SpawnTemporary(template, p.X, p.Y, p.Z, p.O, Me));
        }
        // "At combat start Mandokir is mounted so we must unmount it first" (creature_template_addon 11382 mount 15271).
        uint mount = Me.GetUInt32(UpdateFields.UnitFieldMountdisplayid);
        if (mount != 0)
        {
            _mountDisplay = mount;
            Me.SetUInt32(UpdateFields.UnitFieldMountdisplayid, 0);
        }
        Cast(24349, Me);
    }

    /// <summary>MovementInform(POINT_DOWNSTAIRS): at the foot of the stairs he loses IMMUNE_TO_PLAYER and pulls every player of the map.</summary>
    public override void OnMovementInform(MovementGeneratorType type, uint pointId)
    {
        if (type != MovementGeneratorType.Point || pointId != PointDownstairs || Instance is null) return;
        Me.UnitFlags &= ~UnitFlags.ImmuneToPlayer;
        System?.SetInCombatWithZone(Me);
    }

    private void DespawnSpirits()
    {
        foreach (Creature spirit in _spirits) System?.ForcedDespawn(spirit, 0);
        _spirits.Clear();
    }
    /// <summary>boss_mandokirAI::EnterEvadeMode: TYPE_OHGAN FAIL and the spirits despawned when the evade starts, not when he is home.</summary>
    public override void OnEvade()
    {
        Instance?.SetData(ZulGurubInstance.TypeOhgan, EncounterState.Fail);
        DespawnSpirits();
        base.OnEvade();
    }

    /// <summary>cmangos HomeMovementGenerator::Finalize reloads the creature addon: he is mounted again at home. No state change here
    /// (the base would set FAIL a second time; the reference has no JustReachedHome).</summary>
    public override void OnReachedHome()
    {
        if (_mountDisplay != 0) Me.SetUInt32(UpdateFields.UnitFieldMountdisplayid, _mountDisplay);
    }

    /// <summary>SummonedCreatureJustDied(NPC_OHGAN): enrage and EMOTE_RAGE.</summary>
    public void OnOhganDeath()
    {
        if (_ohganDead) return;
        _ohganDead = true;
        Cast(23537, Me, triggered: true);
        System?.SayText(Me, 10545);
    }

    /// <summary>JustSummoned(NPC_OHGAN): the raptor joins on his victim.</summary>
    public override void OnJustSummoned(Creature summoned)
    {
        if (summoned.Entry == Ohgan && Victim is { } victim) summoned.AI?.AttackStart(victim);
    }

    /// <summary>SpellHitTarget(SPELL_CHARGE): a charge wipes his threat list.</summary>
    public override void OnSpellHitTarget(Unit target, SpellInfo spell)
    {
        if (spell.Id == 24408) ResetThreat();
    }

    /// <summary>ReceiveAIEvent: on Threatening Gaze the watch lines and the watched player's threat are noted; when the gaze ends, a living
    /// player whose threat rose is summoned when out of sight (25104) and charged (24315).</summary>
    public override void OnReceiveAiEvent(uint eventType, Unit sender, Unit? invoker, uint miscValue)
    {
        if (eventType == AiEventGazeApplied && invoker is not null)
        {
            System?.SayText(Me, 10604, invoker);
            System?.SayText(Me, 10628, invoker);
            _gazeThreat = Me.Combat.Threat.GetThreat(invoker);
        }
        else if (eventType == AiEventGazeRemoved && invoker is Player { IsAlive: true } watched &&
                 Me.Combat.Threat.GetThreat(watched) > _gazeThreat)
        {
            if (System is { } system && !system.IsInLineOfSight(Me, watched)) Cast(25104, watched, triggered: true);
            Cast(24315, watched, triggered: true);
        }
    }

    /// <summary>KilledUnit: every third player kill levels him up (with Jin'do's congratulation while Jin'do lives), and in combat the
    /// closest Chained Spirit within 50 yards revives the player.</summary>
    public override void OnKilledUnit(Unit victim)
    {
        if (victim is not Player) return;
        if (++_killCount == 3)
        {
            System?.SayText(Me, 10505);
            if (Raid?.FindJindo() is { IsAlive: true } jindo) System?.SayText(jindo, 10601);
            Cast(24312, Me, triggered: true);
            _killCount = 0;
        }
        if (Me.Combat.IsInCombat) ReviveWithChainedSpirit(victim);
    }

    /// <summary>mob_ohganAI::KilledUnit and boss_mandokirAI::KilledUnit: GetClosestCreatureWithEntry(victim, NPC_CHAINED_SPIRIT, 50) casts 24341.</summary>
    public void ReviveWithChainedSpirit(Unit victim)
    {
        if (System is not { } system) return;
        if (system.CreaturesOfEntryInRange(victim, ChainedSpirit, 50).FirstOrDefault(c => c.IsAlive) is { } spirit)
            system.CastSpell(spirit, 24341, victim, triggered: false);
    }

    private ZulGurubInstance? Raid => Instance as ZulGurubInstance;

    protected override void ResetActions()
    {
        base.ResetActions();
        _ohganDead = false;
        _killCount = 0;
        _gazeThreat = 0;
    }
    public override void OnDeath(Unit? killer)
    {
        DespawnSpirits();
        base.OnDeath(killer);
        Cast(24342, Me, triggered: true);
    }
}

/// <summary>mangos-classic boss_mandokir.cpp ThreateningGaze AuraScript: putting on and taking off 24314 sends its caster's AI
/// AI_EVENT_CUSTOM_A / AI_EVENT_CUSTOM_B with the gazed player as the invoker.</summary>
public sealed class ThreateningGazeAuraModule : ISpellHandlerModule
{
    public const uint ThreateningGaze = 24314;

    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.HolderAdded += holder => Notify(holder, MandokirAI.AiEventGazeApplied);
        system.HolderRemoved += holder => Notify(holder, MandokirAI.AiEventGazeRemoved);
    }

    private static void Notify(SpellAuraHolder holder, uint eventType)
    {
        if (holder.Spell.Id != ThreateningGaze || holder.Target.Map?.FindObject(holder.CasterGuid) is not Creature caster) return;
        caster.ReceiveAiEvent(eventType, holder.Target, holder.Target);
    }
}

/// <summary>vmangos zulgurub/boss_gahzranka.cpp boss_gahzrankaAI::Reset/UpdateAI.</summary>
public sealed class GahzrankaAI : RaidBossAI
{
    public GahzrankaAI(Creature creature) : base(creature, null)
    {
        AddAction(8000, () => Cast(16099, Victim), () => RandomDelay(8000, 20000));
        AddAction(25000, () =>
        {
            if (!Cast(22421, RandomTarget())) return false;
            ResetThreat();
            return true;
        }, () => RandomDelay(16000, 24000));
        AddAction(17000, () => Cast(24326, Victim), () => RandomDelay(12000, 20000));
    }
}
