using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// A bot attacked by a creature standing up a slope from it. In the 2026-10-08 live replays Dawnrover (paladin 6) stood 60 s and more
/// beside a Kobold Laborer on the Elwynn hillside (2 yards apart, 3.9 yards of height: 4.4 yards in 3D) without swinging once - its
/// risk line read <c>ttk=860s reason=behind</c>, no damage dealt - while the kobold hit it, then retreated and once died. The server
/// lets both swing there (MapCombat.CanReachWithMeleeAutoAttack: 2D distance within the combat reach, less than 6 yards of height), but
/// the bot judged melee by the 3D distance against its 4-yard melee range, kept "closing in" on a target it already stood at, and never
/// sent CMSG_ATTACKSWING.
/// </summary>
public sealed class PlayerbotSlopeMeleeTests
{
    [Fact]
    public async Task ACreatureUpTheSlope_WithinTheServersReach_IsSwungAt()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        Creature kobold = null!;
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500, Risk = { Enabled = false } });
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                kobold = new Creature(993901, new CreatureTemplate
                {
                    Entry = 993901, Name = "kobold up the slope", CreatureType = 7,
                    MinLevel = 1, MaxLevel = 1, MinLevelHealth = 20, MaxLevelHealth = 20,
                }, null, CreatureContent.Empty, new Random(993901));
                kobold.Relocate(player.X + 2f, player.Y, player.Z + 3.9f, MathF.PI, host.World.NowMs);
                player.Map!.AddObject(kobold);
                return true;
            });
            await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(kobold.Guid), "kobold visibility");
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                Assert.True(player.Map!.Combat.Attack(kobold, player));
                player.Map!.Combat.DealDamage(kobold, player, 1);
                // The replay's geometry: the server lets them swing at each other, the 3D distance is beyond the bot's melee range.
                Assert.True(MapCombat.CanReachWithMeleeAutoAttack(player, kobold));
                Assert.True(MapCombat.CanReachWithMeleeAutoAttack(kobold, player));
                Assert.True(MathF.Sqrt(4f + (3.9f * 3.9f)) > ArcaneCore.World.Playerbots.Combat.PlayerbotClassRotation.MeleeRange);

                session.Services.GetRequiredService<SpellFeature>().System.Store = new SpellStore([], [], []);
                session.ManagedBudget = new ManagedActionBudget(1);
                brain.Update(500);

                Assert.Same(kobold, brain.InspectionTarget);
                Assert.Same(kobold, player.Combat.Victim);
                Assert.Equal(PlayerbotGoalKind.Combat, brain.Goal);
                return true;
            });
        }
        finally
        {
            brain.Stop();
            session.Kick();
            await session.ManagedClosed;
        }
    }
}
