using System.Text.Json;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.MockClient.Tests;

public sealed class ItemFeedbackProbeTests
{
    [Fact]
    public async Task RejectsNonWhitelistArgumentsWithoutOpeningAConnection()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int result = await ItemFeedbackProbe.MainAsync(
            ["--account", "PROBE", "--character", "Probe", "--password-env", "PROBE_PASSWORD", "--say", ".gm on"],
            output, error);

        Assert.Equal(1, result);
        Assert.Contains("ArgumentException", error.ToString(), StringComparison.Ordinal);
        using JsonDocument report = JsonDocument.Parse(output.ToString());
        Assert.Equal("failed", report.RootElement.GetProperty("Outcome").GetString());
    }

    [Fact]
    public void NearbyForeignSpellAndItemPacketsDoNotImplyOwnMissingItemSucceeded()
    {
        var spell = new PacketWriter(16);
        spell.WritePackedGuid(24);
        spell.WritePackedGuid(24);
        spell.WriteUInt32(433);
        Assert.False(ItemFeedbackProbe.IsOwnSuccess(new((ushort)WorldOpcode.SmsgSpellStart, spell.ToArray()), 25));
        Assert.True(ItemFeedbackProbe.IsOwnSuccess(new((ushort)WorldOpcode.SmsgSpellGo, spell.ToArray()), 24));
        Assert.False(ItemFeedbackProbe.IsOwnSuccess(new((ushort)WorldOpcode.SmsgItemPushResult, BitConverter.GetBytes(24UL)), 25));
        Assert.True(ItemFeedbackProbe.IsOwnSuccess(new((ushort)WorldOpcode.SmsgItemPushResult, BitConverter.GetBytes(24UL)), 24));
        Assert.Throws<MockProtocolException>(() => ItemFeedbackProbe.IsOwnSuccess(new((ushort)WorldOpcode.SmsgSpellStart, []), 24));
    }

    [Fact]
    public void FailureProofRejectsWrongCodeLengthGuidAndBagInsteadOfAcceptingAnyError()
    {
        byte[] body = new byte[18];
        body[0] = 23;
        Assert.Null(ItemFeedbackProbe.ValidateFailure(new((ushort)WorldOpcode.SmsgInventoryChangeFailure, body)));
        body[0] = 59;
        Assert.NotNull(ItemFeedbackProbe.ValidateFailure(new((ushort)WorldOpcode.SmsgInventoryChangeFailure, body)));
        body[0] = 23;
        body[1] = 1;
        Assert.NotNull(ItemFeedbackProbe.ValidateFailure(new((ushort)WorldOpcode.SmsgInventoryChangeFailure, body)));
        body[1] = 0;
        body[17] = 1;
        Assert.NotNull(ItemFeedbackProbe.ValidateFailure(new((ushort)WorldOpcode.SmsgInventoryChangeFailure, body)));
        Assert.NotNull(ItemFeedbackProbe.ValidateFailure(new((ushort)WorldOpcode.SmsgInventoryChangeFailure, [23])));
    }

    [Fact]
    public async Task MissingItemProducesExactItemNotFoundFailureAndCleanLogout()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(deadline.Token);
        await server.AddAccountAsync("ITEMPROBE", "ProbePass1", deadline.Token);
        const string variable = "ARCANE_ITEM_FEEDBACK_TEST_PASSWORD";
        string? previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, "ProbePass1");
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            int result = await ItemFeedbackProbe.MainAsync(
                ["--account", "ITEMPROBE", "--character", "Probehuman", "--password-env", variable,
                    "--realm", $"127.0.0.1:{server.RealmEndpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}"],
                output, error);

            Assert.Equal(0, result);
            using JsonDocument report = JsonDocument.Parse(output.ToString());
            Assert.Equal("passed", report.RootElement.GetProperty("Outcome").GetString());
            Assert.Equal(23, report.RootElement.GetProperty("ActualErrorCode").GetInt32());
            Assert.Equal(18, report.RootElement.GetProperty("ActualBodyLength").GetInt32());
            Assert.True(report.RootElement.GetProperty("CleanLogout").GetBoolean());
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }
}
