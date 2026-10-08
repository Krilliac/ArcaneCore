using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
public class GmSurveyQuestion {
    /// <summary>
    /// cmangos: questions found in GMSurveyQuestions.dbc
    /// ref to i'th GMSurveySurveys.dbc field (all fields in that dbc point to fields in GMSurveyQuestions.dbc)
    /// </summary>
    public required uint QuestionId { get; set; }
    /// <summary>
    /// Rating: hardcoded limit of 0-5 in pre-Wrath, ranges defined in GMSurveyAnswers.dbc Wrath+
    /// </summary>
    public required byte Answer { get; set; }
    /// <summary>
    /// Usage: `GMSurveyAnswerSubmit(question, rank, comment)`
    /// cmangos: Unused in stock UI, can be only set by calling Lua function
    /// </summary>
    public required string Comment { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(QuestionId, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(Answer, cancellationToken).ConfigureAwait(false);

        await w.WriteCString(Comment, cancellationToken).ConfigureAwait(false);

    }

    public static async Task<GmSurveyQuestion> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var questionId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var answer = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var comment = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        return new GmSurveyQuestion {
            QuestionId = questionId,
            Answer = answer,
            Comment = comment,
        };
    }

    internal int Size() {
        var size = 0;

        // question_id: Generator.Generated.DataTypeInteger
        size += 4;

        // answer: Generator.Generated.DataTypeInteger
        size += 1;

        // comment: Generator.Generated.DataTypeCstring
        size += Comment.Length + 1;

        return size;
    }

}

