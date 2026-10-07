using ArcaneCore.Kernel.WorldData.Names;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Game.Reload;
using Microsoft.Extensions.Options;
using ArcaneCore.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Names;

public sealed class ReservedNameWorldTests
{
    [Fact]
    public async Task CharacterCreate_UsesExactReservedStoreWithoutPersistingRejectedRow()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
            services.AddSingleton<IReservedNameStore>(new FakeStore(["reserved"])),
            configure: options => options.CharactersPerRealm = 10);
        byte[] key = await host.AddAccountAsync("RESERVED");
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("RESERVED", key);

        Assert.Equal((byte)CharResult.CharNameReserved, await client.TryCreateCharacterAsync("reserved"));
        Assert.Empty(await host.Characters.GetByAccountAsync((await host.Accounts.FindByUsernameAsync("RESERVED"))!.Id));
        Assert.Equal((byte)CharResult.CharCreateSuccess, await client.TryCreateCharacterAsync("available"));
    }

    [Fact]
    public async Task StaffMayUseSqlReservedNameButStillUsesCatalogPolicy()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
            services.AddSingleton<IReservedNameStore>(new FakeStore(["reserved"])));
        byte[] key = await host.AddAccountAsync("STAFF_RESERVED", AccountSecurity.Administrator);
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("STAFF_RESERVED", key);

        Assert.Equal((byte)CharResult.CharCreateSuccess, await client.TryCreateCharacterAsync("reserved"));
    }

    [Fact]
    public async Task SocketReloadChangesCharacterPolicyAfterCompletion()
    {
        var store = new MutableStore(["oldname"]);
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<IReservedNameStore>(store);
            services.AddSingleton<IOptions<HotReloadOptions>>(Options.Create(new HotReloadOptions { Commands = true }));
        });
        await using WorldTestClient client = await host.EnterWorldAsync("RELOADNAMES", "Reloadseed", AccountSecurity.Administrator);
        store.Names = new HashSet<string>(["newname"], StringComparer.Ordinal);

        await client.SendChatAsync(ChatType.Say, Language.Common, ".reload reserved_name");
        Assert.Equal("Re-loading reserved_name...", (await client.ReadChatAsync()).Text);
        Assert.StartsWith("reserved_name reloaded:", (await client.ReadChatAsync()).Text);

        byte[] key = await host.AddAccountAsync("POLICYNORMAL");
        await using WorldTestClient normal = await host.ConnectAsync();
        await normal.AuthenticateAsync("POLICYNORMAL", key);
        Assert.Equal((byte)CharResult.CharCreateSuccess, await normal.TryCreateCharacterAsync("oldname"));
        Assert.Equal((byte)CharResult.CharNameReserved, await normal.TryCreateCharacterAsync("newname"));
    }

    private sealed class FakeStore(IEnumerable<string> names) : IReservedNameStore
    {
        public IReadOnlySet<string> Names { get; set; } = names.ToHashSet(StringComparer.Ordinal);
        public Task<IReadOnlySet<string>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Names);
    }

    private sealed class MutableStore(IEnumerable<string> names) : IReservedNameStore
    {
        public IReadOnlySet<string> Names { get; set; } = names.ToHashSet(StringComparer.Ordinal);
        public Task<IReadOnlySet<string>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Names);
    }
}
