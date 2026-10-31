using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Progression;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Progression;
using ArcaneCore.World.Rates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.World.Tests.Rates;

/// <summary>
/// <c>.rates</c> end to end in the in-process daemon: show, set and reset for every rate kind, the effect on the consumers' outcomes, range
/// checks, the permission levels, <c>.reload config</c> clearing the overrides, the change log and event, and <c>.modify xprate</c> left alone.
/// </summary>
public sealed class RateCommandTests
{
    private const string NotAvailable = "This command is not available to you.";

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
            {
                Entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    private static Task SayAsync(WorldTestClient client, string text) => client.SendChatAsync(ChatType.Say, Language.Common, text);

    private static async Task<string> ReadChatUntilAsync(WorldTestClient client, Func<string, bool> match)
    {
        for (int i = 0; i < 120; i++)
        {
            string line = (await client.ReadChatAsync()).Text;
            if (match(line))
            {
                return line;
            }
        }

        throw new InvalidOperationException("no matching chat line");
    }

    private static RateScope Scope(WorldTestHost host) => new(host.WorldServices, host.World, new CapturingLogger());

    public static IEnumerable<object[]> EveryRate() => RateCatalog.All.Select(r => new object[] { r.Name });

    [Theory]
    [MemberData(nameof(EveryRate))]
    public async Task EveryRate_CanBeShownSetAndReset(string name)
    {
        await using var host = WorldTestHost.Start();
        RateDefinition rate = ServerRates.Find(name)!;
        float wanted = name == "Rate.XP.Personal.Min" ? 0.5f : Math.Clamp(1.75f, rate.Min, rate.Max); // Min may not pass Max (1)
        (RateStatus before, RateStatus during, RateStatus after, RateSetResult result) = await host.OnWorldAsync(() =>
        {
            ServerRates rates = ServerRates.For(host.World);
            RateScope scope = Scope(host);
            RateStatus b = rates.Describe(scope, rate);
            RateSetResult r = rates.TrySet(scope, rate, wanted, "Test", null, out _);
            RateStatus d = rates.Describe(scope, rate);
            rates.Reset(scope, rate, "Test");
            return (b, d, rates.Describe(scope, rate), r);
        });

        Assert.True(before.Available, $"{name} is not available in the daemon");
        Assert.Equal(RateSetResult.Ok, result);
        Assert.True(during.Overridden);
        Assert.Equal(wanted, during.Current);
        Assert.Equal(before.Current, during.Configured);
        Assert.False(after.Overridden);
        Assert.Equal(before.Current, after.Current);
    }

    [Fact]
    public void Names_AreUnique_AndFoundByNameAliasAndShortForm()
    {
        Assert.Equal(RateCatalog.All.Count, RateCatalog.All.Select(r => r.Name.ToUpperInvariant()).Distinct().Count());
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RateDefinition rate in RateCatalog.All)
        {
            Assert.StartsWith("Rate.", rate.Name);
            Assert.True(rate.Min >= 0f && rate.Min < rate.Max && rate.Max <= 100f);
            Assert.Same(rate, ServerRates.Find(rate.Name));
            Assert.Same(rate, ServerRates.Find(rate.Name.ToLowerInvariant()));
            Assert.Same(rate, ServerRates.Find(rate.Name[5..]));
            foreach (string alias in rate.Aliases)
            {
                Assert.True(seen.Add(alias), $"alias {alias} is used twice");
                Assert.Same(rate, ServerRates.Find(alias.ToUpperInvariant()));
            }
        }

        Assert.Equal("Rate.XP.Kill", ServerRates.Find("xp")!.Name);
        Assert.Equal("Rate.XP.Quest", ServerRates.Find("xp.quest")!.Name);
        Assert.Equal("Rate.Drop.Money", ServerRates.Find("drop.money")!.Name);
        Assert.Equal("Rate.Drop.Money", ServerRates.Find("money")!.Name);
        Assert.Null(ServerRates.Find("nonsense"));
    }

