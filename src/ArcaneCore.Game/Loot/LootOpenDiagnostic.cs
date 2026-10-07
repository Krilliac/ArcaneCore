using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Loot;

/// <summary>World-thread, read-only evidence for a refused corpse-loot open.</summary>
public sealed record LootOpenDiagnostic(
    LootResult Result,
    ulong PlayerGuid,
    uint PlayerMapId,
    float PlayerX,
    float PlayerY,
    float PlayerZ,
    ulong SourceGuid,
    bool BagFound,
    bool? SourceIsInWorld,
    uint? SourceMapId,
    float? SourceX,
    float? SourceY,
    float? SourceZ,
    bool? SameMap,
    CreatureDeathState? CreatureDeathState,
    uint? CorpseDecayMs,
    bool? BagClosed,
    uint? Gold,
    int? ItemCount,
    ulong? OwnerGuid,
    IReadOnlyList<ulong> RecipientGuids,
    bool? IsRecipient,
    bool? HasSomethingForPlayer);

public sealed partial class LootService
{
    /// <summary>Capture the actual open result and current source/bag facts without re-running Open policy.</summary>
    public LootOpenDiagnostic DescribeOpen(Player player, ObjectGuid sourceGuid, LootResult result)
    {
        ArgumentNullException.ThrowIfNull(player);
        _bags.TryGetValue(sourceGuid, out (WorldObject Source, LootBag Bag) entry);
        bool found = entry.Bag is not null;
        WorldObject? source = found ? entry.Source : player.Map?.FindObject(sourceGuid);
        LootBag? bag = found ? entry.Bag : null;
        Creature? creature = source as Creature;
        return new LootOpenDiagnostic(
            result,
            player.Guid.Value,
            player.MapId,
            player.X,
            player.Y,
            player.Z,
            sourceGuid.Value,
            found,
            source?.IsInWorld,
            source?.MapId,
            source?.X,
            source?.Y,
            source?.Z,
            source is null ? null : ReferenceEquals(player.Map, source.Map),
            creature?.DeathState,
            creature?.CorpseDecayMs,
            bag?.IsClosed,
            bag?.Gold,
            bag?.Items.Count,
            bag is null || bag.Owner.IsEmpty ? (ulong?)null : bag.Owner.Value,
            bag is null ? [] : bag.Recipients.Select(guid => guid.Value).Order().ToArray(),
            bag?.IsRecipient(player),
            bag?.HasSomethingFor(player));
    }
}
