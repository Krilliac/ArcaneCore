using ArcaneCore.Game.Entities;
using ArcaneCore.Game.WorldState.States;
using Xunit;

namespace ArcaneCore.Game.Tests.WorldState;

/// <summary>SMSG_INIT_WORLD_STATES contents (vmangos Player.cpp:8156-8213, Server/Packets/Misc.cpp:1007-1060).</summary>
public sealed class WorldStateTests
{
    private sealed class Provider(int order, params (uint State, int Value)[] states) : IWorldStateProvider
    {
        public int Order => order;

        public List<(Player Player, uint Zone)> Calls { get; } = [];

        public void Fill(Player player, uint zoneId, List<WorldStatePair> list)
        {
            Calls.Add((player, zoneId));
            list.AddRange(states.Select(s => new WorldStatePair(s.State, s.Value)));
        }
    }

    private static Player NewPlayer() => TestWorld.CreatePlayer(1, 0, 0, new FakeSession());

    [Fact]
    public void WithoutProvidersOrDefaults_ThePayloadIsMapZoneAndAZeroCount()
    {
        byte[] payload = WorldStatePackets.BuildInit(1, 12, new WorldStateRegistry().Build(NewPlayer(), 12));

        Assert.Equal([1, 0, 0, 0, 12, 0, 0, 0, 0, 0], payload);
    }

    [Fact]
    public void DefaultsComeFirst_ThenProvidersByOrder_AndValuesAreSigned()
    {
        var registry = new WorldStateRegistry { Defaults = [new WorldStatePair(0x0A, 7)] };
        var late = new Provider(5, (0x0515, 1));
        var early = new Provider(-5, (0x03B6, -1));
        registry.Add(late);
        registry.Add(early);
        registry.Add(late); // already registered: ignored

        List<WorldStatePair> states = registry.Build(NewPlayer(), 3277);

        Assert.Equal([new WorldStatePair(0x0A, 7), new WorldStatePair(0x03B6, -1), new WorldStatePair(0x0515, 1)], states);
        byte[] payload = WorldStatePackets.BuildInit(489, 3277, states);
        Assert.Equal(10 + (3 * 8), payload.Length);
        Assert.Equal(3, BitConverter.ToUInt16(payload, 8));
        Assert.Equal(0x0Au, BitConverter.ToUInt32(payload, 10));
        Assert.Equal(-1, BitConverter.ToInt32(payload, 18 + 4)); // the second pair's value: an i32
        Assert.Equal(3277u, early.Calls.Single().Zone);
    }

    [Fact]
    public void RemovingAProvider_StopsItsStates()
    {
        var registry = new WorldStateRegistry();
        var provider = new Provider(0, (1, 1));
        registry.Add(provider);
        Assert.Single(registry.Build(NewPlayer(), 1));
        Assert.True(registry.Remove(provider));
        Assert.Empty(registry.Build(NewPlayer(), 1));
        Assert.False(registry.Remove(provider));
    }

    [Fact]
    public void UpdateWorldState_IsStateThenValue()
        => Assert.Equal([0x15, 5, 0, 0, 1, 0, 0, 0], WorldStatePackets.BuildUpdate(0x0515, 1));

    [Fact]
    public void DefaultsFile_IsParsedStrictly()
    {
        Assert.Equal([new WorldStatePair(2264, 0), new WorldStatePair(3, -4)], WorldStateDefaults.Parse("[[2264, 0], [3, -4]]"));
        Assert.Empty(WorldStateDefaults.Parse("[]"));

        Assert.Throws<InvalidDataException>(() => WorldStateDefaults.Parse("[[1, 2], [1, 3]]"));        // duplicate state
        Assert.Throws<InvalidDataException>(() => WorldStateDefaults.Parse("[[1, \"x\"]]"));            // not a number
        Assert.Throws<InvalidDataException>(() => WorldStateDefaults.Parse("[[1]]"));                    // wrong arity
        Assert.Throws<InvalidDataException>(() => WorldStateDefaults.Parse("[[-1, 0]]"));                // state is a u32
        Assert.Throws<InvalidDataException>(() => WorldStateDefaults.Parse("[[1, 3000000000]]"));        // value is an i32
        Assert.Throws<InvalidDataException>(() => WorldStateDefaults.Parse("{\"a\": 1}"));               // not an array
        Assert.Throws<InvalidDataException>(() => WorldStateDefaults.Parse("[[1, 2"));                   // not JSON
    }

    [Fact]
    public void DefaultsFile_LoadsFromDisk()
    {
        string path = Path.Combine(Path.GetTempPath(), $"worldstates-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "[[10, 1]]");
        try
        {
            Assert.Equal([new WorldStatePair(10, 1)], WorldStateDefaults.Load(path));
        }
        finally
        {
            File.Delete(path);
        }

        Assert.ThrowsAny<IOException>(() => WorldStateDefaults.Load(path)); // gone: a missing file fails startup
    }
}
