using System.Diagnostics.CodeAnalysis;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Instances;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Game.Instances;

public sealed partial class InstanceManager
{
    /// <summary>Seconds before a global raid reset at which warnings go out, then the reset itself (vmangos <c>ResetEventType</c>: 3600, 900, 300, 60).</summary>
    private static readonly long[] RaidEventOffsets = [3600, 900, 300, 60];

    /// <summary>The next global reset of a raid map (Unix seconds), or 0 when it has none.</summary>
    public long GetRaidResetTime(uint mapId) => _raidSchedules.TryGetValue(mapId, out RaidSchedule? schedule) ? schedule.ResetTime : 0;

    /// <summary>
    /// The instance reset schedule (vmangos <c>DungeonResetScheduler::Update</c>; the world
    /// feature runs it on a timer, tests call it directly): global raid warnings and resets,
    /// and the reset of normal dungeons whose time has come while they are empty.
    /// </summary>
    public void UpdateSchedule()
    {
        long now = Now;
        foreach ((uint mapId, RaidSchedule schedule) in _raidSchedules)
        {
            try
            {
                while (schedule.Stage < RaidEventOffsets.Length && now >= schedule.ResetTime - RaidEventOffsets[schedule.Stage])
                {
                    if (schedule.Stage == RaidEventOffsets.Length - 1)
                    {
                        GlobalRaidReset(mapId, schedule);
                        break;
                    }

                    // Skip warnings whose time already passed with the next one due.
                    bool nextDue = now >= schedule.ResetTime - RaidEventOffsets[schedule.Stage + 1];
                    if (!nextDue)
                    {
                        SendResetWarnings(mapId, (uint)Math.Max(0, schedule.ResetTime - now));
                    }

                    schedule.Stage++;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("instance.manager", ex);
                _logger.LogFault("instance.update-raid-schedule", ex, "Failed to update raid reset schedule for map {MapId}", mapId);
            }
        }

        foreach (InstanceSave save in _saves.Values.Where(s => !s.Template.IsRaid && s.ResetScheduled && now >= s.ResetTime).ToArray())
        {
            try
            {
                Map? map = _world.FindMap(save.MapId, save.InstanceId);
                if (map is null)
                {
                    ResetSave(save);
                }
                else if (map.PlayerCount == 0 && map.TransitCount == 0 && _mapStates.TryGetValue(map, out InstanceMapState? state))
                {
                    state.ResetAfterUnload = true;
                    state.UnloadTimerMs = InstanceOptions.MinUnloadDelayMs;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("instance.manager", ex);
                _logger.LogFault("instance.update-save-reset", ex, "Failed to process scheduled reset for map {MapId} instance {InstanceId}", save.MapId, save.InstanceId);
            }
        }
    }

    private void InitializeRaidSchedules(IReadOnlyList<InstanceResetRecord> stored)
    {
        long now = Now;
        long today = now / Day * Day;
        long diff = (long)Math.Clamp(_options.ResetTimeHour, 0, 23) * 3600;
        var storedByMap = stored.GroupBy(r => r.MapId).ToDictionary(g => g.Key, g => g.Last().ResetTime);
        foreach (MapTemplate template in Registry.All.Where(t => t.IsDungeon && t.ResetDelay > 0))
        {
            long period = _options.EffectiveResetDelayDays(template.ResetDelay) * Day; // Rate.InstanceResetTime (ObjectMgr.cpp:6809-6811)
            long t = storedByMap.TryGetValue(template.Entry, out long value) && value > 0
                ? value
                : _raidSchedules.TryGetValue(template.Entry, out RaidSchedule? existing) ? existing.ResetTime : today + period + diff;
            if (t <= now)
            {
                t += ((now - t) / period + 1) * period; // keep the weekly phase
            }

            var schedule = new RaidSchedule(t);
            schedule.SkipPastEvents(now);
            _raidSchedules[template.Entry] = schedule;
            if (!storedByMap.TryGetValue(template.Entry, out long old) || old != t)
            {
                _persistence.RaidResetTimeChanged(template.Entry, t);
            }
        }
    }

    // vmangos MapPersistentStateManager::_ResetOrWarnAll (reset): everyone is unbound, players
    // inside are sent to their bind point, the maps unload, the next reset is scheduled. A save
    // whose map is loaded is deleted when that map unloads (ResetAfterUnload; vmangos removes the
    // state in ~Map), so players whose trip home is still pending or failed are not left in a
    // deleted instance; the homebind timer of the now invalid players retries the trip.
    private void GlobalRaidReset(uint mapId, RaidSchedule schedule)
    {
        foreach (InstanceSave save in _saves.Values.Where(s => s.MapId == mapId).ToArray())
        {
            if (_world.FindMap(save.MapId, save.InstanceId) is { } map && _mapStates.ContainsKey(map))
            {
                UnbindAll(save);
                ResetLoadedMap(map, global: true, notifyInside: false);
                foreach (Player player in map.Players.ToArray())
                {
                    TeleportToHomebind(player);
                }

                continue;
            }

            ResetSave(save);
        }

        MapTemplate? template = Registry.Find(mapId);
        long period = Math.Max(1, _options.EffectiveResetDelayDays(template?.ResetDelay ?? 7)) * Day;
        long diff = (long)Math.Clamp(_options.ResetTimeHour, 0, 23) * 3600;
        long next = (schedule.ResetTime / Day * Day) + period + diff;
        long now = Now;
        if (next <= now)
        {
            next += ((now - next) / period + 1) * period;
        }

        schedule.ResetTime = next;
        schedule.Stage = 0;
        schedule.SkipPastEvents(now);
        _persistence.RaidResetTimeChanged(mapId, next);
        _logger.LogInformation("global reset of raid map {Map}; next at {Next}", mapId, DateTimeOffset.FromUnixTimeSeconds(next));
    }

    private void SendResetWarnings(uint mapId, uint timeLeft)
    {
        byte[] packet = InstancePackets.BuildRaidInstanceMessage(InstancePackets.MessageTypeFor(timeLeft), mapId, timeLeft);
        foreach (Map map in _mapStates.Keys.Where(m => m.MapId == mapId))
        {
            foreach (Player player in map.Players)
            {
                player.Session.Send(WorldOpcode.SmsgRaidInstanceMessage, packet);
            }
        }
    }

    private sealed class RaidSchedule(long resetTime)
    {
        public long ResetTime { get; set; } = resetTime;

        /// <summary>Index into <see cref="RaidEventOffsets"/> of the next event.</summary>
        public int Stage { get; set; }

        public void SkipPastEvents(long now)
        {
            // Warnings already in the past are not replayed; the reset event is never skipped.
            while (Stage < RaidEventOffsets.Length - 1 && now >= ResetTime - RaidEventOffsets[Stage + 1])
            {
                Stage++;
            }

            if (Stage < RaidEventOffsets.Length - 1 && now >= ResetTime - RaidEventOffsets[Stage])
            {
                Stage++;
            }
        }
    }
}
