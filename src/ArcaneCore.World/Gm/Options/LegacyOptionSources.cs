using System.Globalization;
using ArcaneCore.Game.AntiCheat;
using ArcaneCore.World.AntiCheat;
using ArcaneCore.World.AntiCheat.PacketHeuristics;
using ArcaneCore.World.Locomotion;
using ArcaneCore.World.Rates;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.ServerConfig;

/// <summary>
/// The settings that already had their own editor, as option keys: the rates (<see cref="ServerRates"/> keeps their baseline), the
/// anticheat and packet-heuristics settings (through the owners' own apply paths) and the movement aliases. The old <c>set</c> commands
/// and <c>.server config set</c> reach the same keys. All run on the world thread.
/// </summary>
internal static class LegacyOptionSources
{
    private const string RatesGroup = "Rates";

    /// <summary>The <c>.movement set</c> field of a player speed rate key (<c>movement.run</c>), null for any other key.</summary>
    internal static string? MovementAlias(string path)
    {
        foreach ((string field, string rowPath, _, _) in MovementRateCommands.Fields)
        {
            if (rowPath.Equals(path, StringComparison.OrdinalIgnoreCase))
            {
                return "movement." + field;
            }
        }

        return null;
    }

    private static RateScope RateScopeOf(OptionScope scope) => new(scope.Services, scope.World, scope.Logger);

    internal static void AddRates(OptionScope scope, List<OptionKey> keys)
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
                (s, actor, force) =>
                {
                    bool done = ServerRates.For(s.World).TryReset(RateScopeOf(s), rate, actor, RateChangeKind.Reset, out string? refusal, force);
                    return (done, refusal);
                });
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

    internal static void AddAntiCheat(OptionScope scope, List<OptionKey> keys)
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

    internal static void AddPacketHeuristics(OptionScope scope, List<OptionKey> keys)
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

}
