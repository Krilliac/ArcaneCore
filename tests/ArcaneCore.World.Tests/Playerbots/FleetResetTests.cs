using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Population;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary><c>.playerbot fleet reset</c>: every character of a playerbot account goes, the accounts stay, a real player is never reached.</summary>
public sealed class FleetResetTests
{
    private static Task<PopulationTestHost> StartAsync(Action<PlayerbotOptions>? configure = null)
        => PopulationTestHost.StartAsync(o =>
        {
            o.Population.Enabled = false;
            o.MaxBots = 6;
            configure?.Invoke(o);
        });

    private static async Task<Guid[]> CreateBotsAsync(PopulationTestHost world, int count, int started)
    {
        var ids = new Guid[count];
        for (int i = 0; i < count; i++)
        {
            PlayerbotOperationResult made = await world.Bots.CreateAsync($"Resetbot{(char)('a' + i)}", 1, 1);
            Assert.True(made.Success, made.Code);
            ids[i] = made.BotId!.Value;
        }

        for (int i = 0; i < started; i++) Assert.True((await world.Bots.StartAsync(ids[i].ToString())).Success);
        if (started > 0) await world.WaitForSettledAsync(started);
        return ids;
    }

    private static async Task<(int Account, int Character)> AddOfflineRealCharacterAsync(PopulationTestHost world, string account, string name)
    {
        await world.Host.AddAccountAsync(account);
        Account stored = (await world.Host.Accounts.FindByUsernameAsync(account))!;
        CharacterRecord created = await world.Host.Characters.CreateAsync(new CharacterRecord { AccountId = stored.Id, Name = name, Race = 1, Class = 1, Level = 12 });
        return (stored.Id, created.Id);
    }

    [Fact]
    public async Task Reset_DeletesEveryBotCharacter_KeepsTheAccounts_ClearsRegistryAndMembers_AndLeavesRealPlayersAlone()
    {
        await using PopulationTestHost world = await StartAsync();
        Guid[] ids = await CreateBotsAsync(world, 4, started: 2);
        ManagedPlayerbot[] registry = [.. await world.Owners.LoadAllAsync()];
        foreach (Guid id in ids.Take(3))
            world.Members.Seed(new PlayerbotPopulationMember(id, registry.Single(b => b.BotId == id).CharacterId, 12, 0, 1, 1));

        await using WorldTestClient real = await world.Host.EnterWorldAsync("REALONE", "Realone");
        (int offlineAccount, int offlineCharacter) = await AddOfflineRealCharacterAsync(world, "REALTWO", "Realtwo");
        Account realAccount = (await world.Host.Accounts.FindByUsernameAsync("REALONE"))!;
        CharacterRecord realOnline = (await world.Host.Characters.GetByAccountAsync(realAccount.Id)).Single();

        FleetResetCensus census = await world.Bots.TakeFleetCensusAsync();
        Assert.Null(census.Refusal);
        Assert.Equal((4, 4, 2), (census.Characters, census.Accounts, census.Running));

        var progress = new List<string>();
        FleetResetResult result = await world.Bots.ResetFleetAsync(new FleetResetPromise(4, 4), progress.Add);

        Assert.True(result.Success, result.Code + ": " + result.Message);
        Assert.Equal(("reset", 2, 0, 4, 0, 4, 3), (result.Code, result.Stopped, result.StopFailed, result.CharactersDeleted, result.CharactersFailed,
            result.AccountsKept, result.MembersRemoved));
        foreach (ManagedPlayerbot bot in registry)
        {
            Assert.Empty(await world.Host.Characters.GetByAccountAsync(bot.AccountId));
            Assert.Null(await world.Host.Characters.GetByIdAsync(bot.CharacterId));
            Account kept = (await world.Host.Accounts.FindByUsernameAsync(bot.AccountName))!;
            Assert.Equal(bot.AccountId, kept.Id);
        }

        Assert.Empty(await world.Owners.LoadAllAsync());
        Assert.Empty(world.Members.Snapshot);
        Assert.Empty(world.Bots.Snapshot());
        Assert.Equal(0, world.RunningCount);
        Assert.Contains(progress, line => line.StartsWith("Fleet reset started: 4 characters on 4 bot accounts, 2 running", StringComparison.Ordinal));

        // The real players: the same rows, and the online one still plays.
        Assert.Equal(realOnline.Id, (await world.Host.Characters.GetByIdAsync(realOnline.Id))!.Id);
        Assert.Equal("Realtwo", (await world.Host.Characters.GetByIdAsync(offlineCharacter))!.Name);
        Assert.Equal(offlineAccount, (await world.Host.Characters.GetByAccountAsync(offlineAccount)).Single().AccountId);
        Assert.True(await world.Host.World.InvokeAsync(() => world.Host.World.FindOnlinePlayer("Realone") is { IsInWorld: true }));
    }

