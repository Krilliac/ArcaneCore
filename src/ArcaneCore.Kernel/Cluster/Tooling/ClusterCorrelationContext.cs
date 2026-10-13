using System.Text.RegularExpressions;

namespace ArcaneCore.Kernel.Cluster.Tooling;

/// <summary>
/// Structured correlation context for cluster operations, transfers, and persistence barriers (M15.5).
/// Correlates realm, node incarnation, session generation, transfer ID, ownership epoch, and operation ID
/// while guaranteeing that credentials and session keys never enter logs or diagnostic snapshots.
/// </summary>
public sealed partial record ClusterCorrelationContext(
    int RealmId,
    string NodeId,
    Guid NodeIncarnation,
    long SessionGeneration = 0,
    string? TransferId = null,
    long OwnershipEpoch = 0,
    string? OperationId = null)
{
    public const string RedactedValue = "[REDACTED]";

    private static readonly HashSet<string> SensitiveKeyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "password",
        "passwd",
        "pwd",
        "secret",
        "sessionkey",
        "session_key",
        "srp_k",
        "srp_v",
        "srp_s",
        "verifier",
        "salt",
        "token",
        "apikey",
        "api_key",
        "authorization",
        "bearer",
        "privatekey",
        "private_key",
        "credentials",
        "connectionstring",
        "connection_string",
    };

    [GeneratedRegex(@"(?i)\b(password|passwd|pwd|secret|session[_-]?key|srp[_-]?[kvs]|verifier|token|api[_-]?key|bearer)\s*([=:])\s*([^\s,;""']+)", RegexOptions.CultureInvariant)]
    private static partial Regex InlineSecretPattern();

    /// <summary>Formats the correlation fields as a deterministic key=value log prefix.</summary>
    public string FormatLogPrefix()
    {
        string transfer = string.IsNullOrWhiteSpace(TransferId) ? "-" : SanitizeToken(TransferId);
        string op = string.IsNullOrWhiteSpace(OperationId) ? "-" : SanitizeToken(OperationId);
        string node = string.IsNullOrWhiteSpace(NodeId) ? "unknown" : SanitizeToken(NodeId);
        return $"realm={RealmId} node={node} inc={NodeIncarnation:D} sessGen={SessionGeneration} xfer={transfer} epoch={OwnershipEpoch} op={op}";
    }

    /// <summary>Returns a copy of <paramref name="fields"/> with sensitive keys or inline secret values redacted.</summary>
    public static IReadOnlyDictionary<string, string> RedactSensitiveFields(
        IReadOnlyDictionary<string, string?> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var redacted = new Dictionary<string, string>(fields.Count, StringComparer.Ordinal);
        foreach ((string key, string? rawValue) in fields)
        {
            if (IsSensitiveKey(key))
            {
                redacted[key] = RedactedValue;
                continue;
            }

            redacted[key] = RedactInlineSecrets(rawValue ?? string.Empty);
        }

        return redacted;
    }

    /// <summary>Redacts inline key=value or key:value secret patterns inside free-form diagnostic text.</summary>
    public static string RedactInlineSecrets(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        return InlineSecretPattern().Replace(text, $"$1$2{RedactedValue}");
    }

    /// <summary>True when a field name represents a credential, session key, SRP secret, or connection string.</summary>
    public static bool IsSensitiveKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        string trimmed = key.Trim();
        if (SensitiveKeyNames.Contains(trimmed))
            return true;

        return trimmed.Contains("password", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("sessionkey", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("session_key", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("token", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("apikey", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("api_key", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("privatekey", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("connectionstring", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeToken(string value) =>
        RedactInlineSecrets(value.Trim()).Replace(' ', '_');
}
