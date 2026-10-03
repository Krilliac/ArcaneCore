using System.Buffers.Binary;
using ArcaneCore.Data.Characters.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Spells.SpellTestServices;

namespace ArcaneCore.World.Tests.Spells;

/// <summary>
/// Cooldowns and auras across a real logout and login (SpellFeature + SpellStatePersistence over
/// the in-memory <see cref="ICharacterSpellStateStore"/>), and melee damage reaching casts.
/// </summary>
public sealed class SpellPersistenceWorldTests
{
    [Fact]
    public async Task Relog_RestoresCooldownsInInitialSpells_AndSelfAurasWithOwnership()
    {
        await using var host = WorldTestHost.Start();
        (byte[] key, CharacterRecord character) = await CreateAsync(host, "PERSIST", "Persist");
        SpellFeature spells;
        InMemoryCharacterSpellStateStore store;
        await using (WorldTestClient first = await LoginAsync(host, "PERSIST", key, character))
        {
            Player player = await host.PlayerAsync("Persist");
            spells = Feature(player);
            store = Store(player);
            await host.OnWorldAsync(() =>
            {
                Assert.Equal(SpellCastResult.CastOk, spells.System.CastSpell(player, Renew, SpellCastTargets.ForSelf(), triggered: true));
                Assert.Equal(SpellCastResult.CastOk, spells.System.CastSpell(player, Cooldown, SpellCastTargets.ForSelf(), triggered: true));
            });
        }

        await host.WaitForWorldAsync(() => host.World.OnlinePlayerCount == 0, "the first session leaves the world");
        await spells.State.FlushAsync();
        CharacterSpellState saved = await store.LoadAsync(character.Id);
        Assert.Equal(Cooldown, Assert.Single(saved.Cooldowns).Id);
        CharacterAuraRow aura = Assert.Single(saved.Auras);
        Assert.Equal(Renew, aura.Spell);
        Assert.Equal((ulong)character.Id, aura.CasterGuid & 0xFFFFFFFF);

        await using WorldTestClient second = await LoginAsync(host, "PERSIST", key, character);
        Assert.Equal([Cooldown], ParseInitialCooldowns(second.LoginPacket(WorldOpcode.SmsgInitialSpells)));
        Player again = await host.PlayerAsync("Persist");
        (uint auraSpell, int duration, bool owned, bool ready) = await host.OnWorldAsync(() =>
        {
            SpellAuraHolder holder = Assert.Single(spells.System.GetAuras(again), h => !h.Spell.IsPassive);
            return (holder.Spell.Id, holder.Duration, SpellSystem.HasLiveCasterOwnership(holder),
                spells.System.IsSpellReady(again, spells.System.Store.Get(Cooldown)!));
        });
        Assert.Equal(Renew, auraSpell);
        Assert.InRange(duration, 1, 15_000);
        Assert.True(owned);
        Assert.False(ready);
    }

    [Fact]
    public async Task RestoredForeignAura_StaysOrphaned_WhileItsOriginalCasterIsOnline()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient casterClient = await host.EnterWorldAsync("CASTERONLINE", "Casteronline");
        Player caster = await host.PlayerAsync("Casteronline");
        (byte[] key, CharacterRecord character) = await CreateAsync(host, "ORPHAN", "Orphan");
        long now = Feature(caster).UnixNowMs;
        await Store(caster).SaveAsync(character.Id, new CharacterSpellState([],
        [
            new CharacterAuraRow
            {
                Spell = Renew, CasterGuid = caster.Guid.Value, CasterLevel = 1, StackCount = 1,
                MaxDurationMs = 15_000, RemainingMs = 10_000, EffectMask = 1, Amount0 = 3, SavedAtUnixMs = now,
            },
        ]));

        await using WorldTestClient client = await LoginAsync(host, "ORPHAN", key, character);
        Player player = await host.PlayerAsync("Orphan");
        SpellFeature spells = Feature(player);
        (ObjectGuid casterGuid, bool owned, bool actorIsTarget) = await host.OnWorldAsync(() =>
        {
            SpellAuraHolder holder = Assert.Single(spells.System.GetAuras(player), h => h.Spell.Id == Renew);
            return (holder.CasterGuid, SpellSystem.HasLiveCasterOwnership(holder), ReferenceEquals(spells.System.ResolveAuraActor(holder), player));
        });

