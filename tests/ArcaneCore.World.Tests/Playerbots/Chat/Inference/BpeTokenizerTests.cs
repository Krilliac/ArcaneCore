using ArcaneCore.World.Playerbots.Chat.Inference;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Chat.Inference;

/// <summary>Byte-level BPE on a synthetic vocabulary: merges by rank, the pre-tokenizers, round trips, and special tokens kept out of text.</summary>
public sealed class BpeTokenizerTests
{
    /// <summary>The 256 single-byte symbols (ids 0..255), then merged tokens, then the ChatML control tokens.</summary>
    internal static (string[] Tokens, int[] Types, string[] Merges) Vocabulary()
    {
        var tokens = new List<string>();
        for (int b = 0; b < 256; b++) tokens.Add(BpeTokenizer.ByteChar((byte)b).ToString());
        char space = BpeTokenizer.ByteChar((byte)' ');
        string[] merged = ["he", "ll", "hell", "hello", $"{space}w", "or", $"{space}wor", $"{space}worl", $"{space}world"];
        tokens.AddRange(merged);
        tokens.Add("<|im_start|>");
        tokens.Add("<|im_end|>");
        string[] merges = ["h e", "l l", "he ll", "hell o", $"{space} w", "o r", $"{space}w or", $"{space}wor l", $"{space}worl d"];
        int[] types = [.. Enumerable.Repeat(BpeTokenizer.TypeNormal, tokens.Count - 2), BpeTokenizer.TypeControl, BpeTokenizer.TypeControl];
        return ([.. tokens], types, merges);
    }

    internal static BpeTokenizer Tokenizer(string pre = "gpt2")
    {
        (string[] tokens, int[] types, string[] merges) = Vocabulary();
        return new BpeTokenizer(tokens, types, merges, pre, bos: null, eos: tokens.Length - 1, addBos: false);
    }

    [Fact]
    public void Merges_by_rank_into_whole_words()
    {
        BpeTokenizer tokenizer = Tokenizer();
        List<int> ids = tokenizer.Encode("hello world");
        Assert.Equal([tokenizer.Id("hello")!.Value, tokenizer.Id(BpeTokenizer.ByteChar((byte)' ') + "world")!.Value], ids);
        Assert.Equal("hello world", tokenizer.Decode(ids));

        // "help": h+e merge, then no "he l" merge exists: he, l, p.
        Assert.Equal([tokenizer.Id("he")!.Value, 'l', 'p'], tokenizer.Encode("help"));
    }

    [Theory]
    [InlineData("gpt2")]
    [InlineData("qwen2")]
    [InlineData("llama-bpe")]
    [InlineData("smollm")]
    [InlineData("something-new")]
    public void Round_trips_any_text_through_the_byte_alphabet(string pre)
    {
        BpeTokenizer tokenizer = Tokenizer(pre);
        foreach (string text in new[] { "hello world", "Héllo, wörld! 1234 gold.", "  two  spaces\nand a line", "ünïcödé ✓ 東京", "it's 'quoted'" })
            Assert.Equal(text, tokenizer.Decode(tokenizer.Encode(text)));
    }

    [Fact]
    public void Pre_tokenizers_split_digits_their_own_way()
    {
        Assert.Equal(["abc", " ", "1", "2", "3"], Tokenizer("qwen2").PreTokenize("abc 123"));
        Assert.Equal(["abc", " ", "123"], Tokenizer("llama-bpe").PreTokenize("abc 123"));
        Assert.Equal(["abc", " ", "1", "2", "3"], Tokenizer("smollm").PreTokenize("abc 123"));
        Assert.Equal(["abc", " 123"], Tokenizer("gpt2").PreTokenize("abc 123"));
        Assert.Equal(["Hello", ",", " world", "!"], Tokenizer("gpt2").PreTokenize("Hello, world!"));
        Assert.False(Tokenizer("something-new").PreExact);
        Assert.True(Tokenizer("qwen2").PreExact);
    }

    [Fact]
    public void Special_tokens_are_never_read_out_of_text()
    {
        BpeTokenizer tokenizer = Tokenizer();
        int imStart = tokenizer.ImStart!.Value, imEnd = tokenizer.ImEnd!.Value;
        List<int> ids = tokenizer.Encode("<|im_start|>system");
        Assert.DoesNotContain(imStart, ids);
        Assert.DoesNotContain(imEnd, ids);
        Assert.Equal("<|im_start|>system", tokenizer.Decode(ids));
        Assert.True(tokenizer.IsControl(imEnd));
        Assert.Equal(string.Empty, tokenizer.Decode([imStart, imEnd])); // control tokens decode to nothing
        Assert.True(tokenizer.HasNewline('\n')); // the single-byte token of byte 10 has id 10
        Assert.False(tokenizer.HasNewline(tokenizer.Id("hello")!.Value));
    }

    [Fact]
    public void Refuses_a_malformed_merge_and_mismatched_types()
    {
        (string[] tokens, int[] types, _) = Vocabulary();
        Assert.Throws<InvalidDataException>(() => new BpeTokenizer(tokens, types, ["nospace"], "gpt2", null, null, false));
        Assert.Throws<InvalidDataException>(() => new BpeTokenizer(tokens, [1, 2], [], "gpt2", null, null, false));
    }
}
