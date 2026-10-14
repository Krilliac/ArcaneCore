using ArcaneCore.Kernel.Characters;
using ArcaneCore.Game.Entities;
using Xunit;

namespace ArcaneCore.Game.Tests.Characters;

public sealed class CharacterOwnershipSnapshotTests
{
    [Fact]
    public void PlayerSnapshotCarriesTheWorldOwnershipToken()
    {
        Player player = TestWorld.CreatePlayer(42, 1, 2, new FakeSession());
        var token = new CharacterOwnershipToken("world", 9, Guid.NewGuid());
        player.CharacterOwnership = token;

        Assert.Same(token, player.CreateSnapshot(1).Ownership);
    }
}
