using ArcaneCore.Data.Characters.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Spells.SpellTestServices;

namespace ArcaneCore.World.Tests.Spells;

/// <summary>
/// Death removes auras: a lethal <see cref="MapCombat.Kill"/> in a real world strips the victim's
/// non-passive auras through <see cref="SpellFeature"/>, so nothing harmful survives into the
/// logout save (vmangos Unit::RemoveAllAurasOnDeath; vmangos reference clones were not available
/// to this change, see docs/areas/spells.md).
/// </summary>
public sealed class SpellDeathWorldTests
{
    [Fact]
    public async Task LethalKill_RemovesAurasStunAndRoot_AndTheLogoutSaveKeepsOnlyCooldowns()
    {
        await using var host = WorldTestHost.Start();
        byte[] key = await host.AddAccountAsync("DEATHAURA");
        await using (WorldTestClient create = await host.ConnectAsync())
        {
            await create.AuthenticateAsync("DEATHAURA", key);
            await create.CreateCharacterAsync("Deathaura");
        }

        Account stored = (await host.Accounts.FindByUsernameAsync("DEATHAURA"))!;
        CharacterRecord character = (await host.Characters.GetByAccountAsync(stored.Id)).Single();
        SpellFeature spells;
        InMemoryCharacterSpellStateStore store;
        await using (WorldTestClient first = await host.ConnectAsync())
        {
            await first.AuthenticateAsync("DEATHAURA", key);
            await first.LoginAsync((ulong)character.Id);
            Player player = await host.PlayerAsync("Deathaura");
            spells = ((WorldSession)player.Session).Services.GetRequiredService<SpellFeature>();
            store = (InMemoryCharacterSpellStateStore)((WorldSession)player.Session).Services.GetRequiredService<ICharacterSpellStateStore>();

            (int before, bool rootedBefore, bool stunnedBefore) = await host.OnWorldAsync(() =>
            {
                foreach (uint spell in new[] { Renew, DeathDot, DeathStun, DeathRoot, Cooldown })
                {
                    Assert.Equal(SpellCastResult.CastOk, spells.System.CastSpell(player, spell, SpellCastTargets.ForSelf(), triggered: true));
                }

                return (spells.System.GetAuras(player).Count(h => !h.Spell.IsPassive), player.IsRooted, (player.UnitFlags & UnitFlags.Stunned) != 0);
            });
            Assert.Equal(4, before);
            Assert.True(rootedBefore);
            Assert.True(stunnedBefore);

            (int after, int captured, bool rootedAfter, bool stunnedAfter, int cooldowns) = await host.OnWorldAsync(() =>
            {
                player.Map!.FindUpdater<MapCombat>()!.Kill(null, player);
                SpellStateSnapshot snapshot = spells.System.CaptureState(player, spells.UnixNowMs);
                return (spells.System.GetAuras(player).Count(h => !h.Spell.IsPassive), snapshot.Auras.Count,
                    player.IsRooted, (player.UnitFlags & UnitFlags.Stunned) != 0, snapshot.Cooldowns.Count);
            });

            Assert.Equal(0, after);
            Assert.Equal(0, captured);
            Assert.True(rootedAfter); // MapCombat roots the corpse on JUST_DIED; losing the root aura must not undo that
            Assert.False(stunnedAfter);
            Assert.Equal(1, cooldowns);
        }

        await host.WaitForWorldAsync(() => host.World.OnlinePlayerCount == 0, "the dead player leaves the world");
        await spells.State.FlushAsync();
        CharacterSpellState saved = await store.LoadAsync(character.Id);
        Assert.Empty(saved.Auras);
        Assert.Equal(Cooldown, Assert.Single(saved.Cooldowns).Id);
    }
}
