using ArcaneCore.World.Playerbots.Chat.Inference.Sampling;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Chat.Inference.Sampling;

/// <summary>
/// The bot chat line constraint over a toy vocabulary: role/markup tokens and '|' never survive, the end-of-turn token stays reachable
/// when exempt, a line break is banned once the line has content, "&lt;" followed by "think" is blocked, extra bans apply, and both the
/// by-index and the scanning paths agree.
/// </summary>
public sealed class ChatLineConstraintTests
{
    private const int ImStart = 0;
    private const int ImEnd = 1;
    private const int Pipe = 2;
    private const int Hello = 3;
    private const int Newline = 4;
    private const int Lt = 5;
    private const int Think = 6;
    private const int ThinkTag = 7;
    private const int Space = 8;
    private const int Friend = 9;
    private const int DotNewline = 10;
    private const int Thinking = 11;

    private static readonly string?[] Vocab =
    [
        "<|im_start|>", "<|im_end|>", "|", "Hello", "\n", "<", "think", "<think>", " ", " friend", ".\n", "thinking",
    ];

    private static Candidates Flat() => Candidates.FromLogits(new float[Vocab.Length]);

    private static bool Banned(Candidates c, int id)
    {
        for (int i = 0; i < c.Count; i++)
        {
            if (c[i].Id == id)
            {
                return float.IsNegativeInfinity(c[i].Logit);
            }
        }

        throw new InvalidOperationException("token missing");
    }

    [Fact]
    public void MarkupAndPipeTokensAreAlwaysBannedUnlessExempt()
    {
        var constraint = new ChatLineConstraint(Vocab, exemptTokens: [ImEnd]);
        Candidates c = Flat();
        constraint.Apply(c);
        Assert.True(Banned(c, ImStart));
        Assert.True(Banned(c, Pipe));
        Assert.True(Banned(c, ThinkTag));
        Assert.False(Banned(c, ImEnd));
        Assert.False(Banned(c, Hello));
        Assert.False(Banned(c, Lt));
        Assert.Equal(3, constraint.AlwaysBannedCount);
    }

    [Fact]
    public void LineBreaksAreBannedOnlyOnceTheLineHasContent()
    {
        var constraint = new ChatLineConstraint(Vocab);
        Candidates c = Flat();
        constraint.Apply(c);
        Assert.False(Banned(c, Newline));

        constraint.Accept(Space); // whitespace is not content
        c = Flat();
        constraint.Apply(c);
        Assert.False(Banned(c, Newline));

        constraint.Accept(Hello);
        c = Flat();
        constraint.Apply(c);
        Assert.True(Banned(c, Newline));
        Assert.True(Banned(c, DotNewline));
        Assert.False(Banned(c, Friend));

        constraint.Reset();
        c = Flat();
        constraint.Apply(c);
        Assert.False(Banned(c, Newline));
    }

    [Fact]
    public void ThinkIsBannedRightAfterAnOpenAngle()
    {
        var constraint = new ChatLineConstraint(Vocab);
        constraint.Accept(Lt);
        Candidates c = Flat();
        constraint.Apply(c);
        Assert.True(Banned(c, Think));
        Assert.True(Banned(c, Thinking));

        constraint.Accept(Hello);
        c = Flat();
        constraint.Apply(c);
        Assert.False(Banned(c, Think));
    }

    [Fact]
    public void ExtraBannedIdsApply()
    {
        var constraint = new ChatLineConstraint(Vocab, bannedTokens: [Friend]);
        Candidates c = Flat();
        constraint.Apply(c);
        Assert.True(Banned(c, Friend));
        Assert.True(constraint.IsBanned(Friend));
        Assert.False(constraint.IsBanned(Hello));
    }

    [Fact]
    public void ScanningPathAgreesWithTheIndexedPath()
    {
        var constraint = new ChatLineConstraint(Vocab, exemptTokens: [ImEnd], bannedTokens: [Friend]);
        constraint.Accept(Hello);
        constraint.Accept(Lt);
        Candidates indexed = Flat();
        constraint.Apply(indexed);

        Candidates scanned = Flat();
        SamplingMath.SortCanonical(scanned);
        Assert.False(scanned.IdOrdered);
        constraint.Apply(scanned);

        for (int id = 0; id < Vocab.Length; id++)
        {
            Assert.Equal(Banned(indexed, id), Banned(scanned, id));
            Assert.Equal(constraint.IsBanned(id), Banned(indexed, id));
        }
    }

    [Fact]
    public void ChainNeverSamplesMarkupAndCanStillEndTheTurn()
    {
        // Markup tokens get the highest logits; the chain must never pick them, and the exempt end-of-turn token stays sampleable.
        float[] logits = [9f, 1f, 9f, 0f, 0f, 0f, 0f, 9f, 0f, 0f, 0f, 0f];
        var constraint = new ChatLineConstraint(Vocab, exemptTokens: [ImEnd]);
        SamplerChain chain = SamplerChain.Build(
            new SamplerConfig { Seed = 7, TopK = 0, TopP = 1f, MinP = 0f, Temperature = 1f },
            constraint,
            Vocab.Length);
        bool sawEnd = false;
        for (int i = 0; i < 300; i++)
        {
            SampleResult r = chain.Sample(logits);
            Assert.True(r.Ok);
            Assert.NotEqual(ImStart, r.Token);
            Assert.NotEqual(Pipe, r.Token);
            Assert.NotEqual(ThinkTag, r.Token);
            sawEnd |= r.Token == ImEnd;
        }

        Assert.True(sawEnd);
    }
}
