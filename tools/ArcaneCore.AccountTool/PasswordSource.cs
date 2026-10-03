using System.Text;

namespace ArcaneCore.AccountTool;

/// <summary>Where the password for <c>create</c> / <c>set-password</c> comes from.</summary>
public enum PasswordMode
{
    /// <summary>Legacy: the password is an argv element (visible in process listings and shell history).</summary>
    Argv,

    /// <summary>One line from standard input (<c>--password-stdin</c>, or implicitly when stdin is piped).</summary>
    Stdin,

    /// <summary>The <c>ARCANE_ACCOUNT_PASSWORD</c> environment variable.</summary>
    Environment,

    /// <summary>A no-echo interactive prompt (standard input is a terminal).</summary>
    Prompt,
}

/// <summary>The outcome of parsing <c>&lt;command&gt; &lt;username&gt; [password] [--password-stdin]</c>.</summary>
public sealed record PasswordRequest(string? Username, PasswordMode Mode, string? ArgvPassword, string? Error)
{
    public bool IsValid => Error is null;
}

/// <summary>AC-PI-003: keeps the account password out of the command line unless the caller insists.</summary>
public static class PasswordSource
{
    public const string EnvironmentVariable = "ARCANE_ACCOUNT_PASSWORD";

    public const string StdinFlag = "--password-stdin";

    public const string ArgvWarning =
        "warning: a password given on the command line is visible in process listings, shell history and logs; "
        + "use the interactive prompt, --password-stdin or " + EnvironmentVariable + " instead";

    /// <summary>
    /// Select the password source. <paramref name="args"/> starts with the command name. Precedence:
    /// <c>--password-stdin</c>, then a legacy argv password, then the environment variable, then piped
    /// standard input, then the interactive prompt.
    /// </summary>
    public static PasswordRequest Parse(string[] args, Func<string, string?> getEnvironment, bool stdinRedirected)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(getEnvironment);

        bool stdinFlag = false;
        var positional = new List<string>();
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == StdinFlag)
            {
                stdinFlag = true;
            }
            else
            {
                positional.Add(args[i]);
            }
        }

        string command = args.Length > 0 ? args[0] : "create";
        if (positional.Count is 0 or > 2)
        {
            return new PasswordRequest(null, PasswordMode.Prompt, null, Usage(command));
        }

        string username = positional[0];
        if (positional.Count == 2)
        {
            return stdinFlag
                ? new PasswordRequest(username, PasswordMode.Stdin, null, $"{StdinFlag} cannot be combined with a password argument")
                : new PasswordRequest(username, PasswordMode.Argv, positional[1], null);
        }

        if (stdinFlag)
        {
            return new PasswordRequest(username, PasswordMode.Stdin, null, null);
        }

        if (!string.IsNullOrEmpty(getEnvironment(EnvironmentVariable)))
        {
            return new PasswordRequest(username, PasswordMode.Environment, null, null);
        }

        return new PasswordRequest(username, stdinRedirected ? PasswordMode.Stdin : PasswordMode.Prompt, null, null);
    }

    /// <summary>Obtain the password for a valid request; <c>null</c> when the source produced nothing usable.</summary>
    public static string? Read(PasswordRequest request, Func<string, string?> getEnvironment, TextReader stdin, Func<string, string?> promptNoEcho)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? password = request.Mode switch
        {
            PasswordMode.Argv => request.ArgvPassword,
            PasswordMode.Environment => getEnvironment(EnvironmentVariable),
            PasswordMode.Stdin => stdin.ReadLine()?.TrimEnd('\r', '\n'),
            _ => ReadConfirmed(promptNoEcho),
        };

        return string.IsNullOrEmpty(password) ? null : password;
    }

    /// <summary>Read a line from the console without echoing it.</summary>
    public static string? PromptNoEcho(string prompt)
    {
        Console.Error.Write(prompt);
        var text = new StringBuilder();
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.Error.WriteLine();
                return text.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (text.Length > 0)
                {
                    text.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                text.Append(key.KeyChar);
            }
        }
    }

    private static string? ReadConfirmed(Func<string, string?> promptNoEcho)
    {
        string? first = promptNoEcho("Password: ");
        string? second = promptNoEcho("Repeat password: ");
        return string.Equals(first, second, StringComparison.Ordinal) ? first : null;
    }

    private static string Usage(string command) =>
        $"usage: arcane-account {command} <username> [--password-stdin]   (password from a prompt, stdin or {EnvironmentVariable}; "
        + "the legacy <username> <password> form is deprecated)";
}
