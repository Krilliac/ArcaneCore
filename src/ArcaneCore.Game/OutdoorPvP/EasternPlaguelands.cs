using ArcaneCore.Game.Entities;
using ArcaneCore.Game.WorldState.States;
using C = ArcaneCore.Game.OutdoorPvP.EasternPlaguelandsCatalog;

namespace ArcaneCore.Game.OutdoorPvP;

/// <summary>
/// One Eastern Plaguelands tower (vmangos OutdoorPvPEP.cpp <c>OPvPCapturePointEP_EWT/NPT/CGT/PWT</c>). The four classes there share
/// one <c>ChangeState</c> shape; the reward of each tower is the switch on <see cref="EpTowerData.Tower"/>:
/// Eastwall a Lordaeron squad, Northpass a curing shrine, Crown Guard a graveyard and the Spirit of Victory, Plaguewood a flight master.
/// </summary>
public sealed class EasternPlaguelandsTower : CapturePoint
{
    private readonly EasternPlaguelandsZone _ep;
    private ObjectGuid? _banner1;
    private ObjectGuid? _banner2;
    private ObjectGuid? _flare;
    private ObjectGuid? _buffer;
    private ObjectGuid? _shrine;
    private ObjectGuid? _bannerAura;
    private ObjectGuid? _flightMaster;
    private ObjectGuid? _spirit;
    private readonly ObjectGuid?[] _squad = new ObjectGuid?[5];

    internal EasternPlaguelandsTower(EasternPlaguelandsZone zone, EpTowerData data, CapturePointTemplate template)
        : base(zone, data.CapturePoint, template)
    {
        _ep = zone;
        Data = data;
    }

    public EpTowerData Data { get; }

    /// <summary>vmangos <c>m_TowerState</c>.</summary>
    public EpTowerState TowerState { get; private set; } = EpTowerState.Neutral;

    /// <summary>Who has the Crown Guard graveyard (vmangos LinkGraveYard / UnLinkGraveYard); null when unlinked or not Crown Guard.</summary>
    public Team? GraveyardTeam { get; private set; }

    internal IReadOnlyList<ObjectGuid?> Squad => _squad;

    internal ObjectGuid? Buffer => _buffer;

    internal ObjectGuid? FlightMaster => _flightMaster;

    internal ObjectGuid? Shrine => _shrine;

    internal ObjectGuid? Flare => _flare;

    /// <summary>The constructor's body: the capture point, the two banners, then ChangeState for the neutral start.</summary>
    internal void Create()
    {
        Spawn();
        _banner1 = Host.SummonObject(Data.Banner1);
        _banner2 = Host.SummonObject(Data.Banner2);
        InitializeState();
    }

    protected override void SendChangePhase() => SendToActive(C.WorldStateSliderPosition, ValuePct);

