using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_GMSURVEY_SUBMIT: VanillaClientMessage, IWorldMessage {
    /// <summary>
    /// cmangos: Survey ID: found in GMSurveySurveys.dbc
    /// </summary>
    public required uint SurveyId { get; set; }
    public const int QuestionsLength = 10;
    public required GmSurveyQuestion[] Questions { get; set; }
    /// <summary>
    /// cmangos: Answer comment: Unused in stock UI, can be only set by calling Lua function
    /// cmangos: Answer comment max sizes in bytes: Vanilla - 8106:8110, TBC - 11459:11463, Wrath - 582:586
    /// </summary>
    public required string AnswerComment { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(SurveyId, cancellationToken).ConfigureAwait(false);

        foreach (var v in Questions) {
            await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
        }

        await w.WriteCString(AnswerComment, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 810, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 810, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_GMSURVEY_SUBMIT> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var surveyId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var questions = new GmSurveyQuestion[QuestionsLength];
        for (var i = 0; i < QuestionsLength; ++i) {
            questions[i] = await Vanilla.GmSurveyQuestion.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false);
        }

        var answerComment = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        return new CMSG_GMSURVEY_SUBMIT {
            SurveyId = surveyId,
            Questions = questions,
            AnswerComment = answerComment,
        };
    }

    internal int Size() {
        var size = 0;

        // survey_id: Generator.Generated.DataTypeInteger
        size += 4;

        // questions: Generator.Generated.DataTypeArray
        size += Questions.Sum(e => e.Size());

        // answer_comment: Generator.Generated.DataTypeCstring
        size += AnswerComment.Length + 1;

        return size;
    }

}

