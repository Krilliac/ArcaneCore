using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_QUESTUPDATE_ADD_KILL: VanillaServerMessage, IWorldMessage {
    public required uint QuestId { get; set; }
    /// <summary>
    /// Unsure of name
    /// </summary>
    public required uint CreatureId { get; set; }
    public required uint KillCount { get; set; }
    public required uint RequiredKillCount { get; set; }
    public required ulong Guid { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(QuestId, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(CreatureId, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(KillCount, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(RequiredKillCount, cancellationToken).ConfigureAwait(false);

        await w.WriteULong(Guid, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 26, 409, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 26, 409, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_QUESTUPDATE_ADD_KILL> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var questId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var creatureId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var killCount = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var requiredKillCount = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var guid = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        return new SMSG_QUESTUPDATE_ADD_KILL {
            QuestId = questId,
            CreatureId = creatureId,
            KillCount = killCount,
            RequiredKillCount = requiredKillCount,
            Guid = guid,
        };
    }

}

