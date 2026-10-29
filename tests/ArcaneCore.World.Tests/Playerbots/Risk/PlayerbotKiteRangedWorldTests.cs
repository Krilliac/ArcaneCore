using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Risk;

/// <summary>
/// A hurt warrior with a bow kites on the real world (<see cref="RiskTestWorld"/>, manual clock): it hamstrings the creature on it, walks
/// out, and then holds its range and shoots instead of walking back into melee after every step (the brain positions with the kite's
/// range while the kite is under way). The spells are synthetic (an instant Hamstring with a 50% snare, an instant Shoot Bow); the
/// weapon and the arrows are real items from an <see cref="ItemTestContent"/>. Also pins <see cref="PlayerbotKite.HasUsableRangedWeapon"/>.
/// </summary>
public sealed class PlayerbotKiteRangedWorldTests
{
    private const uint HamstringId = 992_510;
    private const uint ShootBowId = 992_511;
    private const uint MobEntry = 992_610;
    private const uint BowEntry = 992_700;
    private const uint ThrownEntry = 992_701;
    private const uint ArrowEntry = 992_702;

    private const uint SubClassBow = 2;
    private const uint SubClassThrown = 16;
    private const uint SubClassArrow = 2;

    private static SpellInfo Hamstring() => new()
    {
        Id = HamstringId, Name = "Hamstring", Rank = "Rank 1", CastTime = new SpellCastTime(0, 0, 0), RangeIndex = SpellConstants.RangeIndexCombat,
        Range = new SpellRange(0f, 5f), StartRecoveryCategory = 133, StartRecoveryTime = 1500, DamageClass = SpellDamageClass.Magic,
        Duration = new SpellDuration(8_000, 0, 8_000),
        Effects =
        [
            new SpellEffectInfo { Effect = SpellEffectName.SchoolDamage, TargetA = SpellImplicitTarget.UnitEnemy, BasePoints = 0, BaseDice = 1, DieSides = 1 },
            new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.ModDecreaseSpeed, TargetA = SpellImplicitTarget.UnitEnemy, BasePoints = -51, BaseDice = 1, DieSides = 1 },
            new SpellEffectInfo(),
        ],
    };

    private static SpellInfo ShootBow() => new()
    {
        Id = ShootBowId, Name = "Shoot Bow", Rank = "Rank 1", CastTime = new SpellCastTime(0, 0, 0), RangeIndex = 5, Range = new SpellRange(0f, 35f),
        StartRecoveryCategory = 133, StartRecoveryTime = 1500, DamageClass = SpellDamageClass.Magic,
        Effects =
        [
            new SpellEffectInfo { Effect = SpellEffectName.SchoolDamage, TargetA = SpellImplicitTarget.UnitEnemy, BasePoints = 0, BaseDice = 1, DieSides = 1 },
            new SpellEffectInfo(),
            new SpellEffectInfo(),
        ],
    };

    private static ItemTestContent Content(uint maxDurability = 0)
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate
        {
            Entry = BowEntry, Name = "Test bow", Class = (uint)ItemClass.Weapon, SubClass = SubClassBow, InventoryType = (uint)InventoryType.Ranged,
            Delay = 2500, Damages = [new ItemDamage(3, 5, 0)], Stackable = 1, MaxDurability = maxDurability,
        });
        items.Templates.Templates.Add(new ItemTemplate
        {
            Entry = ThrownEntry, Name = "Test knife", Class = (uint)ItemClass.Weapon, SubClass = SubClassThrown, InventoryType = (uint)InventoryType.Thrown,
            Delay = 2000, Damages = [new ItemDamage(3, 5, 0)], Stackable = 200,
        });
        items.Templates.Templates.Add(new ItemTemplate
        {
            Entry = ArrowEntry, Name = "Test arrow", Class = (uint)ItemClass.Projectile, SubClass = SubClassArrow, InventoryType = (uint)InventoryType.Ammo,
            Damages = [new ItemDamage(1, 2, 0)], Stackable = 200,
        });
        return items;
    }

    /// <summary>World thread: wear <paramref name="entry"/> in the ranged slot and carry <paramref name="arrows"/> arrows, selected as the ammunition when any.</summary>
    private static Item EquipRanged(Player player, uint entry, uint arrows)
    {
        Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(entry, 1, out Item? weapon));
        Assert.NotNull(weapon);
        player.Inventory.RemoveItem(weapon.BagSlot, weapon.Slot);
        player.Inventory.EquipItem(InventorySlots.Ranged, weapon);
        Item worn = player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Ranged) ?? throw new InvalidOperationException("the ranged weapon was not equipped");
        if (arrows > 0)
        {
            Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(ArrowEntry, arrows, out _));
            player.Inventory.RestoreAmmo(ArrowEntry);
        }

        return worn;
    }

    private static float Gap(RiskTestWorld world, Creature mob)
        => Vector3.Distance(new Vector3(world.Player.X, world.Player.Y, world.Player.Z), new Vector3(mob.X, mob.Y, mob.Z));

    private static string Describe(RiskTestWorld world, Creature mob)
        => $"gap {Gap(world, mob):F1} mob speed {mob.RunSpeed:F1} bot speed {world.Player.RunSpeed:F1} steps {world.Brain.Kite.Steps} over {world.Brain.Kite.Over} "
            + $"retreat {world.Brain.Risk.Retreat.Active}";

    /// <summary>
    /// The first think's place in the 400 ms spell batch window (retail's, <c>ARCANECORE_TEST_SPELL_BATCH_MS=400</c>): every 50 ms manual-clock
    /// step of it, so a kite that only works at some phases of the batch boundary against the 100 ms think cannot pass by the luck of the
    /// set-up's wall-clock time. 10 000 is a multiple of the window.
    /// </summary>
    public static TheoryData<int> BatchPhases() => [0, 50, 100, 150, 200, 250, 300, 350];

    /// <summary>
    /// W1: a warrior at 45% health with a bow and arrows and Hamstring, a melee creature on it. Over the first eight seconds it is outside
    /// melee reach for most of the sampled ticks (it used to step out, fire at most once and walk back in: an out-and-back every step),
    /// and the creature takes damage while the warrior is out of reach, which only a shot can deal. The clock starts at a pinned phase of
    /// the batch window and the spell rolls are seeded (<see cref="RiskTestWorld.StartAsync"/>): on unseeded rolls the first Hamstring was
    /// resisted 1 time in 25 (a 96% magic hit), the warrior then waited out the global cooldown in melee and recast it, and the same kite
    /// ran 1.5 s later, out of reach for only 37 of the 80 ticks.
    /// </summary>
    [Theory]
    [MemberData(nameof(BatchPhases))]
    public async Task AHurtWarriorWithABow_StaysAtRange_AndShoots_InsteadOfOscillatingToMelee(int phaseMs)
    {
        ItemTestContent items = Content();
        using IDisposable scope = items.Use();
        RiskTestWorld world = await RiskTestWorld.StartAsync(clockStartMs: 10_000 + phaseMs);
        await using (world)
        {
            Creature mob = await world.OnWorldAsync(() => world.Spawn(RiskTestWorld.Template(MobEntry, health: 400, minDamage: 1, maxDamage: 2, runSpeed: 1.0f), 2, 0));
            await world.SeeAsync(mob);
            await world.OnWorldAsync(() =>
            {
                Player player = world.Player;
                player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Warrior);
                SpellFeature feature = world.Session.Services.GetRequiredService<SpellFeature>();
                SpellInfo[] added = [Hamstring(), ShootBow()];
                feature.System.Store = new SpellStore([.. feature.System.Store.All, .. added], [], []);
                foreach (SpellInfo spell in added) Assert.True(feature.Spellbook.LearnSpell(player, spell.Id));
                EquipRanged(player, BowEntry, 100);
                Assert.True(PlayerbotKite.HasUsableRangedWeapon(player), "the bow is not usable, so the test proves nothing");
                world.Engage(mob);
                player.Health = player.MaxHealth * 45 / 100;
                return true;
            });

            // Positive control for W2's zero: the same Shoot Bow counter must see at least one hit here.
            SpellSystem spellSystem = world.Session.Services.GetRequiredService<SpellFeature>().System;
            int ticks = 0, outside = 0, shots = 0, bowHits = 0;
            Action<Unit, Unit, SpellInfo> onHit = (_, _, spell) => { if (spell.Id == ShootBowId) bowHits++; };
            spellSystem.SpellHit += onHit;
            uint lastHealth = await world.OnWorldAsync(() => mob.Health);
            float lastGap = await world.OnWorldAsync(() => Gap(world, mob));
            Assert.Equal((uint)phaseMs, await world.OnWorldAsync(() => world.Session.World.NowMs % 400));
            try
            {
                await world.ThinkUntilAsync(8_000, () => false, each: () =>
                {
                    float gap = Gap(world, mob);
                    ticks++;
                    if (gap > 6f) outside++;
                    if (gap > 6f && lastGap > 6f && mob.Health < lastHealth) shots++;
                    lastHealth = mob.Health;
                    lastGap = gap;
                });
            }
            finally
            {
                spellSystem.SpellHit -= onHit;
            }

            string why = await world.OnWorldAsync(() => Describe(world, mob));
            Assert.True(ticks >= 70, $"only {ticks} ticks were sampled");
            Assert.True(outside * 2 > ticks, $"outside melee reach for {outside} of {ticks} ticks: {why}");
            Assert.True(shots >= 1, $"the warrior never shot from range ({shots} damaging ticks out of reach): {why}");
            Assert.True(bowHits >= 1, $"no Shoot Bow hit was counted, so W2's zero proves nothing: {why}");
            Assert.True(await world.OnWorldAsync(() => world.Brain.Kite.Steps) >= 1, "the warrior never stepped: " + why);
            Assert.False(await world.OnWorldAsync(() => world.Brain.Risk.Retreat.Active), "retreating: " + why);
        }
    }

    /// <summary>
    /// W2: the real wiring <c>PlayerbotKite.HoldsRange</c> to <c>PlayerbotCombatSpells.KiteHolds</c> to <c>PlayerbotCombatView.Build</c>. A
    /// warrior at full health with a bow, arrows and Shoot Bow, 20 yards from a creature that is on it, is not kiting, so it walks in toward it (inside 8 yards within three seconds), fights it in melee and
    /// fires no shot on the way (before, any stationary warrior out of melee reach shot, and the brain, which runs the
    /// rotation before positioning, never walked it in).
    /// </summary>
    [Fact]
    public async Task AHealthyWarriorWithABow_WalksIn_AndFiresNoShotOnTheWay()
    {
        ItemTestContent items = Content();
        using IDisposable scope = items.Use();
        RiskTestWorld world = await RiskTestWorld.StartAsync();
        await using (world)
        {
            // Spawned north: a walk east from the human start crosses an area trigger within seven yards, and the bot (which follows triggers
            // like a player) is teleported 687 yards away; the creature then loses its target and evades (SelectHostileTarget). That is the
            // fixture's terrain, not the kite, so the walk must not cross it.
            Creature mob = await world.OnWorldAsync(() => world.Spawn(RiskTestWorld.Template(MobEntry, health: 400, minDamage: 1, maxDamage: 2, runSpeed: 1.0f), 0, 20));
            await world.SeeAsync(mob);
            await world.OnWorldAsync(() =>
            {
                Player player = world.Player;
                player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Warrior);
                SpellFeature feature = world.Session.Services.GetRequiredService<SpellFeature>();
                SpellInfo[] added = [ShootBow()];
                feature.System.Store = new SpellStore([.. feature.System.Store.All, .. added], [], []);
                foreach (SpellInfo spell in added) Assert.True(feature.Spellbook.LearnSpell(player, spell.Id));
                EquipRanged(player, BowEntry, 100);
                Assert.True(PlayerbotKite.HasUsableRangedWeapon(player), "the bow is not usable, so the test proves nothing");
                world.Engage(mob);
                Assert.True(Gap(world, mob) > 15f, "the creature is not 20 yards away, so the test proves nothing");
                return true;
            });

            // Count Shoot Bow hits through the spell system (the creature's health drop would also count melee), and watch the creature each tick.
            SpellSystem spellSystem = world.Session.Services.GetRequiredService<SpellFeature>().System;
            int shots = 0, evadeTicks = 0;
            string trace = "";
            Action<Unit, Unit, SpellInfo> onHit = (_, _, spell) => { if (spell.Id == ShootBowId) shots++; };
            spellSystem.SpellHit += onHit;
            Action sample = () =>
            {
                if (mob.IsInEvadeMode) evadeTicks++;
                trace += $"[{Gap(world, mob):F1} evade={mob.IsInEvadeMode} victim={mob.Combat.Victim?.Guid} threat={mob.Combat.Threat.Entries.Count} combat={world.Player.Combat.IsInCombat}] ";
            };
            try
            {
                bool closed = await world.ThinkUntilAsync(3_000, () => Gap(world, mob) <= 8f, each: sample);
                string why = await world.OnWorldAsync(() => Describe(world, mob)) + " " + trace;
                Assert.True(closed, "the warrior never closed in on the creature: " + why);

                // Then it fights in melee: the creature stays on it (not evading) and takes the warrior's swings.
                uint healthAtMelee = await world.OnWorldAsync(() => mob.Health);
                await world.ThinkUntilAsync(4_000, () => false, each: sample);
                why = await world.OnWorldAsync(() => Describe(world, mob)) + " " + trace;
                Assert.Equal(0, evadeTicks);
                Assert.True(await world.OnWorldAsync(() => Gap(world, mob) <= 6f), "the warrior is not in melee reach: " + why);
                Assert.True(await world.OnWorldAsync(() => mob.Health < healthAtMelee), "the warrior dealt no melee damage: " + why);
                Assert.Equal(0, shots);
                Assert.Equal(0, await world.OnWorldAsync(() => world.Brain.Kite.Steps));
                Assert.NotNull(await world.OnWorldAsync(() => world.Brain.CombatSpells.KiteHolds));
                Assert.False(await world.OnWorldAsync(() => world.Brain.CombatSpells.KiteHolds!.Invoke(world.Player, mob)), "the kite holds a full-health warrior: " + why);
            }
            finally
            {
                spellSystem.SpellHit -= onHit;
            }
        }
    }

    /// <summary>
    /// W3: a hurt warrior that has begun to hold range against a creature, then fights a hostile player (the party and group AI call the kite
    /// with player targets too). The kite's hold is for creature fights: <c>Update</c> answers false and positioning (<c>HoldRange</c>) and the
    /// rotation (<c>KiteHolds</c>) both read no hold, so the bot melees the player instead of standing at range with no shot.
    /// </summary>
    [Fact]
    public async Task AHurtWarriorFightingAHostilePlayer_DoesNotHoldRange_WhileACreatureIsOnIt()
    {
        ItemTestContent items = Content();
        using IDisposable scope = items.Use();
        RiskTestWorld world = await RiskTestWorld.StartAsync();
        await using (world)
        {
            WorldSession enemy = await Combat.PlayerbotPvpTestKit.EnterAsync(world.Host, "KITEENEMY", "Kiteenemy", Combat.PlayerbotPvpTestKit.Orc);
            try
            {
                Creature mob = await world.OnWorldAsync(() => world.Spawn(RiskTestWorld.Template(MobEntry, health: 400, minDamage: 1, maxDamage: 2, runSpeed: 1.0f), 2, 0));
                await world.SeeAsync(mob);
                await world.OnWorldAsync(() =>
                {
                    Player player = world.Player;
                    player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Warrior);
                    SpellFeature feature = world.Session.Services.GetRequiredService<SpellFeature>();
                    SpellInfo[] added = [Hamstring(), ShootBow()];
                    feature.System.Store = new SpellStore([.. feature.System.Store.All, .. added], [], []);
                    foreach (SpellInfo spell in added) Assert.True(feature.Spellbook.LearnSpell(player, spell.Id));
                    EquipRanged(player, BowEntry, 100);
                    world.Engage(mob);
                    player.Health = player.MaxHealth * 45 / 100;
                    return true;
                });

                bool holding = await world.ThinkUntilAsync(8_000, () => world.Brain.Kite.HoldRange(world.Player, 0f) > PlayerbotClassRotation.MeleeRange);
                string why = await world.OnWorldAsync(() => Describe(world, mob));
                Assert.True(holding, "the warrior never began to hold range against the creature, so the test proves nothing: " + why);

                (bool owned, float hold, bool reads) = await world.OnWorldAsync(() =>
                {
                    Player player = world.Player;
                    Player other = enemy.Player!;
                    Combat.PlayerbotPvpTestKit.Flag(other);
                    other.Relocate(player.X + 4, player.Y, player.Z, MathF.PI, world.Session.World.NowMs);
                    Assert.True(player.Combat.IsInCombat, "the warrior left combat, so the test proves nothing");
                    Assert.Same(player.Map, other.Map);
                    Assert.False(mob.IsInEvadeMode, "the creature is evading, so the test proves nothing");
                    Assert.True(mob.IsAlive && mob.Combat.Victim?.Guid == player.Guid, "the creature is no longer pursuing the warrior, so the test proves nothing");
                    // Inside the scan radius, so without the player guard Update would get past "no pursuers" and own the think.
                    Assert.True(Gap(world, mob) <= PlayerbotKiteModel.StartYards(hunter: false), "the creature is outside the kite's scan, so the test proves nothing: " + Describe(world, mob));
                    bool kiteOwns = world.Brain.Kite.Update(player, other, RiskTestWorld.ThinkMs, null, 0f, world.Brain.Risk, grouped: true);
                    return (kiteOwns, world.Brain.Kite.HoldRange(player, 0f), world.Brain.CombatSpells.KiteHolds!.Invoke(player, other));
                });
                Assert.False(owned, "the kite took a think against a player: " + why);
                Assert.Equal(0f, hold);
                Assert.False(reads, "the rotation reads a hold against a player: " + why);
                Assert.Equal(PlayerbotClassRotation.MeleeRange, await world.OnWorldAsync(() => world.Brain.Kite.HoldRange(world.Player, PlayerbotClassRotation.MeleeRange)));
            }
            finally
            {
                enemy.Kick();
                await enemy.ManagedClosed;
            }
        }
    }

    /// <summary>A bow with no arrows selected shoots nothing, so the warrior does not count as a ranged fighter.</summary>
    [Fact]
    public async Task ABowWithNoAmmunition_IsNotUsable()
    {
        ItemTestContent items = Content();
        using IDisposable scope = items.Use();
        RiskTestWorld world = await RiskTestWorld.StartAsync();
        await using (world)
        {
            Assert.False(await world.OnWorldAsync(() =>
            {
                EquipRanged(world.Player, BowEntry, 0);
                Assert.Equal(0u, world.Player.Inventory.AmmoId);
                return PlayerbotKite.HasUsableRangedWeapon(world.Player);
            }));
        }
    }

    /// <summary>The control for the two cases around it: the same bow with arrows is usable.</summary>
    [Fact]
    public async Task ABowWithArrows_IsUsable()
    {
        ItemTestContent items = Content();
        using IDisposable scope = items.Use();
        RiskTestWorld world = await RiskTestWorld.StartAsync();
        await using (world)
            Assert.True(await world.OnWorldAsync(() => { EquipRanged(world.Player, BowEntry, 20); return PlayerbotKite.HasUsableRangedWeapon(world.Player); }));
    }

    /// <summary>A bow worn down to zero durability does not shoot, arrows or not.</summary>
    [Fact]
    public async Task ABowAtZeroDurability_IsNotUsable()
    {
        ItemTestContent items = Content(maxDurability: 40);
        using IDisposable scope = items.Use();
        RiskTestWorld world = await RiskTestWorld.StartAsync();
        await using (world)
        {
            Assert.False(await world.OnWorldAsync(() =>
            {
                Item bow = EquipRanged(world.Player, BowEntry, 20);
                Assert.Equal(40u, bow.MaxDurability);
                Assert.True(PlayerbotKite.HasUsableRangedWeapon(world.Player), "the bow is not usable even before it wears out, so the test proves nothing");
                bow.Durability = 0;
                return PlayerbotKite.HasUsableRangedWeapon(world.Player);
            }));
        }
    }

    /// <summary>A thrown weapon is its own ammunition: usable with no arrows or bullets selected.</summary>
    [Fact]
    public async Task AThrownWeapon_IsUsableWithNoAmmunition()
    {
        ItemTestContent items = Content();
        using IDisposable scope = items.Use();
        RiskTestWorld world = await RiskTestWorld.StartAsync();
        await using (world)
        {
            Assert.True(await world.OnWorldAsync(() =>
            {
                EquipRanged(world.Player, ThrownEntry, 0);
                Assert.Equal(0u, world.Player.Inventory.AmmoId);
                return PlayerbotKite.HasUsableRangedWeapon(world.Player);
            }));
        }
    }
}
