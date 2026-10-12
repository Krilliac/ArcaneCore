using ArcaneCore.Data.Npc;
using ArcaneCore.Kernel.Npc;
using Xunit;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>Skipped (reported as Skipped, not Passed) unless ARCANECORE_TEST_DBC_DIR names a directory holding the build-5875 FactionTemplate.dbc.</summary>
internal sealed class RealFactionDbcFactAttribute : FactAttribute
{
    public RealFactionDbcFactAttribute()
    {
        if (RealFactionDbc.Path() is null)
        {
            Skip = $"{RealFactionDbc.Variable} must name a directory holding the build-5875 FactionTemplate.dbc; the real-faction checks did NOT run.";
        }
    }
}

/// <summary>The developer's build-5875 FactionTemplate.dbc (never shipped), decoded by the production reader.</summary>
internal static class RealFactionDbc
{
    public const string Variable = "ARCANECORE_TEST_DBC_DIR";

    private static readonly Lazy<FactionTemplateCatalog> Loaded = new(() => FactionTemplateDbcReader.Load(Path()!));

    public static string? Path()
    {
        string? directory = Environment.GetEnvironmentVariable(Variable);
        string? file = string.IsNullOrWhiteSpace(directory) ? null : System.IO.Path.Combine(directory, "FactionTemplate.dbc");
        return file is not null && File.Exists(file) ? file : null;
    }

    /// <summary>All 5875 faction templates; read-only, shared by the tests.</summary>
    public static FactionTemplateCatalog Catalog => Loaded.Value;
}
