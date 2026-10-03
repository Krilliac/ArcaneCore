namespace ArcaneCore.Data.Schema.Upgrade;

/// <summary>
/// What a process may do to an existing database when it starts (retail-like servers refuse to start on
/// a database that is behind and make the operator apply updates; D:\refs\vmangos\src\mangosd\Master.cpp:415-470
/// and D:\refs\vmangos\src\shared\Database\Database.cpp:587-640, D:\refs\mangos-classic\src\shared\Database\Database.cpp:448-498).
/// </summary>
public enum SchemaPolicy
{
    /// <summary>Create and upgrade as needed: what every start did before the upgrade tooling existed.</summary>
    Always = 0,

    /// <summary>Create a database that holds no schema (or resume an interrupted create); refuse to adopt or upgrade an existing one.</summary>
    CreateOnly = 1,

    /// <summary>Verify only: accept a database that is already current, create and change nothing (not even a missing database).</summary>
    Never = 2,
}

/// <summary>One completed schema step, reported to <see cref="SchemaUpgradeOptions.Progress"/>.</summary>
/// <param name="Component">auth, characters or world.</param>
/// <param name="Version">The schema version just written (on SQLite the whole bootstrap commits at the end; on MariaDB each step is already durable).</param>
public sealed record SchemaStepProgress(string Component, int Version);

/// <summary>Options of <see cref="SchemaBootstrapper"/> beyond the schema definition.</summary>
public sealed class SchemaUpgradeOptions
{
    /// <summary>How long to wait for another process that is changing the same component's schema.</summary>
    public TimeSpan LockTimeout { get; init; } = SchemaBootstrapper.DefaultLockTimeout;

    /// <summary>What an existing database may be subjected to; <see cref="SchemaPolicy.Always"/> by default (the historic behaviour).</summary>
    public SchemaPolicy Policy { get; init; } = SchemaPolicy.Always;

    /// <summary>
    /// Whether <see cref="SchemaUpgrader"/> refuses when other sessions are connected to the database
    /// (<see cref="ServerProbe.CountOtherSessionsAsync"/>, best effort). Off by default for the library; the
    /// arcane-db tool turns it on unless told <c>--allow-active-sessions</c>, because the references expect the server to be stopped.
    /// </summary>
    public bool RefuseActiveSessions { get; init; }

    /// <summary>Called once per version row written, in order.</summary>
    public IProgress<SchemaStepProgress>? Progress { get; init; }

    internal void Report(SchemaDefinition definition, int version)
        => Progress?.Report(new SchemaStepProgress(definition.Component, version));
}
