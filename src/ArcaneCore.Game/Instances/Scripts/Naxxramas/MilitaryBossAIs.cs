using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.Raids;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Instances.Scripts.Naxxramas;

/// <summary>
/// vmangos naxxramas/boss_razuvious.cpp boss_razuviousAI::Aggro/UpdateAI/JustDied/RespawnAdds;
/// mangos-classic naxxramas/boss_razuvious.cpp boss_razuviousAI::SpellHit/SpellHitTarget.
/// The four understudies are imported world spawns; possession uses the core charm system.
/// </summary>
public sealed class RazuviousAI : RaidBossAI
{
    // mangos-classic boss_razuvious.cpp SAY_UNDERSTUDY_TAUNT_1..4, and its AddOnAggroText/AddOnKillText/AddOnDeathText
    // broadcast texts (SAY_AGGRO1..4, SAY_SLAY, SAY_DEATH).
    private static readonly int[] UnderstudyTauntTexts = [13077, 13072, 13073, 13074];
    private static readonly int[] AggroTexts = [13075, 13076, 13078, 13080];
    private const int SlayText = 13081, DeathText = 13079;
    private uint _killTextCooldown;

    public RazuviousAI(Creature creature) : base(creature, NaxxramasInstance.Razuvious)
    {
        AddAction(30000, () => Cast(26613, Victim), () => 30000);
        AddAction(15000, () => Cast(29107, Victim), () => 25000);
    }

    public override void OnAggro(Unit target)
    {
        base.OnAggro(target);
        System?.SayText(Me, AggroTexts[(int)RandomDelay(0, AggroTexts.Length - 1)]);
        EnsureUnderstudies();
        System?.SetInCombatWithZone(Me);
    }

    /// <summary>mangos-classic CombatAI::KilledUnit: a player kill says SAY_SLAY, then ten seconds of silence.</summary>
    public override void OnKilledUnit(Unit victim)
    {
        if (victim is not Player || _killTextCooldown > 0) return;
        _killTextCooldown = 10000;
        System?.SayText(Me, SlayText, victim);
    }

    protected override void ResetActions()
    {
        base.ResetActions();
        _killTextCooldown = 0;
    }

    private void EnsureUnderstudies()
    {
        // vmangos boss_razuvious.cpp addPositions/RespawnAdds; use the imported
        // template, and keep existing ClassicDB spawns instead of duplicating them.
        if (System?.Content.FindTemplate(16803) is not { } template) return;
        (float X, float Y, float Z, float O)[] places =
        [
            (2757.48f, -3111.52f, 267.768f, 3.92699f),
            (2762.05f, -3084.47f, 267.768f, 2.1293f),
            (2778.91f, -3114.14f, 267.768f, 5.28835f),
            (2781.87f, -3088.19f, 267.768f, 0.907571f),
        ];
        foreach (var place in places)
        {
            if (System.Creatures.Any(c => c.Entry == 16803 && c.IsAlive
                && Math.Abs(c.X - place.X) < 2 && Math.Abs(c.Y - place.Y) < 2)) continue;
            System.SpawnTemporary(template, place.X, place.Y, place.Z, place.O, Me);
        }
    }

    public override void OnSpellHit(Unit caster, SpellInfo spell)
    {
        if (spell.Id == 29060 && caster is Creature { Entry: 16803 })
            System?.SayText(Me, UnderstudyTauntTexts[(int)RandomDelay(0, 3)]);
    }

    public override void OnDeath(Unit? killer)
    {
        System?.SayText(Me, DeathText);
        Cast(29125, Me, triggered: true);
        base.OnDeath(killer);
    }

    protected override void UpdateCombat(uint diffMs)
    {
        if (Me.Z > 285) { EnterEvadeMode(); return; }
        _killTextCooldown = _killTextCooldown > diffMs ? _killTextCooldown - diffMs : 0;
        base.UpdateCombat(diffMs);
    }
}

/// <summary>
/// mangos-classic naxxramas/boss_gothik.cpp boss_gothikAI::{Aggro,StartTraineeSummons,
/// StartKnightSummons,StartRiderSummons,HandleOpenGates,ExecuteAction}; naxxramas.cpp SetData.
/// Summon periodic spells use the imported 1.12 Spell.dbc effects and world trigger positions.
/// </summary>
public sealed class GothikAI : RaidBossAI
{
    private uint _elapsed;
    private uint _teleport;
    private int _teleports;
    private bool _ground;
    private bool _gatesOpen;

    public GothikAI(Creature creature) : base(creature, NaxxramasInstance.Gothik) { }

