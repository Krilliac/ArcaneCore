using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>Test hosts for <c>World:Playerbots:ClientFidelity</c>: a host registers the shared options object the sessions read.</summary>
internal static class BotFidelity
{
    /// <summary><c>configureServices</c> for <see cref="WorldTestHost.Start"/> that sets the bots' client fidelity.</summary>
    internal static Action<IServiceCollection> Services(PlayerbotClientFidelity fidelity)
        => services => services.AddSingleton(Options.Create(new PlayerbotOptions { ClientFidelity = fidelity }));

    internal static WorldTestHost Start(PlayerbotClientFidelity fidelity)
        => WorldTestHost.Start(configureServices: Services(fidelity));
}
