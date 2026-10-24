using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using ArcaneCore.World.Features;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Architecture;

/// <summary>
/// The core/pack boundary (docs/design/hot-reload-tiers.md, sections 4.1, 4.5 and 4.6). Since tier 2b the Playerbots pack is its own
/// assembly (<c>ArcaneCore.Playerbots</c>, namespaces <c>ArcaneCore.World.Playerbots.*</c>) that references the core; the core
/// (<c>ArcaneCore.World</c>) and the contracts (<c>ArcaneCore.World.Contracts</c>) never reference it. The tier 2a allowlist of core
/// types that still referenced the pack is empty (4 entries before 2b: ClusterCommands, now on the bot registry, and the three
/// reload types, now on the options in the contracts assembly).
/// <para>
/// The other direction is pinned too: the pack may use core internals (World and the contracts grant it <c>InternalsVisibleTo</c>) only
/// from the explicit list below, which may only shrink. Tier 2c turns each entry into a public seam or a contract before the pack is
/// loaded collectibly (a pack compiled against changed internals would fail at JIT time, mid-tick, rather than at load).
/// </para>
/// </summary>
public sealed class PlayerbotCoreBoundaryTests
{
    /// <summary>
    /// Core internals (declaring type full name, then <c>.Member</c>; a type alone when the type itself is internal) the pack still
    /// uses, and why. Shrink only: a new use fails, and so does an entry the pack no longer uses (delete it).
    /// </summary>
    private static readonly SortedDictionary<string, string> AllowedInternals = new(StringComparer.Ordinal)
    {
        // The managed-bot session seam: WorldSession's managed members and the types they hand out. Tier 2c: a public managed-session contract (4.1).
        ["ArcaneCore.World.Net.ManagedActionBudget"] = "the per-tick action budget of a managed session",
        ["ArcaneCore.World.Net.ManagedSessionPacket"] = "a packet a managed session queued for its bot",
        ["ArcaneCore.World.Net.WorldSession.CreateManagedAsync"] = "creates a bot's in-process session",
        ["ArcaneCore.World.Net.WorldSession.DispatchManagedSessionAsync"] = "runs a bot's client packet through the session",
        ["ArcaneCore.World.Net.WorldSession.DrainManagedPackets"] = "reads the packets the server sent the bot",
        ["ArcaneCore.World.Net.WorldSession.IsInChaosProbe"] = "chaos probe marker of a bot session",
        ["ArcaneCore.World.Net.WorldSession.IsManaged"] = "whether a session is in-process",
        ["ArcaneCore.World.Net.WorldSession.IsPlayerbot"] = "whether a session is a bot's",
        ["ArcaneCore.World.Net.WorldSession.ManagedBudget"] = "the action budget of a managed session",
        ["ArcaneCore.World.Net.WorldSession.ManagedClosed"] = "a managed session closed",
        ["ArcaneCore.World.Net.WorldSession.ManagedPacketObserver"] = "the pack's view of packets sent to a bot",
        ["ArcaneCore.World.Net.WorldSession.TryManagedAction"] = "spends one action of the budget",
        ["ArcaneCore.World.Net.WorldSession.TryManagedMovement"] = "applies a bot's movement through the session",
        ["ArcaneCore.World.Handlers.MovementHandlers.RelayStoredMovement"] = "relays a bot's stored movement to watchers",

        // Battlegrounds: the bot battleground coordinator reads the core battleground feature. Tier 2c: a public battleground query seam.
        ["ArcaneCore.World.Battlegrounds.BattlegroundEventIndexMap"] = "match event indexes",
        ["ArcaneCore.World.Battlegrounds.BattlegroundFeature.Battlemasters"] = "the battlemaster list",
        ["ArcaneCore.World.Battlegrounds.BattlegroundFeature.Events"] = "match events",
        ["ArcaneCore.World.Battlegrounds.BattlegroundFeature.IsDeserter"] = "deserter check before queueing a bot",
        ["ArcaneCore.World.Battlegrounds.MatchRuntime"] = "a running match",

        // Other core helpers. Gm/** belongs to another lane: its entries move there (public or a contract) in 2c.
        ["ArcaneCore.World.Creatures.CreatureWorldFeature.ExploreAtlas"] = "the explore atlas the creature feature builds (PlayerbotExploreAtlas itself is public)",
        ["ArcaneCore.World.Gm.Character.Boost.BoostEnvironment"] = "bot gear and level boost through the GM boost",
        ["ArcaneCore.World.Gm.Character.Boost.BoostReport"] = "bot gear and level boost through the GM boost",
        ["ArcaneCore.World.Gm.Character.Boost.CharacterBoost"] = "bot gear and level boost through the GM boost",
        ["ArcaneCore.World.Gm.Death.DeathGmCommands.ReviveInPlace"] = "bot revive",
        ["ArcaneCore.World.Teleport.TeleportCommands.ReplyInvalid"] = "the bot teleport commands' error text",

        // Contracts: the playerbot options' internal helpers. Public, the computed properties would become documented configuration keys; tier 2c makes them methods or keeps this list.
        ["ArcaneCore.World.Playerbots.Chat.PlayerbotChatOptions.EffectiveProviders"] = "computed view",
        ["ArcaneCore.World.Playerbots.Chat.PlayerbotChatOptions.HasModelProvider"] = "computed view",
        ["ArcaneCore.World.Playerbots.Chat.PlayerbotChatProviderOptions.ClassifierReasoningEffort"] = "computed view",
        ["ArcaneCore.World.Playerbots.Chat.PlayerbotChatProviderOptions.Clone"] = "copy for a reload",
        ["ArcaneCore.World.Playerbots.Chat.PlayerbotChatProviderOptions.EffectiveKeyVariable"] = "computed view",
        ["ArcaneCore.World.Playerbots.Chat.PlayerbotChatProviderOptions.EffectiveModel"] = "computed view",
        ["ArcaneCore.World.Playerbots.Chat.PlayerbotChatProviderOptions.EffectiveModerationKeyVariable"] = "computed view",
        ["ArcaneCore.World.Playerbots.Chat.PlayerbotChatProviderOptions.EffectiveModerationModel"] = "computed view",
        ["ArcaneCore.World.Playerbots.Chat.PlayerbotChatProviderOptions.Endpoint"] = "computed view",
        ["ArcaneCore.World.Playerbots.Chat.PlayerbotChatProviderOptions.Label"] = "computed view",
        ["ArcaneCore.World.Playerbots.Chat.PlayerbotChatProviderOptions.ModerationEndpoint"] = "computed view",
        ["ArcaneCore.World.Playerbots.Chat.PlayerbotChatProviderOptions.Price"] = "computed view",
        ["ArcaneCore.World.Playerbots.Chat.PlayerbotChatProviderOptions.ReasoningEffortToSend"] = "computed view",
        ["ArcaneCore.World.Playerbots.Chat.PlayerbotChatProviderOptions.Signature"] = "computed view",
        ["ArcaneCore.World.Playerbots.Chat.PlayerbotChatSafetyOptions.PatternTimeout"] = "a test hook, not a configuration key",
        ["ArcaneCore.World.Playerbots.PlayerbotOptions.ApplyConfiguration"] = "binds the section (the pack's service module and the core's reload)",
        ["ArcaneCore.World.Playerbots.PlayerbotRunOptions.TryParseBand"] = "level band parsing",
        ["ArcaneCore.World.Playerbots.PlayerbotRunOptions.TryParseKey"] = "level band parsing",
        ["ArcaneCore.World.Playerbots.PlayerbotTravelOptions.BackoffMs"] = "computed view",
        ["ArcaneCore.World.Playerbots.Population.PlayerbotPopulationOptions.CheckTarget"] = "validation helper",
        ["ArcaneCore.World.Playerbots.Population.PlayerbotPopulationOptions.RushCeilingMs"] = "computed view",
    };

