using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Client.Models;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Client.Models;

/// <summary>An archive that stores a model with a compression method the reader has no code for is a rejected model, not an exception.</summary>
public sealed class ClientModelCacheUnsupportedCompressionTests
{
    private sealed class UnsupportedFiles : IClientFileSource
    {
        public string Description => "unsupported";

        public bool Exists(string path) => true;

        public byte[]? Read(string path) => throw new NotSupportedException("compression method 0x40 is not supported.");

        public IReadOnlyList<string> List(string prefix) => [];

        public string? ArchiveOf(string path) => null;
    }

    [Fact]
    public void AnM2WithAnUnsupportedCompressionMethod_IsRejected_WithTheReason()
    {
        var cache = new ClientModelCache();

        ModelLoad<M2ModelFile> load = cache.GetM2(new UnsupportedFiles(), "World\Doodads\Test\A.m2");

        Assert.False(load.IsOk);
        Assert.NotNull(load.Problem);
        Assert.Equal(ModelProblemKind.Rejected, load.Problem.Kind);
        Assert.Contains("not supported", load.Problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AWmoWithAnUnsupportedCompressionMethod_IsRejected_WithTheReason()
    {
        var cache = new ClientModelCache();

        ModelLoad<WmoModel> load = cache.GetWmo(new UnsupportedFiles(), "World\Wmo\Test\A.wmo");

        Assert.False(load.IsOk);
        Assert.NotNull(load.Problem);
        Assert.Equal(ModelProblemKind.Rejected, load.Problem.Kind);
    }
}
