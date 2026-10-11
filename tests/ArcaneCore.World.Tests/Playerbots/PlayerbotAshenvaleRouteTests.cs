using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed partial class PlayerbotRealTerrainNavigationTests
{
    private static readonly Vector3 AshenvaleStall = new(2770.4f, 1275.2f, 319.1f);
    private static readonly Vector3 JeneuSancrea = new(3355.07f, 1033.14f, 6.03463f);

    [RealTerrainBotFact]
    public async Task Quest6562_FromAshenvaleRidge_SetsAsideTheUnsafeSteepAscent()
    {
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(AshenvaleStall, 1);
        await terrain.Host.OnWorldAsync(() =>
        {
            var map = terrain.Player.Map!;
            var options = new PlayerbotOptions { MaxPathPoints = 128, MaxRouteYards = 2000 };
            PathResult ordinary = map.Collision.FindPath(AshenvaleStall, JeneuSancrea,
                new PathOptions { MaxPoints = 128, Mover = PathMover.Player, ExcludeFlags = NavTerrain.SteepSlopes,
                    AllowPartial = true, MaxSearchNodes = PlayerbotNavigation.MaxSearchNodes, LoadTiles = true });
            PathResult unrestricted = map.Collision.FindPath(AshenvaleStall, JeneuSancrea,
                new PathOptions { MaxPoints = 128, Mover = PathMover.Player, AllowPartial = true,
                    MaxSearchNodes = PlayerbotNavigation.MaxSearchNodes, LoadTiles = true });
            output.WriteLine($"ordinary {ordinary.Type} end {ordinary.End}; unrestricted {unrestricted.Type} end {unrestricted.End} first corner {unrestricted.Points[1]}");
            Assert.True(Vector2.Distance(new(ordinary.End.X, ordinary.End.Y), new(AshenvaleStall.X, AshenvaleStall.Y)) < 2f);
            Assert.True(Vector3.Distance(unrestricted.End, JeneuSancrea) < 8f);
            Assert.False(PlayerbotNavigation.TryPlanToward(terrain.Player, JeneuSancrea, options, out _));
            var suspensions = new PlayerbotSuspensions();
            Assert.True(PlayerbotWorldDestinations.SetAsideSteepAscent(terrain.Player, JeneuSancrea, 6562, 12736,
                terrain.Host.World.NowMs, suspensions, options, out float ascent));
            Assert.True(ascent > 20f, $"reported climb {ascent:F1} yards");
            Assert.True(suspensions.IsQuestSuspended(6562, terrain.Host.World.NowMs));
            Assert.True(suspensions.IsEntrySuspended(12736, terrain.Host.World.NowMs));
            return true;
        });
    }
}
