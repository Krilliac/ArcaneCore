using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Client.Models;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Client.Models;

public sealed class ClientModelCacheClearDuringLoadTests
{
    private const string PathA = "World\\Doodads\\Test\\A.m2";
    private const string PathB = "World\\Doodads\\Test\\B.m2";

    private static readonly TimeSpan s_wait = TimeSpan.FromSeconds(10);

    /// <summary>Blocks inside Read until released, so Clear can run while the cache's Lazy is still loading.</summary>
    private sealed class GatedFiles(IClientFileSource inner) : IClientFileSource
    {
        private readonly ManualResetEventSlim _entered = new(false);
        private readonly ManualResetEventSlim _release = new(false);

        public bool Gate { get; set; } = true;

        public string Description => inner.Description;

        public void WaitEntered() => Assert.True(_entered.Wait(s_wait), "Read was never entered.");

        public void Release() => _release.Set();

        public bool Exists(string path) => inner.Exists(path);

        public byte[]? Read(string path)
        {
            if (Gate)
            {
                _entered.Set();
                Assert.True(_release.Wait(s_wait), "Read was never released.");
            }

            return inner.Read(path);
        }

        public IReadOnlyList<string> List(string prefix) => inner.List(prefix);

        public string? ArchiveOf(string path) => inner.ArchiveOf(path);
    }

    private static long SizeOfOneCube(ModelWorld world, string path)
    {
        var probe = new ClientModelCache();
        Assert.True(probe.GetM2(world.Files, path).IsOk);
        Assert.True(probe.Bytes > 0);
        return probe.Bytes;
    }

    [Fact]
    public async Task Clear_DuringAnM2Load_DoesNotLeaveBytesCountedForAnUncachedEntry()
    {
        var world = new ModelWorld();
        world.AddCube(PathA);
        var files = new GatedFiles(world.Files);
        var cache = new ClientModelCache();

        Task<ModelLoad<M2ModelFile>> load = Task.Run(() => cache.GetM2(files, PathA));
        files.WaitEntered();
        cache.Clear();
        files.Release();
        Assert.True((await load.WaitAsync(s_wait)).IsOk);

        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.Bytes);
    }

    [Fact]
    public async Task Clear_DuringAnM2Load_ThenReload_CountsOnlyTheLiveEntry()
    {
        var world = new ModelWorld();
        world.AddCube(PathA);
        long one = SizeOfOneCube(world, PathA);
        var files = new GatedFiles(world.Files);
        var cache = new ClientModelCache();

        Task<ModelLoad<M2ModelFile>> load = Task.Run(() => cache.GetM2(files, PathA));
        files.WaitEntered();
        cache.Clear();
        files.Release();
        await load.WaitAsync(s_wait);

        files.Gate = false;
        Assert.True(cache.GetM2(files, PathA).IsOk);

        // A phantom remainder from the cleared load would make this larger than one entry.
        Assert.Equal(1, cache.Count);
        Assert.Equal(one, cache.Bytes);
        cache.Clear();
        Assert.Equal(0, cache.Bytes);
    }

    [Fact]
    public async Task Clear_DuringAWmoLoad_DoesNotLeaveBytesCountedForAnUncachedEntry()
    {
        var world = new ModelWorld();
        world.AddWall();
        var probe = new ClientModelCache();
        Assert.True(probe.GetWmo(world.Files, ModelWorld.WallPath).IsOk);
        long one = probe.Bytes;
        Assert.True(one > 0);

        var files = new GatedFiles(world.Files);
        var cache = new ClientModelCache();

        Task<ModelLoad<WmoModel>> load = Task.Run(() => cache.GetWmo(files, ModelWorld.WallPath));
        files.WaitEntered();
        cache.Clear();
        files.Release();
        Assert.True((await load.WaitAsync(s_wait)).IsOk);
        Assert.Equal(0, cache.Bytes);

        files.Gate = false;
        Assert.True(cache.GetWmo(files, ModelWorld.WallPath).IsOk);
        Assert.Equal(one, cache.Bytes);
    }

    [Fact]
    public async Task Clear_DuringLoad_DoesNotEvictLiveModelsForPhantomBytes()
    {
        var world = new ModelWorld();
        world.AddCube(PathA);
        world.AddCube(PathB);
        long one = SizeOfOneCube(world, PathA);
        Assert.Equal(one, SizeOfOneCube(world, PathB));

        var gated = new GatedFiles(world.Files);
        var counting = new CountingFiles(gated);

        // Budget fits exactly two models. A leaked phantom entry would push the count over and evict a live one.
        var cache = new ClientModelCache(maxBytes: one * 2);
        Task<ModelLoad<M2ModelFile>> stale = Task.Run(() => cache.GetM2(counting, PathA));
        gated.WaitEntered();
        cache.Clear();
        gated.Release();
        await stale.WaitAsync(s_wait);

        gated.Gate = false;
        Assert.True(cache.GetM2(counting, PathA).IsOk);
        Assert.True(cache.GetM2(counting, PathB).IsOk);
        int afterFill = counting.Reads;
        Assert.True(cache.GetM2(counting, PathA).IsOk);
        Assert.True(cache.GetM2(counting, PathB).IsOk);

        Assert.Equal(afterFill, counting.Reads);
        Assert.Equal(2, cache.Count);
        Assert.Equal(one * 2, cache.Bytes);
    }
}
