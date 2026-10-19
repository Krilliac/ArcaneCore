using System.Text;

namespace ArcaneCore.Atlas.Reference.Client.Models;

/// <summary>
/// Model path conventions: how a path stored in an ADT, WDT or MODN blob maps to the file in the client archives, and
/// the plain file name the vmap extractor gives the same model on the server side.
/// </summary>
public static class ClientModelNames
{
    /// <summary>
    /// The client file path for a model path found in MMDX, MWMO or MODN: separators become backslashes and a
    /// <c>.mdx</c> or <c>.mdl</c> extension becomes <c>.m2</c> (1.12.1 ADTs name doodads <c>.mdx</c> but the archives hold <c>.m2</c>).
    /// </summary>
    public static string ClientFilePath(string storedPath)
    {
        ArgumentNullException.ThrowIfNull(storedPath);
        string path = storedPath.Replace('/', '\\').TrimStart('\\');

        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/gameobject_extract.cpp:26-31 (.mdx / .mdl -> .m2 for ADTs before 3.1.0)
        if (path.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
        {
            return string.Concat(path.AsSpan(0, path.Length - 2), "2");
        }

        return path;
    }

    public static bool IsWmo(string path) => path.EndsWith(".wmo", StringComparison.OrdinalIgnoreCase);

    public static bool IsModel(string path)
        => path.EndsWith(".m2", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase);

    /// <summary>The file name after the last backslash.</summary>
    public static string PlainName(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/adtfile.cpp:30-37 (GetPlainName: after the last '\')
        int slash = path.LastIndexOf('\\');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    /// <summary>
    /// The file name the vmap extractor writes a model under (and the name server vmap spawns carry, before the
    /// <c>.vmo</c> suffix): the plain name with <c>.mdx</c> turned into <c>.m2</c>, letters after the first of each word
    /// lower-cased, and spaces (except in the last three characters) turned into underscores. Different models with the
    /// same plain name collide on one server file.
    /// </summary>
    public static string ServerModelName(string storedPath)
    {
        string plain = PlainName(ClientFilePath(storedPath));
        return FixNameSpaces(FixNameCase(plain));
    }

    /// <summary>vmangos <c>FixNameCase</c>: first letter of each alphabetic run upper-case, the rest lower-case, extension lower-case.</summary>
    public static string FixNameCase(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length < 3)
        {
            return name;
        }

        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/adtfile.cpp:48-67
        var chars = name.ToCharArray();
        int len = chars.Length;
        for (int i = 0; i < len - 3; i++)
        {
            if (i > 0 && chars[i] is >= 'A' and <= 'Z' && IsAsciiLetter(chars[i - 1]))
            {
                chars[i] = (char)(chars[i] | 0x20);
            }
            else if ((i == 0 || !IsAsciiLetter(chars[i - 1])) && chars[i] is >= 'a' and <= 'z')
            {
                chars[i] = (char)(chars[i] & ~0x20);
            }
        }

        for (int i = len - 3; i < len; i++)
        {
            chars[i] = (char)(chars[i] | 0x20);
        }

        return new string(chars);
    }

    /// <summary>vmangos <c>FixNameSpaces</c>: spaces become underscores, except in the last three characters.</summary>
    public static string FixNameSpaces(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length < 3)
        {
            return name;
        }

        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/adtfile.cpp:69-79
        var builder = new StringBuilder(name);
        for (int i = 0; i < name.Length - 3; i++)
        {
            if (builder[i] == ' ')
            {
                builder[i] = '_';
            }
        }

        return builder.ToString();
    }

    private static bool IsAsciiLetter(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z');
}