    private static Assembly World => typeof(WorldHost).Assembly;

    private static Assembly Contracts => typeof(PlayerbotOptions).Assembly;

    private static Assembly Pack => typeof(ManagedPlayerbotFeature).Assembly;

    [Fact]
    public void CoreAndContractsDoNotReferenceThePack()
    {
        Assert.Equal(WorldCodeAssemblies.PlayerbotsAssemblyName, Pack.GetName().Name);
        Assert.NotEqual(World, Pack);
        foreach (Assembly core in new[] { World, Contracts })
        {
            Assert.DoesNotContain(core.GetReferencedAssemblies(), name => name.Name == WorldCodeAssemblies.PlayerbotsAssemblyName);
        }

        // The move is complete: the world assembly defines no type of the pack's namespaces any more (the options moved to the
        // contracts assembly under their old namespace, so config reload and IOptions<T> stay keyed by the same types).
        string[] left = [.. World.GetTypes().Select(t => t.FullName ?? t.Name).Where(n => n.StartsWith(PackReferenceScanner.PackNamespace + ".", StringComparison.Ordinal))];
        Assert.True(left.Length == 0, "pack types still in ArcaneCore.World: " + string.Join(", ", left));

        // And no core type references a pack-namespace type defined in World (the tier 2a scanner, now with nothing to find).
        (PackReferenceScanner scanner, SortedDictionary<string, SortedSet<string>> found) =
            PackReferenceScanner.Scan(World.Location, isPack => !isPack);
        Assert.True(scanner.TypesScanned > 1000, $"only {scanner.TypesScanned} core types were read");
        Assert.True(scanner.BodiesScanned > 10000, $"only {scanner.BodiesScanned} core method bodies were read");
        Assert.Empty(found);
    }

