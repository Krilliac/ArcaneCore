using System.Numerics;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed partial class PlayerbotRealTerrainNavigationTests
{
    [RealTerrainBotFact]
    public async Task ZzProbe_IronforgeWalks()
    {
        Vector3[] spots = [new(-4850, -882, 502), new(-4650, -908, 502), new(-4610, -1100, 505), new(-4800, -1000, 501.7f), new(-4760, -905, 502), new(-4690, -905, 502)];
        await using Terrain terrain = await Terrain.StartAsync();
        var onList = new List<Vector3>();
        foreach (Vector3 s in spots)
        {
            await terrain.PlaceAsync(s);
            await terrain.Host.OnWorldAsync(() =>
            {
                var nav = (ArcaneCore.Game.Maps.Collision.MMaps.NavMeshPathfinder)terrain.Player.Map!.Collision.MeshPoints!;
                (int tx, int ty) = ArcaneCore.Game.Maps.Terrain.TerrainTile.TileOf(s.X, s.Y)!.Value;
                for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) nav.LoadTile(0, tx + dx, ty + dy);
            });
            for (int a = 0; a < 100; a++)
            {
                bool ok = await terrain.Host.OnWorldAsync(() => terrain.Player.Map!.Collision.TryGetMeshFloor(s.X, s.Y, s.Z, 6f, 6f, out _));
                if (ok) break;
                await Task.Delay(50);
                await terrain.Host.World.AdvanceClockAsync(100);
            }
            output.WriteLine(await terrain.Host.OnWorldAsync(() => { var c = terrain.Player.Map!.Collision; bool a = c.TryGetMeshFloor(s.X, s.Y, s.Z, 60f, 60f, out float h); return $"spot {s} wide={a} {h} playerAt={terrain.Player.X},{terrain.Player.Y},{terrain.Player.Z} meshPts={c.MeshPoints?.GetType().Name}"; }));
            onList.Add(await terrain.Host.OnWorldAsync(() =>
            {
                var c = terrain.Player.Map!.Collision;
                for (float r = 0; r <= 30; r += 1)
                    for (float dx = -r; dx <= r; dx += 1)
                        for (float dy = -r; dy <= r; dy += 1)
                            if ((MathF.Abs(dx) == r || MathF.Abs(dy) == r) && c.TryGetMeshFloor(s.X + dx, s.Y + dy, s.Z, 2f, 2f, out float h))
                                return new Vector3(s.X + dx, s.Y + dy, h + 0.1f);
                throw new InvalidOperationException("none near " + s);
            }));
            output.WriteLine($"spot {s} -> {onList[^1]}");
        }
        Vector3[] on = [.. onList];
        using AnomalyCapture capture = await terrain.Host.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(terrain.Player));
        int seen = 0;
        for (int i = 0; i < on.Length; i++)
        for (int j = 0; j < on.Length; j++)
        {
            if (i == j) continue;
            await terrain.PlaceAsync(on[i]);
            output.WriteLine($"=== {i}->{j} {on[i]} -> {on[j]}");
            float left = await terrain.WalkTowardAsync(on[j], 3f, 120_000, output);
            output.WriteLine($"left {left:F1}");
            var all = capture.Notices.ToArray();
            for (; seen < all.Length; seen++) output.WriteLine($"ANOMALY {all[seen].Key}: {all[seen].Detail}");
        }
    }

    [RealTerrainBotFact]
    public async Task ZzProbe_IronforgeGoals()
    {
        Vector3[] goals = [new(-4638.5f, -923.0f, 502.8f), new(-4647.3f, -912.9f, 505.0f), new(-4636.4f, -905.0f, 502.5f), new(-4636.9f, -1034.5f, 501.4f), new(-4726.9f, -892.3f, 501.7f),
            new(-4724.0f, -894.6f, 501.8f), new(-4708.5f, -893.5f, 501.9f), new(-4641.5f, -911.1f, 504.3f), new(-4627.3f, -896.3f, 508.5f), new(-4718.7f, -897.1f, 501.7f),
            new(-4641.4f, -1029.3f, 504.2f), new(-4645.5f, -919.4f, 502.6f), new(-4599.5f, -902.3f, 523.7f), new(-4795f, -888.7f, 502f)];
        Vector3 start = new(-4840.7f, -857.1f, 502.0f);
        await using Terrain terrain = await Terrain.StartAsync();
        using AnomalyCapture capture = await terrain.Host.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(terrain.Player));
        int seen = 0;
        foreach (bool exact in new[] { false, true })
        foreach (Vector3 g in goals)
        {
            await terrain.PlaceAsync(start);
            await terrain.PlaceAsync(g); // load tiles near the goal too
            await terrain.PlaceAsync(start);
            for (int a = 0; a < 40; a++) { await Task.Delay(50); await terrain.Host.World.AdvanceClockAsync(100); }
            output.WriteLine($"=== {(exact ? "exact" : "toward")} -> {g}");
            float left;
            try { left = exact ? await terrain.WalkAsync(g, 150_000, output) : await terrain.WalkTowardAsync(g, 2f, 150_000, output); }
            catch (Exception e) { output.WriteLine("EXC " + e.Message.Split((char)10)[0]); continue; }
            Vector3 end = await terrain.Host.OnWorldAsync(() => new Vector3(terrain.Player.X, terrain.Player.Y, terrain.Player.Z));
            output.WriteLine($"left {left:F1} ended {end}");
            var all = capture.Notices.ToArray();
            for (; seen < all.Length; seen++) output.WriteLine($"ANOMALY {all[seen].Key}: {all[seen].Detail}");
        }
    }
}