    public override void OnAggro(Unit target)
    {
        base.OnAggro(target);
        Cast(29230, Me, triggered: true);
        Me.UnitFlags |= UnitFlags.ImmuneToPlayer | UnitFlags.NotSelectable;
        // mangos-classic boss_gothik.cpp Aggro: on the balcony he neither chases nor swings. CreatureMapSystem.AttackStart has
        // already started the swing and the chase before this hook, so both are turned off on the live state, not just flagged.
        SetCombatMovement(false);
        SetMeleeEnabled(false);
        System?.SetInCombatWithZone(Me);
    }

    protected override void ResetActions()
    {
        base.ResetActions();
        // mangos-classic boss_gothik.cpp Reset: "Only attack and be attackable while on ground".
        SetMeleeEnabled(false);
        SetCombatMovement(false);
        _elapsed = 0;
        _teleport = 0;
        _teleports = 0;
        _ground = false;
        _gatesOpen = false;
        _bolt = 0;
    }

    protected override void UpdateCombat(uint diffMs)
    {
        uint previous = _elapsed;
        _elapsed = (uint)Math.Min(uint.MaxValue, (ulong)_elapsed + diffMs);
        if (previous < 4000 && _elapsed >= 4000) Cast(28007, Me, triggered: true);
        if (previous < 49000 && _elapsed >= 49000) Cast(28009, Me, triggered: true);
        if (previous < 104000 && _elapsed >= 104000) Cast(28011, Me, triggered: true);
        if (!_ground && _elapsed >= 274000)
        {
            _ground = true;
            _teleport = 20000;
            System?.RemoveAuras(Me, 29230);
            System?.RemoveAuras(Me, 28007);
            System?.RemoveAuras(Me, 28009);
            System?.RemoveAuras(Me, 28011);
            Me.UnitFlags &= ~(UnitFlags.ImmuneToPlayer | UnitFlags.NotSelectable);
            // mangos-classic boss_gothik.cpp HandleGroundPhase: DoResetThreat, then melee and combat movement back on.
            foreach (var entry in Me.Combat.Threat.Entries.ToArray()) Me.Combat.Threat.ModifyThreatPercent(entry.Target, -100);
            SetMeleeEnabled(true);
            SetCombatMovement(true);
            Cast(28025, Me, triggered: true);
        }
        if (!_ground) return;
        if (!_gatesOpen && (_teleports >= 4 || (ulong)Me.Health * 100 <= (ulong)Me.MaxHealth * 30))
        {
            // mangos-classic boss_gothik.cpp ExecuteAction(GOTHIK_STOP_TELE) -> HandleOpenGates -> ProcessCentralDoor;
            // vmangos boss_gothik.cpp opens the gate on the fourth teleport or below 30% the same way.
            _gatesOpen = true;
            if (Instance?.GetData(NaxxramasInstance.Gothik) != EncounterState.Special)
                Instance?.SetData(NaxxramasInstance.Gothik, EncounterState.Special);
        }
        if (!_gatesOpen)
        {
            if (_teleport > diffMs) _teleport -= diffMs;
            else if (Cast((_teleports & 1) == 0 ? 28026u : 28025u, Me))
            {
                _teleports++;
                _teleport = 20000;
            }
        }
        CastShadowBolt(diffMs);
    }

    public override void OnReachedHome()
    {
        ClearSummons();
        Me.UnitFlags &= ~(UnitFlags.ImmuneToPlayer | UnitFlags.NotSelectable);
        base.OnReachedHome();
    }

    public override void OnDeath(Unit? killer)
    {
        ClearSummons();
        base.OnDeath(killer);
    }

    private void ClearSummons()
    {
        Cast(28035, Me, triggered: true); // SD2 boss_gothikAI::EnterEvadeMode reset event.
        foreach (uint spell in new uint[] { 28007, 28009, 28011, 29230 })
            System?.RemoveAuras(Me, spell);
    }

    private uint _bolt;
    private void CastShadowBolt(uint diffMs)
    {
        if (_bolt > diffMs) { _bolt -= diffMs; return; }
        if (Cast(29317, Victim)) _bolt = 2000;
    }
}

/// <summary>
/// mangos-classic naxxramas/boss_four_horsemen.cpp boss_{lady_blaumeux,mograine,
/// thane_korthazz,sir_zeliek}AI::ExecuteAction/JustDied; shared completion in
/// naxxramas.cpp instance_naxxramas::SetData(TYPE_FOUR_HORSEMEN).
/// </summary>
public sealed class HorsemanAI : RaidBossAI
{
    private readonly uint _mark;
    private readonly uint _spirit;
    private bool _shield50;
    private bool _shield20;

