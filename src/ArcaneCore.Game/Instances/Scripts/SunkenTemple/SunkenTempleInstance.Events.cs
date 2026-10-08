using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>
/// ScriptDev2 instance_sunken_temple (mangos-classic eastern_kingdoms/sunken_temple/sunken_temple.cpp:
/// OnCreatureCreate, OnCreatureDeath, OnCreatureEvade, SetData, DoSpawnAtalarionIfCan, ProcessStatueEvent, Update) and
/// sunken_templeScripts.cpp (statue event, Avatar event, eternal flame use, SummonHakkar).
/// </summary>
public sealed partial class SunkenTempleInstance
{
    public const uint NpcAtalarion = 8580;
    public const uint NpcJammalan = 5710;
    public const uint NpcOgom = 5711;
    public const uint NpcShadeOfEranikus = 5709;
    public const uint NpcAvatarOfHakkar = 8443;
    public const uint NpcShadeOfHakkar = 8440;
    public const uint NpcBloodkeeper = 8438;
    public const uint NpcHakkariMinion = 8437;
    public const uint NpcSuppressor = 8497;
    public const uint GoJammalanBarrier = 149431;
    public const uint GoIdolOfHakkar = 148838;
    public const uint GoBigLight = 148937;
    public const uint GoEvilCircle = 148998;
    public const uint GoFirstFlame = 148418;

    private static readonly uint[] Protectors = [5712, 5713, 5714, 5715, 5716, 5717];
    private static readonly uint[] StatueEvents = [3094, 3095, 3097, 3098, 3099, 3100];
    private static readonly (float X, float Y, float Z)[] SuppressorDoors =
        [(-420.629f, 276.682f, -90.827f), (-512.015f, 276.134f, -90.827f)];
    private readonly List<ObjectGuid> _bigLights = [];
    private readonly List<ObjectGuid> _evilCircles = [];
    private readonly List<ObjectGuid> _flames = [];
    private uint _protectorsRemaining;
    private int _statuesActivated;
    private int _flamesDoused;
    private uint _avatarWaveMs;
    private uint _suppressorMs;
    private uint _suppressionMs;
    private bool _firstWave;
    private bool _canSummonBloodkeeper;
    private bool _flameAiRegistered;

    public int StatuesActivated => _statuesActivated;
    public int FlamesDoused => _flamesDoused;
    public uint AvatarWaveRemainingMs => _avatarWaveMs;

    public override void Initialize()
    {
        base.Initialize();
        _protectorsRemaining = 0;
        _statuesActivated = _flamesDoused = 0;
        _avatarWaveMs = _suppressorMs = _suppressionMs = 0;
        _firstWave = _canSummonBloodkeeper = false;
        _bigLights.Clear();
        _evilCircles.Clear();
        _flames.Clear();
    }

    private void RecordTempleObject(GameObject go)
    {
        switch (go.Entry)
        {
            case GoJammalanBarrier:
                OpenIf(go, Encounters[TypeProtectors] == EncounterState.Done);
                StoreGameObject(go);
                break;
            case GoIdolOfHakkar:
                StoreGameObject(go);
                break;
            case GoBigLight:
                _bigLights.Add(go.Guid);
                break;
            case GoEvilCircle:
                _evilCircles.Add(go.Guid);
                break;
            case >= GoFirstFlame and <= GoFirstFlame + 3:
                _flames.Add(go.Guid);
                if (!_flameAiRegistered && Instance.FindUpdater<GameObjectMapSystem>() is { } objects)
                {
                    _flameAiRegistered = true;
                    for (uint entry = GoFirstFlame; entry <= GoFirstFlame + 3; entry++)
                    {
                        objects.RegisterAi(entry, new EternalFlameAI(this));
                    }
                }
                break;
        }
    }

