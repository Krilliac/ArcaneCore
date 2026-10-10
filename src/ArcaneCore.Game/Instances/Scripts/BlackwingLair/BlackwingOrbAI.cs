using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Instances.Scripts.BlackwingLair;

/// <summary>
/// mangos-classic blackwing_lair/boss_razorgore.cpp npc_blackwing_orbAI (Blackwing Orb Trigger 14449).
/// SpellHit: Razorgore's reset Fireball (23024) summons Orb of Domination (14453, 5 s timed despawn) and casts Explode Orb (20037) on
/// itself. UpdateAI: 4 s after reset, the possess visual (23014) starts once Razorgore is alive and Grethok the Controller (12557) is
/// alive within 2 yd, who then casts Control Orb (23018); otherwise it retries every 2 s. Limit: EMOTE_ORB_SHUT_OFF (-1469035) is not
/// said, because the broadcast_text id of that SD2 text is not in this repository.
/// </summary>
public sealed class BlackwingOrbAI : CreatureAI
{
    public const uint Entry = 14449, OrbOfDomination = 14453, Grethok = 12557, Razorgore = 12435;
    public const uint ResetFireball = 23024, ExplodeOrb = 20037, PossessVisual = 23014, ControlOrb = 23018;
    public const uint IntroVisualMs = 4000, RetryMs = 2000;

    public BlackwingOrbAI(Creature creature) : base(creature) => IntroTimerMs = IntroVisualMs;

    public uint IntroTimerMs { get; private set; }

    public override void OnRespawn()
    {
        base.OnRespawn();
        IntroTimerMs = IntroVisualMs;
    }

    public override void OnSpellHit(Unit caster, SpellInfo spell)
    {
        if (spell.Id != ResetFireball || System is not { } system) return;
        system.Summon(Me, OrbOfDomination, null, 5000);
        DoCast(Me, ExplodeOrb, triggered: true);
    }

    public override void OnUpdate(uint diffMs)
    {
        if (IntroTimerMs == 0 || System is not { } system) return;
        if (IntroTimerMs > diffMs)
        {
            IntroTimerMs -= diffMs;
            return;
        }

        if (system.Creatures.FirstOrDefault(c => c.Entry == Razorgore) is { IsAlive: false })
        {
            IntroTimerMs = RetryMs;
            return;
        }

        // GetClosestCreatureWithEntry finds only a living Grethok; without one the check repeats in 2 s.
        if (system.CreaturesOfEntryInRange(Me, Grethok, 2f).FirstOrDefault(c => c.IsAlive) is { } grethok)
        {
            DoCast(Me, PossessVisual, triggered: true);
            system.CastSpellByUnit(grethok, ControlOrb, grethok, triggered: true);
            IntroTimerMs = 0;
        }
        else IntroTimerMs = RetryMs;
    }
}
