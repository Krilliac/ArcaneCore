using ArcaneCore.Data.Content.ClientEffects;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Features;
using ArcaneCore.World.Gm.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Gm.Events;

/// <summary>
/// Loads the client tables of <see cref="LiveFxData"/> at startup from <c>World:GmCommands:LiveFxDbcDirectory</c> (the
/// developer's own build-5875 DBC files; nothing is shipped). Unset: one log line says the ids stay unchecked. Set: each of
/// SoundEntries, ZoneMusic, CinematicSequences, SpellVisualKit, SpellVisualEffectName and WorldStateUI .dbc found there is
/// loaded (a missing file is a warning and leaves that kind unchecked; a malformed file or a missing directory stops the
/// daemon, as every configured DBC does), and every <see cref="LiveFxCommands.Presets"/> sound id is checked against
/// SoundEntries (a warning names a missing one). Nothing is loaded while <see cref="GmOptions.LiveFx"/> is off.
/// </summary>
public sealed class LiveFxDataFeature(ILogger<LiveFxDataFeature> logger, IConfiguration? configuration = null) : IWorldFeature
{
    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        GmOptions options = configuration is null ? new GmOptions() : GmOptions.Bind(configuration);
        if (!options.LiveFx)
        {
            return;
        }

        string directory = options.LiveFxDbcDirectory?.Trim() ?? string.Empty;
        if (directory.Length == 0)
        {
            logger.LogInformation(
                "{Section}:LiveFxDbcDirectory is not set: .fx music, sound, cinematic and visual send any id unchecked and .fx lookup has no client data",
                GmOptions.SectionName);
            return;
        }

        LiveFxData data = Load(directory, logger);
        LiveFxData attached = LiveFxData.Of(world);
        attached.Sounds = data.Sounds;
        attached.ZoneMusic = data.ZoneMusic;
        attached.Cinematics = data.Cinematics;
        attached.VisualKits = data.VisualKits;
        attached.VisualEffects = data.VisualEffects;
        attached.WorldStates = data.WorldStates;
    }

    /// <summary>Read the tables of <paramref name="directory"/> (see the type summary); throws when the directory is missing or a file is malformed.</summary>
    public static LiveFxData Load(string directory, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(logger);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"{GmOptions.SectionName}:LiveFxDbcDirectory '{directory}' does not exist");
        }

        var data = new LiveFxData
        {
            Sounds = Optional(directory, SoundEntriesDbcReader.FileName, SoundEntriesDbcReader.Load, logger),
            ZoneMusic = Optional(directory, ZoneMusicDbcReader.FileName, ZoneMusicDbcReader.Load, logger),
            Cinematics = Optional(directory, CinematicSequencesDbcReader.FileName, CinematicSequencesDbcReader.Load, logger),
            VisualKits = Optional(directory, SpellVisualKitDbcReader.FileName, SpellVisualKitDbcReader.Load, logger),
            VisualEffects = Optional(directory, SpellVisualEffectNameDbcReader.FileName, SpellVisualEffectNameDbcReader.Load, logger),
            WorldStates = Optional(directory, WorldStateUIDbcReader.FileName, WorldStateUIDbcReader.Load, logger),
        };

        logger.LogInformation(
            ".fx client data from {Directory}: SoundEntries {Sounds}, ZoneMusic {Music}, CinematicSequences {Cinematics}, SpellVisualKit {Kits}, SpellVisualEffectName {Effects}, WorldStateUI {States} rows",
            directory, Rows(data.Sounds), Rows(data.ZoneMusic), Rows(data.Cinematics), Rows(data.VisualKits), Rows(data.VisualEffects), Rows(data.WorldStates));

        if (data.Sounds is { } sounds)
        {
            foreach (LiveFxCommands.Preset preset in LiveFxCommands.Presets)
            {
                foreach (uint sound in preset.SoundIds.Where(id => !sounds.Contains(id)))
                {
                    logger.LogWarning(".fx event preset {Preset} plays sound {Sound}, which is not in {File}", preset.Name, sound, sounds.FileName);
                }
            }
        }

        return data;
    }

    private static string Rows<TRow>(DbcTable<TRow>? table)
        where TRow : class
        => table is null ? "missing" : table.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static DbcTable<TRow>? Optional<TRow>(string directory, string fileName, Func<string, DbcTable<TRow>> load, ILogger logger)
        where TRow : class
    {
        string path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
        {
            logger.LogWarning("{File} is not in {Directory}: those .fx ids stay unchecked", fileName, directory);
            return null;
        }

        return load(path);
    }
}
