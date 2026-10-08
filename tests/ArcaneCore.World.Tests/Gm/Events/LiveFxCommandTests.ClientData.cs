using ArcaneCore.Data.Content.ClientEffects;
using ArcaneCore.Protocol;
using ArcaneCore.World.Gm.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Events;

/// <summary>
/// <c>.fx</c> with the client tables of <c>World:GmCommands:LiveFxDbcDirectory</c>: unknown ids are refused before
/// anything is sent, known ids go out as before, <c>.fx lookup</c> searches the tables, and the preset sound ids are
/// exactly the ones the presets send (and, with <c>ARCANECORE_TEST_DBC_DIR</c>, exist in the real client files).
/// </summary>
public sealed partial class LiveFxCommandTests
{
    private const string DirectoryKey = "World:GmCommands:LiveFxDbcDirectory";

    private static Task<Realm> StartWithClientDataAsync(string directory)
        => StartAsync(configuration: new() { [DirectoryKey] = directory });

    /// <summary>Every system line the GM gets for one command (lookups answer with several).</summary>
    private static async Task<List<string>> LinesAsync(Realm realm, string line)
    {
        await realm.Gm.SendChatAsync(ChatType.Say, Language.Common, line);
        var lines = new List<string>();
        foreach ((WorldOpcode opcode, byte[] payload) in await realm.Gm.CollectAsync(Quiet))
        {
            if (opcode == WorldOpcode.SmsgMessagechat && ChatMessage.Parse(payload) is { Type: ChatType.System } chat)
            {
                lines.Add(chat.Text);
            }
        }

        return lines;
    }

    [Fact]
    public async Task WithClientData_UnknownIdsAreRefused_AndNothingIsSent()
    {
        string dir = LiveFxDbcImages.WriteDirectory();
        await using Realm realm = await StartWithClientDataAsync(dir);

        foreach ((string line, WorldOpcode opcode, string reply) in new[]
        {
            (".fx sound 9999 zone", WorldOpcode.SmsgPlaySound, "Sound #9999 does not exist."),
            (".fx music 9999", WorldOpcode.SmsgPlayMusic, "Sound #9999 does not exist."),
            (".fx cinematic 3", WorldOpcode.SmsgTriggerCinematic, "Cinematic #3 does not exist."),
            (".fx visual 99", WorldOpcode.SmsgPlaySpellVisual, "Spell visual kit #99 does not exist."),
        })
        {
            var (got, gm, near, far) = await EffectAsync(realm, line, opcode);
            Assert.Equal(reply, got);
            Assert.Empty(gm);
            Assert.Empty(near);
            Assert.Empty(far);
        }
    }

    [Fact]
    public async Task WithClientData_KnownIdsAreSent()
    {
        string dir = LiveFxDbcImages.WriteDirectory();
        await using Realm realm = await StartWithClientDataAsync(dir);

        Assert.Equal([U32(8574)], (await EffectAsync(realm, ".fx sound 8574", WorldOpcode.SmsgPlaySound)).Gm);
        Assert.Equal([U32(8440)], (await EffectAsync(realm, ".fx music 8440", WorldOpcode.SmsgPlayMusic)).Gm);
        Assert.Equal([U32(2)], (await EffectAsync(realm, ".fx cinematic 2", WorldOpcode.SmsgTriggerCinematic)).Gm);
        Assert.Single((await EffectAsync(realm, ".fx visual 7", WorldOpcode.SmsgPlaySpellVisual)).Gm);
    }

    [Fact]
    public async Task AMissingFile_LeavesThatKindUnchecked_TheOthersStayChecked()
    {
        string dir = LiveFxDbcImages.WriteDirectory(withSounds: false);
        await using Realm realm = await StartWithClientDataAsync(dir);

        Assert.Equal([U32(9999)], (await EffectAsync(realm, ".fx sound 9999", WorldOpcode.SmsgPlaySound)).Gm);
        Assert.Equal("Cinematic #3 does not exist.", (await EffectAsync(realm, ".fx cinematic 3", WorldOpcode.SmsgTriggerCinematic)).Reply);
        Assert.Equal(["SoundEntries.dbc is not loaded (set World:GmCommands:LiveFxDbcDirectory)."], await LinesAsync(realm, ".fx lookup sound cheer"));
    }

