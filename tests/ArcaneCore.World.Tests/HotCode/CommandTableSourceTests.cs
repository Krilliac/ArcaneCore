using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.HotCode;

/// <summary>Registers the live command table in the end-to-end test host, as <c>AddWorldDaemon</c> does.</summary>
internal sealed class CommandTableSourceTestServices : IWorldTestServices
{
    public void Register(IServiceCollection services)
        => services.AddSingleton(sp => new CommandTableSource(sp.GetRequiredService<CommandTable>()));
}

/// <summary>
/// <see cref="CommandTableSource"/>: the table chat resolves against can gain roots at runtime
/// (a command group added by a code hot reload) without changing how anything that already
/// resolved resolves, and a bad addition leaves the table exactly as it was.
/// </summary>
public sealed class CommandTableSourceTests
{
    private static readonly AccountSecurity[] AllLevels = Enum.GetValues<AccountSecurity>();

    private static ChatCommand Root(string name, AccountSecurity security = AccountSecurity.Player, CommandHandler? handler = null)
        => new(name, security, $"Syntax: .{name}", handler ?? ((_, _) => true));

    private static IEnumerable<string> ProperPrefixes(string name)
        => Enumerable.Range(1, name.Length - 1).Select(length => name[..length]);

    [Fact]
    public void NothingAdded_CurrentIsTheStartupTable_WithTheSameRootsInTheSameOrder()
    {
        CommandTable startup = ChatCommands.CreateTable();
        var source = new CommandTableSource(startup);
        Assert.Same(startup, source.Current);
        Assert.Equal(
            ChatCommands.CreateTable().Roots.Select(c => c.Name),
            source.Current.Roots.Select(c => c.Name));
    }

    [Fact]
    public void AddedRoot_IsReachable_AndTheOldTableIsUntouched()
    {
        CommandTable startup = ChatCommands.CreateTable();
        int before = startup.Roots.Count;
        var source = new CommandTableSource(startup);

        CommandAddResult result = source.TryAdd([Root("hotprobe")]);

        Assert.True(result.Applied);
        Assert.Equal(1, result.Added);
        Assert.Equal("hotprobe", source.Current.Resolve("hotprobe", AccountSecurity.Player)?.Name);
        Assert.Null(startup.Resolve("hotprobe", AccountSecurity.Player));
        Assert.Equal(before, startup.Roots.Count);
        Assert.Equal(before + 1, source.Current.Roots.Count);
        Assert.Equal("hotprobe", source.Current.Roots[^1].Name); // appended last
    }

    [Fact]
    public void AddingAnEmptyBatch_ChangesNothing()
    {
        CommandTable startup = ChatCommands.CreateTable();
        var source = new CommandTableSource(startup);
        CommandAddResult result = source.TryAdd([]);
        Assert.True(result.Applied);
        Assert.Equal(0, result.Added);
        Assert.Same(startup, source.Current);
    }

    [Fact]
    public void ARootThatEqualsOrShadowsAnExistingRoot_IsRejected_ForEveryExistingRootAndPrefix()
    {
        CommandTable startup = ChatCommands.CreateTable();
        var source = new CommandTableSource(startup);
        var failures = new List<string>();
        int probed = 0;

        foreach (ChatCommand existing in startup.Roots)
        {
            // The same name, any case, then every proper prefix of it.
            foreach (string name in new[] { existing.Name, existing.Name.ToUpperInvariant() }.Concat(ProperPrefixes(existing.Name)))
            {
                probed++;
                CommandAddResult result = source.TryAdd([Root(name, AccountSecurity.Administrator)]);
                if (result.Applied || !ReferenceEquals(source.Current, startup))
                {
                    failures.Add($"'{name}' (of '{existing.Name}') was accepted");
                }
            }
        }

        Assert.True(probed > startup.Roots.Count * 2, "the probe must cover prefixes, not just exact names");
        Assert.Empty(failures);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("two words")]
    [InlineData("tab\tname")]
    public void ANameThatCouldNeverBeTyped_IsRejected(string name)
    {
        var source = new CommandTableSource(ChatCommands.CreateTable());
        CommandAddResult result = source.TryAdd([Root(name)]);
        Assert.False(result.Applied);
        Assert.Contains("whitespace", result.Error);
    }

    [Fact]
    public void ABatchWithOneBadRoot_AddsNone_AndKeepsTheOldTable()
    {
        CommandTable startup = ChatCommands.CreateTable();
        var source = new CommandTableSource(startup);

        CommandAddResult result = source.TryAdd([Root("goodroot"), Root("help")]);

        Assert.False(result.Applied);
        Assert.Equal(0, result.Added);
        Assert.Contains("'.help' is defined twice", result.Error);
        Assert.Same(startup, source.Current);
        Assert.Null(source.Current.Resolve("goodroot", AccountSecurity.Player));
    }

