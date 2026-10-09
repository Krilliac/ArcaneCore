using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.Raids;
using ArcaneCore.Game.GameObjects;

namespace ArcaneCore.Game.Instances.Scripts.ZulGurub;

/// <summary>Bat and troll phases from vmangos zulgurub/boss_jeklik.cpp Reset/UpdateAI and
/// mangos-classic zulgurub/boss_jeklik.cpp ExecuteAction.</summary>
public sealed class JeklikAI : RaidBossAI
{
    private bool _troll;
    private bool _riders;
    private readonly List<Creature> _summonedRiders = [];
    private Creature? _delayedRider;
    private uint _delayedRiderMs;
    public JeklikAI(Creature creature) : base(creature, 0)
    {
        AddAction(10000, () => !_troll && Cast(24408, RandomTarget()), () => RandomDelay(15000, 30000));
        AddAction(12000, () => !_troll && Cast(23918, Victim), () => RandomDelay(20000, 24000));
        AddAction(8000, () => !_troll && Cast(23919, Victim), () => RandomDelay(12000, 15000));
        AddAction(9000, () => !_troll && Cast(12097, Victim), () => RandomDelay(16000, 18000));
        AddAction(40000, () => !_troll && Cast(23974), () => 65000);
        AddAction(9000, () => _troll && Cast(23952, RandomTarget()), () => RandomDelay(8000, 12000));
        AddAction(2000, () => _troll && Cast(23953, RandomTarget()), () => RandomDelay(25000, 30000));
        AddAction(20000, () => _troll && Cast(23954, Me), () => RandomDelay(20000, 25000));
        AddAction(26000, () => _troll && Cast(16098, RandomTarget()), () => RandomDelay(25000, 30000));
    }
    public override void OnAggro(Unit target)
    {
        base.OnAggro(target);
        System?.SayText(Me, 10027);
        Cast(23966, Me);
    }
    protected override void ResetActions()
    {
        base.ResetActions();
        DespawnRiders();
        _troll = _riders = false;
        _delayedRiderMs = 0;
    }
    private void DespawnRiders()
    {
        foreach (Creature rider in _summonedRiders) System?.ForcedDespawn(rider, 0);
        _summonedRiders.Clear();
        _delayedRider = null;
    }
    private bool SummonRiders()
    {
        if (System is not { } system || system.Content.FindTemplate(14750) is not { } template) return false;
        int index = 0;
        foreach ((float x, float y, float z) in new[]
            { (-12298.03f, -1368.506f, 145.3976f), (-12301.69f, -1371.292f, 145.0924f) })
        {
            Creature rider = system.SpawnTemporary(template, x, y, z, 0, Me);
            _summonedRiders.Add(rider);
            if (index++ == 0) { _delayedRider = rider; _delayedRiderMs = 15000; }
            else system.CastSpell(rider, 23968, RandomTarget(), triggered: true);
        }
        return true;
    }
    public override void OnUpdate(uint diffMs)
    {
        if (_delayedRiderMs != 0)
        {
            _delayedRiderMs = _delayedRiderMs > diffMs ? _delayedRiderMs - diffMs : 0;
            if (_delayedRiderMs == 0 && _delayedRider is { IsAlive: true } rider)
                System?.CastSpell(rider, 23968, RandomTarget(), triggered: true);
        }
        base.OnUpdate(diffMs);
    }
    protected override void UpdateCombat(uint diffMs)
    {
        if (!_troll && Below(49) && Cast(24085, Me))
        {
            _troll = true;
            System?.RemoveAuras(Me, 23966);
            ResetThreat();
        }
        if (_troll && !_riders && Below(34))
        {
            // SD2 boss_jeklikAI::ExecuteAction(JEKLIK_PHASE_BATS).
            if (SummonRiders()) _riders = true;
        }
        base.UpdateCombat(diffMs);
    }
    public override void OnDeath(Unit? killer) { DespawnRiders(); base.OnDeath(killer); System?.SayText(Me, 10452); }
}

