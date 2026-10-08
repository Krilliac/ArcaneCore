using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_RAID_INSTANCE_INFO: VanillaServerMessage, IWorldMessage {
    public required List<RaidInfo> RaidInfos { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt((uint)RaidInfos.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in RaidInfos) {
            await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 716, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 716, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_RAID_INSTANCE_INFO> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        // ReSharper disable once UnusedVariable.Compiler
        var amountOfRaidInfos = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var raidInfos = new List<RaidInfo>();
        for (var i = 0; i < amountOfRaidInfos; ++i) {
            raidInfos.Add(await Vanilla.RaidInfo.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false));
        }

        return new SMSG_RAID_INSTANCE_INFO {
            RaidInfos = raidInfos,
        };
    }

    internal int Size() {
        var size = 0;

        // amount_of_raid_infos: Generator.Generated.DataTypeInteger
        size += 4;

        // raid_infos: Generator.Generated.DataTypeArray
        size += RaidInfos.Sum(e => 12);

        return size;
    }

}

