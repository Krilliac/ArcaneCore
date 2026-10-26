using ArcaneCore.Protocol;

namespace ArcaneCore.World.Gm.Audit;

/// <summary>One answered question of a GM survey. <see cref="Answer"/> is kept as sent (the 1.12 client rates 0 to 5; no clamp).</summary>
public sealed record GmSurveyAnswer(uint QuestionId, byte Answer, string Comment);

/// <summary>A parsed CMSG_GMSURVEY_SUBMIT: the survey, its answered questions and the closing comment (text already cleaned).</summary>
public sealed record GmSurveySubmission(uint SurveyId, IReadOnlyList<GmSurveyAnswer> Answers, string Comment);

/// <summary>A kept survey: when (Unix seconds), from which character, and what was sent.</summary>
public sealed record GmSurveyRecord(long Unix, int CharacterId, GmSurveySubmission Survey);

/// <summary>
/// The CMSG_GMSURVEY_SUBMIT layout: <c>u32 survey id, 10 x (u32 question id, u8 answer, cstring comment), cstring comment</c>
/// (wow_messages vanilla CMSG_GMSURVEY_SUBMIT, the vendored oracle). Unused question slots are sent as id 0. Which of two
/// shapes the retail 1.12.1 client really sends is not confirmed by a capture: all ten entries always present, or the
/// entries ended early by a question id of 0. Both are accepted, so an honest player is never refused for the shape.
/// Parsing never throws: a survey is optional feedback and a malformed one must not cost the player the connection.
/// </summary>
public static class GmSurveyPacket
{
    /// <summary>Question slots in the packet.</summary>
    public const int MaxQuestions = 10;

    /// <summary>Parse a payload; false (and null) when it fits neither shape.</summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out GmSurveySubmission? survey)
        => TryParseShape(payload, allTenEntries: true, out survey) || TryParseShape(payload, allTenEntries: false, out survey);

    private static bool TryParseShape(ReadOnlySpan<byte> payload, bool allTenEntries, out GmSurveySubmission? survey)
    {
        survey = null;
        var reader = new PacketReader(payload);
        if (!reader.TryReadUInt32(out uint surveyId))
        {
            return false;
        }

        var answers = new List<GmSurveyAnswer>(MaxQuestions);
        for (int i = 0; i < MaxQuestions; i++)
        {
            if (!reader.TryReadUInt32(out uint questionId))
            {
                return false;
            }

            if (!allTenEntries && questionId == 0)
            {
                break;
            }

            if (!reader.TryReadByte(out byte answer) || !reader.TryReadCString(PacketReader.MaxCStringBytes, out string comment))
            {
                return false;
            }

            if (questionId != 0)
            {
                answers.Add(new GmSurveyAnswer(questionId, answer, GmTicketHandlers.CleanText(comment)));
            }
        }

        if (!reader.TryReadCString(PacketReader.MaxCStringBytes, out string closing) || reader.Remaining != 0)
        {
            return false;
        }

        survey = new GmSurveySubmission(surveyId, answers, GmTicketHandlers.CleanText(closing));
        return true;
    }
}
