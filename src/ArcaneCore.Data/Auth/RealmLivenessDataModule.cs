using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Realms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Auth;

/// <summary>
/// How many characters an account has on a realm (vmangos <c>realmcharacters</c>: realmid, acctid, numchars; sql/logon.sql). The world
/// writes it when a character is created or deleted, and the logon daemon reads it for the realm list's per-realm character count.
/// </summary>
public sealed class RealmCharacterCountRow
{
    public int RealmId { get; set; }

    public int AccountId { get; set; }

    public int NumChars { get; set; }
}

/// <summary>
/// Auth v7: the realm row's liveness columns (<see cref="RealmEntry.State"/>, <see cref="RealmEntry.HeartbeatUtc"/>,
/// <see cref="RealmEntry.AllowedSecurityLevel"/>) and the per-account, per-realm character counts. Every column is additive and has a
/// default, so a v6 database upgrades in place and a row nobody manages keeps its stored flags
/// (<see cref="RealmRuntimeState.Unknown"/>). The version is one named constant: the integrator renumbers it, nothing else hard-codes it.
/// </summary>
public sealed class RealmLivenessDataModule : IDataModule
{
    /// <summary>Auth schema version introducing the liveness columns and the character counts.</summary>
    public const int Version = 7;

    public DatabaseComponent Component => DatabaseComponent.Auth;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new AddColumnChange("realmlist", nameof(RealmEntry.State)),
        new AddColumnChange("realmlist", nameof(RealmEntry.HeartbeatUtc)),
        new AddColumnChange("realmlist", nameof(RealmEntry.AllowedSecurityLevel)),
        new CreateTableChange("realmcharacters"),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RealmEntry>(entity =>
        {
            entity.Property(r => r.State).HasConversion<byte>().HasDefaultValue(RealmRuntimeState.Unknown);
            entity.Property(r => r.AllowedSecurityLevel).HasDefaultValue((byte)0);
        });

        modelBuilder.Entity<RealmCharacterCountRow>(entity =>
        {
            entity.ToTable("realmcharacters");
            entity.HasKey(r => new { r.RealmId, r.AccountId });
            entity.HasIndex(r => r.AccountId);
        });
    }

    public void AddServices(IServiceCollection services)
        => services.AddScoped<IRealmCharacterCountStore, EfRealmCharacterCountStore>();
}
