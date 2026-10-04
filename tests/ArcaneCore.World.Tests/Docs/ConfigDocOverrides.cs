namespace ArcaneCore.World.Tests.Docs;

/// <summary>
/// Reference text for options whose source has no <c>///</c> summary yet. Keyed "Type.Property". The source summary wins
/// whenever one exists, so adding a summary to the option later makes the entry here dead weight (delete it then).
/// These live in the docs tests rather than next to the options so the documentation lane never edits another lane's files.
/// Only state what the code does; cite a retail reference only where it has been read.
/// </summary>
internal static class ConfigDocOverrides
{
    public static readonly IReadOnlyDictionary<string, string> Texts = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // Database (src/ArcaneCore.Data/DatabaseOptions.cs; the class summary describes the per-component fallback).
        ["DatabaseOptions.Provider"] = "The relational engine for any component that has no `Database:<Component>` sub-section of its own (the single-database layout). MariaDB is the primary engine; MySql, PostgreSql and Sqlite are also supported.",
        ["DatabaseOptions.ConnectionString"] = "The connection string for any component that has no `Database:<Component>` sub-section of its own. The shipped Realm file uses this single-database form.",
        ["DatabaseConnectionOptions.Provider"] = "The relational engine of this component's database (`Database:Auth`, `Database:Characters` or `Database:World`).",
        ["DatabaseConnectionOptions.ConnectionString"] = "The connection string of this component's database. The shipped files carry the development placeholder user `arcane`; change it for any real deployment.",

        // Database:Upgrade (src/ArcaneCore.Data/Schema/Upgrade/DatabaseUpgradeOptions.cs; the class summary documents both keys).
        ["DatabaseUpgradeOptions.Policy"] = "What a start may do to the schema. `Always` (default) creates and upgrades; `CreateOnly` creates an empty database but refuses to upgrade an existing one (the retail-like setting for production, where `arcane-db upgrade` applies updates); `Never` only verifies. See docs/ops/database-upgrade.md.",
        ["DatabaseUpgradeOptions.LockTimeoutSeconds"] = "How long a start waits for another process's schema work, in seconds; must be 1..86400 or the start is refused as a configuration error.",

        // Realm seed (src/ArcaneCore.Kernel/Configuration/RealmSeedOptions.cs; the class summary says what the section does).
        ["RealmSeedOptions.Seed"] = "Realms inserted into an empty realm list on first start. Each entry has `Name`, `Address` (`ip:port` of the world server), `Type`, `Flags`, `Population` and `Category`.",

        // Ranged (src/ArcaneCore.Game/Ranged/RangedOptions.cs; the enum members carry the per-value text).
        ["AmmoOptions.Mode"] = "How ranged ammunition is treated. `Retail` (default): a ranged attack needs compatible ammunition (or a thrown weapon) and consumes one per shot. `Infinite` is a developer switch: no ammunition is required or consumed.",
        ["RangeOptions.Leeway"] = "How the spell range leeway of moving casters is treated. `Retail` (default) keeps the movement leeway; `None` is a deviation with only the fixed player allowance.",
        ["TrapOptions.RadiusSource"] = "Where a hunter trap takes its trigger radius from. `Vmangos` (default) is the retail value as vmangos ships it; `Template` always uses the template radius (a deviation).",
        ["TrapOptions.Hostility"] = "How a trap decides a unit is hostile. `Faction` (default) is the retail faction reaction; `AttackTarget` also accepts neutral creatures (a deviation).",

        // Reputation (src/ArcaneCore.World/Reputation/ReputationOptions.cs; vmangos mangosd.conf.dist.in:2831-2832 lists both keys).
        ["ReputationOptions.RateGain"] = "Multiplier on every reputation gain; the vmangos key `Rate.Reputation.Gain` (default 1, mangosd.conf.dist.in:2831).",
        ["ReputationOptions.RateLowLevelKill"] = "Multiplier on reputation gained from killing low-level creatures; the vmangos key `Rate.Reputation.LowLevel.Kill` (default 0.2, mangosd.conf.dist.in:2832).",

        // Skills (src/ArcaneCore.World/Skills/SkillsFeatureOptions.cs; the SkillsMode enum summary carries the per-value text).
        ["SkillsFeatureOptions.Mode"] = "Which skill implementation the daemon runs. `Retail` (default) is the vmangos skill system and needs the build-5875 skill DBC paths below (without them the daemon logs an error banner and falls back to `Legacy`); `Legacy` is the pre-skill stand-ins for development hosts only.",
    };
}
