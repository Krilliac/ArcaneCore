using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Loot;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Combat;

/// <summary>
/// Idle-grind etiquette of <see cref="PlayerbotBrain.FindTarget"/> (vmangos CombatBotBaseAI::IsValidHostileTarget and the
/// client's grey-name rule): a creature another player tapped (Creature.LootTapPlayerGuid, LootService.OnCreatureDamaged) or is
/// fighting is not taken from them unless that player is in the bot's group, and a creature at or below the experience grey
/// level (ExperienceFormulas.GrayLevel) is not ground at all.
/// </summary>
public sealed class PlayerbotTargetEtiquetteTests
{
    [Fact]
    public async Task ACreatureTappedByAStranger_IsNotChosen_ButAGroupMatesTapIs()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession bot = await CombatTestSessions.EnterAsync(host, "ETIQBOT", "Etiqbot");
        WorldSession stranger = await CombatTestSessions.EnterAsync(host, "ETIQOTHER", "Etiqother");
        try
        {
            Creature tapped = null!, free = null!;
            await host.OnWorldAsync(() =>
            {
                Player player = bot.Player!;
                tapped = AddCreature(host, player, 994001, 3f, level: 1);
                free = AddCreature(host, player, 994002, 12f, level: 1);
                return true;
            });
            await host.WaitForWorldAsync(() => bot.Player!.VisibleObjects.Contains(tapped.Guid)
                && bot.Player.VisibleObjects.Contains(free.Guid), "etiquette candidates");
            await host.OnWorldAsync(() =>
            {
                Player player = bot.Player!;
                Assert.Same(tapped, PlayerbotBrain.FindTarget(player)); // nearest while nobody tapped it

                player.Map!.Combat.DealDamage(stranger.Player!, tapped, 1);
                uint seen = tapped.GetValueFor(UpdateFields.UnitDynamicFlags, player);
                Assert.NotEqual(0u, seen & LootService.UnitDynFlagTapped);
                Assert.Equal(0u, seen & LootService.UnitDynFlagTappedByPlayer); // grey to the bot: someone else's
                Assert.Same(free, PlayerbotBrain.FindTarget(player));
                // An explicit quest objective keeps the same etiquette: the kill would not count for the bot anyway.
                Assert.Same(free, PlayerbotBrain.FindTarget(player, 0));
                Assert.Null(PlayerbotBrain.FindTarget(player, tapped.Entry));
                return true;
            });

            // Group up: the stranger's tap now belongs to the bot's group too (LootService viewer filter), so the bot may help.
            await host.OnWorldAsync(() =>
            {
                Assert.True(TryAction(stranger, WorldOpcode.CmsgGroupInvite, CString("Etiqbot")));
                Assert.True(TryAction(bot, WorldOpcode.CmsgGroupAccept, []));
                return true;
            });
            await host.WaitForWorldAsync(() => host.WorldServices.GetRequiredService<ArcaneCore.World.Social.SocialFeature>().Context.Groups
                .AreInSameGroup(bot.Player!.Guid, stranger.Player!.Guid), "the bot joins the stranger's group");
            // A group formed after the tap does not share it (vmangos records the group at the first hit), so the old tap still
            // excludes its creature; a fresh creature the group mate hits is the bot's to help with.
            Creature helped = await host.OnWorldAsync(() => AddCreature(host, bot.Player!, 994003, 2f, level: 1));
            await host.WaitForWorldAsync(() => bot.Player!.VisibleObjects.Contains(helped.Guid), "helped creature");
            await host.OnWorldAsync(() =>
            {
                Player player = bot.Player!;
                player.Map!.Combat.DealDamage(stranger.Player!, helped, 1);
                Assert.NotEqual(0u, helped.GetValueFor(UpdateFields.UnitDynamicFlags, player) & LootService.UnitDynFlagTappedByPlayer);
                Assert.Same(helped, PlayerbotBrain.FindTarget(player));
                return true;
            });
        }
        finally
        {
            stranger.Kick(); await stranger.ManagedClosed;
            bot.Kick(); await bot.ManagedClosed;
        }
    }

    [Fact]
    public async Task ACreatureFightingAStranger_IsNotChosen_EvenBeforeAnyDamage()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession bot = await CombatTestSessions.EnterAsync(host, "ETIQBOT", "Etiqbot");
        WorldSession stranger = await CombatTestSessions.EnterAsync(host, "ETIQOTHER", "Etiqother");
        try
        {
            Creature engaged = null!, free = null!;
            await host.OnWorldAsync(() =>
            {
                Player player = bot.Player!;
                engaged = AddCreature(host, player, 994011, 3f, level: 1);
                free = AddCreature(host, player, 994012, 12f, level: 1);
                return true;
            });
            await host.WaitForWorldAsync(() => bot.Player!.VisibleObjects.Contains(engaged.Guid)
                && bot.Player.VisibleObjects.Contains(free.Guid), "etiquette candidates");
            await host.OnWorldAsync(() =>
            {
                Player player = bot.Player!;
                Assert.True(player.Map!.Combat.Attack(engaged, stranger.Player!)); // the creature attacks the stranger, nobody hit it yet
                Assert.Equal(0u, engaged.GetValueFor(UpdateFields.UnitDynamicFlags, player) & LootService.UnitDynFlagTapped);
                Assert.Same(free, PlayerbotBrain.FindTarget(player));
                return true;
            });
        }
        finally
        {
            stranger.Kick(); await stranger.ManagedClosed;
            bot.Kick(); await bot.ManagedClosed;
        }
    }

    [Fact]
    public async Task GreyCreatures_AreNotGround_ButTheLevelBoundFollowsTheExperienceFormula()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession bot = await CombatTestSessions.EnterAsync(host, "ETIQBOT", "Etiqbot");
        try
        {
            Creature grey = null!, green = null!;
            await host.OnWorldAsync(() =>
            {
                Player player = bot.Player!;
                player.Level = 20;                                    // grey level 20 - 5 - 2 = 13
                grey = AddCreature(host, player, 994021, 3f, level: 13);
                green = AddCreature(host, player, 994022, 12f, level: 14);
                return true;
            });
            await host.WaitForWorldAsync(() => bot.Player!.VisibleObjects.Contains(grey.Guid)
                && bot.Player.VisibleObjects.Contains(green.Guid), "level candidates");
            await host.OnWorldAsync(() =>
            {
                Player player = bot.Player!;
                Assert.Equal(13u, Game.Progression.ExperienceFormulas.GrayLevel(player.Level));
                Assert.Same(green, PlayerbotBrain.FindTarget(player));
                // A named quest objective may still be grey (the quest asks for it).
                Assert.Same(grey, PlayerbotBrain.FindTarget(player, grey.Entry));
                return true;
            });
        }
        finally
        {
            bot.Kick(); await bot.ManagedClosed;
        }
    }

    private static Creature AddCreature(WorldTestHost host, Player player, uint low, float offset, byte level)
    {
        var creature = new Creature(low, new CreatureTemplate
        {
            Entry = low, Name = "etiquette candidate", CreatureType = 1,
            MinLevel = level, MaxLevel = level, MinLevelHealth = 50, MaxLevelHealth = 50,
        }, null, CreatureContent.Empty, new Random((int)low));
        creature.Relocate(player.X + offset, player.Y, player.Z, 0, host.World.NowMs);
        player.Map!.AddObject(creature);
        return creature;
    }

    private static bool TryAction(WorldSession session, WorldOpcode opcode, byte[] payload)
    {
        session.ManagedBudget = null;
        return session.TryManagedAction(opcode, payload);
    }

    private static byte[] CString(string value)
    {
        var writer = new PacketWriter(value.Length + 1);
        writer.WriteCString(value);
        return writer.ToArray();
    }
}

