using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>
/// Guard Slip'kik's frost trap in Dire Maul North (quest 1193 "A Broken Trap").
/// <para>
/// Rewarding the quest at the Broken Trap (179485) shows the Fixed Trap (179512). classic-db z2815's CompleteScript respawns gameobject
/// guid 4290040, which is the Broken Trap itself, so the Fixed Trap row (guid 4290049, spawntimesecs -7200, same spot) never showed; the
/// instance shows it here instead, as vmangos QuestRewarded_go_broken_trap (instance_dire_maul.cpp:801-813) summons it.
/// </para>
/// <para>
/// mangos-classic instance_dire_maul.cpp go_ai_fixed_trap::UpdateAI (:637-660): while the trap is spawned it reacts only to Guard Slip'kik
/// (14323). When he walks into it he says SAY_SLIPKIK_TRAP ("Doh!"), evades, becomes immune to players and takes faction 14 until he
/// respawns, and the trap despawns. The trap's frost is its own spell, Ice Lock (5249, the trap's data3), which cmangos casts through
/// GameObject::Use(slipkik); vmangos go_fixed_trap casts the Ice Lock 22856 instead (:1297-1310). The reach is vmangos' 2 yd (cmangos looks
/// 0.5 yd past both bounding radii).
/// GuardSlipkikTrigger (31770, triggered by King of the Gordok 22799) gives him the ogre faction 45 back (:672-683).
/// </para>
/// </summary>
public sealed partial class DireMaulInstance
{
    public const uint GoBrokenTrap = 179485, GoFixedTrap = 179512, QuestABrokenTrap = 1193;
    public const uint SpellIceLock = 5249, SpellGuardSlipkikTrigger = 31770;
    public const int SaySlipkikTrap = -1429004;
    public const uint FactionHostile = 14, FactionOgre = 45;
    public const float FixedTrapReach = 2.0f;

    private FixedTrapAI? _fixedTrapAi;

    private void RegisterFixedTrapAi()
    {
        if (_fixedTrapAi is null && Instance.FindUpdater<GameObjectMapSystem>() is { } objects)
        {
            objects.RegisterAi(GoFixedTrap, _fixedTrapAi = new FixedTrapAI(this));
        }
    }

    /// <summary>QuestRewarded_go_broken_trap: show the Fixed Trap where the Broken Trap lies (once while one is up).</summary>
    public override void OnGameObjectQuestRewarded(Player player, GameObject go, uint questId)
    {
        if (questId != QuestABrokenTrap || go.Entry != GoBrokenTrap || Instance.FindUpdater<GameObjectMapSystem>() is not { } objects)
        {
            return;
        }

        RegisterFixedTrapAi();
        if (objects.GameObjects.Any(o => o.Entry == GoFixedTrap && o.IsSpawned && o.LootState != GameObjectLootState.JustDeactivated))
        {
            return;
        }

        if (objects.GameObjects.FirstOrDefault(o => o.Entry == GoFixedTrap && !o.IsSpawned && o.Spawn is not null) is { } row)
        {
            objects.ForceRespawn(row);
            if (row.IsSpawned)
            {
                row.LootState = GameObjectLootState.Ready;
                return;
            }
        }

        objects.Summon(GoFixedTrap, go.X, go.Y, go.Z, go.Orientation, despawnAfterSeconds: 43200);
    }

    /// <summary>The trap sprang on Slip'kik (go_ai_fixed_trap::UpdateAI).</summary>
    internal bool TrySpringFixedTrap(GameObjectMapSystem objects, GameObject trap)
    {
        if (!trap.IsSpawned || trap.LootState == GameObjectLootState.JustDeactivated
            || GetSingleCreatureFromStorage(NpcSlipkik) is not { IsAlive: true, IsInWorld: true } slipkik)
        {
            return false;
        }

        float dx = slipkik.X - trap.X, dy = slipkik.Y - trap.Y, dz = slipkik.Z - trap.Z;
        if (dx * dx + dy * dy + dz * dz > FixedTrapReach * FixedTrapReach)
        {
            return false;
        }

        objects.SendCustomAnim(trap, 0);
        CreatureMapSystem? creatures = slipkik.System ?? Instance.FindUpdater<CreatureMapSystem>();
        creatures?.SayText(slipkik, SaySlipkikTrap);
        slipkik.AI?.EnterEvadeMode();
        slipkik.UnitFlags |= UnitFlags.ImmuneToPlayer;
        slipkik.SetFactionTemporary(FactionHostile, TemporaryFactionFlags.RestoreRespawn);
        creatures?.CastSpell(slipkik, SpellIceLock, slipkik, triggered: true);
        trap.LootState = GameObjectLootState.JustDeactivated; // despawn the trap
        return true;
    }

    /// <summary>GuardSlipkikTrigger::OnEffectExecute: the stored Slip'kik takes the ogre faction again.</summary>
    internal void RestoreSlipkikFaction()
    {
        if (GetSingleCreatureFromStorage(NpcSlipkik) is { } slipkik)
        {
            slipkik.FactionTemplate = FactionOgre;
        }
    }

    private sealed class FixedTrapAI(DireMaulInstance instance) : IGameObjectAi
    {
        public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target) => true; // only Slip'kik springs it

        public bool AcceptsTrapTarget(GameObjectMapSystem objects, GameObject go, Unit candidate) => false;

        public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs) => instance.TrySpringFixedTrap(objects, go);
    }
}

/// <summary>mangos-classic instance_dire_maul.cpp GuardSlipkikTrigger (31770): King of the Gordok gives Slip'kik the ogre faction back.</summary>
[SpellScript(DireMaulInstance.SpellGuardSlipkikTrigger)]
public sealed class GuardSlipkikTriggerScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.Caster.Map?.FindUpdater<InstanceData>() is DireMaulInstance dm)
        {
            dm.RestoreSlipkikFaction();
        }
    }
}
