using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_SET_FACTION_STANDING: VanillaServerMessage, IWorldMessage {
    public required List<FactionStanding> FactionStandings { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt((uint)FactionStandings.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in FactionStandings) {
            await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 292, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 292, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_SET_FACTION_STANDING> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        // ReSharper disable once UnusedVariable.Compiler
        var amountOfFactionStandings = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var factionStandings = new List<FactionStanding>();
        for (var i = 0; i < amountOfFactionStandings; ++i) {
            factionStandings.Add(await Vanilla.FactionStanding.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false));
        }

        return new SMSG_SET_FACTION_STANDING {
            FactionStandings = factionStandings,
        };
    }

    internal int Size() {
        var size = 0;

        // amount_of_faction_standings: Generator.Generated.DataTypeInteger
        size += 4;

        // faction_standings: Generator.Generated.DataTypeArray
        size += FactionStandings.Sum(e => 6);

        return size;
    }

}

