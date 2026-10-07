using ArcaneCore.Data.Content.Names;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Content;
using Xunit;

namespace ArcaneCore.Data.Tests.Names;

public sealed class ReservedNameDataTests
{
    [Fact]
    public void Normalize_UsesUnicodeCharactersAndRejectsMalformedOrOverlongNames()
    {
        Assert.Equal("Ärthas".ToLowerInvariant(), ReservedNameNormalization.Normalize("ÄRTHAS"));
        Assert.Equal("𐐀name".ToLowerInvariant(), ReservedNameNormalization.Normalize("𐐀Name"));
        Assert.Null(ReservedNameNormalization.Normalize("abcdefghijkl" + "m"));
        Assert.Null(ReservedNameNormalization.Normalize("bad\uD800"));
        Assert.Equal(new string('中', 12), ReservedNameNormalization.Normalize(new string('中', 12)));
        Assert.Null(ReservedNameNormalization.Normalize(new string('中', 13)));
    }
}
