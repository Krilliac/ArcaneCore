using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.Raids;

namespace ArcaneCore.Game.Instances.Scripts.TempleOfAhnQiraj;

/// <summary>
/// vmangos scripts/kalimdor/silithus/temple_of_ahnqiraj/boss_skeram.cpp:
/// boss_skeramAI Reset, Aggro, UpdateAI, JustSummoned, UnisonBlink, CastBlink, JustDied.
/// mangos-classic AI/ScriptDevAI/scripts/kalimdor/temple_of_ahnqiraj/boss_skeram.cpp:
/// boss_skeramAI ExecuteAction, HandleInitImages, DoTeleport. GPL references supply behavior only.
/// </summary>
public sealed class SkeramAI : RaidBossAI
{
    private readonly TempleOfAhnQirajInstance _temple;
    private readonly HashSet<ObjectGuid> _images = [];
    private bool _isImage;
    private uint _nextSplit = 75;
    private uint _imageMaxPercent = 10;
    private uint _earthShock = 2000;
    private uint _blink;
    private uint _fulfillment;
    private uint _arcane;
    private ObjectGuid _controlled;

    public SkeramAI(Creature creature, TempleOfAhnQirajInstance temple) : base(creature, TempleOfAhnQirajInstance.Skeram)
    {
        _temple = temple;
        _isImage = creature.System?.SummonerOf(creature)?.Entry == 15263;
        ResetTimers();
    }

    public bool IsImage => _isImage;
    public uint NextSplitPercent => _nextSplit;

    private void ResetTimers()
    {
        _nextSplit = 75;
        _imageMaxPercent = 10;
        _earthShock = 2000;
        _blink = RandomDelay(15000, 20000);
        _fulfillment = RandomDelay(10000, 15000);
        _arcane = RandomDelay(6000, 8000);
        _controlled = default;
    }

    private void ClearFulfillment()
    {
        if (_controlled.IsEmpty || Me.Map?.FindObject(_controlled) is not Player player) return;
        foreach (uint spell in new uint[] { 785, 2313, 26525, 26526 }) System?.RemoveAuras(player, spell);
        _controlled = default;
    }

    private void DespawnImages()
    {
        foreach (ObjectGuid guid in _images.ToArray())
            if (System?.FindCreature(guid) is { } image) System.Despawn(image);
        _images.Clear();
    }

    public override void OnAggro(Unit target)
    {
        if (_isImage) return;
        base.OnAggro(target);
        System?.SayText(Me, 11445);
    }

    public override void OnJustSummoned(Creature summoned)
    {
        if (_isImage || summoned.Entry != Me.Entry || summoned.AI is not SkeramAI imageAi) return;
        imageAi._isImage = true;
        _images.Add(summoned.Guid);
        // vmangos JustSummoned: images have phase dependent maximum health and the boss's current percentage.
        summoned.MaxHealth = Math.Max(1, (uint)((ulong)Me.MaxHealth * _imageMaxPercent / 100));
        summoned.Health = Math.Max(1, (uint)((ulong)summoned.MaxHealth * Me.Health / Me.MaxHealth));
        System?.SetInCombatWithZone(summoned);
        if (_images.Count >= 2) BlinkGroup();
    }

    private void ResetThreat(Creature unit)
    {
        foreach (var entry in unit.Combat.Threat.Entries.ToArray())
            unit.Combat.Threat.ModifyThreatPercent(entry.Target, -100);
    }

    private static float DistanceSquared(Unit a, Unit b)
        => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z);

    private static bool Due(ref uint timer, uint diffMs)
    {
        if (timer > diffMs) { timer -= diffMs; return false; }
        timer = 0;
        return true;
    }

    private void Blink(Creature unit, uint spell)
    {
        unit.System?.CastSpell(unit, spell, unit, true);
        ResetThreat(unit);
        if (unit.AI is SkeramAI ai) ai._earthShock = 2000;
    }

    private void BlinkGroup()
    {
        // vmangos UnisonBlink: three distinct platforms for the true prophet and two current images.
        uint[] spells = [4801, 8195, 20449];
        for (int i = spells.Length - 1; i > 0; --i)
        {
            int j = System?.RandomInt(0, i) ?? 0;
            (spells[i], spells[j]) = (spells[j], spells[i]);
        }
        Blink(Me, spells[0]);
        int n = 1;
        foreach (ObjectGuid guid in _images.TakeLast(2))
            if (System?.FindCreature(guid) is { } image) Blink(image, spells[n++]);
    }

    public override void OnDeath(Unit? killer)
    {
        ClearFulfillment();
        if (_isImage) { System?.Despawn(Me); return; }
        DespawnImages();
        System?.SayText(Me, 11447);
        base.OnDeath(killer);
    }

    public override void OnEvade()
    {
        ClearFulfillment();
        if (!_isImage) DespawnImages();
        ResetTimers();
        base.OnEvade();
    }

    public override void OnReachedHome()
    {
        if (_isImage) System?.Despawn(Me);
        else _temple.SetData(TempleOfAhnQirajInstance.Skeram, EncounterState.Fail);
    }

    public override void OnRespawn() { base.OnRespawn(); ResetTimers(); }

    protected override void UpdateCombat(uint diffMs)
    {
        if (_isImage && _temple.GetData(TempleOfAhnQirajInstance.Skeram) == EncounterState.Done)
        {
            System?.Despawn(Me); return;
        }
        if (!_isImage && _nextSplit > 0 && (ulong)Me.Health * 100 < (ulong)Me.MaxHealth * _nextSplit)
        {
            DespawnImages();
            _imageMaxPercent = _nextSplit switch { 75 => 10u, 50 => 20u, _ => 50u };
            if (Cast(747))
            {
                _nextSplit -= 25;
                if (_nextSplit == 0) System?.SayText(Me, -1531006);
            }
        }
        if (Due(ref _arcane, diffMs))
        {
            int melee = Me.Map?.Players.Count(p => p.IsAlive && MapCombat.CanReachWithMeleeAutoAttack(Me, p)) ?? 0;
            int limit = (Me.Map?.Players.Count ?? 0) / 10;
            _arcane = melee > limit && Cast(26192) ? RandomDelay(6000, 14000) : 1000;
        }
        if (Victim is { } victim && !MapCombat.CanReachWithMeleeAutoAttack(Me, victim))
        {
            if (Due(ref _earthShock, diffMs)) _earthShock = Cast(26194, victim) ? 2000u : 0u;
        }
        else _earthShock = 2000;
        if (Due(ref _fulfillment, diffMs))
        {
            Player? closest = Me.Map?.Players.Where(p => p.IsAlive && DistanceSquared(Me, p) <= 1600)
                .OrderBy(p => DistanceSquared(Me, p)).FirstOrDefault();
            if (closest is not null && Cast(785, closest))
            {
                ClearFulfillment();
                Cast(2313, closest, triggered: true);
                Cast(26525, closest, triggered: true);
                Cast(26526, closest, triggered: true);
                _controlled = closest.Guid;
                _fulfillment = RandomDelay(20500, 25000);
            }
        }
        if (Due(ref _blink, diffMs))
        {
            if (!_isImage) BlinkGroup();
            _blink = RandomDelay(10000, 18000);
        }
    }
}
