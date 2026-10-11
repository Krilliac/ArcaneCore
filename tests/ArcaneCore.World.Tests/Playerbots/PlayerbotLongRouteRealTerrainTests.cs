using System.Numerics;
using ArcaneCore.Game.Npc;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed partial class PlayerbotRealTerrainNavigationTests
{
    // Neeka Bloodscar (creature 5394, spawn 6874), the ender of quest 1418, at the top of the Kargath tower.
    private static readonly Vector3 NeekaBloodscar = new(-6672.25f, -2240.52f, 278.577f);

    /// <summary>
    /// The bot walks (the real motion, game time) from Kargath's floor up the tower's stairs to Neeka Bloodscar, the upper flight of which
    /// the mesh flags steep. Before, every route stopped 13 yards below her and the bots that reached Kargath stood there.
    /// </summary>
    [RealTerrainBotFact]
    public async Task FromKargathsFloor_TheBotWalksUpTheTowerStairs_ToNeekaBloodscar()
    {
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(new Vector3(-6675.7f, -2173.8f, 244.2f));
        await terrain.WalkAsync(NeekaBloodscar, 60_000, output);
        float left = await terrain.Host.OnWorldAsync(() => Vector3.Distance(new(terrain.Player.X, terrain.Player.Y, terrain.Player.Z), NeekaBloodscar));
        Assert.True(left <= QuestNpcServices.InteractionDistance, $"stopped {left:F1} yards from Neeka Bloodscar");
    }
}