    /// <summary>sunken_templeScripts.cpp ProcessEventId_event_antalarion_statue_activation: the goober's data2 is the event id.</summary>
    public override void OnObjectUsed(Player player, GameObject go)
    {
        if (go.Entry is < 148830 or > 148835 || GetData(TypeAtalarion) != EncounterState.NotStarted) return;
        GameObjectMapSystem? objects = Instance.FindUpdater<GameObjectMapSystem>();
        if (ProcessStatueEvent(go.Template.GetData(2)))
        {
            if (Closest(148883) is { } light && !light.IsSpawned) objects?.ForceRespawn(light);
        }
        else
        {
            foreach (uint entry in new uint[] { 177484, 177485, 148837 })
            {
                if (Closest(entry) is { } trap) { objects?.UseByUnit(player, trap); break; }
            }
        }

        GameObject? Closest(uint entry)
            => objects?.GameObjects.Where(g => g.Entry == entry &&
                (g.X - go.X) * (g.X - go.X) + (g.Y - go.Y) * (g.Y - go.Y) + (g.Z - go.Z) * (g.Z - go.Z) <= 25f)
                .OrderBy(g => (g.X - go.X) * (g.X - go.X) + (g.Y - go.Y) * (g.Y - go.Y)).FirstOrDefault();
    }

    public override void OnCreatureCreate(Creature creature)
    {
        uint entry = creature.Template.Entry;
        if (Array.IndexOf(Protectors, entry) >= 0)
        {
            _protectorsRemaining++;
            return;
        }
        if (entry is NpcJammalan or NpcOgom or NpcAtalarion or NpcShadeOfEranikus or NpcShadeOfHakkar or NpcAvatarOfHakkar)
        {
            StoreCreature(creature);
            if (((entry is NpcJammalan or NpcOgom) && GetData(TypeProtectors) == EncounterState.Done)
                || (entry == NpcShadeOfEranikus && GetData(TypeJammalan) == EncounterState.Done))
            {
                creature.UnitFlags &= ~UnitFlags.ImmuneToPlayer;
            }
        }
    }

    public override void OnCreatureDeath(Creature creature)
    {
        switch (creature.Template.Entry)
        {
            case NpcAtalarion: SetData(TypeAtalarion, EncounterState.Done); break;
            case NpcJammalan: SetData(TypeJammalan, EncounterState.Done); break;
            case NpcAvatarOfHakkar: SetData(TypeAvatar, EncounterState.Done); break;
            case NpcSuppressor: _canSummonBloodkeeper = true; break;
            default:
                if (Array.IndexOf(Protectors, creature.Template.Entry) >= 0) SetData(TypeProtectors, EncounterState.Done);
                break;
        }
    }

    public override void OnCreatureEvade(Creature creature)
    {
        if (creature.Template.Entry is NpcBloodkeeper or NpcHakkariMinion or NpcSuppressor or NpcAvatarOfHakkar)
        {
            SetData(TypeAvatar, EncounterState.Fail);
        }
        else if (creature.Template.Entry is NpcJammalan or NpcOgom or NpcShadeOfEranikus)
        {
            creature.UnitFlags &= ~UnitFlags.ImmuneToPlayer;
        }
    }

    private void SetTempleData(uint type, uint data)
    {
        switch (type)
        {
            case TypeAtalarion:
                if (data == EncounterState.Special) SpawnAtalarionIfCan();
                Encounters[type] = data;
                break;
            case TypeProtectors:
                if (data == EncounterState.Done && _protectorsRemaining > 0 && --_protectorsRemaining == 0)
                {
                    Encounters[type] = data;
                    DoUseDoorOrButton(GoJammalanBarrier);
                    if (GetSingleCreatureFromStorage(NpcJammalan) is { } jammalan)
                    {
                        Instance.FindUpdater<CreatureMapSystem>()?.SayText(jammalan, -1109005);
                        jammalan.UnitFlags &= ~UnitFlags.ImmuneToPlayer;
                    }
                    if (GetSingleCreatureFromStorage(NpcOgom) is { } ogom) ogom.UnitFlags &= ~UnitFlags.ImmuneToPlayer;
                }
                break;
            case TypeJammalan:
                Encounters[type] = data;
                if (data == EncounterState.Done && GetSingleCreatureFromStorage(NpcShadeOfEranikus) is { } eranikus)
                    eranikus.UnitFlags &= ~UnitFlags.ImmuneToPlayer;
                break;
            case TypeMalfurion:
                if (data == EncounterState.InProgress) Encounters[type] = data;
                break;
            default:
                return;
        }
        SaveIfDone(data);
    }

