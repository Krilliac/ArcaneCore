using System.Globalization;
using ArcaneCore.Kernel.Logging;

namespace ArcaneCore.World.Gm.Core;

/// <summary>
/// The GM command audit line. vmangos logs every command above SEC_PLAYER before running it
/// (Chat.cpp:1908-1925, "Command: %s [Player: %s (Group Leader ..., Account: %u) X: %f Y: %f Z: %f
/// Map: %u Selected: %s]"); the group-leader field is left out because the group system is not
/// reachable from the command layer.
/// </summary>
public static class GmCommandLog
{
    /// <summary>Logger category of the audit lines.</summary>
    public const string Category = "ArcaneCore.Gm";

    /// <summary>Whether running <paramref name="command"/> writes an audit line.</summary>
    public static bool ShouldLog(Commands.ChatCommand command, GmOptions options)
        => options.LogCommands && command.RequiredLevel(options) > 0;

    /// <summary>The audit line of a command typed at the server console (no player, no account, no position).</summary>
    public static string DescribeConsole(string commandText) => $"Command: {Safe(commandText)} [Console]";

    /// <summary>The audit line of a stripped developer opcode a staff session sent (dropped unread, no effect).</summary>
    public static string DescribeDevOpcode(string opcodeName, int accountId, string playerName, int length)
        => string.Create(CultureInfo.InvariantCulture,
            $"Command: dev-opcode {Safe(opcodeName)} [Player: {Safe(playerName)} (Account: {accountId}) {length} bytes, dropped unread]");

    public static string Describe(string commandText, int accountId, string playerName, uint mapId, float x, float y, float z, string selection)
        => string.Create(CultureInfo.InvariantCulture,
            $"Command: {Safe(commandText)} [Player: {Safe(playerName)} (Account: {accountId}) X: {x} Y: {y} Z: {z} Map: {mapId} Selected: {Safe(selection)}]");

    /// <summary>Client-typed text for an audit line: control, format and line-separator characters become visible escapes (CWE-117), nothing is cut.</summary>
    private static string Safe(string text) => LogSafe.Escape(text, int.MaxValue);
}