        Assert.Equal(caster.Guid, casterGuid);
        Assert.False(owned);
        Assert.True(actorIsTarget);
    }

    [Fact]
    public async Task FailedLogoutSave_IsKeptInMemory_AndRestoredOnTheNextLogin()
    {
        await using var host = WorldTestHost.Start();
        (byte[] key, CharacterRecord character) = await CreateAsync(host, "SAVEFAIL", "Savefail");
        InMemoryCharacterSpellStateStore store;
        SpellFeature spells;
        await using (WorldTestClient first = await LoginAsync(host, "SAVEFAIL", key, character))
        {
            Player player = await host.PlayerAsync("Savefail");
            spells = Feature(player);
            store = Store(player);
            await host.OnWorldAsync(() => spells.System.CastSpell(player, Cooldown, SpellCastTargets.ForSelf(), triggered: true));
            store.FailSave = true;
        }

        await host.WaitForWorldAsync(() => host.World.OnlinePlayerCount == 0, "the failing session leaves the world");
        await spells.State.FlushAsync();
        Assert.True(spells.State.HasUnsaved(character.Id));
        Assert.Empty((await store.LoadAsync(character.Id)).Cooldowns);

        await using (WorldTestClient second = await LoginAsync(host, "SAVEFAIL", key, character))
        {
            Assert.Equal([Cooldown], ParseInitialCooldowns(second.LoginPacket(WorldOpcode.SmsgInitialSpells)));
            store.FailSave = false;
        }

        await host.WaitForWorldAsync(() => host.World.OnlinePlayerCount == 0, "the recovering session leaves the world");
        await spells.State.FlushAsync();
        Assert.False(spells.State.HasUnsaved(character.Id));
        Assert.Single((await store.LoadAsync(character.Id)).Cooldowns);
    }

    [Fact]
    public async Task MeleeDamage_PushesBackACastInProgress()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient attackerClient = await host.EnterWorldAsync("MELEE", "Melee");
        await using WorldTestClient casterClient = await host.EnterWorldAsync("PUSHED", "Pushed");
        Player attacker = await host.PlayerAsync("Melee");
        Player caster = await host.PlayerAsync("Pushed");
        SpellFeature spells = Feature(caster);

        await host.OnWorldAsync(() => Assert.Equal(SpellCastResult.CastOk,
            spells.System.CastSpell(caster, SlowBolt, SpellCastTargets.ForUnit(attacker.Guid), triggered: false)));
        await Task.Delay(1000);
        (int before, int after, int pushbacks) = await host.OnWorldAsync(() =>
        {
            SpellCast cast = spells.System.GetState(caster.Guid)!.CurrentCast!;
            int timer = cast.Timer;
            caster.Map!.FindUpdater<MapCombat>()!.DealDamage(attacker, caster, 1);
            return (timer, cast.Timer, cast.PushbackCount);
        });

        Assert.InRange(before, 1, 2500);
        Assert.Equal(before + 500, after);
        Assert.Equal(1, pushbacks);
    }

    [Fact]
    public async Task DeletedCharacter_LosesItsSavedAndUnsavedCooldownsAndAuras()
    {
        await using var host = WorldTestHost.Start();
        (byte[] key, CharacterRecord character) = await CreateAsync(host, "SPELLDEL", "Spelldel");
        SpellFeature spells;
        InMemoryCharacterSpellStateStore store;
        await using (WorldTestClient first = await LoginAsync(host, "SPELLDEL", key, character))
        {
            Player player = await host.PlayerAsync("Spelldel");
            spells = Feature(player);
            store = Store(player);
            await host.OnWorldAsync(() => spells.System.CastSpell(player, Cooldown, SpellCastTargets.ForSelf(), triggered: true));
            await store.SaveAsync(character.Id, new CharacterSpellState(
                [new CharacterSpellCooldownRow { Kind = 0, Id = Renew, EndsAtUnixMs = long.MaxValue }], []));
            store.FailSave = true;
        }

        // The logout save fails: rows from an earlier save plus an unsaved snapshot in memory.
        await host.WaitForWorldAsync(() => host.World.OnlinePlayerCount == 0, "the session leaves the world");
        await spells.State.FlushAsync();
        Assert.True(spells.State.HasUnsaved(character.Id));
        Assert.Single((await store.LoadAsync(character.Id)).Cooldowns);
        store.FailSave = false;

        await using (WorldTestClient again = await host.ConnectAsync())
        {
            await again.AuthenticateAsync("SPELLDEL", key);
            byte[] guid = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(guid, (ulong)character.Id);
            await again.SendAsync(WorldOpcode.CmsgCharDelete, guid);
            Assert.Equal((byte)CharResult.CharDeleteSuccess, (await again.ReadUntilAsync(WorldOpcode.SmsgCharDelete))[0]);
        }

        // Neither the snapshot nor the rows survive for a later character with a reused id.
        await spells.State.FlushAsync();
        Assert.False(spells.State.HasUnsaved(character.Id));
        CharacterSpellState left = await store.LoadAsync(character.Id);
        Assert.Empty(left.Cooldowns);
        Assert.Empty(left.Auras);
        Assert.Equal(SpellStateSnapshot.Empty.Cooldowns, (await spells.State.LoadAsync(character.Id, store)).Cooldowns);
    }

    private static async Task<(byte[] Key, CharacterRecord Character)> CreateAsync(WorldTestHost host, string account, string name)
    {
        byte[] key = await host.AddAccountAsync(account);
        await using (WorldTestClient client = await host.ConnectAsync())
        {
            await client.AuthenticateAsync(account, key);
            await client.CreateCharacterAsync(name);
        }

        Account stored = (await host.Accounts.FindByUsernameAsync(account))!;
        return (key, (await host.Characters.GetByAccountAsync(stored.Id)).Single());
    }

    private static async Task<WorldTestClient> LoginAsync(WorldTestHost host, string account, byte[] key, CharacterRecord character)
    {
        WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync(account, key);
        await client.LoginAsync((ulong)character.Id);
        return client;
    }

    private static SpellFeature Feature(Player player)
        => ((WorldSession)player.Session).Services.GetRequiredService<SpellFeature>();

    private static InMemoryCharacterSpellStateStore Store(Player player)
        => (InMemoryCharacterSpellStateStore)((WorldSession)player.Session).Services.GetRequiredService<ICharacterSpellStateStore>();

    /// <summary>The spell ids of the cooldown block of SMSG_INITIAL_SPELLS (u16 spell, u16 item, u16 category, u32, u32 each).</summary>
    private static List<uint> ParseInitialCooldowns(byte[] packet)
    {
        int count = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(1));
        int offset = 3 + (count * 4);
        int cooldowns = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(offset));
        return [.. Enumerable.Range(0, cooldowns).Select(i => (uint)BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(offset + 2 + (i * 14))))];
    }
}
