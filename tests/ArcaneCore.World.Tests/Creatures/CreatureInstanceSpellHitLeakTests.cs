using System.Reflection;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Creatures;
using Xunit;

namespace ArcaneCore.World.Tests.Creatures;

/// <summary>
/// The shared creature spell caster must not keep a handler for an unloaded instance's creature system:
/// the delegate list would root the dead Map and make every later spell hit walk it.
/// </summary>
public sealed class CreatureInstanceSpellHitLeakTests
{
    private const uint Dungeon = 36;
    private const uint WolfEntry = 299;

    [Fact]
    public async Task UnloadedInstance_ReleasesItsSharedSpellHitSubscription()
    {
        var wolf = new CreatureTemplate { Entry = WolfEntry, Name = "Young Wolf", MinLevel = 2, MaxLevel = 2, DisplayIds = [903], Faction = 32, MinLevelHealth = 55, MaxLevelHealth = 55 };
        var spawn = new CreatureSpawn { Guid = 4243, Entry = WolfEntry, MapId = Dungeon, X = 2f, Y = 0f, Z = 0f };
        var context = new CreatureTestContext(new CreatureContent([wolf], [spawn], [], [], []));
        CreatureTestStore.Current.Value = context;
        WorldTestHost host;
        try
        {
            host = WorldTestHost.Start();
        }
        finally
        {
            CreatureTestStore.Current.Value = null;
        }

        await using (host)
        {
            CreatureWorldFeature feature = context.Feature!;
            var caster = Assert.IsType<SpellSystemCreatureCaster>(feature.AiServices.Spells);
            int baseline = await host.OnWorldAsync(() => HitHandlers(caster));

            Map[] instances = await host.OnWorldAsync(() => new[] { host.World.GetMap(Dungeon, 401), host.World.GetMap(Dungeon, 402), host.World.GetMap(Dungeon, 403) });
            Assert.Equal(baseline + instances.Length, await host.OnWorldAsync(() => HitHandlers(caster)));
            Assert.All(instances, map => Assert.NotNull(feature.FindSystem(map)));

            await host.OnWorldAsync(() =>
            {
                foreach (Map map in instances)
                {
                    host.World.UnloadMap(map);
                }
            });
            await host.WaitForWorldAsync(() => instances.All(map => map.IsUnloaded), "the empty instances unload");

            // RED on origin/main: the count stays at baseline + 3, one dead CreatureMapSystem per unloaded instance.
            Assert.Equal(baseline, await host.OnWorldAsync(() => HitHandlers(caster)));
        }
    }

    private static int HitHandlers(SpellSystemCreatureCaster caster)
    {
        var handlers = (Delegate?)typeof(SpellSystemCreatureCaster)
            .GetField("Hit", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(caster);
        return handlers?.GetInvocationList().Length ?? 0;
    }
}
