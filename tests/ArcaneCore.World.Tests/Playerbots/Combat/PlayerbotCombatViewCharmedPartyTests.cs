using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Updates;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Playerbots.Groups;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Combat;

/// <summary>
/// The combat view's party list leaves out a charmed member: a charmed player is not assistable, so a heal or buff aimed at it would
/// be refused and put that spell into <see cref="PlayerbotCombatSpells.RefusalBackoffMs"/>. The member returns when the charm ends.
/// </summary>
public sealed class PlayerbotCombatViewCharmedPartyTests
{
    [Fact]
    public async Task ACharmedPartyMember_IsNotInThePartyView_UntilTheCharmEnds()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([]);
        var healer = await world.AddBotAsync("Charmhealer", 1, 5, 10, []);
        var friend = await world.AddBotAsync("Charmfriend", 1, 1, 10, []);
        await world.RunAsync(2_000);

        await world.OnWorldAsync(() =>
        {
            Player h = world.Player(healer.Id), f = world.Player(friend.Id);
            world.GroupManager.Invite(h, f.Name);
            world.GroupManager.Accept(f);
            return true;
        });

        await world.OnWorldAsync(() =>
        {
            Player h = world.Player(healer.Id), f = world.Player(friend.Id);
            WorldSession session = world.Session(healer.Id);
            SpellFeature feature = session.Services.GetRequiredService<SpellFeature>();

            RotationState before = PlayerbotCombatView.Build(session, feature, h, null, PlayerbotAbilities.Empty, PlayerbotRole.Healer, static _ => false);
            Assert.Contains(before.Party, u => u.Guid == f.Guid);

            f.SetUInt64(UpdateFields.UnitFieldCharmedby, h.Guid.Value);
            RotationState charmed = PlayerbotCombatView.Build(session, feature, h, null, PlayerbotAbilities.Empty, PlayerbotRole.Healer, static _ => false);
            Assert.DoesNotContain(charmed.Party, u => u.Guid == f.Guid);

            f.SetUInt64(UpdateFields.UnitFieldCharmedby, 0);
            RotationState released = PlayerbotCombatView.Build(session, feature, h, null, PlayerbotAbilities.Empty, PlayerbotRole.Healer, static _ => false);
            Assert.Contains(released.Party, u => u.Guid == f.Guid);
            return true;
        });
    }
}