    protected override void ChangeState()
    {
        // "If changing from controlling alliance to horde or vice versa."
        if ((OldState == ObjectiveState.Alliance || OldState == ObjectiveState.Horde) && OldState != State)
        {
            _ep.SetControl(Data.Tower, null);
        }

        Delete(ref _buffer, creature: true);
        Delete(ref _flare, creature: false);
        switch (Data.Tower)
        {
            case EpTower.Northpass:
                Delete(ref _shrine, creature: false);
                Delete(ref _bannerAura, creature: false);
                break;
            case EpTower.CrownGuard:
                Delete(ref _bannerAura, creature: false);
                GraveyardTeam = null;
                break;
            case EpTower.Plaguewood:
                Delete(ref _flightMaster, creature: true);
                break;
        }

        switch (State)
        {
            case ObjectiveState.Alliance:
            case ObjectiveState.Horde:
            {
                Team team = State == ObjectiveState.Alliance ? Team.Alliance : Team.Horde;
                TowerState = team == Team.Alliance ? EpTowerState.Alliance : EpTowerState.Horde;
                PlaySound(team == Team.Alliance ? C.SoundVictoryAlliance : C.SoundVictoryHorde);
                if (Data.Tower == EpTower.CrownGuard)
                {
                    GraveyardTeam = team;
                    SummonBannerAura(team);
                }
                else if (Data.Tower == EpTower.Plaguewood)
                {
                    SummonFlightMaster(team);
                }

                _flare = Host.SummonObject(team == Team.Alliance ? Data.FlareAlliance : Data.FlareHorde);
                break;
            }
            case ObjectiveState.AllianceProgressing:
            case ObjectiveState.HordeProgressing:
            {
                Team team = State == ObjectiveState.AllianceProgressing ? Team.Alliance : Team.Horde;
                UpdateBannerArt(team == Team.Alliance ? C.ArtKitAlliance : C.ArtKitHorde, team == Team.Alliance ? C.AnimationAlliance : C.AnimationHorde);
                switch (Data.Tower)
                {
                    case EpTower.Northpass:
                        SummonCuringShrine(team);
                        break;
                    case EpTower.CrownGuard:
                        SummonBannerAura(team);
                        GraveyardTeam = team;
                        break;
                    case EpTower.Plaguewood:
                        SummonFlightMaster(team);
                        break;
                }

                ObjectiveState full = team == Team.Alliance ? ObjectiveState.Alliance : ObjectiveState.Horde;
                if (OldState == full)
                {
                    TowerState = team == Team.Alliance ? EpTowerState.AllianceProgressing : EpTowerState.HordeProgressing;
                    PlaySound(team == Team.Alliance ? C.SoundWarningAlliance : C.SoundWarningHorde);
                }
                else if (OldState <= ObjectiveState.HordeContested)
                {
                    // The slider left the grey band: the tower is taken.
                    TowerState = team == Team.Alliance ? EpTowerState.AllianceProgressing : EpTowerState.HordeProgressing;
                    PlaySound(team == Team.Alliance ? C.SoundFlagCapturedAlliance : C.SoundFlagCapturedHorde);
                    if (Data.Tower == EpTower.Eastwall)
                    {
                        SummonSquad(team);
                    }
                    else if (Data.Tower == EpTower.CrownGuard)
                    {
                        SummonSpiritOfVictory(team);
                    }

                    _buffer = Host.SummonCreature(team == Team.Alliance ? Data.BufferAlliance : Data.BufferHorde);
                    _ep.SetControl(Data.Tower, team);
                    if (OldState != State)
                    {
                        Host.SendDefenseMessage(C.MapId, C.ZoneId, team == Team.Alliance ? Data.TakenAllianceText : Data.TakenHordeText);
                    }
                }

                break;
            }
            case ObjectiveState.Neutral:
            case ObjectiveState.AllianceContested:
            case ObjectiveState.HordeContested:
                TowerState = State switch
                {
                    ObjectiveState.AllianceContested => EpTowerState.AllianceContested,
                    ObjectiveState.HordeContested => EpTowerState.HordeContested,
                    _ => EpTowerState.Neutral,
                };
                UpdateBannerArt(C.ArtKitNeutral, C.AnimationNeutral);
                if (Data.Tower == EpTower.Eastwall)
                {
                    RemoveSquad();
                }

                break;
        }

        if (_buffer is { } buffer)
        {
            Host.CreatureCastOnSelf(buffer, C.SpellTowerCaptureTest);
        }

        UpdateTowerState();
    }

    public override void FillInitialWorldStates(List<WorldStatePair> states)
    {
        foreach ((uint state, EpTowerState flag) in Data.States.All)
        {
            states.Add(new WorldStatePair(state, (TowerState & flag) != 0 ? 1 : 0));
        }
    }

    /// <summary>vmangos <c>UpdateTowerState</c>: the seven icons to the whole zone.</summary>
    private void UpdateTowerState()
    {
        foreach ((uint state, EpTowerState flag) in Data.States.All)
        {
            Zone.SendUpdateWorldState(state, (TowerState & flag) != 0 ? 1u : 0u);
        }
    }

    private void UpdateBannerArt(uint artKit, uint animation)
    {
        foreach (ObjectGuid? guid in new[] { Object, _banner1, _banner2 })
        {
            if (guid is { } g)
            {
                Host.SetBannerArt(g, artKit, animation);
            }
        }
    }

