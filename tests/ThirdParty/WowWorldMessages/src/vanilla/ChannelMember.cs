using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
public class ChannelMember {
    public required ulong Guid { get; set; }
    public required Vanilla.ChannelMemberFlags MemberFlags { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Guid, cancellationToken).ConfigureAwait(false);

        await w.WriteByte((byte)MemberFlags, cancellationToken).ConfigureAwait(false);

    }

    public static async Task<ChannelMember> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var guid = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var memberFlags = (ChannelMemberFlags)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        return new ChannelMember {
            Guid = guid,
            MemberFlags = memberFlags,
        };
    }

}

