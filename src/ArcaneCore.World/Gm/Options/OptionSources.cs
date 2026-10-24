using System.Globalization;
using ArcaneCore.Game.AntiCheat;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Configuration.Conf;
using ArcaneCore.World.AntiCheat;
using ArcaneCore.World.AntiCheat.PacketHeuristics;
using ArcaneCore.World.OptionKeys;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Rates;
using ArcaneCore.World.Reload;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Gm.ServerConfig;

/// <summary>
/// Where <see cref="OptionCatalog"/> gets its keys: adapters over the existing, already classified sources, so no option is declared twice.
/// The reload keys keep their Read/Apply/Check delegates (<see cref="WorldConfigKeys"/>); the rates keep <see cref="ServerRates"/> as their
/// own baseline tracker; the anticheat and packet-heuristics settings go through the owners' own apply paths; packs add theirs through
/// <see cref="IOptionKeyProvider"/>. All run on the world thread.
/// </summary>
internal static class OptionSources
{
    private const string RatesGroup = "Rates";

    internal static IReadOnlyList<OptionKey> Collect(OptionScope scope, out int skipped)
    {
        var keys = new List<OptionKey>();
        skipped = 0;
        AddReloadKeys(scope, keys, ref skipped);
        AddRates(scope, keys);
        AddAntiCheat(scope, keys);
        AddPacketHeuristics(scope, keys);
        AddProviders(scope, keys);
        return keys;
    }

    // --- the reload keys ---------------------------------------------------------------------------------------------

    private static WorldConfigView ViewOf(OptionScope scope)
        => new(
            scope.World.Options,
            scope.Services.GetService<IOptions<WorldOptions>>()?.Value,
            scope.Services.GetService<SocialFeature>()?.Options,
            scope.Services.GetService<IOptions<PlayerbotOptions>>()?.Value,
            LocomotionEnvironment.RegisteredOptions(scope.World));

    private static void AddReloadKeys(OptionScope scope, List<OptionKey> keys, ref int skipped)
    {
        WorldConfigView probe = ViewOf(scope);
        foreach (WorldConfigKey wck in WorldConfigKeys.All)
        {
            object? native = wck.Read(probe);
            if (native is null)
            {
                continue; // the owning side is not running
            }

            if (!TryCanonical(native, out _, out OptionType type, out IReadOnlyList<string> choices))
            {
                skipped++;
                continue;
            }

            Type clr = native.GetType();
            WorldConfigKey key = wck;
            string[] segments = key.Path.Split(':');
            string group = segments.Length >= 3 ? segments[1] : segments[0];
            var aliases = new List<string>();
            if (key.Path.StartsWith(WorldOptions.SectionName + ":", StringComparison.Ordinal))
            {
                aliases.Add(key.Path[(WorldOptions.SectionName.Length + 1)..]);
            }

            if (MovementAlias(key.Path) is { } movement)
            {
                aliases.Add(movement);
            }

            Func<OptionScope, object?> get = s => key.Read(ViewOf(s)) is { } v && TryCanonical(v, out object canonical, out _, out _) ? canonical : null;
            Func<OptionScope, object, OptionActor, string?>? set = key.Live ? (s, value, _) => ApplyReloadKey(s, key, clr, value) : null;
            keys.Add(new OptionKey(
                key.Path, DescribeReloadKey(key.Path), type, key.Live ? OptionApplyClass.Live : OptionApplyClass.Restart, group, get, set,
                choices: choices, aliases: aliases));
        }
    }

    internal const string NoDescription = "(no description)";

    /// <summary>The first sentence of the key's entry in the configuration catalog (the text the generated reference prints), without markup.</summary>
    internal static string DescribeReloadKey(string path)
    {
        string meaning = ConfCatalog.Embedded.Find(path)?.Meaning.Replace("`", string.Empty, StringComparison.Ordinal).Trim() ?? string.Empty;
        if (meaning.Length == 0)
        {
            return NoDescription;
        }

        int end = meaning.IndexOf(". ", StringComparison.Ordinal);
        string sentence = end > 0 ? meaning[..(end + 1)] : meaning;
        return sentence.Length > 160 ? sentence[..157] + "..." : sentence;
    }

