using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Features;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// Live 2026-10-11 (deploy at 03:07 UTC): 30 recovery faults <c>playerbot-recovery-spirit-healer-failed</c> in the first minutes,
/// every one at the fallback's stuck bound (PlayerbotRecovery.cs:259 then :376), and bots disabled after three. The characters
/// database showed the cause on every affected bot (Wfbotgx 720, Wfbotam 553, Wfbotfb 672 and 18 more): <c>character_vitals.IsGhost
/// = 1</c> with no <c>character_corpse</c> row and the ghost aura 8326 saved in <c>character_aura</c>, at health 0 (dead) or at
/// half health and more (alive). A stored ghost without a body logs in ALIVE at half health (vmangos Player::LoadCorpse, "Prevent
/// Dead Player login without corpse", Player.cpp:15427-15440), but the login then restored its saved ghost aura, whose
/// SPELL_AURA_GHOST sets PLAYER_FLAGS_GHOST on the living bot. vmangos loads the auras first (Player.cpp:14971) and LoadCorpse's
/// ResurrectPlayer removes the ghost form again (RemoveGhostForm, Player.cpp:4571-4578, from ResurrectPlayer, Player.cpp:4638-4646).
/// The flagged living bot went on playing; at its next death the release was refused (a player that already is a ghost,
/// HandleRepopRequestOpcode), so it lay rooted at 0 health with the ghost flag and no body: the recovery took that for a ghost
/// without a body, went for the spirit healer, could not walk (rooted) and faulted at the stuck bound; and every quarantine relog
/// restored the same state.
/// </summary>
public sealed class PlayerbotRestoredGhostRecoveryTests
{
    /// <summary>
    /// A bot stored as a ghost without a body (the live rows) comes back alive at half health WITHOUT the ghost form, so it is an
    /// ordinary living player: when it dies again it releases (a body is left where it fell), runs back and reclaims it, and the
    /// recovery never faults. Before, it came back alive yet flagged a ghost, its release was refused, and the recovery threw
    /// <see cref="PlayerbotRecovery.SpiritHealerFailed"/>.
    /// </summary>
    [Fact]
    public async Task ABotStoredAsAGhostWithoutABody_ComesBackAliveWithoutTheGhostForm_AndRecoversFromItsNextDeath()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton<IWorldFeature, ManualClockFeature>());
        (Account owner, int character, WorldSession first) = await EnterAsync(host);
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = first.Player!;
                SpellSystem spells = Install(host);
                player.Health = 0;
                player.Map!.Combat.KillPlayer(player);
                Assert.True(player.Map!.Combat.RepopPlayer(player));
                Assert.True(spells.HasAura(player, CombatConstants.GhostSpellId));
            });
        }
        finally
        {
            first.Kick();
            await first.ManagedClosed;
        }

        // The logout save stores the ghost with its body and its ghost aura; the live rows had lost the body (IsGhost = 1 and no
        // character_corpse row, health 0 like Wfbotgx).
        await host.WaitForWorldAsync(() => host.Characters.Life(character) is { IsGhost: true, Corpse: not null }, "the ghost's logout save");
        CharacterLife saved = host.Characters.Life(character)!;
        host.Characters.SetLife(character, saved with { Health = 0, Corpse = null });

        WorldSession session = await LoginAsync(host, owner, character);
        try
        {
            var clock = new TestDeathClock(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                SpellSystem spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
                Assert.True(player.IsAlive);
                Assert.Equal(player.MaxHealth / 2, player.Health);
                Assert.False(player.Flags.HasFlag(PlayerFlags.Ghost), "a living bot flagged a ghost (the saved ghost aura came back)");
                Assert.False(spells.HasAura(player, CombatConstants.GhostSpellId));
                Assert.Equal(0, player.GetByte(UpdateFields.UnitFieldBytes1, 3) & 1);

                // It dies again, where it stands.
                DeathHooks.Register(host.World, new DeathHooks(new DeathOptions(), clock));
                player.Health = 0;
                player.Map!.Combat.KillPlayer(player);
            });

            var recovery = new PlayerbotRecovery(session, new PlayerbotOptions { Enabled = true, AllowedMaps = [0, 1] });
            bool released = false, alive = false;
            var steps = new List<string>();
            for (int think = 0; think < 200 && !alive; think++)
            {
                alive = await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    PlayerbotMovementControl.Update(session, player);
                    session.ManagedBudget = new ManagedActionBudget(4);
                    recovery.Update(player, 500); // throws the live fault before the fix
                    released |= player.Combat.Corpse is not null;
                    if (steps.Count == 0 || steps[^1] != recovery.LastStep.ToString()) steps.Add(recovery.LastStep.ToString());
                    return player.IsAlive;
                });
                if (think == 10) clock.Seconds += 125; // past any reclaim delay (30, 60 or 120 s by recent deaths)
                if (!alive) await host.World.AdvanceClockAsync(500);
            }

            Assert.True(released, "the spirit was never released: " + string.Join(" -> ", steps));
            Assert.True(alive, "still dead: " + string.Join(" -> ", steps));
            Assert.False(recovery.UsingSpiritHealer, "the bot took the spirit healer instead of its body");
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                Assert.False(player.Flags.HasFlag(PlayerFlags.Ghost));
                Assert.Null(player.Combat.Corpse);
            });
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }

    /// <summary>
    /// A ghost WITH a body keeps its ghost form across the relog (the body is put back and the bot is still a ghost); the fix only
    /// leaves out the ghost form of a player the login brought back to life.
    /// </summary>
    [Fact]
    public async Task ABotStoredAsAGhostWithItsBody_IsStillAGhostAfterTheRelog()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        (Account owner, int character, WorldSession first) = await EnterAsync(host);
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = first.Player!;
                Install(host);
                player.Health = 0;
                player.Map!.Combat.KillPlayer(player);
                Assert.True(player.Map!.Combat.RepopPlayer(player));
            });
        }
        finally
        {
            first.Kick();
            await first.ManagedClosed;
        }

        await host.WaitForWorldAsync(() => host.Characters.Life(character) is { IsGhost: true, Corpse: not null }, "the ghost's logout save");
        WorldSession session = await LoginAsync(host, owner, character);
        try
        {
            await host.WaitForWorldAsync(() => session.Player?.Combat.Corpse is not null, "the body to be put back");
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                Assert.False(player.IsAlive);
                Assert.True(player.Flags.HasFlag(PlayerFlags.Ghost));
                Assert.True(host.WorldServices.GetRequiredService<SpellFeature>().System.HasAura(player, CombatConstants.GhostSpellId));
            });
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }

    private static async Task<(Account Owner, int Character, WorldSession Session)> EnterAsync(WorldTestHost host)
    {
        Account owner = await host.Accounts.CreateAsync(new Account { Username = "BOTGHOST", Salt = new byte[32], Verifier = new byte[32] });
        WorldSession session = await CreateSessionAsync(host, owner);
        var create = new PacketWriter(); create.WriteCString("Ghostbot"); create.WriteByte(1); create.WriteByte(1);
        for (int i = 0; i < 8; i++) create.WriteByte(0);
        await session.DispatchManagedSessionAsync(WorldOpcode.CmsgCharCreate, create.ToArray());
        int character = (await session.Services.GetRequiredService<ICharacterStore>().GetByAccountAsync(owner.Id)).Single().Id;
        await DispatchLoginAsync(host, session, character);
        return (owner, character, session);
    }

    private static async Task<WorldSession> LoginAsync(WorldTestHost host, Account owner, int character)
    {
        WorldSession session = await CreateSessionAsync(host, owner);
        await DispatchLoginAsync(host, session, character);
        return session;
    }

    private static Task<WorldSession> CreateSessionAsync(WorldTestHost host, Account owner)
        => WorldSession.CreateManagedAsync(owner, null, host.WorldServices, host.Opcodes, host.World, host.Registry,
            new WorldSessionOptions(), NullLogger.Instance);

    private static async Task DispatchLoginAsync(WorldTestHost host, WorldSession session, int character)
    {
        var login = new PacketWriter(); login.WriteUInt64((ulong)character);
        await session.DispatchManagedSessionAsync(WorldOpcode.CmsgPlayerLogin, login.ToArray());
        await host.WaitForWorldAsync(() => session.Player is { IsInWorld: true }, "the bot's login");
    }

    /// <summary>The ghost spells (8326 with water walking, 20584) as synthetic spell data: the test host has no spell store.</summary>
    private static SpellSystem Install(WorldTestHost host)
    {
        SpellSystem spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
        spells.Store = new SpellStore([.. spells.Store.All, Form(CombatConstants.GhostSpellId, true), Form(CombatConstants.WispGhostSpellId, false)], [], []);
        return spells;
    }

    private static SpellInfo Form(uint id, bool waterWalk) => new()
    {
        Id = id, Name = $"Synthetic ghost form {id}", RangeIndex = SpellConstants.RangeIndexSelfOnly,
        Attributes = SpellAttributes.AllowCastWhileDead,
        AttributesEx2 = SpellAttributesEx2.AllowDeadTarget,
        AttributesEx3 = 0x00100000,
        Duration = new SpellDuration(-1, 0, -1),
        Effects = waterWalk ? [Aura(AuraType.Ghost), Aura(AuraType.WaterWalk)] : [Aura(AuraType.Ghost)],
    };

    private static SpellEffectInfo Aura(AuraType type) => new()
    {
        Effect = SpellEffectName.ApplyAura, AuraType = type, TargetA = SpellImplicitTarget.UnitCaster,
    };

    private sealed class ManualClockFeature : IWorldFeature
    {
        public void Attach(ArcaneCore.Game.Maps.WorldRuntime world) => world.UseManualClock();
    }

    private sealed class TestDeathClock(long seconds) : DeathClock
    {
        public long Seconds { get; set; } = seconds;
        public override long UnixSeconds => Seconds;
    }
}
