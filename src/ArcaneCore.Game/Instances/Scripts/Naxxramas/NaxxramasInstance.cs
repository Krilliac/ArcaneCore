using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Instances.Scripts.Naxxramas;

/// <summary>
/// Map 533 encounter state and the Arachnid/Plague wing doors. Behaviour and IDs:
/// vmangos eastern_kingdoms/eastern_plaguelands/naxxramas/instance_naxxramas.cpp
/// Initialize, OnObjectCreate, SetData, UpdateTeleporters and SetTeleporterState;
/// naxxramas.h NAXX_ENCOUNTERS_TYPES/NaxxGOs. Reimplemented, no GPL source copied.
/// The Military, Construct and Frostwyrm Lair slots (6-14) live in NaxxramasInstance.PartTwo.cs; this file calls its
/// hooks from SetData, OnObjectCreate, OnCreatureDeath and Update.
/// </summary>
[InstanceScript(533)]
public sealed partial class NaxxramasInstance(Map map) : ScriptedInstance(map, 15)
{
    private readonly HashSet<ObjectGuid> _alivePlayers = [];
    private readonly HashSet<ObjectGuid>[] _heiganTraps = [[], [], [], []];
    public const uint AnubRekhan = 0, Faerlina = 1, Maexxna = 2, Noth = 3, Heigan = 4, Loatheb = 5;

    // (gameobject entry, encounter slot, entrance door): gates stay open after the boss dies;
    // an entrance door closes only while the encounter is in progress. Only the Arachnid and
    // Plague doors are driven here: the other wings' gates (vmangos GO_MILI_*, GO_CONS_*,
    // GO_KELTHUZAD_*) keep their database state until their encounters are scripted, so this
    // part cannot shut a gate whose boss nothing would ever mark done. Heigan's exit door
    // 181203 is left alone, as in both references ("not used").
    private static readonly (uint Entry, uint Boss, bool Entrance)[] Doors =
    [
        (181126, AnubRekhan, true), (181195, AnubRekhan, false),
        (181235, Faerlina, true), (181167, Faerlina, false), (181209, Faerlina, false),
        (181197, Maexxna, true), (181200, Noth, true), (181201, Noth, false),
        (181202, Heigan, true), (181496, Heigan, false), (181241, Heigan, false),
    ];

    private static readonly (uint Entry, uint Boss)[] Portals =
    [
        (181575, Maexxna), (181577, Loatheb),
        (181576, 12), (181578, 8),
        (181212, Maexxna), (181233, Maexxna),
        (181211, Loatheb), (181231, Loatheb),
        (181213, 12), (181232, 12),
        (181210, 8), (181230, 8),
    ];

    public bool WingsCleared => GetData(Maexxna) == EncounterState.Done
        && GetData(Loatheb) == EncounterState.Done
        && GetData(8) == EncounterState.Done
        && GetData(12) == EncounterState.Done;

    public override uint GetData(uint type) => type < Encounters.Length ? Encounters[type] : 0;
    // mangos-classic instance_naxxramas::IsEncounterInProgress: "Some Encounters use SPECIAL while in progress" (Gothik).
    public override bool IsEncounterInProgress => Encounters.Contains(EncounterState.InProgress)
        || GetData(Gothik) == EncounterState.Special;

    public override void SetData(uint type, uint data)
    {
        if (type >= Encounters.Length || Encounters[type] == data) return;
        Encounters[type] = data;
        if (type == AnubRekhan)
        {
            _alivePlayers.Clear();
            if (data == EncounterState.InProgress)
                foreach (Player player in Instance.Players.Where(p => p.IsAlive)) _alivePlayers.Add(player.Guid);
        }
        foreach (var door in Doors.Where(d => d.Boss == type ||
                     (d.Entry == 181241 && type == Loatheb) ||
                     (d.Entry == 181202 && type == Noth)))
            Refresh(door.Entry);
        if (type is Maexxna or Loatheb or 8 or 12)
        {
            foreach (var portal in Portals.Where(p => p.Boss == type)) Refresh(portal.Entry);
            Refresh(181229);
        }
        OnPartTwoStateChanged(type, data);
        SaveIfDone(data);
    }

