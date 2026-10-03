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

namespace ArcaneCore.World.Tests.GameObjects;

/// <summary>
/// The adapter that feeds vmangos Player::HasQuestForItem (Player.cpp:14267-14320) to loot and game objects:
/// a raid group member does not want items of a quest that is not allowed in raids (QuestDef.cpp:227-235).
/// </summary>
public sealed class QuestJournalAdapterTests
{
    private const uint OrdinaryQuest = 9301;
    private const uint RaidQuest = 9302;
    private const uint OrdinaryItem = 9311;
    private const uint RaidItem = 9312;
    private const uint RaidFlag = 0x40; // QuestFlags.Raid, QuestDef.cpp:227-235

    private static QuestJournalFixture NewFixture() => new()
    {
        Templates =
        [
            new QuestTemplate { Entry = OrdinaryQuest, Method = 2, QuestLevel = 1, MinLevel = 1, Title = "Ordinary", ReqItemId1 = OrdinaryItem, ReqItemCount1 = 3 },
            new QuestTemplate { Entry = RaidQuest, Method = 2, QuestLevel = 1, MinLevel = 1, Title = "Raid", QuestFlags = RaidFlag, ReqItemId1 = RaidItem, ReqItemCount1 = 3 },
        ],
    };
    private static CharacterQuestStatus Incomplete(int characterId, uint quest)
        => new(characterId, quest, (byte)QuestStatus.Incomplete, false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    [Fact]
    public async Task ARaidGroupMember_DoesNotSeeTheItemsOfAQuestThatIsNotAllowedInRaids()
    {
        QuestJournalFixture fixture = NewFixture();
        QuestNpcTestServices.Current.Value = fixture;
        WorldTestHost host;
        try { host = WorldTestHost.Start(); }
        finally { QuestNpcTestServices.Current.Value = null; }

        await using (host)
        await using (WorldTestClient leader = await host.ConnectAsync())
        {
            byte[] key = await host.AddAccountAsync("RAIDER");
            await leader.AuthenticateAsync("RAIDER", key);
            await leader.CreateCharacterAsync("Raider");
            var account = (await host.Accounts.FindByUsernameAsync("RAIDER"))!;
            CharacterRecord character = (await host.Characters.GetByAccountAsync(account.Id)).Single();
            fixture.Characters.Seed(Incomplete(character.Id, OrdinaryQuest), Incomplete(character.Id, RaidQuest));
            await leader.LoginAsync((ulong)character.Id);
            await using WorldTestClient member = await host.EnterWorldAsync("FRIEND", "Friend");
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Raider") is { } p
                && ((WorldSession)p.Session).Services.GetRequiredService<QuestNpcFeature>().Services.StateOf(p) is { Loaded: true },
                "the quest journal to load");

            bool Needs(uint item) => host.OnWorldAsync(() =>
            {
                Player p = host.World.FindOnlinePlayer("Raider")!;
                ILootQuestJournal journal = ((WorldSession)p.Session).Services.GetRequiredService<GameObjectLootFeature>().Quests;
                return journal.NeedsQuestItem(p, item);
            }).GetAwaiter().GetResult();

            // Not grouped: both quests want their items.
            Assert.True(Needs(OrdinaryItem));
            Assert.True(Needs(RaidItem));

            await leader.SendAsync(WorldOpcode.CmsgGroupInvite, [.. "friend"u8, 0]);
            await member.ReadUntilAsync(WorldOpcode.SmsgGroupInvite);
            await member.SendAsync(WorldOpcode.CmsgGroupAccept, []);
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Raider") is { } p
                && ((WorldSession)p.Session).Services.GetRequiredService<SocialFeature>().Context.Groups.GetGroup(p.Guid) is not null, "the party to form");

            // A plain party is not a raid: unchanged.
            Assert.True(Needs(OrdinaryItem));
            Assert.True(Needs(RaidItem));

            await host.OnWorldAsync(() =>
            {
                Player p = host.World.FindOnlinePlayer("Raider")!;
                ((WorldSession)p.Session).Services.GetRequiredService<SocialFeature>().Context.Groups.ConvertToRaid(p);
            });

            // Raid group: the ordinary quest's item is hidden, the raid-flagged quest's item still shows.
            Assert.False(Needs(OrdinaryItem));
            Assert.True(Needs(RaidItem));
        }
    }
}
