using ArcaneCore.Data.ClientData;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.Reputation;
using ArcaneCore.Data.Social;
using ArcaneCore.Game.Channels;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Economy;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.ClientData;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Net;
using ArcaneCore.World.Ops.Cli;
using ArcaneCore.World.Tests.Docs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.ClientData;

/// <summary>
/// <c>ClientData:DbcDirectory</c> in the world daemon (docs/areas/client-data.md): every DBC path key of the options classes is a
/// registered consumer, the directory fills unset keys before the features bind, the problems reach <c>check-config</c> (fatal with
/// <c>ClientData:Strict</c>), and the <c>.arcane dbc</c> commands are ranked like the other operator commands.
/// </summary>
public sealed class ClientDataWorldTests
{
    private static string TempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "arcane-clientdata-world-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>A three-row WDBC with the reference layout of every consumer file.</summary>
    private static string WriteConsumerFiles(string directory)
    {
        foreach (string file in ClientDbcConsumers.Files)
        {
            ClientDbcLayout layout = ClientDbcLayouts.Find(file)!;
            using var writer = new BinaryWriter(File.Create(Path.Combine(directory, file)));
            writer.Write(0x43424457u);
            writer.Write(3u);
            writer.Write((uint)layout.Fields);
            writer.Write((uint)layout.RecordSize);
            writer.Write(1u);
            for (uint id = 1; id <= 3; id++)
            {
                byte[] record = new byte[layout.RecordSize];
                BitConverter.GetBytes(id).CopyTo(record, 0);
                writer.Write(record);
            }

            writer.Write((byte)0);
        }

        return directory;
    }