    public override void OnObjectCreate(GameObject go)
    {
        OnPartTwoObjectCreate(go);
        int section = HeiganTrapSection(go);
        if (section >= 0) _heiganTraps[section].Add(go.Guid);
        if (go.Entry == 181229 || Doors.Any(d => d.Entry == go.Entry) || Portals.Any(p => p.Entry == go.Entry))
        {
            StoreGameObject(go);
            Apply(go);
        }
    }

    private static int HeiganTrapSection(GameObject go)
    {
        // vmangos instance_naxxramas::OnObjectCreate groups the 1.12 floor-trap entries,
        // with a few duplicate/exception spawn GUIDs. Those GUIDs are vmangos' own spawn numbering
        // and do not occur in ClassicDB z2815 (map 533 GUIDs are 533xxxx there); the entry ranges carry it.
        if (go.Type != GameObjectType.Trap) return -1;
        uint entry = go.Entry, guid = go.Spawn?.Guid ?? 0;
        if (entry is >= 181517 and <= 181524 or 181678) return 0;
        if (entry is >= 181510 and <= 181516 or >= 181525 and <= 181531 or 181533 or 181676) return 1;
        if (entry is >= 181534 and <= 181544 or 181532 or 181677 || guid is 533185 or 533196 or 533198) return 2;
        if (entry is >= 181545 and <= 181552 && guid is not (533119 or 533123)
            || guid is 533181 or 533182 or 533183 or 533184 or 533187 or 533188
                or 533189 or 533190 or 533191 or 533192 or 533193 or 533194
                or 533195 or 533197 or 533199 or 533200) return 3;
        return -1;
    }

    internal int ActivateHeiganTraps(int safeSection, Unit trigger)
    {
        if (Instance.FindUpdater<GameObjectMapSystem>() is not { } objects) return 0;
        int activated = 0;
        for (int section = 0; section < _heiganTraps.Length; section++)
        {
            if (section == safeSection) continue;
            foreach (ObjectGuid guid in _heiganTraps[section])
                if (objects.Find(guid) is { } trap && objects.UseByUnit(trigger, trap) == GameObjectUseResult.Ok)
                    activated++;
        }
        return activated;
    }

    /// <summary>The hub's Frostwyrm Lair trigger (vmangos naxxramas.h AREATRIGGER_HUB_TO_FROSTWYRM).</summary>
    public const uint FrostwyrmTrigger = 4156;

    /// <summary>
    /// mangos-classic naxxramas.cpp instance_naxxramas::DoHandleAreaTrigger(AREATRIGGER_FROSTWYRM_TELE): "Area trigger handles teleport
    /// in DB", the script only stops it until Maexxna, Loatheb, the Four Horsemen and Thaddius are done. ClassicDB z2815 has that row
    /// (areatrigger_teleport 4156 "Naxxramas (Entrance)", map 533 at 3498.28,-5349.9,144.968), and TeleportHandlers runs it after the
    /// trigger listeners whatever they did, so the requirement must be a veto, not a scripted teleport of its own (vmangos
    /// onNaxxramasAreaTrigger teleports itself, for a database without the row). Every non-GM is held back, dead or alive (vmangos
    /// ports only the living).
    /// </summary>
    public override bool BlocksAreaTriggerTeleport(Player player, uint triggerId)
        => triggerId == FrostwyrmTrigger && !WingsCleared;

    public override void OnCreatureDeath(Creature creature)
    {
        OnPartTwoCreatureDeath(creature);
        // vmangos boss_maexxna.cpp mob_webwrapAI::JustDied;
        // boss_anubrekhanAI::ExplodeOneDeadCryptGuard.
        if (creature.Entry == 16486)
        {
            Player? wrapped = Instance.Players.Where(p => p.IsAlive &&
                    (creature.System?.HasAura(p, 28622) ?? false))
                .OrderBy(p => (p.X - creature.X) * (p.X - creature.X) +
                              (p.Y - creature.Y) * (p.Y - creature.Y)).FirstOrDefault();
            if (wrapped is not null) creature.System?.RemoveAuras(wrapped, 28622);
        }
        else if (creature.Entry == 16573 && GetData(AnubRekhan) == EncounterState.InProgress
                 && creature.System is { } system)
        {
            Creature? anub = system.Creatures.FirstOrDefault(c => c.Entry == 15956 && c.IsAlive);
            (anub?.AI as NaxxramasBossAI)?.CryptGuardDied(creature);
        }
    }

