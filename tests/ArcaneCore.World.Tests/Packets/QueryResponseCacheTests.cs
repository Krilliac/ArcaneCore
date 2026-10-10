using ArcaneCore.World.Packets;
using Xunit;

namespace ArcaneCore.World.Tests.Packets;

public sealed class QueryResponseCacheTests
{
    private sealed record Template(string Name);

    private static byte[] Build(uint entry, Template? template)
        => template is null ? [0xFF, (byte)entry] : [(byte)entry, (byte)template.Name.Length];

    [Fact]
    public void SameTemplate_IsBuiltOnce_AndReturnsTheSameBytes()
    {
        var cache = new QueryResponseCache<Template>();
        var t = new Template("Hogger");
        byte[] first = cache.Get(1, t, Build);
        byte[] second = cache.Get(1, t, Build);
        Assert.Same(first, second);
        Assert.Equal(Build(1, t), first);
        Assert.Equal(1, cache.Builds);
        Assert.Equal(1, cache.Hits);
    }

    [Fact]
    public void ReplacedTemplate_IsRebuilt_EvenWithoutClear()
    {
        var cache = new QueryResponseCache<Template>();
        byte[] old = cache.Get(1, new Template("Hogger"), Build);
        var reloaded = new Template("Hogger the Reloaded");
        byte[] fresh = cache.Get(1, reloaded, Build);
        Assert.NotSame(old, fresh);
        Assert.Equal(Build(1, reloaded), fresh);
        Assert.Same(fresh, cache.Get(1, reloaded, Build));
    }

    [Fact]
    public void UnknownEntries_AreNotKept()
    {
        var cache = new QueryResponseCache<Template>();
        for (uint e = 0; e < 100; e++)
        {
            Assert.Equal(Build(e, null), cache.Get(e, null, Build));
        }

        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Clear_DropsKeptReplies()
    {
        var cache = new QueryResponseCache<Template>();
        var t = new Template("x");
        byte[] first = cache.Get(1, t, Build);
        cache.Clear();
        Assert.Equal(0, cache.Count);
        Assert.NotSame(first, cache.Get(1, t, Build));
    }
}