    private void PlaySound(uint sound)
    {
        if (Object is { } point)
        {
            Host.PlayObjectSound(point, sound);
        }
    }

    private void Delete(ref ObjectGuid? guid, bool creature)
    {
        if (guid is { } g)
        {
            if (creature)
            {
                Host.RemoveCreature(g);
            }
            else
            {
                Host.RemoveObject(g);
            }
        }

        guid = null;
    }

    private void RemoveSquad()
    {
        for (int i = 0; i < _squad.Length; i++)
        {
            Delete(ref _squad[i], creature: true);
        }
    }

    /// <summary>vmangos <c>SummonSquadAtEastWallTower</c>. The formation link to the commander is not modelled.</summary>
    private void SummonSquad(Team team)
    {
        OutdoorPvPSpawn[] squad = team == Team.Alliance ? C.EastwallSquadAlliance : C.EastwallSquadHorde;
        for (int i = 0; i < _squad.Length; i++)
        {
            Delete(ref _squad[i], creature: true);
            _squad[i] = Host.SummonCreature(squad[i]);
        }
    }

    private void SummonCuringShrine(Team team)
    {
        OutdoorPvPSpawn[] shrine = team == Team.Alliance ? C.NorthpassShrineAlliance : C.NorthpassShrineHorde;
        Delete(ref _shrine, creature: false);
        Delete(ref _bannerAura, creature: false);
        _shrine = Host.SummonObject(shrine[0]);
        _bannerAura = Host.SummonObject(shrine[1]);
    }

    private void SummonBannerAura(Team team)
    {
        Delete(ref _bannerAura, creature: false);
        _bannerAura = Host.SummonObject(team == Team.Alliance ? C.CrownGuardBannerAuraAlliance : C.CrownGuardBannerAuraHorde);
    }

    private void SummonFlightMaster(Team team)
    {
        Delete(ref _flightMaster, creature: true);
        _flightMaster = Host.SummonCreature(C.PlaguewoodFlightMaster,
            team == Team.Alliance ? C.FactionFlightMasterAlliance : C.FactionFlightMasterHorde,
            team == Team.Alliance ? C.SpellSpiritParticles : C.SpellSpiritParticlesRedBig);
    }

    /// <summary>vmangos <c>SummonSpiritOfVictory</c>. Its waypoint path (PATH_FROM_SPECIAL 18039) is not modelled.</summary>
    private void SummonSpiritOfVictory(Team team)
    {
        Delete(ref _spirit, creature: true);
        _spirit = Host.SummonCreature(C.CrownGuardSpiritOfVictory, 0,
            team == Team.Alliance ? C.SpellSpiritParticlesSuperBig : C.SpellSpiritParticlesRedSuperBig);
    }
}

/// <summary>
/// The Eastern Plaguelands towers (vmangos OutdoorPvPEP.cpp <c>OutdoorPvPEP</c>): the tower count world states and the Echoes of
/// Lordaeron buff of the team's tower count for everyone in Eastern Plaguelands, Stratholme and Scholomance.
/// </summary>
public sealed class EasternPlaguelandsZone(IOutdoorPvPHost host) : OutdoorPvPZoneScript(host)
{
    private readonly Team?[] _controls = new Team?[4];

    public override uint MapId => C.MapId;

    public override IReadOnlyList<uint> Zones => C.BuffZones;

    public uint AllianceTowers { get; private set; }

    public uint HordeTowers { get; private set; }

    public IEnumerable<EasternPlaguelandsTower> Towers => CapturePoints.OfType<EasternPlaguelandsTower>();

    public EasternPlaguelandsTower Tower(EpTower tower) => Towers.First(t => t.Data.Tower == tower);

    /// <summary>Who holds the Crown Guard graveyard (vmangos AddGraveYardLink 927 to zone 139 and area 2258).</summary>
    public Team? GraveyardTeam => Towers.FirstOrDefault(t => t.Data.Tower == EpTower.CrownGuard)?.GraveyardTeam;

    /// <summary>vmangos <c>EP_Controls[tower]</c>.</summary>
    public Team? Control(EpTower tower) => _controls[(int)tower];

