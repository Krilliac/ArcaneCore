namespace ArcaneCore.Kernel.ServerMail;

/// <summary>AzerothCore mail_server_template: a reward letter every character gets once, when it meets every condition.</summary>
public sealed record ServerMailTemplateRow(uint Id, uint SenderEntry, uint MoneyAlliance, uint MoneyHorde, string Subject, string Body, bool Active);

/// <summary>AzerothCore mail_server_template_items: one item for one faction ("Alliance" or "Horde").</summary>
public sealed record ServerMailItemRow(uint TemplateId, string Faction, uint Item, uint ItemCount);

/// <summary>AzerothCore mail_server_template_conditions: conditionType is the name ("Level", "Quest", ...).</summary>
public sealed record ServerMailConditionRow(uint TemplateId, string ConditionType, uint ConditionValue, uint ConditionState);

/// <summary>Everything the server mail tables hold.</summary>
public sealed record ServerMailContent(IReadOnlyList<ServerMailTemplateRow> Templates, IReadOnlyList<ServerMailItemRow> Items,
    IReadOnlyList<ServerMailConditionRow> Conditions);

/// <summary>The server mail tables and the per-character record of letters sent (AzerothCore mail_server_character).</summary>
public interface IServerMailStore
{
    Task<ServerMailContent> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>The template ids already sent to a character.</summary>
    Task<IReadOnlyCollection<uint>> GetSentAsync(int characterId, CancellationToken cancellationToken = default);

    /// <summary>AzerothCore CHAR_REP_MAIL_SERVER_CHARACTER.</summary>
    Task MarkSentAsync(int characterId, uint templateId, CancellationToken cancellationToken = default);
}