    [Fact]
    public async Task Reset_WhenARegistryRowNamesARealPlayersCharacter_RefusesAndTouchesNothing()
    {
        await using PopulationTestHost world = await StartAsync();
        Guid[] ids = await CreateBotsAsync(world, 2, started: 1);
        (_, int realCharacter) = await AddOfflineRealCharacterAsync(world, "REALTHREE", "Realthree");
        world.Owners.ChangeOutOfBand(ids[1], bot => bot with { CharacterId = realCharacter });

        FleetResetCensus census = await world.Bots.TakeFleetCensusAsync();
        Assert.Contains("not of its own bot account", census.Refusal);

        FleetResetResult result = await world.Bots.ResetFleetAsync(new FleetResetPromise(census.Characters, census.Accounts), _ => { });

        Assert.False(result.Success);
        Assert.Equal("census-refused", result.Code);
        Assert.NotNull(await world.Host.Characters.GetByIdAsync(realCharacter));
        Assert.Equal(1, world.RunningCount); // nothing was even stopped
        Assert.Equal(2, (await world.Owners.LoadAllAsync()).Count);
    }

    [Fact]
    public async Task Reset_WhenTheRegistryNamesAnAccountTheStoreDoesNotConfirm_Refuses()
    {
        await using PopulationTestHost world = await StartAsync();
        Guid[] ids = await CreateBotsAsync(world, 1, started: 0);
        (int realAccount, int realCharacter) = await AddOfflineRealCharacterAsync(world, "REALFOUR", "Realfour");
        // A corrupt row: a bot's id and account id, but the name of a real player's account.
        world.Owners.ChangeOutOfBand(ids[0], bot => bot with { AccountName = "REALFOUR" });

        FleetResetCensus census = await world.Bots.TakeFleetCensusAsync();

        Assert.Contains("does not confirm", census.Refusal);
        Assert.True((await world.Bots.ResetFleetAsync(new FleetResetPromise(1, 1), _ => { })).Code == "census-refused");
        Assert.NotNull(await world.Host.Characters.GetByIdAsync(realCharacter));
        Assert.Single(await world.Host.Characters.GetByAccountAsync(realAccount));
    }

    [Fact]
    public async Task Reset_RefusesABotAccountWithALoginKey_AndARealSessionOnABotAccount()
    {
        await using PopulationTestHost world = await StartAsync();
        Guid[] ids = await CreateBotsAsync(world, 1, started: 0);
        ManagedPlayerbot bot = (await world.Owners.LoadAllAsync()).Single();
        Account account = (await world.Host.Accounts.FindByUsernameAsync(bot.AccountName))!;
        account.SessionKey = new byte[40]; // somebody authenticated as this account

        FleetResetCensus census = await world.Bots.TakeFleetCensusAsync();

        Assert.Contains("a session key is issued", census.Refusal);
        Assert.NotNull(await world.Host.Characters.GetByIdAsync(bot.CharacterId));
        Assert.Equal("census-refused", (await world.Bots.ResetFleetAsync(new FleetResetPromise(1, 1), _ => { })).Code);
        Assert.Single(ids);
    }