    internal void ExplodeCryptGuard(Creature corpse)
    {
        if (GetData(AnubRekhan) == EncounterState.InProgress && corpse.System is { } system)
            SpawnScarabs(system, corpse.X, corpse.Y, corpse.Z, 10);
    }

    public override void OnPlayerEnter(Player player)
    {
        if (player.IsAlive) _alivePlayers.Add(player.Guid);
    }

    public override void OnPlayerLeave(Player player) => _alivePlayers.Remove(player.Guid);

    public override void Update(uint diffMs)
    {
        UpdatePartTwo(diffMs);
        UpdateLivingPoison(diffMs);
        if (GetData(AnubRekhan) != EncounterState.InProgress) return;
        foreach (Player player in Instance.Players)
        {
            if (player.IsAlive) _alivePlayers.Add(player.Guid);
            else if (_alivePlayers.Remove(player.Guid) && Instance.FindUpdater<CreatureMapSystem>() is { } system)
                SpawnScarabs(system, player.X, player.Y, player.Z, 5);
        }
    }

    private void SpawnScarabs(CreatureMapSystem system, float x, float y, float z, int count)
    {
        // vmangos instance_naxxramas::OnPlayerDeath and boss_anubrekhanAI::ExplodeOneDeadCryptGuard.
        // The map tick detects player death from any source, including environmental damage.
        if (system.Content.FindTemplate(16698) is not { } template) return;
        Creature? anub = system.Creatures.FirstOrDefault(c => c.Entry == 15956 && c.IsAlive);
        Player[] targets = Instance.Players.Where(p => p.IsAlive).ToArray();
        for (int i = 0; i < count; i++)
        {
            Creature scarab = system.SpawnTemporary(template, x, y, z, 0, anub);
            if (targets.Length == 0) continue;
            Player target = targets[system.RandomInt(0, targets.Length - 1)];
            scarab.AI?.AttackStart(target);
            scarab.Combat.Threat.AddThreat(target, 5000);
        }
    }

    private void Refresh(uint entry)
    {
        if (GetSingleGameObjectFromStorage(entry) is { } go) Apply(go);
    }

    private void Apply(GameObject go)
    {
        if (go.Entry == 181229)
        {
            go.State = WingsCleared ? GameObjectState.Active : GameObjectState.Ready;
            return;
        }
        foreach (var door in Doors)
        {
            if (door.Entry != go.Entry) continue;
            bool opened = door.Entrance ? GetData(door.Boss) != EncounterState.InProgress
                : GetData(door.Boss) == EncounterState.Done;
            if (go.Entry == 181126) opened = GetData(AnubRekhan) is EncounterState.Fail or EncounterState.Done;
            if (go.Entry == 181202) opened = GetData(Noth) == EncounterState.Done
                && GetData(Heigan) != EncounterState.InProgress;
            // Loatheb's entrance must remain open after Heigan until Loatheb is pulled.
            if (go.Entry == 181241) opened = GetData(Heigan) == EncounterState.Done
                && GetData(Loatheb) != EncounterState.InProgress;
            go.State = opened ? GameObjectState.Active : GameObjectState.Ready;
            if (go.Entry is 181195 or 181167)
            {
                if (GetData(door.Boss) == EncounterState.Done) go.Flags &= ~(GameObjectFlags.Locked | GameObjectFlags.NoInteract);
                else go.Flags |= GameObjectFlags.Locked;
            }
            return;
        }
        foreach (var portal in Portals)
        {
            if (portal.Entry != go.Entry) continue;
            bool active = GetData(portal.Boss) == EncounterState.Done;
            go.State = active ? GameObjectState.Active : GameObjectState.Ready;
            if (portal.Entry is 181575 or 181576 or 181577 or 181578)
            {
                if (active) go.Flags &= ~GameObjectFlags.NoInteract;
                else go.Flags |= GameObjectFlags.NoInteract;
            }
            return;
        }
    }
}
