using ArcaneCore.Game.AntiCheat;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Kernel.Configuration.Conf;
using ArcaneCore.World.AntiCheat;
using ArcaneCore.World.Gm.ServerConfig;
using ArcaneCore.World.Locomotion;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Rates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.ConfigEditor;

/// <summary>
/// <see cref="OptionCatalog"/> in the in-process daemon: which keys exist, bounds and cross-checks, the Live/NextLoad/Restart classes,
/// baseline and override tracking with <c>diff</c> and <c>reset</c>, the alias routing of the old <c>set</c> verbs, secret masking, and the
/// pack registration seam.
/// </summary>
public sealed class OptionCatalogTests
{
    private const string Autosave = "World:AutosaveIntervalMs";

    private static readonly OptionActor Tester = new("Tester", "unit test");

    private static OptionScope Scope(WorldTestHost host) => new(host.WorldServices, host.World, NullLogger.Instance);

    [Fact]
    public async Task Catalog_ListsEverySource_WithUniquePaths_AndClassifiesTheApplyClass()
    {
        await using var host = WorldTestHost.Start();
        (OptionCatalog catalog, int conflicts) = await host.OnWorldAsync(() =>
        {
            var c = OptionCatalog.Create(Scope(host));
            return (c, c.Conflicts.Count);
        });

        Assert.Equal(0, conflicts);
        Assert.Equal(catalog.Keys.Count, catalog.Keys.Select(k => k.Path.ToUpperInvariant()).Distinct().Count());
        Assert.True(catalog.Keys.Count > 100, "the reload keys, rates and anticheat keys must all be listed; found " + catalog.Keys.Count);

        OptionKey restart = catalog.Find("World:TickIntervalMs")!;
        Assert.Equal(OptionApplyClass.Restart, restart.Apply);
        Assert.False(restart.Writable);

        OptionKey live = catalog.Find(Autosave)!;
        Assert.Equal(OptionApplyClass.Live, live.Apply);
        Assert.True(live.Writable);
        Assert.Same(live, catalog.Find("autosaveintervalms"));

        Assert.Equal(OptionApplyClass.Live, catalog.Find("Rate.XP.Kill")!.Apply);
        Assert.Same(catalog.Find("Rate.XP.Kill"), catalog.Find("xp"));
        Assert.Equal(OptionApplyClass.NextLoad, catalog.Find("Rate.Creature.Normal.HP")!.Apply);
        Assert.Equal(OptionApplyClass.Live, catalog.Find("AntiCheat:Enabled")!.Apply);
        Assert.Same(catalog.Find("AntiCheat:Enabled"), catalog.Find("anticheat.enabled"));
        Assert.Same(catalog.Find("World:PacketHeuristics:Enabled"), catalog.Find("packetcheck.enabled"));
        Assert.Same(catalog.Find("Locomotion:PlayerRunSpeedRate"), catalog.Find("movement.run"));
        Assert.All(catalog.Keys.Where(k => k.Apply == OptionApplyClass.Restart), k => Assert.False(k.Writable));
        Assert.All(catalog.Keys.Where(k => k.Writable), k => Assert.NotEqual(OptionApplyClass.Restart, k.Apply));
    }

    [Fact]
    public async Task EveryRate_NamesARealConfigurationKey_ForTheSourcesListing()
    {
        await using var host = WorldTestHost.Start();
        OptionKey[] rates = await host.OnWorldAsync(() => OptionCatalog.Create(Scope(host)).Keys.Where(k => k.Group == "Rates").ToArray());

        Assert.NotEmpty(rates);
        Assert.Equal("Progression:RateXpKill", rates.Single(k => k.Path == "Rate.XP.Kill").ConfigPath);
        Assert.All(rates, k => Assert.True(ConfCatalog.Embedded.Find(k.ConfigPath) is not null, $"{k.Path} -> '{k.ConfigPath}' is not a configuration key"));
        Assert.Equal(rates.Length, rates.Select(k => k.ConfigPath).Distinct().Count());
    }

