using System.Buffers.Binary;
using ArcaneCore.Kernel.Realms;
using ArcaneCore.Realm.Protocol;
using Xunit;

namespace ArcaneCore.Realm.Tests;

/// <summary>
/// Bug #221/#154: <c>RealmListWriter.Build</c> cast the realm count to a byte and the body
/// length to a ushort without a guard, so an oversized list wrapped into a corrupt reply.
/// </summary>
public sealed class RealmListWriterBoundsTests
{
    private static List<RealmEntry> MakeRealms(int count, int nameLength = 4)
    {
        var realms = new List<RealmEntry>(count);
        for (int i = 0; i < count; i++)
        {
            realms.Add(new RealmEntry { Name = new string('a', nameLength), Address = "127.0.0.1:8085" });
        }

        return realms;
    }

    /// <summary>256 realms cannot be represented in the u8 count and must fail closed.</summary>
    [Fact]
    public void Build_256Realms_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => RealmListWriter.Build(MakeRealms(256), 0));
    }

    /// <summary>255 realms is the largest valid count: count byte is 255 and the size field matches the body.</summary>
    [Fact]
    public void Build_255Realms_CountAndSizeAreConsistent()
    {
        ReadOnlyMemory<byte> packet = RealmListWriter.Build(MakeRealms(255), 0);
        ReadOnlySpan<byte> span = packet.Span;

        Assert.Equal((byte)AuthCommand.RealmList, span[0]);
        Assert.Equal(packet.Length - 3, BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(1, 2)));
        Assert.Equal(255, span[3 + 4]);
    }

    /// <summary>A body over 65535 bytes (255 realms with long names) cannot fit the u16 size field and must throw.</summary>
    [Fact]
    public void Build_BodyOverUInt16_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => RealmListWriter.Build(MakeRealms(255, nameLength: 300), 0));
    }
}
