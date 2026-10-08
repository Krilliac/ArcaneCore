using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Instances.Scripts.RuinsOfAhnQiraj;

/// <summary>
/// vmangos scripts/kalimdor/silithus/ruins_of_ahnqiraj/boss_kurinnaxx.cpp
/// KurinnaxxSandTrap::OnEffectExecute: spell 26524 summons 180647 at a random threat target.
/// Missing templates are reported, never replaced with made-up trap data.
/// </summary>
[SpellScript(26524)]
public sealed class KurinnaxxSandTrapScript : ISpellScript
{
    public const uint TrapEntry = 180647;

    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.EffectIndex != 0 || context.Caster is not Creature { AI: KurinnaxxAI } caster
            || caster.Map?.FindUpdater<GameObjectMapSystem>() is not { } objects)
        {
            return;
        }

        Unit[] targets = [.. caster.Combat.Threat.Entries.Select(e => e.Target)
            .Where(t => t.IsAlive && t.IsInWorld && ReferenceEquals(t.Map, caster.Map))];
        if (targets.Length == 0)
        {
            return;
        }

        Unit target = targets[context.System.Random.Next(targets.Length)];
        // SD2 activates after four seconds; vmangos removes untriggered traps after five.
        if (objects.Summon(TrapEntry, target.X, target.Y, target.Z, 0, despawnAfterSeconds: 5) is { } trap)
        {
            trap.SpellId = context.Spell.Id;
            trap.SetOwner(caster.Guid);
        }
        else
        {
            caster.Map.FindUpdater<InstanceData>()?.Logger.LogWarning("Kurinnaxx sand trap template {Entry} is missing", TrapEntry);
        }
    }
}

/// <summary>
/// mangos-classic boss_kurinnaxxAI::JustSummoned(GameObject*) / TriggerTrap:
/// activate each summoned sand trap after 4000 ms even when its template radius is zero.
/// The GameObject host owns removal and clock; no wall timer or retained GUID list.
/// </summary>
internal sealed class KurinnaxxSandTrapAI : IGameObjectAi
{
    public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target) => go.SpellId == 26524;

    public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs)
    {
        if (go.SpellId != 26524 || !go.IsSpawned || go.LootState == GameObjectLootState.JustDeactivated
            || objects.ClockMs - go.CreatedAtMs < 4000)
        {
            return;
        }

        if (objects.Map.FindUpdater<CreatureMapSystem>()?.FindCreature(go.OwnerGuid) is { } owner)
        {
            objects.TriggerScriptedTrap(go, owner);
        }
    }
}
