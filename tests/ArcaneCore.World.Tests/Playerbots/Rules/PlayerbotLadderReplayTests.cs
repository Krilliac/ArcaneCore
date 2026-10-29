using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Rules;

/// <summary>
/// The bot goal ladder as it was hard-coded in <c>PlayerbotBrain</c>, recorded on a fixed seed BEFORE the ladder became rules (spec P1,
/// "Replay test"). The constants below are that recording; the rule-driven ladder must reproduce them.
/// </summary>
public sealed class PlayerbotLadderReplayTests
{
    internal const string RecordedDigest = "DCB6EB348C404A277D82A846ABE3E4D80C4D438C4C591B9B9807152A0F39CFBC";
    internal const string RecordedHistogram = "1=310,2=316,3=280,4=298,5=285,6=309,7=288,8=305,9=271,10=302,11=251,12=254,13=274,14=223,15=266,16=231,fall=9937";

    [Fact]
    public void TheHardCodedLadder_ReplaysToTheRecordedChoices()
    {
        LadderReplay.Result result = LadderReplay.Run((_, _) => PlayerbotLadder.HardCodedOrder);

        Assert.True(result.Histogram == RecordedHistogram && result.Digest == RecordedDigest,
            $"histogram={result.Histogram} digest={result.Digest}");
    }
}
