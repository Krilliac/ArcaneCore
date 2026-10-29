using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Net;
using ArcaneCore.World.Tests.Items;
using ArcaneCore.World.Tests.PacketHeuristics;
using Xunit;

namespace ArcaneCore.World.Tests.Fuzz;

/// <summary>
/// The opcode handler fuzz's player invariant checker must fail on corrupted state, every break it can emit. The corruption is written
/// straight into the entity's fields (<c>Relocate</c> refuses non-finite coordinates, so a handler going through it could not show the
/// position branch), and the test fails if a break name in <see cref="HandlerFuzzKit.AllBreakNames"/> never fires.
/// </summary>
public sealed class HandlerFuzzInvariantTests
{
    private const uint PotionEntry = 920001;

    [Fact]
    public async Task CheckPlayer_FiresEveryBreakItCanEmit_AndNothingForAHealthyPlayer()
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = PotionEntry, Class = 0, Name = "Invariant Potion", DisplayId = 1, Quality = 1, Stackable = 20 });
        WorldTestHost host;
        using (items.Use())
        {
            host = WorldTestHost.Start();
        }

        await using WorldTestHost hostScope = host;
        (WorldTestClient client, WorldSession session) = await PacketHeuristicsTestKit.EnterClientAsync(host, "INVARIANT", "Invariant");
        await using WorldTestClient clientScope = client;
        Player player = session.Player!;

        HashSet<string> fired = await host.OnWorldAsync(() =>
        {
            var seen = new HashSet<string>();

            List<HandlerFuzzBreak> Check(HandlerFuzzInventory before, bool mayChange = false, bool mapChecks = true)
            {
                var found = new List<HandlerFuzzBreak>();
                HandlerFuzzKit.CheckPlayer(player, before, mayChange, mapChecks, found);
                foreach (HandlerFuzzBreak b in found)
                {
                    seen.Add(b.Name);
                }

                return found;
            }

            HandlerFuzzInventory clean = HandlerFuzzInventory.Take(player);
            Assert.Empty(Check(clean)); // a healthy player passes

            float x = player.X, y = player.Y;
            player.X = float.NaN;
            Check(clean); // position-not-finite
            player.X = x;
            player.Y = 20_000f;
            Check(clean); // position-out-of-range
            player.Y = y;

            Map? map = player.Map;
            player.Map = null;
            Check(clean); // map-null
            player.Map = map;
            uint mapId = player.MapId;
            player.MapId = mapId + 7;
            Check(clean); // map-id-mismatch
            player.MapId = mapId;

            uint max = player.MaxHealth;
            player.Health = max + 5;
            Check(clean); // health-above-max
            player.Health = 0;
            Check(clean); // alive-with-zero-health
            player.MaxHealth = 0;
            Check(clean); // max-health-zero
            player.MaxHealth = max;
            player.Health = max;

            player.Combat.DeathState = DeathState.Corpse;
            Check(clean); // dead-with-health
            player.Combat.DeathState = DeathState.Dead;
            player.Health = 5;
            Check(clean); // ghost-health and ghost-flag-mismatch
            player.Combat.DeathState = DeathState.Alive;
            player.Health = max;

            player.Money += 1;
            Check(clean); // money-changed-outside-set
            player.Money -= 1;
            Assert.Empty(Check(clean)); // and back to healthy

            player.Money = uint.MaxValue;
            Check(clean, mayChange: true); // money-above-cap
            player.Money = clean.Money;

            HandlerFuzzInventory beforeItem = HandlerFuzzInventory.Take(player);
            Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(PotionEntry, 3, out Item? potion));
            Check(beforeItem); // inventory-changed-outside-set
            potion!.Count = 200;
            Check(beforeItem, mayChange: true); // stack-above-max
            return seen;
        });

        string[] missing = [.. HandlerFuzzKit.AllBreakNames.Where(n => !fired.Contains(n))];
        Assert.True(missing.Length == 0, "CheckPlayer never fired: " + string.Join(", ", missing));
        Assert.Empty(fired.Except(HandlerFuzzKit.AllBreakNames)); // a new break name must be listed (and shown to fire) here
    }
}
