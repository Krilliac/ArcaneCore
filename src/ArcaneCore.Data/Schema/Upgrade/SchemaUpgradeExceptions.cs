namespace ArcaneCore.Data.Schema.Upgrade;

/// <summary>The database is newer than the code; an older build must not touch it.</summary>
public sealed class SchemaDowngradeException(string message, int databaseVersion, int codeVersion) : SchemaMismatchException(message)
{
    public int DatabaseVersion { get; } = databaseVersion;

    public int CodeVersion { get; } = codeVersion;
}

/// <summary>An upgrade that would be refused (unknown state, version gap, conflicting index, duplicate rows, foreign table); nothing was changed by the pre-flight.</summary>
public sealed class SchemaBlockedException(string message, SchemaPlan plan) : SchemaMismatchException(message)
{
    /// <summary>The plan that found the refusal.</summary>
    public SchemaPlan Plan { get; } = plan;
}

/// <summary>Other sessions are connected to the database and the apply was asked to refuse that.</summary>
public sealed class SchemaActiveSessionsException(string message, int sessions) : SchemaMismatchException(message)
{
    public int Sessions { get; } = sessions;
}

/// <summary>The startup <see cref="SchemaPolicy"/> forbids what the database needs.</summary>
public sealed class SchemaPolicyException(string message, SchemaState state, int? databaseVersion, int codeVersion) : SchemaMismatchException(message)
{
    public SchemaState State { get; } = state;

    public int? DatabaseVersion { get; } = databaseVersion;

    public int CodeVersion { get; } = codeVersion;

    internal static string MissingDatabaseMessage(SchemaDefinition definition)
        => $"The {definition.Component} database does not exist and the schema policy Never creates nothing. " +
           $"Create it with 'arcane-db upgrade' (docs/ops/database-upgrade.md) or set Database:Upgrade:Policy to CreateOnly or Always.";

    /// <summary>Refuse a database state the policy does not allow; states the normal path refuses itself (newer, unknown) pass through.</summary>
    internal static void ThrowIfForbidden(SchemaPolicy policy, SchemaPlan plan)
    {
        if (policy == SchemaPolicy.Always)
        {
            return;
        }

        bool allowed = plan.State switch
        {
            SchemaState.Current or SchemaState.Newer or SchemaState.Unknown => true,
            SchemaState.Missing or SchemaState.Fresh or SchemaState.Creating or SchemaState.NoVersionRow => policy == SchemaPolicy.CreateOnly,
            _ => false,
        };
        if (allowed)
        {
            return;
        }

        string have = plan.DatabaseVersion is { } v ? $"schema version {v}" : $"no complete schema ({plan.State})";
        string message = plan.State == SchemaState.Missing
            ? MissingDatabaseMessage(new SchemaDefinition { Component = plan.Component, CurrentVersion = plan.CodeVersion, Version1Tables = [] })
            : $"The {plan.Component} database has {have}, this build needs schema version {plan.CodeVersion}; the schema policy {policy} " +
              "does not create or upgrade an existing database. Run 'arcane-db upgrade' with every daemon stopped " +
              "(docs/ops/database-upgrade.md), or set Database:Upgrade:Policy to Always.";
        throw new SchemaPolicyException(message, plan.State, plan.DatabaseVersion, plan.CodeVersion);
    }
}
