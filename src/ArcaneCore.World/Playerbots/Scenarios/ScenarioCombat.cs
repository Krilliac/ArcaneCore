using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots.Scenarios;

/// <summary>One of the bot's own completed casts (SMSG_SPELL_GO) with what the world looked like at that tick.</summary>
public sealed record CombatCast(uint SpellId, string Name, float Distance, float HealthPercent, IReadOnlyList<ulong> Targets, bool HadPet,
    uint WorldMs);

/// <summary>
/// The casts an autonomous bot completed, read from its recorded SMSG_SPELL_GO packets on the world thread during a wait
/// (<see cref="Observe"/> runs after every tick), so each cast carries the bot's distance to the victim and its own health at the
/// tick the server sent it.
/// </summary>
public sealed class CombatTrace(ScenarioContext context, ScenarioBot bot, Creature victim)
{
    private long _since = 1;

    public List<CombatCast> Casts { get; } = [];

    /// <summary>Whether the bot's pet was ever seen attacking the victim.</summary>
    public bool PetAttackedVictim { get; private set; }

    /// <summary>
    /// The nearest the bot itself came to where the victim stood when the trace started (the victim's own approach does not count):
    /// how far a ranged bot advanced into the fight.
    /// </summary>
    public float ClosestToVictimStart { get; private set; } = float.MaxValue;

    private readonly (float X, float Y) _victimStart = (victim.X, victim.Y);

    /// <summary>Record the casts received since the last look (world thread).</summary>
    public void Observe()
    {
        Player player = bot.RequirePlayer();
        float sx = player.X - _victimStart.X, sy = player.Y - _victimStart.Y;
        ClosestToVictimStart = MathF.Min(ClosestToVictimStart, MathF.Sqrt((sx * sx) + (sy * sy)));
        SpellSystem spells = context.Services.GetRequiredService<SpellFeature>().System;
        if (!player.PetGuid.IsEmpty && player.Map?.FindObject(player.PetGuid) is Creature pet && ReferenceEquals(pet.Combat.Victim, victim))
            PetAttackedVictim = true;
        foreach (ScenarioPacket packet in bot.Log.Received(WorldOpcode.SmsgSpellGo, _since))
        {
            _since = packet.Sequence + 1;
            SpellGoView go = ScenarioDecoders.SpellGo(packet.Payload);
            if (go.Caster != bot.Guid.Value) continue;
            float dx = player.X - victim.X, dy = player.Y - victim.Y, dz = player.Z - victim.Z;
            Casts.Add(new CombatCast(go.SpellId, spells.Store.Get(go.SpellId)?.Name ?? "?", MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz)),
                player.MaxHealth == 0 ? 0f : player.Health * 100f / player.MaxHealth, go.Hits, !player.PetGuid.IsEmpty, packet.WorldMs));
        }
    }

    public CombatCast? First(string name) => Casts.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => string.Join(", ", Casts.Select(c => $"{c.Name}@{c.Distance:F1}yd"));
}

/// <summary>
/// A bot of one class, created with its first spells, is handed to its own brain beside the scenario wolf and must kill it the way
/// its class fights: the class's spells go off (SMSG_SPELL_GO from the bot), a caster's first attack lands from range, and the kill
/// is the bot's (its SMSG_LOG_XPGAIN names the wolf). The spells are taught by their classic ids; the wolf is the scenario content's
/// "Scenario Wolf" (entry 990001, spawn 990001 east of the human start, docs/areas/playbots-combat.md). Where that content is
/// absent the run fails at the named "find the wolf" step.
/// </summary>
public abstract class ClassCombatScenario : IPlayerbotScenario
{
    /// <summary>The scenario content's hostile level-1 wolf (14 health).</summary>
    public const uint WolfEntry = 990001;

    public const uint WolfSpawn = 990001;

    public const float WolfX = -8889.95f;

    public const float WolfY = -132.49f;

    public const float WolfZ = 83.53f;

    public static ObjectGuid Wolf => ObjectGuid.WithEntry(HighGuid.Unit, WolfEntry, WolfSpawn);