    [Fact]
    public async Task ReloadKeys_CarryTheirConfigurationDescription_SoTheFilterAndGetCanUseIt()
    {
        await using var host = WorldTestHost.Start();
        OptionCatalog catalog = await host.OnWorldAsync(() => OptionCatalog.Create(Scope(host)));

        OptionKey grid = catalog.Find("World:Maps:GridUnload")!;
        Assert.NotEqual("(no description)", grid.Description);
        Assert.DoesNotContain('`', grid.Description);
        Assert.True(grid.Description.Length <= 160);
        Assert.Contains(grid, catalog.Match(grid.Description.Split(' ')[^1].Trim('.', ',', ')', '(')));

        OptionKey[] reload = [.. catalog.Keys.Where(k => k.Group != "Rates" && k.Group != "AntiCheat" && k.Group != "PacketHeuristics" && k.Group != "Pack")];
        Assert.True(reload.Count(k => k.Description == "(no description)") * 20 < reload.Length, "at most 5% of the reload keys may lack a catalog description");
    }

    [Fact]
    public async Task Set_ChangesALiveKey_TracksTheBaseline_AndDiffAndResetUseIt()
    {
        await using var host = WorldTestHost.Start();
        (int original, int during, string before, string after, bool overriddenDuring, int diffCount, int restored, bool overriddenAfter, bool resetAgain) = await host.OnWorldAsync(() =>
        {
            OptionScope scope = Scope(host);
            var catalog = OptionCatalog.Create(scope);
            OptionKey key = catalog.Find(Autosave)!;
            int originalValue = host.World.Options.AutosaveIntervalMs;
            OptionSetResult result = catalog.TrySet(scope, key, "12345", Tester, out _, out string b, out string a);
            Assert.Equal(OptionSetResult.Ok, result);
            int now = host.World.Options.AutosaveIntervalMs;
            OptionStatus status = catalog.Describe(scope, key);
            int diff = catalog.Diff(scope).Count;
            // A second set keeps the first baseline.
            Assert.Equal(OptionSetResult.Ok, catalog.TrySet(scope, key, "23456", Tester, out _, out _, out _));
            Assert.Equal((long)originalValue, catalog.Describe(scope, key).Baseline);
            bool reset = catalog.Reset(scope, key, "Tester");
            bool resetTwice = catalog.Reset(scope, key, "Tester");
            return (originalValue, now, b, a, status.Overridden, diff, host.World.Options.AutosaveIntervalMs, catalog.Describe(scope, key).Overridden, reset && !resetTwice);
        });

        Assert.Equal(12345, during);
        Assert.Equal(original.ToString(), before);
        Assert.Equal("12345", after);
        Assert.True(overriddenDuring);
        Assert.Equal(1, diffCount);
        Assert.Equal(original, restored);
        Assert.False(overriddenAfter);
        Assert.True(resetAgain);
    }

    [Theory]
    [InlineData("World:AutosaveIntervalMs", "abc", OptionSetResult.Invalid)]
    [InlineData("World:AutosaveIntervalMs", "1.5", OptionSetResult.Invalid)]
    [InlineData("World:AutosaveIntervalMs", "-5", OptionSetResult.Invalid)]
    [InlineData("World:AutosaveIntervalMs", "99999999999", OptionSetResult.Invalid)]
    [InlineData("World:MaxCommandsPerTick", "0", OptionSetResult.Invalid)]
    [InlineData("World:TickIntervalMs", "50", OptionSetResult.ReadOnly)]
    [InlineData("Rate.XP.Kill", "1000", OptionSetResult.Invalid)]
    [InlineData("Rate.XP.Kill", "nan", OptionSetResult.Invalid)]
    [InlineData("Rate.XP.Personal.Min", "50", OptionSetResult.Invalid)]
    [InlineData("AntiCheat:Action", "bogus", OptionSetResult.Invalid)]
    [InlineData("AntiCheat:Enabled", "maybe", OptionSetResult.Invalid)]
    [InlineData("AntiCheat:ScoreGmAlert", "500", OptionSetResult.Invalid)]
    public async Task Set_RefusesBadTypeBoundsCrossChecksAndRestartKeys_AndChangesNothing(string path, string text, OptionSetResult expected)
    {
        await using var host = WorldTestHost.Start();
        (OptionSetResult result, string error, int overrides) = await host.OnWorldAsync(() =>
        {
            OptionScope scope = Scope(host);
            var catalog = OptionCatalog.Create(scope);
            OptionSetResult r = catalog.TrySet(scope, catalog.Find(path)!, text, Tester, out string e, out _, out _);
            return (r, e, catalog.Diff(scope).Count);
        });

        Assert.Equal(expected, result);
        Assert.NotEmpty(error);
        Assert.Equal(0, overrides);
    }

