using System.Net;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Ops;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests;

/// <summary>
/// A world port that another process holds. The refusal used to escape <see cref="WorldStartup.RunAsync"/> as an <see cref="InvalidOperationException"/> (the early probe) or a
/// <see cref="System.Net.Sockets.SocketException"/> (the real bind), which the crash handler turns into a fail-fast with a dump. Now both end the run with a failure exit code.
/// These run the real generic host through <see cref="WorldStartup.RunAsync"/>; the exit code is process-global, so they share the non-parallel collection.
/// </summary>
[Collection(ExitCodesCollection.Name)]
public sealed class WorldStartPortInUseTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task TheRealBindOnATakenPort_ReturnsAFailureExitCode_StopsTheStartedServices_AndDoesNotEscapeRunAsync()
    {
        int before = ExitCodes.Current;
        var taken = ArcaneCore.TestSupport.LoopbackPorts.CreateListener();
        taken.Start();
        var started = new Recorder();
        try
        {
            ExitCodes.Current = ExitCodes.Success;
            int port = ((IPEndPoint)taken.LocalEndpoint).Port;
            IHost host = new HostBuilder().ConfigureServices(services =>
            {
                services.AddSingleton<ICharacterStore>(new InMemoryCharacterStore());
                services.AddSingleton(sp => new CharacterSaveQueue(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance));
                services.AddSingleton(sp => new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, sp.GetRequiredService<CharacterSaveQueue>(), NullLogger<WorldRuntime>.Instance));
                services.AddSingleton<IHostedService>(started);
                services.AddSingleton<IHostedService>(sp => new WorldServer(
                    sp.GetRequiredService<IServiceScopeFactory>(),
                    Options.Create(new WorldOptions { BindAddress = IPAddress.Loopback.ToString(), Port = port }),
                    Options.Create(new WorldSessionOptions()), new OpcodeTable(), sp.GetRequiredService<WorldRuntime>(), new SessionRegistry(),
                    NullLoggerFactory.Instance, NullLogger<WorldServer>.Instance));
            }).Build();

            int code = await WorldStartup.RunAsync(host, () => Task.CompletedTask, new StringWriter(), () => ExitCodes.Current).WaitAsync(Deadline);

            Assert.Equal(ExitCodes.Failure, code);
            Assert.True(started.Stopped, "a hosted service started before the listener must still be stopped in order");
        }
        finally
        {
            taken.Stop();
            ExitCodes.Current = before;
        }
    }

    [Fact]
    public async Task TheEarlyProbeRefusal_ReturnsAFailureExitCode_AlsoWhenTheHostWrapsItInAnAggregate()
    {
        int before = ExitCodes.Current;
        try
        {
            ExitCodes.Current = ExitCodes.Success;
            IHost host = new HostBuilder().ConfigureServices(services =>
                services.AddSingleton<IHostedService>(new Refuses(new AggregateException(new WorldPortInUseException("the world port 127.0.0.1:1 is already in use")))))
                .Build();

            int code = await WorldStartup.RunAsync(host, () => Task.CompletedTask, new StringWriter(), () => ExitCodes.Current).WaitAsync(Deadline);

            Assert.Equal(ExitCodes.Failure, code);
        }
        finally
        {
            ExitCodes.Current = before;
        }
    }

    private sealed class Recorder : IHostedService
    {
        public bool Stopped { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Stopped = true;
            return Task.CompletedTask;
        }
    }

    private sealed class Refuses(Exception failure) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => throw failure;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