    public HorsemanAI(Creature creature) : base(creature, NaxxramasInstance.Horsemen)
    {
        (_mark, _spirit) = creature.Entry switch
        {
            16065 => (28833u, 28931u),
            16062 => (28834u, 28928u),
            16064 => (28832u, 28932u),
            _ => (28835u, 28934u),
        };
        // vmangos boss_four_horsemen.cpp boss_four_horsemen_shared::Reset m_uiMarkTimer = 20 s, then 12 s after each cast.
        AddAction(20000, CastMark, () => 12000);
        // vmangos Aggro EVENT_BOSS_ABILITY: Blaumeux 12 s (repeat 12 s), Korth'azz 30 s (repeat 12-15 s),
        // Zeliek 12 s (repeat 10-14 s). Mograine has no timed special (his Righteous Fire is a passive proc).
        switch (creature.Entry)
        {
            case 16065: AddAction(12000, Special, () => 12000); break;
            case 16064: AddAction(30000, Special, () => RandomDelay(12000, 15000)); break;
            case 16063: AddAction(12000, Special, () => RandomDelay(10000, 14000)); break;
        }
        AddAction(1200000, () => Cast(26662, Me), () => 300000);
    }

    public override void OnAggro(Unit target)
    {
        // vmangos boss_four_horsemen.cpp boss_four_horsemen_shared::Aggro: the first horseman pulled brings the
        // other three into the fight; once the encounter is in progress a later aggro pulls nobody.
        bool first = Instance?.GetData(NaxxramasInstance.Horsemen) != EncounterState.InProgress;
        base.OnAggro(target);
        if (Me.Entry == 16062) Cast(28881, Me, triggered: true); // vmangos MograineAI::Aggro, Righteous Fire triggers 28882.
        if (!first || System is not { } system) return;
        foreach (uint entry in NaxxramasInstance.HorsemenEntries)
        {
            if (entry != Me.Entry && system.Creatures.FirstOrDefault(c => c.Entry == entry && c.IsAlive && !c.Combat.IsInCombat) is { } other)
                other.AI?.AttackStart(target);
        }
    }

    /// <summary>
    /// vmangos boss_four_horsemen_shared::UpdateAI: after a successful mark every threat entry that holds threat loses half of it
    /// ("todo: this behavior should get some more confirmation" in vmangos).
    /// </summary>
    private bool CastMark()
    {
        if (!Cast(_mark, Me)) return false;
        foreach (var entry in Me.Combat.Threat.Entries.ToArray())
            if (entry.Threat > 0) Me.Combat.Threat.ModifyThreatPercent(entry.Target, -50);
        return true;
    }

    private Player? RandomPlayer()
    {
        Player[] players = [.. Me.Combat.Threat.Entries.Select(e => e.Target).OfType<Player>()
            .Where(p => p.IsAlive && p.IsInWorld && p.Map == Me.Map)];
        return players.Length == 0 ? null : players[System!.RandomInt(0, players.Length - 1)];
    }

    private bool Special()
    {
        Player? target = RandomPlayer();
        if (target is null) return false;
        if (Me.Entry == 16065)
        {
            // vmangos boss_four_horsemen.cpp BlaumeuxAI::UpdateAI: summon a
            // Void Zone at a player's position, with its own imported template AI.
            if (System?.Content.FindTemplate(16697) is not { } template) return false;
            System.SpawnTemporary(template, target.X, target.Y, Math.Max(241.35f, target.Z), 0, Me);
            return true;
        }
        return Cast(Me.Entry == 16064 ? 28884u : 28883u, target);
    }

    protected override void ResetActions()
    {
        base.ResetActions();
        _shield50 = _shield20 = false;
    }

    protected override void UpdateCombat(uint diffMs)
    {
        if (!_shield50 && (ulong)Me.Health * 100 <= (ulong)Me.MaxHealth * 50 && Cast(29061, Me)) _shield50 = true;
        if (!_shield20 && (ulong)Me.Health * 100 <= (ulong)Me.MaxHealth * 20 && Cast(29061, Me)) _shield20 = true;
        base.UpdateCombat(diffMs);
    }

    public override void OnDeath(Unit? killer)
    {
        Cast(_spirit, Me, triggered: true);
        (Instance as NaxxramasInstance)?.RecordHorsemanDeath(Me.Entry);
    }
}
