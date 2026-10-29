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
/// <see cref="ISpellGroupResolver.AppendGroupMembers"/> must append exactly the GUIDs, in the order, of <c>GetGroupMembers(unit, raid)</c>
/// (solo, party, raid sub-group, whole raid, no manager), keep what the list already held, and not allocate once the list has capacity.
/// </summary>
public sealed class WorldSpellGroupsAppendTests
{
    private static readonly ulong[] NoIcons = new ulong[8];

    private const int LastCharacter = 12;

    [Fact]
    public void AppendGroupMembers_AppendsTheSameSequenceAsGetGroupMembers_ForEveryUnitInSoloPartyAndRaid()
    {
        using var kit = new Kit();
        kit.Restore(
            Party(1, 1, 1, 2, 3, 4, 5),
            Raid(2, 6, (6, 0), (7, 0), (8, 0), (9, 1), (10, 1), (11, 1)));
        ISpellGroupResolver resolver = new WorldSpellGroups(() => kit.Groups);

        int compared = 0;
        foreach (Player unit in Enumerable.Range(1, LastCharacter).Select(i => kit.Players[i]))
        {
            foreach (bool raid in new[] { false, true })
            {
                var appended = new List<ObjectGuid>();
                resolver.AppendGroupMembers(unit, raid, appended);
                Assert.Equal(resolver.GetGroupMembers(unit, raid), appended);
                compared++;
            }
        }

        Assert.Equal(LastCharacter * 2, compared);
        // Expectations themselves, so the comparison cannot pass vacuously.
        var party = new List<ObjectGuid>();
        resolver.AppendGroupMembers(kit.Players[1], false, party);
        Assert.Equal(5, party.Count);
        var subGroup = new List<ObjectGuid>();
        resolver.AppendGroupMembers(kit.Players[6], false, subGroup);
        Assert.Equal([kit.Players[6].Guid, kit.Players[7].Guid, kit.Players[8].Guid], subGroup);
        var wholeRaid = new List<ObjectGuid>();
        resolver.AppendGroupMembers(kit.Players[6], true, wholeRaid);
        Assert.Equal(6, wholeRaid.Count);
        var solo = new List<ObjectGuid>();
        resolver.AppendGroupMembers(kit.Players[12], true, solo);
        Assert.Equal([kit.Players[12].Guid], solo);
    }

    [Fact]
    public void AppendGroupMembers_KeepsWhatTheListAlreadyHolds()
    {
        using var kit = new Kit();
        kit.Restore(Party(1, 1, 1, 2, 3));
        ISpellGroupResolver resolver = new WorldSpellGroups(() => kit.Groups);
        ObjectGuid existing = ObjectGuid.Player(99);
        var into = new List<ObjectGuid> { existing };

        resolver.AppendGroupMembers(kit.Players[1], false, into);

        Assert.Equal(existing, into[0]);
        Assert.Equal([existing, .. resolver.GetGroupMembers(kit.Players[1], false)], into);
    }

    [Fact]
    public void AppendGroupMembers_WithoutAGroupManager_AppendsTheUnitAlone()
    {
        using var kit = new Kit();
        ISpellGroupResolver none = new WorldSpellGroups(static () => null);
        ISpellGroupResolver throwing = new WorldSpellGroups(static () => throw new InvalidOperationException("social not attached"));
        foreach (ISpellGroupResolver resolver in new ISpellGroupResolver[] { none, throwing, NoGroupResolver.Instance })
        {
            var into = new List<ObjectGuid>();
            resolver.AppendGroupMembers(kit.Players[1], raid: true, into);
            Assert.Equal([kit.Players[1].Guid], into);
        }

        Assert.Throws<ArgumentNullException>(() => none.AppendGroupMembers(null!, false, []));
    }

    [Fact]
    public void AppendGroupMembers_AllocatesNothing_OnceTheListHasCapacity()
    {
        using var kit = new Kit();
        kit.Restore(
            Party(1, 1, 1, 2, 3, 4, 5),
            Raid(2, 6, (6, 0), (7, 0), (8, 0), (9, 1), (10, 1), (11, 1)));
        ISpellGroupResolver resolver = new WorldSpellGroups(() => kit.Groups);
        Player solo = kit.Players[12];
        Player partyMember = kit.Players[1];
        Player raider = kit.Players[6];
        var into = new List<ObjectGuid>(32);

        void SoloWindow() { for (int i = 0; i < 1000; i++) { into.Clear(); resolver.AppendGroupMembers(solo, true, into); } }
        void PartyWindow() { for (int i = 0; i < 1000; i++) { into.Clear(); resolver.AppendGroupMembers(partyMember, false, into); } }
        void RaidWindow() { for (int i = 0; i < 1000; i++) { into.Clear(); resolver.AppendGroupMembers(raider, i % 2 == 0, into); } }

        AllocationProbe.AssertEveryCleanWindowAtMost(SoloWindow, 0, "solo AppendGroupMembers");
        AllocationProbe.AssertEveryCleanWindowAtMost(PartyWindow, 0, "5-man AppendGroupMembers");
        AllocationProbe.AssertEveryCleanWindowAtMost(RaidWindow, 0, "raid AppendGroupMembers");
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
