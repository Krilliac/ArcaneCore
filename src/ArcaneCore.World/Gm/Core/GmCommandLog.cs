using System.Globalization;

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

    public static string Describe(string commandText, int accountId, string playerName, uint mapId, float x, float y, float z, string selection)
        => string.Create(CultureInfo.InvariantCulture,
            $"Command: {commandText} [Player: {playerName} (Account: {accountId}) X: {x} Y: {y} Z: {z} Map: {mapId} Selected: {selection}]");
}
