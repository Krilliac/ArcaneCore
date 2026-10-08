using System.Globalization;
using ArcaneCore.Game;
using ArcaneCore.Game.AntiCheat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.AntiCheat;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Packets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.AntiCheat;

/// <summary>
/// <c>.anticheat status|top|report|set|warn|delete|score|rubberband</c> (docs/areas/anticheat.md), after the MaNGOS Zero
/// fork's AntiCheatCommands.cpp. The fork's SEC_GAMEMASTER commands need GameMaster here and its SEC_ADMINISTRATOR ones
/// (set, delete, score with a value) need Administrator; acting on another player also follows the GM hierarchy rule
/// (<see cref="CommandContext.CanActOn"/>). A target is a character name, or the selected player, or the invoker.
/// <c>.anticheat set</c> changes the live options until the next <c>.reload config</c> or restart. The fork's
/// <c>.spoof</c>/<c>test</c> simulators and its jail are not ported.
/// </summary>
public sealed class AntiCheatCommands : ICommandGroup
{
    private const int ReportRows = 10;

    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("anticheat", AccountSecurity.GameMaster, "Anticheat scores, reports and settings.", Children:
        [
            new ChatCommand("status", AccountSecurity.GameMaster, "Syntax: .anticheat status [$name]\nShow the anticheat settings and the live score of the character (or the selected player, or you).", Status),
            new ChatCommand("top", AccountSecurity.GameMaster, "Syntax: .anticheat top [#count]\nList the highest live scores (10 by default, at most 50).", Top),
            new ChatCommand("report", AccountSecurity.GameMaster, "Syntax: .anticheat report [$name]\nShow the newest violation log rows of the character.", Report),
            new ChatCommand("set", AccountSecurity.Administrator, "Syntax: .anticheat set $field $value\nChange a live setting until the next .reload config: enabled, action (none|log|gmalert|rubberband|kick), alert, rubberband, kick, decay, speedtolerance, teleport, terrain, persist, autoban.", Set),
            new ChatCommand("warn", AccountSecurity.GameMaster, "Syntax: .anticheat warn [$name]\nSend the character an on-screen anticheat warning.", Warn),
            new ChatCommand("delete", AccountSecurity.Administrator, "Syntax: .anticheat delete [$name]\nForget the character's live score and delete its violation log rows.", Delete),
            new ChatCommand("score", AccountSecurity.GameMaster, "Syntax: .anticheat score [$name] [#value]\nShow the character's live score; with a value (administrators) set it and apply what it warrants.", Score),
            new ChatCommand("rubberband", AccountSecurity.GameMaster, "Syntax: .anticheat rubberband [$name]\nMove the online character back to its last validated position.", Rubberband),
        ]),
    ];

    private static AntiCheatFeature? FeatureOf(CommandContext context) => context.Session.Services.GetService<AntiCheatFeature>();

    // --- status / top -------------------------------------------------------------------------------------------------

    private static bool Status(CommandContext context, string text)
    {
        if (FeatureOf(context) is not { } feature)
        {
            context.Reply("AntiCheat is not available on this server.");
            return true;
        }

        AntiCheatOptions o = feature.Options;
        context.Reply(string.Create(CultureInfo.InvariantCulture,
            $"AntiCheat: {(o.Enabled ? "enabled" : "disabled")}, action ceiling {o.Action}, thresholds alert {o.ScoreGmAlert:0} / rubberband {o.ScoreRubberband:0} / kick {o.ScoreKick:0}, decay {o.DecayPerSecond:0.##}/s, speed tolerance {o.SpeedTolerancePercent:0.#}%, teleport {o.TeleportDistance:0} yd, terrain checks {(o.TerrainChecks ? "on" : "off")}, autoban {(o.Autoban.Enabled ? "on" : "off")}, log {(o.Log.Persist ? "on" : "off")} ({feature.Log.Count} queued). Exempt: security {o.ExemptSecurity} and above, GM mode{(o.ExemptManagedBots ? ", managed playerbots" : string.Empty)}."));
        if (Target(context, new CommandArgs(text), out Player? online, out CharacterIdentity? offline) is not { } id)
        {
            return true;
        }

        string name = online?.Name ?? offline!.Name;
        context.Reply(string.Create(CultureInfo.InvariantCulture,
            $"{name}: live score {feature.ScoreOf(id):0}, findings counted {feature.Scores.Findings(id)}{(online is null ? " (offline)" : string.Empty)}."));
        return true;
    }

    private static bool Top(CommandContext context, string text)
    {
        if (FeatureOf(context) is not { } feature)
        {
            context.Reply("AntiCheat is not available on this server.");
            return true;
        }

        var args = new CommandArgs(text);
        int count = 10;
        if (!args.IsEmpty && (!args.ExtractInt32(out count) || count <= 0))
        {
            return false;
        }

        count = Math.Min(count, 50);
        IReadOnlyList<(int CharacterId, float Score)> top = feature.Scores.Top(count, context.World.NowMs, feature.Options.DecayPerSecond);
        if (top.Count == 0)
        {
            context.Reply("AntiCheat: nobody carries a live score.");
            return true;
        }

        CharacterDirectory? directory = context.Session.Services.GetService<CharacterDirectory>();
        context.Reply($"AntiCheat: top {top.Count} by live score:");
        for (int i = 0; i < top.Count; i++)
        {
            string name = context.World.FindOnlinePlayer(ObjectGuid.Player((uint)top[i].CharacterId))?.Name
                ?? directory?.Find(top[i].CharacterId)?.Name ?? $"<character {top[i].CharacterId}>";
            context.Reply(string.Create(CultureInfo.InvariantCulture, $"  {i + 1}. {name}: {top[i].Score:0}"));
        }

        return true;
    }

    // --- report / delete (the violation log) ---------------------------------------------------------------------------

    private static bool Report(CommandContext context, string text)
    {
        if (FeatureOf(context) is not { } feature || Target(context, new CommandArgs(text), out Player? online, out CharacterIdentity? offline) is not { } id)
        {
            return true;
        }

        string name = online?.Name ?? offline!.Name;
        IServiceScopeFactory scopes = context.Session.Services.GetRequiredService<IServiceScopeFactory>();
        Task flushed = feature.FlushNowAsync();
        ILogger logger = context.Session.Logger;
        _ = Task.Run(async () =>
        {
            try
            {
                await flushed.ConfigureAwait(false);
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                if (scope.ServiceProvider.GetService<IAntiCheatLogStore>() is not { } store)
                {
                    context.Reply("AntiCheat: the violation log is not stored on this server.");
                    return;
                }

                IReadOnlyList<AntiCheatLogEntry> rows = await store.RecentAsync(id, ReportRows).ConfigureAwait(false);
                if (rows.Count == 0)
                {
                    context.Reply($"AntiCheat: no recorded violations for {name}.");
                    return;
                }

                context.Reply($"AntiCheat: last violations of {name}:");
                foreach (AntiCheatLogEntry row in rows)
                {
                    context.Reply(string.Create(CultureInfo.InvariantCulture,
                        $"  {DateTimeOffset.FromUnixTimeSeconds(row.LastAt):yyyy-MM-dd HH:mm:ss} {(AntiCheatViolation)row.Type} x{row.Count} weight {row.Weight:0} score {row.Score:0} map {row.MapId} ({row.X:0.0}, {row.Y:0.0}, {row.Z:0.0}) {row.Detail}"));
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, ".anticheat report failed");
                context.Reply("AntiCheat: the violation log is unavailable; see the server log.");
            }
        });
        return true;
    }

    private static bool Delete(CommandContext context, string text)
    {
        if (FeatureOf(context) is not { } feature || Target(context, new CommandArgs(text), out Player? online, out CharacterIdentity? offline) is not { } id)
        {
            return true;
        }

        if (online is not null && !context.CanActOn(online))
        {
            return true;
        }

        string name = online?.Name ?? offline!.Name;
        feature.Forget(id);
        IServiceScopeFactory scopes = context.Session.Services.GetRequiredService<IServiceScopeFactory>();
        ILogger logger = context.Session.Logger;
        logger.LogInformation("AntiCheat: {Gm} cleared the anticheat record of {Character} ({Id})", context.Player.Name, name, id);
        _ = Task.Run(async () =>
        {
            try
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                int rows = scope.ServiceProvider.GetService<IAntiCheatLogStore>() is { } store ? await store.DeleteAsync(id).ConfigureAwait(false) : 0;
                context.Reply($"AntiCheat: cleared the live score and {rows} violation log row(s) of {name}.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, ".anticheat delete failed");
                context.Reply("AntiCheat: the violation log is unavailable; see the server log.");
            }
        });
        return true;
    }

    // --- score / warn / rubberband ---------------------------------------------------------------------------------

    private static bool Score(CommandContext context, string text)
    {
        if (FeatureOf(context) is not { } feature)
        {
            return true;
        }

        var args = new CommandArgs(text);
        string? first = args.ExtractArg();
        string? second = args.ExtractArg();
        string? nameArg = null;
        string? valueArg = null;
        if (first is not null && float.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            valueArg = first;
        }
        else
        {
            nameArg = first;
            valueArg = second;
        }

        if (Target(context, new CommandArgs(nameArg ?? string.Empty), out Player? online, out CharacterIdentity? offline) is not { } id)
        {
            return true;
        }

        string name = online?.Name ?? offline!.Name;
        if (valueArg is null)
        {
            context.Reply(string.Create(CultureInfo.InvariantCulture, $"AntiCheat: {name} has a live score of {feature.ScoreOf(id):0}."));
            return true;
        }

        if (context.Security < AccountSecurity.Administrator)
        {
            context.Reply("Setting a score needs an administrator account.");
            return true;
        }

        if (!float.TryParse(valueArg, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || !float.IsFinite(value) || value < 0)
        {
            return false;
        }

        if (online is null)
        {
            context.Reply($"AntiCheat: {name} is not online.");
            return true;
        }

        if (!context.CanActOn(online))
        {
            return true;
        }

        AntiCheatAction warranted = feature.SetScore(online, value);
        context.Reply(string.Create(CultureInfo.InvariantCulture, $"AntiCheat: set the score of {name} to {value:0}; applied {warranted} under the current ceiling."));
        return true;
    }

    private static bool Warn(CommandContext context, string text)
    {
        if (Target(context, new CommandArgs(text), out Player? online, out _) is null)
        {
            return true;
        }

        if (online is null)
        {
            context.Reply("AntiCheat: that character is not online.");
            return true;
        }

        if (!context.CanActOn(online))
        {
            return true;
        }

        online.Session.Send(WorldOpcode.SmsgNotification, ChatPackets.BuildNotification("[AntiCheat] You have been warned by a GM for suspicious activity."));
        context.Session.Logger.LogInformation("AntiCheat: {Gm} warned {Character}", context.Player.Name, online.Name);
        context.Reply($"AntiCheat: warned {online.Name}.");
        return true;
    }

    private static bool Rubberband(CommandContext context, string text)
    {
        if (FeatureOf(context) is not { } feature || Target(context, new CommandArgs(text), out Player? online, out _) is null)
        {
            return true;
        }

        if (online is null)
        {
            context.Reply("AntiCheat: that character is not online.");
            return true;
        }

        if (!context.CanActOn(online))
        {
            return true;
        }

        context.Reply(feature.Rubberband(online)
            ? $"AntiCheat: moved {online.Name} back to the last validated position."
            : $"AntiCheat: {online.Name} has no validated position yet (it has not moved since entering the world).");
        return true;
    }

    // --- set ------------------------------------------------------------------------------------------------------------

    private static bool Set(CommandContext context, string text)
    {
        if (FeatureOf(context) is not { } feature)
        {
            return true;
        }

        var args = new CommandArgs(text);
        string? field = args.ExtractArg()?.ToLowerInvariant();
        string? value = args.ExtractArg();
        if (field is null || value is null)
        {
            return false;
        }

        AntiCheatOptions options = feature.Options.Clone();
        bool parsed = field switch
        {
            "enabled" or "enable" => TryBool(value, b => options.Enabled = b),
            "action" => Enum.TryParse(value, ignoreCase: true, out AntiCheatAction action) && Enum.IsDefined(action) && Assign(() => options.Action = action),
            "alert" => TryFloat(value, f => options.ScoreGmAlert = f),
            "rubberband" => TryFloat(value, f => options.ScoreRubberband = f),
            "kick" => TryFloat(value, f => options.ScoreKick = f),
            "decay" => TryFloat(value, f => options.DecayPerSecond = f),
            "speedtolerance" => TryFloat(value, f => options.SpeedTolerancePercent = f),
            "teleport" => TryFloat(value, f => options.TeleportDistance = f),
            "terrain" => TryBool(value, b => options.TerrainChecks = b),
            "persist" => TryBool(value, b => options.Log.Persist = b),
            "autoban" => TryBool(value, b => options.Autoban.Enabled = b),
            _ => false,
        };

        if (!parsed)
        {
            context.Reply($"AntiCheat: cannot set '{field}' to '{value}'. Fields: enabled, action (none|log|gmalert|rubberband|kick), alert, rubberband, kick, decay, speedtolerance, teleport, terrain, persist, autoban.");
            return true;
        }

        IReadOnlyList<string> problems = options.Validate();
        if (problems.Count > 0)
        {
            context.Reply("AntiCheat: refused: " + string.Join(" ", problems));
            return true;
        }

        feature.ApplyOptions(options);
        context.Session.Logger.LogInformation("AntiCheat: {Gm} set {Field} = {Value} (live until the next .reload config)", context.Player.Name, field, value);
        context.Reply($"AntiCheat: {field} = {value} (live until the next .reload config or restart).");
        return true;
    }

    private static bool Assign(Action assign)
    {
        assign();
        return true;
    }

    private static bool TryBool(string text, Action<bool> assign)
    {
        bool? value = text.ToLowerInvariant() switch
        {
            "1" or "on" or "true" or "yes" => true,
            "0" or "off" or "false" or "no" => false,
            _ => null,
        };
        if (value is not { } b)
        {
            return false;
        }

        assign(b);
        return true;
    }

    private static bool TryFloat(string text, Action<float> assign)
    {
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || !float.IsFinite(value))
        {
            return false;
        }

        assign(value);
        return true;
    }

    // --- target ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The character named by the first argument (online, or known to the character directory), else the selected player,
    /// else the invoker; its character id, or null after replying that it was not found.
    /// </summary>
    private static int? Target(CommandContext context, CommandArgs args, out Player? online, out CharacterIdentity? offline)
    {
        online = null;
        offline = null;
        string? raw = args.ExtractArg();
        if (raw is null)
        {
            online = context.SelectedPlayerOrSelf();
            if (online is null)
            {
                context.Reply("AntiCheat: select a player or name a character.");
                return null;
            }

            return (int)online.Guid.Low;
        }

        if (!PlayerNames.TryNormalize(raw, out string name))
        {
            context.Reply($"AntiCheat: no character named '{raw}'.");
            return null;
        }

        online = context.World.FindOnlinePlayer(name);
        if (online is not null)
        {
            return (int)online.Guid.Low;
        }

        offline = context.Session.Services.GetService<CharacterDirectory>()?.FindByName(name);
        if (offline is null)
        {
            context.Reply($"AntiCheat: no character named '{name}'.");
            return null;
        }

        return offline.Id;
    }
}
