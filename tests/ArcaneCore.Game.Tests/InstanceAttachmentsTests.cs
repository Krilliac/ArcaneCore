using Xunit;

namespace ArcaneCore.Game.Tests;

/// <summary>The attach, create, try-attach, find and remove semantics the converted locators (locomotion, death hooks, combo points) rely on.</summary>
public sealed class InstanceAttachmentsTests
{
    private sealed class Key;

    private sealed class Value(int number)
    {
        public int Number { get; } = number;
    }

    [Fact]
    public void FindIsNullUntilAttached()
    {
        var table = new InstanceAttachments<Key, Value>();
        var key = new Key();
        Assert.Null(table.Find(key));
        var value = new Value(1);
        table.Attach(key, value);
        Assert.Same(value, table.Find(key));
    }

    [Fact]
    public void AttachReplacesAnEarlierValue()
    {
        var table = new InstanceAttachments<Key, Value>();
        var key = new Key();
        table.Attach(key, new Value(1));
        var second = new Value(2);
        table.Attach(key, second);
        Assert.Same(second, table.Find(key));
    }

    [Fact]
    public void TryAttachKeepsTheFirstValue()
    {
        var table = new InstanceAttachments<Key, Value>();
        var key = new Key();
        var first = new Value(1);
        Assert.True(table.TryAttach(key, first));
        Assert.False(table.TryAttach(key, new Value(2)));
        Assert.Same(first, table.Find(key));
    }

    [Fact]
    public void GetOrCreateCreatesOnceAndRemoveClears()
    {
        var table = new InstanceAttachments<Key, Value>();
        var key = new Key();
        int created = 0;
        Value first = table.GetOrCreate(key, _ => new Value(++created));
        Value again = table.GetOrCreate(key, _ => new Value(++created));
        Assert.Same(first, again);
        Assert.Equal(1, created);

        table.Remove(key);
        Assert.Null(table.Find(key));
        Assert.NotSame(first, table.GetOrCreate(key, _ => new Value(++created)));
    }
}
