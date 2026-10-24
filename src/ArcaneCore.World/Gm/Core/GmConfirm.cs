using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Audit;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Core;

/// <summary>
/// The command-facing entry points of the confirm codes (plan I7) over the host's <see cref="ConfirmTokenStore"/>: a code is bound to the
/// invoker, the running command (<see cref="CommandContext.CommandPath"/>) and the normalized command text. Handlers that want the whole
/// two-step flow with its replies call <see cref="ConfirmTokenStore.TryConfirm"/> instead. World thread (command handlers).
/// </summary>
public static class GmConfirm
{
    /// <summary>
    /// The store key of an invoker: the account for a player, the name for a console or remote administration invoker, so a character
    /// named like the console (<see cref="CommandContext.ConsoleName"/>) can neither use nor replace the console's code.
    /// </summary>
    public static string InvokerKey(CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.IsConsole ? "console:" + context.InvokerName : "account:" + context.Session.AccountId;
    }

    /// <summary>
    /// A fresh 6-character code for the invoker and <paramref name="normalizedText"/> (the command line without its
    /// <c>confirm &lt;code&gt;</c> suffix), valid for <see cref="ConfirmTokenStore.Lifetime"/>. Empty when the host has no
    /// <see cref="GmAuditFeature"/> (no code can exist, so <see cref="TryConsume"/> fails closed).
    /// </summary>
    public static string Issue(CommandContext context, string normalizedText)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(normalizedText);
        return Store(context)?.Issue(InvokerKey(context), context.CommandPath, normalizedText).Code ?? string.Empty;
    }

    /// <summary>
    /// Spend <paramref name="code"/> for the invoker and <paramref name="normalizedText"/>: true once, within the lifetime, with three
    /// wrong tries allowed per code. The reason for a refusal is not replied; use <see cref="ConfirmTokenStore.TryConfirm"/> for that.
    /// </summary>
    public static bool TryConsume(CommandContext context, string normalizedText, string code)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(normalizedText);
        return Store(context) is { } store
            && store.TryRedeem(InvokerKey(context), context.CommandPath, normalizedText, code) == ConfirmResult.Accepted;
    }

    private static ConfirmTokenStore? Store(CommandContext context)
        => context.Session.Services.GetService<GmAuditFeature>()?.ConfirmTokens;
}