    /// <summary>
    /// The scanner sees references at all (a scan that stopped checking would pass the test above): in the pack assembly, with the
    /// party namespace as the "pack", the managed bot feature's reference to the party AI is found.
    /// </summary>
    [Fact]
    public void ScannerFindsKnownReferences()
    {
        (PackReferenceScanner scanner, SortedDictionary<string, SortedSet<string>> found) =
            PackReferenceScanner.Scan(Pack.Location, isPack => !isPack, packNamespace: PackReferenceScanner.PackNamespace + ".Party");
        Assert.True(scanner.TypesScanned > 500, $"only {scanner.TypesScanned} pack types were read");
        Assert.True(found.TryGetValue("ArcaneCore.World.Playerbots.ManagedPlayerbotFeature", out SortedSet<string>? refs),
            "the managed bot feature's references to the party namespace were not seen");
        Assert.Contains("ArcaneCore.World.Playerbots.Party.PlayerbotPartyAI", refs!);
    }

    [Fact]
    public void PackUsesOnlyTheAllowlistedCoreInternals()
    {
        (SortedSet<string> used, int tokens) = CoreInternalsUsedByPack();
        Assert.True(tokens > 5000, $"only {tokens} type and member references of the pack were resolved");

        var report = new StringBuilder();
        foreach (string member in used.Where(u => !AllowedInternals.ContainsKey(u)))
        {
            report.AppendLine(member);
        }

        foreach (string stale in AllowedInternals.Keys.Where(k => !used.Contains(k)))
        {
            report.AppendLine($"stale allowlist entry (the pack no longer uses it, delete it): {stale}");
        }

        Assert.True(report.Length == 0, "core internals used by the Playerbots pack:" + Environment.NewLine + report);
    }

    /// <summary>
    /// Every type and member reference of the pack (TypeRef and MemberRef tokens, resolved through the loaded pack module) that lands
    /// on a non-public type or member of the core or the contracts, keyed as in <see cref="AllowedInternals"/>. The compiler emits a
    /// reference token only for what the pack's code uses, so a token is a use.
    /// </summary>
    private static (SortedSet<string> Used, int Tokens) CoreInternalsUsedByPack()
    {
        var used = new SortedSet<string>(StringComparer.Ordinal);
        var unresolved = new List<string>();
        int tokens = 0;
        Module module = Pack.ManifestModule;
        using var stream = File.OpenRead(Pack.Location);
        using var pe = new PEReader(stream);
        MetadataReader md = pe.GetMetadataReader();

        foreach (TypeReferenceHandle handle in md.TypeReferences)
        {
            tokens++;
            Type? type = Resolve(() => module.ResolveType(MetadataTokens.GetToken(handle)), md.GetString(md.GetTypeReference(handle).Name), unresolved);
            if (type is not null && IsCore(type.Assembly) && !type.IsVisible)
            {
                used.Add(type.FullName ?? type.Name);
            }
        }

        foreach (MemberReferenceHandle handle in md.MemberReferences)
        {
            tokens++;
            MemberReference reference = md.GetMemberReference(handle);
            if (reference.Parent.Kind is HandleKind.MethodDefinition or HandleKind.ModuleReference)
            {
                continue; // vararg call sites and module-level references: never core
            }

            MemberInfo? member = Resolve(() => module.ResolveMember(MetadataTokens.GetToken(handle))!, md.GetString(reference.Name), unresolved, generic: reference.Parent.Kind == HandleKind.TypeSpecification);
            if (member?.DeclaringType is not { } declaring || !IsCore(declaring.Assembly))
            {
                continue;
            }

            if (!declaring.IsVisible)
            {
                used.Add(declaring.FullName ?? declaring.Name);
            }
            else if (IsInternal(member))
            {
                used.Add((declaring.IsGenericType ? declaring.GetGenericTypeDefinition() : declaring).FullName + "." + MemberName(member));
            }
        }

        Assert.True(unresolved.Count == 0, "pack references that did not resolve: " + string.Join(", ", unresolved.Take(20)));
        return (used, tokens);
    }

    private static T? Resolve<T>(Func<T> resolve, string name, List<string> unresolved, bool generic = false)
        where T : class
    {
        try
        {
            return resolve();
        }
        catch (ArgumentException) when (generic)
        {
            return null; // a member of a generic instantiation the module cannot resolve without a context: the open member is listed separately when it is the core's
        }
        catch (Exception ex) when (ex is ArgumentException or TypeLoadException or MissingMemberException or BadImageFormatException)
        {
            unresolved.Add($"{name}: {ex.GetType().Name}");
            return null;
        }
    }

    private static bool IsCore(Assembly assembly) => assembly == World || assembly == Contracts;

    private static bool IsInternal(MemberInfo member) => member switch
    {
        MethodBase m => m.IsAssembly || m.IsFamilyAndAssembly,
        FieldInfo f => f.IsAssembly || f.IsFamilyAndAssembly,
        _ => false,
    };

    /// <summary>A property or event accessor is listed under the property or event name; a constructor as <c>.ctor</c>.</summary>
    private static string MemberName(MemberInfo member)
    {
        string name = member.Name;
        foreach (string prefix in new[] { "get_", "set_", "add_", "remove_" })
        {
            if (member is MethodInfo { IsSpecialName: true } && name.StartsWith(prefix, StringComparison.Ordinal))
            {
                return name[prefix.Length..];
            }
        }

        return name;
    }
}
