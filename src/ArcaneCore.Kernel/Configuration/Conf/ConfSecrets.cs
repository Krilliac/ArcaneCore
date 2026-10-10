using System.Text.RegularExpressions;

namespace ArcaneCore.Kernel.Configuration.Conf;

/// <summary>
/// Decides which configuration values are secrets and masks them for <c>write-config --current</c>. A key is secret when the last
/// segment of its path ends with <c>ConnectionString</c> (the password inside is masked, the rest stays readable) or contains
/// <c>Password</c>, <c>Pwd</c>, <c>Secret</c>, <c>Token</c> or <c>ApiKey</c> (the whole value is masked). Independently of the key
/// name, a <c>Password=</c> or <c>Pwd=</c> component inside any value is masked. Pure functions, thread-safe, no I/O.
/// </summary>
public static class ConfSecrets
{
    /// <summary>The text that replaces a masked secret.</summary>
    public const string Placeholder = "********";

    private static readonly Regex PasswordComponent = new(
        @"(?<=(?:^|;)\s*(?:password|pwd)\s*=\s*)(?:""[^""]*""|'[^']*'|[^;]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly string[] WholeValueMarkers = ["password", "pwd", "secret", "token", "apikey"];

    /// <summary>True when the last segment of <paramref name="path"/> (separated by ':' or '.') names a secret or a connection string.</summary>
    public static bool IsSecretPath(string path) => IsConnectionString(path) || IsWholeValueSecret(path);

    /// <summary>The value to write for <paramref name="path"/> when secrets are masked.</summary>
    public static string Mask(string path, string value)
    {
        if (IsWholeValueSecret(path))
        {
            return value.Length == 0 ? value : Placeholder;
        }

        return MaskPasswordComponents(value);
    }

    /// <summary>Replaces the value of every <c>Password=</c> or <c>Pwd=</c> component of a connection-string-shaped value.</summary>
    public static string MaskPasswordComponents(string value) => PasswordComponent.Replace(value, Placeholder);

    private static string LastSegment(string path)
    {
        int cut = Math.Max(path.LastIndexOf(':'), path.LastIndexOf('.'));
        return cut < 0 ? path : path[(cut + 1)..];
    }

    private static bool IsConnectionString(string path) => LastSegment(path).EndsWith("ConnectionString", StringComparison.OrdinalIgnoreCase);

    private static bool IsWholeValueSecret(string path)
    {
        string segment = LastSegment(path);
        foreach (string marker in WholeValueMarkers)
        {
            if (segment.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
