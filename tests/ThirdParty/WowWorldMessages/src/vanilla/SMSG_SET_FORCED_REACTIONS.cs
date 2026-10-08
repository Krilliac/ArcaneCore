using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_SET_FORCED_REACTIONS: VanillaServerMessage, IWorldMessage {
    public required List<ForcedReaction> Reactions { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt((uint)Reactions.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in Reactions) {
            await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 677, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 677, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_SET_FORCED_REACTIONS> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        // ReSharper disable once UnusedVariable.Compiler
        var amountOfReactions = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var reactions = new List<ForcedReaction>();
        for (var i = 0; i < amountOfReactions; ++i) {
            reactions.Add(await Vanilla.ForcedReaction.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false));
        }

        return new SMSG_SET_FORCED_REACTIONS {
            Reactions = reactions,
        };
    }

    internal int Size() {
        var size = 0;

        // amount_of_reactions: Generator.Generated.DataTypeInteger
        size += 4;

        // reactions: Generator.Generated.DataTypeArray
        size += Reactions.Sum(e => 6);

        return size;
    }

}

