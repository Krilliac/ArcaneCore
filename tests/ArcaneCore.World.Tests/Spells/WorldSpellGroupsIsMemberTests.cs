using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Social;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

/// <summary>
/// <see cref="ISpellGroupResolver.IsMember"/> must answer exactly what <c>GetGroupMembers(unit, raid).Contains(other)</c> answers
/// (solo, party, raid sub-group, outsider, the unit itself) and must not allocate: it runs per query on the map threads.
/// </summary>
public sealed class WorldSpellGroupsIsMemberTests
{
    private static readonly ulong[] NoIcons = new ulong[8];

    private const int LastCharacter = 12;

    [Fact]
    public void IsMember_MatchesGetGroupMembersContains_ForEveryPairInSoloPartyAndRaid()
    {
        using var kit = new Kit();
        kit.Restore(
            Party(1, 1, 1, 2, 3, 4, 5),
            Raid(2, 6, (6, 0), (7, 0), (8, 0), (9, 1), (10, 1), (11, 1)));
        // 12 is in no group; 99 is not a character at all.
        var resolver = new WorldSpellGroups(() => kit.Groups);
        Player[] units = [.. Enumerable.Range(1, LastCharacter).Select(i => kit.Players[i])];
        ObjectGuid[] others = [.. units.Select(u => u.Guid), ObjectGuid.Player(99)];

        int checkedPairs = 0;
        foreach (Player unit in units)
        {
            foreach (ObjectGuid other in others)
            {
                foreach (bool raid in new[] { false, true })
                {
                    bool expected = resolver.GetGroupMembers(unit, raid).Contains(other);
                    Assert.Equal(expected, resolver.IsMember(unit, other, raid));
                    checkedPairs++;
                }
            }
        }

        Assert.Equal(units.Length * others.Length * 2, checkedPairs);
        // Spot-check the expectations themselves so the comparison cannot pass vacuously.
        Assert.True(resolver.IsMember(kit.Players[1], kit.Players[5].Guid, raid: false));
        Assert.False(resolver.IsMember(kit.Players[1], kit.Players[6].Guid, raid: true));
        Assert.True(resolver.IsMember(kit.Players[6], kit.Players[8].Guid, raid: false));
        Assert.False(resolver.IsMember(kit.Players[6], kit.Players[9].Guid, raid: false));
        Assert.True(resolver.IsMember(kit.Players[6], kit.Players[9].Guid, raid: true));
        Assert.True(resolver.IsMember(kit.Players[12], kit.Players[12].Guid, raid: false));
        Assert.False(resolver.IsMember(kit.Players[12], kit.Players[1].Guid, raid: true));
    }

    [Fact]
    public void IsMember_WithoutAGroupManager_IsSelfOnly()
    {
        using var kit = new Kit();
        var resolver = new WorldSpellGroups(static () => null);
        var throwing = new WorldSpellGroups(static () => throw new InvalidOperationException("social not attached"));
        foreach (ISpellGroupResolver r in new ISpellGroupResolver[] { resolver, throwing, NoGroupResolver.Instance })
        {
            Assert.True(r.IsMember(kit.Players[1], kit.Players[1].Guid, raid: true));
            Assert.False(r.IsMember(kit.Players[1], kit.Players[2].Guid, raid: false));
        }

        Assert.Throws<ArgumentNullException>(() => resolver.IsMember(null!, ObjectGuid.Player(1), false));
    }

