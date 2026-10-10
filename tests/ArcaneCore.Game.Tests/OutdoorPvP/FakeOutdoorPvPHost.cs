using ArcaneCore.Game.Entities;
using ArcaneCore.Game.OutdoorPvP;

namespace ArcaneCore.Game.Tests.OutdoorPvP;

/// <summary>A recording <see cref="IOutdoorPvPHost"/>: players stand at points the test sets; everything else is logged.</summary>
internal sealed class FakeOutdoorPvPHost : IOutdoorPvPHost
{
    private uint _next = 1;

    public readonly Dictionary<ObjectGuid, (Team Team, uint MapId, float X, float Y, float Z, bool Active)> Players = [];
    public readonly Dictionary<ObjectGuid, HashSet<uint>> Auras = [];
    public readonly Dictionary<(ObjectGuid, uint), uint> WorldStates = [];
    public readonly List<(ObjectGuid Player, uint Spell)> Casts = [];
    public readonly Dictionary<ObjectGuid, OutdoorPvPSpawn> Objects = [];
    public readonly Dictionary<ObjectGuid, (OutdoorPvPSpawn Spawn, uint Faction, uint Aura)> Creatures = [];
    public readonly Dictionary<ObjectGuid, uint> ArtKits = [];
    public readonly List<uint> Sounds = [];
    public readonly List<(ObjectGuid Creature, uint Spell)> CreatureCasts = [];
    public readonly List<uint> DefenseMessages = [];
    public readonly List<(uint Zone, string Text)> ZoneTexts = [];
    public readonly List<(uint Entry, uint Text)> Says = [];
    public readonly List<(ObjectGuid Player, uint Entry)> Credits = [];
    public CapturePointTemplate? Template { get; set; }

    public ObjectGuid AddPlayer(uint id, Team team, OutdoorPvPSpawn at, bool active = true)
    {
        var guid = ObjectGuid.Player(id);
        Players[guid] = (team, at.MapId, at.X, at.Y, at.Z, active);
        Auras[guid] = [];
        return guid;
    }

    public void Move(ObjectGuid guid, float x, float y, float z) => Players[guid] = Players[guid] with { X = x, Y = y, Z = z };

    public OutdoorPvPPlayer P(ObjectGuid guid) => new(guid, Players[guid].Team);

    public uint State(ObjectGuid player, uint state) => WorldStates.GetValueOrDefault((player, state));

    public void SendWorldState(ObjectGuid player, uint state, uint value) => WorldStates[(player, state)] = value;

    public void CastOnSelf(ObjectGuid player, uint spellId)
    {
        Casts.Add((player, spellId));
        if (Auras.TryGetValue(player, out HashSet<uint>? set))
        {
            set.Add(spellId);
        }
    }

    public void RemoveAura(ObjectGuid player, uint spellId) => Auras.GetValueOrDefault(player)?.Remove(spellId);

    public bool HasAura(ObjectGuid player, uint spellId) => Auras.GetValueOrDefault(player)?.Contains(spellId) == true;

    public IEnumerable<OutdoorPvPPlayer> ActivePlayersNear(uint mapId, float x, float y, float z, float radius)
        => Players.Where(p => p.Value.Active && p.Value.MapId == mapId
                && MathF.Sqrt(((p.Value.X - x) * (p.Value.X - x)) + ((p.Value.Y - y) * (p.Value.Y - y)) + ((p.Value.Z - z) * (p.Value.Z - z))) <= radius)
            .Select(p => new OutdoorPvPPlayer(p.Key, p.Value.Team)).ToList();

    public CapturePointTemplate? CapturePoint(uint entry) => Template;

    public HashSet<ObjectGuid> SpawnedByDefault { get; } = [];

    public Dictionary<ObjectGuid, ObjectGuid> Groups { get; } = [];

    public List<(ObjectGuid Creature, uint PathId)> SpecialPaths { get; } = [];

    public void JoinCreatureGroup(ObjectGuid member, ObjectGuid leader) => Groups[member] = leader;

    public bool StartSpecialPath(ObjectGuid creature, uint pathId)
    {
        SpecialPaths.Add((creature, pathId));
        return true;
    }

    public ObjectGuid? SummonObject(OutdoorPvPSpawn spawn, bool spawnedByDefault = false)
    {
        var guid = ObjectGuid.WithEntry(HighGuid.GameObject, spawn.Entry, _next++);
        Objects[guid] = spawn;
        if (spawnedByDefault) SpawnedByDefault.Add(guid);
        return guid;
    }

    public void RemoveObject(ObjectGuid guid) => Objects.Remove(guid);

    public void SetBannerArt(ObjectGuid guid, uint artKit, uint animation) => ArtKits[guid] = artKit;

    public void PlayObjectSound(ObjectGuid guid, uint soundId) => Sounds.Add(soundId);

    public ObjectGuid? SummonCreature(OutdoorPvPSpawn spawn, uint faction = 0, uint aura = 0)
    {
        var guid = ObjectGuid.WithEntry(HighGuid.Unit, spawn.Entry, _next++);
        Creatures[guid] = (spawn, faction, aura);
        return guid;
    }

    public void RemoveCreature(ObjectGuid guid) => Creatures.Remove(guid);

    public void CreatureCastOnSelf(ObjectGuid creature, uint spellId) => CreatureCasts.Add((creature, spellId));

    public void SendDefenseMessage(uint mapId, uint zoneId, uint broadcastTextId) => DefenseMessages.Add(broadcastTextId);

    public void SendZoneText(uint zoneId, string text) => ZoneTexts.Add((zoneId, text));

    public void NearestCreatureSays(ObjectGuid player, uint entry, uint broadcastTextId) => Says.Add((entry, broadcastTextId));

    public void KilledMonsterCredit(ObjectGuid player, uint creatureEntry) => Credits.Add((player, creatureEntry));

    public float? DistanceTo(ObjectGuid player, uint mapId, float x, float y, float z)
        => Players.TryGetValue(player, out var p) && p.MapId == mapId
            ? MathF.Sqrt(((p.X - x) * (p.X - x)) + ((p.Y - y) * (p.Y - y)) + ((p.Z - z) * (p.Z - z)))
            : null;

    public int CreaturesOf(uint entry) => Creatures.Values.Count(c => c.Spawn.Entry == entry);

    public int ObjectsOf(uint entry) => Objects.Values.Count(o => o.Entry == entry);
}
