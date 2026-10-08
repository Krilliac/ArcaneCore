using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Instances;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Instances;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Instances;
using ArcaneCore.World.Social;
using ArcaneCore.World.Tests.Instances;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Social;

/// <summary>An in-memory <see cref="IGroupStore"/> shared by the hosts of a restart test (thread-safe).</summary>
internal sealed class InMemoryGroupStore : IGroupStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<uint, GroupRecord> _groups = [];

    public int Saves { get; private set; }

    public GroupRecord? Group(uint id)
    {
        lock (_lock)
        {
            return _groups.GetValueOrDefault(id);
        }
    }

    public IReadOnlyList<GroupRecord> All()
    {
        lock (_lock)
        {
            return [.. _groups.Values.OrderBy(g => g.Id)];
        }
    }

    public void Seed(GroupRecord group)
    {
        lock (_lock)
        {
            _groups[group.Id] = group;
        }
    }

    public Task<IReadOnlyList<GroupRecord>> LoadGroupsAsync(CancellationToken cancellationToken = default) => Task.FromResult(All());

    public Task SaveGroupAsync(GroupRecord group, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            Saves++;
            HashSet<int> moving = [.. group.Members.Select(m => m.CharacterId)];
            foreach (GroupRecord other in _groups.Values.Where(g => g.Id != group.Id).ToArray())
            {
                if (other.Members.Any(m => moving.Contains(m.CharacterId)))
                {
                    _groups[other.Id] = other with { Members = [.. other.Members.Where(m => !moving.Contains(m.CharacterId))] };
                }
            }

            _groups[group.Id] = group;
        }

        return Task.CompletedTask;
    }

    public Task DeleteGroupAsync(uint groupId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _groups.Remove(groupId);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Groups survive a world restart (vmangos groups / group_member, ObjectMgr::LoadGroups) over real sockets: two world hosts
/// in turn over the same accounts, characters and group rows.
/// </summary>
public sealed class GroupPersistenceWorldTests
{
    private static byte[] CString(string value)
    {
        var writer = new PacketWriter(value.Length + 1);
        writer.WriteCString(value);
        return writer.ToArray();
    }

    private static async Task<byte[]> ReadGroupListAsync(WorldTestClient client, uint others)
    {
        while (true)
        {
            byte[] list = await client.ReadUntilAsync(WorldOpcode.SmsgGroupList);
            if (list.Length >= 6 && BinaryPrimitives.ReadUInt32LittleEndian(list.AsSpan(2)) == others)
            {
                return list;
            }
        }
    }

    /// <summary>The second host runs over the first one's accounts, characters and directory, as a restarted daemon would.</summary>
    private static WorldTestHost Restart(WorldTestHost before, InMemoryGroupStore groups) => WorldTestHost.Start(configureServices: services =>
    {
        services.AddSingleton<IGroupStore>(groups);
        services.AddSingleton<IAccountStore>(before.Accounts);
        services.AddSingleton<IAccountAdmin>(before.Accounts);
        services.AddSingleton<ICharacterStore>(before.Characters);
        services.AddSingleton<ICharacterLifeStore>(before.Characters);
        services.AddSingleton<CharacterDirectory>(before.Directory);
    });

    private static async Task<WorldTestClient> ComeBackAsync(WorldTestHost host, WorldTestHost before, string account, string character)
    {
        Account stored = (await before.Accounts.FindByUsernameAsync(account))!;
        CharacterRecord record = (await before.Characters.GetByAccountAsync(stored.Id)).Single(c => c.Name == character);
        WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync(account, stored.SessionKey!);
        await client.LoginAsync((ulong)record.Id);
        return client;
    }

    [Fact]
    public async Task AParty_SurvivesAWorldRestart_AndItsLeaderSeesItOnComingBack()
    {
        var groups = new InMemoryGroupStore();
        WorldTestHost first = WorldTestHost.Start(configureServices: services => services.AddSingleton<IGroupStore>(groups));
        uint groupId;
        await using (first)
        {
            await using WorldTestClient leader = await first.EnterWorldAsync("LEADA", "Leader");
            await using WorldTestClient member = await first.EnterWorldAsync("MEMBA", "Member");
            await leader.SendAsync(WorldOpcode.CmsgGroupInvite, CString("member"));
            await member.ReadUntilAsync(WorldOpcode.SmsgGroupInvite);
            await member.SendAsync(WorldOpcode.CmsgGroupAccept, []);
            await ReadGroupListAsync(leader, others: 1);

            // Make it a raid with the member as assistant, so more than the membership has to come back.
            await leader.SendAsync(WorldOpcode.CmsgGroupRaidConvert, []);
            await WorldTestHost.WaitForAsync(() => groups.All() is [{ IsRaid: true, Members.Count: 2 }], "the raid to reach storage");
            groupId = groups.All()[0].Id;
        }

        // The daemon is gone; a new one starts over the same rows.
        await using WorldTestHost second = Restart(first, groups);
        Group? restored = await second.OnWorldAsync(() => second.WorldServices.GetRequiredService<SocialFeature>().Context.Groups.Groups.SingleOrDefault());
        Assert.NotNull(restored);
        Assert.Equal((groupId, true, "Leader"), (restored.Id, restored.IsRaid, restored.LeaderName));
        Assert.Equal(["Leader", "Member"], restored.Members.Select(m => m.Name));

        await using WorldTestClient back = await ComeBackAsync(second, first, "LEADA", "Leader");
        byte[] list = await ReadGroupListAsync(back, others: 1);
        Assert.Equal((byte)GroupType.Raid, list[0]);
        var reader = new PacketReader(list.AsSpan(6).ToArray());
        Assert.Equal("Member", reader.ReadCString());
    }

    [Fact]
    public async Task ARestoredGroup_TakesBackItsLeadersStoredPermanentBind_AfterARestart()
    {
        // vmangos ObjectMgr::LoadGroups attaches the group_instance rows of the leader to each group it loads
        // (ObjectMgr.cpp:5463-5513): the stored group (groups lane) and the stored group bind (instances lane) meet at start.
        const uint Deadmines = 36;
        var groups = new InMemoryGroupStore();
        WorldTestHost first = WorldTestHost.Start(configureServices: services => services.AddSingleton<IGroupStore>(groups));
        int leaderId;
        await using (first)
        {
            await using WorldTestClient leader = await first.EnterWorldAsync("LEADE", "Leadere");
            await using WorldTestClient member = await first.EnterWorldAsync("MEMBE", "Membere");
            await leader.SendAsync(WorldOpcode.CmsgGroupInvite, CString("membere"));
            await member.ReadUntilAsync(WorldOpcode.SmsgGroupInvite);
            await member.SendAsync(WorldOpcode.CmsgGroupAccept, []);
            await WorldTestHost.WaitForAsync(() => groups.All() is [{ Members.Count: 2 }], "the party to reach storage");
            leaderId = groups.All()[0].LeaderId;
        }

        InMemoryInstanceStore.Seed.Value = new InstanceStoreSnapshot([new InstanceRecord(150, Deadmines, 0)], [], [], [])
        {
            GroupBinds = [new GroupInstanceBindRecord(leaderId, 150, Permanent: true)],
        };
        WorldTestHost second;
        try
        {
            second = Restart(first, groups);
        }
        finally
        {
            InMemoryInstanceStore.Seed.Value = null;
        }

        await using (second)
        {
            InstanceBind? bind = await second.OnWorldAsync(() =>
            {
                Group group = second.WorldServices.GetRequiredService<SocialFeature>().Context.Groups.Groups.Single();
                return second.WorldServices.GetRequiredService<InstanceFeature>().Instances.GetGroupBind(group, Deadmines);
            });
            Assert.NotNull(bind);
            Assert.Equal((150u, true), (bind.Value.Save.InstanceId, bind.Value.Permanent));
        }
    }

    [Fact]
    public async Task ADisbandedGroup_IsDeletedFromStorage()
    {
        var groups = new InMemoryGroupStore();
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton<IGroupStore>(groups));
        await using WorldTestClient leader = await host.EnterWorldAsync("LEADB", "Leaderb");
        await using WorldTestClient member = await host.EnterWorldAsync("MEMBB", "Memberb");
        await leader.SendAsync(WorldOpcode.CmsgGroupInvite, CString("memberb"));
        await member.ReadUntilAsync(WorldOpcode.SmsgGroupInvite);
        await member.SendAsync(WorldOpcode.CmsgGroupAccept, []);
        await WorldTestHost.WaitForAsync(() => groups.All().Count == 1, "the party row");

        await member.SendAsync(WorldOpcode.CmsgGroupDisband, []); // leaving a two-member party disbands it
        await WorldTestHost.WaitForAsync(() => groups.All().Count == 0, "the party row to be deleted");
    }

    [Fact]
    public async Task ADeletedOfflineMember_LeavesTheStoredGroup_AndIsNotWrittenBack()
    {
        // vmangos Player::DeleteFromDB -> RemoveFromGroup: GroupDataModule removes the member row with the character, and
        // SocialCharacterDeleteHook takes the slot out of the live group, so the next sync stores the group without it
        // (a three-member party, so the deletion does not disband it).
        var groups = new InMemoryGroupStore();
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton<IGroupStore>(groups));
        await using WorldTestClient leader = await host.EnterWorldAsync("LEADD", "Leaderd");
        await using WorldTestClient stays = await host.EnterWorldAsync("STAYD", "Staysd");
        byte[] key = await host.AddAccountAsync("GONED");
        WorldTestClient gone = await host.ConnectAsync();
        await gone.AuthenticateAsync("GONED", key);
        await gone.CreateCharacterAsync("Goned");
        Account goneAccount = (await host.Accounts.FindByUsernameAsync("GONED"))!;
        int goneId = (await host.Characters.GetByAccountAsync(goneAccount.Id)).Single().Id;
        await gone.LoginAsync((ulong)goneId);

        uint others = 0;
        foreach ((WorldTestClient client, string name) in new[] { (stays, "staysd"), (gone, "goned") })
        {
            await leader.SendAsync(WorldOpcode.CmsgGroupInvite, CString(name));
            await client.ReadUntilAsync(WorldOpcode.SmsgGroupInvite);
            await client.SendAsync(WorldOpcode.CmsgGroupAccept, []);
            await ReadGroupListAsync(leader, ++others);
        }

        await WorldTestHost.WaitForAsync(() => groups.All() is [{ Members.Count: 3 }], "the three-member party to reach storage");
        uint groupId = groups.All()[0].Id;

        // The member logs out (it stays in the group while offline), then deletes the character at the character list.
        await gone.DisposeAsync();
        await host.WaitForWorldAsync(() => !host.World.IsOnline(ObjectGuid.Player((uint)goneId)), "the member to leave the world");
        await using (WorldTestClient again = await host.ConnectAsync())
        {
            await again.AuthenticateAsync("GONED", key);
            byte[] guid = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(guid, (ulong)goneId);
            await again.SendAsync(WorldOpcode.CmsgCharDelete, guid);
            Assert.Equal((byte)CharResult.CharDeleteSuccess, (await again.ReadUntilAsync(WorldOpcode.SmsgCharDelete))[0]);
        }

        await WorldTestHost.WaitForAsync(() => groups.Group(groupId) is { Members.Count: 2 }, "the stored party without the deleted member");
        SocialGroupPersistenceFeature persistence = host.WorldServices.GetRequiredService<SocialGroupPersistenceFeature>();
        await persistence.Writes!.FlushAsync();
        int savesAfterDelete = groups.Saves;
        await host.OnWorldAsync(persistence.SyncNow);
        await persistence.Writes.FlushAsync();

        GroupRecord stored = groups.Group(groupId)!;
        Assert.DoesNotContain(stored.Members, m => m.CharacterId == goneId);
        Assert.Equal(savesAfterDelete, groups.Saves); // nothing left to write: the live group no longer names it
    }

    [Fact]
    public async Task StoredGroupsThatCannotComeBack_AreDeletedAtStart()
    {
        var groups = new InMemoryGroupStore();
        groups.Seed(new GroupRecord(77, 4040, 3, 4040, 2, false, new ulong[8], [new GroupMemberRecord(4040, 0, false), new GroupMemberRecord(4041, 0, false)]));
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton<IGroupStore>(groups));

        await WorldTestHost.WaitForAsync(() => groups.Group(77) is null, "the orphaned group row to be deleted");
        Assert.Equal([77u], host.WorldServices.GetRequiredService<SocialGroupPersistenceFeature>().Restored!.Dropped);
    }

    [Fact]
    public async Task WithoutAStore_GroupsLiveInMemoryOnly_AsBefore()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        Assert.Null(host.WorldServices.GetRequiredService<SocialGroupPersistenceFeature>().Writes);
    }
}
