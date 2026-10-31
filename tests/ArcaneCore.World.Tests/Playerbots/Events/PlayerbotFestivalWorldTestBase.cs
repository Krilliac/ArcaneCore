using ArcaneCore.Game;
using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Events;
using ArcaneCore.World.Playerbots.Population;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Events.EventsTestWorld;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Events;

/// <summary>
/// Festival gathering end to end (P7a H2 and H3, design 4.2 and section 9): capital-homed idle bots on the scenario world with the real
/// game-event feature, spawn gate and town idle. A running festival moves a bounded number of them from the inn to a spot 8 to 12 yards
/// from a festival NPC, and its end brings them back; the NPCs a player talks to stay clear; a spot the bot cannot get to sends it back to
/// the inn instead of out hunting. Positions only: the tests read where the bots stand, not how they decided.
/// </summary>
public abstract class PlayerbotFestivalWorldTestBase
{
    private protected const uint RevelerEntry = 991_001, GiverEntry = 991_002, PlainEntry = 991_003, HiddenEntry = 991_004, FarEntry = 991_005, SecondRevelerEntry = 991_006;

    /// <summary>A bot at its spot stands within the town idle's linger distance (4 yards) of a point 8 to 12 yards from the NPC.</summary>
    private protected const float SpotReach = PlayerbotFestivalRules.RingMaxYards + PlayerbotTownIdle.LingerYards + 0.5f;

    private protected static FestivalNpc Reveler(float dx = 60f, float dy = 0f, float dz = 0f, uint entry = RevelerEntry)
        => new(entry, "Winter Reveler", NpcFlags.Gossip, dx, dy, dz);

    private protected static FestivalNpc Giver(float dx = -60f, float dy = 0f)
        => new(GiverEntry, "Greatfather Winter", NpcFlags.QuestGiver | NpcFlags.Vendor, dx, dy);

    private protected static void Quick(PlayerbotOptions options)
    {
        options.StallSeconds = 10;
        options.Social.Events.Holidays.CelebrateChance = 100; // every bot celebrates: these tests are about where it goes, not the roll
    }

    private protected static async Task<float> FromInnAsync(EventsTestWorld world, Guid bot) => Distance2D(await world.PositionAsync(bot), Inn);

    public static TheoryData<string> SwitchesOff() => new() { "Events:Enabled", "Holidays:Enabled", "CelebrateChance=0" };

    private protected static bool Apply(PlayerbotOptions options, string key, bool on)
    {
        switch (key)
        {
            case "Events:Enabled": options.Social.Events.Enabled = on; break;
            case "Holidays:Enabled": options.Social.Events.Holidays.Enabled = on; break;
            default: options.Social.Events.Holidays.CelebrateChance = on ? 100 : 0; break;
        }

        return true;
    }

    // --- helpers ---------------------------------------------------------------------------------------------------------

    private protected static float StartXOf(float dx) => StartX + dx;

    private protected static float StartYOf(float dy) => StartY + dy;

    private protected static bool Near(EventsTestWorld world, Guid bot, Vector3 point, float yards)
    {
        Player player = world.World.Bots.FindSession(bot)!.Player!;
        return Distance2D(new Vector3(player.X, player.Y, player.Z), point) <= yards;
    }

    private protected static float InnDistance(EventsTestWorld world, Guid bot)
    {
        Player player = world.World.Bots.FindSession(bot)!.Player!;
        return Distance2D(new Vector3(player.X, player.Y, player.Z), Inn);
    }
}
