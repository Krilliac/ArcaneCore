using System.Buffers.Binary;
using System.Collections.Concurrent;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests;

/// <summary>
/// <see cref="ICharacterHooks"/>: features take part in character creation, the character list
/// and loading (docs/integration/seams.md). The probe only reacts to names starting "Hook".
/// </summary>
public sealed class CharacterHooksTests
{
    /// <summary>SMSG_CHAR_ENUM offset of the first equipment slot for a character named <paramref name="name"/>.</summary>
    private static int FirstSlotOffset(string name)
        => 1 + 8 + name.Length + 1 + 8 + 1 + 4 + 4 + 12 + 4 + 4 + 1 + 12; // count, guid, name, looks, level, zone, map, xyz, guild, flags, first login, pet

    [Fact]
    public async Task Hooks_RunOnCreate_FillTheCharacterList_AndLoadThePlayerBeforeItEntersTheWorld()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        byte[] key = await host.AddAccountAsync("HOOKS1");
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("HOOKS1", key);
        await client.CreateCharacterAsync("Hookalpha");
        Assert.Contains("Hookalpha", HookProbe.Created);

        await client.SendAsync(WorldOpcode.CmsgCharEnum, []);
        byte[] list = await client.ReadUntilAsync(WorldOpcode.SmsgCharEnum);
        int slot = FirstSlotOffset("Hookalpha");
        Assert.Equal(HookProbe.DisplayId, BinaryPrimitives.ReadUInt32LittleEndian(list.AsSpan(slot)));
        Assert.Equal(HookProbe.InventoryType, list[slot + 4]);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(list.AsSpan(slot + 5))); // second slot empty

        Account account = (await host.Accounts.FindByUsernameAsync("HOOKS1"))!;
        CharacterRecord record = (await host.Characters.GetByAccountAsync(account.Id)).Single();
        await client.LoginAsync((ulong)record.Id);
        Assert.Contains(("Hookalpha", false), HookProbe.Loaded); // loaded while not yet online
    }

    [Fact]
    public async Task AFailingLoadHook_FailsTheLogin_AndTheCharacterNeverEntersTheWorld()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        byte[] key = await host.AddAccountAsync("HOOKS2");
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("HOOKS2", key);
        await client.CreateCharacterAsync("Hookfail");
        Account account = (await host.Accounts.FindByUsernameAsync("HOOKS2"))!;
        CharacterRecord record = (await host.Characters.GetByAccountAsync(account.Id)).Single();

        var login = new PacketWriter(8);
        login.WriteUInt64((ulong)record.Id);
        await client.SendAsync(WorldOpcode.CmsgPlayerLogin, login.ToArray());
        byte[] failed = await client.ReadUntilAsync(WorldOpcode.SmsgCharacterLoginFailed);
        Assert.Equal((byte)CharResult.CharLoginFailed, failed[0]);
        Assert.Equal(0, host.World.OnlinePlayerCount);

        // The session is still usable on the character screen.
        await client.SendAsync(WorldOpcode.CmsgCharEnum, []);
        Assert.Equal(1, (await client.ReadUntilAsync(WorldOpcode.SmsgCharEnum))[0]);
    }

    [Fact]
    public async Task AFailingCreateHook_ReportsCreateError()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        byte[] key = await host.AddAccountAsync("HOOKS3");
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("HOOKS3", key);
        Assert.Equal((byte)CharResult.CharCreateError, await client.TryCreateCharacterAsync("Hookbroken"));
    }

    /// <summary>Registers the probe in every test host.</summary>
    private sealed class HookProbeServices : IWorldTestServices
    {
        public void Register(IServiceCollection services)
        {
            services.AddSingleton<HookProbe>();
            services.AddSingleton<ICharacterHooks>(sp => sp.GetRequiredService<HookProbe>());
        }
    }

    private sealed class HookProbe : ICharacterHooks
    {
        public const uint DisplayId = 4321;
        public const byte InventoryType = 5; // INVTYPE_CHEST

        public static ConcurrentQueue<string> Created { get; } = new();

        public static ConcurrentQueue<(string Name, bool Online)> Loaded { get; } = new();

        public Task OnCharacterCreatedAsync(WorldSession session, CharacterRecord character)
        {
            if (character.Name == "Hookbroken")
            {
                throw new InvalidOperationException("feature bug");
            }

            if (character.Name.StartsWith("Hook", StringComparison.Ordinal))
            {
                Created.Enqueue(character.Name);
            }

            return Task.CompletedTask;
        }

        public Task OnPlayerLoadingAsync(WorldSession session, CharacterRecord character, Player player)
        {
            if (character.Name == "Hookfail")
            {
                throw new InvalidOperationException("feature bug");
            }

            if (character.Name.StartsWith("Hook", StringComparison.Ordinal))
            {
                Loaded.Enqueue((player.Name, session.World.IsOnline(player.Guid)));
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<int, CharEnumItem[]>?> GetCharEnumEquipmentAsync(WorldSession session, IReadOnlyList<CharacterRecord> characters)
        {
            Dictionary<int, CharEnumItem[]> equipment = characters
                .Where(c => c.Name.StartsWith("Hook", StringComparison.Ordinal))
                .ToDictionary(c => c.Id, _ => new[] { new CharEnumItem(DisplayId, InventoryType) });
            return Task.FromResult<IReadOnlyDictionary<int, CharEnumItem[]>?>(equipment.Count == 0 ? null : equipment);
        }
    }
}