    /// <summary>The <c>.movement set</c> field of a player speed rate key (<c>movement.run</c>), null for any other key.</summary>
    private static string? MovementAlias(string path) => path switch
    {
        "Locomotion:PlayerSpeedRate" => "movement.speedrate",
        "Locomotion:PlayerRunSpeedRate" => "movement.run",
        "Locomotion:PlayerRunBackSpeedRate" => "movement.runback",
        "Locomotion:PlayerSwimSpeedRate" => "movement.swim",
        "Locomotion:PlayerSwimBackSpeedRate" => "movement.swimback",
        "Locomotion:PlayerWalkSpeedRate" => "movement.walk",
        "Locomotion:PlayerTurnRate" => "movement.turn",
        _ => null,
    };

    private static string? ApplyReloadKey(OptionScope scope, WorldConfigKey key, Type clr, object canonical)
    {
        if (!TryNative(clr, canonical, out object native, out string error))
        {
            return error;
        }

        if (key.Check?.Invoke(native) is { } problem)
        {
            return $"{key.Path} {problem}";
        }

        WorldConfigView view = ViewOf(scope);
        object? previous = key.Read(view);
        key.Apply!(view, native);
        if (view.Playerbots is { } playerbots && key.Path.StartsWith(PlayerbotOptions.SectionName + ":", StringComparison.Ordinal))
        {
            // The rules that tie two values together (UnfriendAffinity not above FriendAffinity...) are checked on the whole section, as
            // .reload config does (ConfigCandidate.Validate); the change is taken back when the section no longer holds together.
            try
            {
                playerbots.Social.Lists.Validate();
            }
            catch (InvalidOperationException ex)
            {
                key.Apply(view, previous);
                return ex.Message;
            }
        }

        if (WorldConfigKeys.IsSpeedRate(key) && view.Locomotion is { } locomotion)
        {
            SpeedRates.ApplyToAll(scope.World, locomotion); // the players' copies are re-sent, as .movement set does
        }

        return null;
    }

    /// <summary>The canonical form of a reload key's native value; false for a structured value (a list, a table) or one that does not fit.</summary>
    private static bool TryCanonical(object native, out object canonical, out OptionType type, out IReadOnlyList<string> choices)
    {
        choices = [];
        canonical = native;
        type = OptionType.Text;
        switch (native)
        {
            case bool:
                type = OptionType.Bool;
                return true;
            case sbyte or byte or short or ushort or int or uint or long:
                canonical = Convert.ToInt64(native, CultureInfo.InvariantCulture);
                type = OptionType.Integer;
                return true;
            case ulong big when big <= long.MaxValue:
                canonical = (long)big;
                type = OptionType.Integer;
                return true;
            case float f when float.IsFinite(f):
                canonical = double.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
                type = OptionType.Number;
                return true;
            case double d when double.IsFinite(d):
                type = OptionType.Number;
                return true;
            case string:
                return true;
            case Enum e when !e.GetType().IsDefined(typeof(FlagsAttribute), inherit: false):
                canonical = e.ToString();
                type = OptionType.Choice;
                choices = Enum.GetNames(e.GetType());
                return true;
            default:
                return false;
        }
    }

    /// <summary>The native value of type <paramref name="clr"/> for a canonical one; false, with the reason, when it does not fit the type.</summary>
    private static bool TryNative(Type clr, object canonical, out object native, out string error)
    {
        native = canonical;
        error = string.Empty;
        try
        {
            if (clr == typeof(bool) || clr == typeof(string) || clr == typeof(double))
            {
                return true;
            }

            if (clr.IsEnum)
            {
                native = Enum.Parse(clr, (string)canonical, ignoreCase: true);
                return true;
            }

            if (clr == typeof(float))
            {
                float narrowed = (float)(double)canonical;
                if (!float.IsFinite(narrowed))
                {
                    error = $"{OptionKey.Number((double)canonical)} does not fit a single-precision number";
                    return false;
                }

                native = narrowed;
                return true;
            }

            native = Convert.ChangeType(canonical, clr, CultureInfo.InvariantCulture);
            return true;
        }
        catch (OverflowException)
        {
            error = $"{canonical} is out of range for a {clr.Name}";
            return false;
        }
        catch (ArgumentException)
        {
            error = $"{canonical} is not valid for this option";
            return false;
        }
    }