    [Fact]
    public async Task WithoutClientData_EveryIdIsSentUnchecked_AndLookupSaysWhy()
    {
        await using Realm realm = await StartAsync();

        Assert.Equal([U32(9999)], (await EffectAsync(realm, ".fx sound 9999", WorldOpcode.SmsgPlaySound)).Gm);
        Assert.Equal([U32(77)], (await EffectAsync(realm, ".fx cinematic 77", WorldOpcode.SmsgTriggerCinematic)).Gm);
        Assert.Single((await EffectAsync(realm, ".fx visual 12345", WorldOpcode.SmsgPlaySpellVisual)).Gm);
        Assert.Equal(["CinematicSequences.dbc is not loaded (set World:GmCommands:LiveFxDbcDirectory)."], await LinesAsync(realm, ".fx lookup cinematic 2"));
    }

    [Fact]
    public async Task Lookup_FindsByIdAndByNamePart_InEveryTable()
    {
        string dir = LiveFxDbcImages.WriteDirectory();
        await using Realm realm = await StartWithClientDataAsync(dir);

        Assert.Equal(
            [@"Sound #8440 Darkmoon_Faire_Music (type 28): Sound\Music\WorldEvents\DarkMoonFaire_2.mp3 (+1 more files)"],
            await LinesAsync(realm, ".fx lookup sound 8440"));
        Assert.Equal([@"Sound #8574 CrowdCheerHorde2 (type 1): Sound\Spells\CrowdCheerHorde2.wav"], await LinesAsync(realm, ".fx lookup sound CHEER"));
        Assert.Equal(["ZoneMusic #1 ZoneMusicDarkmoon: day sound #8440 Darkmoon_Faire_Music, night sound #3439 HornGoober"], await LinesAsync(realm, ".fx lookup music darkmoon"));
        Assert.Equal(["Cinematic #2: sound #8574 CrowdCheerHorde2, cameras 41,42"], await LinesAsync(realm, ".fx lookup cinematic 2"));
        Assert.Equal(["Kit #7: anim 54, effects HolyGlow (#11), sound #8574 CrowdCheerHorde2"], await LinesAsync(realm, ".fx lookup visual holy_glow"));
        string silithyst = "WorldStateUI #126 (map 1, area 1377): %2313w/%2317w | Alliance Silithyst Collected";
        Assert.Equal([silithyst], await LinesAsync(realm, ".fx lookup worldstate 2317"));
        Assert.Equal([silithyst], await LinesAsync(realm, ".fx lookup worldstate silithyst"));
        Assert.Equal(["No world state matches '231'."], await LinesAsync(realm, ".fx lookup worldstate 231"));
        Assert.Equal(["No sound matches 'thunderfury'."], await LinesAsync(realm, ".fx lookup sound thunderfury"));
    }

