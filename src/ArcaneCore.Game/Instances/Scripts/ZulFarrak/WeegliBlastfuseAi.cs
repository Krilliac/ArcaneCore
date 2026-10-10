using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Npc;

namespace ArcaneCore.Game.Instances.Scripts.ZulFarrak;

/// <summary>
/// vmangos npc_weegli_blastfuse: after the pyramid fight, his gossip starts a run to plant an explosive,
/// retreat, blow the end door, and leave. The door remains shut until the retreat reaches its point.
/// </summary>
public sealed class WeegliBlastfuseAi(Creature creature, ZulFarrakInstance instance) : ScriptedAI(creature), INpcGossipScript
{
    public const uint ExplosiveCharge = 144065;
    private const uint ExplosionSpell = 13259;
    private const uint Sender = 1, BlowDoorAction = 1001;
    private const string BlowDoorOption = "Will you blow up that door now?";

    private enum DoorRun { None, Planting, Retreating, Leaving, Complete }

    private DoorRun _run;
    private GameObject? _charge;
    private uint _bombMs;

    protected override void Reset() => _bombMs = 10_000;

    public override void OnRespawn()
    {
        _run = DoorRun.None;
        _charge = null;
        base.OnRespawn();
    }

    public ScriptedGossipMenu? Hello(Player player, NpcInfo npc)
    {
        if (npc.Entry != ZulFarrakInstance.Weegli || npc.Guid != Me.Guid || !ReferenceEquals(player.Map, Me.Map)) return null;
        bool available = Me.IsAlive && _run == DoorRun.None
            && instance.GetData(ZulFarrakInstance.TypePyramid) == EncounterState.Done
            && instance.GetData(ZulFarrakInstance.TypeEndDoor) != EncounterState.Done;
        return new ScriptedGossipMenu(false, 0,
            available ? [new ScriptedGossipItem(0, BlowDoorOption, Sender, BlowDoorAction)] : []);
    }

    public uint Select(Player player, NpcInfo npc, uint sender, uint action) => SelectReply(player, npc, sender, action).NpcTextId;

    public ScriptedGossipReply SelectReply(Player player, NpcInfo npc, uint sender, uint action)
    {
        if (sender != Sender || action != BlowDoorAction || Hello(player, npc)?.Items.Count != 1 || !StartDoorRun())
            return default;
        return new ScriptedGossipReply(0, Close: true);
    }

    private bool StartDoorRun()
    {
        if (_run != DoorRun.None || !Me.IsAlive || instance.GetData(ZulFarrakInstance.TypePyramid) != EncounterState.Done
            || instance.GetData(ZulFarrakInstance.TypeEndDoor) == EncounterState.Done)
            return false;

        _run = DoorRun.Planting;
        Me.FactionTemplate = 35; // friendly while placing the charge
        System?.SayText(Me, 3785); // SAY_WEEGLI_OK_I_GO
        Me.Motion.MovePoint(0, 1858.57f, 1146.35f, 14.745f, run: true);
        return true;
    }

    public override void OnMovementInform(MovementGeneratorType type, uint id)
    {
        if (!Me.IsAlive || type != MovementGeneratorType.Point) return;
        if (_run == DoorRun.Planting && id == 0)
        {
            GameObjectMapSystem? objects = Me.Map?.FindUpdater<GameObjectMapSystem>();
            _charge = objects?.Summon(ExplosiveCharge, 1856.3142f, 1144.9905f, 15.4863f, 5.6635f, despawnAfterSeconds: 30);
            if (_charge is null)
            {
                _run = DoorRun.None; // content absent: leave the door shut and allow a retry
                return;
            }

            _run = DoorRun.Retreating;
            Me.Motion.MovePoint(1, 1863.77f, 1176.99f, 9.993f, run: true);
        }
        else if (_run == DoorRun.Retreating && id == 1 && _charge is { } charge)
        {
            _run = DoorRun.Leaving;
            charge.SpellId = ExplosionSpell;
            charge.State = GameObjectState.Active;
            Me.Map?.FindUpdater<GameObjectMapSystem>()?.Spells?.Cast(charge, ExplosionSpell, Me, null);
            instance.SetData(ZulFarrakInstance.TypeEndDoor, EncounterState.Done);
            if (System?.Creatures.FirstOrDefault(c => c.Entry == 7267 && c.IsAlive) is { } chief)
                System.SayText(chief, 6067);
            Me.Motion.MovePoint(2, 1827.1f, 1184f, 8.993f, run: true);
        }
        else if (_run == DoorRun.Leaving && id == 2)
        {
            _run = DoorRun.Complete;
            System?.ForcedDespawn(Me, 0);
        }
    }

    public override void OnDeath(Unit? killer)
    {
        if ((_run is DoorRun.Planting or DoorRun.Retreating) && _charge is { } charge)
            Me.Map?.FindUpdater<GameObjectMapSystem>()?.Remove(charge);
        _charge = null;
        _run = DoorRun.None;
    }

    public override bool OnEnterEvadeMode()
    {
        if (_run is DoorRun.None or DoorRun.Complete) return false;
        // The default evade would send him home after OnEvade. Keep the run's current point instead.
        System?.StopCombatInPlace(Me);
        Reset();
        switch (_run)
        {
            case DoorRun.Planting: Me.Motion.MovePoint(0, 1858.57f, 1146.35f, 14.745f, run: true); break;
            case DoorRun.Retreating: Me.Motion.MovePoint(1, 1863.77f, 1176.99f, 9.993f, run: true); break;
            case DoorRun.Leaving: Me.Motion.MovePoint(2, 1827.1f, 1184f, 8.993f, run: true); break;
        }
        return true;
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!UpdateVictim()) return;
        if (_bombMs <= diffMs)
        {
            DoCast(Victim, 8858); // Goblin Bomb, vmangos' ten-second combat timer
            _bombMs = 10_000;
        }
        else _bombMs -= diffMs;
    }
}