    [Fact]
    public async Task Reset_WhenTheFleetChangedSinceThePreview_RefusesAndStopsNothing()
    {
        await using PopulationTestHost world = await StartAsync();
        await CreateBotsAsync(world, 2, started: 1);

        FleetResetResult result = await world.Bots.ResetFleetAsync(new FleetResetPromise(1, 1), _ => { });

        Assert.Equal("fleet-changed", result.Code);
        Assert.Equal(1, world.RunningCount);
        Assert.Equal(2, (await world.Owners.LoadAllAsync()).Count);
    }

    [Fact]
    public async Task WhileTheResetRuns_NoBotCanBeCreatedOrStarted()
    {
        await using PopulationTestHost world = await StartAsync();
        Guid[] ids = await CreateBotsAsync(world, 1, started: 0);
        TaskCompletionSource held = world.Owners.HoldNextLoadAll();

        Task<FleetResetResult> reset = world.Bots.ResetFleetAsync(new FleetResetPromise(1, 1), _ => { });
        await held.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(world.Bots.ResetInProgress);
        Assert.Equal("fleet-reset-running", (await world.Bots.CreateAsync("Latecomer", 1, 1)).Code);
        Assert.Equal("fleet-reset-running", (await world.Bots.StartAsync(ids[0].ToString())).Code);
        Assert.Equal("reset-running", (await world.Bots.ResetFleetAsync(new FleetResetPromise(1, 1), _ => { })).Code);

        world.Owners.ReleaseHeldLoadAll();
        Assert.True((await reset).Success);
        Assert.False(world.Bots.ResetInProgress);
    }

