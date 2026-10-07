using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots.Scenarios;

/// <summary>
/// The scenarios <c>.playerbot scenario run</c> knows: the live-safe built-ins (they need only ordinary classic content:
/// a starting zone, spell 7266 "Duel" with its flag, item 2589 "Linen Cloth") plus any <see cref="IPlayerbotScenario"/>
/// registered in the service container.
/// </summary>
public static class PlayerbotScenarioCatalog
{
    /// <summary>Names of the two bots the built-ins log in (created on first use, reused afterwards).</summary>
    public const string BotA = "Scnalpha";
    public const string BotB = "Scnbeta";

    public static IReadOnlyList<IPlayerbotScenario> Builtins { get; } =
    [
        new SmokeScenario(),
        new GroupChatScenario(),
        new TradeItemForGoldScenario(),
        new DuelScenario(),
    ];

    public static IReadOnlyList<IPlayerbotScenario> All(IServiceProvider services)
        => [.. Builtins, .. services.GetServices<IPlayerbotScenario>().Where(s => Builtins.All(b => b.Name != s.Name))];

    public static IPlayerbotScenario? Find(IServiceProvider services, string name)
        => All(services).FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Log both built-in bots in and stand them face to face, two yards apart, where the first one is.</summary>
    internal static async Task<(ScenarioBot A, ScenarioBot B)> PairAsync(ScenarioContext context)
    {
        ScenarioBot a = await context.StepAsync("login " + BotA, () => context.LoginAsync(BotA)).ConfigureAwait(false);
        ScenarioBot b = await context.StepAsync("login " + BotB, () => context.LoginAsync(BotB)).ConfigureAwait(false);
        await context.StepAsync("place face to face", () => context.PlaceFacingAsync(a, b)).ConfigureAwait(false);
        return (a, b);
    }
}

/// <summary>One bot logs in and its /say comes back to it.</summary>
public sealed class SmokeScenario : IPlayerbotScenario
{
    public string Name => "smoke";

    public string Description => "one bot logs in and hears its own /say";

    public async Task RunAsync(ScenarioContext context)
    {
        ScenarioBot a = await context.StepAsync("login " + PlayerbotScenarioCatalog.BotA,
            () => context.LoginAsync(PlayerbotScenarioCatalog.BotA)).ConfigureAwait(false);
        await context.StepAsync("say and hear it", async () =>
        {
            long mark = a.Mark();
            ScenarioContext.Expect(await a.SayAsync("scenario smoke").ConfigureAwait(false), "say refused");
            ChatMessageView heard = await a.WaitForPacketAsync(WorldOpcode.SmsgMessagechat, ScenarioDecoders.ChatMessage,
                m => m.Type == ChatType.Say && m.Message == "scenario smoke", mark).ConfigureAwait(false);
            ScenarioContext.ExpectEqual(a.Guid.Value, heard.Sender, "say sender");
        }).ConfigureAwait(false);
    }
}

/// <summary>Invite, accept, both rosters, party chat, leave: the server-side group and both clients' group lists.</summary>
public sealed class GroupChatScenario : IPlayerbotScenario
{
    public string Name => "group-chat";

    public string Description => "A invites B, B accepts, party chat reaches B, B leaves";