/// <summary>vmangos zulgurub/boss_venoxis.cpp Reset/UpdateAI; phase thresholds and poison
/// from mangos-classic zulgurub/boss_venoxis.cpp ExecuteAction.</summary>
public sealed class VenoxisAI : RaidBossAI
{
    private bool _snake, _serpent, _frenzy;
    public VenoxisAI(Creature creature) : base(creature, 1)
    {
        AddAction(7500, () => !_snake && Cast(23858, Me), () => RandomDelay(14000, 16000));
        AddAction(10000, () => !_snake && Cast(23860, Victim), () => RandomDelay(8000, 12000));
        AddAction(30000, () => !_snake && Cast(23979, Victim), () => RandomDelay(15000, 25000));
        AddAction(30500, () => !_snake && Cast(23895, Me), () => RandomDelay(20000, 22000));
        AddAction(35000, () => !_snake && Cast(23859, Me), () => RandomDelay(16000, 18000));
        AddAction(2000, () => _snake && Cast(23861, Victim), () => RandomDelay(7000, 10000));
        AddAction(5000, () => _snake && Cast(3391, Me), () => RandomDelay(10000, 20000));
        AddAction(5500, () => _snake && Cast(23862, RandomTarget()), () => RandomDelay(15000, 20000));
        AddAction(10000, () => _snake && !_serpent && Cast(23865, RandomTarget()), () => 10000);
        AddAction(0, () => _serpent && Cast(23867, RandomTarget()), () => RandomDelay(15000, 20000));
    }
    protected override void ResetActions() { base.ResetActions(); _snake = _serpent = _frenzy = false; }
    protected override void UpdateCombat(uint diffMs)
    {
        if (!_snake && Below(50) && Cast(23849, Me))
        {
            _snake = true;
            System?.SayText(Me, 10026);
            Cast(23861, Me, triggered: true);
            Cast(22413, Me, triggered: true);
            ResetThreat();
        }
        if (!_frenzy && Below(19) && Cast(23537, Me)) _frenzy = true;
        if (_snake && !_serpent && Below(24)) _serpent = true;
        base.UpdateCombat(diffMs);
    }
    public override void OnDeath(Unit? killer) { base.OnDeath(killer); System?.SayText(Me, 10460); }
}

/// <summary>vmangos zulgurub/boss_marli.cpp Reset/UpdateAI and mangos-classic
/// zulgurub/boss_marli.cpp Aggro/ExecuteAction.</summary>
public sealed class MarliAI : RaidBossAI
{
    private bool _spider;
    private uint _formTimer = 35000;
    public MarliAI(Creature creature) : base(creature, 2)
    {
        AddAction(15000, () => !_spider && Cast(24099, Victim), () => RandomDelay(10000, 20000));
        AddAction(30000, () => !_spider && Cast(24300, Victim), () => RandomDelay(20000, 50000));
        AddAction(20000, () => !_spider && SummonSpiders((uint)System!.RandomInt(1, 4)), () => RandomDelay(20000, 30000));
        AddAction(0, () => !_spider && Cast(24109, Me), () => RandomDelay(10000, 20000));
        AddAction(5000, () => _spider && Cast(24110, Victim), () => RandomDelay(10000, 15000));
        AddAction(1000, () => _spider && Cast(24111, Victim), () => RandomDelay(25000, 35000));
        AddAction(5000, () => _spider && Cast(3391, Me), () => RandomDelay(10000, 20000));
    }
    public override void OnAggro(Unit target)
    {
        base.OnAggro(target);
        System?.SayText(Me, -1309005);
        Cast(24083, Me);
        SummonSpiders(4);
    }
    private bool SummonSpiders(uint count)
    {
        if (System is not { } system || Me.Map?.FindUpdater<GameObjectMapSystem>() is not { } objects ||
            system.Content.FindTemplate(15041) is not { } spider) return false;
        int spawned = 0;
        foreach (GameObject egg in objects.GameObjects.Where(g => g.Entry == 179985 && g.State == GameObjectState.Ready).Take((int)count))
        {
            egg.State = GameObjectState.Active;
            Creature add = system.SpawnTemporary(spider, egg.X, egg.Y, egg.Z, 0, Me);
            if (RandomTarget() is { } target) add.AI?.AttackStart(target);
            spawned++;
        }
        return spawned > 0;
    }
    public override void OnReachedHome()
    {
        base.OnReachedHome();
        if (Me.Map?.FindUpdater<GameObjectMapSystem>() is { } objects)
            foreach (GameObject egg in objects.GameObjects.Where(g => g.Entry == 179985)) egg.State = GameObjectState.Ready;
    }
    protected override void ResetActions() { base.ResetActions(); _spider = false; _formTimer = 35000; }
    protected override void UpdateCombat(uint diffMs)
    {
        _formTimer = _formTimer > diffMs ? _formTimer - diffMs : 0;
        if (_formTimer == 0 && Cast(_spider ? 24085u : 24084u, Me))
        {
            _spider = !_spider;
            ResetThreat();
            _formTimer = 35000;
            System?.SayText(Me, _spider ? 10443 : -1309025);
        }
        base.UpdateCombat(diffMs);
    }
    public override void OnDeath(Unit? killer) { base.OnDeath(killer); System?.SayText(Me, 10459); }
}
