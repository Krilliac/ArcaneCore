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

        // Entry fields of the list and map options (the groups of keys below Auth:IntegrityHashes, Auth:AutoPatch:Patches, Realms:Seed, NpcServices:NpcTemplates,
        // Quests:AreaTriggerQuests, Economy:AuctionHouses and Warden:Checks). The properties of these entry classes carry no /// summary in the source.
        ["ClientIntegrityHashOptions.Build"] = "The client build this entry is for (for example 5875); it must equal the build the client reported in its logon challenge.",
        ["ClientIntegrityHashOptions.Os"] = "The client operating system string, as the client reports it in the logon challenge; compared ignoring case.",
        ["ClientIntegrityHashOptions.Platform"] = "The client platform string, as the client reports it in the logon challenge; compared ignoring case.",
        ["ClientIntegrityHashOptions.Hash"] = "The 20-byte integrity hash as 40 hex digits. An entry whose text is not 40 characters is skipped, a malformed one is skipped with a warning, and an all-zero hash accepts that client tuple without a check.",
        ["AutoPatchEntry.Build"] = "The client build this patch upgrades (for example 5875); the entry applies to a client of exactly this build.",
        ["RealmSeedEntry.Name"] = "The realm name shown on the realm-list screen.",
        ["RealmSeedEntry.Type"] = "The realm type (icon) shown on the realm-list screen; the numeric values are the build-5875 wire values.",
        ["RealmSeedEntry.Flags"] = "The realm flags stored with the row (offline, new players, recommended and so on). A seeded row starts stopped (offline in the realm list) until a world reports in.",
        ["RealmSeedEntry.Population"] = "The population value stored with the row and sent in the realm list as it is. It holds only until a world reports in: with realm liveness on, the world resets it to 0 and then writes sessions / player limit * 2, so on that scale 0 is empty and 2 is full.",
        ["RealmSeedEntry.Category"] = "The realm category (timezone group) id of the build-5875 realm-list layout.",
        ["NpcTemplateMetadata.Entry"] = "The creature_template entry the override applies to.",
        ["NpcTemplateMetadata.GossipMenuId"] = "Replaces the creature's gossip_menu_id.",
        ["NpcTemplateMetadata.TrainerType"] = "Replaces the creature's trainer_type.",
        ["NpcTemplateMetadata.TrainerClass"] = "Replaces the creature's trainer_class.",
        ["NpcTemplateMetadata.TrainerRace"] = "Replaces the creature's trainer_race.",
        ["NpcTemplateMetadata.TrainerSpell"] = "Replaces the creature's trainer_spell.",
        ["QuestAreaTrigger.TriggerId"] = "The AreaTrigger id (areatrigger_involvedrelation id) that the player enters.",
        ["QuestAreaTrigger.QuestId"] = "The quest that is explored or completed by entering the trigger.",
        ["AuctionHouseEntry.Id"] = "The AuctionHouse.dbc id of the house: 2 is the Alliance house, 6 the Horde house, 7 the neutral house.",
        ["AuctionHouseEntry.DepositPercent"] = "The deposit percentage of the house (AuctionHouse.dbc deposit rate).",
        ["AuctionHouseEntry.CutPercent"] = "The consignment cut percentage the house keeps from a sale (AuctionHouse.dbc consignment rate).",
        ["WardenCheckOptions.Id"] = "The scan id, unique within `Warden:Checks`; an entry replaces the `warden_checks` row with the same id.",
        ["WardenCheckOptions.Kind"] = "Which kind of scan this is; it decides which of the other fields apply.",
        ["WardenCheckOptions.Comment"] = "Free text naming the scan in logs and staff notices; empty shows the kind instead.",

        // Skills (src/ArcaneCore.World/Skills/SkillsFeatureOptions.cs; the SkillsMode enum summary carries the per-value text).
        ["SkillsFeatureOptions.Mode"] = "Which skill implementation the daemon runs. `Retail` (default) is the vmangos skill system and needs the build-5875 skill DBC paths below (without them the daemon logs an error banner and falls back to `Legacy`); `Legacy` is the pre-skill stand-ins for development hosts only.",
    };
}
