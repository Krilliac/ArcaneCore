using ArcaneCore.Data.ClientData;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content.Creatures;
using ArcaneCore.Data.Content.Maps;
using ArcaneCore.Data.Content.Names;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.Content.Transports;
using ArcaneCore.Data.Crafting;
using ArcaneCore.Data.Graveyards;
using ArcaneCore.Data.Items;
using ArcaneCore.Data.Npc;
using ArcaneCore.Data.Reputation;
using ArcaneCore.Data.Skills;
using ArcaneCore.Data.Social;
using ArcaneCore.Data.Talents;
using ArcaneCore.Data.Tests.Skills;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ArcaneCore.Data.Tests.ClientData;

/// <summary>Writes synthetic WDBC files (no client data is shipped or needed).</summary>
internal static class SyntheticDbc
{
    /// <summary>A WDBC image of <paramref name="rows"/> (each padded to <paramref name="fields"/> four-byte fields) and a one-byte string block.</summary>
    public static byte[] Image(int fields, int? recordSize, params uint[][] rows)
    {
        int size = recordSize ?? fields * 4;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0x43424457u);
        writer.Write((uint)rows.Length);
        writer.Write((uint)fields);
        writer.Write((uint)size);
        writer.Write(1u);
        foreach (uint[] row in rows)
        {
            byte[] record = new byte[size];
            for (int i = 0; i < row.Length && (i * 4) + 4 <= size; i++)
            {
                BitConverter.GetBytes(row[i]).CopyTo(record, i * 4);
            }

            writer.Write(record);
        }

        writer.Write((byte)0);
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>Write <paramref name="file"/> in <paramref name="directory"/> with the reference layout of its name (ids 1..rows).</summary>
    public static string Write(string directory, string file, int rows = 3, int? fields = null)
    {
        ClientDbcLayout layout = ClientDbcLayouts.Find(file) ?? throw new ArgumentException(file);
        string path = Path.Combine(directory, file);
        File.WriteAllBytes(path, Image(fields ?? layout.Fields, fields is null ? layout.RecordSize : null,
            [.. Enumerable.Range(1, rows).Select(id => new[] { (uint)id })]));
        return path;
    }
}

