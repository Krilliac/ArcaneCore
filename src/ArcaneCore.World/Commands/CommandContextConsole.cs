namespace ArcaneCore.World.Commands;

/// <summary>
/// True console versus remote invokers (design 1.7). <see cref="CommandContext.IsConsole"/> is also true for a remote administration
/// invoker (<see cref="CommandContext.ForRemote"/>): it means "no character, replies to a sink". Only the true console outranks every
/// account, so a check that grants or skips a security rule uses <see cref="IsTrueConsole"/>.
/// </summary>
public static class CommandContextConsole
{
    /// <summary>The <see cref="CommandContext.Failure"/> reason of a refused Console-only command.</summary>
    public const string ConsoleOnlyFailure = "console-only";

    /// <summary>The reply of a refused Console-only command.</summary>
    public const string ConsoleOnlyText = "This command can only be run at the server console.";

    /// <summary>Whether the invoker is the server console itself: a console invoker that is not a remote one.</summary>
    public static bool IsTrueConsole(this CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.IsConsole && !context.IsRemote;
    }

    /// <summary>True for the true console; otherwise fails the context with <see cref="ConsoleOnlyFailure"/>, replies and returns false (the handler returns true).</summary>
    public static bool RequireTrueConsole(this CommandContext context)
    {
        if (context.IsTrueConsole())
        {
            return true;
        }

        context.Fail(ConsoleOnlyFailure);
        context.Reply(ConsoleOnlyText);
        return false;
    }
}
