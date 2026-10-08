using System.Globalization;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots;

/// <summary>Administrator lifecycle controls for server-managed playerbots.</summary>
public sealed class PlayerbotCommands : ICommandGroup
{
    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("playerbot", AccountSecurity.GameMaster,
            "Syntax: .playerbot <create|start|stop|status|list|inspect|invite|chat|scenario>\nManage server-owned autonomous players.",
            Children:
            [
                new ChatCommand("create", AccountSecurity.Administrator,
                    "Syntax: .playerbot create $name [#race #class]\nCreate a persistent bot character.", Create),
                new ChatCommand("start", AccountSecurity.Administrator,
                    "Syntax: .playerbot start $id|$name\nStart a persistent bot.", Start),
                new ChatCommand("stop", AccountSecurity.Administrator,
                    "Syntax: .playerbot stop $id|$name\nStop a persistent bot.", Stop),
                new ChatCommand("status", AccountSecurity.GameMaster,
                    "Syntax: .playerbot status [$id|$name]\nShow bot lifecycle status.", Status),
                new ChatCommand("list", AccountSecurity.GameMaster,
                    "Syntax: .playerbot list\nList managed bots.", List),
                new ChatCommand("inspect", AccountSecurity.GameMaster,
                    "Syntax: .playerbot inspect $id|$name\nRead actual target, victim, cast and nearby trainer facts.", Inspect),
                new ChatCommand("invite", AccountSecurity.GameMaster,
                    "Syntax: .playerbot invite $id|$name\nPut a running bot into your group; it follows you and takes your commands.", Invite),
                new ChatCommand("chat", AccountSecurity.GameMaster,
                    "Syntax: .playerbot chat <status|flags|pardon>\nThe bots' chat replies and their safety screening.",
                    Children:
                    [
                        new ChatCommand("status", AccountSecurity.GameMaster,
                            "Syntax: .playerbot chat status\nShow bot chat: on/off, the spend estimate, and per provider its kind, model, whether its key variable is set, replies this hour and the last error.",
                            ChatStatus),
                        new ChatCommand("flags", AccountSecurity.GameMaster,
                            "Syntax: .playerbot chat flags [$player]\nShow the latest lines the bot chat safety flagged (all players, or one player's with their strikes, cut-off and AI choice).",
                            ChatFlags),
                        new ChatCommand("pardon", AccountSecurity.GameMaster,
                            "Syntax: .playerbot chat pardon $player\nClear a player's bot chat strikes and cut-off (an automatic mute is lifted with .unmute).",
                            ChatPardon),
                    ]),
                Scenarios.PlayerbotScenarioCommands.Command,
            ])
    ];

    /// <summary>
    /// <c>.playerbot chat status</c>: the chat service's state. A key is never shown, only its variable's name and whether it is set.
    /// </summary>
    private static bool ChatStatus(CommandContext context, string text)
    {
        if (text.Trim().Length != 0) return false;
        if (context.Session.Services.GetService<ManagedPlayerbotFeature>()?.Chat is not { } chat)
        {
            context.Reply("Playerbot chat is unavailable.");
            return true;
        }

        foreach (string line in ChatStatusLines(chat.Status())) context.Reply(line);
        return true;
    }

    /// <summary>The most flag records <c>.playerbot chat flags</c> shows.</summary>
    internal const int MaxFlagLines = 15;

    /// <summary><c>.playerbot chat flags [player]</c>: the newest flag records (one player's, with their standing, when named).</summary>
    private static bool ChatFlags(CommandContext context, string text)
    {
        string name = text.Trim();
        if (name.Contains(' ', StringComparison.Ordinal)) return false;
        if (context.Session.Services.GetService<ManagedPlayerbotFeature>()?.Chat is not { } chat)
        {
            context.Reply("Playerbot chat is unavailable.");
            return true;
        }

        foreach (string line in ChatFlagLines(chat.Safety, name.Length == 0 ? null : name)) context.Reply(line);
        return true;
    }

    internal static IEnumerable<string> ChatFlagLines(Chat.PlayerbotChatSafety safety, string? player)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        if (player is not null)
        {
            if (safety.Player(player) is { } standing)
            {
                string choice = standing.AiReplies switch { true => "on", false => "off", _ => "default" };
                yield return string.Create(c,
                    $"{standing.PlayerName}: strikes={standing.Strikes} cut-off={(standing.CutoffSeconds > 0 ? Math.Ceiling(standing.CutoffSeconds / 60d) + "m" : "no")} ai={choice}");
            }
            else
            {
                yield return $"{player}: no strikes, no cut-off.";
            }
        }

        IReadOnlyList<Chat.BotChatFlag> flags = safety.Flags(player);
        if (flags.Count == 0)
        {
            yield return "No flagged lines.";
            yield break;
        }

        foreach (Chat.BotChatFlag flag in flags.Take(MaxFlagLines))
        {
            string excerpt = flag.Excerpt is null ? string.Empty : $" \"{flag.Excerpt.Replace('|', '/')}\"";
            yield return string.Create(c,
                $"{flag.At:yyyy-MM-dd HH:mm:ss} {flag.PlayerName} (guid {flag.PlayerGuid}, account {flag.AccountId}) -> {flag.Bot}: {flag.Category} ({Chat.PlayerbotChatSafety.Words(flag.Source)}){excerpt}");
        }

        if (flags.Count > MaxFlagLines) yield return string.Create(c, $"... and {flags.Count - MaxFlagLines} older.");
    }

    /// <summary><c>.playerbot chat pardon &lt;player&gt;</c>: clear the player's strikes and cut-off.</summary>
    private static bool ChatPardon(CommandContext context, string text)
    {
        string name = text.Trim();
        if (name.Length == 0 || name.Contains(' ', StringComparison.Ordinal)) return false;
        if (context.Session.Services.GetService<ManagedPlayerbotFeature>()?.Chat is not { } chat)
        {
            context.Reply("Playerbot chat is unavailable.");
            return true;
        }

        ulong? guid = context.World.FindOnlinePlayer(name)?.Guid.Value;
        context.Reply(chat.Safety.Pardon(name, guid)
            ? $"{name}: bot chat strikes and cut-off cleared."
            : $"{name} has no bot chat strikes or cut-off.");
        return true;
    }

    internal static IEnumerable<string> ChatStatusLines(Chat.BotChatStatus status)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        string cap = status.MaxDailySpendUsd > 0 ? Chat.PlayerbotChat.Usd(status.MaxDailySpendUsd) : "none";
        yield return string.Create(c,
            $"Bot chat: enabled={(status.Enabled ? "yes" : "no")} channels={status.Channels} queued={status.Queued} answered={status.Answered} unanswered={status.Dropped} spend-today={Chat.PlayerbotChat.Usd(status.SpentTodayUsd)} cap={cap}");
        foreach (Chat.BotChatProviderStatus p in status.Providers)
        {
            string key = p.KeyVariable is null ? "none" : $"{p.KeyVariable}:{(p.KeyPresent ? "present" : "missing")}";
            yield return string.Create(c,
                $"#{p.Index} {p.Kind} model={p.Model} key={key} replies-hour={p.RepliesThisHour}/{p.MaxRepliesPerHour} replies={p.Replies} errors={p.Errors} last-error={p.LastError ?? "none"} cooldown={p.CooldownSeconds}s priced={(p.Priced ? "yes" : "no")}");
        }

        Chat.BotChatSafetyStatus s = status.Safety;
        yield return string.Create(c,
            $"Bot chat safety: screening={(s.Screening ? "on" : "off")} screened={s.Screened} flagged={s.Flagged} moderation-flagged={s.ModerationFlagged} output-flagged={s.OutputFlagged} builtin-only={s.BuiltinOnly} cut-offs={s.Cutoffs} cut-off-now={s.CutOffNow} auto-mutes={s.AutoMutes} opted-out={s.OptedOut} opted-in={s.OptedIn} disclosed={s.Disclosed} filter={s.Terms} terms/{s.Patterns} patterns");
    }

    private static bool Create(CommandContext context, string text)
    {
        CommandArgs args = new(text);
        string? name = args.ExtractArg();
        if (name is null || !TryOptionalRaceClass(args, out byte race, out byte characterClass)) return false;
        return StartOperation(context, service => service.CreateAsync(name, race, characterClass), "create");
    }

    private static bool Start(CommandContext context, string text)
    {
        if (!TryRequiredId(text, out string id)) return false;
        return StartOperation(context, service => service.StartAsync(id), "start");
    }

    private static bool Stop(CommandContext context, string text)
    {
        if (!TryRequiredId(text, out string id)) return false;
        return StartOperation(context, service => service.StopAsync(id), "stop");
    }

    private static bool Status(CommandContext context, string text)
    {
        IPlayerbotService? service = context.Session.Services.GetService<IPlayerbotService>();
        if (service is null)
        {
            context.Reply("Playerbot service is unavailable.");
            return true;
        }

        PlayerbotStatus? status = null;
        if (!string.IsNullOrWhiteSpace(text) && !TryFind(service, text, out status))
        {
            context.Reply("Playerbot not found.");
            return true;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            foreach (PlayerbotStatus item in service.Snapshot()) context.Reply(Format(item));
        }
        else
        {
            context.Reply(Format(status!));
        }

        return true;
    }

    private static bool List(CommandContext context, string text)
    {
        if (text.Trim().Length != 0) return false;
        return Status(context, string.Empty);
    }

    private static bool Inspect(CommandContext context, string text)
    {
        if (!TryRequiredId(text, out string id)) return false;
        ManagedPlayerbotFeature? service = context.Session.Services.GetService<ManagedPlayerbotFeature>();
        if (service is null) { context.Reply("Playerbot inspection is unavailable."); return true; }
        _ = InspectAndReplyAsync(context, service, id);
        return true;
    }

    /// <summary>
    /// <c>.playerbot invite</c> (vmangos <c>.partybot add</c>): the invoker invites the bot through the ordinary group invite and the
    /// bot accepts at once, whatever its invite policy. Runs on the world thread, where commands run.
    /// </summary>
    private static bool Invite(CommandContext context, string text)
    {
        if (!TryRequiredId(text, out string id)) return false;
        ManagedPlayerbotFeature? service = context.Session.Services.GetService<ManagedPlayerbotFeature>();
        if (service is null) { context.Reply("Playerbot service is unavailable."); return true; }
        PlayerbotOperationResult result = service.InviteToGroup(context.Player, id);
        context.Reply($"Playerbot {(result.Success ? "ok" : "failed")}: {result.Code} ({result.Name ?? id}).");
        return true;
    }

    private static async Task InspectAndReplyAsync(CommandContext context, ManagedPlayerbotFeature service, string id)
    {
        try { await InspectReplyCoreAsync(context, service, id).ConfigureAwait(false); }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException or InvalidOperationException)
        { context.Reply("Playerbot inspection is temporarily unavailable."); }
    }

    private static async Task InspectReplyCoreAsync(CommandContext context, ManagedPlayerbotFeature service, string id)
    {
        PlayerbotInspection? value = await service.InspectAsync(id).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        if (value is null) { context.Reply("Playerbot is not running or was not found."); return; }
        context.Reply(FormattableString.Invariant($"BOTINSPECT {value.Name} goal={value.Goal} report={value.ReportedTarget} quest={value.QuestId} map={value.MapId} level={value.Level} hp={value.Health}/{value.MaxHealth} money={value.Money} combat={value.InCombat} ghost={value.Ghost}"));
        context.Reply(FormattableString.Invariant($"BOTINSPECT death={value.DeathState} pos={value.PlayerX:F2},{value.PlayerY:F2},{value.PlayerZ:F2}"));
        context.Reply(FormattableString.Invariant($"BOTINSPECT movement=flags:{(uint)value.MovementFlags:X8} stand:{value.StandState} time:{value.MovementTimeMs} following:{value.Following} loops:{value.LoopsGivenUp}"));
        context.Reply($"BOTINSPECT stall={value.Stall ?? "none"}");
        context.Reply($"BOTINSPECT {value.Risk ?? "decision=none"}");
        context.Reply(value.Master is null ? "BOTINSPECT party=none"
            : $"BOTINSPECT party=master:{value.Master} mode:{value.PartyMode?.ToString().ToLowerInvariant()}");
        if (value.Corpse is { } corpse)
        {
            string distance = corpse.Distance?.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable";
            context.Reply(FormattableString.Invariant($"BOTINSPECT corpse={corpse.Guid:X} map={corpse.MapId} pos={corpse.X:F2},{corpse.Y:F2},{corpse.Z:F2} distance={distance}"));
            context.Reply($"BOTINSPECT delay_remaining_s={corpse.ReclaimDelayRemainingSeconds?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable"}");
        }
        else
            context.Reply("BOTINSPECT corpse=none");
        context.Reply($"BOTINSPECT target={Facts(value.Target)} victim={Facts(value.Victim)}");
        context.Reply($"BOTINSPECT attackers=count={value.AttackerCount} shown={value.Attackers.Count}");
        foreach (PlayerbotUnitFacts attacker in value.Attackers)
            context.Reply($"BOTINSPECT attacker={Facts(attacker)}");
        context.Reply(FormattableString.Invariant($"BOTINSPECT equipment=mainhand={ItemFacts(value.Equipment.MainHand)} mainhand_skill={value.Equipment.MainHandWeaponSkill?.ToString(CultureInfo.InvariantCulture) ?? "unavailable"} armor={value.Equipment.TotalArmor?.ToString(CultureInfo.InvariantCulture) ?? "unavailable"} feet={ItemFacts(value.Equipment.Feet)}"));
        context.Reply($"BOTINSPECT cast={value.CurrentCast} melee={value.MeleeCast} knownCount={value.KnownSpells.Count}");
        foreach (uint[] batch in value.KnownSpells.Chunk(24))
            context.Reply($"BOTINSPECT known={string.Join(',', batch)}");
        foreach (PlayerbotNpcFacts npc in value.NearbyServices)
        {
            context.Reply(FormattableString.Invariant($"BOTINSPECT npc={npc.Entry} guid={npc.Guid:X} flags={npc.Flags:X} class={npc.TrainerClass} type={npc.TrainerType} hostile={npc.Hostile} combat={npc.InCombat} distance={npc.Distance:F1}"));
            foreach (PlayerbotTeacherFacts teacher in npc.Teaching)
                context.Reply($"BOTINSPECT teach={teacher.TeachingSpell} learned={teacher.LearnedSpell} known={teacher.Known} fit={teacher.FitsClassRace} level={teacher.RequiredLevel} cost={teacher.Cost}");
        }
    }

    private static string Facts(PlayerbotUnitFacts? unit) => unit is null ? "none"
        : $"{unit.Kind}/{unit.Guid:X}/entry:{unit.Entry}/npc:{unit.NpcFlags:X}/faction:{unit.Faction}/type:{unit.CreatureType}";

    private static string ItemFacts(PlayerbotItemFacts? item) => item is null ? "none"
        : $"entry:{item.Entry}/durability:{item.Durability}/{item.MaxDurability}";

    private static bool StartOperation(CommandContext context, Func<IPlayerbotService, Task<PlayerbotOperationResult>> operation,
        string verb)
    {
        IPlayerbotService? service = context.Session.Services.GetService<IPlayerbotService>();
        if (service is null)
        {
            context.Reply("Playerbot service is unavailable.");
            return true;
        }

        context.Reply($"Playerbot {verb} accepted.");
        _ = Task.Run(() => CompleteAsync(context, service, operation));
        return true;
    }

    private static async Task CompleteAsync(CommandContext context, IPlayerbotService service,
        Func<IPlayerbotService, Task<PlayerbotOperationResult>> operation)
    {
        try
        {
            PlayerbotOperationResult result = await operation(service).ConfigureAwait(false);
            string identity = result.Name is null ? result.BotId?.ToString() ?? "unknown" : result.Name;
            context.Reply($"Playerbot {(result.Success ? "ok" : "failed")}: {result.Code} ({identity}).");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            context.Reply($"Playerbot failed: {error.GetType().Name}.");
        }
    }

    private static bool TryFind(IPlayerbotService service, string text, out PlayerbotStatus? status)
    {
        status = null;
        string id = text.Trim();
        if (Guid.TryParse(id, out Guid botId))
            status = service.Snapshot().FirstOrDefault(item => item.BotId == botId);
        else
            status = service.Snapshot().FirstOrDefault(item => item.Name.Equals(id, StringComparison.OrdinalIgnoreCase));
        return status is not null;
    }

    private static bool TryRequiredId(string text, out string id)
    {
        CommandArgs args = new(text);
        id = args.ExtractArg() ?? string.Empty;
        return id.Length != 0 && args.IsEmpty;
    }

    private static bool TryOptionalRaceClass(CommandArgs args, out byte race, out byte characterClass)
    {
        race = 1;
        characterClass = 1;
        if (args.IsEmpty) return true;
        if (!args.ExtractUInt32(out uint rawRace) || rawRace > byte.MaxValue
            || !args.ExtractUInt32(out uint rawClass) || rawClass > byte.MaxValue || !args.IsEmpty)
            return false;
        race = (byte)rawRace;
        characterClass = (byte)rawClass;
        return true;
    }

    private static string Format(PlayerbotStatus status)
        => string.Create(CultureInfo.InvariantCulture,
            $"{status.BotId} {status.Name} state={status.State} desired={status.DesiredEnabled} goal={status.Goal} "
            + $"target={status.TargetEntry} quest={status.QuestId} map={status.MapId} health={status.Health} "
            + $"error={status.ErrorCode ?? "none"}{(status.Risk is { } risk ? " " + risk : string.Empty)}");
}
