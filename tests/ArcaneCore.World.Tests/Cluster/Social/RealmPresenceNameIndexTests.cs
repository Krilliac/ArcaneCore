using ArcaneCore.Kernel.Cluster.Social;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Social;

/// <summary>The presence directory's name index follows the guid that currently owns the name; a stale removal or rename must not delete another guid's entry.</summary>
public sealed class RealmPresenceNameIndexTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static RealmPresenceEntry Presence(ulong guid, string name, long version = 1, long generation = 1)
        => new(guid, name, AccountId: (int)guid, WorkerId: "w", GatewayId: "g", MapId: 0, ZoneId: 1, AreaId: 1, Level: 10, Class: 1, Race: 1,
            Gender: 0, GuildId: 0, GroupId: 0, PresenceVersion: version, SessionGeneration: generation, UpdatedAtUtc: BaseTime);

    [Fact]
    public void RemovingTheOlderHolderOfAReusedName_KeepsTheNewHoldersNameLookup()
    {
        var directory = new InMemoryRealmPresenceDirectory();
        Assert.Equal(PresenceMutationResult.Applied, directory.TryUpsert(Presence(1, "Bob"), out _));
        Assert.Equal(PresenceMutationResult.Applied, directory.TryUpsert(Presence(2, "Bob"), out _));

        Assert.Equal(PresenceMutationResult.Applied, directory.TryRemove(1, expectedPresenceVersion: 1, expectedSessionGeneration: 1));

        Assert.Equal(2ul, directory.FindByName("Bob")?.CharacterGuid);
    }

    [Fact]
    public void RenamingTheOlderHolderOfAReusedName_KeepsTheNewHoldersNameLookup()
    {
        var directory = new InMemoryRealmPresenceDirectory();
        Assert.Equal(PresenceMutationResult.Applied, directory.TryUpsert(Presence(1, "Bob"), out _));
        Assert.Equal(PresenceMutationResult.Applied, directory.TryUpsert(Presence(2, "Bob"), out _));

        Assert.Equal(PresenceMutationResult.Applied, directory.TryUpsert(Presence(1, "Robert", version: 2), out _));

        Assert.Equal(2ul, directory.FindByName("Bob")?.CharacterGuid);
        Assert.Equal(1ul, directory.FindByName("Robert")?.CharacterGuid);
    }

    [Fact]
    public void RemovingTheOnlyHolder_StillDropsTheName()
    {
        var directory = new InMemoryRealmPresenceDirectory();
        Assert.Equal(PresenceMutationResult.Applied, directory.TryUpsert(Presence(1, "Bob"), out _));

        Assert.Equal(PresenceMutationResult.Applied, directory.TryRemove(1, 1, 1));

        Assert.Null(directory.FindByName("Bob"));
    }
}
