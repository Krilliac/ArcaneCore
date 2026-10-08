using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
public class QuestObjective {
    /// <summary>
    /// cmangos: client expected gameobject template id in form (id|0x80000000)
    /// </summary>
    public required uint CreatureId { get; set; }
    public required uint KillCount { get; set; }
    public required uint RequiredItemId { get; set; }
    public required uint RequiredItemCount { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(CreatureId, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(KillCount, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(RequiredItemId, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(RequiredItemCount, cancellationToken).ConfigureAwait(false);

    }

    public static async Task<QuestObjective> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var creatureId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var killCount = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var requiredItemId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var requiredItemCount = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new QuestObjective {
            CreatureId = creatureId,
            KillCount = killCount,
            RequiredItemId = requiredItemId,
            RequiredItemCount = requiredItemCount,
        };
    }

}

