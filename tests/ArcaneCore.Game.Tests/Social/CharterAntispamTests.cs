using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Social;

/// <summary>
/// The guild and charter name antispam check (vmangos AntispamInterface::filterMessage at HandlePetitionBuyOpcode,
/// PetitionsHandler.cpp:87-95, and at Guild::Create, Guild.cpp:130-137). vmangos ships no filter; the operator's patterns
/// (World:Guild:CharterSpamPatterns) are the filter here, empty by default.
/// </summary>
public sealed class CharterAntispamTests
{
    private static (uint Command, string Text, uint Error) Result(byte[] payload)
    {
        var reader = new PacketReader(payload);
        return (reader.ReadUInt32(), reader.ReadCString(), reader.ReadUInt32());
    }

    [Theory]
    [InlineData("Gold Sellers", true)]
    [InlineData("gOLDSELLERS united", true)]
    [InlineData("Cheap Gold Sel lers", true)]
    [InlineData("Golden Order", false)]
    [InlineData("Sellers of Gold", false)]
    public void APattern_MatchesWithoutCaseAndSpaces(string name, bool spam)
        => Assert.Equal(spam, new PatternCharterAntispamFilter(["goldsellers", "  ", ""]).IsSpam(name));

    [Fact]
    public void BuyingACharterWithASpamName_IsRefusedAsAnInvalidName_AndCostsNothing()
    {
        using var k = new PetitionKit();
        k.Guilds.Antispam = new PatternCharterAntispamFilter(["goldsellers"]);
        Player a = k.Add(1);
        string? refused = null;
        k.Petitions.SpamRefused += (_, name) => refused = name;

        k.Petitions.Buy(a, k.Npc.Guid, "Gold Sellers");

        Assert.Equal((0u, "Gold Sellers", (uint)GuildCommandError.NameInvalid), Result(Assert.Single(k.Sent(a, WorldOpcode.SmsgGuildCommandResult))));
        Assert.Null(k.Charter(a));
        Assert.Equal(10_000u, a.Money);
        Assert.Equal("Gold Sellers", refused);

        // An honest name still buys.
        k.ClearAll();
        Assert.NotNull(k.Buy(a, "Honest Traders"));
    }

    [Fact]
    public void WithoutPatterns_NothingIsFiltered_AsInVmangos()
    {
        using var k = new PetitionKit();
        Assert.Null(k.Guilds.Antispam);
        Player a = k.Add(1);
        Assert.NotNull(k.Buy(a, "Gold Sellers"));
    }

    [Fact]
    public void GuildCreate_AndTheCharterTurnIn_RefuseASpamName()
    {
        using var k = new PetitionKit();
        Player a = k.Add(1);
        Player b = k.Add(2);
        k.Guilds.Antispam = new PatternCharterAntispamFilter(["goldsellers"]);

        Assert.Equal(GuildAdminResult.NameInvalid, k.Guilds.Create(a.Guid.Low, "Gold Sellers", out Guild? none));
        Assert.Null(none);
        Assert.Null(k.Guilds.CreateFromPetition(b.Guid.Low, "Gold Sellers", [], petitionId: 0));
        Assert.Equal(GuildAdminResult.Ok, k.Guilds.Create(a.Guid.Low, "Honest Traders", out _));
    }
}
