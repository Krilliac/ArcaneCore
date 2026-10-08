using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_GUILD_ROSTER: VanillaServerMessage, IWorldMessage {
    public required string Motd { get; set; }
    public required string GuildInfo { get; set; }
    public required List<uint> Rights { get; set; }
    public required List<GuildMember> Members { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt((uint)Members.Count, cancellationToken).ConfigureAwait(false);

        await w.WriteCString(Motd, cancellationToken).ConfigureAwait(false);

        await w.WriteCString(GuildInfo, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)Rights.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in Rights) {
            await w.WriteUInt(v, cancellationToken).ConfigureAwait(false);
        }

        foreach (var v in Members) {
            await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 138, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 138, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_GUILD_ROSTER> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        // ReSharper disable once UnusedVariable.Compiler
        var amountOfMembers = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var motd = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        var guildInfo = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        // ReSharper disable once UnusedVariable.Compiler
        var amountOfRights = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var rights = new List<uint>();
        for (var i = 0; i < amountOfRights; ++i) {
            rights.Add(await r.ReadUInt(cancellationToken).ConfigureAwait(false));
        }

        var members = new List<GuildMember>();
        for (var i = 0; i < amountOfMembers; ++i) {
            members.Add(await Vanilla.GuildMember.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false));
        }

        return new SMSG_GUILD_ROSTER {
            Motd = motd,
            GuildInfo = guildInfo,
            Rights = rights,
            Members = members,
        };
    }

    internal int Size() {
        var size = 0;

        // amount_of_members: Generator.Generated.DataTypeInteger
        size += 4;

        // motd: Generator.Generated.DataTypeCstring
        size += Motd.Length + 1;

        // guild_info: Generator.Generated.DataTypeCstring
        size += GuildInfo.Length + 1;

        // amount_of_rights: Generator.Generated.DataTypeInteger
        size += 4;

        // rights: Generator.Generated.DataTypeArray
        size += Rights.Sum(e => 4);

        // members: Generator.Generated.DataTypeArray
        size += Members.Sum(e => e.Size());

        return size;
    }

}

