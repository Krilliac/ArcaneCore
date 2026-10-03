using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Reputation;

namespace ArcaneCore.Game.Tests.Reputation;

/// <summary>
/// Synthetic Faction.dbc / FactionTemplate.dbc content shaped like the vanilla records the
/// rules depend on (own-team capital with forced peace, opposite-team hidden war, neutral
/// cartel, hidden parent, forced-invisible pirate faction, class-dependent base).
/// </summary>
internal static class ReputationFixtures
{
    public const uint Stormwind = 72;      // list 7
    public const uint AllianceParent = 469; // list 10, hidden
    public const uint BootyBay = 21;       // list 0, no default flags
    public const uint ClassBased = 99;     // list 6, warrior base 500
    public const uint Pirates = 87;        // list 5, forced invisible
    public const uint Defias = 15;         // no reputation
    public const uint UnknownFaction = 4242;

    public const uint AllianceRaces = 1 | 4 | 8 | 64;
    public const uint HordeRaces = 2 | 16 | 32 | 128;

    public static FactionCatalog Factions { get; } = new(
    [
        new FactionRecord(Stormwind, 7, [AllianceRaces, HordeRaces, 0, 0], [0, 0, 0, 0], [0, -42000, 0, 0],
            [0x11, 0x0E, 0, 0], AllianceParent, "Stormwind"),
        new FactionRecord(AllianceParent, 10, [0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0], [0x04, 0, 0, 0], 0, "Alliance"),
        new FactionRecord(BootyBay, 0, [0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0], 0, "Booty Bay"),
        new FactionRecord(ClassBased, 6, [0, 0, 0, 0], [1, 0, 0, 0], [500, 100, 0, 0], [0x01, 0, 0, 0], 0, "Class based"),
        new FactionRecord(Pirates, 5, [0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0], [0x08, 0, 0, 0], 0, "Pirates"),
        new FactionRecord(Defias, -1, [0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0], 0, "Defias"),
    ]);

    // Player template 1: own mask 1, friendly to 2, hostile to monsters (8).
    public static FactionTemplateRecord PlayerTemplate { get; } = new(1, 1, 0, 1, 2, 8);
    public static FactionTemplateRecord NeutralNpc { get; } = new(2, 0, 0, 8, 0, 0);
    public static FactionTemplateRecord HostileNpc { get; } = new(3, 0, 0, 8, 0, 1);
    public static FactionTemplateRecord StormwindNpc { get; } = new(4, Stormwind, 0, 2, 1, 8);
    public static FactionTemplateRecord ContestedGuard { get; } = new(5, 0, FactionTemplateCatalog.ContestedGuardFlag, 8, 0, 0);
    public static FactionTemplateRecord DefiasNpc { get; } = new(6, Defias, 0, 8, 0, 1);
    public static FactionTemplateRecord UnknownNpc { get; } = new(7, UnknownFaction, 0, 8, 0, 0);
    public static FactionTemplateRecord FriendlyNpc { get; } = new(8, 0, 0, 2, 1, 0);
    public static FactionTemplateRecord BootyBayNpc { get; } = new(9, BootyBay, 0, 8, 0, 0);
    public static FactionTemplateRecord StormwindContestedGuard { get; } = new(10, Stormwind, FactionTemplateCatalog.ContestedGuardFlag, 2, 1, 8);

    public static FactionTemplateCatalog Templates { get; } = new([PlayerTemplate, NeutralNpc, HostileNpc, StormwindNpc,
        ContestedGuard, DefiasNpc, UnknownNpc, FriendlyNpc, BootyBayNpc, StormwindContestedGuard]);

    public static FactionRecord Get(uint id) => Factions.Find(id)!;

    public static PlayerReputation Human() => new(Factions, Race.Human, Class.Warrior);
}

internal sealed class RecordingReputationSink : IReputationSink
{
    public List<CharacterReputationRow> Rows { get; } = [];

    public List<int> Watched { get; } = [];

    public void FactionsChanged(Player player, IReadOnlyList<CharacterReputationRow> rows) => Rows.AddRange(rows);

    public void WatchedFactionChanged(Player player, int watchedFaction) => Watched.Add(watchedFaction);
}
