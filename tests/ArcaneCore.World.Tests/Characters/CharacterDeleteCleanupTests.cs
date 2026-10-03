using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Net;
using ArcaneCore.World.Social;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Characters;

/// <summary>
/// CMSG_CHAR_DELETE against the real world daemon over loopback, with the live state of the
/// social, group, guild and spell features (<see cref="ICharacterDeleteHook"/>): the deleted
/// character leaves every friend/ignore list (listers are told), its guild and its group, and its
/// cached spellbook goes; a guild leader is refused and keeps everything (vmangos
/// HandleCharDeleteOpcode → Player::DeleteFromDB).
/// </summary>
public sealed class CharacterDeleteCleanupTests
{
    [Fact]
    public void DeleteHooks_AreDiscoveredOnTheFeaturesThatOwnLiveState()
    {
        Type[] hooks = [.. ArcaneCore.World.Features.WorldFeatures.FeatureTypes.Where(typeof(ICharacterDeleteHook).IsAssignableFrom)];
        Assert.Contains(typeof(SocialCharacterDeleteHook), hooks);
        Assert.Contains(typeof(SpellCharacterDeleteHook), hooks);
        Assert.Contains(typeof(ArcaneCore.World.Npc.QuestNpcFeature), hooks);
    }

    [Fact]
    public async Task Delete_RemovesTheCharacterFromListsGroupGuildAndSpellbook()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient keeper = await host.EnterWorldAsync("KEEPER", "Keeper", AccountSecurity.GameMaster);
        byte[] key = await host.AddAccountAsync("VICTIM");
        WorldTestClient victim = await host.ConnectAsync();
        await victim.AuthenticateAsync("VICTIM", key);
        await victim.CreateCharacterAsync("Victim");
        ulong victimGuid = await GuidOfAsync(host, "VICTIM", "Victim");
        await victim.LoginAsync(victimGuid);
        SocialFeature social = await host.PlayerStateAsync("Keeper", Social);
        await social.GuildsLoaded.WaitAsync(TimeSpan.FromSeconds(10));
        await Drain(keeper, victim);

        // Keeper lists Victim as friend and ignored, leads a guild with Victim in it, and groups with Victim.
        await keeper.SendAsync(WorldOpcode.CmsgAddFriend, CString("victim"));
        Assert.Equal((byte)FriendsResult.AddedOnline, (await keeper.ReadUntilAsync(WorldOpcode.SmsgFriendStatus))[0]);
        await keeper.SendAsync(WorldOpcode.CmsgAddIgnore, CString("victim"));
        Assert.Equal((byte)FriendsResult.IgnoreAdded, (await keeper.ReadUntilAsync(WorldOpcode.SmsgFriendStatus))[0]);
        Assert.Equal("Guild Arcane created.", await CommandAsync(keeper, ".guild create Keeper \"Arcane\""));
        Assert.Equal("Added to Arcane.", await CommandAsync(keeper, ".guild invite Victim \"Arcane\""));
        await keeper.SendAsync(WorldOpcode.CmsgGroupInvite, CString("victim"));
        await victim.ReadUntilAsync(WorldOpcode.SmsgGroupInvite);
        await victim.SendAsync(WorldOpcode.CmsgGroupAccept, []);
        await host.WaitForWorldAsync(() => social.Context.Groups.GetGroup(ObjectGuid.Player((uint)victimGuid)) is not null, "Victim to join the group");
        SpellbookCache spellbook = await host.PlayerStateAsync("Keeper", p => Services(p).GetRequiredService<SpellFeature>().Spellbook);
        Assert.True(spellbook.ContainsCharacter((int)victimGuid));

        // Victim logs out; group and guild membership stay while it is offline.
        await victim.DisposeAsync();
        await host.WaitForWorldAsync(() => !host.World.IsOnline(ObjectGuid.Player((uint)victimGuid)), "Victim to leave the world");
        Assert.NotNull(await host.OnWorldAsync(() => social.Context.Groups.GetGroup(ObjectGuid.Player((uint)victimGuid))));
        await WorldTestHost.WaitForAsync(() => social.PendingWrites == 0, "social writes before the deletion");
        await Drain(keeper);

        await using (WorldTestClient again = await host.ConnectAsync())
        {
            await again.AuthenticateAsync("VICTIM", key);
            await again.SendAsync(WorldOpcode.CmsgCharDelete, U64(victimGuid));
            Assert.Equal((byte)CharResult.CharDeleteSuccess, (await again.ReadUntilAsync(WorldOpcode.SmsgCharDelete))[0]);
        }

        // The lister hears both removals, for the deleted GUID.
        byte[] first = await keeper.ReadUntilAsync(WorldOpcode.SmsgFriendStatus);
        byte[] second = await keeper.ReadUntilAsync(WorldOpcode.SmsgFriendStatus);
        Assert.Equal([(byte)FriendsResult.Removed, (byte)FriendsResult.IgnoreRemoved], new[] { first[0], second[0] });
        Assert.Equal(victimGuid, BinaryPrimitives.ReadUInt64LittleEndian(first.AsSpan(1)));
        Assert.Equal(victimGuid, BinaryPrimitives.ReadUInt64LittleEndian(second.AsSpan(1)));

