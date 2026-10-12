using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Updates;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.GridTerrain;

/// <summary>
/// <see cref="IPlayerSession.WantsUpdateObjects"/> false (a lean playerbot, World:Playerbots:ClientFidelity): the map builds no
/// SMSG_UPDATE_OBJECT block for that viewer, while the visible set and the observer index stay exactly what a real client's are.
/// </summary>
public sealed class LeanUpdateObjectTests
{
    private sealed class CountingSession(int accountId, bool wants) : IPlayerSession
    {
        public int UpdatePackets;
        public int AccountId { get; } = accountId;
        public AccountSecurity Security => AccountSecurity.Player;
        public bool WantsUpdateObjects { get; set; } = wants;
        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload)
        {
            if (opcode is WorldOpcode.SmsgUpdateObject or WorldOpcode.SmsgCompressedUpdateObject) UpdatePackets++;
        }
        public void ProcessWorldPackets(Player player) { }
        public void Kick() { }
        public void OnLoggedOut() { }
    }

    /// <summary>A viewer in a world with two creatures; one walks in and out of range, the other changes its health.</summary>
    private static (int Packets, int ObserversOfWalker, List<string> Trail) Run(bool wants)
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var session = new CountingSession(1, wants);
        Player viewer = TestWorld.CreatePlayer(1, 0, 0, session);
        world.AddPlayer(viewer);
        session.UpdatePackets = 0; // the login self block is built for everyone
        var still = new TestUnit(1, 20, 0);
        var walker = new TestUnit(2, 300, 0);
        map.AddObject(still);
        map.AddObject(walker);
        List<string> visibleTrail = [];
        void Snapshot()
            => visibleTrail.Add(string.Join(',', viewer.VisibleObjects.Select(g => g.Value).OrderBy(v => v)));
        map.Update(50);
        Snapshot();
        walker.SetPosition(40, 0, 0, 0);
        map.Update(50);
        Snapshot();
        still.Health = 77; // a values update for the viewer
        map.Update(50);
        Snapshot();
        walker.SetPosition(300, 0, 0, 0);
        map.Update(50);
        Snapshot();
        walker.SetPosition(30, 0, 0, 0);
        map.Update(50);
        Snapshot();
        return (session.UpdatePackets, map.ObserversOf(walker).Count, visibleTrail);
    }

    [Fact]
    public void ALeanViewerGetsNoUpdateObjectBlocksButTheSameVisibleSetAsARealClient()
    {
        long skippedBefore = BotLeanStats.UpdateBlocks;
        var real = Run(wants: true);
        long skippedByReal = BotLeanStats.UpdateBlocks - skippedBefore;
        var lean = Run(wants: false);
        long skippedByLean = BotLeanStats.UpdateBlocks - skippedBefore - skippedByReal;

        Assert.True(real.Packets >= 3, "a real client gets create, values and out-of-range packets: " + real.Packets);
        Assert.Equal(0, lean.Packets);
        Assert.Equal(0, skippedByReal);
        Assert.True(skippedByLean >= 3, "create, values and out-of-range blocks are counted: " + skippedByLean);
        Assert.Equal(real.Trail, lean.Trail);
        Assert.Contains(real.Trail, trail => trail.Length > 0);
        Assert.Equal(real.ObserversOfWalker, lean.ObserversOfWalker);
        Assert.Equal(1, lean.ObserversOfWalker);
    }

    [Fact]
    public void ALeanViewerThatStartsWantingUpdatesGetsThemAgain()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var session = new CountingSession(1, wants: false);
        world.AddPlayer(TestWorld.CreatePlayer(1, 0, 0, session));
        session.UpdatePackets = 0;
        var unit = new TestUnit(1, 20, 0);
        map.AddObject(unit);
        map.Update(50);
        Assert.Equal(0, session.UpdatePackets);
        session.WantsUpdateObjects = true;
        unit.Health = 5;
        map.Update(50);
        Assert.True(session.UpdatePackets > 0);
    }
}
