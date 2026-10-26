using System.Reflection;
using System.Runtime.CompilerServices;
using ArcaneCore.Game.Maps;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.MapThreads;

/// <summary>
/// The shared-static audit of the parallel maps phase (docs/research/multithreading-plan-20261017.md, W2.3): every static field of the
/// simulation assemblies (Game, World, Playerbots) that can be written after the type initializer runs (not readonly, not const, not
/// [ThreadStatic]) is listed below with why it is safe when maps update on several threads. A new one fails this test until it is
/// made thread-safe ([ThreadStatic], Interlocked, a lock, a readonly concurrent collection) or added here with its reason.
/// </summary>
public sealed class StaticMutableStateAuditTests(ITestOutputHelper output)
{
    /// <summary>"Type.field" → why it is safe with parallel maps.</summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["ArcaneCore.Game.Combat.Corpse.s_nextCounter"] = "GUID counter, Interlocked.Increment",
        ["ArcaneCore.Game.Creatures.CreaturePathStats.s_deferred"] = "process-wide counter, Interlocked",
        ["ArcaneCore.Game.Creatures.CreaturePathStats.s_directFallbacks"] = "process-wide counter, Interlocked",
        ["ArcaneCore.Game.Creatures.CreaturePathStats.s_homeTeleports"] = "process-wide counter, Interlocked",
        ["ArcaneCore.Game.Creatures.CreaturePathStats.s_noPath"] = "process-wide counter, Interlocked",
        ["ArcaneCore.Game.Locomotion.ForcedMovement.s_splineId"] = "spline id counter, Interlocked.Increment",
        ["ArcaneCore.Game.Maps.Collision.MMaps.NavMesh.DeadlineClock (property)"] = "test seam, a delegate reference (atomic write) swapped by tests only (serialized in the DisableParallelization collection NavMeshDeadlineClock, keep it that way); production never writes it",
        ["ArcaneCore.Game.Maps.Collision.MMaps.NavMeshTile.s_nextSearchId"] = "tile search id counter, Interlocked.Increment",
        ["ArcaneCore.Game.Maps.Collision.NavJobSystem.s_shared"] = "the process's path-job pool, created under lock (s_sharedGate)",
        ["ArcaneCore.Game.Maps.Collision.PathSearchCounters.s_bvTreesRejected"] = "process-wide counter, Interlocked",
        ["ArcaneCore.Game.Maps.Collision.PathSearchCounters.s_processTileReadTicks"] = "process-wide counter, Interlocked",
        ["ArcaneCore.Game.Maps.Collision.PathSearchCounters.s_processTileReads"] = "process-wide counter, Interlocked",
        ["ArcaneCore.Game.Maps.DefaultMapUpdaters.s_current"] = "volatile reference to an immutable set, swapped by a hot reload",
        ["ArcaneCore.Game.Spells.PersistentAreaAuras.DynamicObject.s_nextCounter"] = "GUID counter, Interlocked.Increment",
        ["ArcaneCore.Game.Updates.BotLeanStats._ringBytes"] = "process-wide counter, Interlocked",
        ["ArcaneCore.Game.Updates.BotLeanStats._ringPackets"] = "process-wide counter, Interlocked",
        ["ArcaneCore.Game.Updates.BotLeanStats._updateBlocks"] = "process-wide counter, Interlocked",
        ["ArcaneCore.World.Features.WorldCodeAssemblies._startup"] = "set once at start-up, before the world thread runs",
        ["ArcaneCore.World.Gm.Character.Boost.BoostEnvironment._sourcesCache"] = "reference to an immutable cache, replaced whole (a race only builds it twice)",
        ["ArcaneCore.World.Gm.Character.Boost.CharacterBoost._candidates"] = "reference to an immutable cache, replaced whole (a race only builds it twice)",
        ["ArcaneCore.World.Gm.Core.DangerousDebugGate.LiveServerProbe (property)"] = "test seam, a delegate reference (atomic write) swapped by tests only (serialized in the DisableParallelization collection DangerousDebugProbeCollection, keep it that way); production never writes it",
        ["ArcaneCore.World.Gm.Inspect.InspectSectionRegistry._discovered"] = "guarded by the registry's Gate lock; immutable array replaced whole",
        ["ArcaneCore.World.Gm.Inspect.InspectSectionRegistry._problems"] = "guarded by the registry's Gate lock; list set once at discovery",
        ["ArcaneCore.World.Gm.Inspect.InspectSectionRegistry._registered"] = "guarded by the registry's Gate lock; immutable array replaced whole",
        ["ArcaneCore.World.Gm.Tools.DebugQueueCommands._baseline"] = "volatile reference to an immutable snapshot, replaced whole by .debug opcodes on/reset",
        ["ArcaneCore.World.HotCode.HotCodeAudit.BeforeWriteHook (property)"] = "test seam, a delegate reference (atomic write) swapped by tests only (HotCodeAuditTests, which filter by their own temp path); production never writes it",
        ["ArcaneCore.World.HotCode.HotCodeMetadataHandler.s_active"] = "Volatile/Interlocked reference",
        ["ArcaneCore.World.Ops.MapFeed.MapFeedConnection.s_lastConnectionId"] = "connection id counter, Interlocked.Increment",
        ["ArcaneCore.World.Playerbots.Chat.PlayerbotChatSafety.s_nextLogin"] = "login sequence, Interlocked.Increment",
        ["ArcaneCore.World.Playerbots.Control.BotController.s_sequence"] = "control event sequence, Interlocked.Increment and Interlocked.Read",
        ["ArcaneCore.World.Playerbots.Groups.PlayerbotWorldBossMeters._active"] = "gauge value, Volatile.Write by the coordinator and Volatile.Read by the meter callback",
        ["ArcaneCore.World.Playerbots.PlayerbotFleetCommands.s_nextPostId"] = "post id, Interlocked.Increment",
        ["ArcaneCore.World.Playerbots.PlayerbotGearScoreSeam.UpgradeGain (property)"] = "test seam, set by tests before the world runs",
        ["ArcaneCore.World.Playerbots.PlayerbotTravel.s_staticRoutes"] = "Volatile reference to an immutable route list",
        ["ArcaneCore.World.Playerbots.Scenarios.PlayerbotScenarioCommands.s_running"] = "Interlocked.CompareExchange flag",
        ["ArcaneCore.World.Playerbots.Scenarios.ScenarioPacketLog.s_sequence"] = "sequence, Interlocked.Increment",
    };

    [Fact]
    public void EveryWritableStaticField_IsThreadSafeOrAllowListed()
    {
        Assembly[] assemblies =
        [
            typeof(WorldRuntime).Assembly,
            typeof(ArcaneCore.World.WorldServiceCollectionExtensions).Assembly,
            typeof(ArcaneCore.World.Playerbots.ManagedPlayerbotFeature).Assembly,
        ];
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Assembly assembly in assemblies)
        {
            foreach (Type type in Types(assembly))
            {
                if (type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false) || type.Name.Contains('<', StringComparison.Ordinal))
                {
                    continue; // lambdas' closure caches and state machines
                }

                foreach (FieldInfo field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (field.IsLiteral || field.IsInitOnly || field.IsDefined(typeof(ThreadStaticAttribute), inherit: false))
                    {
                        continue;
                    }

                    found.Add($"{type.FullName}.{Display(field.Name)}");
                }
            }
        }

        foreach (string entry in found)
        {
            output.WriteLine(entry);
        }

        string[] unexpected = [.. found.Where(f => !Allowed.ContainsKey(f))];
        string[] stale = [.. Allowed.Keys.Where(a => !found.Contains(a))];
        Assert.True(unexpected.Length == 0 && stale.Length == 0,
            $"{unexpected.Length} writable static field(s) not in the audit list:\n  " + string.Join("\n  ", unexpected)
            + $"\n{stale.Length} audit entr(ies) that no longer exist:\n  " + string.Join("\n  ", stale));
    }

    private static string Display(string field)
        => field.StartsWith('<') && field.EndsWith(">k__BackingField", StringComparison.Ordinal) ? field[1..field.IndexOf('>')] + " (property)" : field;

    private static IEnumerable<Type> Types(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }
}