    [Fact]
    public async Task Lookup_IsBoundedTo20Lines_AndCountsTheRest()
    {
        string dir = LiveFxDbcImages.WriteDirectory();
        await using Realm realm = await StartWithClientDataAsync(dir);

        List<string> lines = await LinesAsync(realm, ".fx lookup sound bulk_");

        Assert.Equal(LiveFxLookup.MaxLines + 1, lines.Count);
        Assert.All(lines.Take(LiveFxLookup.MaxLines), l => Assert.StartsWith("Sound #90", l, StringComparison.Ordinal));
        Assert.Equal("... 5 more match(es); narrow the search.", lines[^1]);
        Assert.Contains("Syntax", (await LinesAsync(realm, ".fx lookup spells fire"))[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RefusesAMissingDirectory_AndAMalformedFile()
    {
        Assert.Throws<DirectoryNotFoundException>(() => LiveFxDataFeature.Load(Path.Combine(Path.GetTempPath(), "arcanecore-no-such-" + Guid.NewGuid().ToString("N")), NullLogger.Instance));

        string dir = LiveFxDbcImages.WriteDirectory();
        File.WriteAllBytes(Path.Combine(dir, "SpellVisualKit.dbc"), LiveFxDbcImages.Build(34, new object?[34]));
        Assert.Throws<InvalidDataException>(() => LiveFxDataFeature.Load(dir, NullLogger.Instance));
    }

    [Fact]
    public async Task EveryPresetsSoundIds_AreExactlyTheSoundsItSends()
    {
        await using Realm realm = await StartAsync();

        foreach (LiveFxCommands.Preset preset in LiveFxCommands.Presets)
        {
            var gm = new List<(WorldOpcode Opcode, byte[] Payload)>();
            await RunAsync(realm, ".fx event " + preset.Name, gm);
            gm.AddRange(await realm.Gm.CollectAsync(Quiet));
            uint[] sent = [.. gm.Where(p => p.Opcode is WorldOpcode.SmsgPlaySound or WorldOpcode.SmsgPlayMusic).Select(p => BitConverter.ToUInt32(p.Payload))];
            Assert.Equal(preset.SoundIds.Order(), sent.Order());
        }
    }

    [LiveFxRealDbcFact]
    public void RealClientData_HoldsEveryPresetSound_AndEveryTableLoads()
    {
        // Reports Skipped (naming the variable) when ARCANECORE_TEST_DBC_DIR is unset, never a silent pass.
        string dir = Environment.GetEnvironmentVariable(LiveFxRealDbcFactAttribute.Variable)!;
        LiveFxData data = LiveFxDataFeature.Load(dir, NullLogger.Instance);

        // The client-effective copies (patch-2.MPQ > patch.MPQ > dbc.MPQ) of build 5875.
        Assert.Equal(4623, data.Sounds!.Count);
        Assert.Equal(99, data.ZoneMusic!.Count);
        Assert.Equal(10, data.Cinematics!.Count);
        Assert.Equal(1772, data.VisualKits!.Count);
        Assert.Equal(775, data.VisualEffects!.Count);
        Assert.Equal(20, data.WorldStates!.Count);

        string[] missing = [.. LiveFxCommands.Presets.SelectMany(p => p.SoundIds.Where(id => !data.Sounds.Contains(id)).Select(id => $"{p.Name}: {id}"))];
        Assert.True(missing.Length == 0, string.Join(", ", missing));
        Assert.NotEmpty(LiveFxCommands.Presets.SelectMany(p => p.SoundIds));
        Assert.Equal("Darkmoon_Faire_Music", data.Sounds.Find(LiveFxCommands.DarkmoonFaireMusic)!.Name);

        // Cross-table references resolve (a reader taking the wrong columns would not): the client's own files leave 2 of the
        // music-set sound slots and 27 of the 1120 kit sounds dangling, so the check is a floor, not "all".
        uint[] musicSounds = [.. data.ZoneMusic.Rows.SelectMany(m => new[] { m.DaySound, m.NightSound }).Where(s => s != 0)];
        Assert.True(musicSounds.Count(data.Sounds.Contains) >= musicSounds.Length - 2, "ZoneMusic sound slots that resolve");
        uint[] kitSounds = [.. data.VisualKits.Rows.Select(k => k.SoundId).Where(s => s != 0)];
        Assert.True(kitSounds.Count(data.Sounds.Contains) >= kitSounds.Length * 95 / 100, "SpellVisualKit sounds that resolve");
        Assert.Contains(LiveFxLookup.Run(data, "sound", "8440")!, l => l.StartsWith("Sound #8440 Darkmoon_Faire_Music", StringComparison.Ordinal));
        // Warsong Gulch: the Alliance flag captures field 1581 is shown by WorldStateUI row 2 as "%1581w/%1601w".
        Assert.Contains(LiveFxLookup.Run(data, "worldstate", "1581")!, l => l.StartsWith("WorldStateUI #2 (map 489, area 0): %1581w/%1601w | Alliance flag captures", StringComparison.Ordinal));
    }
}

/// <summary>A fact that needs the developer's build-5875 DBC directory; Skipped (never a silent pass) without it.</summary>
public sealed class LiveFxRealDbcFactAttribute : FactAttribute
{
    public const string Variable = "ARCANECORE_TEST_DBC_DIR";

    public LiveFxRealDbcFactAttribute()
    {
        string? dir = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            Skip = $"{Variable} is not set to a directory of build-5875 DBC files.";
        }
    }
}