    public async Task RunAsync(ScenarioContext context)
    {
        (ScenarioBot a, ScenarioBot b) = await PlayerbotScenarioCatalog.PairAsync(context).ConfigureAwait(false);
        await ScenarioSteps.LeaveAnyGroupAsync(context, a, b).ConfigureAwait(false);
        await ScenarioSteps.FormGroupAsync(context, a, b).ConfigureAwait(false);
        await context.StepAsync("party chat reaches B", async () =>
        {
            long mark = b.Mark();
            ScenarioContext.Expect(await a.PartyAsync("scenario party").ConfigureAwait(false), "party chat refused");
            ChatMessageView heard = await b.WaitForPacketAsync(WorldOpcode.SmsgMessagechat, ScenarioDecoders.ChatMessage,
                m => m.Type == ChatType.Party && m.Message == "scenario party", mark).ConfigureAwait(false);
            ScenarioContext.ExpectEqual(a.Guid.Value, heard.Sender, "party chat sender");
        }).ConfigureAwait(false);
        await context.StepAsync("B leaves; the group dissolves", async () =>
        {
            long mark = a.Mark();
            ScenarioContext.Expect(await b.LeaveGroupAsync().ConfigureAwait(false), "leave refused");
            await a.WaitForPacketAsync(WorldOpcode.SmsgGroupList, ScenarioDecoders.GroupList, g => g.IsEmpty, mark).ConfigureAwait(false);
            await context.WaitUntilAsync("no group remains", () => !ScenarioSteps.InGroup(context, a)).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }
}

/// <summary>
/// A trades one <see cref="ItemEntry"/> to B for <see cref="Price"/> copper through the ordinary trade window; both
/// inventories and purses must change exactly.
/// </summary>
public sealed class TradeItemForGoldScenario : IPlayerbotScenario
{
    /// <summary>Linen Cloth (classic item_template 2589).</summary>
    public const uint ItemEntry = 2589;

    public const uint Price = 75;

    public string Name => "trade";

    public string Description => $"A trades item {ItemEntry} to B for {Price} copper";

    public async Task RunAsync(ScenarioContext context)
    {
        (ScenarioBot a, ScenarioBot b) = await PlayerbotScenarioCatalog.PairAsync(context).ConfigureAwait(false);
        Game.ObjectGuid item = await context.StepAsync("give A the item, B the gold", async () =>
        {
            await context.GiveMoneyAsync(b, Price).ConfigureAwait(false);
            return await context.GiveItemAsync(a, ItemEntry).ConfigureAwait(false);
        }).ConfigureAwait(false);
        uint aItems = await a.ReadAsync(p => p.Inventory.GetItemCount(ItemEntry)).ConfigureAwait(false);
        uint bItems = await b.ReadAsync(p => p.Inventory.GetItemCount(ItemEntry)).ConfigureAwait(false);
        uint aMoney = await a.ReadAsync(p => p.Money).ConfigureAwait(false);
        uint bMoney = await b.ReadAsync(p => p.Money).ConfigureAwait(false);

        await ScenarioSteps.OpenTradeAsync(context, a, b).ConfigureAwait(false);
        await context.StepAsync("A offers the item, B offers the gold", async () =>
        {
            ScenarioContext.Expect(await a.SetTradeItemAsync(0, item).ConfigureAwait(false), "set trade item refused");
            ScenarioContext.Expect(await b.SetTradeGoldAsync(Price).ConfigureAwait(false), "set trade gold refused");
            await context.WaitUntilAsync("both offers are on the table", () => ScenarioSteps.TradeOffers(context, a) is (1, Price))
                .ConfigureAwait(false);
        }).ConfigureAwait(false);
        await ScenarioSteps.AcceptTradeAsync(context, a, b).ConfigureAwait(false);
        await context.StepAsync("items and money moved", async () =>
        {
            await context.ExpectItemCountAsync(a, ItemEntry, aItems - 1).ConfigureAwait(false);
            await context.ExpectItemCountAsync(b, ItemEntry, bItems + 1).ConfigureAwait(false);
            await context.ExpectMoneyAsync(a, aMoney + Price).ConfigureAwait(false);
            await context.ExpectMoneyAsync(b, bMoney - Price).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }
}

/// <summary>
/// A challenges B (spell 7266), B accepts, the countdown runs, both fight in melee until the loser is left at 1 health;
/// SMSG_DUEL_COMPLETE and SMSG_DUEL_WINNER must agree with the server's health and duel state.
/// </summary>
public sealed class DuelScenario : IPlayerbotScenario
{
    public string Name => "duel";

    public string Description => "A duels B to completion in melee";

    public async Task RunAsync(ScenarioContext context)
    {
        (ScenarioBot a, ScenarioBot b) = await PlayerbotScenarioCatalog.PairAsync(context).ConfigureAwait(false);
        await context.StepAsync("A knows Duel", () => context.LearnSpellAsync(a, ScenarioBot.DuelSpell)).ConfigureAwait(false);
        await context.StepAsync("both are healthy and out of combat", () => context.WaitUntilAsync("both out of combat",
            () => a.RequirePlayer() is { IsAlive: true } pa && !pa.Combat.IsInCombat
                && b.RequirePlayer() is { IsAlive: true } pb && !pb.Combat.IsInCombat, TimeSpan.FromSeconds(30))).ConfigureAwait(false);
        DuelRequestedView request = await context.StepAsync("A challenges B", async () =>
        {
            long mark = b.Mark();
            ScenarioContext.Expect(await a.RequestDuelAsync(b.Guid).ConfigureAwait(false), "duel cast refused");
            DuelRequestedView view = await b.WaitForPacketAsync(WorldOpcode.SmsgDuelRequested, ScenarioDecoders.DuelRequested,
                since: mark).ConfigureAwait(false);
            ScenarioContext.ExpectEqual(a.Guid.Value, view.Initiator, "duel initiator");
            return view;
        }).ConfigureAwait(false);
        await context.StepAsync("B accepts; the countdown runs out", async () =>
        {
            ScenarioContext.Expect(await b.AcceptDuelAsync(request.Arbiter).ConfigureAwait(false), "duel accept refused");
            await context.WaitUntilAsync("the duel started", () => a.RequirePlayer().DuelTeam != 0 && b.RequirePlayer().DuelTeam != 0,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }).ConfigureAwait(false);
        DuelWinnerView winner = await context.StepAsync("fight to the finish", async () =>
        {
            // Bounded fight: both start from the same low health.
            await context.SetHealthAsync(a, 40).ConfigureAwait(false);
            await context.SetHealthAsync(b, 40).ConfigureAwait(false);
            long mark = a.Mark();
            ScenarioContext.Expect(await a.AttackAsync(b.Guid).ConfigureAwait(false), "A attack refused");
            ScenarioContext.Expect(await b.AttackAsync(a.Guid).ConfigureAwait(false), "B attack refused");
            await a.WaitForPacketAsync(WorldOpcode.SmsgAttackerstateupdate, ScenarioDecoders.AttackerState,
                s => s.Attacker == a.Guid.Value || s.Attacker == b.Guid.Value, mark).ConfigureAwait(false);
            ScenarioContext.Expect(await a.WaitForPacketAsync(WorldOpcode.SmsgDuelComplete, ScenarioDecoders.DuelComplete, since: mark,
                timeout: TimeSpan.FromSeconds(120)).ConfigureAwait(false), "the duel was interrupted, not fought out");
            return await a.WaitForPacketAsync(WorldOpcode.SmsgDuelWinner, ScenarioDecoders.DuelWinner, since: mark).ConfigureAwait(false);
        }).ConfigureAwait(false);
        await context.StepAsync("server state agrees with SMSG_DUEL_WINNER", async () =>
        {
            ScenarioContext.ExpectEqual((byte)0, winner.Reason, "duel winner reason (0 = won)");
            ScenarioBot loser = winner.Loser.Equals(a.Name, StringComparison.OrdinalIgnoreCase) ? a : b;
            ScenarioBot victor = ReferenceEquals(loser, a) ? b : a;
            ScenarioContext.Expect(winner.Winner.Equals(victor.Name, StringComparison.OrdinalIgnoreCase), $"winner {winner.Winner} is not {victor.Name}");
            await context.ExpectAsync(loser, "the loser is alive", p => p.IsAlive).ConfigureAwait(false);
            await context.WaitUntilAsync("the duel state is cleared", () => a.RequirePlayer().DuelArbiter == 0 && b.RequirePlayer().DuelArbiter == 0
                && a.RequirePlayer().DuelTeam == 0 && b.RequirePlayer().DuelTeam == 0).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }
}

/// <summary>Reusable multi-step building blocks for scenarios (each is one or more report steps).</summary>
public static class ScenarioSteps
{
    public static bool InGroup(ScenarioContext context, ScenarioBot bot)
        => context.Services.GetRequiredService<Social.SocialFeature>().Context.Groups.GetGroup(bot.Guid) is not null;

    /// <summary>Leave whatever group the bots are still in (a reused bot may carry one from an earlier run).</summary>
    public static Task LeaveAnyGroupAsync(ScenarioContext context, params ScenarioBot[] bots) => context.StepAsync("leave earlier groups", async () =>
    {
        foreach (ScenarioBot bot in bots)
        {
            if (await context.ReadAsync(() => InGroup(context, bot)).ConfigureAwait(false))
                await bot.LeaveGroupAsync().ConfigureAwait(false);
        }

        await context.WaitUntilAsync("no bot is grouped", () => bots.All(bot => !InGroup(context, bot))).ConfigureAwait(false);
    });

    /// <summary><paramref name="leader"/> invites each member by name; each accepts; every client's SMSG_GROUP_LIST and the server roster agree.</summary>
    public static Task FormGroupAsync(ScenarioContext context, ScenarioBot leader, params ScenarioBot[] members) => context.StepAsync(
        $"{leader.Name} forms a group with {string.Join(", ", members.Select(m => m.Name))}", async () =>
        {
            foreach (ScenarioBot member in members)
            {
                long mark = member.Mark();
                ScenarioContext.Expect(await leader.InviteAsync(member.Name).ConfigureAwait(false), "invite refused");
                string inviter = await member.WaitForPacketAsync(WorldOpcode.SmsgGroupInvite, ScenarioDecoders.GroupInvite, since: mark)
                    .ConfigureAwait(false);
                ScenarioContext.Expect(inviter.Equals(leader.Name, StringComparison.OrdinalIgnoreCase), $"invited by {inviter}");
                long accepted = member.Mark();
                ScenarioContext.Expect(await member.AcceptInviteAsync().ConfigureAwait(false), "accept refused");
                GroupListView list = await member.WaitForPacketAsync(WorldOpcode.SmsgGroupList, ScenarioDecoders.GroupList,
                    g => g.Members.Any(m => m.Guid == leader.Guid.Value), accepted).ConfigureAwait(false);
                ScenarioContext.ExpectEqual(leader.Guid.Value, list.Leader, $"{member.Name}'s group list leader");
            }

            GroupListView own = await leader.WaitForPacketAsync(WorldOpcode.SmsgGroupList, ScenarioDecoders.GroupList,
                g => g.Members.Count == members.Length).ConfigureAwait(false);
            ScenarioContext.Expect(members.All(m => own.Members.Any(x => x.Guid == m.Guid.Value)), "leader's group list misses a member");
            await context.ExpectGroupAsync(leader, members).ConfigureAwait(false);
        });

    /// <summary>A opens a trade with B; B begins it; both windows open.</summary>
    public static Task OpenTradeAsync(ScenarioContext context, ScenarioBot a, ScenarioBot b) => context.StepAsync("open the trade window", async () =>
    {
        long mark = b.Mark();
        long markA = a.Mark();
        ScenarioContext.Expect(await a.InitiateTradeAsync(b.Guid).ConfigureAwait(false), "initiate trade refused");
        TradeStatusView begin = await b.WaitForPacketAsync(WorldOpcode.SmsgTradeStatus, ScenarioDecoders.TradeStatus,
            s => s.Status == TradeStatus.BeginTrade, mark).ConfigureAwait(false);
        ScenarioContext.ExpectEqual(a.Guid.Value, begin.Guid, "trade proposer");
        ScenarioContext.Expect(await b.BeginTradeAsync().ConfigureAwait(false), "begin trade refused");
        await a.WaitForPacketAsync(WorldOpcode.SmsgTradeStatus, ScenarioDecoders.TradeStatus, s => s.Status == TradeStatus.OpenWindow, markA)
            .ConfigureAwait(false);
        await b.WaitForPacketAsync(WorldOpcode.SmsgTradeStatus, ScenarioDecoders.TradeStatus, s => s.Status == TradeStatus.OpenWindow, mark)
            .ConfigureAwait(false);
    });

    /// <summary>(items offered by A, gold offered by the other side) of A's open trade, world thread.</summary>
    public static (int Items, uint Gold) TradeOffers(ScenarioContext context, ScenarioBot a)
    {
        Economy.EconomyFeature economy = context.Services.GetRequiredService<Economy.EconomyFeature>();
        Player player = a.RequirePlayer();
        if (economy.TradeOf(player) is not { } trade) return (-1, 0);
        TradeSide mine = trade.SideOf(player);
        TradeSide theirs = trade.OtherSide(player);
        int items = 0;
        for (int i = 0; i < TradeRules.TradedSlotCount; i++) if (!mine[i].IsEmpty) items++;
        return (items, theirs.Gold);
    }

    /// <summary>
    /// Both accept (after the anti-scam window that bounces an accept right after a change) and the trade completes for both.
    /// </summary>
    public static Task AcceptTradeAsync(ScenarioContext context, ScenarioBot a, ScenarioBot b) => context.StepAsync("both accept; the trade completes", async () =>
    {
        await context.IdleAsync(TimeSpan.FromMilliseconds(1500)).ConfigureAwait(false);
        long markA = a.Mark();
        long markB = b.Mark();
        ScenarioContext.Expect(await a.AcceptTradeAsync().ConfigureAwait(false), "A accept refused");
        await b.WaitForPacketAsync(WorldOpcode.SmsgTradeStatus, ScenarioDecoders.TradeStatus, s => s.Status == TradeStatus.TradeAccept, markB)
            .ConfigureAwait(false);
        ScenarioContext.Expect(await b.AcceptTradeAsync().ConfigureAwait(false), "B accept refused");
        await a.WaitForPacketAsync(WorldOpcode.SmsgTradeStatus, ScenarioDecoders.TradeStatus, s => s.Status == TradeStatus.TradeComplete, markA)
            .ConfigureAwait(false);
        await b.WaitForPacketAsync(WorldOpcode.SmsgTradeStatus, ScenarioDecoders.TradeStatus, s => s.Status == TradeStatus.TradeComplete, markB)
            .ConfigureAwait(false);
    });
}
