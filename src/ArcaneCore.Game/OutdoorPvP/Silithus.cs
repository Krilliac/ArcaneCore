using ArcaneCore.Game.Entities;
using ArcaneCore.Game.WorldState.States;

namespace ArcaneCore.Game.OutdoorPvP;

/// <summary>
/// Silithyst (vmangos OutdoorPvPSI.cpp <c>OutdoorPvPSI</c>): carriers bring silithyst to their camp's area trigger; the first team to
/// <see cref="MaxResources"/> wins Cenarion Favor for the zone and both counters reset.
/// </summary>
public sealed class SilithusZone(IOutdoorPvPHost host, uint maxResources = SilithusCatalog.DefaultMaxResources) : OutdoorPvPZoneScript(host)
{
    private readonly List<ObjectGuid> _bagsAlliance = [];
    private readonly List<ObjectGuid> _bagsHorde = [];

    /// <summary>The trigger positions, for the keep-on-drop check (vmangos <c>sObjectMgr.GetAreaTrigger</c>); set by the world.</summary>
    public Func<uint, (float X, float Y, float Z)?> AreaTriggerPosition { get; set; } = static _ => null;

    public override uint MapId => SilithusCatalog.MapId;

    public override IReadOnlyList<uint> Zones => SilithusCatalog.BuffZones;

    public uint MaxResources { get; } = maxResources == 0 ? SilithusCatalog.DefaultMaxResources : maxResources;

    /// <summary>
    /// vmangos <c>sObjectMgr.SetSavedVariable</c> of the three Silithyst states at every <c>UpdateWorldState</c>: called with
    /// (gathered Alliance, gathered Horde, maximum). The world feature writes them to the characters database (schema 53).
    /// </summary>
    public Action<uint, uint, uint>? Saved { get; init; }

    public uint GatheredAlliance { get; private set; }

    public uint GatheredHorde { get; private set; }

    /// <summary>Silithyst flags picked up (the flag aura applied) since the zone script started; the world feature counts them.</summary>
    public ulong PickupsTotal { get; private set; }

    /// <summary>Turn-ins accepted at the own camp since the zone script started (the reset at the maximum does not clear it).</summary>
    public ulong TurnInsTotal { get; private set; }

    /// <summary>The camp counter right after the latest turn-in (before a reset at the maximum), for the log line.</summary>
    public uint LastTurnInCount { get; private set; }

    /// <summary>Called by the world feature when a player takes the flag (aura 29519 applied).</summary>
    public void NotePickup() => PickupsTotal++;

    /// <summary>vmangos <c>m_LastController</c>: the last team to fill its counter.</summary>
    public Team? LastController { get; private set; }

    public int DustBagsAlliance => _bagsAlliance.Count;

    public int DustBagsHorde => _bagsHorde.Count;

    public override void Setup()
    {
        GatheredAlliance = 0;
        GatheredHorde = 0;
    }

    public override void OnPlayerEnter(OutdoorPvPPlayer player)
    {
        if (player.Team == LastController)
        {
            Host.CastOnSelf(player.Guid, SilithusCatalog.SpellCenarionFavor);
        }

        base.OnPlayerEnter(player);
    }

    public override void FillInitialWorldStates(List<WorldStatePair> states)
    {
        Fill(states, SilithusCatalog.WorldStateGatheredAlliance, GatheredAlliance);
        Fill(states, SilithusCatalog.WorldStateGatheredHorde, GatheredHorde);
        Fill(states, SilithusCatalog.WorldStateSilithystMax, MaxResources);
    }

    public override void SendRemoveWorldStates(ObjectGuid player)
    {
        Host.SendWorldState(player, SilithusCatalog.WorldStateGatheredAlliance, 0);
        Host.SendWorldState(player, SilithusCatalog.WorldStateGatheredHorde, 0);
        Host.SendWorldState(player, SilithusCatalog.WorldStateSilithystMax, 0);
    }

    private void UpdateWorldState()
    {
        SendUpdateWorldState(SilithusCatalog.WorldStateGatheredAlliance, GatheredAlliance);
        SendUpdateWorldState(SilithusCatalog.WorldStateGatheredHorde, GatheredHorde);
        SendUpdateWorldState(SilithusCatalog.WorldStateSilithystMax, MaxResources);
        Saved?.Invoke(GatheredAlliance, GatheredHorde, MaxResources);
    }

