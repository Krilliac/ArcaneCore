using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Kernel.Social;
using Xunit;

namespace ArcaneCore.Game.Tests.Social;

/// <summary>
/// A signer who is already in a guild cannot found another one (vmangos Guild::AddMember skips nobody, but
/// Player::RemovePetitionsAndSigns drops the signature the moment a signer joins a guild). A stored signature of
/// an already-guilded character is therefore stale: it must not count towards the turn-in minimum.
/// </summary>
public sealed class PetitionGuildedSignerLoadTests
{
    [Fact]
    public void Load_DropsSignaturesOfCharactersAlreadyInAGuild_AndPersistsTheTrim()
    {
        using var k = new PetitionKit();
        k.Add(1);
        k.Add(2);
        Player guilded = k.Add(3);
        Assert.Equal(GuildAdminResult.Ok, k.Guilds.Create(guilded.Guid.Low, "Founded", out Guild? _));
        k.Persistence.PetitionWrites.Clear();
        var fresh = new PetitionManager(k.F.Context);

        fresh.Load([new PetitionData(1, 1, 100, "Good", [new PetitionSignatureData(2, 2), new PetitionSignatureData(3, 3)])]);

        Petition petition = Assert.Single(fresh.All);
        Assert.Equal([new PetitionSignature(2, 2)], petition.Signatures);
        Assert.Contains("save:1:1", k.Persistence.PetitionWrites);
    }

    [Fact]
    public void GuildedSignatureFromStorage_DoesNotCompleteThePetition()
    {
        using var k = new PetitionKit(options: new GuildOptions { MinPetitionSigns = 2 });
        k.Add(1);
        k.Add(2);
        Player guilded = k.Add(3);
        Assert.Equal(GuildAdminResult.Ok, k.Guilds.Create(guilded.Guid.Low, "Founded", out Guild? _));
        var fresh = new PetitionManager(k.F.Context);

        fresh.Load([new PetitionData(1, 1, 100, "Good", [new PetitionSignatureData(2, 2), new PetitionSignatureData(3, 3)])]);

        Assert.False(fresh.IsComplete(Assert.Single(fresh.All)));
    }
}
