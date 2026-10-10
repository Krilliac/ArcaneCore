using System.Globalization;
using ArcaneCore.Game.Economy.AuctionBot;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Economy;

/// <summary>
/// The <c>.ahbot</c> commands (cMaNGOS Chat.cpp ahbotCommandTable, SEC_ADMINISTRATOR; handlers in Level3.cpp:71-182):
/// <c>rebuild [all]</c>, <c>reload</c>, <c>status</c> and <c>item #itemid [$value [$addchance [$minstack [$maxstack]]]] | reset</c>.
/// ArcaneCore adds <c>ledger</c>: the custody totals. The root exists only when <c>AuctionHouseBot:Enabled</c> is on.
/// </summary>
public sealed class AuctionBotCommands : ICommandGroup
{
    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("ahbot", AccountSecurity.Administrator, "Syntax: .ahbot $subcommand", Children:
        [
            new ChatCommand("rebuild", AccountSecurity.Administrator,
                "Syntax: .ahbot rebuild [all]\nExpire the bot's auctions without bids (all of them with 'all') and refill the auction houses.", Rebuild),
            new ChatCommand("reload", AccountSecurity.Administrator, "Syntax: .ahbot reload\nReload the AuctionHouseBot options and the ahbot_items table.", Reload),
            new ChatCommand("status", AccountSecurity.Administrator, "Syntax: .ahbot status\nThe bot's auctions per house and quality.", Status),
            new ChatCommand("item", AccountSecurity.Administrator,
                "Syntax: .ahbot item #itemid [$itemvalue [$addchance [$minstack [$maxstack]]]] | #itemid reset\nShow or override how the bot values and adds an item.", Item),
            new ChatCommand("ledger", AccountSecurity.Administrator, "Syntax: .ahbot ledger\nThe custody ledger: today's budgets, open reservations and the audit.", Ledger),
        ]),
    ];

    public bool IsEnabled(IServiceProvider? services)
        => services is null || services.GetService<IConfiguration>()?.GetValue<bool>($"{AuctionBotOptions.SectionName}:{nameof(AuctionBotOptions.Enabled)}") == true;

    private static AuctionBotFeature? Bot(CommandContext context)
    {
        AuctionBotFeature? bot = context.Session.Services.GetService<AuctionBotFeature>();
        if (bot is null || !bot.Options.Enabled)
        {
            context.Reply("The auction house bot is disabled (AuctionHouseBot:Enabled).");
            return null;
        }

        if (bot.Halted)
        {
            context.Reply("The auction house bot stopped itself after a failed custody audit; see the log.");
        }

        return bot;
    }

    private static bool Rebuild(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        bool all = false;
        if (!args.IsEmpty)
        {
            if (args.ExtractLiteral("all") is null)
            {
                return false;
            }

            all = true;
        }

        if (Bot(context) is not { } bot)
        {
            return true;
        }

        (int expired, int listed) = bot.Rebuild(all);
        context.Reply($"AHBot: rebuilding auction house items: {expired} auctions expire, {listed} new listings.");
        return true;
    }

    private static bool Reload(CommandContext context, string text)
    {
        if (Bot(context) is not { } bot)
        {
            return true;
        }

        IReadOnlyList<string> fixes = bot.Reload();
        context.Reply(fixes.Count == 0 ? "AHBot config reloaded." : $"AHBot config reloaded with {fixes.Count} corrections: {string.Join(" ", fixes)}");
        return true;
    }

    private static bool Status(CommandContext context, string text)
    {
        if (Bot(context) is not { } bot)
        {
            return true;
        }

        var status = bot.Status();
        context.Reply("AHBot auctions (house: total | poor normal uncommon rare epic legendary artifact):");
        foreach ((uint house, int count, int[] q) in status)
        {
            context.Reply(string.Create(CultureInfo.InvariantCulture, $"house {house}: {count} | {string.Join(' ', q)}"));
        }

        context.Reply($"total: {status.Sum(s => s.Count)}");
        return true;
    }

    private static bool Ledger(CommandContext context, string text)
    {
        if (Bot(context) is not { } bot)
        {
            return true;
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var totals = bot.Ledger.Totals(now);
        IReadOnlyList<string> problems = bot.Ledger.Audit();
        context.Reply(string.Create(CultureInfo.InvariantCulture,
            $"AHBot custody: {totals.Rows} rows; today {totals.ItemsToday}/{bot.Options.DailyItemBudget} items, {totals.CopperToday}/{bot.Options.DailyBuyBudgetCopper} copper; reserved {totals.ReservedItems} items, {totals.ReservedCopper} copper; {(bot.Persistent ? "persistent" : "memory only")}; audit {(problems.Count == 0 ? "ok" : string.Join("; ", problems))}."));
        return true;
    }

    private static bool Item(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        string? key = args.ExtractKeyFromLink("Hitem", out _, out _);
        if (key is null)
        {
            return false;
        }

        if (Bot(context) is not { } bot)
        {
            return true;
        }

        IItemTemplateStore templates = context.Player.Inventory.Templates;
        ItemTemplate? template = new CommandArgs(key).ExtractUInt32(out uint id) ? templates.Find(id)
            : LiveItemTemplateStore.Unwrap(templates)?.All.Where(t => t.Name.Equals(key, StringComparison.OrdinalIgnoreCase)).MinBy(t => t.Entry);
        if (template is null)
        {
            context.Reply(GmStrings.CouldNotFind(key));
            return true;
        }

        AuctionBotItemOverride shown;
        if (args.ExtractLiteral("reset") is not null)
        {
            bot.SetItemData(template, null);
            shown = bot.ItemData(template);
        }
        else if (!args.ExtractUInt32(out uint value))
        {
            shown = bot.ItemData(template);
        }
        else
        {
            uint addChance = 0, min = 0, max = 0;
            if (args.ExtractUInt32(out addChance) && args.ExtractUInt32(out min))
            {
                args.ExtractUInt32(out max);
            }

            shown = bot.SetItemData(template, new AuctionBotItemOverride(template.Entry, value, addChance, min, max))!;
        }

        string money = string.Create(CultureInfo.InvariantCulture, $"{shown.Value / 10000}g, {shown.Value / 100 % 100}s, {shown.Value % 100}c. ");
        string detail = shown.MinAmount == 0 ? "Item data is not overridden by user."
            : shown.AddChance == 0 ? "Item will be added using normal sources."
            : string.Create(CultureInfo.InvariantCulture, $"Add chance: {shown.AddChance}%, Min/Max amount: {shown.MinAmount}/{shown.MaxAmount}");
        context.Reply(string.Create(CultureInfo.InvariantCulture, $"{template.Entry} - |cffffffff|Hitem:{template.Entry}:0:0:0|h[{template.Name}]|h|r {money}{detail}"));
        return true;
    }
}