    [Fact]
    public async Task TheCommand_PrintsTheCensusAndACode_ThenRunsOnTheCode_AndTheCodeExpires()
    {
        await using PopulationTestHost world = await StartAsync();
        await CreateBotsAsync(world, 3, started: 2);
        var clock = new FakeClock();
        world.Bots.ResetClock = clock;
        await using WorldTestClient admin = await world.Host.EnterWorldAsync("RESETADMIN", "Resetadmin", AccountSecurity.Administrator);
        await admin.CollectAsync();

        string line = await SayAsync(admin, ".playerbot fleet reset", l => l.StartsWith("Playerbot fleet reset would", StringComparison.Ordinal));
        Assert.Equal("Playerbot fleet reset would stop 2 running bots and delete 3 bot characters on 3 bot accounts. The emptied bot accounts are kept and reused for new bots; real players' characters are not touched.", line);
        string proceed = await ReadLineAsync(admin, l => l.StartsWith("To proceed within 60 seconds:", StringComparison.Ordinal));
        string code = proceed[(proceed.LastIndexOf(' ') + 1)..];
        Assert.Equal(6, code.Length);

        // Nothing happened yet, and a wrong code changes nothing.
        Assert.Equal(3, (await world.Owners.LoadAllAsync()).Count);
        await SayAsync(admin, ".playerbot fleet reset confirm AAAAAA", l => l.Contains("wrong confirm code", StringComparison.Ordinal));
        Assert.Equal(3, (await world.Owners.LoadAllAsync()).Count);

        // The code expires after 60 seconds.
        clock.Advance(TimeSpan.FromSeconds(61));
        await SayAsync(admin, $".playerbot fleet reset confirm {code}", l => l.Contains("has expired", StringComparison.Ordinal));
        Assert.Equal(3, (await world.Owners.LoadAllAsync()).Count);
        Assert.Equal(2, world.RunningCount);

        // A fresh code works, once.
        await SayAsync(admin, ".playerbot fleet reset", l => l.StartsWith("Playerbot fleet reset would", StringComparison.Ordinal));
        proceed = await ReadLineAsync(admin, l => l.StartsWith("To proceed within 60 seconds:", StringComparison.Ordinal));
        code = proceed[(proceed.LastIndexOf(' ') + 1)..];
        string done = await SayAsync(admin, $".playerbot fleet reset confirm {code}", l => l.StartsWith("Playerbot fleet reset done:", StringComparison.Ordinal));
        Assert.StartsWith("Playerbot fleet reset done: 3 characters deleted (0 failed), 2 bots stopped (0 failed)", done);
        Assert.Empty(await world.Owners.LoadAllAsync());
        Assert.Equal(0, world.RunningCount);
        await SayAsync(admin, $".playerbot fleet reset confirm {code}", l => l.Contains("nothing to confirm", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheCommand_IsForAdministratorsOnly()
    {
        await using PopulationTestHost world = await StartAsync();
        await CreateBotsAsync(world, 1, started: 0);
        await using WorldTestClient player = await world.Host.EnterWorldAsync("RESETPLAYER", "Resetplayer");
        await player.CollectAsync();

        await player.SendChatAsync(ChatType.Say, Language.Common, ".playerbot fleet reset");
        await player.ReadChatAsync();

        Assert.False(world.Bots.ResetConfirmation.HasPending);
        Assert.Single(await world.Owners.LoadAllAsync());
    }

    [Fact]
    public async Task Reset_RemovesTheBotFromARealPlayersGroup()
    {
        await using PopulationTestHost world = await StartAsync();
        Guid[] ids = await CreateBotsAsync(world, 1, started: 1);
        await using WorldTestClient real = await world.Host.EnterWorldAsync("REALGROUP", "Realgroup");
        Account realAccount = (await world.Host.Accounts.FindByUsernameAsync("REALGROUP"))!;

        PlayerbotOperationResult invited = await world.Host.World.InvokeAsync(() =>
            world.Bots.InviteToGroup(world.Host.World.FindOnlinePlayer("Realgroup")!, ids[0].ToString()));
        Assert.True(invited.Success, invited.Code);
        FleetResetCensus census = await world.Bots.TakeFleetCensusAsync();
        Assert.Equal(1, census.GroupedWithPlayers);

        FleetResetResult result = await world.Bots.ResetFleetAsync(new FleetResetPromise(1, 1), _ => { });

        Assert.True(result.Success, result.Code + ": " + result.Message);
        Assert.Equal(1, result.GroupedWithPlayers);
        Assert.Null(await world.Host.World.InvokeAsync(() =>
            world.WorldGroups()?.GetGroup(world.Host.World.FindOnlinePlayer("Realgroup")!.Guid)));
        Assert.Single(await world.Host.Characters.GetByAccountAsync(realAccount.Id));
    }

    [Fact]
    public async Task Reset_WhenABotAccountHoldsAnotherCharacter_RefusesAndDeletesNothing()
    {
        await using PopulationTestHost world = await StartAsync();
        await CreateBotsAsync(world, 2, started: 1);
        ManagedPlayerbot[] registry = [.. await world.Owners.LoadAllAsync()];
        CharacterRecord human = await world.Host.Characters.CreateAsync(
            new CharacterRecord { AccountId = registry[0].AccountId, Name = "Humanalt", Race = 1, Class = 1, Level = 30 });

        FleetResetCensus census = await world.Bots.TakeFleetCensusAsync();
        Assert.Contains($"character {human.Id}", census.Refusal);
        Assert.Contains("not that bot's registry character", census.Refusal);
        FleetResetResult result = await world.Bots.ResetFleetAsync(new FleetResetPromise(census.Characters, census.Accounts), _ => { });

        Assert.Equal(("census-refused", false), (result.Code, result.Success));
        Assert.NotNull(await world.Host.Characters.GetByIdAsync(human.Id));
        foreach (ManagedPlayerbot bot in registry) Assert.NotNull(await world.Host.Characters.GetByIdAsync(bot.CharacterId));
        Assert.Equal(1, world.RunningCount); // nothing was even stopped
        Assert.Equal(2, (await world.Owners.LoadAllAsync()).Count);
    }

    [Fact]
    public async Task Reset_WhenACharacterAppearsOnABotAccountAfterTheCensus_KeepsThatAccountsCharacters_AndResetsTheRest()
    {
        await using PopulationTestHost world = await StartAsync();
        await CreateBotsAsync(world, 2, started: 0);
        ManagedPlayerbot[] registry = [.. await world.Owners.LoadAllAsync()];
        ManagedPlayerbot tainted = registry[0], clean = registry[1];
        CharacterRecord? human = null;

        // The census has passed when the first progress line is written: a character shows up on one bot account now.
        FleetResetResult result = await world.Bots.ResetFleetAsync(new FleetResetPromise(2, 2), line =>
        {
            human ??= world.Host.Characters.CreateAsync(
                new CharacterRecord { AccountId = tainted.AccountId, Name = "Latealt", Race = 1, Class = 1, Level = 30 }).GetAwaiter().GetResult();
        });

        Assert.False(result.Success);
        Assert.Equal("reset-incomplete", result.Code);
        Assert.NotNull(human);
        Assert.NotNull(await world.Host.Characters.GetByIdAsync(human!.Id));
        Assert.NotNull(await world.Host.Characters.GetByIdAsync(tainted.CharacterId));
        Assert.Null(await world.Host.Characters.GetByIdAsync(clean.CharacterId));
        Assert.Single(await world.Owners.LoadAllAsync());
    }

    private static ArcaneCore.Game.Guilds.GuildManager GuildsOf(PopulationTestHost world)
        => world.Host.WorldServices.GetRequiredService<ArcaneCore.World.Social.SocialFeature>().Context.Guilds;

    // A running bot and an online real player, and the ids of both characters.
    private static async Task<(Guid Bot, uint BotCharacter, WorldTestClient Real, uint RealCharacter)> BotAndRealAsync(PopulationTestHost world)
    {
        Guid[] ids = await CreateBotsAsync(world, 1, started: 1);
        await world.Host.WorldServices.GetRequiredService<ArcaneCore.World.Social.SocialFeature>().GuildsLoaded.WaitAsync(TimeSpan.FromSeconds(10));
        uint botCharacter = (uint)(await world.Owners.LoadAllAsync()).Single().CharacterId;
        WorldTestClient real = await world.Host.EnterWorldAsync("REALGUILD", "Realguild");
        uint realCharacter = await world.Host.World.InvokeAsync(() => world.Host.World.FindOnlinePlayer("Realguild")!.Guid.Low);
        return (ids[0], botCharacter, real, realCharacter);
    }

    [Fact]
    public async Task Reset_WhenTheBotLeadsAGuildWithARealMember_TheRealMemberKeepsTheGuildAndLeadsIt()
    {
        await using PopulationTestHost world = await StartAsync();
        (_, uint bot, WorldTestClient real, uint realId) = await BotAndRealAsync(world);
        await using WorldTestClient keep = real;
        await world.Host.World.InvokeAsync(() =>
        {
            Assert.Equal(ArcaneCore.Game.Guilds.GuildAdminResult.Ok, GuildsOf(world).Create(bot, "Botled Wolves", out _));
            Assert.Equal(ArcaneCore.Game.Guilds.GuildAdminResult.Ok, GuildsOf(world).AdminInvite(realId, "Botled Wolves"));
            return true;
        });

        FleetResetResult result = await world.Bots.ResetFleetAsync(new FleetResetPromise(1, 1), _ => { });

        Assert.True(result.Success, result.Code + ": " + result.Message);
        Assert.Equal(1, result.GuildsReleased);
        (string? name, uint leader, int members, bool botIn) = await world.Host.World.InvokeAsync(() =>
        {
            var guild = GuildsOf(world).GetGuildOf(realId);
            return (guild?.Name, guild?.LeaderId ?? 0u, guild?.Members.Count ?? 0, GuildsOf(world).GetGuildOf(bot) is not null);
        });
        Assert.Equal(("Botled Wolves", realId, 1, false), (name, leader, members, botIn));
        Assert.Null(await world.Host.Characters.GetByIdAsync((int)bot));
        Assert.NotNull(await world.Host.Characters.GetByIdAsync((int)realId));
    }

    [Fact]
    public async Task Reset_WhenTheBotIsAMemberOfARealPlayersGuild_TheGuildStaysWithoutTheBot()
    {
        await using PopulationTestHost world = await StartAsync();
        (_, uint bot, WorldTestClient real, uint realId) = await BotAndRealAsync(world);
        await using WorldTestClient keep = real;
        await world.Host.World.InvokeAsync(() =>
        {
            Assert.Equal(ArcaneCore.Game.Guilds.GuildAdminResult.Ok, GuildsOf(world).Create(realId, "Real Ravens", out _));
            Assert.Equal(ArcaneCore.Game.Guilds.GuildAdminResult.Ok, GuildsOf(world).AdminInvite(bot, "Real Ravens"));
            return true;
        });

        FleetResetResult result = await world.Bots.ResetFleetAsync(new FleetResetPromise(1, 1), _ => { });

        Assert.True(result.Success, result.Code + ": " + result.Message);
        Assert.Equal(0, result.GuildsReleased); // it led nothing
        (uint leader, int members, bool botIn, string name) = await world.Host.World.InvokeAsync(() =>
        {
            var guild = GuildsOf(world).GetGuildOf(realId)!;
            return (guild.LeaderId, guild.Members.Count, GuildsOf(world).GetGuildOf(bot) is not null, guild.Name);
        });
        Assert.Equal((realId, 1, false, "Real Ravens"), (leader, members, botIn, name));
    }

    [Fact]
    public async Task Reset_WhenTheBotIsTheOnlyMemberOfItsGuild_TheGuildIsDisbanded()
    {
        await using PopulationTestHost world = await StartAsync();
        (_, uint bot, WorldTestClient real, _) = await BotAndRealAsync(world);
        await using WorldTestClient keep = real;
        await world.Host.World.InvokeAsync(() =>
        {
            Assert.Equal(ArcaneCore.Game.Guilds.GuildAdminResult.Ok, GuildsOf(world).Create(bot, "Lonely Bots", out _));
            return true;
        });

        FleetResetResult result = await world.Bots.ResetFleetAsync(new FleetResetPromise(1, 1), _ => { });

        Assert.True(result.Success, result.Code + ": " + result.Message);
        Assert.Equal(1, result.GuildsReleased);
        Assert.Null(await world.Host.World.InvokeAsync(() => GuildsOf(world).GetByName("Lonely Bots")));
    }

    private sealed class RefusingDeleteHook : ArcaneCore.World.Characters.ICharacterDeleteHook
    {
        public Task OnCharacterDeletingAsync(ArcaneCore.World.Net.WorldSession session, CharacterRecord character)
            => throw new InvalidOperationException("fixture refuses the deletion");
    }

    [Fact]
    public async Task Reset_WhenTheDeletionIsRefusedAfterTheHandOver_TheBotKeepsItsCharacterButLeftTheGuild_AndItIsReportedAsFailed()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o =>
        {
            o.Population.Enabled = false;
            o.MaxBots = 6;
        }, configureServices: services => services.AddSingleton<ArcaneCore.World.Characters.ICharacterDeleteHook, RefusingDeleteHook>());
        (_, uint bot, WorldTestClient real, uint realId) = await BotAndRealAsync(world);
        await using WorldTestClient keep = real;
        await world.Host.World.InvokeAsync(() =>
        {
            Assert.Equal(ArcaneCore.Game.Guilds.GuildAdminResult.Ok, GuildsOf(world).Create(bot, "Refused Wolves", out _));
            Assert.Equal(ArcaneCore.Game.Guilds.GuildAdminResult.Ok, GuildsOf(world).AdminInvite(realId, "Refused Wolves"));
            return true;
        });

        FleetResetResult result = await world.Bots.ResetFleetAsync(new FleetResetPromise(1, 1), _ => { });

        Assert.Equal(("reset-incomplete", 1, 0), (result.Code, result.CharactersFailed, result.CharactersDeleted));
        Assert.NotNull(await world.Host.Characters.GetByIdAsync((int)bot));
        Assert.Single(await world.Owners.LoadAllAsync()); // the registry row stays with the character
        (uint leader, bool botIn) = await world.Host.World.InvokeAsync(() =>
            (GuildsOf(world).GetGuildOf(realId)!.LeaderId, GuildsOf(world).GetGuildOf(bot) is not null));
        Assert.Equal((realId, false), (leader, botIn)); // the real player's guild is whole either way
    }

    [Fact]
    public async Task Reset_CancelsABotsTradeWithARealPlayer_AndTouchesNeitherItemsNorGold()
    {
        var content = new ArcaneCore.World.Tests.Items.ItemTestContent();
        content.Templates.Templates.Add(new ArcaneCore.Kernel.Items.ItemTemplate { Entry = 9001, Class = 15, Name = "Trade Token", DisplayId = 1, Quality = 1, MaxCount = 20 });
        Task<PopulationTestHost> starting;
        using (content.Use()) starting = StartAsync();
        await using PopulationTestHost world = await starting;
        Guid[] ids = await CreateBotsAsync(world, 1, started: 1);
        await using WorldTestClient real = await world.Host.EnterWorldAsync("REALTRADE", "Realtrade");
        var economy = world.Host.WorldServices.GetRequiredService<ArcaneCore.World.Economy.EconomyFeature>();
        (uint[] itemsBefore, uint goldBefore) = await world.Host.World.InvokeAsync(() =>
        {
            var me = world.Host.World.FindOnlinePlayer("Realtrade")!;
            me.Money = 5000;
            var bot = world.Bots.FindSession(ids[0])!.Player!;
            // The economy store is not wired in this host (Enabled is false, so InitiateTrade refuses): seed the open trade the way
            // InitiateTrade and BeginTrade leave it.
            var trades = (System.Collections.IDictionary)typeof(ArcaneCore.World.Economy.EconomyFeature)
                .GetField("_trades", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(economy)!;
            var trade = new ArcaneCore.Game.Economy.TradeSession(bot, me) { Opened = true };
            trade.SideOf(me).Gold = 1000;
            Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, me.Inventory.AddItem(9001, 5, out ArcaneCore.Game.Items.Item? offered));
            trade.SideOf(me)[0] = offered!.Guid;
            trades[bot] = trade;
            trades[me] = trade;
            Assert.NotNull(economy.TradeOf(me));
            return (me.Inventory.AllItems.Select(i => i.Guid.Low).Order().ToArray(), me.Money);
        });

        FleetResetCensus census = await world.Bots.TakeFleetCensusAsync();
        Assert.Equal(1, census.TradingWithPlayers);
        FleetResetResult result = await world.Bots.ResetFleetAsync(new FleetResetPromise(1, 1), _ => { });

        Assert.True(result.Success, result.Code + ": " + result.Message);
        Assert.Equal(1, result.TradesCancelled);
        (bool open, uint gold, uint[] items) = await world.Host.World.InvokeAsync(() =>
        {
            var me = world.Host.World.FindOnlinePlayer("Realtrade")!;
            return (economy.TradeOf(me) is not null, me.Money, me.Inventory.AllItems.Select(i => i.Guid.Low).Order().ToArray());
        });
        Assert.False(open);
        Assert.True(items.Length > 0); // the offered item is still carried by its owner
        Assert.Equal(goldBefore, gold);
        Assert.Equal(itemsBefore, items);
    }

    [Fact]
    public async Task AfterAReset_NewBotsReuseTheEmptiedAccounts_ThenGetFreshOnes()
    {
        await using PopulationTestHost world = await StartAsync();
        await CreateBotsAsync(world, 2, started: 0);
        HashSet<string> old = [.. (await world.Owners.LoadAllAsync()).Select(b => b.AccountName)];
        Assert.True((await world.Bots.ResetFleetAsync(new FleetResetPromise(2, 2), _ => { })).Success);

        Guid[] reused = await CreateBotsAsync(world, 2, started: 0);
        ManagedPlayerbot[] now = [.. await world.Owners.LoadAllAsync()];
        Assert.Equal(old.Order(), now.Select(b => b.AccountName).Order());
        Assert.All(now, b => Assert.Single(world.Host.Characters.GetByAccountAsync(b.AccountId).GetAwaiter().GetResult()));

        PlayerbotOperationResult third = await world.Bots.CreateAsync("Resetbotz", 1, 1);
        Assert.True(third.Success, third.Code);
        Assert.DoesNotContain((await world.Owners.LoadAllAsync()).Single(b => b.BotId == third.BotId).AccountName, old);
        Assert.Equal(2, reused.Length);
    }

    [Fact]
    public async Task Reset_WhenTheFleetSwappedACharacterSinceThePreview_RefusesEvenWithTheSameCounts()
    {
        await using PopulationTestHost world = await StartAsync();
        await CreateBotsAsync(world, 2, started: 0);
        FleetResetCensus census = await world.Bots.TakeFleetCensusAsync();
        var promise = new FleetResetPromise(census.Characters, census.Accounts, [.. census.CharacterIds.Select(id => id + 1000)]);

        FleetResetResult result = await world.Bots.ResetFleetAsync(promise, _ => { });

        Assert.Equal("fleet-changed", result.Code);
        Assert.Contains("2 new ones appeared", result.Message);
        Assert.Equal(2, (await world.Owners.LoadAllAsync()).Count);
    }

    [Fact]
    public async Task ThePreview_WarnsWhenThePopulationWouldRefillTheFleetWithBoostedBots()
    {
        await using PopulationTestHost world = await StartAsync(o => { o.Population.Enabled = true; o.Population.FreshLaunch = false; });
        Assert.Contains("Turn FreshLaunch on", world.Bots.RefillWarning);
        world.Population.FreshLaunch = true;
        Assert.Null(world.Bots.RefillWarning);
        world.Population.FreshLaunch = false;
        world.Population.Enabled = false;
        Assert.Null(world.Bots.RefillWarning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AReusedAccount_WhoseRegistryInsertFails_LeavesNoCharacterBehind_AndStaysReusable(bool cancel)
    {
        await using PopulationTestHost world = await StartAsync();
        await CreateBotsAsync(world, 1, started: 0);
        ManagedPlayerbot old = (await world.Owners.LoadAllAsync()).Single();
        Assert.True((await world.Bots.ResetFleetAsync(new FleetResetPromise(1, 1), _ => { })).Success);

        world.Owners.NextCreateException = cancel ? new OperationCanceledException() : new InvalidOperationException("fixture registry failure");
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => world.Bots.CreateAsync("Sparefail", 1, 1));
        else Assert.False((await world.Bots.CreateAsync("Sparefail", 1, 1)).Success);

        Assert.Empty(await world.Host.Characters.GetByAccountAsync(old.AccountId)); // no orphan: a character the registry does not know
        Assert.Empty(await world.Owners.LoadAllAsync());
        PlayerbotOperationResult again = await world.Bots.CreateAsync("Sparegood", 1, 1);
        Assert.True(again.Success, again.Code);
        Assert.Equal(old.AccountName, (await world.Owners.LoadAllAsync()).Single().AccountName);
    }

    private sealed class FakeClock : TimeProvider
    {
        private long _ticks = DateTimeOffset.UtcNow.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    private static async Task<string> SayAsync(WorldTestClient admin, string command, Func<string, bool> reply)
    {
        await admin.SendChatAsync(ChatType.Say, Language.Common, command);
        return await ReadLineAsync(admin, reply);
    }

    private static async Task<string> ReadLineAsync(WorldTestClient admin, Func<string, bool> reply)
    {
        while (true)
        {
            string line = (await admin.ReadChatAsync()).Text;
            if (reply(line)) return line;
        }
    }
}

internal static class FleetResetTestExtensions
{
    public static ArcaneCore.Game.Groups.GroupManager? WorldGroups(this PopulationTestHost world)
        => world.Host.WorldServices.GetService<ArcaneCore.World.Social.SocialFeature>()?.Context.Groups;
}
