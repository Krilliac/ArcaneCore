using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_PETITION_QUERY_RESPONSE: VanillaServerMessage, IWorldMessage {
    public required uint PetitionId { get; set; }
    public required ulong CharterOwner { get; set; }
    public required string GuildName { get; set; }
    /// <summary>
    /// cmangos/vmangos/mangoszero: Set to 0, only info is comment from vmangos
    /// </summary>
    public required string BodyText { get; set; }
    /// <summary>
    /// cmangos/vmangos/mangoszero: Set to 1, only info is comment from vmangos
    /// </summary>
    public required uint UnknownFlags { get; set; }
    /// <summary>
    /// cmangos/vmangos/mangoszero: Set to 9, only info is comment from vmangos
    /// </summary>
    public required uint MinimumSignatures { get; set; }
    /// <summary>
    /// cmangos/vmangos/mangoszero: Set to 9, only info is comment from vmangos
    /// </summary>
    public required uint MaximumSignatures { get; set; }
    /// <summary>
    /// cmangos/vmangos/mangoszero: Set to 0, only info is comment from vmangos
    /// </summary>
    public required uint Deadline { get; set; }
    /// <summary>
    /// cmangos/vmangos/mangoszero: Set to 0, only info is comment from vmangos
    /// </summary>
    public required uint IssueDate { get; set; }
    /// <summary>
    /// cmangos/vmangos/mangoszero: Set to 0, only info is comment from vmangos
    /// </summary>
    public required uint AllowedGuildId { get; set; }
    /// <summary>
    /// cmangos/vmangos/mangoszero: Set to 0, only info is comment from vmangos
    /// </summary>
    public required Vanilla.AllowedClass AllowedClass { get; set; }
    /// <summary>
    /// cmangos/vmangos/mangoszero: Set to 0, only info is comment from vmangos
    /// </summary>
    public required Vanilla.AllowedRace AllowedRace { get; set; }
    /// <summary>
    /// cmangos/vmangos/mangoszero: Set to 0, only info is comment from vmangos
    /// </summary>
    public required ushort AllowedGenders { get; set; }
    /// <summary>
    /// cmangos/vmangos/mangoszero: Set to 0, only info is comment from vmangos
    /// </summary>
    public required uint AllowedMinimumLevel { get; set; }
    /// <summary>
    /// cmangos/vmangos/mangoszero: Set to 0, only info is comment from vmangos
    /// </summary>
    public required uint AllowedMaximumLevel { get; set; }
    /// <summary>
    /// cmangos/vmangos/mangoszero: Set to 0, only info is comment from vmangos
    /// vmangos: char m_choicetext[10][64]
    /// </summary>
    public required uint TodoAmountOfSigners { get; set; }
    /// <summary>
    /// cmangos/vmangos/mangoszero: Set to 0, only info is comment from vmangos
    /// </summary>
    public required uint NumberOfChoices { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(PetitionId, cancellationToken).ConfigureAwait(false);

        await w.WriteULong(CharterOwner, cancellationToken).ConfigureAwait(false);

        await w.WriteCString(GuildName, cancellationToken).ConfigureAwait(false);

        await w.WriteCString(BodyText, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(UnknownFlags, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(MinimumSignatures, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(MaximumSignatures, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Deadline, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(IssueDate, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(AllowedGuildId, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)AllowedClass, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)AllowedRace, cancellationToken).ConfigureAwait(false);

        await w.WriteUShort(AllowedGenders, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(AllowedMinimumLevel, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(AllowedMaximumLevel, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(TodoAmountOfSigners, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(NumberOfChoices, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 455, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 455, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_PETITION_QUERY_RESPONSE> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var petitionId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var charterOwner = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var guildName = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        var bodyText = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        var unknownFlags = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var minimumSignatures = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var maximumSignatures = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var deadline = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var issueDate = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var allowedGuildId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var allowedClass = (AllowedClass)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var allowedRace = (AllowedRace)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var allowedGenders = await r.ReadUShort(cancellationToken).ConfigureAwait(false);

        var allowedMinimumLevel = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var allowedMaximumLevel = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var todoAmountOfSigners = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var numberOfChoices = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_PETITION_QUERY_RESPONSE {
            PetitionId = petitionId,
            CharterOwner = charterOwner,
            GuildName = guildName,
            BodyText = bodyText,
            UnknownFlags = unknownFlags,
            MinimumSignatures = minimumSignatures,
            MaximumSignatures = maximumSignatures,
            Deadline = deadline,
            IssueDate = issueDate,
            AllowedGuildId = allowedGuildId,
            AllowedClass = allowedClass,
            AllowedRace = allowedRace,
            AllowedGenders = allowedGenders,
            AllowedMinimumLevel = allowedMinimumLevel,
            AllowedMaximumLevel = allowedMaximumLevel,
            TodoAmountOfSigners = todoAmountOfSigners,
            NumberOfChoices = numberOfChoices,
        };
    }

    internal int Size() {
        var size = 0;

        // petition_id: Generator.Generated.DataTypeInteger
        size += 4;

        // charter_owner: Generator.Generated.DataTypeGuid
        size += 8;

        // guild_name: Generator.Generated.DataTypeCstring
        size += GuildName.Length + 1;

        // body_text: Generator.Generated.DataTypeCstring
        size += BodyText.Length + 1;

        // unknown_flags: Generator.Generated.DataTypeInteger
        size += 4;

        // minimum_signatures: Generator.Generated.DataTypeInteger
        size += 4;

        // maximum_signatures: Generator.Generated.DataTypeInteger
        size += 4;

        // deadline: Generator.Generated.DataTypeInteger
        size += 4;

        // issue_date: Generator.Generated.DataTypeInteger
        size += 4;

        // allowed_guild_id: Generator.Generated.DataTypeInteger
        size += 4;

        // allowed_class: Generator.Generated.DataTypeFlag
        size += 4;

        // allowed_race: Generator.Generated.DataTypeFlag
        size += 4;

        // allowed_genders: Generator.Generated.DataTypeInteger
        size += 2;

        // allowed_minimum_level: Generator.Generated.DataTypeLevel32
        size += 4;

        // allowed_maximum_level: Generator.Generated.DataTypeLevel32
        size += 4;

        // todo_amount_of_signers: Generator.Generated.DataTypeInteger
        size += 4;

        // number_of_choices: Generator.Generated.DataTypeInteger
        size += 4;

        return size;
    }

}

