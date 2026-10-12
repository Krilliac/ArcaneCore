using Xunit;

namespace ArcaneCore.World.Tests.ClientData;

/// <summary>The theory form of <see cref="RealClientDbcFactAttribute"/>: skipped unless <c>ARCANECORE_TEST_DBC_DIR</c> names a DBC directory.</summary>
public sealed class RealClientDbcTheoryAttribute : TheoryAttribute
{
    public RealClientDbcTheoryAttribute()
    {
        string? directory = Environment.GetEnvironmentVariable(RealClientDbcFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            Skip = $"{RealClientDbcFactAttribute.Variable} is not set to a directory of build-5875 DBC files.";
        }
    }
}
