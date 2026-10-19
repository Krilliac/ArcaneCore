using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Dbc;
using ArcaneCore.Atlas.Reference.Mpq;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Atlas.Reference.Mpq;

/// <summary>
/// Opens the 1.12.1 client for every other module: registers the <see cref="IClientFileSource"/> (the MPQ archives, or an
/// extracted tree when there are none), the <see cref="IMapDirectory"/> read from the client's DBCs, and the
/// <c>client.*</c> commands. Registers first (<see cref="ModuleOrder.Foundation"/>) because layers read these services while
/// they register.
/// </summary>
public sealed class ClientModule : IAtlasModule
{
    /// <inheritdoc />
    public string Name => "client";

    /// <inheritdoc />
    public int Order => ModuleOrder.Foundation;

    /// <inheritdoc />
    public void Register(AtlasContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The commands exist whether or not a client is configured; they report "unavailable" themselves.
        ClientCommands.Register(context.Commands);

        ILogger log = context.Loggers.CreateLogger("ArcaneCore.Atlas.Client");
        IClientFileSource? files = OpenFiles(context, log);
        if (files is null)
        {
            return;
        }

        try
        {
            context.Services.Add<IMapDirectory>(ClientMapDirectory.Load(files));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException)
        {
            string problem = $"Client map tables could not be read from {files.Description}: {e.GetType().Name}: {e.Message}";
            context.ReportProblem(problem);
            log.LogWarning("{Problem}", problem);
        }
    }

    private static IClientFileSource? OpenFiles(AtlasContext context, ILogger log)
    {
        AtlasPaths paths = context.Paths;
        try
        {
            // The archives are the ground truth; an extracted tree is only the fallback.
            if (paths.ClientDataDirectory is { } data && MpqFileSystem.HasArchives(data))
            {
                MpqFileSystem archives = MpqFileSystem.Open(data);
                context.Services.Add<MpqFileSystem>(archives);
                context.Services.Add<IClientFileSource>(archives);
                foreach (SkippedArchive skipped in archives.Skipped)
                {
                    log.LogInformation("Client archive {Path} not mounted: {Reason}", skipped.Path, skipped.Reason);
                }

                return archives;
            }

            if (paths.ClientRoot is { } root && DirectoryClientFiles.LooksExtracted(root))
            {
                var extracted = new DirectoryClientFiles(root);
                context.Services.Add<IClientFileSource>(extracted);
                return extracted;
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
        {
            string problem = $"Client files could not be opened: {e.GetType().Name}: {e.Message}";
            context.ReportProblem(problem);
            log.LogWarning("{Problem}", problem);
        }

        return null;
    }
}
