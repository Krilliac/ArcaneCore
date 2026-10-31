using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// AIName 'SmartAI': the creature runs its <c>smart_scripts</c> rows (AzerothCore src/server/game/AI/SmartScripts/SmartAI.cpp, re-implemented;
/// no code copied). A thin <see cref="AggressorAI"/> that forwards the AI hooks to a <see cref="SmartScript"/>. Movement, melee and
/// victim selection stay the aggressor's; the rows add behaviour on top. See docs/integration/smartai-20261010.md for how this sits
/// next to EventAI and the C# scripts.
/// </summary>
public sealed class CreatureSmartAI : AggressorAI
{
    public CreatureSmartAI(Creature creature, CreatureAiContent content) : base(creature)
    {
        ArgumentNullException.ThrowIfNull(content);
        Script = new SmartScript(this, content.SmartScripts.For(creature.Template.Entry, creature.Spawn?.Guid ?? 0), content.SmartScripts);
    }

    public SmartScript Script { get; }

    internal CreatureMapSystem? Host => System;

    /// <summary>SMART_ACTION_AUTO_ATTACK (UnitAI::SetMeleeEnabled): a change starts or stops the swing at the current victim.</summary>
    internal void SetMeleeEnabled(bool enabled)
    {
        if (enabled == MeleeEnabled) return;
        MeleeEnabled = enabled;
        if (Victim is { } current) System?.SetMelee(Me, current, enabled);
    }

    /// <summary>
    /// AzerothCore SmartAI::InitializeAI and SmartAI::JustRespawned (SmartAI.cpp:861-871, 1104-1109): OnReset, then SMART_EVENT_RESPAWN. ArcaneCore
    /// calls this both on the first spawn and on every respawn.
    /// </summary>
    public override void OnRespawn()
    {
        Script.OnReset();
        Script.ProcessEventsFor(SmartEvent.Respawn);
    }

    /// <summary>SMART_EVENT_FRIENDLY_HEALTH's search (SmartScript::DoSelectLowestHpFriendly): the in-combat friend missing more than <paramref name="minMissing"/>.</summary>
    internal Creature? LowestHpFriendly(float range, uint minMissing) => SelectLowestHpFriendly(range, minMissing);

    /// <summary>AzerothCore SmartAI::JustEngagedWith: SMART_EVENT_AGGRO with the attacker as invoker.</summary>
    public override void OnAggro(Unit target) => Script.ProcessEventsFor(SmartEvent.Aggro, target);

    public override void OnDeath(Unit? killer) => Script.ProcessEventsFor(SmartEvent.Death, killer);

    /// <summary>AzerothCore SmartAI::EnterEvadeMode: SMART_EVENT_EVADE (the reset waits for home).</summary>
    public override void OnEvade() => Script.ProcessEventsFor(SmartEvent.Evade);

    /// <summary>AzerothCore SmartAI::JustReachedHome: OnReset, then SMART_EVENT_REACHED_HOME.</summary>
    public override void OnReachedHome()
    {
        Script.OnReset();
        Script.ProcessEventsFor(SmartEvent.ReachedHome);
    }

    /// <summary>AzerothCore SmartAI::KilledUnit: SMART_EVENT_KILL with the victim as invoker.</summary>
    public override void OnKilledUnit(Unit victim)
    {
        base.OnKilledUnit(victim);
        Script.ProcessEventsFor(SmartEvent.Kill, victim);
    }

    /// <summary>AzerothCore SmartAI::JustSummoned: SMART_EVENT_SUMMONED_UNIT with the summon as invoker.</summary>
    public override void OnJustSummoned(Creature summoned)
    {
        base.OnJustSummoned(summoned);
        Script.ProcessEventsFor(SmartEvent.SummonedUnit, summoned);
    }

    /// <summary>AzerothCore SmartAI::ReceiveEmote: SMART_EVENT_RECEIVE_EMOTE with the player as invoker and the text emote as var0.</summary>
    public override void OnReceiveEmote(Player player, uint textEmote)
    {
        base.OnReceiveEmote(player, textEmote);
        Script.ProcessEventsFor(SmartEvent.ReceiveEmote, player, textEmote);
    }

    /// <summary>AzerothCore SmartAI::sGossipHello (SmartAI.cpp:1202-1205): SMART_EVENT_GOSSIP_HELLO with the player as invoker; the menu still shows.</summary>
    public override void OnGossipHello(Player player) => Script.ProcessEventsFor(SmartEvent.GossipHello, player, 0);

    /// <summary>AzerothCore SmartAI::sGossipSelect (SmartAI.cpp:1207-1210): SMART_EVENT_GOSSIP_SELECT, var0 the menu id and var1 the option id.</summary>
    public override void OnGossipSelect(Player player, uint menuId, uint optionId)
        => Script.ProcessEventsFor(SmartEvent.GossipSelect, player, menuId, null, optionId);

    public override void OnSpellHit(Unit caster, SpellInfo spell) => Script.ProcessEventsFor(SmartEvent.SpellHit, caster, spell: spell);

    /// <summary>AzerothCore SmartAI::UpdateAI: the timers every update, then the aggressor's victim choice and melee.</summary>
    public override void OnUpdate(uint diffMs)
    {
        if (!Me.IsAlive) return;
        Script.OnUpdate(diffMs);
        base.OnUpdate(diffMs);
    }
}
