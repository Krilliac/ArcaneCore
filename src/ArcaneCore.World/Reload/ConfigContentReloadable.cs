using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Reload;
using ArcaneCore.Kernel.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Reload;

/// <summary>
/// <c>.reload config</c> (vmangos <c>HandleReloadConfigCommand</c>, ServerCommands.cpp:1016 →
/// <c>World::LoadConfigSettings(true)</c>, World.cpp:445): the configuration sources are read again into
/// a throwaway configuration, the <c>World</c> section is bound to a candidate, and the live options
/// are updated in place on the world thread. Options the process only reads at start keep their value
/// and are reported (World.cpp:3044-3055). A source that cannot be read, a value of the wrong type or
/// a nonsensical value rejects the whole reload; the running options are never touched.
/// <para>
/// The live configuration root is never reloaded: a broken file would empty it
/// (<c>FileConfigurationProvider.Load(reload: true)</c> clears its data before it throws), so the
/// candidate is built from fresh providers over the same source list and disposed afterwards.
/// </para>
/// </summary>
public sealed class ConfigContentReloadable(IServiceProvider services) : IContentReloadable
{
    public string Name => "config";

    /// <summary>vmangos <c>reload all</c> does not include the config (ServerCommands.cpp:885-905).</summary>
    public bool IncludedInAll => false;

    public Task<ContentCandidate> BuildAsync(CancellationToken cancellationToken)
    {
        IConfiguration configuration = services.GetService<IConfiguration>()
            ?? throw new InvalidOperationException("no configuration is registered, so it cannot be re-read");

        if (configuration is not IConfigurationBuilder builder)
        {
            throw new InvalidOperationException("the configuration was not built from a source list, so it cannot be re-read");
        }

        var fresh = new ConfigurationBuilder();
        foreach (IConfigurationSource source in builder.Sources)
        {
            fresh.Add(source);
        }

        var runtime = new WorldRuntimeOptions();
        var listener = new WorldOptions();
        IConfigurationRoot snapshot = fresh.Build();
        try
        {
            IConfigurationSection section = snapshot.GetSection(WorldOptions.SectionName);
            section.Bind(runtime);
            section.Bind(listener);
        }
        finally
        {
            (snapshot as IDisposable)?.Dispose();
        }

        WorldOptions? liveListener = services.GetService<IOptions<WorldOptions>>()?.Value;
        return Task.FromResult<ContentCandidate>(new ConfigCandidate(new WorldConfigView(runtime, listener), liveListener));
    }

    private sealed class ConfigCandidate(WorldConfigView candidate, WorldOptions? liveListener) : ContentCandidate
    {
        private string _summary = "configuration";

        public override string Summary => _summary;

        public override IReadOnlyList<string> Validate()
        {
            var problems = new List<string>();
            foreach (WorldConfigKey key in WorldConfigKeys.All)
            {
                if (key.Check is { } check && check(key.Read(candidate)) is { } problem)
                {
                    problems.Add($"{key.Path} ({WorldConfigKey.Show(key.Read(candidate))}) {problem}");
                }
            }

            return problems;
        }

        public override void Commit(WorldRuntime world, ReloadTransaction transaction)
        {
            var live = new WorldConfigView(world.Options, liveListener);
            int changed = 0;
            foreach (WorldConfigKey key in WorldConfigKeys.All)
            {
                object? current = key.Read(live);
                object? wanted = key.Read(candidate);
                if (Equals(current, wanted))
                {
                    continue;
                }

                if (key.Apply is { } apply)
                {
                    transaction.Step(key.Path, () => apply(world.Options, wanted), () => apply(world.Options, current));
                    changed++;
                }
                else if (current is not null)
                {
                    // A null current value means the live side is unknown (no listener options registered): nothing to compare.
                    transaction.Note($"{key.Path} option can't be changed at reload, using current value ({WorldConfigKey.Show(current)}).");
                }
            }

            _summary = changed == 0 ? "no changes" : $"{changed} option(s) changed";
        }
    }
}
