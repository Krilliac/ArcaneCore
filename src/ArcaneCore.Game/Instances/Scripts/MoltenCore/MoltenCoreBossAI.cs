using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Instances.Scripts.MoltenCore;

/// <summary>The first eight bosses and their scripted adds. Spell IDs, target filters, initial/repeat timers and
/// thresholds follow mangos-classic scripts/eastern_kingdoms/molten_core/boss_*.cpp, constructors, Reset,
/// ExecuteAction, SpellHit and JustDied. Lucifron uses vmangos boss_lucifronAI::Reset/UpdateAI timers.</summary>
public sealed class MoltenCoreBossAI(Creature creature, MoltenCoreInstance instance, uint? encounter)
    : RaidCreatureAI(creature, instance, encounter)
{
    private bool _threshold;
    private uint _quake;
    private readonly List<Creature> _guards = [];
    public override void OnAggro(Unit target)
    {
        base.OnAggro(target);
        // ClassicDB z2815 creature_linking_template: 11661/11662/11672 flags 1031, 12099 flags 1543, map 409.
        uint entry = Me.Template.Entry switch { 12259 => 11661, 12057 => 12099, 11988 => 11672, 12098 => 11662, _ => 0 };
        if (entry == 0 || System is not { } system) return;
        if (Me.Entry == 12057) Cast(23487, triggered: true);
        _guards.Clear();
        _guards.AddRange(system.Creatures.Where(c => c.Entry == entry));
        foreach (Creature guard in _guards.Where(c => c.IsAlive))
        {
            guard.AI?.AttackStart(target);
        }
    }
    public override void OnEvade()
    {
        base.OnEvade();
        // The reference's creature linking resets the encounter's guards with the boss.
        foreach (Creature guard in _guards)
        {
            if (!guard.IsAlive) System?.ForceRespawn(guard);
            else if (guard.Combat.IsInCombat) guard.AI?.EnterEvadeMode();
        }
    }
    protected override void Reset()
    {
        base.Reset();
        _threshold = false;
        _quake = 0;
        switch (Me.Template.Entry)
        {
            case 12118: // vmangos boss_lucifron.cpp Reset/UpdateAI
                Spell(19702, 10000, 10000, 20000, 20000);
                Spell(19703, 20000, 20000, 15000, 15000);
                Spell(19460, 6000, 6000, 6000, 6000, () => RandomTarget());
                break;
            case 11982: // mangos-classic boss_magmadarAI
                Cast(19449, triggered: true);
                Schedule(30000, 30000, 15000, 20000, () => { if (!Cast(19451)) return false; Say(7797); return true; });
                Spell(19408, 6000, 10000, 30000, 35000);
                Spell(19411, 12000, 12000, 12000, 15000, () => RandomTarget(u => u.PowerType != PowerType.Mana));
                Spell(20474, 18000, 18000, 12000, 15000, () => RandomTarget(u => u.PowerType == PowerType.Mana));
                break;
            case 12259: // boss_gehennasAI
                Spell(19716, 5000, 10000, 25000, 30000);
                Spell(19717, 6000, 12000, 6000, 12000, PlayerTarget);
                Spell(19729, 3000, 6000, 3000, 6000, PlayerTarget);
                Spell(19728, 3000, 6000, 3000, 6000, () => Victim);
                break;
            case 12057: // boss_garrAI
                Cast(23487, triggered: true);
                Spell(19492, 10000, 15000, 15000, 20000);
                Spell(19496, 5000, 10000, 10000, 15000);
                Schedule(360000, 360000, 20000, 20000, () => { if (!Cast(20482)) return false; Say(8254); return true; });
                break;
            case 12099: // mob_fireswornAI
                Cast(8876, triggered: true);
                Cast(15733, triggered: true);
                break;
            case 12056: // boss_baron_geddonAI
                Spell(19695, 45000, 45000, 45000, 45000);
                Spell(19659, 30000, 30000, 30000, 30000);
                Spell(20475, 35000, 35000, 35000, 35000, PlayerTarget);
                break;
            case 12264: // boss_shazzrahAI
                Spell(19712, 6000, 6000, 5000, 9000);
                Spell(19713, 10000, 10000, 20000, 20000);
                Spell(19715, 15000, 15000, 16000, 20000);
                Spell(19714, 24000, 24000, 35000, 35000);
                Spell(23138, 30000, 30000, 45000, 45000);
                break;
            case 12098: // boss_sulfuronAI
                Spell(19778, 15000, 15000, 15000, 20000);
                Spell(19779, 3000, 3000, 10000, 10000, () => Friendly(45, c => c.Combat.IsInCombat && !(System?.HasAura(c, 19779) ?? false), random: true) ?? Me);
                Spell(19780, 6000, 6000, 12000, 15000);
                Spell(19781, 2000, 2000, 12000, 16000, PlayerTarget);
                break;
            case 11662: // mob_flamewaker_priestAI
                Spell(19777, 10000, 10000, 15000, 18000, () => Victim);
                Spell(19775, 15000, 30000, 15000, 20000, () => Friendly(60, c => c.Health < c.MaxHealth));
                Spell(19776, 2000, 2000, 18000, 26000, PlayerTarget);
                Spell(20294, 8000, 8000, 15000, 25000, PlayerTarget);
                break;
            case 11988: // boss_golemaggAI
                Cast(13879, triggered: true); Cast(18943, triggered: true); Cast(20556, triggered: true);
                Spell(20228, 7000, 7000, 7000, 7000, PlayerTarget);
                break;
            case 11672: // mob_core_ragerAI
                Cast(12787, triggered: true);
                Spell(19820, 7000, 7000, 10000, 10000, () => Victim);
                break;
        }
    }
    private Unit? PlayerTarget() => RandomTarget(u => u is Player);
    public override void OnUpdate(uint diffMs)
    {
        if (!Me.Combat.IsInCombat || !UpdateVictim()) return;
        if (Me.Template.Entry == 12056 && !_threshold && Below(2) && Cast(20478))
        {
            _threshold = true;
            Say(8253);
            SetMeleeEnabled(false);
            CombatMovement = false;
            System?.MoveIdle(Me);
        }
        if (Me.Template.Entry == 11988 && (_threshold || Below(10)))
        {
            _threshold = true;
            if (Due(ref _quake, diffMs) && Cast(19798)) _quake = 3000;
        }
        if (Me.Template.Entry == 11672 && Below(50) && Cast(17683)) Say(7865);
        if (Me.Template.Entry != 12056 || !_threshold) TickActions(diffMs);
    }
    public override void OnSpellHit(Unit caster, SpellInfo spell)
    {
        if (Me.Template.Entry == 12057 && spell.Id == 19515) Cast(19516, triggered: true);
        if (Me.Template.Entry == 12099 && spell.Id == 20482) Cast(20483);
    }
    public override void OnReceiveAiEvent(uint eventType, Unit sender, Unit? invoker, uint miscValue)
    {
        // mangos-classic boss_shazzrahAI::ReceiveAIEvent, AI_EVENT_CUSTOM_A (1000).
        if (Me.Template.Entry != 12264 || eventType != 1000) return;
        foreach (var entry in Me.Combat.Threat.Entries.ToArray()) Me.Combat.Threat.ModifyThreatPercent(entry.Target, -100);
        Cast(19712);
    }
    public override void OnDeath(Unit? killer)
    {
        base.OnDeath(killer);
        if (Me.Template.Entry == 12099)
        {
            if (!ReferenceEquals(killer, Me)) Cast(19497, triggered: true);
            Cast(19515, triggered: true);
        }
        if (Me.Template.Entry == 11988 && System is { } system)
            foreach (Creature add in system.CreaturesOfEntryInRange(Me, 11672, 100).Where(c => c.IsAlive).ToArray())
                system.CastSpell(add, 3617, add, true);
    }
}