    protected ClassCombatScenario(string className, byte race, Class playerClass, IReadOnlyList<uint> spells, IReadOnlyList<string> expectedCasts,
        float firstCastDistance = 0f)
    {
        ClassName = className;
        Race = race;
        PlayerClass = playerClass;
        Spells = spells;
        ExpectedCasts = expectedCasts;
        FirstCastDistance = firstCastDistance;
    }

    public string ClassName { get; }

    public byte Race { get; }

    public Class PlayerClass { get; }

    /// <summary>The spells taught after creation (classic ids).</summary>
    public IReadOnlyList<uint> Spells { get; }

    /// <summary>Spell names that must go off (SMSG_SPELL_GO from the bot) by the time the wolf is dead.</summary>
    public IReadOnlyList<string> ExpectedCasts { get; }

    /// <summary>The least distance of the bot's first damaging cast (a caster attacks from range); 0: no check.</summary>
    public float FirstCastDistance { get; }

    /// <summary>The first damaging spell the range check applies to (null: none).</summary>
    protected virtual string? FirstAttack => null;

    /// <summary>The least distance a ranged bot keeps from the wolf's starting spot while fighting.</summary>
    public const float RangedHold = 20f;

    /// <summary>Where the bot is put: this many yards west of the wolf.</summary>
    protected virtual float StartDistance => 35f;

    public string Name => "combat-" + ClassName;

    public string Description => $"a level-1 {ClassName} bot kills a wolf on its own ({string.Join(", ", ExpectedCasts)})";

    /// <summary>The bot's name: "Cmb" and the class.</summary>
    public string BotName => "Cmb" + ClassName;

