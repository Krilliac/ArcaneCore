using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Risk;

/// <summary>
/// A retreating bot casts its class escape through the ordinary CMSG_CAST_SPELL handler (the server takes it), once per retreat:
/// Frost Nova for a mage, Vanish for a rogue, Feign Death for a hunter, Psychic Scream for a priest, Intimidating Shout for a
/// warrior. The spells are stand-ins with the 1.12 names (an aura on the caster, or on the enemy for the shout); what is tested is
/// the choice and the cast, not the spells' effects.
/// </summary>
public sealed class PlayerbotRetreatEscapeTests
{
    [Theory]
    [InlineData(Class.Mage, PlayerbotEscapes.FrostNova, false)]
    [InlineData(Class.Rogue, PlayerbotEscapes.Vanish, false)]
    [InlineData(Class.Hunter, PlayerbotEscapes.FeignDeath, false)]
    [InlineData(Class.Priest, PlayerbotEscapes.PsychicScream, false)]
    [InlineData(Class.Warrior, PlayerbotEscapes.IntimidatingShout, true)]
    public async Task ARetreatingBot_CastsItsClassEscape(Class playerClass, string escape, bool atEnemy)
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        Creature mob = await world.OnWorldAsync(() => world.Spawn(RiskTestWorld.Template(991041, health: 500, minDamage: 1, maxDamage: 1), 2, 0));
        await world.SeeAsync(mob);
        uint spellId = 991_400 + (uint)playerClass;
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)playerClass);
            SpellFeature feature = world.Session.Services.GetRequiredService<SpellFeature>();
            feature.System.Store = new SpellStore([.. feature.System.Store.All, new SpellInfo
            {
                Id = spellId, Name = escape, Duration = new SpellDuration(10_000, 0, 10_000),
                Effects = [new SpellEffectInfo
                {
                    Effect = SpellEffectName.ApplyAura, AuraType = AuraType.Dummy,
                    TargetA = atEnemy ? SpellImplicitTarget.UnitEnemy : SpellImplicitTarget.UnitCaster,
                }],
            }], [], []);
            Assert.True(feature.Spellbook.LearnSpell(player, spellId));
            world.Engage(mob);
            world.Brain.Risk.StartRetreat(player, [mob], "test");
            return true;
        });

        Assert.True(await world.ThinkUntilAsync(3_000, () => world.Brain.Risk.Retreat.UsedEscapes.Contains(escape)),
            $"no {escape}: {await world.OnWorldAsync(() => world.Brain.RiskReport)}");
        // With spell batching on (ARCANECORE_TEST_SPELL_BATCH_MS) the escape goes off at the next batch boundary; the bot thinks on meanwhile.
        SpellSystem spells = world.Session.Services.GetRequiredService<SpellFeature>().System;
        if (await world.OnWorldAsync(() => spells.HasPendingGo(world.Player)))
            Assert.True(await world.ThinkUntilAsync(spells.SpellBatchWindowMs + RiskTestWorld.ThinkMs, () => !spells.HasPendingGo(world.Player)),
                "the escape never went off");
        Assert.True(await world.OnWorldAsync(() => world.Session.Services.GetRequiredService<SpellFeature>().System
            .HasAura(atEnemy ? mob : world.Player, spellId)), "the server did not apply the escape");
        Assert.Contains("escapes=" + escape.Replace(' ', '_'), await world.OnWorldAsync(() => world.Brain.RiskReport));
        Assert.Single(await world.OnWorldAsync(() => world.Brain.Risk.Retreat.UsedEscapes.ToArray()));
    }

    [Theory]
    [InlineData(Class.Druid, PlayerbotEscapes.EntanglingRoots, true, 1500)]
    [InlineData(Class.Warlock, PlayerbotEscapes.Fear, true, 1500)]
    public async Task ACastTimeEscape_HoldsStillUntilTheCastFinishes_InsteadOfCancellingOnMove(
        Class playerClass, string escape, bool atEnemy, uint castTimeMs)
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        Creature mob = await world.OnWorldAsync(() => world.Spawn(RiskTestWorld.Template(991042, health: 500, minDamage: 1, maxDamage: 1), 2, 0));
        await world.SeeAsync(mob);
        uint spellId = 991_420 + (uint)playerClass;
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)playerClass);
            SpellFeature feature = world.Session.Services.GetRequiredService<SpellFeature>();
            feature.System.Store = new SpellStore([.. feature.System.Store.All, new SpellInfo
            {
                Id = spellId, Name = escape, CastTime = new SpellCastTime((int)castTimeMs, 0, (int)castTimeMs),
                Duration = new SpellDuration(10_000, 0, 10_000),
                Effects = [new SpellEffectInfo
                {
                    Effect = SpellEffectName.ApplyAura, AuraType = AuraType.ModRoot,
                    TargetA = atEnemy ? SpellImplicitTarget.UnitEnemy : SpellImplicitTarget.UnitCaster,
                }],
            }], [], []);
            Assert.True(feature.Spellbook.LearnSpell(player, spellId));
            world.Engage(mob);
            world.Brain.Risk.StartRetreat(player, [mob], "test");
            return true;
        });

        Assert.True(await world.ThinkUntilAsync(4_000, () => world.Session.Services.GetRequiredService<SpellFeature>().System
            .HasAura(atEnemy ? mob : world.Player, spellId)),
            $"cast-time escape {escape} never landed: {await world.OnWorldAsync(() => world.Brain.RiskReport)}");
    }
}
