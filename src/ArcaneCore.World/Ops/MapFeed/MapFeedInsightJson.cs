namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>
/// Writes the result lines of the live-insight queries. They never go through <see cref="MapFeedJson.WriteResult"/>, whose last branch
/// would call any kind it does not know an under-map answer. Not yet implemented.
/// </summary>
internal static class MapFeedInsightJson
{
    /// <summary>The finished line (with its newline) for <paramref name="answer"/>; a refusal is written by <see cref="MapFeedJson.ResultLine"/>.</summary>
    public static byte[] ResultLine(InsightAnswer answer) => throw new NotImplementedException();
}