    /// <summary>sunken_templeScripts.cpp ProcessEventId_event_antalarion_statue_activation.</summary>
    public bool ProcessStatueEvent(uint eventId)
    {
        if (GetData(TypeAtalarion) != EncounterState.NotStarted || _statuesActivated >= StatueEvents.Length
            || StatueEvents[_statuesActivated] != eventId) return false;
        if (++_statuesActivated == StatueEvents.Length) SetData(TypeAtalarion, EncounterState.Special);
        return true;
    }

    /// <summary>sunken_templeScripts.cpp ProcessEventId_event_avatar_of_hakkar.</summary>
    public void BeginAvatarEvent()
    {
        if (GetData(TypeAvatar) is EncounterState.NotStarted or EncounterState.Fail)
            SetData(TypeAvatar, EncounterState.InProgress);
    }

    private void SpawnAtalarionIfCan()
    {
        if (GetSingleCreatureFromStorage(NpcAtalarion) is not null || Instance.Players.FirstOrDefault() is null) return;
        Instance.FindUpdater<CreatureMapSystem>()?.SummonForInstance(NpcAtalarion, -466.513f, 95.1982f, -189.646f, 0.0349f);
        RespawnObject(GetSingleGameObjectFromStorage(GoIdolOfHakkar));
        foreach (ObjectGuid guid in _bigLights) RespawnObject(Instance.FindUpdater<GameObjectMapSystem>()?.Find(guid));
    }

    private bool BeginTempleAvatar()
    {
        _suppressorMs = 0;
        SetFlamesInteractable();
        _avatarWaveMs = 3000;
        _firstWave = true;
        if (Instance.Players.FirstOrDefault() is null) return false;
        Instance.FindUpdater<CreatureMapSystem>()?.SummonForInstance(NpcShadeOfHakkar, -466.8673f, 272.31204f, -90.7441f, 3.5255f);
        foreach (ObjectGuid guid in _evilCircles) RespawnObject(Instance.FindUpdater<GameObjectMapSystem>()?.Find(guid));
        return true;
    }

    private void FailTempleAvatar()
    {
        if (GetSingleCreatureFromStorage(NpcShadeOfHakkar) is { } shade)
            Instance.FindUpdater<CreatureMapSystem>()?.ForcedDespawn(shade, 0);
        SetFlamesInteractable();
        _flamesDoused = 0;
        foreach (ObjectGuid guid in _evilCircles)
            if (Instance.FindUpdater<GameObjectMapSystem>()?.Find(guid) is { } circle)
                Instance.FindUpdater<GameObjectMapSystem>()?.DespawnForRespawn(circle);
    }

    private void DouseTempleFlame()
    {
        if (GetSingleCreatureFromStorage(NpcShadeOfHakkar) is not { } shade) return;
        CreatureMapSystem? creatures = Instance.FindUpdater<CreatureMapSystem>();
        if (creatures?.HasAura(shade, 12623) == true && _suppressionMs == 0)
        {
            _suppressionMs = 20_000;
            return;
        }
        _flamesDoused++;
        if (_flamesDoused <= 4) creatures?.SayText(shade, -1109005 - _flamesDoused);
        if (_flamesDoused == 4)
        {
            creatures?.CastSpell(shade, 12639, shade, triggered: true);
            creatures?.UpdateEntry(shade, NpcAvatarOfHakkar); // sunken_templeScripts.cpp SummonHakkar::OnEffectExecute
            creatures?.SayText(shade, -1109010);
            _avatarWaveMs = _suppressorMs = 0;
        }
        else if (_flamesDoused < 4) _suppressorMs = (uint)(creatures?.RandomInt(15_000, 45_000) ?? 15_000);
    }

    private void SetFlamesInteractable()
    {
        foreach (ObjectGuid guid in _flames)
            if (Instance.FindUpdater<GameObjectMapSystem>()?.Find(guid) is { } flame)
                flame.Flags &= ~GameObjectFlags.NoInteract;
    }

