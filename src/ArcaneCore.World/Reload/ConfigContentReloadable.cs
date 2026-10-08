using ArcaneCore.Game.AntiCheat;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Reload;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.World.AntiCheat;
using ArcaneCore.World.Social;
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
/// a nonsensical value rejects the whole reload (a negative number instead falls back to its default, as vmangos setConfigPos does, unless <c>HotReload:NegativeNumbers</c> is <c>Reject</c>); the running options are never touched.
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
        var social = new SocialOptions();
        var antiCheat = new AntiCheatOptions();
        IConfigurationRoot snapshot = fresh.Build();
        try
        {
            IConfigurationSection section = snapshot.GetSection(WorldOptions.SectionName);
            section.Bind(runtime);
            section.Bind(listener);
            snapshot.GetSection(SocialOptions.SectionName).Bind(social);
            snapshot.GetSection(AntiCheatOptions.SectionName).Bind(antiCheat);
        }
        finally
        {
            (snapshot as IDisposable)?.Dispose();
        }

        // vmangos setConfigPos/setConfigMin: a negative value is logged and replaced by the default, and the reload
        // goes on (World.cpp:2949-2977); HotReload:NegativeNumbers = Reject keeps the whole-reload rejection.
        var substitutions = new List<string>();
        var candidateView = new WorldConfigView(runtime, listener, social);
        var defaults = new WorldConfigView(new WorldRuntimeOptions(), null);
        if (ReloadPolicy.Resolve(services).NegativeNumbers == InvalidNumberPolicy.Retail)
        {
            foreach (WorldConfigKey key in WorldConfigKeys.All.Where(k => k.NegativeUsesDefault))
            {
                object? value = key.Read(candidateView);
                if (key.Check!(value) is not null)
                {
                    object? fallback = key.Read(defaults);
                    key.Apply!(candidateView, fallback);
                    substitutions.Add($"{key.Path} ({WorldConfigKey.Show(value)}) can't be negative. Using {WorldConfigKey.Show(fallback)} instead.");
                }
            }
        }

        WorldOptions? liveListener = services.GetService<IOptions<WorldOptions>>()?.Value;
        SocialOptions? liveSocial = services.GetService<SocialFeature>()?.Options;
        AntiCheatFeature? liveAntiCheat = services.GetService<AntiCheatFeature>();
        return Task.FromResult<ContentCandidate>(new ConfigCandidate(candidateView, liveListener, liveSocial, substitutions, antiCheat, liveAntiCheat));
    }

    private sealed class ConfigCandidate(
        WorldConfigView candidate, WorldOptions? liveListener, SocialOptions? liveSocial, IReadOnlyList<string> substitutions,
        AntiCheatOptions antiCheat, AntiCheatFeature? liveAntiCheat) : ContentCandidate
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

            // The AntiCheat section is applied as a whole (docs/areas/anticheat.md): one bad key rejects the reload.
            problems.AddRange(antiCheat.Validate());
            return problems;
        }

        public override void Commit(WorldRuntime world, ReloadTransaction transaction)
        {
            var live = new WorldConfigView(world.Options, liveListener, liveSocial);
            foreach (string substitution in substitutions)
            {
                transaction.Note(substitution);
            }

            int changed = 0;
            foreach (WorldConfigKey key in WorldConfigKeys.All)
            {
                object? current = key.Read(live);
                object? wanted = key.Read(candidate);
                if (current is null || Equals(current, wanted))
                {
                    // A null current value means the live side is unknown (no listener or social options
                    // registered): there is nothing to compare or to apply to.
                    continue;
                }

                if (key.Apply is { } apply)
                {
                    transaction.Step(key.Path, () => apply(live, wanted), () => apply(live, current));
                    changed++;
                }
                else
                {
                    transaction.Note($"{key.Path} option can't be changed at reload, using current value ({WorldConfigKey.Show(current)}).");
                }
            }

            if (liveAntiCheat is not null)
            {
                AntiCheatOptions previous = liveAntiCheat.Options;
                transaction.Step(AntiCheatOptions.SectionName, () => liveAntiCheat.ApplyOptions(antiCheat), () => liveAntiCheat.ApplyOptions(previous));
            }

            _summary = changed == 0 ? "no changes" : $"{changed} option(s) changed";
        }
    }
}