    [Fact]
    public async Task Show_ListsValueConfiguredValueAndOverride_AndFilters()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await admin.CollectAsync();

        await SayAsync(admin, ".rates set xp 2.5 double weekend");
        Assert.StartsWith("Rate Rate.XP.Kill: 1 -> 2.5 (configured 1;", (await admin.ReadChatAsync()).Text);

        await SayAsync(admin, ".rates show xp.kill");
        Assert.StartsWith("Server rates (2, 1 overridden", (await admin.ReadChatAsync()).Text);
        Assert.Equal("Rate.XP.Kill = 2.5 (OVERRIDDEN, configured 1): kill experience of a normal creature", (await admin.ReadChatAsync()).Text);
        Assert.Equal("Rate.XP.Kill.Elite = 1 (configured): kill experience of an elite creature", (await admin.ReadChatAsync()).Text);

        await SayAsync(admin, ".rates nomatchatall");
        Assert.Contains("no rate matches", (await admin.ReadChatAsync()).Text);

        await SayAsync(admin, ".rates");
        string header = (await admin.ReadChatAsync()).Text;
        Assert.StartsWith($"Server rates ({RateCatalog.All.Count}, 1 overridden", header);
    }

    [Fact]
    public async Task Set_ChangesLiveOptions_ResetRestoresThem()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await admin.CollectAsync();
        ProgressionOptions options = host.WorldServices.GetRequiredService<ProgressionFeature>().Progression.Options;

        await SayAsync(admin, ".rates set Rate.XP.Kill 3");
        await admin.ReadChatAsync();
        Assert.Equal(3f, await host.OnWorldAsync(() => options.RateXpKill));

        await SayAsync(admin, ".rates set RATE.XP.KILL 4");
        Assert.Contains("3 -> 4 (configured 1;", (await admin.ReadChatAsync()).Text);

        await SayAsync(admin, ".rates reset xp");
        Assert.StartsWith("Rate.XP.Kill is back to its configured value 1", (await admin.ReadChatAsync()).Text);
        Assert.Equal(1f, await host.OnWorldAsync(() => options.RateXpKill));

        await SayAsync(admin, ".rates reset xp");
        Assert.Equal("Rate.XP.Kill is not overridden.", (await admin.ReadChatAsync()).Text);

        await SayAsync(admin, ".rates set xp 2");
        await admin.ReadChatAsync();
        await SayAsync(admin, ".rates set money 2");
        await admin.ReadChatAsync();
        await SayAsync(admin, ".rates reset all");
        Assert.Equal("2 rate(s) back to their configured values.", (await admin.ReadChatAsync()).Text);
        Assert.Equal(1f, await host.OnWorldAsync(() => options.RateXpKill));
        await SayAsync(admin, ".rates reset");
        Assert.Equal("No rate is overridden.", (await admin.ReadChatAsync()).Text);
    }

    [Theory]
    [InlineData("xp -1")]
    [InlineData("xp 101")]
    [InlineData("xp nan")]
    [InlineData("xp inf")]
    [InlineData("auction.time 0.05")]
    [InlineData("creature.normal.hp 0")]
    public async Task Set_RejectsInvalidValues_AndChangesNothing(string args)
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await admin.CollectAsync();

        await SayAsync(admin, ".rates set " + args);
        string first = (await admin.ReadChatAsync()).Text;

        Assert.True(first.StartsWith(".rates set:", StringComparison.Ordinal) || first.StartsWith("Syntax: .rates set", StringComparison.Ordinal), first);
        Assert.Equal(0, await host.OnWorldAsync(() => ServerRates.For(host.World).OverrideCount));
    }

    [Theory]
    [InlineData(".rates set nosuchrate 2", "unknown rate")]
    [InlineData(".rates reset nosuchrate", "unknown rate")]
    [InlineData(".rates set xp", "Syntax: .rates set")]
    [InlineData(".rates set xp abc", "Syntax: .rates set")]
    public async Task Set_AndReset_RefuseUnknownNamesAndBadSyntax(string command, string expected)
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await admin.CollectAsync();

        await SayAsync(admin, command);

        Assert.Contains(expected, (await admin.ReadChatAsync()).Text);
        Assert.Equal(0, await host.OnWorldAsync(() => ServerRates.For(host.World).OverrideCount));
    }

    [Fact]
    public async Task GameMaster_MayShow_ButNotSetOrReset()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("GM", "Gm", AccountSecurity.GameMaster);
        await gm.CollectAsync();

        await SayAsync(gm, ".rates show Rate.XP.Quest");
        Assert.StartsWith("Server rates (1, 0 overridden", (await gm.ReadChatAsync()).Text);
        Assert.StartsWith("Rate.XP.Quest = 1 (configured)", (await gm.ReadChatAsync()).Text);

        await SayAsync(gm, ".rates set xp.quest 5");
        Assert.Equal(NotAvailable, (await gm.ReadChatAsync()).Text);
        await SayAsync(gm, ".rates reset all");
        Assert.Equal(NotAvailable, (await gm.ReadChatAsync()).Text);
        Assert.Equal(0, await host.OnWorldAsync(() => ServerRates.For(host.World).OverrideCount));
        Assert.Equal(1f, await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<QuestNpcFeature>().Options.RateXpQuest));
    }

    [Fact]
    public async Task APlayer_CannotUseRates()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient player = await host.EnterWorldAsync("PLAYER", "Plain");
        await player.CollectAsync();

        await SayAsync(player, ".rates set xp 9");
        await player.ReadChatAsync();

        Assert.Equal(0, await host.OnWorldAsync(() => ServerRates.For(host.World).OverrideCount));
        Assert.Equal(1f, await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<ProgressionFeature>().Progression.Options.RateXpKill));
    }

    [Fact]
    public async Task KillExperience_FollowsTheSetRate()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await admin.CollectAsync();
        Player player = await host.PlayerAsync("Admin");
        PlayerProgression progression = host.WorldServices.GetRequiredService<ProgressionFeature>().Progression;
        var template = new CreatureTemplate { Entry = 90, Name = "Victim", Faction = 14, MinLevel = 3, MaxLevel = 3 };
        var victim = new Creature(900100, template, new CreatureSpawn { Guid = 900100, Entry = 90, MapId = 0, X = 0, Y = 0, Z = 83.5f },
            CreatureContent.Empty, new Random(1));
        victim.Level = 3;
        uint Kill() => KillRewards.AwardExperience(progression, [player], victim, nonRaidDungeon: false)[0];

        uint plain = await host.OnWorldAsync(Kill);
        Assert.True(plain > 0);

        await SayAsync(admin, ".rates set xp 4");
        await admin.ReadChatAsync();
        uint boosted = await host.OnWorldAsync(Kill);
        Assert.InRange(boosted, (plain * 4) - 3, (plain * 4) + 3);

        await SayAsync(admin, ".rates reset xp");
        await admin.ReadChatAsync();
        Assert.Equal(plain, await host.OnWorldAsync(Kill));
    }

    [Fact]
    public async Task ItemDropChance_AndMoney_FollowTheSetRates()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await admin.CollectAsync();
        LootOptions options = host.WorldServices.GetRequiredService<GameObjectLootFeature>().Options;
        var content = new LootContent([(LootTableKind.Creature, new LootStoreRow(1, 100, 5.0f, 0, 1, 1))], []);
        var loot = new LootService(content, options, new Random(7))
        {
            Items = new ItemTemplateStore([new ItemTemplate { Entry = 100, Quality = 3 }]),
        };
        int Drops() => Enumerable.Range(0, 2000).Sum(_ => loot.Generator.Roll(LootTableKind.Creature, 1).Count);

        Assert.InRange(await host.OnWorldAsync(Drops), 50, 160);   // about 5 %

        await SayAsync(admin, ".rates set drop.rare 10");
        await admin.ReadChatAsync();
        Assert.InRange(await host.OnWorldAsync(Drops), 900, 1100); // about 50 %

        await SayAsync(admin, ".rates set drop.epic 10"); // another quality: the rare row is untouched
        await admin.ReadChatAsync();
        await SayAsync(admin, ".rates reset drop.rare");
        await admin.ReadChatAsync();
        Assert.InRange(await host.OnWorldAsync(Drops), 50, 160);

        Assert.Equal(100u, LootMoneyRules.Generate(100, 100, options.MoneyRate, new Random(1)));
        await SayAsync(admin, ".rates set money 3");
        await admin.ReadChatAsync();
        Assert.Equal(300u, LootMoneyRules.Generate(100, 100, options.MoneyRate, new Random(1)));
        Assert.Equal(3f, host.WorldServices.GetRequiredService<QuestNpcFeature>().Options.RateDropMoney); // one vmangos key scales both
    }

    private static readonly CreatureTemplate Beast = new()
    {
        Entry = 91, Name = "Beast", Faction = 14, MinLevel = 1, MaxLevel = 1, MinLevelHealth = 100, MaxLevelHealth = 100,
        MinMeleeDamage = 10, MaxMeleeDamage = 20, MinRangedDamage = 4, MaxRangedDamage = 8,
    };

    private static Creature SpawnBeast(WorldTestHost host, Player near)
        => host.WorldServices.GetRequiredService<CreatureWorldFeature>().GetOrCreateSystem(near.Map!).SpawnTemporary(Beast, near.X, near.Y, near.Z, 0);

    [Fact]
    public async Task CreatureDamageRate_IsPushedIntoLiveCreaturesMeleeAndRangedFields_HpOnlyForNewSpawns()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await admin.CollectAsync();
        Player player = await host.PlayerAsync("Admin");
        Creature existing = await host.OnWorldAsync(() => SpawnBeast(host, player));
        (float Min, float Max, float RMin, float RMax) Fields() => (existing.GetFloat(UpdateFields.UnitFieldMindamage), existing.GetFloat(UpdateFields.UnitFieldMaxdamage),
            existing.GetFloat(UpdateFields.UnitFieldMinrangeddamage), existing.GetFloat(UpdateFields.UnitFieldMaxrangeddamage));
        Assert.Equal((10f, 20f, 4f, 8f), await host.OnWorldAsync(Fields));

        await SayAsync(admin, ".rates set creature.normal.damage 2.5");
        await admin.ReadChatAsync();
        await SayAsync(admin, ".rates set creature.normal.hp 3");
        Assert.Contains("Applies to creatures created or respawned afterwards", (await admin.ReadChatAsync()).Text);

        Assert.Equal((25f, 50f, 10f, 20f), await host.OnWorldAsync(Fields));
        Assert.Equal(100u, await host.OnWorldAsync(() => existing.MaxHealth));
        Creature fresh = await host.OnWorldAsync(() => SpawnBeast(host, player));
        Assert.Equal(300u, await host.OnWorldAsync(() => fresh.MaxHealth));
        Assert.Equal(25f, await host.OnWorldAsync(() => fresh.GetFloat(UpdateFields.UnitFieldMindamage)));

        await SayAsync(admin, ".rates reset all");
        await admin.ReadChatAsync();
        Assert.Equal((10f, 20f, 4f, 8f), await host.OnWorldAsync(Fields));
    }

    [Fact]
    public async Task ReloadConfig_RollbackPutsTheOverridesBack()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await admin.CollectAsync();
        ProgressionOptions options = host.WorldServices.GetRequiredService<ProgressionFeature>().Progression.Options;
        await SayAsync(admin, ".rates set xp 6");
        await admin.ReadChatAsync();
        ArcaneCore.Game.Reload.ContentCandidate candidate = await new ArcaneCore.World.Reload.ConfigContentReloadable(host.WorldServices).BuildAsync(CancellationToken.None);

        (float committed, int overridesAfterCommit, float rolledBack, int overridesAfterRollback, IReadOnlyList<Exception> failures) = await host.OnWorldAsync(() =>
        {
            var transaction = new ArcaneCore.Game.Reload.ReloadTransaction();
            candidate.Commit(host.World, transaction);
            float c = options.RateXpKill;
            int o = ServerRates.For(host.World).OverrideCount;
            // A step after the rate step fails in the real reload; the transaction then undoes everything newest first.
            IReadOnlyList<Exception> errors = transaction.Rollback();
            return (c, o, options.RateXpKill, ServerRates.For(host.World).OverrideCount, errors);
        });

        Assert.Equal(1f, committed);
        Assert.Equal(0, overridesAfterCommit);
        Assert.Equal(6f, rolledBack);
        Assert.Equal(1, overridesAfterRollback);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task ResetRestoresATargetStoredAsDouble_Exactly()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await admin.CollectAsync();
        var talents = host.WorldServices.GetRequiredService<ArcaneCore.World.Talents.TalentFeature>().Options;
        await host.OnWorldAsync(() => talents.PointsRate = 1.1);

        await SayAsync(admin, ".rates set talent 2");
        await admin.ReadChatAsync();
        await SayAsync(admin, ".rates reset talent");
        await admin.ReadChatAsync();

        Assert.Equal(1.1, await host.OnWorldAsync(() => talents.PointsRate));
    }

    [Fact]
    public async Task ResettingOneSideOfThePersonalPair_CannotLeaveMinAboveMax()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await admin.CollectAsync();
        ProgressionOptions options = host.WorldServices.GetRequiredService<ProgressionFeature>().Progression.Options;
        await host.OnWorldAsync(() => { options.RateXpPersonalMin = 1; options.RateXpPersonalMax = 10; });

        await SayAsync(admin, ".rates set Rate.XP.Personal.Max 20");
        await admin.ReadChatAsync();
        await SayAsync(admin, ".rates set Rate.XP.Personal.Min 15");
        await admin.ReadChatAsync();

        await SayAsync(admin, ".rates reset Rate.XP.Personal.Max");
        Assert.Contains("would be below Rate.XP.Personal.Min 15", (await admin.ReadChatAsync()).Text);
        Assert.Equal((15f, 20f), await host.OnWorldAsync(() => (options.RateXpPersonalMin, options.RateXpPersonalMax)));

        await SayAsync(admin, ".rates reset all"); // order independent: Min goes first, then Max
        await admin.ReadChatAsync();
        Assert.Equal((1f, 10f), await host.OnWorldAsync(() => (options.RateXpPersonalMin, options.RateXpPersonalMax)));
    }

    [Fact]
    public async Task PersonalMin_AndMax_CannotCross()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await admin.CollectAsync();
        ProgressionOptions options = host.WorldServices.GetRequiredService<ProgressionFeature>().Progression.Options;

        await SayAsync(admin, ".rates set Rate.XP.Personal.Min 2");
        Assert.Contains("would be above Rate.XP.Personal.Max 1", (await admin.ReadChatAsync()).Text);
        await SayAsync(admin, ".rates set Rate.XP.Personal.Max 0.5");
        Assert.Contains("would be below Rate.XP.Personal.Min 1", (await admin.ReadChatAsync()).Text);
        Assert.Equal((1f, 1f), await host.OnWorldAsync(() => (options.RateXpPersonalMin, options.RateXpPersonalMax)));

        await SayAsync(admin, ".rates set Rate.XP.Personal.Max 4");
        await admin.ReadChatAsync();
        await SayAsync(admin, ".rates set Rate.XP.Personal.Min 3");
        Assert.StartsWith("Rate Rate.XP.Personal.Min: 1 -> 3", (await admin.ReadChatAsync()).Text);
    }

    [Fact]
    public async Task ReloadConfig_ClearsTheOverrides()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await admin.CollectAsync();
        ProgressionOptions options = host.WorldServices.GetRequiredService<ProgressionFeature>().Progression.Options;
        await SayAsync(admin, ".rates set xp 6");
        await admin.ReadChatAsync();
        Assert.Equal(6f, await host.OnWorldAsync(() => options.RateXpKill));

        await SayAsync(admin, ".reload config");
        await host.WaitForWorldAsync(() => ServerRates.For(host.World).OverrideCount == 0, "the reload clears the overrides");

        Assert.Equal(1f, await host.OnWorldAsync(() => options.RateXpKill));
        await SayAsync(admin, ".rates show xp.kill");
        await admin.ReadChatAsync();
        Assert.Contains("Rate.XP.Kill = 1 (configured)", await ReadChatUntilAsync(admin, l => l.StartsWith("Rate.XP.Kill =", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task EveryChange_IsLoggedAtInformation_AndPublishedOnce()
    {
        var log = new CapturingLogger();
        await using var host = WorldTestHost.Start(sessionLogger: log);
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await admin.CollectAsync();
        var seen = new List<RateChange>();
        ServerRates rates = ServerRates.For(host.World);
        rates.RateChanged += seen.Add;
        rates.RateChanged += _ => throw new InvalidOperationException("a faulty subscriber");

        await SayAsync(admin, ".rates set xp 2 holiday event");
        Assert.StartsWith("Rate Rate.XP.Kill: 1 -> 2", (await admin.ReadChatAsync()).Text);
        await SayAsync(admin, ".rates reset xp");
        await admin.ReadChatAsync();

        Assert.Collection(seen,
            set =>
            {
                Assert.Equal(("Rate.XP.Kill", 1f, 2f, RateChangeKind.Set, "Admin", "holiday event"), (set.Rate.Name, set.Old, set.New, set.Kind, set.Actor, set.Reason));
            },
            reset =>
            {
                Assert.Equal(("Rate.XP.Kill", 2f, 1f, RateChangeKind.Reset, "Admin", (string?)null), (reset.Rate.Name, reset.Old, reset.New, reset.Kind, reset.Actor, reset.Reason));
            });
        lock (log.Entries)
        {
            Assert.Contains(log.Entries, e => e.Level == LogLevel.Information
                && e.Message == "Rate Rate.XP.Kill Set: 1 -> 2 by Admin (reason: holiday event)");
        }
    }

    [Fact]
    public async Task ModifyXpRate_IsUnaffected_ByTheServerRate_ButFollowsThePersonalLimit()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await using WorldTestClient player = await host.EnterWorldAsync("PLAYER", "Plain");
        await admin.CollectAsync();
        await player.CollectAsync();
        Player plain = await host.PlayerAsync("Plain");

        await SayAsync(admin, ".rates set xp 5");
        await admin.ReadChatAsync();
        Assert.Equal(-1.0f, await host.OnWorldAsync(() => plain.PersonalXpRate));
        await SayAsync(player, ".modify xprate 2");
        Assert.Equal("You can't set XP rate above 1!", (await player.ReadChatAsync()).Text);
        await player.CollectAsync(); // the syntax help that follows a refusal

        await SayAsync(admin, ".rates set Rate.XP.Personal.Max 3");
        await admin.ReadChatAsync();
        await SayAsync(player, ".modify xprate 2");
        Assert.Equal("You have changed your XP rate to 2 times normal experience gain.", (await player.ReadChatAsync()).Text);
        Assert.Equal(2.0f, await host.OnWorldAsync(() => plain.PersonalXpRate));
    }

    [Fact]
    public async Task ADocumentedRate_ForEveryCatalogEntry()
    {
        string docs = await File.ReadAllTextAsync(Path.Combine(RepositorySource.RequireRoot(), "docs", "areas", "rates.md"));
        foreach (RateDefinition rate in RateCatalog.All)
        {
            Assert.Contains(rate.Name, docs);
        }

        Assert.Contains(".rates set", docs);
    }
}
