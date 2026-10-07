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
            "Syntax: .playerbot <create|start|stop|status|list|inspect|scenario>\nManage server-owned autonomous players.",
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
                Scenarios.PlayerbotScenarioCommands.Command,
            ])
    ];

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
        context.Reply(FormattableString.Invariant($"BOTINSPECT movement=flags:{(uint)value.MovementFlags:X8} stand:{value.StandState} time:{value.MovementTimeMs}"));
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
            + $"error={status.ErrorCode ?? "none"}");
}
