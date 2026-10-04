using System.Reflection;
using ArcaneCore.Data;
using ArcaneCore.Data.Schema.Upgrade;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Social;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.World.Net;
using ArcaneCore.World.Reload;
using ArcaneCore.World.Social;

namespace ArcaneCore.World.Tests.Docs;

/// <summary>A configuration key that is read directly by name (not through an options class).</summary>
internal sealed record AdHocKey(string Path, string TypeText, string Default, string Meaning);

/// <summary>
/// Everything about how configuration is bound that a walk over "options class with a SectionName constant" cannot see.
/// Each row is a deliberate, named exception, so a new binding style fails the coverage guard instead of vanishing
/// from the reference.
/// </summary>
internal static class ConfigExceptionTable
{
    /// <summary>Classes that hold a SectionName constant but are not the options class (the feature owns the constant).</summary>
    public static readonly IReadOnlyDictionary<string, Type> HolderToOptions = new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        ["EconomyFeature"] = typeof(EconomyOptions),
        ["GameObjectLootFeature"] = typeof(LootOptions),
    };

    /// <summary>
    /// Holders whose constant is relative to a parent section and is reached by walking into the parent
    /// (<c>Database:Upgrade</c> is <see cref="DatabaseOptions.Upgrade"/>), so it is not a top-level section.
    /// </summary>
    public static readonly IReadOnlySet<string> RelativeHolders = new HashSet<string>(StringComparer.Ordinal) { nameof(DatabaseUpgradeOptions) };

    /// <summary>Sections bound without a SectionName constant on their type, or several types sharing one section.</summary>
    public static readonly IReadOnlyList<ConfigSection> ExtraSections =
    [
        // One flat section feeds three classes (WorldServiceCollectionExtensions.cs, ConfigContentReloadable.cs).
        new(WorldOptions.SectionName, typeof(WorldRuntimeOptions)),
        new(WorldOptions.SectionName, typeof(WorldSessionOptions)),
        // SocialFeature binds SocialOptions.SectionName + ":WriteQueue" to its own options class.
        new(SocialOptions.SectionName + ":WriteQueue", typeof(SocialWriteQueueOptions)),
    ];

    /// <summary>
    /// Keys that must not be documented twice or are not keys, with the reason. A "Type.Property" entry skips that property;
    /// a bare type name skips a whole options type whose keys another type already documents.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> SkippedProperties = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ChatRestrictionOptions"] = "`World:Chat:FloodMessageCount`, `World:Chat:FloodMessageDelaySeconds` and `World:Chat:FloodMuteSeconds` are bound twice: "
            + "by `ChatFeature` into `ChatOptions` (documented in the `World:Chat` table) and by `ChatRestrictionFeature` into `ChatRestrictionOptions`, both with the same defaults. Set them once; both read them.",
        ["LootOptions.RewardRange"] = "`Loot:RewardRange` is computed from `Loot:GroupLootDistance`, `Loot:BossRewardDistanceBonus` and `Loot:RaidMapsUnlimitedRewardDistance`; it is not a key.",
        ["WorldRuntimeOptions.Perf"] = "`World:Perf:*` is an alias. `PerformanceLog` is the real section (`PerformanceLogOptions.SectionName`); "
            + "`WorldServiceCollectionExtensions` copies it into `WorldRuntimeOptions.Perf` with a PostConfigure, so a `World:Perf:*` key is never read.",
    };

    /// <summary>Configuration sections of the host framework, not of ArcaneCore (the appsettings files carry them).</summary>
    public static readonly IReadOnlyList<string> FrameworkSections = ["Logging", "HostOptions"];

    /// <summary>Options types the host framework binds that the source scan may see.</summary>
    public static readonly IReadOnlySet<string> FrameworkTypes = new HashSet<string>(StringComparer.Ordinal) { "HostOptions" };

    /// <summary>Keys read by name.</summary>
    public static readonly IReadOnlyList<AdHocKey> AdHocKeys =
    [
        new("Spells:RequireSpellFocus", "bool", "true",
            "`false` stops enforcing `SpellInfo.RequiresSpellFocus` on casts (read once by `SpellFocusFeature`); `true` is the retail behaviour."),
        new("Startup:Strict", "bool", "false",
            "`true` makes a configuration warning fail start-up like an error (exit code 78); read by `ConfigValidation.Run`."),
    ];

    /// <summary>The assemblies whose options classes are catalogued.</summary>
    public static readonly IReadOnlyList<Assembly> Assemblies =
    [
        typeof(WorldOptions).Assembly,
        typeof(WorldRuntimeOptions).Assembly,
        typeof(DatabaseOptions).Assembly,
        typeof(WorldConfigKeys).Assembly,
    ];

    /// <summary>The names of the classes that carry a section constant, with that constant's value.</summary>
    public static IReadOnlyDictionary<string, string> DiscoverHolders()
    {
        var holders = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Assembly assembly in Assemblies)
        {
            foreach (Type type in assembly.GetExportedTypes())
            {
                foreach (string constant in new[] { "SectionName", "Section" })
                {
                    FieldInfo? field = type.GetField(constant, BindingFlags.Public | BindingFlags.Static);
                    if (field is { IsLiteral: true } && field.FieldType == typeof(string))
                    {
                        holders[type.Name] = (string)field.GetRawConstantValue()!;
                    }
                }
            }
        }

        return holders;
    }

    /// <summary>Every section of the production catalog.</summary>
    public static IReadOnlyList<ConfigSection> Sections()
    {
        var sections = new List<ConfigSection>();
        foreach (Assembly assembly in Assemblies)
        {
            foreach (Type type in assembly.GetExportedTypes())
            {
                string? path = null;
                foreach (string constant in new[] { "SectionName", "Section" })
                {
                    FieldInfo? field = type.GetField(constant, BindingFlags.Public | BindingFlags.Static);
                    if (field is { IsLiteral: true } && field.FieldType == typeof(string))
                    {
                        path = (string)field.GetRawConstantValue()!;
                    }
                }

                if (path is null || RelativeHolders.Contains(type.Name) || SkippedProperties.ContainsKey(type.Name))
                {
                    continue;
                }

                sections.Add(new ConfigSection(path, HolderToOptions.TryGetValue(type.Name, out Type? options) ? options : type));
            }
        }

        sections.AddRange(ExtraSections);
        return [.. sections.OrderBy(s => s.Path, StringComparer.Ordinal).ThenBy(s => s.Type.Name, StringComparer.Ordinal)];
    }
}
