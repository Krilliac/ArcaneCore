using System.Buffers.Binary;
using System.Text;
using ArcaneCore.Data.Content.GameObjects;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game.DebugDraw;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Gm.DebugDraw;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.DebugDraw;

/// <summary>
/// The marker models against the client's GameObjectDisplayInfo.dbc (<c>World:GmCommands:DebugDraw:GameObjectDisplayInfoDbcPath</c>):
/// overrides the client has are drawn, ones it lacks fall back to the built-in model with a warning, and (with
/// <c>ARCANECORE_TEST_DBC_DIR</c>) every built-in display id exists in the real file with the model its comment names.
/// </summary>
public sealed partial class DebugDrawCommandTests
{
    /// <summary>Every built-in marker and glow model, as named in DebugMarkerStyles' comments (client-effective build-5875 file).</summary>
    public static readonly IReadOnlyDictionary<uint, string> BuiltInModels = new Dictionary<uint, string>
    {
        [5811] = @"World\Goober\G_JewelBlack.mdx",
        [5912] = @"World\Generic\PVP\CTFflags\AllianceCTFflag.mdx",
        [5913] = @"World\Generic\PVP\CTFflags\HordeCTFflag.mdx",
        [2972] = @"World\Kalimdor\UnGoro\PassiveDoodads\Crystals\UngoroCrystal_Green01.mdx",
        [3993] = @"World\Generic\PassiveDoodads\ParticleEmitters\AuraGreenShort.mdx",
        [2973] = @"World\Kalimdor\UnGoro\PassiveDoodads\Crystals\UngoroCrystal_Red01.mdx",
        [1308] = @"World\Generic\PassiveDoodads\ParticleEmitters\AuraRedShort.mdx",
        [327] = @"World\Goober\G_JewelRed.mdx",
        [5746] = @"World\Kalimdor\DireMaul\ActiveDoodads\CrystalCorrupter\CorruptedCrystalShard.mdx",
        [6430] = @"World\ENVIRONMENT\DOODAD\Carnival\Carni_CannonTarget.mdx",
        [2971] = @"World\Kalimdor\UnGoro\PassiveDoodads\Crystals\UngoroCrystal_Blue01.mdx",
        [263] = @"World\Generic\PassiveDoodads\ParticleEmitters\AuraBlueShort.mdx",
        [2770] = @"World\Goober\G_JewelBlue.mdx",
        [2974] = @"World\Kalimdor\UnGoro\PassiveDoodads\Crystals\UngoroCrystal_Yellow01.mdx",
        [1268] = @"World\Generic\PassiveDoodads\ParticleEmitters\AuraYellowShort.mdx",
        [1667] = @"World\Kalimdor\Silithus\PassiveDoodads\Crystals\FloatingPurpleCrystal01.mdx",
        [266] = @"World\Generic\PassiveDoodads\ParticleEmitters\AuraYellowVeryTall.mdx",
        [6431] = @"World\Kalimdor\Silithus\PassiveDoodads\Crystals\GlyphedCrystal.mdx",
        [363] = @"World\Generic\PassiveDoodads\ParticleEmitters\AuraPurpleShort.mdx",
    };

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Text)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add((logLevel, formatter(state, exception)));

        public IEnumerable<string> Warnings => Lines.Where(l => l.Level == LogLevel.Warning).Select(l => l.Text);
    }

    /// <summary>A GameObjectDisplayInfo.dbc image (12 fields: id 0, model 1, sounds 2-11) with the given rows.</summary>
    private static byte[] DisplayInfoImage(IEnumerable<(uint Id, string Model)> rows)
    {
        (uint Id, string Model)[] list = [.. rows];
        var strings = new MemoryStream();
        strings.WriteByte(0);
        byte[] records = new byte[list.Length * 48];
        for (int row = 0; row < list.Length; row++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(records.AsSpan(row * 48), list[row].Id);
            BinaryPrimitives.WriteUInt32LittleEndian(records.AsSpan((row * 48) + 4), (uint)strings.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(records.AsSpan((row * 48) + 8), 77); // a sound id, not the model
            strings.Write(Encoding.UTF8.GetBytes(list[row].Model));
            strings.WriteByte(0);
        }

        byte[] block = strings.ToArray();
        byte[] image = new byte[20 + records.Length + block.Length];
        "WDBC"u8.CopyTo(image);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), (uint)list.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), 12);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12), 48);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16), (uint)block.Length);
        records.CopyTo(image, 20);
        block.CopyTo(image, 20 + records.Length);
        return image;
    }

    /// <summary>A file with every built-in model plus display 9001, minus <paramref name="leaveOut"/>.</summary>
    private static string WriteDisplayInfo(params uint[] leaveOut)
    {
        string path = Path.Combine(Path.GetTempPath(), "arcanecore-godi-" + Guid.NewGuid().ToString("N") + ".dbc");
        File.WriteAllBytes(path, DisplayInfoImage(BuiltInModels.Where(m => !leaveOut.Contains(m.Key)).Select(m => (m.Key, m.Value)).Append((9001u, @"World\Test\Marker.mdx"))));
        return path;
    }

    [Fact]
    public void Reader_DecodesIdAndModel_AndRefusesOtherLayouts()
    {
        DbcTable<GameObjectDisplayInfoEntry> table = GameObjectDisplayInfoDbcReader.Read(DbcFile.Parse(DisplayInfoImage([(5811, "a.mdx"), (9001, "b.mdx")])));

        Assert.Equal([new GameObjectDisplayInfoEntry(5811, "a.mdx"), new GameObjectDisplayInfoEntry(9001, "b.mdx")], table.Rows);
        byte[] wrong = DisplayInfoImage([(1, "x")]);
        BinaryPrimitives.WriteUInt32LittleEndian(wrong.AsSpan(8), 11);
        BinaryPrimitives.WriteUInt32LittleEndian(wrong.AsSpan(12), 44);
        Assert.Throws<InvalidDataException>(() => GameObjectDisplayInfoDbcReader.Read(DbcFile.Parse(wrong[..^4])));
        Assert.Throws<InvalidDataException>(() => GameObjectDisplayInfoDbcReader.Read(DbcFile.Parse(DisplayInfoImage([(1, "x"), (1, "y")]))));
    }

    [Fact]
    public void Resolve_AppliesOverridesTheClientHas_AndFallsBackOnOnesItLacks()
    {
        var log = new ListLogger();
        var options = new DebugDrawOptions { GameObjectDisplayInfoDbcPath = WriteDisplayInfo() };
        options.Models["Cell"] = 9001;          // in the file: used
        options.Models["gridcorner"] = 4242;    // not in the file: built-in kept
        options.Models["Bogus"] = 9001;         // not a kind: ignored
        options.Models["3"] = 9001;             // a number is not a kind name
        options.GlowModels["LosClear"] = 4242;  // not in the file: built-in glow kept
        options.GlowModels["LosBlocked"] = 0;   // 0 turns the glow off

        DebugMarkerStyle[] styles = DebugMarkerModels.Resolve(options, log);

        Assert.Equal(9001u, styles[(int)DebugMarkerKind.Cell].DisplayId);
        Assert.Equal(5913u, styles[(int)DebugMarkerKind.GridCorner].DisplayId);
        Assert.Equal(2972u, styles[(int)DebugMarkerKind.LosClear].DisplayId);
        Assert.Equal(3993u, styles[(int)DebugMarkerKind.LosClear].GlowDisplayId);
        Assert.Equal(0u, styles[(int)DebugMarkerKind.LosBlocked].GlowDisplayId);
        Assert.Equal(DebugMarkerStyles.All.Count, styles.Length);
        Assert.Equal(4, log.Warnings.Count());
        Assert.Contains(log.Warnings, w => w.Contains("GridCorner = 4242", StringComparison.Ordinal) && w.Contains("built-in model 5913", StringComparison.Ordinal));
        Assert.Contains(log.Warnings, w => w.Contains("LosClear = 4242", StringComparison.Ordinal) && w.Contains("built-in glow 3993", StringComparison.Ordinal));
        Assert.Equal(2, log.Warnings.Count(w => w.Contains("is not a marker kind", StringComparison.Ordinal)));
        // The built-in table itself is untouched.
        Assert.Equal(5912u, DebugMarkerStyles.Of(DebugMarkerKind.Cell).DisplayId);
    }

    [Fact]
    public void Resolve_WarnsAboutABuiltInModelTheFileLacks_AndWithoutAFileTakesOverridesUnchecked()
    {
        var log = new ListLogger();
        DebugMarkerStyle[] styles = DebugMarkerModels.Resolve(new DebugDrawOptions { GameObjectDisplayInfoDbcPath = WriteDisplayInfo(leaveOut: 6430) }, log);
        Assert.Equal(DebugMarkerStyles.All, styles);
        Assert.Contains(log.Warnings, w => w.Contains("HitPoint marker model 6430", StringComparison.Ordinal));

        var unchecked_ = new DebugDrawOptions();
        unchecked_.Models["Cell"] = 4242;
        Assert.Equal(4242u, DebugMarkerModels.Resolve(unchecked_, new ListLogger())[(int)DebugMarkerKind.Cell].DisplayId);
        Assert.Throws<FileNotFoundException>(() => DebugMarkerModels.Resolve(new DebugDrawOptions { GameObjectDisplayInfoDbcPath = WriteDisplayInfo() + ".missing" }, log));
    }

    [Fact]
    public async Task ConfiguredModels_AreWhatTheMarkersShow()
    {
        await using WorldTestHost host = Start(new()
        {
            ["World:GmCommands:DebugDraw:GameObjectDisplayInfoDbcPath"] = WriteDisplayInfo(),
            ["World:GmCommands:DebugDraw:Models:LosClear"] = "9001",
            ["World:GmCommands:DebugDraw:GlowModels:LosClear"] = "4242",
        });
        await using WorldTestClient gm = await EnterAsync(host, "Modeler", AccountSecurity.GameMaster);
        await SelectWolfAsync(host, "Modeler");

        List<ParsedMarker> created = (await SendAsync(gm, ".debug vis los")).Created;

        Assert.Contains(created, m => m.DisplayId == 9001);
        Assert.Contains(created, m => m.DisplayId == 3993);   // the glow override was not in the file: built-in glow kept
        Assert.DoesNotContain(created, m => m.DisplayId == 2972);
    }

    [DebugDrawRealDbcFact]
    public void EveryBuiltInMarkerModel_IsInTheRealClientFile_WithTheModelItsCommentNames()
    {
        // Reports Skipped (naming the variable) when ARCANECORE_TEST_DBC_DIR is unset, never a silent pass.
        string path = Path.Combine(Environment.GetEnvironmentVariable(DebugDrawRealDbcFactAttribute.Variable)!, GameObjectDisplayInfoDbcReader.FileName);
        DbcTable<GameObjectDisplayInfoEntry> displays = GameObjectDisplayInfoDbcReader.Load(path);
        Assert.Equal(1638, displays.Count);

        uint[] used = [.. DebugMarkerStyles.All.SelectMany(s => new[] { s.DisplayId, s.GlowDisplayId }).Where(id => id != 0).Distinct().Order()];
        Assert.Equal(BuiltInModels.Keys.Order(), used);
        string[] wrong = [.. used.Where(id => displays.Find(id)?.ModelName != BuiltInModels[id]).Select(id => $"{id}: {displays.Find(id)?.ModelName ?? "missing"}")];
        Assert.True(wrong.Length == 0, string.Join(", ", wrong));

        var log = new ListLogger();
        Assert.Equal(DebugMarkerStyles.All, DebugMarkerModels.Resolve(new DebugDrawOptions { GameObjectDisplayInfoDbcPath = path }, log));
        Assert.Empty(log.Warnings);
    }
}

/// <summary>A fact that needs the developer's build-5875 DBC directory; Skipped (never a silent pass) without it.</summary>
public sealed class DebugDrawRealDbcFactAttribute : FactAttribute
{
    public const string Variable = "ARCANECORE_TEST_DBC_DIR";

    public DebugDrawRealDbcFactAttribute()
    {
        string? dir = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            Skip = $"{Variable} is not set to a directory of build-5875 DBC files.";
        }
    }
}
