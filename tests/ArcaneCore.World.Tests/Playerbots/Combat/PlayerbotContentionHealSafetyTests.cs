using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using ArcaneCore.Game;
using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Net;
using ArcaneCore.World.Tests.Spells;
using Microsoft.Extensions.DependencyInjection;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Combat;

/// <summary>
/// The heal a bot casts at the player it helps (docs/areas/playbots.md, Contention): a server refusal would bar that spell from the bot's
/// own rotation for a while, so a cast the server would refuse (an enemy, a flagged player, no line of sight) is never sent.
/// </summary>
public sealed class PlayerbotContentionHealSafetyTests
{
    [Fact]
    public async Task ASameFactionTagger_InSight_IsHealed_AndEveryUnsafeOneIsRefusedBeforeAnyCast()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession bot = await CombatTestSessions.EnterAsync(host, "HEALBOT", "Healbot");
        WorldSession friend = await CombatTestSessions.EnterAsync(host, "HEALFRI", "Healfri");
        WorldSession enemy = await CombatTestSessions.EnterAsync(host, "HEALENE", "Healene", race: 2); // an orc: the other team
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player me = bot.Player!;
                Assert.True(PlayerbotBrain.IsSafeHealTarget(me, friend.Player!));

                Assert.NotEqual(me.Team, enemy.Player!.Team);
                Assert.False(PlayerbotBrain.IsSafeHealTarget(me, enemy.Player!));

                friend.Player!.UnitFlags |= UnitFlags.Pvp;
                Assert.False(PlayerbotBrain.IsSafeHealTarget(me, friend.Player!)); // flagged tagger, unflagged bot
                me.UnitFlags |= UnitFlags.Pvp;
                Assert.True(PlayerbotBrain.IsSafeHealTarget(me, friend.Player!)); // both flagged: a heal flags nobody new
                me.UnitFlags &= ~UnitFlags.Pvp;
                friend.Player!.UnitFlags &= ~UnitFlags.Pvp;
                friend.Player!.Flags |= PlayerFlags.FfaPvp;
                Assert.False(PlayerbotBrain.IsSafeHealTarget(me, friend.Player!));
                friend.Player!.Flags &= ~PlayerFlags.FfaPvp;
                Assert.True(PlayerbotBrain.IsSafeHealTarget(me, friend.Player!));

                // Both entered at the same start spot: objects within each other's bounding radius are in sight without a ray
                // (vmangos WorldObject::IsWithinLOSInMap, Object.cpp:1759-1760; MapCollision.IsWithinLineOfSight), so a wall can only
                // stand between them once they are apart.
                friend.Player!.Relocate(me.X + 10, me.Y, me.Z, me.Orientation, host.World.NowMs);
                Assert.True(PlayerbotBrain.IsSafeHealTarget(me, friend.Player!)); // apart, nothing in the way yet
                WorldCollision.Of(host.World).Install(lineOfSight: new Blocked());
                Assert.False(PlayerbotBrain.IsSafeHealTarget(me, friend.Player!)); // a wall between them
                return true;
            });
        }
        finally
        {
            enemy.Kick(); await enemy.ManagedClosed;
            friend.Kick(); await friend.ManagedClosed;
            bot.Kick(); await bot.ManagedClosed;
        }
    }

    [Fact]
    public async Task TryHealTagger_GoesThroughTheGate_HealsAFriend_AndLeavesTheBotsOwnHealUsable()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession priest = await CombatTestSessions.EnterAsync(host, "HEALPRI", "Healpri"); // a warrior made a priest below (the test content has no priest creation row)
        WorldSession friend = await CombatTestSessions.EnterAsync(host, "HEALFRI", "Healfri");
        WorldSession enemy = await CombatTestSessions.EnterAsync(host, "HEALENE", "Healene", race: 2);
        uint selfBefore = 0, friendBefore = 0;
        var brain = new PlayerbotBrain(priest, new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500, Risk = { Enabled = false } });
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player me = priest.Player!;
                me.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Priest);
                SpellFeature feature = priest.Services.GetRequiredService<SpellFeature>();
                feature.Spellbook.LearnSpell(me, SpellTestServices.Heal); // the warrior content already teaches it
                priest.ManagedBudget = new ManagedActionBudget(4);
                var anyMob = new ObjectGuid(0xF130000000000001UL);

                // A safe tagger, but the only heal known lands on its caster (Test Heal: UnitCaster, like Desperate Prayer): nothing is cast.
                friend.Player!.Health = 1; // out-of-combat regen (a few points per 2 s) cannot refill this inside the 10 s wait; only the heal below can
                brain.Contention.BeginAssist(anyMob, friend.Player.Guid);
                me.Health = Math.Max(1u, me.MaxHealth / 2);
                selfBefore = me.Health;
                friendBefore = friend.Player.Health;
                Assert.False(brain.TryHealTagger(me));

                // A heal that lands on a friend (UnitFriend) joins the book.
                SpellInfo selfOnly = feature.System.Store.Get(SpellTestServices.Heal)!;
                SpellInfo friendly = selfOnly with
                {
                    Id = 9101, Name = "Test Friend Heal",
                    Effects = [.. selfOnly.Effects.Select(e => e with { TargetA = SpellImplicitTarget.UnitFriend, BasePoints = 100_000 })], // fills the friend: regen alone cannot
                };
                feature.System.Store = new SpellStore(feature.System.Store.All.Append(friendly), [], []);
                Assert.True(feature.Spellbook.LearnSpell(me, friendly.Id));
                brain.CombatSpells.Reset();

                // Unsafe taggers: the heal that could land on them is not sent, so nothing is refused and the rotation keeps the spell.
                enemy.Player!.Health = Math.Max(1u, enemy.Player.MaxHealth / 4);
                brain.Contention.BeginAssist(anyMob, enemy.Player.Guid);
                Assert.False(brain.TryHealTagger(me));
                friend.Player.UnitFlags |= UnitFlags.Pvp;
                brain.Contention.BeginAssist(anyMob, friend.Player.Guid);
                Assert.False(brain.TryHealTagger(me));
                friend.Player.UnitFlags &= ~UnitFlags.Pvp;
                friend.Player.Flags |= PlayerFlags.FfaPvp;
                Assert.False(brain.TryHealTagger(me));
                friend.Player.Flags &= ~PlayerFlags.FfaPvp;

                Assert.True(brain.CombatSpells.CanCastNow(me, friendly, friend.Player)); // the bot's own heal was not barred by a refusal
                Assert.True(brain.CombatSpells.CanCastNow(me, selfOnly, me));

                // A same-faction tagger in sight and in range is healed.
                Assert.True(brain.TryHealTagger(me));
                return true;
            });
            await host.WaitForWorldAsync(() => friend.Player!.Health == friend.Player.MaxHealth, "the friend is healed to full");
            Assert.InRange(await host.OnWorldAsync(() => priest.Player!.Health), selfBefore, selfBefore + 19); // the self-only heal (20) was never cast; at most a regen tick landed
        }
        finally
        {
            brain.Stop();
            enemy.Kick(); await enemy.ManagedClosed;
            friend.Kick(); await friend.ManagedClosed;
            priest.Kick(); await priest.ManagedClosed;
        }
    }

    private sealed class Blocked : ILineOfSight
    {
        public bool Enabled => true;
        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => false;
        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        { hit = to; return true; }
        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => null;
        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        { info = default; return false; }
    }
}