    [Fact]
    public void TwoNewRootsWithTheSameName_AreRejected()
    {
        var source = new CommandTableSource(ChatCommands.CreateTable());
        Assert.False(source.TryAdd([Root("twin"), Root("TWIN")]).Applied);
    }

    [Fact]
    public void AnExistingRootThatIsAPrefixOfTheNewOne_IsAllowed_AndStillWinsItsOwnName()
    {
        var source = new CommandTableSource(ChatCommands.CreateTable());
        Assert.True(source.TryAdd([Root("helpful")]).Applied); // "help" is a proper prefix of it
        Assert.Equal("help", source.Current.Resolve("help", AccountSecurity.Player)?.Name);
        Assert.Equal("helpful", source.Current.Resolve("helpf", AccountSecurity.Player)?.Name);
    }

    [Fact]
    public void AddingARoot_ChangesNoExistingInput_AtAnySecurityLevel()
    {
        CommandTable startup = ChatCommands.CreateTable();
        var source = new CommandTableSource(startup);
        string[] inputs = startup.Roots
            .SelectMany(c => new[] { c.Name }.Concat(ProperPrefixes(c.Name)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Dictionary<(string, AccountSecurity), string?> baseline = Resolve(startup, inputs);

        Assert.True(source.TryAdd([Root("zzhotprobe", AccountSecurity.Player), Root("zzhotadmin", AccountSecurity.Administrator)]).Applied);

        Dictionary<(string, AccountSecurity), string?> after = Resolve(source.Current, inputs);
        Assert.Equal(inputs.Length * AllLevels.Length, baseline.Count);
        Assert.Equal(baseline, after);

        static Dictionary<(string, AccountSecurity), string?> Resolve(CommandTable table, string[] words)
            => words.SelectMany(w => AllLevels.Select(level => ((w, level), table.Resolve(w, level)?.Name))).ToDictionary();
    }

    [Fact]
    public async Task ConcurrentReaders_NeverSeeAPartialTable()
    {
        CommandTable startup = ChatCommands.CreateTable();
        string[] baseline = startup.Roots.Select(c => c.Name).ToArray();
        var source = new CommandTableSource(startup);
        const int additions = 300;
        using var stop = new CancellationTokenSource();

        Task[] readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            int last = 0;
            int reads = 0;
            while (!stop.IsCancellationRequested)
            {
                CommandTable table = source.Current;
                IReadOnlyList<ChatCommand> roots = table.Roots;
                Assert.True(roots.Count >= last, "the table only grows");
                last = roots.Count;
                for (int i = 0; i < baseline.Length; i++)
                {
                    Assert.Equal(baseline[i], roots[i].Name);
                }

                // Every added root in a table we hold is fully present: roots 0..n-1, in order.
                for (int i = baseline.Length; i < roots.Count; i++)
                {
                    Assert.Equal($"zhot{i - baseline.Length:D4}", roots[i].Name);
                }

                Assert.NotNull(table.Resolve("help", AccountSecurity.Player));
                reads++;
            }

            return reads;
        })).ToArray();

        for (int i = 0; i < additions; i++)
        {
            Assert.True(source.TryAdd([Root($"zhot{i:D4}")]).Applied);
        }

        await stop.CancelAsync();
        await Task.WhenAll(readers);
        Assert.Equal(baseline.Length + additions, source.Current.Roots.Count);
    }

    [Fact]
    public async Task ARootAddedWhileTheServerRuns_IsUsableFromChat_WithoutARestart()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient player = await host.EnterWorldAsync("HOTCMD", "Hotcmd");
        await player.CollectAsync();

        await player.SendChatAsync(ChatType.Say, Language.Common, ".hotprobe");
        Assert.Equal("There is no such command.", (await player.ReadChatAsync()).Text);

        var source = host.WorldServices.GetRequiredService<CommandTableSource>();
        Assert.True(source.TryAdd([Root("hotprobe", handler: (context, _) =>
        {
            context.Reply("hotprobe: ran");
            return true;
        })]).Applied);

        await player.SendChatAsync(ChatType.Say, Language.Common, ".hotprobe");
        Assert.Equal("hotprobe: ran", (await player.ReadChatAsync()).Text);
        await player.SendChatAsync(ChatType.Say, Language.Common, ".help");
        Assert.EndsWith("server, hotprobe", (await player.ReadChatAsync()).Text);
    }
}