    [Fact]
    public async Task Rates_AreSetThroughTheCatalog_KeepTheirOwnBaseline_AndTheHistory()
    {
        await using var host = WorldTestHost.Start();
        (bool overridden, float now, int diff, bool reset, bool overriddenAfter, int history) = await host.OnWorldAsync(() =>
        {
            OptionScope scope = Scope(host);
            var catalog = OptionCatalog.Create(scope);
            OptionKey key = catalog.Find("xp")!;
            Assert.Equal(OptionSetResult.Ok, catalog.TrySet(scope, key, "2.5", Tester, out _, out string before, out string after));
            Assert.Equal("1", before);
            Assert.Equal("2.5", after);
            ServerRates rates = ServerRates.For(host.World);
            RateDefinition rate = ServerRates.Find("Rate.XP.Kill")!;
            bool isOverridden = rates.IsOverridden(rate);
            float current = rates.Describe(new RateScope(host.WorldServices, host.World, NullLogger.Instance), rate).Current;
            int changed = catalog.Diff(scope).Count;
            bool wasReset = catalog.Reset(scope, key, "Tester");
            return (isOverridden, current, changed, wasReset, rates.IsOverridden(rate), rates.History.Count);
        });

        Assert.True(overridden);
        Assert.Equal(2.5f, now);
        Assert.Equal(1, diff);
        Assert.True(reset);
        Assert.False(overriddenAfter);
        Assert.Equal(2, history); // the set and the reset
    }

    [Fact]
    public async Task RateHistory_KeepsTheNewestChanges_WithActorAndReason()
    {
        await using var host = WorldTestHost.Start();
        (int count, RateHistoryEntry last) = await host.OnWorldAsync(() =>
        {
            var scope = new RateScope(host.WorldServices, host.World, NullLogger.Instance);
            ServerRates rates = ServerRates.For(host.World);
            RateDefinition rate = ServerRates.Find("xp")!;
            for (int i = 0; i < ServerRates.HistoryCapacity + 20; i++)
            {
                Assert.Equal(RateSetResult.Ok, rates.TrySet(scope, rate, 1f + (i % 5), "Tester", $"change {i}", out _));
            }

            return (rates.History.Count, rates.History[^1]);
        });

        Assert.Equal(ServerRates.HistoryCapacity, count);
        Assert.Equal("change " + (ServerRates.HistoryCapacity + 19), last.Change.Reason);
        Assert.Equal("Tester", last.Change.Actor);
        Assert.Equal(RateChangeKind.Set, last.Change.Kind);
    }

    [Fact]
    public async Task AReloadKey_IsCrossCheckedAgainstItsSection_AsTheReloadDoes()
    {
        var playerbots = new PlayerbotOptions();
        await using var host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IOptions<PlayerbotOptions>>(Microsoft.Extensions.Options.Options.Create(playerbots)));
        (OptionSetResult refused, string error, int original, int unchanged, int overrides, OptionSetResult fine, int friend, int lowered) = await host.OnWorldAsync(() =>
        {
            OptionScope scope = Scope(host);
            var catalog = OptionCatalog.Create(scope);
            OptionKey unfriend = catalog.Find("World:Playerbots:Social:Lists:UnfriendAffinity")!;
            int originalValue = playerbots.Social.Lists.UnfriendAffinity;
            int friendAffinity = playerbots.Social.Lists.FriendAffinity;
            Assert.True(friendAffinity < 90, "the test needs FriendAffinity below the value it sets");
            // 90 passes the key's own -100..100 check but ends above FriendAffinity, which startup and .reload config both refuse.
            OptionSetResult result = catalog.TrySet(scope, unfriend, "90", Tester, out string message, out _, out _);
            int after = playerbots.Social.Lists.UnfriendAffinity;
            int count = OptionOverrides.For(host.World).Count;
            OptionSetResult ok = catalog.TrySet(scope, unfriend, (friendAffinity - 1).ToString(), Tester, out _, out _, out _);
            return (result, message, originalValue, after, count, ok, friendAffinity, playerbots.Social.Lists.UnfriendAffinity);
        });

