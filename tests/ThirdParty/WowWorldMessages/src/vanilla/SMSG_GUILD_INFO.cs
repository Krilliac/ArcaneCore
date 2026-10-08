using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_GUILD_INFO: VanillaServerMessage, IWorldMessage {
    public required string GuildName { get; set; }
    public required uint CreatedDay { get; set; }
    public required uint CreatedMonth { get; set; }
    public required uint CreatedYear { get; set; }
    public required uint AmountOfCharactersInGuild { get; set; }
    public required uint AmountOfAccountsInGuild { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteCString(GuildName, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(CreatedDay, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(CreatedMonth, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(CreatedYear, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(AmountOfCharactersInGuild, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(AmountOfAccountsInGuild, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 136, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 136, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_GUILD_INFO> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var guildName = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        var createdDay = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var createdMonth = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var createdYear = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var amountOfCharactersInGuild = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var amountOfAccountsInGuild = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_GUILD_INFO {
            GuildName = guildName,
            CreatedDay = createdDay,
            CreatedMonth = createdMonth,
            CreatedYear = createdYear,
            AmountOfCharactersInGuild = amountOfCharactersInGuild,
            AmountOfAccountsInGuild = amountOfAccountsInGuild,
        };
    }

    internal int Size() {
        var size = 0;

        // guild_name: Generator.Generated.DataTypeCstring
        size += GuildName.Length + 1;

        // created_day: Generator.Generated.DataTypeInteger
        size += 4;

        // created_month: Generator.Generated.DataTypeInteger
        size += 4;

        // created_year: Generator.Generated.DataTypeInteger
        size += 4;

        // amount_of_characters_in_guild: Generator.Generated.DataTypeInteger
        size += 4;

        // amount_of_accounts_in_guild: Generator.Generated.DataTypeInteger
        size += 4;

        return size;
    }

}

