using System.Diagnostics;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests;

/// <summary>
/// The connection setup of <see cref="WorldTestClient"/> (auth, character create, login) is off-world-thread work, so it has its own, wider
/// per-read bound than a command reply. A character store that answers late stands in for a starved host.
/// </summary>
public sealed class WorldTestClientSetupTimeoutTests
{
    [Fact]
    public async Task ASlowCharacterCreate_WithinTheSetupBound_StillEntersTheWorld()
    {
        // 12 s is past the 10 s reply bound the setup reads used to share, and well inside the setup bound.
        var delay = new CreateDelay { Duration = TimeSpan.FromSeconds(12) };
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(delay.Install);
        delay.Armed = true;

        var watch = Stopwatch.StartNew();
        await using WorldTestClient gm = await world.EnterWorldAsync("REPROGM", "Reprogm", AccountSecurity.Administrator);

        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(11), $"the create was not delayed (took {watch.Elapsed})");
        Assert.Single(await world.WithScopeAsync(sp => sp.GetRequiredService<ICharacterStore>().GetAllIdentitiesAsync()), i => i.Name == "Reprogm");
    }

    [Fact]
    public async Task ACharacterCreate_PastTheSetupBound_FailsNamingTheOpcodeAndStep()
    {
        var delay = new CreateDelay { Duration = TimeSpan.FromSeconds(6) };
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(delay.Install);
        delay.Armed = true;

        byte[] key = await world.Host.AddAccountAsync("GUARDGM", AccountSecurity.Administrator);
        await using WorldTestClient client = await world.Host.ConnectAsync();
        await client.AuthenticateAsync("GUARDGM", key);
        client.HandshakeTimeout = TimeSpan.FromSeconds(2);

        TimeoutException ex = await Assert.ThrowsAsync<TimeoutException>(() => client.CreateCharacterAsync("Guardgm"));
        Assert.Contains("SMSG_CHAR_CREATE", ex.Message);
        Assert.Contains("char create", ex.Message);
        Assert.Contains("(bound 2 s)", ex.Message);
        Assert.IsAssignableFrom<OperationCanceledException>(ex.InnerException);
    }

    [Fact]
    public async Task AReplyTheServerNeverSends_StillFailsAtTheTenSecondReadBound()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync();
        byte[] key = await world.Host.AddAccountAsync("SILENTGM", AccountSecurity.Administrator);
        await using WorldTestClient client = await world.Host.ConnectAsync();
        await client.AuthenticateAsync("SILENTGM", key);
        await client.CreateCharacterAsync("Silentgm");

        var watch = Stopwatch.StartNew();
        TimeoutException ex = await Assert.ThrowsAsync<TimeoutException>(() => client.ReadUntilAsync(WorldOpcode.SmsgMessagechat));
        Assert.Contains("SmsgMessagechat", ex.Message);
        Assert.InRange(watch.Elapsed.TotalSeconds, 9.5, 20);
    }

    /// <summary>Wraps the scoped character store so CreateAsync answers late once armed.</summary>
    private sealed class CreateDelay
    {
        public TimeSpan Duration { get; init; }

        public volatile bool Armed;

        public void Install(IServiceCollection services)
        {
            ServiceDescriptor original = services.Last(d => d.ServiceType == typeof(ICharacterStore));
            services.Remove(original);
            services.AddScoped<ICharacterStore>(sp =>
            {
                ICharacterStore inner = original.ImplementationType is { } type
                    ? (ICharacterStore)ActivatorUtilities.CreateInstance(sp, type)
                    : (ICharacterStore)original.ImplementationFactory!(sp);
                return new DelayedStore(inner, this);
            });
        }
    }

    private sealed class DelayedStore(ICharacterStore inner, CreateDelay delay) : ICharacterStore
    {
        public Task<IReadOnlyList<CharacterRecord>> GetByAccountAsync(int accountId, CancellationToken cancellationToken = default) => inner.GetByAccountAsync(accountId, cancellationToken);

        public Task<CharacterRecord?> GetByIdAsync(int id, CancellationToken cancellationToken = default) => inner.GetByIdAsync(id, cancellationToken);

        public Task<bool> IsNameTakenAsync(string name, CancellationToken cancellationToken = default) => inner.IsNameTakenAsync(name, cancellationToken);

        public Task<int> CountByAccountAsync(int accountId, CancellationToken cancellationToken = default) => inner.CountByAccountAsync(accountId, cancellationToken);

        public async Task<CharacterRecord> CreateAsync(CharacterRecord character, CancellationToken cancellationToken = default)
        {
            if (delay.Armed)
            {
                await Task.Delay(delay.Duration, CancellationToken.None);
            }

            return await inner.CreateAsync(character, cancellationToken);
        }

        public Task<bool> DeleteAsync(int id, int accountId, CancellationToken cancellationToken = default) => inner.DeleteAsync(id, accountId, cancellationToken);

        public Task SaveStateAsync(CharacterState state, CancellationToken cancellationToken = default) => inner.SaveStateAsync(state, cancellationToken);

        public Task<IReadOnlyList<ActionButton>> GetActionButtonsAsync(int characterId, CancellationToken cancellationToken = default) => inner.GetActionButtonsAsync(characterId, cancellationToken);

        public Task<IReadOnlyList<CharacterIdentity>> GetAllIdentitiesAsync(CancellationToken cancellationToken = default) => inner.GetAllIdentitiesAsync(cancellationToken);
    }
}
