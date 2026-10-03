using System.Text.Json;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.WorldState.States;

/// <summary>One world state: the client's state id and its value (vmangos <c>std::pair&lt;uint32, int32&gt;</c> in <c>InitWorldStates::states</c>).</summary>
public readonly record struct WorldStatePair(uint State, int Value);

/// <summary>
/// Adds the world states a feature owns (battlegrounds, outdoor PvP, war effort, invasions, zone
/// scripts) to the SMSG_INIT_WORLD_STATES of a zone entry (vmangos <c>Player::SendInitWorldStates</c>,
/// Player.cpp:8156-8213, which asks each of those systems in turn). Register with
/// <see cref="WorldStateRegistry.Add"/>. World thread.
/// </summary>
public interface IWorldStateProvider
{
    /// <summary>Lower runs first (ties: registration order).</summary>
    int Order => 0;

    /// <summary>Append this provider's states for <paramref name="player"/> entering <paramref name="zoneId"/>.</summary>
    void Fill(Player player, uint zoneId, List<WorldStatePair> states);
}

/// <summary>
/// The world states of a zone entry: the operator's default list, then the providers (vmangos adds
/// <c>def_world_states</c> before the zone script, battleground and war-effort states,
/// Player.cpp:8194-8205). The default list is data the operator supplies
/// (<see cref="WorldStateDefaults"/>); nothing is compiled in.
/// </summary>
public sealed class WorldStateRegistry
{
    private readonly List<IWorldStateProvider> _providers = [];
    private volatile IReadOnlyList<WorldStatePair> _defaults = [];

    /// <summary>The default pairs sent in every zone entry (empty unless the operator configured a file).</summary>
    public IReadOnlyList<WorldStatePair> Defaults
    {
        get => _defaults;
        set => _defaults = value ?? throw new ArgumentNullException(nameof(value));
    }

    public IReadOnlyList<IWorldStateProvider> Providers => _providers;

    public void Add(IWorldStateProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (_providers.Contains(provider))
        {
            return;
        }

        _providers.Add(provider);
        IWorldStateProvider[] ordered = _providers.Select((p, i) => (p, i)).OrderBy(t => t.p.Order).ThenBy(t => t.i).Select(t => t.p).ToArray();
        _providers.Clear();
        _providers.AddRange(ordered);
    }

    public bool Remove(IWorldStateProvider provider) => _providers.Remove(provider);

    /// <summary>The states for a zone entry: defaults first, then each provider.</summary>
    public List<WorldStatePair> Build(Player player, uint zoneId)
    {
        ArgumentNullException.ThrowIfNull(player);
        var states = new List<WorldStatePair>(_defaults);
        foreach (IWorldStateProvider provider in _providers.ToArray())
        {
            provider.Fill(player, zoneId, states);
        }

        return states;
    }
}

/// <summary>Loads the operator's default world-state list: a JSON array of <c>[state, value]</c> pairs.</summary>
public static class WorldStateDefaults
{
    /// <summary>Parse strictly: numbers only, no duplicate state ids, state a u32 and value an i32.</summary>
    public static IReadOnlyList<WorldStatePair> Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(json).RootElement;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("the world-state defaults are not valid JSON", ex);
        }

        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("the world-state defaults must be a JSON array of [state, value] pairs");
        }

        var pairs = new List<WorldStatePair>();
        var seen = new HashSet<uint>();
        int index = 0;
        foreach (JsonElement item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() != 2
                || item[0].ValueKind != JsonValueKind.Number || item[1].ValueKind != JsonValueKind.Number
                || !item[0].TryGetUInt32(out uint state) || !item[1].TryGetInt32(out int value))
            {
                throw new InvalidDataException($"world-state default {index} must be [u32 state, i32 value]");
            }

            if (!seen.Add(state))
            {
                throw new InvalidDataException($"world-state default {index}: state {state} is listed twice");
            }

            pairs.Add(new WorldStatePair(state, value));
            index++;
        }

        return pairs;
    }

    public static IReadOnlyList<WorldStatePair> Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadAllText(path));
    }
}

/// <summary>World-state packet bodies for build 5875.</summary>
public static class WorldStatePackets
{
    /// <summary>
    /// SMSG_INIT_WORLD_STATES: u32 map, u32 zone, u16 count, then (u32 state, i32 value) pairs (vmangos
    /// Server/Packets/Misc.cpp:1043-1060, builds above 1.10.2; wow_messages smsg_init_world_states.wowm 1.12).
    /// </summary>
    public static byte[] BuildInit(uint mapId, uint zoneId, IReadOnlyList<WorldStatePair> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        var writer = new PacketWriter(10 + (states.Count * 8));
        writer.WriteUInt32(mapId);
        writer.WriteUInt32(zoneId);
        writer.WriteUInt16((ushort)states.Count);
        foreach (WorldStatePair pair in states)
        {
            writer.WriteUInt32(pair.State);
            writer.WriteInt32(pair.Value);
        }

        return writer.ToArray();
    }

    /// <summary>SMSG_UPDATE_WORLD_STATE: u32 state, u32 value (vmangos Misc.cpp:1007-1024).</summary>
    public static byte[] BuildUpdate(uint state, uint value)
    {
        var writer = new PacketWriter(8);
        writer.WriteUInt32(state);
        writer.WriteUInt32(value);
        return writer.ToArray();
    }
}