    // --- the rates ---------------------------------------------------------------------------------------------------

    private static RateScope RateScopeOf(OptionScope scope) => new(scope.Services, scope.World, scope.Logger);

    private static void AddRates(OptionScope scope, List<OptionKey> keys)
    {
        ServerRates rates = ServerRates.For(scope.World);
        foreach (RateDefinition definition in ServerRates.All)
        {
            RateDefinition rate = definition;
            if (!rates.Describe(RateScopeOf(scope), rate).Available)
            {
                continue;
            }

            var aliases = new List<string>(rate.Aliases);
            if (rate.Name.StartsWith("Rate.", StringComparison.Ordinal))
            {
                aliases.Add(rate.Name[5..]);
            }

            Func<OptionScope, object?> get = s => Canonical(ServerRates.For(s.World).Describe(RateScopeOf(s), rate) is { Available: true } d ? d.Current : (float?)null);
            Func<OptionScope, object, OptionActor, string?> set = (s, value, actor) =>
            {
                RateSetResult result = ServerRates.For(s.World).TrySet(RateScopeOf(s), rate, (float)(double)value, actor.Name, actor.Reason, out string? error);
                return result == RateSetResult.Ok ? null : error ?? "refused";
            };
            var tracking = new OptionTracking(
                s =>
                {
                    RateStatus d = ServerRates.For(s.World).Describe(RateScopeOf(s), rate);
                    return (d.Overridden, Canonical(d.Configured));
                },
                (s, actor) => ServerRates.For(s.World).Reset(RateScopeOf(s), rate, actor));
            keys.Add(new OptionKey(
                rate.Name, rate.Description, OptionType.Number, rate.AppliesTo == "live" ? OptionApplyClass.Live : OptionApplyClass.NextLoad, RatesGroup, get, set,
                min: rate.Min, max: rate.Max, aliases: aliases, tracking: tracking, configPath: RateConfigPath(rate.Name) ?? string.Empty));
        }
    }

    /// <summary>
    /// The configuration key a rate is bound from (<c>Progression:RateXpKill</c> for <c>Rate.XP.Kill</c>); null when the name is not a known rate.
    /// A rate that scales two options (<c>Rate.Drop.Money</c>) answers for the first one.
    /// </summary>
    internal static string? RateConfigPath(string name)
    {
        const string dropItem = "Rate.Drop.Item.";
        const string creature = "Rate.Creature.";
        if (name.StartsWith(dropItem, StringComparison.Ordinal))
        {
            return $"Loot:DropItem{name[dropItem.Length..]}Rate";
        }

        if (name.StartsWith(creature, StringComparison.Ordinal) && name != "Rate.Creature.Aggro")
        {
            string[] parts = name[creature.Length..].Split('.');
            string stat = parts[^1] switch { "HP" => "Hp", var other => other };
            string? rank = string.Join('.', parts[..^1]) switch
            {
                "Normal" => "Normal",
                "Elite.Elite" => "Elite",
                "Elite.RAREELITE" => "RareElite",
                "Elite.WORLDBOSS" => "WorldBoss",
                "Elite.RARE" => "Rare",
                _ => null,
            };
            return rank is null ? null : $"Creatures:Rates:{rank}{stat}";
        }

        return name switch
        {
            "Rate.XP.Kill" => "Progression:RateXpKill",
            "Rate.XP.Kill.Elite" => "Progression:RateXpKillElite",
            "Rate.XP.Quest" => "Quests:RateXpQuest",
            "Rate.XP.Explore" => "World:Exploration:RateXp",
            "Rate.XP.Personal.Min" => "Progression:RateXpPersonalMin",
            "Rate.XP.Personal.Max" => "Progression:RateXpPersonalMax",
            "Rate.Rest.InGame" => "Rest:RateInGame",
            "Rate.Rest.Offline.InTavernOrCity" => "Rest:RateOfflineInTavernOrCity",
            "Rate.Rest.Offline.InWilderness" => "Rest:RateOfflineInWilderness",
            "Rate.Reputation.Gain" => "Reputation:RateGain",
            "Rate.Reputation.LowLevel.Kill" => "Reputation:RateLowLevelKill",
            "Rate.Honor" => "World:Honor:Rate",
            "Rate.Talent" => "Talents:PointsRate",
            "Rate.Health" => "Combat:RateHealth",
            "Rate.Mana" => "Combat:RateMana",
            "Rate.Rage.Income" => "Combat:RateRageIncome",
            "Rate.Rage.Loss" => "Combat:RateRageLoss",
            "Rate.Energy" => "Combat:RateEnergy",
            "Rate.Damage.Fall" => "Locomotion:RateDamageFall",
            "Rate.Drop.Money" => "Loot:MoneyRate",
            "Rate.Corpse.Decay.Looted" => "Loot:LootedCorpseDecayRate",
            "Rate.Mining.Amount" => "GameObjects:MiningAmountRate",
            "Rate.Mining.Next" => "GameObjects:MiningNextRate",
            "Rate.Auction.Time" => "Economy:AuctionRateTime",
            "Rate.Auction.Deposit" => "Economy:AuctionRateDeposit",
            "Rate.Auction.Cut" => "Economy:AuctionRateCut",
            "Rate.Creature.Aggro" => "Creatures:AggroRate",
            _ => null,
        };
    }