    private void RespawnObject(GameObject? go)
    {
        if (go is { IsSpawned: false }) Instance.FindUpdater<GameObjectMapSystem>()?.ForceRespawn(go);
    }

    public override void Update(uint diffMs)
    {
        if (Encounters[TypeAvatar] != EncounterState.InProgress) return;
        CreatureMapSystem? creatures = Instance.FindUpdater<CreatureMapSystem>();
        Creature? shade = GetSingleCreatureFromStorage(NpcShadeOfHakkar);
        if (_suppressionMs > 0)
        {
            if (_suppressionMs < diffMs)
            {
                _suppressionMs = 0;
                if (shade is not null && creatures?.HasAura(shade, 12623) == true)
                {
                    creatures.SayText(shade, -1109015);
                    SetData(TypeAvatar, EncounterState.Fail);
                    return;
                }
            }
            else _suppressionMs -= diffMs;
        }
        if (_avatarWaveMs > 0)
        {
            if (_avatarWaveMs <= diffMs)
            {
                if (shade is null || _evilCircles.Count == 0) return;
                if (_firstWave)
                {
                    foreach (ObjectGuid guid in _evilCircles)
                        if (Instance.FindUpdater<GameObjectMapSystem>()?.Find(guid) is { } circle)
                            creatures?.SummonCorpseDespawn(shade, NpcHakkariMinion, circle.X, circle.Y, circle.Z, 0);
                    SummonAtRandomCircle(shade, NpcBloodkeeper);
                    _canSummonBloodkeeper = _firstWave = false;
                    _avatarWaveMs = 50_000;
                }
                else
                {
                    int roll = creatures?.RandomInt(0, 99) ?? 0;
                    int count = roll < 75 ? 1 : roll < 95 ? 2 : 3;
                    if (_canSummonBloodkeeper && creatures?.RandomInt(0, 99) < 30)
                    {
                        SummonAtRandomCircle(shade, NpcBloodkeeper);
                        _canSummonBloodkeeper = false;
                        count--;
                    }
                    for (int i = 0; i < count; i++) SummonAtRandomCircle(shade, NpcHakkariMinion);
                    _avatarWaveMs = (uint)(creatures?.RandomInt(3000, 15000) ?? 3000);
                }
            }
            else _avatarWaveMs -= diffMs;
        }
        if (_suppressorMs > 0)
        {
            if (_suppressorMs <= diffMs)
            {
                if (shade is null) return;
                int door = creatures?.RandomInt(0, 1) ?? 0;
                var at = SuppressorDoors[door];
                if (creatures?.SummonCorpseDespawn(shade, NpcSuppressor, at.X, at.Y, at.Z, 0) is { } suppressor)
                {
                    creatures.ChangeMovement(suppressor, 2, (uint)door, 0);
                    creatures.SayText(suppressor, -1109011 - creatures.RandomInt(0, 3));
                }
                _suppressorMs = 0;
            }
            else _suppressorMs -= diffMs;
        }
    }

    private void SummonAtRandomCircle(Creature shade, uint entry)
    {
        CreatureMapSystem? creatures = Instance.FindUpdater<CreatureMapSystem>();
        int index = creatures?.RandomInt(0, _evilCircles.Count - 1) ?? 0;
        if (Instance.FindUpdater<GameObjectMapSystem>()?.Find(_evilCircles[index]) is { } circle)
            creatures?.SummonCorpseDespawn(shade, entry, circle.X, circle.Y, circle.Z, 0);
    }

    private sealed class EternalFlameAI(SunkenTempleInstance instance) : IGameObjectAi
    {
        public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target) => false;
        public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs) { }
        public bool OnUse(GameObjectMapSystem objects, GameObject go, Unit user)
        {
            if (instance.GetData(TypeAvatar) != EncounterState.InProgress) return false;
            instance.SetData(TypeAvatar, EncounterState.Special);
            go.Flags |= GameObjectFlags.NoInteract;
            return true;
        }
    }
}
