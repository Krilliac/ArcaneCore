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
        Assert.True(await world.OnWorldAsync(() => world.Session.Services.GetRequiredService<SpellFeature>().System
            .HasAura(atEnemy ? mob : world.Player, spellId)), "the server did not apply the escape");
        Assert.Contains("escapes=" + escape.Replace(' ', '_'), await world.OnWorldAsync(() => world.Brain.RiskReport));
        Assert.Single(await world.OnWorldAsync(() => world.Brain.Risk.Retreat.UsedEscapes.ToArray()));
    }
}
