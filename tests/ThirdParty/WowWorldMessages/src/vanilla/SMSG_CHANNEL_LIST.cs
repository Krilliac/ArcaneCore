using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_CHANNEL_LIST: VanillaServerMessage, IWorldMessage {
    public required string ChannelName { get; set; }
    public required Vanilla.ChannelFlags ChannelFlags { get; set; }
    public required List<ChannelMember> Members { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteCString(ChannelName, cancellationToken).ConfigureAwait(false);

        await w.WriteByte((byte)ChannelFlags, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)Members.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in Members) {
            await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 155, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 155, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_CHANNEL_LIST> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var channelName = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        var channelFlags = (ChannelFlags)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        // ReSharper disable once UnusedVariable.Compiler
        var amountOfMembers = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var members = new List<ChannelMember>();
        for (var i = 0; i < amountOfMembers; ++i) {
            members.Add(await Vanilla.ChannelMember.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false));
        }

        return new SMSG_CHANNEL_LIST {
            ChannelName = channelName,
            ChannelFlags = channelFlags,
            Members = members,
        };
    }

    internal int Size() {
        var size = 0;

        // channel_name: Generator.Generated.DataTypeCstring
        size += ChannelName.Length + 1;

        // channel_flags: Generator.Generated.DataTypeFlag
        size += 1;

        // amount_of_members: Generator.Generated.DataTypeInteger
        size += 4;

        // members: Generator.Generated.DataTypeArray
        size += Members.Sum(e => 9);

        return size;
    }

}

