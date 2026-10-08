using ArcaneCore.Data.World.Creatures;
using Xunit;

namespace ArcaneCore.Data.Tests;

public sealed class ScriptDevTextImportTests
{
    [Fact]
    public void ScriptTexts_FeedTheExistingTextStore_WithEventAiRowsTakingPrecedence()
    {
        var importer = new CreatureDumpImporter();
        importer.Read(new StringReader("""
            INSERT INTO `creature_ai_texts` (`entry`,`content_default`,`sound`,`type`,`language`,`emote`,`broadcast_text_id`) VALUES
            (-1230001,'EventAI preferred',0,0,0,0,0);
            INSERT INTO `script_texts` (`entry`,`content_default`,`sound`,`type`,`language`,`emote`,`broadcast_text_id`) VALUES
            (-1230001,'ScriptDev collision',0,1,0,0,0),
            (-1230002,'ScriptDev yell',77,1,0,5,0);
            """));

        Assert.Equal(2, importer.BuildReport().AiTexts);
        Assert.Equal("EventAI preferred", importer.AiSnapshot().Texts.Single(t => t.Entry == -1230001).Content);
        CreatureAiTextRow text = importer.AiSnapshot().Texts.Single(t => t.Entry == -1230002);
        Assert.Equal(("ScriptDev yell", (byte)1, 77u, 5u), (text.Content, text.Type, text.Sound, text.Emote));
    }
}