        (bool listed, bool grouped, bool keeperGrouped, bool guilded, int members) = await host.PlayerStateAsync("Keeper", p =>
        {
            SocialContext context = social.Context;
            uint id = (uint)victimGuid;
            return (context.Friends.Get(p).Entries.ContainsKey(id),
                context.Groups.GetGroup(ObjectGuid.Player(id)) is not null,
                context.Groups.GetGroup(p.Guid) is not null,
                context.Guilds.GetGuildOf(id) is not null,
                context.Guilds.GetGuildOf(p)!.MemberCount);
        });
        Assert.False(listed);
        Assert.False(grouped);
        Assert.False(keeperGrouped); // a two-member group disbands when one member goes
        Assert.False(guilded);
        Assert.Equal(1, members);
        Assert.False(spellbook.ContainsCharacter((int)victimGuid));
        Assert.Null(host.Directory.Find((int)victimGuid));

        // The purge was queued after every earlier social write: storage names Victim nowhere.
        await WorldTestHost.WaitForAsync(() => social.PendingWrites == 0, "the social purge");
        ISocialStore store = await host.PlayerStateAsync("Keeper", p => Services(p).GetRequiredService<ISocialStore>());
        Assert.Empty(await store.GetSocialAsync((int)(await host.PlayerAsync("Keeper")).Guid.Low));
        GuildData guild = Assert.Single(await store.GetGuildsAsync());
        Assert.DoesNotContain(guild.Members, m => m.CharacterId == (int)victimGuid);
    }

    [Fact]
    public async Task Delete_OfAGuildLeader_IsRefused_AndKeepsTheCharacterAndGuild()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("GM", "Keeper", AccountSecurity.GameMaster);
        byte[] key = await host.AddAccountAsync("LEADER");
        WorldTestClient leader = await host.ConnectAsync();
        await leader.AuthenticateAsync("LEADER", key);
        await leader.CreateCharacterAsync("Founder");
        ulong founderGuid = await GuidOfAsync(host, "LEADER", "Founder");
        await leader.LoginAsync(founderGuid);
        SocialFeature social = await host.PlayerStateAsync("Keeper", Social);
        await social.GuildsLoaded.WaitAsync(TimeSpan.FromSeconds(10));
        await Drain(gm, leader);
        Assert.Equal("Guild Arcane created.", await CommandAsync(gm, ".guild create Founder \"Arcane\""));
        Assert.Equal("Added to Arcane.", await CommandAsync(gm, ".guild invite Keeper \"Arcane\""));
        await leader.DisposeAsync();
        await host.WaitForWorldAsync(() => !host.World.IsOnline(ObjectGuid.Player((uint)founderGuid)), "Founder to leave the world");

        await using WorldTestClient again = await host.ConnectAsync();
        await again.AuthenticateAsync("LEADER", key);
        await again.SendAsync(WorldOpcode.CmsgCharDelete, U64(founderGuid));
        Assert.Equal((byte)CharResult.CharDeleteFailed, (await again.ReadUntilAsync(WorldOpcode.SmsgCharDelete))[0]);

        Assert.NotNull(await host.Characters.GetByIdAsync((int)founderGuid));
        Assert.NotNull(host.Directory.Find((int)founderGuid));
        (bool leads, int members) = await host.OnWorldAsync(() =>
            (social.Context.Guilds.GetGuildOf((uint)founderGuid)!.LeaderId == (uint)founderGuid,
                social.Context.Guilds.GetGuildOf((uint)founderGuid)!.MemberCount));
        Assert.True(leads);
        Assert.Equal(2, members);
    }

    [Fact]
    public async Task Delete_OfAnotherAccountsOrUnknownCharacter_Fails()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient owner = await host.EnterWorldAsync("OWNER", "Owner");
        byte[] key = await host.AddAccountAsync("THIEF");
        await using WorldTestClient thief = await host.ConnectAsync();
        await thief.AuthenticateAsync("THIEF", key);
        ulong ownerGuid = await GuidOfAsync(host, "OWNER", "Owner");

        foreach (ulong guid in new[] { ownerGuid, 4242ul, 0ul, (ulong)int.MaxValue + 1 })
        {
            await thief.SendAsync(WorldOpcode.CmsgCharDelete, U64(guid));
            Assert.Equal((byte)CharResult.CharDeleteFailed, (await thief.ReadUntilAsync(WorldOpcode.SmsgCharDelete))[0]);
        }

        Assert.NotNull(await host.Characters.GetByIdAsync((int)ownerGuid));
        Assert.True(host.World.IsOnline(ObjectGuid.Player((uint)ownerGuid)));
    }

    private static async Task<ulong> GuidOfAsync(WorldTestHost host, string account, string name)
    {
        Account stored = (await host.Accounts.FindByUsernameAsync(account))!;
        return (ulong)(await host.Characters.GetByAccountAsync(stored.Id)).Single(c => c.Name == name).Id;
    }

    private static IServiceProvider Services(Game.Entities.Player player) => ((WorldSession)player.Session).Services;

    private static SocialFeature Social(Game.Entities.Player player) => Services(player).GetRequiredService<SocialFeature>();

    private static async Task<string> CommandAsync(WorldTestClient client, string line)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, line);
        ChatMessage reply = await client.ReadChatAsync();
        Assert.Equal(ChatType.System, reply.Type);
        return reply.Text;
    }

    private static async Task Drain(params WorldTestClient[] clients)
    {
        foreach (WorldTestClient client in clients)
        {
            await client.CollectAsync();
        }
    }

    private static byte[] U64(ulong value)
    {
        byte[] bytes = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] CString(string value)
    {
        var writer = new PacketWriter(value.Length + 1);
        writer.WriteCString(value);
        return writer.ToArray();
    }
}