    [Fact]
    public void EveryDbcPathKeyOfTheOptionsClasses_IsARegisteredConsumer()
    {
        ConfigCatalogResult catalog = ConfigCatalog.Build(
            ConfigExceptionTable.Sections(), (_, _) => "doc.", new Dictionary<string, string>(), ConfigExceptionTable.SkippedProperties, _ => null);
        string[] keys = [.. catalog.Entries.Select(e => e.Path).Concat(ConfigExceptionTable.AdHocKeys.Select(k => k.Path))
            .Where(p => p.EndsWith("DbcPath", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];

        Assert.Equal(keys, ClientDbcConsumers.All.Select(c => c.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Apply_FillsUnsetKeysAfterEveryOtherSource_SoTheFeaturesBindTheDirectoryFile_AndAnExplicitKeyWins()
    {
        string directory = WriteConsumerFiles(TempDirectory());
        try
        {
            var configuration = new ConfigurationManager();
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ClientData:DbcDirectory"] = directory,
                ["Talents:TalentDbcPath"] = Path.Combine(directory, "Talent.dbc"),
                ["Reputation:FactionDbcPath"] = "",
            });

            ClientDataReport report = ClientDataStartup.Apply(configuration);

            var combat = new CombatOptions();
            configuration.GetSection(CombatOptions.SectionName).Bind(combat);
            Assert.Equal(Path.Combine(directory, "SpellShapeshiftForm.dbc"), combat.ShapeshiftFormDbcPath);
            Assert.Equal(Path.Combine(directory, "Faction.dbc"), configuration["Reputation:FactionDbcPath"]);
            Assert.Equal(Path.Combine(directory, "TalentTab.dbc"), configuration["Talents:TalentTabDbcPath"]);
            Assert.Equal(ClientDbcSource.Explicit, report.Resolutions.Single(r => r.Consumer.Key == "Talents:TalentDbcPath").Source);
            Assert.Empty(report.Problems);
            Assert.Empty(new ClientDataConfigChecks().Check(configuration));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AProblem_IsACheckConfigWarning_AndAnErrorWithStrict()
    {
        string directory = WriteConsumerFiles(TempDirectory());
        try
        {
            File.Delete(Path.Combine(directory, "SpellShapeshiftForm.dbc"));
            IConfiguration lax = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ClientData:DbcDirectory"] = directory }).Build();
            IConfiguration strict = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ClientData:DbcDirectory"] = directory,
                ["ClientData:Strict"] = "true",
            }).Build();

            ConfigIssue warning = Assert.Single(OpsCli.Validate(lax).Issues, i => i.Key == "Combat:ShapeshiftFormDbcPath");
            Assert.Equal(ConfigSeverity.Warning, warning.Severity);
            Assert.Contains("SpellShapeshiftForm.dbc in ClientData:DbcDirectory is missing", warning.Problem, StringComparison.Ordinal);

            ConfigReport report = OpsCli.Validate(strict);
            Assert.Equal(ConfigSeverity.Error, Assert.Single(report.Issues, i => i.Key == "Combat:ShapeshiftFormDbcPath").Severity);
            Assert.True(report.IsInvalid);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>The next system line starting with <paramref name="prefix"/> (login chatter such as the message of the day is skipped).</summary>
    private static async Task<string> ReadChatStartingWithAsync(WorldTestClient gm, string prefix)
    {
        while (true)
        {
            string text = (await gm.ReadChatAsync()).Text;
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                return text;
            }
        }
    }

    [Fact]
    public void Levels_ArcaneDbcIsGameMaster_ValidateIsAdministrator()
    {
        CommandTable table = ChatCommands.CreateTable();
        Assert.Null(table.Resolve("arcane dbc", AccountSecurity.Moderator));
        Assert.NotNull(table.Resolve("arcane dbc", AccountSecurity.GameMaster));
        Assert.Null(table.Resolve("arcane dbc validate", AccountSecurity.GameMaster));
        Assert.NotNull(table.Resolve("arcane dbc validate", AccountSecurity.Administrator));
    }

    [Fact]
    public async Task ArcaneDbc_ShowsTheStartupReport_AndValidateNeedsADirectory()
    {
        string directory = WriteConsumerFiles(TempDirectory());
        try
        {
            ClientDataReport report = ClientDataReport.Build(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ClientData:DbcDirectory"] = directory }).Build());
            await using (WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton(report)))
            await using (WorldTestClient gm = await host.EnterWorldAsync("DBCGM", "Dbcgm", AccountSecurity.Administrator))
            {
                await gm.SendChatAsync(ChatType.Say, Language.Common, ".arcane dbc");
                Assert.Equal($"ClientData:DbcDirectory {directory}: {ClientDbcConsumers.All.Count} of {ClientDbcConsumers.All.Count} DBC keys filled from it",
                    await ReadChatStartingWithAsync(gm, "ClientData:DbcDirectory "));
                Assert.StartsWith("ClientData: BankBagSlotPrices.dbc: loaded 3 records (2 fields, vmangos DBCfmt.h BankBagSlotPricesEntryfmt) from ", (await gm.ReadChatAsync()).Text, StringComparison.Ordinal);
            }

            await using (WorldTestHost bare = WorldTestHost.Start())
            await using (WorldTestClient gm = await bare.EnterWorldAsync("DBCGN", "Dbcgn", AccountSecurity.Administrator))
            {
                await gm.SendChatAsync(ChatType.Say, Language.Common, ".arcane dbc validate");
                Assert.Equal("ClientData:DbcDirectory is not set: nothing to validate against.", await ReadChatStartingWithAsync(gm, "ClientData:"));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---- built-in tables against the client's own DBCs (skipped without ARCANECORE_TEST_DBC_DIR) ----------------------------

    [RealClientDbcFact]
    public void TheBuiltInShapeshiftTable_IsTheClientsSpellShapeshiftForm()
    {
        ShapeshiftFormCatalog dbc = ShapeshiftFormDbcReader.Load(Path.Combine(RealClientDbcFactAttribute.DbcDirectory, "SpellShapeshiftForm.dbc"));
        Assert.Equal(dbc.Forms.OrderBy(f => f.Id), ShapeshiftFormCatalog.Retail.Forms.OrderBy(f => f.Id));
    }

    [RealClientDbcFact]
    public void TheBuiltInChatChannels_AreTheClientsChatChannels()
    {
        ChatChannelCatalog dbc = ChatChannelCatalog.FromDbc(ChatChannelsDbcReader.Load(Path.Combine(RealClientDbcFactAttribute.DbcDirectory, "ChatChannels.dbc")));
        Assert.Equal(
            dbc.Channels.Select(c => (c.Id, c.Pattern, c.Flags, c.DbcFlags)),
            ChatChannelCatalog.Builtin.Channels.Select(c => (c.Id, c.Pattern, c.Flags, c.DbcFlags)));
    }

    [RealClientDbcFact]
    public void TheTeamFactions_AreTheClientsAllianceAndHorde()
    {
        var factions = FactionDbcReader.Load(Path.Combine(RealClientDbcFactAttribute.DbcDirectory, "Faction.dbc"));
        Assert.Equal("Alliance", factions.Find(FactionTeams.AllianceFaction)!.Name);
        Assert.Equal("Horde", factions.Find(FactionTeams.HordeFaction)!.Name);
        foreach (uint goblin in new EconomyOptions().NeutralAuctioneerFactions)
        {
            Assert.Equal(169u, factions.Find(goblin)!.ParentFactionId); // Steamwheedle Cartel
        }
    }
}