    internal void SetControl(EpTower tower, Team? team) => _controls[(int)tower] = team;

    public override void Setup()
    {
        foreach (EpTowerData data in C.Towers)
        {
            CapturePointTemplate template = Host.CapturePoint(data.CapturePoint.Entry) ?? C.FallbackTemplate;
            var tower = new EasternPlaguelandsTower(this, data, template);
            AddCapturePoint(tower);
            tower.Create();
        }
    }

    public override void Update(uint diffMs)
    {
        base.Update(diffMs);
        if (!ObjectiveChanged)
        {
            return;
        }

        AllianceTowers = (uint)_controls.Count(c => c == Team.Alliance);
        HordeTowers = (uint)_controls.Count(c => c == Team.Horde);
        SendUpdateWorldState(C.WorldStateTowerCountAlliance, AllianceTowers);
        SendUpdateWorldState(C.WorldStateTowerCountHorde, HordeTowers);
        BuffTeams();

        if (AllianceTowers == 4)
        {
            Host.SendDefenseMessage(C.MapId, C.ZoneId, C.TextAllAlliance);
        }
        else if (HordeTowers == 4)
        {
            Host.SendDefenseMessage(C.MapId, C.ZoneId, C.TextAllHorde);
        }
    }

    public override void OnPlayerEnter(OutdoorPvPPlayer player)
    {
        if (BuffFor(player.Team) is { } buff)
        {
            Host.CastOnSelf(player.Guid, buff);
        }

        base.OnPlayerEnter(player);
    }

    public override void OnPlayerLeave(OutdoorPvPPlayer player, bool loggingOut)
    {
        foreach (uint buff in player.Team == Team.Alliance ? C.AllianceBuffs : C.HordeBuffs)
        {
            Host.RemoveAura(player.Guid, buff);
        }

        base.OnPlayerLeave(player, loggingOut);
    }

    /// <summary>vmangos <c>BuffTeams</c>: every player's Echoes of Lordaeron replaced with the rank of the current count.</summary>
    private void BuffTeams()
    {
        foreach (Team team in new[] { Team.Alliance, Team.Horde })
        {
            uint[] buffs = team == Team.Alliance ? C.AllianceBuffs : C.HordeBuffs;
            foreach (ObjectGuid guid in Players(team).ToArray())
            {
                foreach (uint buff in buffs)
                {
                    Host.RemoveAura(guid, buff);
                }

                if (BuffFor(team) is { } rank)
                {
                    Host.CastOnSelf(guid, rank);
                }
            }
        }
    }

    /// <summary>The Echoes of Lordaeron rank for the team's tower count, or null for none.</summary>
    public uint? BuffFor(Team team)
    {
        uint count = team == Team.Alliance ? AllianceTowers : HordeTowers;
        return count is >= 1 and <= 4 ? (team == Team.Alliance ? C.AllianceBuffs : C.HordeBuffs)[count - 1] : null;
    }

    public override void FillInitialWorldStates(List<WorldStatePair> states)
    {
        Fill(states, C.WorldStateTowerCountAlliance, AllianceTowers);
        Fill(states, C.WorldStateTowerCountHorde, HordeTowers);
        Fill(states, C.WorldStateSliderDisplay, 0u);
        Fill(states, C.WorldStateSliderPosition, 50u);
        Fill(states, C.WorldStateSliderNeutral, 100u);
        foreach (CapturePoint point in CapturePoints)
        {
            point.FillInitialWorldStates(states);
        }
    }

    public override void SendRemoveWorldStates(ObjectGuid player)
    {
        foreach (uint state in new[] { C.WorldStateTowerCountAlliance, C.WorldStateTowerCountHorde, C.WorldStateSliderDisplay, C.WorldStateSliderPosition, C.WorldStateSliderNeutral })
        {
            Host.SendWorldState(player, state, 0);
        }

        foreach (EpTowerData data in new[] { C.Eastwall, C.Northpass, C.Plaguewood, C.CrownGuard })
        {
            foreach ((uint state, _) in data.States.All)
            {
                Host.SendWorldState(player, state, 0);
            }
        }
    }
}
