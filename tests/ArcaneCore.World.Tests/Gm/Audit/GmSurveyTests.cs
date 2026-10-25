using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.AntiCheat.PacketHeuristics;
using ArcaneCore.World.Gm.Audit;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Tests.PacketHeuristics;
using Microsoft.Extensions.DependencyInjection;
using WowWorldMessages.Vanilla;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Audit;

/// <summary>CMSG_GMSURVEY_SUBMIT: the parser (both wire shapes) and the handler's effect, with no reply and no kick.</summary>
public sealed class GmSurveyTests
{
    private static async Task<byte[]> OracleAsync(uint surveyId, (uint Id, byte Answer, string Comment)[] real, string closing)
    {
        var questions = new GmSurveyQuestion[10];
        for (int i = 0; i < questions.Length; i++)
        {
            questions[i] = i < real.Length
                ? new GmSurveyQuestion { QuestionId = real[i].Id, Answer = real[i].Answer, Comment = real[i].Comment }
                : new GmSurveyQuestion { QuestionId = 0, Answer = 0, Comment = string.Empty };
        }

        using var stream = new MemoryStream();
        await new CMSG_GMSURVEY_SUBMIT { SurveyId = surveyId, Questions = questions, AnswerComment = closing }.WriteBodyAsync(stream);
        return stream.ToArray();
    }

    private static byte[] StopAtZero(uint surveyId, (uint Id, byte Answer, string Comment)[] real, string closing)
    {
        var writer = new PacketWriter(64);
        writer.WriteUInt32(surveyId);
        foreach ((uint id, byte answer, string comment) in real)
        {
            writer.WriteUInt32(id);
            writer.WriteByte(answer);
            writer.WriteCString(comment);
        }

        writer.WriteUInt32(0);
        writer.WriteCString(closing);
        return writer.ToArray();
    }

    private static readonly (uint Id, byte Answer, string Comment)[] Three = [(4, 5, "great"), (7, 0, string.Empty), (9, 3, "ok")];

    private static void AssertThree(GmSurveySubmission? survey)
    {
        Assert.NotNull(survey);
        Assert.Equal(12u, survey.SurveyId);
        Assert.Equal([new GmSurveyAnswer(4, 5, "great"), new GmSurveyAnswer(7, 0, string.Empty), new GmSurveyAnswer(9, 3, "ok")], survey.Answers);
        Assert.Equal("thanks", survey.Comment);
    }

    [Fact]
    public async Task TryParse_TheOracleEncoding_ThreeQuestionsAndSevenEmptySlots()
    {
        byte[] payload = await OracleAsync(12, Three, "thanks");
        Assert.True(GmSurveyPacket.TryParse(payload, out GmSurveySubmission? survey));
        AssertThree(survey);
    }

    [Fact]
    public void TryParse_TheStopAtZeroShape_GivesTheSameResult()
    {
        Assert.True(GmSurveyPacket.TryParse(StopAtZero(12, Three, "thanks"), out GmSurveySubmission? survey));
        AssertThree(survey);
    }

    [Fact]
    public async Task TryParse_Malformed_IsFalse_NotAnException()
    {
        byte[] good = await OracleAsync(12, Three, "thanks");
        Assert.False(GmSurveyPacket.TryParse(good.AsSpan(0, 10), out GmSurveySubmission? survey));
        Assert.Null(survey);
        Assert.False(GmSurveyPacket.TryParse([.. good, 1, 2, 3], out _));
        Assert.False(GmSurveyPacket.TryParse([.. StopAtZero(12, Three, "thanks"), 1, 2, 3], out _));
        Assert.False(GmSurveyPacket.TryParse([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF], out _));
        Assert.False(GmSurveyPacket.TryParse([], out _));
    }

    [Fact]
    public async Task TryParse_CleansText_RemovesControlCharacters_AndCapsLength()
    {
        string longText = new('x', 5000);
        byte[] payload = await OracleAsync(1, [(2, 1, "a\u0007b\u0001c")], longText);
        Assert.True(GmSurveyPacket.TryParse(payload, out GmSurveySubmission? survey));
        Assert.Equal("abc", Assert.Single(survey!.Answers).Comment);
        Assert.Equal(ArcaneCore.Kernel.Gm.GmAuditLimits.MaxTextLength, survey.Comment.Length);
    }

    [Fact]
    public async Task Handler_StoresTheSurvey_SendsNothing_AndKeepsTheSession_EvenForAMalformedOne()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("SURV", "Surveyor");
        Player player = await host.PlayerAsync("Surveyor");
        GmAuditFeature audit = host.WorldServices.GetRequiredService<GmAuditFeature>();
        await client.CollectAsync();

        await client.SendAsync(WorldOpcode.CmsgGmsurveySubmit, await OracleAsync(12, Three, "thanks"));
        await host.WaitForWorldAsync(() => audit.Surveys(10).Count == 1, "the survey to be stored");
        GmSurveyRecord record = Assert.Single(audit.Surveys(10));
        Assert.Equal((int)player.Guid.Low, record.CharacterId);
        AssertThree(record.Survey);

        await client.SendAsync(WorldOpcode.CmsgGmsurveySubmit, [1, 2, 3]);
        await client.SendAsync(WorldOpcode.CmsgToggleCloak, []);
        await host.WaitForWorldAsync(() => (player.Flags & PlayerFlags.HideCloak) != 0, "the session still runs after a malformed survey");
        Assert.Single(audit.Surveys(10));
        Assert.DoesNotContain(await client.CollectAsync(), p => p.Opcode != WorldOpcode.SmsgUpdateObject); // only the cloak toggle's own update; the survey got no reply
    }

    [Fact]
    public async Task OpeningCinematicAndSurvey_AreHandled_AndNeverCountedAsUnhandled()
    {
        var unhandled = new UnhandledOpcodeTracker();
        await using var host = WorldTestHost.Start(configureServices: c => c.AddSingleton(unhandled));
        (WorldTestClient client, WorldSession session) = await PacketHeuristicsTestKit.EnterClientAsync(host, "NOOPS", "Noopsone");
        await using (client)
        {
            (PacketHeuristicsFeature feature, _) = PacketHeuristicsTestKit.Enable(host);

            Assert.True(host.Opcodes.TryGet(WorldOpcode.CmsgOpeningCinematic, out OpcodeHandler? cinematic));
            Assert.False(cinematic!.Stripped);
            Assert.True(host.Opcodes.TryGet(WorldOpcode.CmsgGmsurveySubmit, out OpcodeHandler? survey));
            Assert.False(survey!.Stripped);

            await client.SendAsync(WorldOpcode.CmsgOpeningCinematic, []);
            await client.SendAsync(WorldOpcode.CmsgGmsurveySubmit, await OracleAsync(3, Three, string.Empty));
            await PacketHeuristicsTestKit.SettleAsync(host, client, session);

            Assert.Equal(0, unhandled.Total);
            Assert.Empty(unhandled.Snapshot());
            Assert.Equal(0, feature.Count(PacketCheck.UnknownOpcode, PacketTraffic.Player));
        }
    }
}