        Assert.Equal(OptionSetResult.Invalid, refused);
        Assert.Contains("UnfriendAffinity must not exceed FriendAffinity", error, StringComparison.Ordinal);
        Assert.Equal(original, unchanged);
        Assert.Equal(0, overrides);
        Assert.Equal(OptionSetResult.Ok, fine);
        Assert.Equal(friend - 1, lowered);
    }

    [Fact]
    public async Task AValueAReloadPutBack_IsNoLongerReportedAsOverridden()
    {
        await using var host = WorldTestHost.Start();
        (bool during, bool after) = await host.OnWorldAsync(() =>
        {
            OptionScope scope = Scope(host);
            var catalog = OptionCatalog.Create(scope);
            OptionKey key = catalog.Find(Autosave)!;
            int original = host.World.Options.AutosaveIntervalMs;
            Assert.Equal(OptionSetResult.Ok, catalog.TrySet(scope, key, "777", Tester, out _, out _, out _));
            bool overridden = catalog.Describe(scope, key).Overridden;
            host.World.Options.AutosaveIntervalMs = original; // another path puts the first value back
            return (overridden, catalog.Describe(scope, key).Overridden);
        });

        Assert.True(during);
        Assert.False(after);
    }

    [Fact]
    public async Task ReloadConfig_EndsTheOverridesOfTheKeysItReloads_AndKeepsThePacketHeuristicsOnes_AndRollbackRestoresThem()
    {
        await using var host = WorldTestHost.Start();
        ArcaneCore.Game.Reload.ContentCandidate candidate = await new ArcaneCore.World.Reload.ConfigContentReloadable(host.WorldServices).BuildAsync(CancellationToken.None);
        const string packetEnabled = "World:PacketHeuristics:Enabled";

        (bool reloadKeyEnds, int valueAfter, int changedTo, bool packetKept, int count, bool rolledBack, int valueRolledBack, IReadOnlyList<Exception> failures) = await host.OnWorldAsync(() =>
        {
            OptionScope scope = Scope(host);
            var catalog = OptionCatalog.Create(scope);
            OptionKey autosave = catalog.Find(Autosave)!;
            OptionKey packet = catalog.Find(packetEnabled)!;
            int file = host.World.Options.AutosaveIntervalMs;
            int changed = file == 777 ? 778 : 777;
            Assert.Equal(OptionSetResult.Ok, catalog.TrySet(scope, autosave, changed.ToString(), Tester, out _, out _, out _));
            bool packetNow = (bool)catalog.Describe(scope, packet).Current!;
            Assert.Equal(OptionSetResult.Ok, catalog.TrySet(scope, packet, (!packetNow).ToString(), Tester, out _, out _, out _));
            Assert.Equal(2, OptionOverrides.For(host.World).Count);

            var transaction = new ArcaneCore.Game.Reload.ReloadTransaction();
            candidate.Commit(host.World, transaction);
            bool ends = !catalog.Describe(scope, autosave).Overridden;
            int after = host.World.Options.AutosaveIntervalMs;
            bool kept = catalog.Describe(scope, packet).Overridden;
            int remaining = OptionOverrides.For(host.World).Count;

            IReadOnlyList<Exception> errors = transaction.Rollback();
            return (ends, after, changed, kept, remaining, catalog.Describe(scope, autosave).Overridden, host.World.Options.AutosaveIntervalMs, errors);
        });

        Assert.True(reloadKeyEnds);
        Assert.NotEqual(changedTo, valueAfter); // the reload put the configured value back
        Assert.True(packetKept);
        Assert.Equal(1, count);
        Assert.True(rolledBack);
        Assert.Equal(changedTo, valueRolledBack);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task ASecretKey_IsNeverWritableThroughTheEditor()
    {
        var provider = new SecretProvider();
        await using var host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IOptionSource>(provider));
        (bool writable, OptionSetResult result, string error, string shown) = await host.OnWorldAsync(() =>
        {
            OptionScope scope = Scope(host);
            var catalog = OptionCatalog.Create(scope);
            OptionKey key = catalog.Find("Pack:Test:Token")!;
            OptionSetResult r = catalog.TrySet(scope, key, "hunter2", Tester, out string message, out _, out _);
            return (key.Writable, r, message, key.Format("hunter2"));
        });

        Assert.False(writable);
        Assert.Equal(OptionSetResult.ReadOnly, result);
        Assert.Contains("secret", error, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", shown, StringComparison.Ordinal);
        Assert.Equal("original", provider.Value);
    }

    private sealed class SecretProvider : IOptionSource
    {
        public string Value { get; private set; } = "original";

        public void RegisterOptions(IOptionRegistry registry)
            => registry.AddText("Pack:Test:Token", "a secret test token", OptionApplyClass.Live, () => Value, v => Value = v, secret: true);
    }

    [Fact]
    public async Task TheAliasNames_ReachTheSameKeysAsThePaths_SoTheOldVerbsSetThroughTheCatalog()
    {
        await using var host = WorldTestHost.Start();
        (bool sameKey, bool changedByAlias, bool overridden, bool reset, bool restored) = await host.OnWorldAsync(() =>
        {
            OptionScope scope = Scope(host);
            AntiCheatFeature feature = host.WorldServices.GetRequiredService<AntiCheatFeature>();
            bool original = feature.Options.Enabled;
            var catalog = OptionCatalog.Create(scope);
            OptionKey key = catalog.Find("anticheat.enabled")!;
            bool same = ReferenceEquals(key, catalog.Find("AntiCheat:Enabled"));
            Assert.Equal(OptionSetResult.Ok, catalog.TrySet(scope, key, (!original).ToString(), Tester, out _, out _, out _));
            bool changed = feature.Options.Enabled != original;
            bool isOverridden = catalog.Describe(scope, key).Overridden;
            bool wasReset = catalog.Reset(scope, key, "Tester");
            return (same, changed, isOverridden, wasReset, feature.Options.Enabled == original);
        });

        Assert.True(sameKey);
        Assert.True(changedByAlias);
        Assert.True(overridden);
        Assert.True(reset);
        Assert.True(restored);
    }

    [Fact]
    public async Task EveryMovementSetField_ResolvesToACatalogKey_ThatReadsTheValueTheFieldReads()
    {
        await using var host = WorldTestHost.Start();
        (string[] missing, string[] wrongValue) = await host.OnWorldAsync(() =>
        {
            OptionScope scope = Scope(host);
            var catalog = OptionCatalog.Create(scope);
            LocomotionOptions options = LocomotionEnvironment.RegisteredOptions(host.World)!;
            var none = new List<string>();
            var wrong = new List<string>();
            foreach ((string field, string path, Func<LocomotionOptions, float> get, _) in MovementRateCommands.Fields)
            {
                OptionKey? key = catalog.Find("movement." + field);
                if (key is null)
                {
                    none.Add(field); // a new .movement set field needs its WorldConfigKeys entry
                    continue;
                }

                if (key.Path != path || Math.Abs(Convert.ToDouble(catalog.Describe(scope, key).Current, System.Globalization.CultureInfo.InvariantCulture) - get(options)) > 1e-6)
                {
                    wrong.Add(field);
                }
            }

            return (none.ToArray(), wrong.ToArray());
        });

        Assert.Empty(missing);
        Assert.Empty(wrongValue);
    }

    [Fact]
    public void SecretKeys_AreMasked_WhateverTheirValue()
    {
        var byName = new OptionKey("Some:ApiPassword", "d", OptionType.Text, OptionApplyClass.Live, "t", _ => "hunter2", null);
        var flagged = new OptionKey("Some:Plain", "d", OptionType.Text, OptionApplyClass.Live, "t", _ => "hunter2", null, secret: true);
        var open = new OptionKey("Some:Plain", "d", OptionType.Text, OptionApplyClass.Live, "t", _ => "visible", null);
        var switchKey = new OptionKey("Some:GhPassToken", "d", OptionType.Bool, OptionApplyClass.Live, "t", _ => true, null);

        string[] shown = [byName.Format("hunter2"), flagged.Format("hunter2"), open.Format("visible"), switchKey.Format(true), byName.Format(string.Empty)];

        Assert.Equal(["********", "********", "visible", "true", "(empty)"], shown);
        Assert.DoesNotContain(shown, s => s.Contains("hunter2", StringComparison.Ordinal));
    }

    [Fact]
    public void Format_ShowsNumbersInvariantlyAndBoundsAreEnforced()
    {
        var key = new OptionKey("Some:Rate", "d", OptionType.Number, OptionApplyClass.Live, "t", _ => 1.5, null, min: 0.1, max: 10);
        Assert.Equal("1.5", key.Format(1.5));
        Assert.Equal("0.1..10", key.BoundsText);
        Assert.False(key.TryParse("11", out _, out string error));
        Assert.Contains("between 0.1 and 10", error, StringComparison.Ordinal);
        Assert.True(key.TryParse("0.1", out object value, out _));
        Assert.Equal(0.1, value);
        Assert.False(key.TryParse("1e999", out _, out _));
    }

    private sealed class ProviderOverlay(IServiceProvider inner, IOptionSource extra) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IEnumerable<IOptionSource>) ? new[] { extra } : inner.GetService(serviceType);
    }

    private sealed class FakePack : IOptionSource
    {
        public bool Flag { get; set; }

        public long Level { get; set; } = 3;

        public void RegisterOptions(IOptionRegistry registry)
        {
            registry.AddBool("Fake:Pack:Flag", "a switch", OptionApplyClass.Live, () => Flag, v => Flag = v);
            registry.AddInteger("Fake:Pack:Level", "a level", OptionApplyClass.NextLoad, 1, 10, () => Level, v => Level = v);
            registry.AddInteger("Fake:Pack:Fixed", "read at start", OptionApplyClass.Restart, 0, 5, () => 2, v => Level = v);
            registry.AddText("Fake:Pack:ApiToken", "a credential", OptionApplyClass.Live, () => "hunter2", _ => { });
        }
    }

    [Fact]
    public async Task APackProvider_AddsItsKeys_WithBoundsApplyClassesAndMasking()
    {
        await using var host = WorldTestHost.Start();
        var pack = new FakePack();
        (OptionSetResult flag, OptionSetResult tooHigh, OptionSetResult level, OptionSetResult fixedKey, string shown, bool restartWritable) = await host.OnWorldAsync(() =>
        {
            OptionScope scope = new(new ProviderOverlay(host.WorldServices, pack), host.World, NullLogger.Instance);
            var catalog = OptionCatalog.Create(scope);
            Assert.All(new[] { "Fake:Pack:Flag", "Fake:Pack:Level", "Fake:Pack:Fixed", "Fake:Pack:ApiToken" }, p => Assert.Equal("Pack", catalog.Find(p)!.Group));
            OptionSetResult f = catalog.TrySet(scope, catalog.Find("Fake:Pack:Flag")!, "on", Tester, out _, out _, out _);
            OptionSetResult high = catalog.TrySet(scope, catalog.Find("Fake:Pack:Level")!, "11", Tester, out _, out _, out _);
            OptionSetResult l = catalog.TrySet(scope, catalog.Find("Fake:Pack:Level")!, "7", Tester, out _, out _, out _);
            OptionSetResult fx = catalog.TrySet(scope, catalog.Find("Fake:Pack:Fixed")!, "1", Tester, out _, out _, out _);
            OptionKey token = catalog.Find("Fake:Pack:ApiToken")!;
            return (f, high, l, fx, token.Format(catalog.Describe(scope, token).Current), catalog.Find("Fake:Pack:Fixed")!.Writable);
        });

        Assert.Equal(OptionSetResult.Ok, flag);
        Assert.True(pack.Flag);
        Assert.Equal(OptionSetResult.Invalid, tooHigh);
        Assert.Equal(OptionSetResult.Ok, level);
        Assert.Equal(7, pack.Level);
        Assert.Equal(OptionSetResult.ReadOnly, fixedKey);
        Assert.False(restartWritable);
        Assert.Equal("********", shown);
    }

    private sealed class CheckedPack : IOptionSource
    {
        public long Low { get; private set; } = 2;

        public void RegisterOptions(IOptionRegistry registry)
            => registry.AddInteger("Checked:Pack:Low", "must stay below 8", OptionApplyClass.Live, 0, 100, () => Low, v => Low = v, v => v >= 8 ? "Checked:Pack:Low must stay below 8" : null);
    }

    [Fact]
    public async Task APackKey_CanRefuseAValueWithItsOwnCheckMessage_BeforeItsSetterRuns()
    {
        await using var host = WorldTestHost.Start();
        var pack = new CheckedPack();
        (OptionSetResult refused, string message, long unchanged, OptionSetResult accepted) = await host.OnWorldAsync(() =>
        {
            OptionScope scope = new(new ProviderOverlay(host.WorldServices, pack), host.World, NullLogger.Instance);
            var catalog = OptionCatalog.Create(scope);
            OptionKey key = catalog.Find("Checked:Pack:Low")!;
            OptionSetResult no = catalog.TrySet(scope, key, "9", Tester, out string error, out _, out _);
            long kept = pack.Low;
            return (no, error, kept, catalog.TrySet(scope, key, "5", Tester, out _, out _, out _));
        });

        Assert.Equal(OptionSetResult.Invalid, refused);
        Assert.Equal("Checked:Pack:Low must stay below 8", message);
        Assert.Equal(2, unchanged);
        Assert.Equal(OptionSetResult.Ok, accepted);
        Assert.Equal(5, pack.Low);
    }
}
