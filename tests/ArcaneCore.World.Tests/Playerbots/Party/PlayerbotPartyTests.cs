using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Skills;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Party;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Party;

/// <summary>
/// Coverage for party bot completeness: rich whisper commands (gear, bags, gold, quests, skills, talents, buff, share, help),
/// human-to-bot trading via <see cref="WorldOpcode.SmsgTradeStatus"/>, and party combat facing.
/// </summary>
public sealed class PlayerbotPartyTests
{
    [Theory]
    [InlineData("gear", PlayerbotPartyCommand.Gear)]
    [InlineData("equip", PlayerbotPartyCommand.Gear)]
    [InlineData("equipment", PlayerbotPartyCommand.Gear)]
    [InlineData("bags", PlayerbotPartyCommand.Bags)]
    [InlineData("bag", PlayerbotPartyCommand.Bags)]
    [InlineData("inv", PlayerbotPartyCommand.Bags)]
    [InlineData("inventory", PlayerbotPartyCommand.Bags)]
    [InlineData("gold", PlayerbotPartyCommand.Gold)]
    [InlineData("money", PlayerbotPartyCommand.Gold)]
    [InlineData("cash", PlayerbotPartyCommand.Gold)]
    [InlineData("quests", PlayerbotPartyCommand.Quests)]
    [InlineData("quest", PlayerbotPartyCommand.Quests)]
    [InlineData("skills", PlayerbotPartyCommand.Skills)]
    [InlineData("professions", PlayerbotPartyCommand.Skills)]
    [InlineData("prof", PlayerbotPartyCommand.Skills)]
    [InlineData("talents", PlayerbotPartyCommand.Talents)]
    [InlineData("spec", PlayerbotPartyCommand.Talents)]
    [InlineData("talent", PlayerbotPartyCommand.Talents)]
    [InlineData("buff", PlayerbotPartyCommand.Upkeep)]
    [InlineData("buffs", PlayerbotPartyCommand.Upkeep)]
    [InlineData("upkeep", PlayerbotPartyCommand.Upkeep)]
    [InlineData("share", PlayerbotPartyCommand.ShareQuests)]
    [InlineData("sharequests", PlayerbotPartyCommand.ShareQuests)]
    [InlineData("help", PlayerbotPartyCommand.Help)]
    [InlineData("commands", PlayerbotPartyCommand.Help)]
    public void ChatCommands_TryParse_ParsesRichPartyCommandsAndAliases(string text, PlayerbotPartyCommand expected)
    {
        Assert.True(PlayerbotChatCommands.TryParse(text, out PlayerbotPartyCommand actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PacketIntake_AndPartyIntake_IncludeSmsgTradeStatus()
    {
        Assert.Contains(WorldOpcode.SmsgTradeStatus, (IReadOnlySet<WorldOpcode>)PlayerbotPacketIntake.RingOpcodes);
        Assert.Contains(WorldOpcode.SmsgTradeStatus, PlayerbotPartyAI.IntakeOpcodes);
        Assert.Contains("gear, bags, gold, quests, skills, talents, buff, share, leave", PlayerbotChatCommands.Help);
    }

    [Fact]
    public async Task RichWhisperCommands_AreAnsweredByPartyBot()
    {
        await using WorldTestHost host = PartyTestHost.Start(options => options.Party.InvitePolicy = PlayerbotInvitePolicy.Anyone);
        Guid bot = await PartyTestHost.StartBotAsync(host, "Partycmdbot");
        await using WorldTestClient master = await host.EnterWorldAsync("PARTYCMDM", "Partycmdm");
        ulong botGuid = (await host.PlayerStateAsync("Partycmdbot", p => p.Guid)).Value;

        await master.SendAsync(WorldOpcode.CmsgGroupInvite, PartyTestHost.CString("Partycmdbot"));
        await host.WaitForWorldAsync(() => PartyTestHost.Feature(host).IsPartyDriven(bot), "the party AI drives the bot");

        await host.OnWorldAsync(() =>
        {
            Player botPlayer = host.World.FindOnlinePlayer("Partycmdbot")!;
            botPlayer.Money = 12_345;
            botPlayer.SetUInt32(UpdateFields.PlayerCharacterPoints1, 3);
            botPlayer.SetUInt32(UpdateFields.PlayerQuestLog11, 42);
            if (botPlayer.Skills?.Set(SkillIds.Mining, 45, 75) != true)
            {
                botPlayer.SetUInt32(UpdateFields.PlayerSkillInfo11, SkillIds.Mining);
                botPlayer.SetUInt32(UpdateFields.PlayerSkillInfo11 + 1, 45u | (75u << 16));
            }
        });

        await master.SendChatAsync(ChatType.Whisper, Language.Common, "gold", "Partycmdbot");
        Assert.Equal("Gold: 1g 23s 45c.", (await PartyTestHost.ReadWhisperFromAsync(master, botGuid)).Text);

        await master.SendChatAsync(ChatType.Whisper, Language.Common, "bags", "Partycmdbot");
        ChatMessage bagsReply = await PartyTestHost.ReadWhisperFromAsync(master, botGuid);
        Assert.Matches(@"^Bags: \d+/16 free\.$", bagsReply.Text);

        await master.SendChatAsync(ChatType.Whisper, Language.Common, "gear", "Partycmdbot");
        ChatMessage gearReply = await PartyTestHost.ReadWhisperFromAsync(master, botGuid);
        Assert.StartsWith("Gear: ", gearReply.Text);

        await master.SendChatAsync(ChatType.Whisper, Language.Common, "quests", "Partycmdbot");
        Assert.Equal("Quests: #42 (incomplete).", (await PartyTestHost.ReadWhisperFromAsync(master, botGuid)).Text);

        await master.SendChatAsync(ChatType.Whisper, Language.Common, "skills", "Partycmdbot");
        ChatMessage skillsReply = await PartyTestHost.ReadWhisperFromAsync(master, botGuid);
        Assert.Contains("Mining 45/75", skillsReply.Text);

        await master.SendChatAsync(ChatType.Whisper, Language.Common, "talents", "Partycmdbot");
        ChatMessage talentsReply = await PartyTestHost.ReadWhisperFromAsync(master, botGuid);
        Assert.Matches(@"^Talents: .+ \(0 spent, 3 free\)\.$", talentsReply.Text);

        await master.SendChatAsync(ChatType.Whisper, Language.Common, "buff", "Partycmdbot");
        ChatMessage buffReply = await PartyTestHost.ReadWhisperFromAsync(master, botGuid);
        Assert.True(buffReply.Text is "Buffing." or "Upkeep done.", buffReply.Text);

        await master.SendChatAsync(ChatType.Whisper, Language.Common, "share", "Partycmdbot");
        Assert.Equal("Shared 1 quest(s).", (await PartyTestHost.ReadWhisperFromAsync(master, botGuid)).Text);

        await master.SendChatAsync(ChatType.Whisper, Language.Common, "help", "Partycmdbot");
        Assert.Equal(PlayerbotChatCommands.Help, (await PartyTestHost.ReadWhisperFromAsync(master, botGuid)).Text);
    }

    [Fact]
    public async Task HumanToBotTrade_MasterTradeIsAccepted_AndStrangerTradeIsIgnored()
    {
        await using WorldTestHost host = PartyTestHost.Start(
            options => options.Party.InvitePolicy = PlayerbotInvitePolicy.Anyone,
            services => services.AddScoped<ArcaneCore.Kernel.Economy.IEconomyStore>(_ => new StubEconomyStore()));
        Guid bot = await PartyTestHost.StartBotAsync(host, "Partytrade");
        await using WorldTestClient master = await host.EnterWorldAsync("PARTYTRADEM", "Partytradem");
        await using WorldTestClient stranger = await host.EnterWorldAsync("PARTYTRADES", "Partytrades");

        await master.SendAsync(WorldOpcode.CmsgGroupInvite, PartyTestHost.CString("Partytrade"));
        await host.WaitForWorldAsync(() => PartyTestHost.Feature(host).IsPartyDriven(bot), "the party AI drives the bot");

        await host.OnWorldAsync(() =>
        {
            Player botPlayer = host.World.FindOnlinePlayer("Partytrade")!;
            Player masterPlayer = host.World.FindOnlinePlayer("Partytradem")!;
            Player strangerPlayer = host.World.FindOnlinePlayer("Partytrades")!;
            WorldSession botSession = PartyTestHost.Feature(host).FindSession(bot)!;

            // Stop the bot and place it within trade distance (11.11 yards) of the master.
            PlayerbotMovementControl.Stop(botSession, botPlayer);
            botPlayer.Relocate(masterPlayer.X + 2f, masterPlayer.Y, masterPlayer.Z, 0f, host.World.NowMs);
            botPlayer.Inventory.Load([]);
            masterPlayer.Inventory.Load([]);

            var packet = new PacketWriter(12);
            packet.WriteUInt32(1u); // TradeStatus.BeginTrade
            packet.WriteUInt64(strangerPlayer.Guid.Value);
            botSession.Send(WorldOpcode.SmsgTradeStatus, packet.ToArray());

            PlayerbotPartyAI party = PartyTestHost.Feature(host).FindParty(bot)!;
            botSession.ManagedBudget = new ManagedActionBudget(2);
            party.Intake(botPlayer);
            Assert.Equal(2, botSession.ManagedBudget.Remaining);
        });

        ObjectGuid botGuid = await host.PlayerStateAsync("Partytrade", p => p.Guid);
        var initiate = new PacketWriter(8);
        initiate.WriteUInt64(botGuid.Value);
        await master.SendAsync(WorldOpcode.CmsgInitiateTrade, initiate.ToArray());

        // Wait for the bot's intake to answer BeginTrade with CmsgBeginTrade (sending SmsgTradeStatus OpenWindow = 2 to master).
        byte[] openStatus = await master.ReadUntilAsync(WorldOpcode.SmsgTradeStatus);
        Assert.Equal(2u, BitConverter.ToUInt32(openStatus, 0));

        // Master accepts the trade; bot receives TradeAccept (4) and answers with CmsgAcceptTrade, sending TradeAccept (4) then TradeComplete (8) to master.
        await master.SendAsync(WorldOpcode.CmsgAcceptTrade, BitConverter.GetBytes(1u));
        uint finalStatus;
        do
        {
            byte[] statusPacket = await master.ReadUntilAsync(WorldOpcode.SmsgTradeStatus);
            finalStatus = BitConverter.ToUInt32(statusPacket, 0);
        }
        while (finalStatus == 4u);
        Assert.Equal(8u, finalStatus);
    }

    [Fact]
    public async Task Fight_TurnsStoppedPartyBot_TowardTargetOutsideAutoAttackArc()
    {
        await using WorldTestHost host = PartyTestHost.Start(options => options.Party.InvitePolicy = PlayerbotInvitePolicy.Anyone);
        WorldSession botSession = await PartyTestHost.EnterManagedAsync(host, "PARTYFACEBOT", "Partyfacebot");
        await using WorldTestClient master = await host.EnterWorldAsync("PARTYFACEM", "Partyfacem");

        await host.OnWorldAsync(() =>
        {
            Player botPlayer = botSession.Player!;
            Player masterPlayer = host.World.FindOnlinePlayer("PARTYFACEM") ?? host.World.FindOnlinePlayer("Partyfacem")!;
            botPlayer.Map!.Combat.Hooks = new HostileCombatHooks();
            SocialFeature social = host.WorldServices.GetRequiredService<SocialFeature>();
            social.Context.Groups.Invite(masterPlayer, botPlayer.Name);
            social.Context.Groups.Accept(botPlayer);

            var ai = new PlayerbotPartyAI(botSession, PartyTestHost.PlayerbotOptionsOf(host));
            Assert.True(ai.Drives(botPlayer));

            var template = new CreatureTemplate
            {
                Entry = 990_401,
                Name = "training dummy",
                CreatureType = 1,
                MinLevel = 1,
                MaxLevel = 1,
                MinLevelHealth = 500,
                MaxLevelHealth = 500,
            };
            var system = new CreatureMapSystem(botPlayer.Map!, new CreatureContent([template], [], [], [], []), random: new Random(1));
            Creature target = system.SpawnTemporary(template, botPlayer.X + 2.5f, botPlayer.Y, botPlayer.Z, 0f);
            botSession.Services.GetRequiredService<ArcaneCore.World.Spells.SpellFeature>().System.Store =
                new ArcaneCore.Game.Spells.SpellStore([], [], []);

            // Drain any login movement-change acks, stop follow motion, and face away from the target (+X is 0 rad, bot faces PI rad).
            botSession.ManagedBudget = new ManagedActionBudget(8);
            while (PlayerbotMovementControl.Update(botSession, botPlayer)) { }
            PlayerbotMovementControl.Stop(botSession, botPlayer);
            botPlayer.Relocate(botPlayer.X, botPlayer.Y, botPlayer.Z, MathF.PI, host.World.NowMs);
            masterPlayer.Combat.Victim = target;

            botSession.ManagedBudget = new ManagedActionBudget(8);
            ai.Update(botPlayer, 500);

            Assert.True(MathF.Abs(botPlayer.Orientation) < 0.1f, $"Expected orientation near 0, got {botPlayer.Orientation} (goal={ai.Goal}, target={ai.TargetEntry})");
        });
    }

    private sealed class HostileCombatHooks : ArcaneCore.Game.Combat.CombatHooks
    {
        public override bool CanAttack(Unit attacker, Unit victim) => !ReferenceEquals(attacker, victim);
    }

    private sealed class StubEconomyStore : ArcaneCore.Kernel.Economy.IEconomyStore
    {
        public Task<ArcaneCore.Kernel.Economy.EconomyCommitResult> CommitAsync(
            ArcaneCore.Kernel.Economy.EconomyCommitRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(ArcaneCore.Kernel.Economy.EconomyCommitResult.Committed);

        public Task<bool> IsCommittedAsync(Guid operationId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<IReadOnlyList<ArcaneCore.Kernel.Economy.MailRecord>> GetMailsAsync(int receiverId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ArcaneCore.Kernel.Economy.MailRecord>>([]);

        public Task<IReadOnlyList<ArcaneCore.Kernel.Economy.MailRecord>> GetExpiredMailsAsync(long now, int max, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ArcaneCore.Kernel.Economy.MailRecord>>([]);

        public Task<IReadOnlyList<ArcaneCore.Kernel.Economy.MailRecord>> GetMailsInvolvingAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ArcaneCore.Kernel.Economy.MailRecord>>([]);

        public Task<string?> GetItemTextAsync(uint itemTextId, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<ArcaneCore.Kernel.Economy.AuctionRecord>> GetAuctionsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ArcaneCore.Kernel.Economy.AuctionRecord>>([]);

        public Task<IReadOnlyDictionary<uint, ItemInstanceData>> GetEscrowItemsAsync(IReadOnlyCollection<uint> itemGuids, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<uint, ItemInstanceData>>(new Dictionary<uint, ItemInstanceData>());

        public Task<ArcaneCore.Kernel.Economy.AuctionSnapshot> GetAuctionSnapshotAsync(
            ArcaneCore.Kernel.Economy.AuctionSnapshotFilter filter,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ArcaneCore.Kernel.Economy.AuctionSnapshot([], new Dictionary<uint, ItemInstanceData>()));

        public Task<ArcaneCore.Kernel.Economy.EconomyIdSeed> GetIdSeedAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ArcaneCore.Kernel.Economy.EconomyIdSeed(0, 0, 0));
    }

    [Theory]
    [InlineData("debug", PlayerbotPartyCommand.Debug)]
    [InlineData("diag", PlayerbotPartyCommand.Debug)]
    [InlineData("diagnostics", PlayerbotPartyCommand.Debug)]
    [InlineData("state", PlayerbotPartyCommand.Debug)]
    [InlineData("brain", PlayerbotPartyCommand.Debug)]
    [InlineData("trace", PlayerbotPartyCommand.Debug)]
    [InlineData("motion", PlayerbotPartyCommand.Debug)]
    [InlineData("route", PlayerbotPartyCommand.Debug)]
    [InlineData("stall", PlayerbotPartyCommand.Debug)]
    [InlineData("cmd", PlayerbotPartyCommand.Help)]
    [InlineData("cmds", PlayerbotPartyCommand.Help)]
    [InlineData("?", PlayerbotPartyCommand.Help)]
    public void ChatCommands_TryParse_ParsesDebugAndHelpAliases(string text, PlayerbotPartyCommand expected)
    {
        Assert.True(PlayerbotChatCommands.TryParse(text, out PlayerbotPartyCommand actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task HelpQuestions_AndGmDebugStates_AreAnsweredAccordingToPlayerRole()
    {
        await using WorldTestHost host = PartyTestHost.Start(options =>
        {
            options.ThinkIntervalMs = 50;
            options.Chat.Enabled = false;
            options.Party.InvitePolicy = PlayerbotInvitePolicy.Anyone;
        });
        Guid bot = await PartyTestHost.StartBotAsync(host, "Helpgmdbg");
        await using WorldTestClient stranger = await host.EnterWorldAsync("HELPSTRANGER", "Helpstranger");
        await using WorldTestClient gm = await host.EnterWorldAsync("HELPGMUSER", "Helpgmuser", ArcaneCore.Kernel.Accounts.AccountSecurity.GameMaster);
        ulong botGuid = (await host.PlayerStateAsync("Helpgmdbg", p => p.Guid)).Value;

        // A non-GM stranger asking about commands gets the standard command list even when ungrouped and with chat off.
        await stranger.SendChatAsync(ChatType.Whisper, Language.Common, "what commands do you have?", "Helpgmdbg");
        Assert.Equal(PlayerbotChatCommands.Help, (await PartyTestHost.ReadWhisperFromAsync(stranger, botGuid)).Text);

        // A GM asking for help gets the expanded GM help list including debug/diagnostic commands.
        await gm.SendChatAsync(ChatType.Whisper, Language.Common, "help", "Helpgmdbg");
        Assert.Equal(PlayerbotChatCommands.GmHelp, (await PartyTestHost.ReadWhisperFromAsync(gm, botGuid)).Text);

        // A GM whispering "debug" or asking a debug question gets the live internal diagnostic state even without being in the bot's group.
        await gm.SendChatAsync(ChatType.Whisper, Language.Common, "debug", "Helpgmdbg");
        ChatMessage gmDebug = await PartyTestHost.ReadWhisperFromAsync(gm, botGuid);
        Assert.StartsWith("Debug: driver=", gmDebug.Text);
        Assert.Contains("goal=", gmDebug.Text);
        Assert.Contains("phase=", gmDebug.Text);

        await gm.SendChatAsync(ChatType.Whisper, Language.Common, "what is your brain state?", "Helpgmdbg");
        ChatMessage gmDebugQuestion = await PartyTestHost.ReadWhisperFromAsync(gm, botGuid);
        Assert.StartsWith("Debug: driver=", gmDebugQuestion.Text);

        // .playerbot debug <name> on an autonomous (non-party) bot succeeds and reports "autonomous" instead of failing or empty "following ".
        List<string> autoDebug = await FleetWorld.SayAsync(gm, ".playerbot debug Helpgmdbg", lines: 2);
        Assert.Contains("driver=autonomous", autoDebug[0], StringComparison.Ordinal);
        Assert.Contains("drv=brain", autoDebug[0], StringComparison.Ordinal);
        Assert.DoesNotContain("grpStep=", autoDebug[0], StringComparison.Ordinal);
        Assert.EndsWith(", autonomous.", autoDebug[1], StringComparison.Ordinal);
        Assert.DoesNotContain("following ", autoDebug[1], StringComparison.Ordinal);
    }
}