    /// <summary>vmangos <c>OutdoorPvPSI::HandleAreaTrigger</c> (OutdoorPvPSI.cpp:160-261).</summary>
    public override bool HandleAreaTrigger(OutdoorPvPPlayer player, uint triggerId)
    {
        if (!Host.HasAura(player.Guid, SilithusCatalog.SpellSilithystFlag))
        {
            return false;
        }

        Team team;
        if (triggerId == SilithusCatalog.AreaTriggerAlliance)
        {
            team = Team.Alliance;
        }
        else if (triggerId == SilithusCatalog.AreaTriggerHorde)
        {
            team = Team.Horde;
        }
        else
        {
            return false;
        }

        // The other camp's trigger is swallowed without effect.
        if (player.Team != team)
        {
            return true;
        }

        uint gathered = team == Team.Alliance ? ++GatheredAlliance : ++GatheredHorde;
        TurnInsTotal++;
        LastTurnInCount = gathered;
        uint[] yells = team == Team.Alliance ? SilithusCatalog.YellsAlliance : SilithusCatalog.YellsHorde;
        int yell = gathered == MaxResources / 4 ? 0
            : gathered == MaxResources / 2 ? 1
            : gathered == MaxResources - (MaxResources / 4) ? 2
            : gathered == MaxResources ? 3
            : -1;
        if (yell >= 0)
        {
            Host.NearestCreatureSays(player.Guid, team == Team.Alliance ? SilithusCatalog.AnnouncerAlliance : SilithusCatalog.AnnouncerHorde, yells[yell]);
        }

        if (gathered >= MaxResources)
        {
            TeamApplyBuff(team, SilithusCatalog.SpellCenarionFavor);
            Host.SendZoneText(SilithusCatalog.SilithusZone, team == Team.Alliance ? SilithusCatalog.CaptureTextAlliance : SilithusCatalog.CaptureTextHorde);
            LastController = team;
            ResetResourceCount();
        }
        else
        {
            SpawnDustBags(gathered, team == Team.Alliance ? SilithusCatalog.DustBagsAlliance : SilithusCatalog.DustBagsHorde,
                team == Team.Alliance ? _bagsAlliance : _bagsHorde);
        }

        Host.KilledMonsterCredit(player.Guid, team == Team.Alliance ? SilithusCatalog.TurnInCreditAlliance : SilithusCatalog.TurnInCreditHorde);

        Host.RemoveAura(player.Guid, SilithusCatalog.SpellSilithystFlag);
        Host.CastOnSelf(player.Guid, SilithusCatalog.SpellTracesOfSilithyst);
        Host.CastOnSelf(player.Guid, SilithusCatalog.SpellHonorPoints199);
        Host.CastOnSelf(player.Guid, SilithusCatalog.SpellSilithystCapReward);
        UpdateWorldState();
        return true;
    }

    /// <summary>
    /// vmangos <c>OutdoorPvPSI::HandleDropFlag</c> (OutdoorPvPSI.cpp:263-282): reached when the carrier mounts or stealths. Within five
    /// yards of the own turn-in trigger the flag is kept for the turn-in; otherwise the drop spell puts it on the ground.
    /// </summary>
    public override bool HandleDropFlag(OutdoorPvPPlayer player, uint spellId)
    {
        if (spellId != SilithusCatalog.SpellSilithystFlag)
        {
            return false;
        }

        uint trigger = player.Team == Team.Alliance ? SilithusCatalog.AreaTriggerAlliance : SilithusCatalog.AreaTriggerHorde;
        if (AreaTriggerPosition(trigger) is { } at
            && Host.DistanceTo(player.Guid, SilithusCatalog.MapId, at.X, at.Y, at.Z) is { } distance
            && distance <= SilithusCatalog.TurnInKeepRadius)
        {
            return false;
        }

        Host.CastOnSelf(player.Guid, SilithusCatalog.SpellFlagDrop);
        return true;
    }

    /// <summary>vmangos <c>SpawnDustBags</c>: one bag per 15 delivered, in table order.</summary>
    private void SpawnDustBags(uint resource, OutdoorPvPSpawn[] all, List<ObjectGuid> spawned)
    {
        uint needed = resource / SilithusCatalog.SilithystPerDustBag;
        for (int i = spawned.Count; i < all.Length && spawned.Count < needed; i++)
        {
            if (Host.SummonObject(all[i]) is { } guid)
            {
                spawned.Add(guid);
            }
        }
    }

    /// <summary>vmangos <c>ResetResourceCount</c>: both counters to 0 and every dust bag removed.</summary>
    private void ResetResourceCount()
    {
        GatheredAlliance = 0;
        GatheredHorde = 0;
        foreach (ObjectGuid guid in _bagsAlliance.Concat(_bagsHorde))
        {
            Host.RemoveObject(guid);
        }

        _bagsAlliance.Clear();
        _bagsHorde.Clear();
    }
}