    [Fact]
    public void IsMember_AllocatesNothing_ForSoloPartyAndRaid()
    {
        using var kit = new Kit();
        kit.Restore(
            Party(1, 1, 1, 2, 3, 4, 5),
            Raid(2, 6, (6, 0), (7, 0), (8, 0), (9, 1), (10, 1), (11, 1)));
        ISpellGroupResolver resolver = new WorldSpellGroups(() => kit.Groups);
        Player solo = kit.Players[12];
        Player partyMember = kit.Players[1];
        Player raider = kit.Players[6];
        ObjectGuid partyOther = kit.Players[5].Guid;
        ObjectGuid raidOther = kit.Players[9].Guid;
        bool sink = false;

        void SoloWindow() { for (int i = 0; i < 1000; i++) { sink ^= resolver.IsMember(solo, solo.Guid, false) ^ resolver.IsMember(solo, partyOther, true); } }
        void PartyWindow() { for (int i = 0; i < 1000; i++) { sink ^= resolver.IsMember(partyMember, partyOther, false) ^ resolver.IsMember(partyMember, raidOther, true); } }
        void RaidWindow() { for (int i = 0; i < 1000; i++) { sink ^= resolver.IsMember(raider, raidOther, true) ^ resolver.IsMember(raider, raidOther, false); } }

        AllocationProbe.AssertEveryCleanWindowAtMost(SoloWindow, 0, "solo IsMember");
        AllocationProbe.AssertEveryCleanWindowAtMost(PartyWindow, 0, "5-man IsMember");
        AllocationProbe.AssertEveryCleanWindowAtMost(RaidWindow, 0, "raid IsMember");
        GC.KeepAlive(sink);
    }

    [Fact]
    public void GetGroupMembersContains_Allocates_SoTheProbeCanSeeTheDifference()
    {
        using var kit = new Kit();
        var resolver = new WorldSpellGroups(() => kit.Groups);
        Player solo = kit.Players[12];
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool sink = false;
        for (int i = 0; i < 100; i++)
        {
            sink ^= resolver.GetGroupMembers(solo, false).Contains(solo.Guid);
        }

        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before > 0);
        GC.KeepAlive(sink);
    }

    private static GroupRecord Party(uint id, int leader, params int[] members)
        => new(id, leader, (byte)LootMethod.GroupLoot, leader, Group.DefaultLootThreshold, false, NoIcons,
            [.. members.Select(m => new GroupMemberRecord(m, 0, false))]);

    private static GroupRecord Raid(uint id, int leader, params (int Id, byte SubGroup)[] members)
        => new(id, leader, (byte)LootMethod.GroupLoot, leader, Group.DefaultLootThreshold, true, NoIcons,
            [.. members.Select(m => new GroupMemberRecord(m.Id, m.SubGroup, false))]);

    private sealed class Kit : IDisposable
    {
        private readonly ServiceProvider _services;
        private readonly WorldRuntime _world;
        private readonly SocialFeature _feature;

        public Kit()
        {
            _services = new ServiceCollection().AddSingleton<ISocialStore>(new EmptyStore()).BuildServiceProvider();
            _world = new WorldRuntime(new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 },
                new NullSaveQueue(), NullLogger<WorldRuntime>.Instance);
            var directory = new CharacterDirectory();
            for (int i = 1; i <= LastCharacter; i++)
            {
                directory.Add(new CharacterIdentity(i, i, $"P{i}", 1, 0, 1));
                Players[i] = new Player(new CharacterRecord
                {
                    Id = i, AccountId = i, Name = $"P{i}", Race = (byte)Race.Human,
                    Class = (byte)Class.Warrior, Gender = (byte)Gender.Male, Level = 1, ZoneId = 12,
                }, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), new QuietSession(i));
            }

            _feature = new SocialFeature(directory, _services.GetRequiredService<IServiceScopeFactory>(), NullLoggerFactory.Instance);
            _feature.Attach(_world);
        }

        public Dictionary<int, Player> Players { get; } = [];

        public GroupManager Groups => _feature.Context.Groups;

        public void Restore(params GroupRecord[] records)
        {
            GroupRestoreResult result = Groups.Restore(records);
            Assert.Equal(records.Length, result.Restored.Count);
        }

        public void Dispose()
        {
            _world.Dispose();
            _services.Dispose();
        }
    }

    private sealed class QuietSession(int accountId) : IPlayerSession
    {
        public int AccountId => accountId;
        public AccountSecurity Security => AccountSecurity.Player;
        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload) { }
        public void ProcessWorldPackets(Player player) { }
        public void Kick() { }
        public void OnLoggedOut() { }
    }

    private sealed class EmptyStore : ISocialStore
    {
        public Task<IReadOnlyList<SocialEntry>> GetSocialAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SocialEntry>>([]);

        public Task SetSocialAsync(int characterId, int otherId, SocialFlags flags, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<GuildData>> GetGuildsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GuildData>>([]);

        public Task SaveGuildAsync(GuildData guild, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteGuildAsync(int guildId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