    private static object? Canonical(float? value)
        => value is { } f ? double.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) : null;

    // --- anticheat ---------------------------------------------------------------------------------------------------

    private static void AddAntiCheat(OptionScope scope, List<OptionKey> keys)
    {
        if (scope.Services.GetService<AntiCheatFeature>() is null)
        {
            return;
        }

        AntiCheatKey(keys, "Enabled", "enabled", "run the anticheat checks", OptionType.Bool, o => o.Enabled, (o, v) => o.Enabled = (bool)v);
        AntiCheatKey(keys, "Action", "action", "the most severe action the anticheat may take", OptionType.Choice, o => o.Action.ToString(),
            (o, v) => o.Action = Enum.Parse<AntiCheatAction>((string)v, ignoreCase: true), Enum.GetNames<AntiCheatAction>());
        AntiCheatKey(keys, "ScoreGmAlert", "alert", "score that alerts the staff", OptionType.Number, o => Num(o.ScoreGmAlert), (o, v) => o.ScoreGmAlert = (float)(double)v);
        AntiCheatKey(keys, "ScoreRubberband", "rubberband", "score that moves the player back", OptionType.Number, o => Num(o.ScoreRubberband), (o, v) => o.ScoreRubberband = (float)(double)v);
        AntiCheatKey(keys, "ScoreKick", "kick", "score that kicks the player", OptionType.Number, o => Num(o.ScoreKick), (o, v) => o.ScoreKick = (float)(double)v);
        AntiCheatKey(keys, "DecayPerSecond", "decay", "score lost per second", OptionType.Number, o => Num(o.DecayPerSecond), (o, v) => o.DecayPerSecond = (float)(double)v);
        AntiCheatKey(keys, "SpeedTolerancePercent", "speedtolerance", "speed above the allowed one that is tolerated, percent", OptionType.Number,
            o => Num(o.SpeedTolerancePercent), (o, v) => o.SpeedTolerancePercent = (float)(double)v);
        AntiCheatKey(keys, "TeleportDistance", "teleport", "jump distance (yards) counted as a teleport hack", OptionType.Number, o => Num(o.TeleportDistance), (o, v) => o.TeleportDistance = (float)(double)v);
        AntiCheatKey(keys, "TerrainChecks", "terrain", "check movement against the terrain", OptionType.Bool, o => o.TerrainChecks, (o, v) => o.TerrainChecks = (bool)v);
        AntiCheatKey(keys, "Log:Persist", "persist", "store the violation log in the database", OptionType.Bool, o => o.Log.Persist, (o, v) => o.Log.Persist = (bool)v);
        AntiCheatKey(keys, "Autoban:Enabled", "autoban", "ban players whose kick points pass the threshold", OptionType.Bool, o => o.Autoban.Enabled, (o, v) => o.Autoban.Enabled = (bool)v);
    }

    private static object Num(float value) => double.Parse(value.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private static void AntiCheatKey(
        List<OptionKey> keys, string name, string field, string description, OptionType type, Func<AntiCheatOptions, object> read, Action<AntiCheatOptions, object> write,
        IReadOnlyList<string>? choices = null)
        => keys.Add(new OptionKey(
            $"{AntiCheatOptions.SectionName}:{name}", description, type, OptionApplyClass.Live, "AntiCheat",
            s => s.Services.GetService<AntiCheatFeature>() is { } feature ? read(feature.Options) : null,
            (s, value, _) =>
            {
                AntiCheatFeature feature = s.Services.GetRequiredService<AntiCheatFeature>();
                AntiCheatOptions options = feature.Options.Clone();
                write(options, value);
                IReadOnlyList<string> problems = options.Validate();
                if (problems.Count > 0)
                {
                    return string.Join(' ', problems);
                }

                feature.ApplyOptions(options);
                return null;
            },
            choices: choices, aliases: [$"anticheat.{field}"]));

    // --- packet heuristics -------------------------------------------------------------------------------------------

    private static void AddPacketHeuristics(OptionScope scope, List<OptionKey> keys)
    {
        if (scope.Services.GetService<PacketHeuristicsFeature>() is null)
        {
            return;
        }

        PacketKey(keys, "Enabled", "enabled", "run the report-only packet heuristics", o => o.Enabled, (f, v) => f.SetEnabled(v));
        PacketKey(keys, "ExemptBots", "exemptbots", "leave the managed playerbot sessions out of the checks", o => o.ExemptBots, (f, v) => f.SetExemptBots(v));
        PacketKey(keys, "ExemptStaff", "exemptstaff", "leave staff accounts out of the checks", o => o.ExemptStaff, (f, v) => f.SetExemptStaff(v));
    }

    private static void PacketKey(
        List<OptionKey> keys, string name, string field, string description, Func<PacketHeuristicsOptions, bool> read, Action<PacketHeuristicsFeature, bool> write)
        => keys.Add(new OptionKey(
            $"{PacketHeuristicsOptions.SectionName}:{name}", description, OptionType.Bool, OptionApplyClass.Live, "PacketHeuristics",
            s => s.Services.GetService<PacketHeuristicsFeature>() is { } feature ? read(feature.Options) : null,
            (s, value, _) =>
            {
                write(s.Services.GetRequiredService<PacketHeuristicsFeature>(), (bool)value);
                return null;
            },
            aliases: [$"packetcheck.{field}"]));

    // --- packs -------------------------------------------------------------------------------------------------------

    private static void AddProviders(OptionScope scope, List<OptionKey> keys)
    {
        foreach (IOptionKeyProvider provider in scope.Services.GetServices<IOptionKeyProvider>())
        {
            try
            {
                provider.RegisterOptions(new Registry(keys));
            }
            catch (Exception ex)
            {
                scope.Logger.LogWarning(ex, "Option provider {Provider} failed to register its keys", provider.GetType().Name);
            }
        }
    }

    private sealed class Registry(List<OptionKey> keys) : IOptionRegistry
    {
        private const string Group = "Pack";

        public void AddBool(string path, string description, OptionApplyClass apply, Func<bool?> get, Action<bool>? set = null)
            => keys.Add(new OptionKey(path, description, OptionType.Bool, apply, Group, _ => get(),
                set is null ? null : (_, v, _) => { set((bool)v); return null; }));

        public void AddInteger(string path, string description, OptionApplyClass apply, long min, long max, Func<long?> get, Action<long>? set = null)
            => keys.Add(new OptionKey(path, description, OptionType.Integer, apply, Group, _ => get(),
                set is null ? null : (_, v, _) => { set((long)v); return null; }, min: min, max: max));

        public void AddNumber(string path, string description, OptionApplyClass apply, double min, double max, Func<double?> get, Action<double>? set = null)
            => keys.Add(new OptionKey(path, description, OptionType.Number, apply, Group, _ => get(),
                set is null ? null : (_, v, _) => { set((double)v); return null; }, min: min, max: max));

        public void AddText(string path, string description, OptionApplyClass apply, Func<string?> get, Action<string>? set = null, bool secret = false)
            => keys.Add(new OptionKey(path, description, OptionType.Text, apply, Group, _ => get(),
                set is null ? null : (_, v, _) => { set((string)v); return null; }, secret: secret));
    }
}
