using Xunit;

namespace ArcaneCore.World.Tests.ClientData;

/// <summary>Runs only when <c>ARCANECORE_TEST_DBC_DIR</c> names a directory of the developer's build-5875 DBC files (none is shipped).</summary>
public sealed class RealClientDbcFactAttribute : FactAttribute
{
    public const string Variable = "ARCANECORE_TEST_DBC_DIR";

    public RealClientDbcFactAttribute()
    {
        string? directory = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            Skip = $"{Variable} is not set to a directory of build-5875 DBC files.";
        }
    }

    public static string DbcDirectory => Environment.GetEnvironmentVariable(Variable)!;
}