/// <summary>A directory removed after the test.</summary>
internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "arcane-clientdata-" + Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>The client DBC layouts, the header check and the resolution of <c>ClientData:DbcDirectory</c> (docs/areas/client-data.md).</summary>
public sealed class ClientDataTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private static string WriteAllConsumerFiles(string directory)
    {
        foreach (string file in ClientDbcConsumers.Files)
        {
            SyntheticDbc.Write(directory, file);
        }

        return directory;
    }

    [Fact]
    public void EveryVmangosFormat_HasTheFieldCountItDeclares_AndEveryConsumerFileHasALayout()
    {
        foreach (ClientDbcLayout layout in ClientDbcLayouts.All.Values)
        {
            if (layout.Format is not null)
            {
                Assert.Equal(layout.Format.Length, layout.Fields);
                Assert.Matches("^[nifsdx]+$", layout.Format);
            }
        }

        foreach (string file in ClientDbcConsumers.Files)
        {
            Assert.NotNull(ClientDbcLayouts.Find(file));
        }
    }

    [Fact]
    public void TheLayouts_AgreeWithTheFieldCountsTheReadersEnforce()
    {
        (string File, int Fields)[] readers =
        [
            ("CharSections.dbc", CharacterAppearanceDbcReader.CharSectionsFieldCount),
            ("CharacterFacialHairStyles.dbc", CharacterAppearanceDbcReader.FacialHairStylesFieldCount),
            ("CreatureDisplayInfo.dbc", CreatureDisplayModelDbcReader.DisplayInfoFieldCount),
            ("CreatureModelData.dbc", CreatureDisplayModelDbcReader.ModelDataFieldCount),
            ("AreaTable.dbc", AreaTableDbcReader.FieldCount),
            ("AreaTrigger.dbc", AreaTriggerDbcReader.FieldCount),
            ("Map.dbc", MapDbcReader.FieldCount),
            ("NamesProfanity.dbc", NamesDbcReader.FieldCount),
            ("SpellShapeshiftForm.dbc", ShapeshiftFormDbcReader.FieldCount),
            ("Spell.dbc", SpellDbcImporter.SpellFieldCount),
            ("TransportAnimation.dbc", TransportAnimationDbcReader.FieldCount),
            ("SpellItemEnchantment.dbc", EnchantDbcReader.Fields),
            ("WorldSafeLocs.dbc", WorldSafeLocsDbcReader.FieldCount),
            ("ItemRandomProperties.dbc", ItemRandomPropertiesDbcReader.FieldCount),
            ("ItemSet.dbc", ItemSetDbcReader.FieldCount),
            ("SkillLineAbility.dbc", NpcServiceDbcReaders.SkillLineAbilityFields),
            ("Faction.dbc", FactionDbcReader.FieldCount),
            ("SkillLine.dbc", SkillDbcReaders.SkillLineFields),
            ("SkillRaceClassInfo.dbc", SkillDbcReaders.SkillRaceClassInfoFields),
            ("SkillTiers.dbc", SkillDbcReaders.SkillTiersFields),
            ("ChatChannels.dbc", ChatChannelsDbcReader.FieldCount),
            ("EmotesText.dbc", EmoteDbcReaders.TextFieldCount),
            ("Emotes.dbc", EmoteDbcReaders.EmoteFieldCount),
            ("Talent.dbc", TalentDbcReaders.TalentFields),
            ("TalentTab.dbc", TalentDbcReaders.TalentTabFields),
        ];
        foreach ((string file, int fields) in readers)
        {
            Assert.True(ClientDbcLayouts.Find(file)!.Fields == fields, $"{file}: layout {ClientDbcLayouts.Find(file)!.Fields}, reader {fields}");
        }
    }

    [Fact]
    public void TheHeaderCheck_TellsLoadedMissingMalformedAndMismatched()
    {
        using var temp = new TempDirectory();
        Assert.Equal(ClientDbcStatus.Loaded, ClientDbcInspector.Check(SyntheticDbc.Write(temp.Path, "Talent.dbc", rows: 4)).Status);
        Assert.Equal(4, ClientDbcInspector.Check(Path.Combine(temp.Path, "Talent.dbc")).Records);
        Assert.Equal(ClientDbcStatus.Missing, ClientDbcInspector.Check(Path.Combine(temp.Path, "TalentTab.dbc")).Status);

        ClientDbcFileCheck mismatch = ClientDbcInspector.Check(SyntheticDbc.Write(temp.Path, "SpellShapeshiftForm.dbc", fields: 13));
        Assert.Equal(ClientDbcStatus.FormatMismatch, mismatch.Status);
        Assert.Contains("13 fields in 52-byte records, expected 14 fields in 56-byte records", mismatch.Describe(), StringComparison.Ordinal);

        string truncated = Path.Combine(temp.Path, "Map.dbc");
        byte[] image = SyntheticDbc.Image(42, null, [1u], [2u]);
        File.WriteAllBytes(truncated, image[..^5]);
        Assert.Equal(ClientDbcStatus.Malformed, ClientDbcInspector.Check(truncated).Status);

        string notWdbc = Path.Combine(temp.Path, "Emotes.dbc");
        File.WriteAllText(notWdbc, "this is not a client table at all");
        Assert.Equal(ClientDbcStatus.Malformed, ClientDbcInspector.Check(notWdbc).Status);

        // CharStartOutfit packs byte fields: 41 fields in 152-byte records is the build-5875 layout, not a mismatch.
        Assert.Equal(ClientDbcStatus.Loaded, ClientDbcInspector.Check(SyntheticDbc.Write(temp.Path, "CharStartOutfit.dbc")).Status);
    }

    [Fact]
    public void TheDirectory_FillsEveryUnsetKey_AndAnExplicitKeyWins()
    {
        using var temp = new TempDirectory();
        WriteAllConsumerFiles(temp.Path);
        ClientDataReport report = ClientDataReport.Build(Config(
            ("ClientData:DbcDirectory", temp.Path),
            ("Talents:TalentDbcPath", "D:/elsewhere/Talent.dbc"),
            ("Combat:ShapeshiftFormDbcPath", "   ")));

        Assert.Equal(Path.Combine(temp.Path, "SpellShapeshiftForm.dbc"), report.Overlay["Combat:ShapeshiftFormDbcPath"]); // blank is unset
        Assert.Equal(Path.Combine(temp.Path, "FactionTemplate.dbc"), report.Overlay["Creatures:FactionTemplateDbcPath"]);
        Assert.Equal(Path.Combine(temp.Path, "FactionTemplate.dbc"), report.Overlay["Quests:FactionTemplateDbcPath"]);
        Assert.False(report.Overlay.ContainsKey("Talents:TalentDbcPath"));
        Assert.Equal(Path.Combine(temp.Path, "TalentTab.dbc"), report.Overlay["Talents:TalentTabDbcPath"]);
        Assert.Equal(ClientDataReport.KeyCount - 1, report.Overlay.Count);
        Assert.Equal(Path.GetFullPath(temp.Path), report.Overlay["World:GmCommands:LiveFxDbcDirectory"]); // a directory key is filled with the directory itself
        ClientDbcResolution talent = report.Resolutions.Single(r => r.Consumer.Key == "Talents:TalentDbcPath");
        Assert.Equal(ClientDbcSource.Explicit, talent.Source);
        Assert.Equal(ClientDbcStatus.Missing, talent.Check!.Status);
        Assert.Contains(report.Problems, p => p.Key == "Talents:TalentDbcPath");
    }

    [Fact]
    public void AMissingOrMismatchedFile_IsNotHandedOver_AndItsGroupStaysTogether()
    {
        using var temp = new TempDirectory();
        WriteAllConsumerFiles(temp.Path);
        File.Delete(Path.Combine(temp.Path, "SpellShapeshiftForm.dbc"));
        SyntheticDbc.Write(temp.Path, "TalentTab.dbc", fields: 16);

        ClientDataReport report = ClientDataReport.Build(Config(("ClientData:DbcDirectory", temp.Path)));

        Assert.False(report.Overlay.ContainsKey("Combat:ShapeshiftFormDbcPath"));
        Assert.False(report.Overlay.ContainsKey("Talents:TalentTabDbcPath"));
        Assert.False(report.Overlay.ContainsKey("Talents:TalentDbcPath")); // usable, but only useful with TalentTab
        Assert.True(report.Overlay.ContainsKey("Skills:SkillLineDbcPath"));
        Assert.Contains(report.Problems, p => p.Key == "Combat:ShapeshiftFormDbcPath" && p.Problem.Contains("missing", StringComparison.Ordinal)
            && p.Problem.Contains("built-in build-5875 table", StringComparison.Ordinal));
        Assert.Contains(report.Problems, p => p.Key == "Talents:TalentTabDbcPath" && p.Problem.Contains("format mismatch", StringComparison.Ordinal));
        Assert.Contains(report.Problems, p => p.Key == "Talents:TalentDbcPath" && p.Problem.Contains("TalentTab.dbc is needed with it", StringComparison.Ordinal));

        IReadOnlyList<ClientDataLine> lines = report.Lines();
        Assert.Contains(lines, l => l.Warning && l.Text.StartsWith("ClientData: SpellShapeshiftForm.dbc: missing", StringComparison.Ordinal));
        Assert.Contains(lines, l => !l.Warning && l.Text.StartsWith("ClientData: Faction.dbc: loaded 3 records (37 fields, ArcaneCore FactionDbcReader)", StringComparison.Ordinal));
        // One line per DBC file the daemon reads (FactionTemplate.dbc and SkillLineAbility.dbc feed two keys each).
        Assert.Equal(ClientDbcConsumers.Files.Count, lines.Count(l => l.Text.StartsWith("ClientData: ", StringComparison.Ordinal) && l.Text.Contains(".dbc: ", StringComparison.Ordinal)
            && ClientDbcConsumers.Files.Any(f => l.Text.StartsWith("ClientData: " + f + ": ", StringComparison.Ordinal))));
    }

    [Fact]
    public void NoDirectory_ChangesNothing_AndAMissingDirectoryIsAProblem()
    {
        ClientDataReport none = ClientDataReport.Build(Config());
        Assert.Empty(none.Overlay);
        Assert.Empty(none.Problems);
        Assert.Equal("ClientData:DbcDirectory is not set: only the per-file DBC keys are read", none.Lines()[0].Text);

        ClientDataReport missing = ClientDataReport.Build(Config(("ClientData:DbcDirectory", Path.Combine(Path.GetTempPath(), "no-such-dbc-dir-" + Guid.NewGuid().ToString("N")))));
        Assert.Empty(missing.Overlay);
        Assert.Contains(missing.Problems, p => p.Key == "ClientData:DbcDirectory");
        Assert.True(missing.Lines()[0].Warning);
    }

    [Fact]
    public void ADirectoryFileThatNoConsumerReads_IsStillChecked()
    {
        using var temp = new TempDirectory();
        WriteAllConsumerFiles(temp.Path);
        SyntheticDbc.Write(temp.Path, "Map.dbc", fields: 41);
        SyntheticDbc.Write(temp.Path, "Spell.dbc");

        ClientDataReport report = ClientDataReport.Build(Config(("ClientData:DbcDirectory", temp.Path)));

        Assert.Contains(report.Problems, p => p.Key == "ClientData:DbcDirectory" && p.Problem.StartsWith("Map.dbc", StringComparison.Ordinal));
        Assert.Contains(report.Lines(), l => l.Warning && l.Text.StartsWith("ClientData: Map.dbc: format mismatch", StringComparison.Ordinal));
        Assert.Equal(ClientDbcStatus.Loaded, report.DirectoryFiles.Single(f => f.File == "Spell.dbc").Status);
    }

    [RealDbcFact]
    public void TheRealClientDirectory_FeedsEveryConsumer_WithoutAProblem()
    {
        string directory = Environment.GetEnvironmentVariable(RealDbcFactAttribute.Variable)!;
        ClientDataReport report = ClientDataReport.Build(Config(("ClientData:DbcDirectory", directory)));

        Assert.Empty(report.Problems);
        Assert.Equal(ClientDataReport.KeyCount, report.Overlay.Count);
        Assert.All(report.DirectoryFiles.Where(f => f.Layout is not null), f => Assert.Equal(ClientDbcStatus.Loaded, f.Status));
    }
}
