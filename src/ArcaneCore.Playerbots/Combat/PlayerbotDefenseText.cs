using System.Globalization;
using ArcaneCore.Data.Content.Maps;

namespace ArcaneCore.World.Playerbots.Combat;

/// <summary>
/// The "LocalDefense" line a bot posts when it calls for help (<c>World:Playerbots:Pvp:Defense</c>), as pure functions: no world, no clock. The line is
/// bot chat, not the client-formatted zone packet retail sends (docs/areas/playbots-combat.md, Defense calls).
/// </summary>
internal static class PlayerbotDefenseText
{
    /// <summary>
    /// "<paramref name="place"/> is under attack!", followed by " x, y" (whole map percents) when <paramref name="coords"/> is set.
    /// <paramref name="place"/> is the subzone name when it differs from the zone, otherwise the zone name.
    /// </summary>
    public static string Format(string place, (int X, int Y)? coords)
    {
        ArgumentNullException.ThrowIfNull(place);
        string line = place + " is under attack!";
        return coords is { } at
            ? string.Create(CultureInfo.InvariantCulture, $"{line} {at.X}, {at.Y}")
            : line;
    }

    /// <summary>
    /// The map position of a world point inside <paramref name="bounds"/>, in whole percents (fractions cut off, as the client's coordinate display does),
    /// or null when the point lies outside the rectangle or a value is not finite. The client frame runs right to left along world Y and top to bottom
    /// along world X: x = (Left - worldY) / (Left - Right) * 100 and y = (Top - worldX) / (Top - Bottom) * 100.
    /// </summary>
    public static (int X, int Y)? Coordinates(WorldMapAreaBounds bounds, float worldX, float worldY)
    {
        double x = (bounds.Left - (double)worldY) / (bounds.Left - (double)bounds.Right) * 100.0;
        double y = (bounds.Top - (double)worldX) / (bounds.Top - (double)bounds.Bottom) * 100.0;
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0.0 || x >= 100.0 || y < 0.0 || y >= 100.0)
            return null;

        return ((int)x, (int)y);
    }
}
