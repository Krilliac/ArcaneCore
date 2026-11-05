using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;

namespace ArcaneCore.World.Cluster.Gateway;

/// <summary>
/// The schema and content versions a worker advertises in its link manifest. With <see cref="GatewayWorkerOptions.VersionsFromDatabase"/>
/// they are the versions the databases record (characters and world schema version rows; the content revision is derived from the
/// world schema version), so a worker and a gateway on mixed versions are refused at link admission. The option labels are used only
/// as the explicit override (tests, or an operator who pins them). A database whose version cannot be read, or reads as a
/// create in progress, fails closed: <see cref="Resolve"/> throws and the worker does not start a link it cannot vouch for.
/// <para>Immutable value; safe to share across threads. <see cref="ReadAsync"/> runs on the caller's context and reads each database once.</para>
/// </summary>
/// <param name="CharactersSchemaVersion">The characters schema version.</param>
/// <param name="WorldSchemaVersion">The world schema version.</param>
/// <param name="ContentRevision">The content revision label.</param>
public sealed record GatewayManifestVersions(int CharactersSchemaVersion, int WorldSchemaVersion, string ContentRevision)
{
    /// <summary>The content revision for a world database at <paramref name="worldSchemaVersion"/>.</summary>
    public static string ContentRevisionFor(int worldSchemaVersion) => $"world-schema-{worldSchemaVersion}";

    /// <summary>
    /// The versions to advertise: the option labels when <see cref="GatewayWorkerOptions.VersionsFromDatabase"/> is false, otherwise the
    /// recorded versions. Throws <see cref="InvalidOperationException"/> (fail closed) when a recorded version is missing or not positive.
    /// </summary>
    /// <param name="options">The worker options (labels and the override switch).</param>
    /// <param name="charactersRecorded">The characters version row, or null when absent.</param>
    /// <param name="worldRecorded">The world version row, or null when absent.</param>
    public static GatewayManifestVersions Resolve(GatewayWorkerOptions options, int? charactersRecorded, int? worldRecorded)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.VersionsFromDatabase)
        {
            return new GatewayManifestVersions(options.CharactersSchemaVersion, options.WorldSchemaVersion, options.ContentRevision);
        }

        if (charactersRecorded is not > 0)
        {
            throw new InvalidOperationException(
                $"cannot advertise the characters schema version: the database records {Describe(charactersRecorded)}; refusing to link");
        }

        if (worldRecorded is not > 0)
        {
            throw new InvalidOperationException(
                $"cannot advertise the world schema version: the database records {Describe(worldRecorded)}; refusing to link");
        }

        return new GatewayManifestVersions(charactersRecorded.Value, worldRecorded.Value, ContentRevisionFor(worldRecorded.Value));
    }

    /// <summary>Reads both databases' versions (only when the options ask for it) and resolves them.</summary>
    public static async Task<GatewayManifestVersions> ReadAsync(
        GatewayWorkerOptions options, CharacterDbContext characters, WorldDbContext world, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(characters);
        ArgumentNullException.ThrowIfNull(world);
        if (!options.VersionsFromDatabase)
        {
            return Resolve(options, null, null);
        }

        int? charactersRecorded = await SchemaVersionReader.ReadAsync(characters, CharacterDbContext.Schema, cancellationToken).ConfigureAwait(false);
        int? worldRecorded = await SchemaVersionReader.ReadAsync(world, WorldDbContext.Schema, cancellationToken).ConfigureAwait(false);
        return Resolve(options, charactersRecorded, worldRecorded);
    }

    private static string Describe(int? recorded) => recorded is null ? "no version" : $"version {recorded}";
}
