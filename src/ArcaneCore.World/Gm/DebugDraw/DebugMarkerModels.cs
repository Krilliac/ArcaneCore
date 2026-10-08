using ArcaneCore.Data.Content.GameObjects;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game.DebugDraw;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Gm.DebugDraw;

/// <summary>
/// The marker models a server uses: the built-in <see cref="DebugMarkerStyles"/> with the operator's
/// <see cref="DebugDrawOptions.Models"/> / <see cref="DebugDrawOptions.GlowModels"/> overrides applied. With
/// <see cref="DebugDrawOptions.GameObjectDisplayInfoDbcPath"/> set, every display id is checked against the client's
/// GameObjectDisplayInfo.dbc at startup: an override the client does not have is a warning and the kind keeps its built-in
/// model; a built-in id the file lacks is a warning too (it has no fallback). Without the path the overrides are taken as
/// given. The model a marker shows is its object's GAMEOBJECT_DISPLAYID (the glow companions already rely on that), so an
/// override does not change the synthetic template the client is told about.
/// </summary>
public static class DebugMarkerModels
{
    /// <summary>The styles to draw with, indexed by kind (see the type summary). Throws when the configured DBC cannot be read.</summary>
    public static DebugMarkerStyle[] Resolve(DebugDrawOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        DebugMarkerStyle[] styles = [.. DebugMarkerStyles.All];
        string path = options.GameObjectDisplayInfoDbcPath?.Trim() ?? string.Empty;
        DbcTable<GameObjectDisplayInfoEntry>? displays = path.Length == 0 ? null : GameObjectDisplayInfoDbcReader.Load(path);

        if (displays is not null)
        {
            foreach (DebugMarkerStyle style in styles)
            {
                foreach (uint id in new[] { style.DisplayId, style.GlowDisplayId }.Where(id => id != 0 && !displays.Contains(id)))
                {
                    logger.LogWarning("DebugDraw: the built-in {Kind} marker model {DisplayId} is not in {Path}", style.Kind, id, path);
                }
            }
        }

        int overrides = 0;
        foreach ((string key, uint id) in options.Models)
        {
            if (Kind(key, logger) is not { } kind)
            {
                continue;
            }

            if (id == 0 || (displays is not null && !displays.Contains(id)))
            {
                logger.LogWarning(
                    "DebugDraw: {Section}:Models:{Kind} = {DisplayId} is not a GameObjectDisplayInfo id{Where}; the marker keeps its built-in model {Default}",
                    DebugDrawOptions.SectionName, kind, id, displays is null ? string.Empty : " in " + path, styles[(int)kind].DisplayId);
                continue;
            }

            styles[(int)kind] = styles[(int)kind] with { DisplayId = id };
            overrides++;
        }

        foreach ((string key, uint id) in options.GlowModels)
        {
            if (Kind(key, logger) is not { } kind)
            {
                continue;
            }

            // 0 turns the glow companion off.
            if (id != 0 && displays is not null && !displays.Contains(id))
            {
                logger.LogWarning(
                    "DebugDraw: {Section}:GlowModels:{Kind} = {DisplayId} is not a GameObjectDisplayInfo id in {Path}; the marker keeps its built-in glow {Default}",
                    DebugDrawOptions.SectionName, kind, id, path, styles[(int)kind].GlowDisplayId);
                continue;
            }

            styles[(int)kind] = styles[(int)kind] with { GlowDisplayId = id };
            overrides++;
        }

        if (displays is not null)
        {
            logger.LogInformation("DebugDraw: marker models checked against {Path} ({Rows} display ids), {Overrides} override(s) applied", path, displays.Count, overrides);
        }
        else if (overrides > 0)
        {
            logger.LogInformation(
                "DebugDraw: {Overrides} marker model override(s) applied unchecked ({Section}:GameObjectDisplayInfoDbcPath is not set)", overrides, DebugDrawOptions.SectionName);
        }

        return styles;
    }

    private static DebugMarkerKind? Kind(string key, ILogger logger)
    {
        if (Enum.TryParse(key, ignoreCase: true, out DebugMarkerKind kind) && Enum.IsDefined(kind) && (int)kind < DebugMarkerStyles.All.Count
            && !int.TryParse(key, out _))
        {
            return kind;
        }

        logger.LogWarning("DebugDraw: '{Key}' is not a marker kind ({Kinds}); its model override is ignored", key, string.Join(", ", Enum.GetNames<DebugMarkerKind>()));
        return null;
    }
}
