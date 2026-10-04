using System.Globalization;
using System.Net;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Gm;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Instances;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Reputation;
using ArcaneCore.World.Social;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Gm.Audit;

/// <summary>
/// ArcaneCore's own operator commands, under the root <c>.arcane</c>: a name no retail core has, so it can never collide
/// with a future retail command. Every sub-command only READS state:
/// <c>bancheck</c> says whether an account, a character's account or an IP address is blocked from logging in right now;
/// <c>mutes</c> lists the chat mutes in force; <c>gmlog</c> prints the tail of the GM command audit log;
/// <c>queues</c> reports the write-behind queues (pending writes and writes retained after failed attempts).
/// <c>bancheck</c> and <c>mutes</c> need GameMaster, <c>gmlog</c> and <c>queues</c> Administrator (the audit log shows what
/// other staff typed, and the queue report is for whoever runs the server).
/// </summary>
public sealed class ArcaneCommands : ICommandGroup
{
    /// <summary>The most lines <c>.arcane mutes</c> prints.</summary>
    public const int MaxMutesListed = 50;

    /// <summary>The default and the largest line count of <c>.arcane gmlog</c>.</summary>
    public const int DefaultLogLines = 20;

    public const int MaxLogLines = 100;

    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("arcane", AccountSecurity.GameMaster, "ArcaneCore operator commands (read-only). Syntax: .arcane $subcommand", Children:
        [
            new ChatCommand("bancheck", AccountSecurity.GameMaster, GmAuditStrings.BanCheckSyntax, BanCheck),
            new ChatCommand("mutes", AccountSecurity.GameMaster, "Syntax: .arcane mutes\nList the chat mutes in force, with who set them and when they end.", Mutes),
            new ChatCommand("gmlog", AccountSecurity.Administrator, "Syntax: .arcane gmlog [$count]\nShow the latest audited GM commands (default 20, at most 100; kept in memory since the last restart).", GmLog),
            new ChatCommand("queues", AccountSecurity.Administrator, "Syntax: .arcane queues\nShow the pending and retained writes of the write-behind queues.", Queues),
        ]),
    ];

    // ---- .arcane bancheck -----------------------------------------------------------------------

    private static bool BanCheck(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        string? kind = args.ExtractLiteral();
        string? value = args.ExtractLiteral();
        if (kind is null || value is null || !args.IsEmpty)
        {
            return false;
        }

        if ("ip".Equals(kind, StringComparison.OrdinalIgnoreCase))
        {
            if (!IPAddress.TryParse(value, out _) || AccountBanEvaluator.NormalizeIp(value) is not { } ip)
            {
                return false;
            }

            Run(context, async services => await ReplyIpAsync(services, context, ip).ConfigureAwait(false));
            return true;
        }

        bool byCharacter = "character".Equals(kind, StringComparison.OrdinalIgnoreCase);
        if (!byCharacter && !"account".Equals(kind, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int? characterAccount = null;
        if (byCharacter)
        {
            if (!PlayerNames.TryNormalize(value, out string normalized)
                || context.Session.Services.GetRequiredService<CharacterDirectory>().FindByName(normalized) is not { } identity)
            {
                context.Reply(GmStrings.PlayerNotFound);
                return true;
            }

            characterAccount = identity.AccountId;
        }

        Run(context, async services => await ReplyAccountAsync(services, context, value, characterAccount).ConfigureAwait(false));
        return true;
    }

    private static async Task ReplyAccountAsync(IServiceProvider services, CommandContext context, string value, int? characterAccount)
    {
        Account? account;
        if (characterAccount is { } id)
        {
            IReadOnlyDictionary<int, string> names = await services.GetRequiredService<IAccountAdmin>().GetUsernamesAsync([id]).ConfigureAwait(false);
            account = names.TryGetValue(id, out string? username) ? await services.GetRequiredService<IAccountStore>().FindByUsernameAsync(username).ConfigureAwait(false) : null;
        }
        else
        {
            account = await services.GetRequiredService<IAccountStore>().FindByUsernameAsync(value).ConfigureAwait(false);
        }

        if (account is null)
        {
            context.Reply(string.Format(CultureInfo.InvariantCulture, "Account {0} does not exist.", characterAccount is null ? value.ToUpperInvariant() : "of that character"));
            return;
        }

        AccountBanRecord? ban = await services.GetRequiredService<IBanStore>().GetActiveAccountBanAsync(account.Id).ConfigureAwait(false);
        bool blocked = ban is not null || account.Status != AccountStatus.Active;
        context.Reply(string.Create(CultureInfo.InvariantCulture, $"Account {account.Username} (id {account.Id}): {(blocked ? "BLOCKED from logging in" : "not blocked")}"));
        if (ban is not null)
        {
            context.Reply(ban.IsPermanent
                ? $"Ban: permanent, set {GmAuditStrings.Stamp(ban.BanDate)} by {ban.BannedBy}: {ban.Reason}"
                : $"Ban: until {GmAuditStrings.Stamp(ban.UnbanDate)}, set {GmAuditStrings.Stamp(ban.BanDate)} by {ban.BannedBy}: {ban.Reason}");
        }

        if (account.Status != AccountStatus.Active)
        {
            context.Reply($"Status override: {account.Status} (set on the account itself, not a ban row)");
        }

        GmAuditFeature audit = context.Session.Services.GetRequiredService<GmAuditFeature>();
        context.Reply("Chat: " + GmAuditStrings.MuteState(audit.MuteOf(account.Id), audit.NowUnixSeconds));
    }

    private static async Task ReplyIpAsync(IServiceProvider services, CommandContext context, string ip)
    {
        IpBanRecord? ban = await services.GetRequiredService<IBanStore>().GetActiveIpBanAsync(ip).ConfigureAwait(false);
        if (ban is null)
        {
            context.Reply($"IP {ip}: not blocked");
            return;
        }

        context.Reply(ban.IsPermanent
            ? $"IP {ip}: BLOCKED permanently, set {GmAuditStrings.Stamp(ban.BanDate)} by {ban.BannedBy}: {ban.Reason}"
            : $"IP {ip}: BLOCKED until {GmAuditStrings.Stamp(ban.UnbanDate)}, set {GmAuditStrings.Stamp(ban.BanDate)} by {ban.BannedBy}: {ban.Reason}");
    }

    /// <summary>
    /// Run the store work off the world thread on its own DI scope and reply when it completes; a failure is logged and
    /// answered, never thrown into the world (the pattern of <c>.ban</c>).
    /// </summary>
    private static void Run(CommandContext context, Func<IServiceProvider, Task> work)
    {
        IServiceScopeFactory scopes = context.Session.Services.GetRequiredService<IServiceScopeFactory>();
        ILogger logger = context.Session.Logger;
        _ = Task.Run(async () =>
        {
            try
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                await work(scope.ServiceProvider).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "An .arcane command failed");
                context.Reply(GmAuditStrings.BanStoreUnavailable);
            }
        });
    }

    // ---- .arcane mutes --------------------------------------------------------------------------

    private static bool Mutes(CommandContext context, string text)
    {
        if (text.Length > 0)
        {
            return false;
        }

        GmAuditFeature audit = context.Session.Services.GetRequiredService<GmAuditFeature>();
        long now = audit.NowUnixSeconds;
        IReadOnlyList<AccountMuteRecord> mutes = audit.ActiveMutes();
        if (mutes.Count == 0)
        {
            context.Reply(GmAuditStrings.NoMutes);
            return true;
        }

        Dictionary<int, string> online = context.World.OnlinePlayers.GroupBy(p => p.AccountId).ToDictionary(g => g.Key, g => g.First().Name);
        context.Reply(string.Create(CultureInfo.InvariantCulture, $"Chat mutes in force: {mutes.Count}"));
        foreach (AccountMuteRecord mute in mutes.Take(MaxMutesListed))
        {
            context.Reply(string.Create(CultureInfo.InvariantCulture,
                $"Account {mute.AccountId} ({(online.TryGetValue(mute.AccountId, out string? name) ? name : "offline")}): {GmAuditStrings.Span(mute.MutedUntil - now)} left, by {mute.MutedBy}: {mute.Reason}"));
        }

        if (mutes.Count > MaxMutesListed)
        {
            context.Reply(GmAuditStrings.TicketsOmitted(mutes.Count - MaxMutesListed));
        }

        return true;
    }

    // ---- .arcane gmlog --------------------------------------------------------------------------

    private static bool GmLog(CommandContext context, string text)
    {
        int count = DefaultLogLines;
        if (text.Length > 0)
        {
            var args = new CommandArgs(text);
            if (!args.ExtractUInt32(out uint requested) || !args.IsEmpty || requested is 0)
            {
                return false;
            }

            count = (int)Math.Min(requested, MaxLogLines);
        }

        GmAuditFeature audit = context.Session.Services.GetRequiredService<GmAuditFeature>();
        if (audit.TailCapacity == 0)
        {
            context.Reply(GmAuditStrings.AuditTailOff);
            return true;
        }

        IReadOnlyList<GmAuditEntry> entries = audit.Tail(count);
        if (entries.Count == 0)
        {
            context.Reply(GmAuditStrings.NoAuditLines);
            return true;
        }

        foreach (GmAuditEntry entry in entries)
        {
            context.Reply(string.Create(CultureInfo.InvariantCulture, $"[{GmAuditStrings.Stamp(entry.Unix)}] {entry.Player} (account {entry.AccountId}): .{entry.Command}"));
        }

        return true;
    }

    // ---- .arcane queues -------------------------------------------------------------------------

    private static bool Queues(CommandContext context, string text)
    {
        if (text.Length > 0)
        {
            return false;
        }

        IServiceProvider services = context.Session.Services;
        var lines = new List<(string Name, int Pending, IReadOnlyList<string> Retained)>();
        Add(lines, "character saves", services.GetService<CharacterSaveQueue>()?.Pending);
        Add(lines, "social", services.GetService<SocialFeature>()?.PendingWrites);
        Add(lines, "reputation", services.GetService<ReputationFeature>()?.PendingWrites);
        Add(lines, "instances", services.GetService<InstanceFeature>()?.PendingWrites);
        Add(lines, "creature respawns", services.GetService<CreatureRespawnFeature>()?.PendingWrites);
        Add(lines, "explored zones", services.GetService<ExploredZonesPersistence>()?.Writes.Pending);
        if (services.GetService<GmAuditFeature>() is { } audit)
        {
            lines.Add(("gm audit", audit.Writes.Pending, audit.Writes.RetainedKeys));
        }

        foreach ((string name, int pending, IReadOnlyList<string> retained) in lines)
        {
            context.Reply(string.Create(CultureInfo.InvariantCulture,
                $"{name}: {pending} pending{(retained.Count > 0 ? $", RETAINED after failed writes: {string.Join(", ", retained)}" : string.Empty)}"));
        }

        return true;
    }

    private static void Add(List<(string Name, int Pending, IReadOnlyList<string> Retained)> lines, string name, int? pending)
    {
        if (pending is { } value)
        {
            lines.Add((name, value, []));
        }
    }
}