    public async Task RunAsync(ScenarioContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ScenarioBot bot = await context.StepAsync($"login {BotName} ({Race}/{(byte)PlayerClass})",
            () => context.LoginAsync(BotName, Race, (byte)PlayerClass)).ConfigureAwait(false);
        await context.StepAsync("the bot is a " + ClassName, async () => ScenarioContext.ExpectEqual(PlayerClass,
            await bot.ReadAsync(p => p.Class).ConfigureAwait(false), "class")).ConfigureAwait(false);
        await context.StepAsync("teach its spells", async () =>
        {
            foreach (uint spell in Spells) await context.LearnSpellAsync(bot, spell).ConfigureAwait(false);
        }).ConfigureAwait(false);
        await PrepareAsync(context, bot).ConfigureAwait(false);

        Creature wolf = await context.StepAsync("find the wolf", async () =>
        {
            await context.PlaceAsync(bot, 0, WolfX - StartDistance, WolfY, WolfZ, 0f).ConfigureAwait(false);
            Creature? found = null;
            await context.WaitUntilAsync("the scenario wolf (entry 990001) is in sight", () =>
                (found = context.FindCreature(bot, Wolf)) is { IsAlive: true }, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            return found!;
        }).ConfigureAwait(false);

        var trace = new CombatTrace(context, bot, wolf);
        long mark = bot.Mark();
        await context.StepAsync("hand the bot to its brain", () => context.AutonomousAsync(bot)).ConfigureAwait(false);
        await context.StepAsync("the bot kills the wolf", async () =>
        {
            try
            {
                await context.WaitUntilAsync("the wolf is dead", () =>
                {
                    trace.Observe();
                    DuringFight(context, bot, wolf, trace);
                    return !wolf.IsAlive;
                }, TimeSpan.FromSeconds(120)).ConfigureAwait(false);
            }
            catch (ScenarioTimeoutException timeout)
            {
                string state = await context.ReadAsync(() => Describe(context, bot, wolf)).ConfigureAwait(false);
                throw new ScenarioAssertionException($"{timeout.Message}; it cast: [{trace}]; {state}");
            }
        }).ConfigureAwait(false);
        await context.StepAsync("the kill is the bot's", () => context.WaitUntilAsync("SMSG_LOG_XPGAIN for the wolf", () =>
        {
            trace.Observe();
            return bot.Received(WorldOpcode.SmsgLogXpgain, ScenarioDecoders.XpGain, mark).Any(x => x.Victim == wolf.Guid.Value);
        })).ConfigureAwait(false);

        await context.StepAsync("its class spells went off", () =>
        {
            foreach (string name in ExpectedCasts)
                ScenarioContext.Expect(trace.First(name) is not null, $"no {name} from the bot; it cast: [{trace}]");
            if (FirstAttack is { } attack && FirstCastDistance > 0)
            {
                CombatCast first = trace.First(attack) ?? throw new ScenarioAssertionException($"no {attack}; it cast: [{trace}]");
                ScenarioContext.Expect(first.Distance >= FirstCastDistance,
                    $"the first {attack} went off {first.Distance:F1} yards from the wolf, not from range ({FirstCastDistance} or more)");
                // A ranged bot holds at its range and lets the enemy come: it never walks in on the wolf's spot.
                ScenarioContext.Expect(trace.ClosestToVictimStart >= RangedHold,
                    $"the bot advanced to {trace.ClosestToVictimStart:F1} yards from where the wolf stood (a ranged bot holds at {RangedHold} or more)");
            }
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        await VerifyAsync(context, bot, wolf, trace).ConfigureAwait(false);
        // The evidence in the report: every cast with its distance from the wolf.
        await context.StepAsync($"it cast: [{trace}]; closest to the wolf's spot {trace.ClosestToVictimStart:F1} yd", () => Task.CompletedTask)
            .ConfigureAwait(false);
    }

    /// <summary>What the bot and the wolf look like now, for a failure report (world thread).</summary>
    internal static string Describe(ScenarioContext context, ScenarioBot bot, Creature wolf)
    {
        Player player = bot.RequirePlayer();
        PlayerbotStatus? status = context.Services.GetService<ManagedPlayerbotFeature>()?.Snapshot().FirstOrDefault(s => s.BotId == bot.BotId);
        float dx = player.X - wolf.X, dy = player.Y - wolf.Y;
        string auras = string.Join("/", context.Services.GetRequiredService<SpellFeature>().System.GetAuras(player)
            .Where(h => !h.IsRemoved).Select(h => h.Spell.Name));
        return $"bot goal {status?.Goal} target {status?.TargetEntry} at ({player.X:F1}, {player.Y:F1}) {MathF.Sqrt((dx * dx) + (dy * dy)):F1} yd from the wolf, "
            + $"health {player.Health}/{player.MaxHealth} power {SpellSystem.GetPower(player, player.PowerType)} in combat {player.Combat.IsInCombat} "
            + $"victim {player.Combat.Victim?.Guid.Value:X} auras [{auras}]; wolf health {wolf.Health}/{wolf.MaxHealth} victim {wolf.Combat.Victim?.Guid.Value:X}";
    }

    /// <summary>Class setup before the fight (items, ammunition).</summary>
    protected virtual Task PrepareAsync(ScenarioContext context, ScenarioBot bot) => Task.CompletedTask;

    /// <summary>Runs on the world thread after every tick of the fight.</summary>
    protected virtual void DuringFight(ScenarioContext context, ScenarioBot bot, Creature wolf, CombatTrace trace)
    {
    }

    /// <summary>Class checks after the kill.</summary>
    protected virtual Task VerifyAsync(ScenarioContext context, ScenarioBot bot, Creature wolf, CombatTrace trace) => Task.CompletedTask;
}

/// <summary>Orc warrior: Battle Stance between fights, Charge to open (its rage then feeds Rend).</summary>
public sealed class CombatWarriorScenario() : ClassCombatScenario("warrior", 2, Class.Warrior,
    [ClassCombatSpells.BattleStance, ClassCombatSpells.Charge, ClassCombatSpells.Rend, ClassCombatSpells.HeroicStrike],
    ["Battle Stance", "Charge"]);

/// <summary>Dwarf paladin: Devotion Aura between fights, Seal of Righteousness and Judgement in melee.</summary>
public sealed class CombatPaladinScenario() : ClassCombatScenario("paladin", 3, Class.Paladin,
    [ClassCombatSpells.DevotionAura, ClassCombatSpells.SealOfRighteousness, ClassCombatSpells.Judgement, ClassCombatSpells.HolyLight],
    ["Devotion Aura", "Seal of Righteousness"]);

/// <summary>
/// Troll hunter with a bow and arrows: Auto Shot from beyond its 8-yard dead zone; the first Auto Shot must go off 8 yards or more
/// from the wolf, and the bot never swings in melee before it.
/// </summary>
public sealed class CombatHunterScenario() : ClassCombatScenario("hunter", 8, Class.Hunter,
    [ClassCombatSpells.AutoShot, ClassCombatSpells.RaptorStrike, ClassCombatSpells.SerpentSting],
    ["Auto Shot"], firstCastDistance: 8f)
{
    /// <summary>Worn Shortbow and Rough Arrow (classic item ids).</summary>
    public const uint Bow = 2504;

    public const uint Arrow = 2512;

    protected override string? FirstAttack => "Auto Shot";

    protected override async Task PrepareAsync(ScenarioContext context, ScenarioBot bot)
    {
        await context.StepAsync("equip a bow and carry arrows", async () =>
        {
            ObjectGuid bow = await context.GiveItemAsync(bot, Bow).ConfigureAwait(false);
            await context.GiveItemAsync(bot, Arrow, 200).ConfigureAwait(false);
            (byte bag, byte slot) = await bot.ReadAsync(p => p.Inventory.GetItemByGuid(bow) is { } item ? (item.BagSlot, item.Slot)
                : throw new ScenarioAssertionException("the bow is not in the bags")).ConfigureAwait(false);
            ScenarioContext.Expect(await bot.SendAsync(WorldOpcode.CmsgAutoequipItem, [bag, slot]).ConfigureAwait(false), "equip refused");
            await context.WaitUntilAsync("the bow is in the ranged slot", () =>
                bot.RequirePlayer().Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Ranged)?.Entry == Bow).ConfigureAwait(false);
            await context.ReadAsync(() =>
            {
                bot.RequirePlayer().Inventory.SetAmmo(Arrow);
                return true;
            }).ConfigureAwait(false);
            ScenarioContext.ExpectEqual(Arrow, await bot.ReadAsync(p => p.Inventory.AmmoId).ConfigureAwait(false), "ammo");
        }).ConfigureAwait(false);
    }
}

/// <summary>Human rogue: Stealth while closing in, then Sinister Strike.</summary>
public sealed class CombatRogueScenario() : ClassCombatScenario("rogue", 1, Class.Rogue,
    [ClassCombatSpells.Stealth, ClassCombatSpells.SinisterStrike, ClassCombatSpells.Eviscerate],
    ["Stealth", "Sinister Strike"]);

/// <summary>
/// Undead priest: Power Word: Fortitude between fights, Smite from range; once the fight is on its health is dropped to 40% and it
/// must heal itself with Lesser Heal (vmangos SelectHealTarget below 60% for a healer).
/// </summary>
public sealed class CombatPriestScenario() : ClassCombatScenario("priest", 5, Class.Priest,
    [ClassCombatSpells.Smite, ClassCombatSpells.LesserHeal, ClassCombatSpells.PowerWordFortitude],
    ["Power Word: Fortitude", "Smite", "Lesser Heal"], firstCastDistance: 20f)
{
    private bool _wounded;

    protected override string? FirstAttack => "Smite";

    protected override void DuringFight(ScenarioContext context, ScenarioBot bot, Creature wolf, CombatTrace trace)
    {
        if (_wounded || trace.First("Smite") is null) return;
        Player player = bot.RequirePlayer();
        player.Health = Math.Max(1u, player.MaxHealth * 2 / 5);
        _wounded = true;
    }

    protected override Task VerifyAsync(ScenarioContext context, ScenarioBot bot, Creature wolf, CombatTrace trace)
        => context.StepAsync("it healed itself below half health", () =>
        {
            CombatCast smite = trace.First("Smite")!;
            ScenarioContext.Expect(trace.Casts.Any(c => c.Name == "Lesser Heal" && c.WorldMs >= smite.WorldMs && c.Targets.Contains(bot.Guid.Value)),
                $"no Lesser Heal on itself after it dropped to 40%; it cast: [{trace}]");
            return Task.CompletedTask;
        });
}

/// <summary>Tauren shaman: Lightning Bolt from range.</summary>
public sealed class CombatShamanScenario() : ClassCombatScenario("shaman", 6, Class.Shaman,
    [ClassCombatSpells.LightningBolt, ClassCombatSpells.HealingWave],
    ["Lightning Bolt"], firstCastDistance: 20f)
{
    protected override string? FirstAttack => "Lightning Bolt";
}

/// <summary>Gnome mage: Frost Armor and Arcane Intellect between fights, Fireball from range.</summary>
public sealed class CombatMageScenario() : ClassCombatScenario("mage", 7, Class.Mage,
    [ClassCombatSpells.Fireball, ClassCombatSpells.FrostArmor, ClassCombatSpells.ArcaneIntellect],
    ["Frost Armor", "Arcane Intellect", "Fireball"], firstCastDistance: 20f)
{
    protected override string? FirstAttack => "Fireball";
}

/// <summary>
/// Human warlock: Demon Skin, then its Imp summoned before the pull (the Imp is out when the first Shadow Bolt goes off, and its
/// summon came first), then Shadow Bolt from range with the Imp sent at the wolf.
/// </summary>
public sealed class CombatWarlockScenario() : ClassCombatScenario("warlock", 1, Class.Warlock,
    [ClassCombatSpells.ShadowBolt, ClassCombatSpells.DemonSkin, ClassCombatSpells.SummonImp],
    ["Demon Skin", "Summon Imp", "Shadow Bolt"], firstCastDistance: 20f)
{
    protected override string? FirstAttack => "Shadow Bolt";

    protected override Task VerifyAsync(ScenarioContext context, ScenarioBot bot, Creature wolf, CombatTrace trace)
        => context.StepAsync("the Imp came first and fought", () =>
        {
            CombatCast summon = trace.First("Summon Imp")!;
            CombatCast bolt = trace.First("Shadow Bolt")!;
            ScenarioContext.Expect(summon.WorldMs <= bolt.WorldMs && bolt.HadPet, $"the Imp was not out before the pull; it cast: [{trace}]");
            ScenarioContext.Expect(trace.PetAttackedVictim, "the Imp never attacked the wolf");
            return Task.CompletedTask;
        });
}

/// <summary>Night elf druid: Mark of the Wild between fights, Wrath from range.</summary>
public sealed class CombatDruidScenario() : ClassCombatScenario("druid", 4, Class.Druid,
    [ClassCombatSpells.Wrath, ClassCombatSpells.HealingTouch, ClassCombatSpells.MarkOfTheWild],
    ["Mark of the Wild", "Wrath"], firstCastDistance: 20f)
{
    protected override string? FirstAttack => "Wrath";
}

/// <summary>
/// Etiquette: another player tags the wolf and fights it; an autonomous warrior beside it must leave it alone (vmangos
/// IsValidHostileTarget and the grey name of a tapped creature) for the whole of a bounded watch.
/// </summary>
public sealed class CombatTappedWolfScenario : IPlayerbotScenario
{
    public const string Tagger = "Cmbtagger";

    public const string Bot = "Cmbpolite";

    public string Name => "combat-tapped-wolf";

    public string Description => "a wolf another player tagged is not taken from them";

    public async Task RunAsync(ScenarioContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ScenarioBot tagger = await context.StepAsync("login " + Tagger, () => context.LoginAsync(Tagger)).ConfigureAwait(false);
        ScenarioBot bot = await context.StepAsync("login " + Bot, () => context.LoginAsync(Bot, 2, (byte)Class.Warrior)).ConfigureAwait(false);
        await context.StepAsync("teach the bot its spells", () => context.LearnSpellAsync(bot, ClassCombatSpells.BattleStance)).ConfigureAwait(false);
        Creature wolf = await context.StepAsync("the tagger hits the wolf", async () =>
        {
            await context.PlaceAsync(tagger, 0, ClassCombatScenario.WolfX - 2f, ClassCombatScenario.WolfY, ClassCombatScenario.WolfZ, 0f).ConfigureAwait(false);
            Creature? found = null;
            await context.WaitUntilAsync("the scenario wolf (entry 990001) is in sight", () =>
                (found = context.FindCreature(tagger, ClassCombatScenario.Wolf)) is { IsAlive: true }, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            ScenarioContext.Expect(await tagger.AttackAsync(found!.Guid).ConfigureAwait(false), "attack refused");
            await context.WaitUntilAsync("the wolf is tapped by the tagger", () =>
                (found!.GetValueFor(UpdateFields.UnitDynamicFlags, tagger.RequirePlayer()) & Game.Loot.LootService.UnitDynFlagTappedByPlayer) != 0)
                .ConfigureAwait(false);
            // One tag is enough; the wolf keeps fighting the tagger, who no longer swings back.
            ScenarioContext.Expect(await tagger.StopAttackAsync().ConfigureAwait(false), "attack stop refused");
            return found!;
        }).ConfigureAwait(false);

        await context.StepAsync("the bot stands beside the fight", () =>
            context.PlaceAsync(bot, 0, ClassCombatScenario.WolfX - 12f, ClassCombatScenario.WolfY + 2f, ClassCombatScenario.WolfZ, 0f)).ConfigureAwait(false);
        await context.StepAsync("the wolf is grey to the bot", () => context.WaitUntilAsync("the bot sees the tag", () =>
        {
            uint seen = wolf.GetValueFor(UpdateFields.UnitDynamicFlags, bot.RequirePlayer());
            return bot.RequirePlayer().VisibleObjects.Contains(wolf.Guid) && (seen & Game.Loot.LootService.UnitDynFlagTapped) != 0
                && (seen & Game.Loot.LootService.UnitDynFlagTappedByPlayer) == 0;
        })).ConfigureAwait(false);

        await context.StepAsync("hand the bot to its brain", () => context.AutonomousAsync(bot)).ConfigureAwait(false);
        await context.StepAsync("the bot leaves the wolf alone", async () =>
        {
            bool touched = false;
            try
            {
                await context.WaitUntilAsync("the bot attacks the tagged wolf", () =>
                {
                    Player player = bot.RequirePlayer();
                    touched |= ReferenceEquals(player.Combat.Victim, wolf) || wolf.Combat.Threat.Contains(player)
                        || player.Combat.ThreatenedBy.Contains(wolf);
                    return touched || !wolf.IsAlive;
                }, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            }
            catch (ScenarioTimeoutException)
            {
                // The watch ran out with the wolf untouched by the bot: that is the pass.
            }

            ScenarioContext.Expect(!touched, "the bot attacked a wolf another player had tagged");
        }).ConfigureAwait(false);
    }
}

/// <summary>The classic spell ids the class combat scenarios teach (rank 1 unless named).</summary>
public static class ClassCombatSpells
{
    public const uint BattleStance = 2457;
    public const uint Charge = 100;
    public const uint Rend = 772;
    public const uint RendRank2 = 6546;
    public const uint HeroicStrike = 78;

    public const uint DevotionAura = 465;
    public const uint SealOfRighteousness = 21084;
    public const uint Judgement = 20271;
    public const uint HolyLight = 635;

    public const uint AutoShot = 75;
    public const uint RaptorStrike = 2973;
    public const uint SerpentSting = 1978;

    public const uint Stealth = 1784;
    public const uint SinisterStrike = 1752;
    public const uint Eviscerate = 2098;

    public const uint Smite = 585;
    public const uint LesserHeal = 2050;
    public const uint PowerWordFortitude = 1243;

    public const uint LightningBolt = 403;
    public const uint HealingWave = 331;

    public const uint Fireball = 133;
    public const uint FireballRank2 = 143;
    public const uint FrostArmor = 168;
    public const uint ArcaneIntellect = 1459;

    public const uint ShadowBolt = 686;
    public const uint DemonSkin = 687;
    public const uint SummonImp = 688;

    public const uint Wrath = 5176;
    public const uint HealingTouch = 5185;
    public const uint MarkOfTheWild = 1126;
}
