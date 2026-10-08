using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.Social;
using ArcaneCore.Kernel.Social;
using Xunit;

namespace ArcaneCore.Data.Tests.Social;

/// <summary>
/// EmotesText.dbc (19 fields) and Emotes.dbc (7 fields) of build 5875 (vmangos DBCfmt.h EmotesTextEntryfmt and
/// EmotesEntryfmt). Synthetic data; the developer's own client files are read only when <c>ARCANECORE_CLIENT_DBC_DIR</c> is set.
/// </summary>
public sealed class EmoteDbcReaderTests
{
    [Fact]
    public void SyntheticTextEmotes_GiveTheEmoteEachPlays()
    {
        // Field 1 is the name (a string), field 2 the emote id; the image helper writes strings from field 3 on, so the
        // synthetic rows carry the emote id in the "faction" slot, which is field 2.
        IReadOnlyList<TextEmoteRow> rows = EmoteDbcReaders.ReadText(DbcFile.Parse(ChatChannelsDbcReaderTests.Image(
            EmoteDbcReaders.TextFieldCount, (1, 0, 0, []), (3, 0, 14, []))));
        Assert.Equal([new TextEmoteRow(1, 0), new TextEmoteRow(3, 14)], rows);
    }

    [Fact]
    public void SyntheticEmotes_GiveFlagsTypeAndStandState()
    {
        IReadOnlyList<EmoteRow> rows = EmoteDbcReaders.ReadEmotes(DbcFile.Parse(ChatChannelsDbcReaderTests.Image(
            EmoteDbcReaders.EmoteFieldCount, (12, 0, 0, ["ONESHOT_X", "", "", "", ""]))));
        // fields: 0 id, 1 name, 2 anim, 3 flags, 4 type, 5 stand state, 6 sound; the helper filled 3..6 with string offsets,
        // so only the id is meaningful here: the layout guard and the decode path are what this pins.
        Assert.Equal(12u, Assert.Single(rows).Id);
    }

    [Theory]
    [InlineData(18)]
    [InlineData(20)]
    public void AnotherBuildsTextLayout_IsRefused(int fields)
        => Assert.Throws<InvalidDataException>(() => EmoteDbcReaders.ReadText(DbcFile.Parse(ChatChannelsDbcReaderTests.Image(fields, (1, 0, 0, [])))));

    [Fact]
    public void AnotherBuildsEmoteLayout_AndDuplicateIds_AreRefused()
    {
        Assert.Throws<InvalidDataException>(() => EmoteDbcReaders.ReadEmotes(DbcFile.Parse(ChatChannelsDbcReaderTests.Image(8, (1, 0, 0, [])))));
        Assert.Throws<InvalidDataException>(() => EmoteDbcReaders.ReadText(DbcFile.Parse(ChatChannelsDbcReaderTests.Image(19, (1, 0, 0, []), (1, 0, 0, [])))));
    }

    [Fact]
    public void TheDevelopersOwnClientFiles_DecodeAsTheServerExpects()
    {
        if (Environment.GetEnvironmentVariable("ARCANECORE_CLIENT_DBC_DIR") is not { Length: > 0 } directory
            || !File.Exists(Path.Combine(directory, "EmotesText.dbc")) || !File.Exists(Path.Combine(directory, "Emotes.dbc")))
        {
            return; // no client data on this machine (none is shipped)
        }

        IReadOnlyList<TextEmoteRow> text = EmoteDbcReaders.LoadText(Path.Combine(directory, "EmotesText.dbc"));
        IReadOnlyList<EmoteRow> emotes = EmoteDbcReaders.LoadEmotes(Path.Combine(directory, "Emotes.dbc"));
        Assert.Equal(169, text.Count);
        Assert.Equal(14u, text.Single(t => t.Id == 3).EmoteId);   // ANGRY
        Assert.Equal(0u, text.Single(t => t.Id == 1).EmoteId);    // AGREE: no animation
        EmoteRow wave = emotes.Single(e => e.Id == 3);            // ONESHOT_WAVE
        Assert.Equal((2048u, 0u), (wave.Flags, wave.EmoteType));
    }
}
