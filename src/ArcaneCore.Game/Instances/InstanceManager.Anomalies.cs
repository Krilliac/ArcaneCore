using System.Globalization;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Diagnostics;

namespace ArcaneCore.Game.Instances;

/// <summary>
/// Outcome checks for instances (H1), reported through <see cref="AnomalyLedger"/> as subsystem <c>instance</c>: a bind whose save is
/// not in the save table, a reset or an unload that meets players still inside, an entry refused (loudly or silently), and instance
/// script state that never reached the save. Every check is a table lookup or a counter compare; strings are built only on failure.
/// World thread only.
/// </summary>
public sealed partial class InstanceManager
{
    /// <summary>The anomaly subsystem of every instance check.</summary>
    public const string AnomalySubsystem = "instance";

    /// <summary>A player or group was bound to a save the manager does not hold (or one that is deleted): the bind would never be stored or reset.</summary>
    public const string BindWithoutSaveCode = "bind-without-save";

    /// <summary>A save was reset or its map unloaded while players were still inside the map.</summary>
    public const string ResetWithPlayersInsideCode = "reset-with-players-inside";

    /// <summary>The map was unloaded with players still in it.</summary>
    public const string UnloadWithPlayersInsideCode = "unload-with-players-inside";

    /// <summary>A player was refused entry and told why (counted; informational).</summary>
    public const string EnterRefusedCode = "enter-refused";

    /// <summary>A login re-entry was refused and the player was moved to the entrance without any message.</summary>
    public const string EnterRefusedSilentlyCode = "enter-refused-silently";

    /// <summary>The instance script saved its state after its save was deleted: the state is lost.</summary>
    public const string SaveDataDroppedCode = "save-data-dropped";

    /// <summary>The instance script's current state differs from what the save holds as the map unloads: the state would be lost on reload.</summary>
    public const string SaveDataStaleCode = "save-data-stale";

    /// <summary>Test seam: reset a save as the reset flows do.</summary>
    internal void ResetSaveForTest(InstanceSave save) => ResetSave(save);

    /// <summary>Test seam: bind a character to a save as the entry flow does.</summary>
    internal void BindForTest(ObjectGuid player, InstanceSave save) => BindPlayer(player, save, permanent: false);

    private static string SubjectOf(InstanceSave save)
        => string.Create(CultureInfo.InvariantCulture, $"instance {save.InstanceId} of map {save.MapId}");

    private static string SubjectOf(Player player)
        => string.Create(CultureInfo.InvariantCulture, $"player (guid {player.Guid.Low})");

    /// <summary>After a bind: the save must still be the one in the save table and not deleted.</summary>
    private void VerifyBindHasSave(InstanceSave save, string kind, ulong id)
    {
        if (!save.IsDeleted && _saves.TryGetValue(save.InstanceId, out InstanceSave? held) && ReferenceEquals(held, save))
        {
            return;
        }

        AnomalyLedger.Shared.Report(AnomalySubsystem, BindWithoutSaveCode, SubjectOf(save),
            string.Create(CultureInfo.InvariantCulture, $"{kind} {id} was bound but the save is {(save.IsDeleted ? "deleted" : "not in the save table")}"));
    }

    /// <summary>Before a save is reset: nobody may be inside its loaded map.</summary>
    private void VerifyNobodyInside(InstanceSave save, string code, string action)
    {
        if (_world.FindMap(save.MapId, save.InstanceId) is { PlayerCount: > 0 } map)
        {
            AnomalyLedger.Shared.Report(AnomalySubsystem, code, SubjectOf(save),
                string.Create(CultureInfo.InvariantCulture, $"{action} while {map.PlayerCount} player(s) were inside the map"));
        }
    }

    private static void ReportEnterRefused(Player player, uint mapId, TransferAbortReason reason, bool sendErrors)
    {
        AnomalyLedger.Shared.Report(
            AnomalySubsystem,
            sendErrors ? EnterRefusedCode : EnterRefusedSilentlyCode,
            SubjectOf(player),
            string.Create(CultureInfo.InvariantCulture, $"entry to map {mapId} refused: {reason}{(sendErrors ? string.Empty : "; no message was sent")}"),
            sendErrors ? AnomalySeverity.Info : AnomalySeverity.Warning);
    }

    /// <summary>The script's save string now, or null when it keeps none or throws (a throwing script is already counted by the fault ledger).</summary>
    private static string? CurrentScriptData(Map map)
    {
        if (map.FindUpdater<InstanceData>() is not { } data)
        {
            return null;
        }

        try
        {
            return data.GetSaveData();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("instance.script-save-data", ex);
            return null;
        }
    }

    /// <summary>At unload: a script state that is neither in the save nor the state the map started with was never saved.</summary>
    private void VerifyScriptStateSaved(Map map, InstanceMapState state)
    {
        InstanceSave save = state.Save;
        if (save.IsDeleted || state.ResetAfterUnload)
        {
            return; // the instance is being reset: the state is meant to go
        }

        string? current = CurrentScriptData(map);
        string? expected = save.Data ?? state.BaselineData;
        if (current is not null && !string.Equals(current, expected, StringComparison.Ordinal))
        {
            AnomalyLedger.Shared.Report(AnomalySubsystem, SaveDataStaleCode, SubjectOf(save),
                string.Create(CultureInfo.InvariantCulture, $"script state '{current}' differs from the saved '{expected}' as the map unloads; it will be lost"));
        }
    }

    private void ReportSaveDataDropped(InstanceSave save, string text)
        => AnomalyLedger.Shared.Report(AnomalySubsystem, SaveDataDroppedCode, SubjectOf(save),
            string.Create(CultureInfo.InvariantCulture, $"script saved '{text}' after the save was deleted"));
}
