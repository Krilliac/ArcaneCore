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

    /// <summary>AzerothCore SmartAI::JustRespawned: OnReset.</summary>
    public override void OnRespawn() => Script.OnReset();

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

    public override void OnSpellHit(Unit caster, SpellInfo spell) => Script.ProcessEventsFor(SmartEvent.SpellHit, caster, spell: spell);

    /// <summary>AzerothCore SmartAI::UpdateAI: the timers every update, then the aggressor's victim choice and melee.</summary>
    public override void OnUpdate(uint diffMs)
    {
        if (!Me.IsAlive) return;
        Script.OnUpdate(diffMs);
        base.OnUpdate(diffMs);
    }
}
