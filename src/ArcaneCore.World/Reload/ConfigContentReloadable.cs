using ArcaneCore.Game.AntiCheat;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Reload;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.World.AntiCheat;
using ArcaneCore.World.Gm.ServerConfig;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Rates;
using ArcaneCore.World.Social;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
        var playerbots = new PlayerbotOptions();
        var locomotion = new LocomotionOptions();
        var antiCheat = new AntiCheatOptions();
        IConfigurationRoot snapshot = fresh.Build();
        IConfiguration modules;
        try
        {
            // The script modules' section, copied out before the snapshot is disposed (docs/integration/script-hooks.md).
            string modulesSection = Scripting.ScriptModuleContext.ModulesSectionName;
            modules = new ConfigurationBuilder()
                .AddInMemoryCollection(snapshot.GetSection(modulesSection).AsEnumerable())
                .Build();
            IConfigurationSection section = snapshot.GetSection(WorldOptions.SectionName);
            section.Bind(runtime);
            section.Bind(listener);
            snapshot.GetSection(SocialOptions.SectionName).Bind(social);
            PlayerbotOptions.ApplyConfiguration(playerbots, snapshot);
            snapshot.GetSection(LocomotionOptions.SectionName).Bind(locomotion);
            snapshot.GetSection(AntiCheatOptions.SectionName).Bind(antiCheat);
        }
        finally
        {
            (snapshot as IDisposable)?.Dispose();
        }

        // vmangos setConfigPos/setConfigMin: a negative value is logged and replaced by the default, and the reload
        // goes on (World.cpp:2949-2977); HotReload:NegativeNumbers = Reject keeps the whole-reload rejection.
        var substitutions = new List<string>();

        // The speed rates are clamped to 0.1..10 at reload as at start (the fork's setConfigMinMax also runs on reload).
        foreach (string clamped in locomotion.Normalize())
        {
            substitutions.Add($"{LocomotionOptions.SectionName}:{clamped} is out of range and was corrected.");
        }

        var candidateView = new WorldConfigView(runtime, listener, social, playerbots, locomotion);
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
        PlayerbotOptions? livePlayerbots = services.GetService<IOptions<PlayerbotOptions>>()?.Value;
        AntiCheatFeature? liveAntiCheat = services.GetService<AntiCheatFeature>();
        return Task.FromResult<ContentCandidate>(new ConfigCandidate(candidateView, liveListener, liveSocial, livePlayerbots, substitutions, antiCheat, liveAntiCheat,
            services.GetService<Scripting.ScriptHooksFeature>(), modules, services));
    }

    private sealed class ConfigCandidate(
        WorldConfigView candidate, WorldOptions? liveListener, SocialOptions? liveSocial, PlayerbotOptions? livePlayerbots,
        IReadOnlyList<string> substitutions, AntiCheatOptions antiCheat, AntiCheatFeature? liveAntiCheat,
        Scripting.ScriptHooksFeature? scripts, IConfiguration modules, IServiceProvider services) : ContentCandidate
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

            // The keys are checked one by one above; the rules that tie two values together are checked on the whole candidate section
            // (UnfriendAffinity not above FriendAffinity, UnignoreAffinity not below IgnoreAffinity), as at start.
            if (candidate.Playerbots is { } playerbots)
            {
                try
                {
                    playerbots.Social.Lists.Validate();
                }
                catch (InvalidOperationException ex)
                {
                    problems.Add(ex.Message);
                }
            }

            // The AntiCheat section is applied as a whole (docs/areas/anticheat.md): one bad key rejects the reload.
            problems.AddRange(antiCheat.Validate());
            return problems;
        }

        public override void Commit(WorldRuntime world, ReloadTransaction transaction)
        {
            LocomotionOptions? liveLocomotion = LocomotionEnvironment.RegisteredOptions(world);
            var live = new WorldConfigView(world.Options, liveListener, liveSocial, livePlayerbots, liveLocomotion);
            foreach (string substitution in substitutions)
            {
                transaction.Note(substitution);
            }

            // Speed rates: the players' copies are re-sent after the keys change, and on a rollback after the keys are restored
            // (the undo log runs newest first, so this first step's undo runs last).
            bool speedRatesChanged = liveLocomotion is not null
                && WorldConfigKeys.All.Any(k => WorldConfigKeys.IsSpeedRate(k) && !Equals(k.Read(live), k.Read(candidate)));
            if (speedRatesChanged)
            {
                transaction.Step("player speed rates (rollback re-send)", static () => { }, () => SpeedRates.ApplyToAll(world, liveLocomotion!));
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

            if (speedRatesChanged)
            {
                int refreshed = 0;
                transaction.Step("player speed rates", () => refreshed = SpeedRates.ApplyToAll(world, liveLocomotion!), static () => { });
                transaction.Note($"Player speed rates changed; the speeds of {refreshed} online player(s) were re-sent.");
            }

            // .rates set overrides last until the next reload config (docs/areas/rates.md): the rates go back to the values they were
            // configured with, and a rollback puts the overrides back. The rate options themselves are not part of the reload set.
            ServerRates rates = ServerRates.For(world);
            if (rates.OverrideCount > 0)
            {
                var rateScope = new RateScope(services, world, (services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance).CreateLogger("ArcaneCore.World.Rates"));
                IReadOnlyList<(RateDefinition Rate, float Value)> overrides = rates.SnapshotOverrides(rateScope);
                int cleared = 0;
                transaction.Step(
                    "rate overrides",
                    () => cleared = rates.ResetAll(rateScope, "reload config", RateChangeKind.Reload),
                    () =>
                    {
                        foreach ((RateDefinition rate, float value) in overrides)
                        {
                            rates.TrySet(rateScope, rate, value, "reload config rollback", null, out _);
                        }
                    });
                transaction.Note($"{cleared} .rates override(s) cleared; the rates are back to their configured values.");
            }

            // The overrides made through .server config end here too: the keys they changed hold the file's value now, so their baseline
            // is the file's value again. The PacketHeuristics and pack keys are not reloaded and keep theirs.
            OptionOverrides optionOverrides = OptionOverrides.For(world);
            if (optionOverrides.Count > 0)
            {
                IReadOnlyList<KeyValuePair<string, OptionOverrides.Entry>> forgotten = [];
                transaction.Step(
                    "option overrides",
                    () => forgotten = optionOverrides.ForgetReloaded(),
                    () => optionOverrides.Restore(forgotten));
                if (forgotten.Count > 0)
                {
                    transaction.Note($"{forgotten.Count} .server config override(s) ended; those options hold the configured values.");
                }
            }

            if (liveAntiCheat is not null)
            {
                AntiCheatOptions previous = liveAntiCheat.Options;
                transaction.Step(AntiCheatOptions.SectionName, () => liveAntiCheat.ApplyOptions(antiCheat), () => liveAntiCheat.ApplyOptions(previous));
            }

            if (scripts is not null)
            {
                // AzerothCore WorldScript::OnAfterConfigLoad(reload: true): after the options above. Not undone on a rollback.
                transaction.Step("script modules", () => scripts.ReloadConfig(modules), static () => { });
            }

            _summary = changed == 0 ? "no changes" : $"{changed} option(s) changed";
        }
    }
}