/// <summary>Managed sessions of the combat tests: a named human warrior on its own account (WorldTestHost content).</summary>
internal static class CombatTestSessions
{
    internal static async Task<WorldSession> EnterAsync(WorldTestHost host, string account, string name, byte race = 1, byte cls = 1)
    {
        Account owner = await host.Accounts.CreateAsync(new Account { Username = account, Salt = new byte[32], Verifier = new byte[32] });
        WorldSession session = await WorldSession.CreateManagedAsync(owner, null, host.WorldServices, host.Opcodes,
            host.World, host.Registry, new WorldSessionOptions(), NullLogger.Instance);
        var create = new PacketWriter();
        create.WriteCString(name);
        create.WriteByte(race);
        create.WriteByte(cls);
        for (int i = 0; i < 8; i++) create.WriteByte(0);
        await session.DispatchManagedSessionAsync(WorldOpcode.CmsgCharCreate, create.ToArray());
        CharacterRecord character = (await session.Services.GetRequiredService<ICharacterStore>().GetByAccountAsync(owner.Id)).Single();
        var login = new PacketWriter();
        login.WriteUInt64((ulong)character.Id);
        await session.DispatchManagedSessionAsync(WorldOpcode.CmsgPlayerLogin, login.ToArray());
        await host.WaitForWorldAsync(() => session.Player is { IsInWorld: true }, $"{name} login");
        return session;
    }
}
