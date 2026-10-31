using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Protocol;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Social;
using ArcaneCore.World.Tests.Npc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Battlegrounds;

/// <summary>
/// vmangos Player::HasQuestForItem (Player.cpp:14287-14289) hides quest items of non-raid quests from raid-group members only
/// when !InBattleGround(), so a battleground raid participant still sees Alterac Valley collection-quest drops.
/// </summary>
public sealed class BattlegroundQuestLootVisibilityTests
{
    private const uint OrdinaryQuest = 9401;
    private const uint RaidQuest = 9402;
    private const uint OrdinaryItem = 9411;
    private const uint RaidItem = 9412;
    private const uint RaidFlag = 0x40; // QuestFlags.Raid, QuestDef.cpp:227-235

    private static CharacterQuestStatus Incomplete(int characterId, uint quest)
        => new(characterId, quest, (byte)QuestStatus.Incomplete, false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    [Fact]
    public async Task ABattlegroundRaidMember_StillSeesNonRaidQuestItems_ButAnOrdinaryRaidMemberDoesNot()
    {
        QuestJournalFixture fixture = new()
        {
            Templates =
            [
                new QuestTemplate { Entry = OrdinaryQuest, Method = 2, QuestLevel = 1, MinLevel = 1, Title = "Ordinary", ReqItemId1 = OrdinaryItem, ReqItemCount1 = 3 },
                new QuestTemplate { Entry = RaidQuest, Method = 2, QuestLevel = 1, MinLevel = 1, Title = "Raid", QuestFlags = RaidFlag, ReqItemId1 = RaidItem, ReqItemCount1 = 3 },
            ],
        };
        QuestNpcTestServices.Current.Value = fixture;
        WorldTestHost host;
        try { host = WorldTestHost.Start(); }
        finally { QuestNpcTestServices.Current.Value = null; }

        await using (host)
        await using (WorldTestClient client = await host.ConnectAsync())
        {
            byte[] key = await host.AddAccountAsync("BGRAIDER");
            await client.AuthenticateAsync("BGRAIDER", key);
            await client.CreateCharacterAsync("Bgraider");
            var account = (await host.Accounts.FindByUsernameAsync("BGRAIDER"))!;
            CharacterRecord character = (await host.Characters.GetByAccountAsync(account.Id)).Single();
            fixture.Characters.Seed(Incomplete(character.Id, OrdinaryQuest), Incomplete(character.Id, RaidQuest));
            await client.LoginAsync((ulong)character.Id);
            await using WorldTestClient member = await host.EnterWorldAsync("FRIEND", "Friend");
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Bgraider") is { } p
                && ((WorldSession)p.Session).Services.GetRequiredService<QuestNpcFeature>().Services.StateOf(p) is { Loaded: true },
                "the quest journal to load");

            bool Needs(uint item) => host.OnWorldAsync(() =>
            {
                Player p = host.World.FindOnlinePlayer("Bgraider")!;
                ILootQuestJournal journal = ((WorldSession)p.Session).Services.GetRequiredService<GameObjectLootFeature>().Quests;
                return journal.NeedsQuestItem(p, item);
            }).GetAwaiter().GetResult();

            // Ordinary raid group outside a battleground: the non-raid quest's item is hidden, the raid quest's item shows.
            await client.SendAsync(WorldOpcode.CmsgGroupInvite, [.. "friend"u8, 0]);
            await member.ReadUntilAsync(WorldOpcode.SmsgGroupInvite);
            await member.SendAsync(WorldOpcode.CmsgGroupAccept, []);
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Bgraider") is { } p
                && ((WorldSession)p.Session).Services.GetRequiredService<SocialFeature>().Context.Groups.GetGroup(p.Guid) is not null, "the party to form");
            await host.OnWorldAsync(() =>
            {
                Player p = host.World.FindOnlinePlayer("Bgraider")!;
                ((WorldSession)p.Session).Services.GetRequiredService<SocialFeature>().Context.Groups.ConvertToRaid(p);
            });
            Assert.False(Needs(OrdinaryItem));
            Assert.True(Needs(RaidItem));

            // Battleground raid: the exemption applies, both quests want their items.
            await host.OnWorldAsync(() =>
            {
                Player p = host.World.FindOnlinePlayer("Bgraider")!;
                var groups = ((WorldSession)p.Session).Services.GetRequiredService<SocialFeature>().Context.Groups;
                Assert.NotNull(groups.AddToBattlegroundRaid(p, null));
            });
            Assert.True(Needs(OrdinaryItem));
            Assert.True(Needs(RaidItem));
        }
    }
}
