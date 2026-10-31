using ArcaneCore.Game.AntiCheat;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.AntiCheat;
using ArcaneCore.World.AntiCheat.PacketHeuristics;
using ArcaneCore.World.Gm.ServerConfig;
using ArcaneCore.World.Rates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.ConfigEditor;

/// <summary>
/// Edge cases of <see cref="OptionKey"/> parsing and formatting, <see cref="OptionOverrides"/> bookkeeping, each write path of the catalog
/// (reload keys, locomotion, rates, anticheat, packet heuristics, pack keys) and what a refused or secret change leaves behind.
/// </summary>
public sealed class OptionCatalogEdgeTests
{
    private static readonly OptionActor Tester = new("Tester", "unit test");

    private static OptionScope Scope(WorldTestHost host) => new(host.WorldServices, host.World, NullLogger.Instance);

    private static OptionKey Key(OptionType type, double min = double.NaN, double max = double.NaN, IReadOnlyList<string>? choices = null, bool secret = false, string path = "Some:Key")
        => new(path, "d", type, OptionApplyClass.Live, "t", _ => null, null, min, max, secret, choices);

    // --- parsing -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("1", true)]
    [InlineData("on", true)]
    [InlineData("TRUE", true)]
    [InlineData(" Yes ", true)]
    [InlineData("0", false)]
    [InlineData("off", false)]
    [InlineData("False", false)]
    [InlineData("no", false)]
    public void Bool_AcceptsTheDocumentedSpellings_CaseAndSpaceInsensitively(string text, bool expected)
    {
        Assert.True(Key(OptionType.Bool).TryParse(text, out object value, out _));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("2")]
    [InlineData("maybe")]
    [InlineData("truee")]
    public void Bool_RefusesAnythingElse(string text)
    {
        Assert.False(Key(OptionType.Bool).TryParse(text, out _, out string error));
        Assert.Contains("is not a boolean", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("10", true)]
    [InlineData(" 5 ", true)]
    [InlineData("+5", true)]
    [InlineData("0", false)]
    [InlineData("11", false)]
    [InlineData("-1", false)]
    [InlineData("1e1", false)]
    [InlineData("5.0", false)]
    [InlineData("", false)]
    [InlineData("99999999999999999999", false)]
    public void Integer_BoundsAreInclusive_AndOnlyWholeNumbersParse(string text, bool accepted)
    {
        OptionKey key = Key(OptionType.Integer, 1, 10);

        Assert.Equal(accepted, key.TryParse(text, out object value, out string error));
        if (accepted)
        {
            Assert.IsType<long>(value);
            Assert.Empty(error);
        }
        else
        {
            Assert.NotEmpty(error);
        }
    }

    [Theory]
    [InlineData("0.5", true)]
    [InlineData("10", true)]
    [InlineData("0.4999999", false)]
    [InlineData("10.0000001", false)]
    [InlineData("NaN", false)]
    [InlineData("Infinity", false)]
    [InlineData("-Infinity", false)]
    [InlineData("1,5", false)]
    public void Number_BoundsAreInclusive_AndNonFiniteValuesAreRefused(string text, bool accepted)
        => Assert.Equal(accepted, Key(OptionType.Number, 0.5, 10).TryParse(text, out _, out _));

    [Fact]
    public void Number_WithNoBounds_AcceptsAnyFiniteValue_AndIntegerAnyLong()
    {
        Assert.True(Key(OptionType.Number).TryParse("-1e300", out _, out _));
        Assert.True(Key(OptionType.Integer).TryParse(long.MinValue.ToString(), out object low, out _));
        Assert.Equal(long.MinValue, low);
    }

    [Fact]
    public void Choice_MatchesCaseInsensitively_ReturnsTheCanonicalName_AndListsTheChoicesOnError()
    {
        OptionKey key = Key(OptionType.Choice, choices: ["None", "Kick"]);

        Assert.True(key.TryParse(" kick ", out object value, out _));
        Assert.Equal("Kick", value);
        Assert.False(key.TryParse("ban", out _, out string error));
        Assert.Equal("'ban' is not one of None|Kick", error);
        Assert.Equal("None|Kick", key.BoundsText);
    }

    [Fact]
    public void Text_IsTrimmed_AndTakenVerbatim()
    {
        Assert.True(Key(OptionType.Text).TryParse("  a b:c  ", out object value, out _));
        Assert.Equal("a b:c", value);
        Assert.Equal(string.Empty, Key(OptionType.Text).BoundsText);
    }

    // --- formatting --------------------------------------------------------------------------------------------------

    [Fact]
    public void Format_ShowsMissingValues_FlattensLines_AndTruncatesLongText()
    {
        OptionKey text = Key(OptionType.Text);

        Assert.Equal("(not available)", text.Format(null));
        Assert.Equal("a  b c", text.Format("a\r\nb\nc"));
        string shown = text.Format(new string('x', 500));
        Assert.Equal(120, shown.Length);
        Assert.EndsWith("...", shown, StringComparison.Ordinal);
        Assert.Equal(new string('y', 120), text.Format(new string('y', 120)));
    }

    [Fact]
    public void Format_NumbersAreInvariantAndShortest()
    {
        OptionKey number = Key(OptionType.Number);

        Assert.Equal("0.1", number.Format(0.1));
        Assert.Equal("1", number.Format(1.0));
        Assert.Equal("-2.5", number.Format(-2.5));
        Assert.Equal("1234567890123", Key(OptionType.Integer).Format(1234567890123L));
    }

    [Fact]
    public void Secret_ByPathPattern_IsMaskedWithoutTheFlag_AndABoolStaysVisible()
    {
        OptionKey byPath = Key(OptionType.Text, path: "Database:ConnectionString");
        OptionKey token = Key(OptionType.Text, path: "Some:AuthToken");

        Assert.True(byPath.Secret);
        Assert.True(token.Secret);
        Assert.Equal("********", byPath.Format("Server=x;Password=y"));
        Assert.Equal("********", token.Format("abc"));
        Assert.False(Key(OptionType.Text, path: "Some:Plain").Secret);
    }

    // --- bookkeeping -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Overrides_KeepTheFirstBaseline_DropAnEntryThatReturnsToIt_AndForget()
    {
        await using var host = WorldTestHost.Start();
        (long baselineAfterSecond, int afterReturn, bool forgot, bool forgotAgain, int countStart) = await host.OnWorldAsync(() =>
        {
            OptionOverrides store = OptionOverrides.For(host.World);
            int start = store.Count;
            Assert.Same(store, OptionOverrides.For(host.World));
            store.Remember("Z:One", 1L, 2L, "A", "first");
            store.Remember("Z:One", 2L, 3L, "B", null);
            long baseline = (long)store.Find("Z:One")!.Baseline;
            store.Remember("z:one", 3L, 1L, "C", null); // case-insensitive; back at the baseline
            int count = store.Count;
            store.Remember("Z:Two", 5L, 6L, "A", null);
            bool f = store.Forget("Z:Two");
            return (baseline, count, f, store.Forget("Z:Two"), start);
        });

        Assert.Equal(1L, baselineAfterSecond);
        Assert.Equal(countStart, afterReturn);
        Assert.True(forgot);
        Assert.False(forgotAgain);
    }

    [Fact]
    public async Task OverridesAreKeptPerWorld()
    {
        await using var first = WorldTestHost.Start();
        await using var second = WorldTestHost.Start();
        OptionOverrides a = await first.OnWorldAsync(() => OptionOverrides.For(first.World));
        OptionOverrides b = await second.OnWorldAsync(() => OptionOverrides.For(second.World));

        Assert.NotSame(a, b);
    }

    // --- the write paths ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task SettingTheCurrentValue_IsOk_ButNeverAnOverride()
    {
        await using var host = WorldTestHost.Start();
        (OptionSetResult result, string before, string after, int diff) = await host.OnWorldAsync(() =>
        {
            OptionScope scope = Scope(host);
            var catalog = OptionCatalog.Create(scope);
            OptionKey key = catalog.Find("World:AutosaveIntervalMs")!;
            OptionSetResult r = catalog.TrySet(scope, key, host.World.Options.AutosaveIntervalMs.ToString(), Tester, out _, out string b, out string a);
            return (r, b, a, catalog.Diff(scope).Count);
        });

        Assert.Equal(OptionSetResult.Ok, result);
        Assert.Equal(before, after);
        Assert.Equal(0, diff);
    }

    [Fact]
    public async Task Find_TrimsTheName_AndReturnsNullForUnknownNames()
    {
        await using var host = WorldTestHost.Start();
        (bool trimmed, bool unknown, bool empty) = await host.OnWorldAsync(() =>
        {
            var catalog = OptionCatalog.Create(Scope(host));
            return (catalog.Find("  World:AutosaveIntervalMs ") is not null, catalog.Find("No:Such:Key") is null, catalog.Find(string.Empty) is null);
        });

        Assert.True(trimmed);
        Assert.True(unknown);
        Assert.True(empty);
    }

    [Fact]
    public async Task Match_FiltersByPathAliasAndDescription_CaseInsensitively_AndAllWhenEmpty()
    {
        await using var host = WorldTestHost.Start();
        (int all, int blank, int byPath, int byAlias, int byDescription, int none) = await host.OnWorldAsync(() =>
        {
            var catalog = OptionCatalog.Create(Scope(host));
            OptionKey kick = catalog.Find("AntiCheat:ScoreKick")!;
            return (
                catalog.Keys.Count,
                catalog.Match("   ").Count(),
                catalog.Match("scorekick").Count(k => k == kick),
                catalog.Match("anticheat.kick").Count(k => k == kick),
                catalog.Match("KICKS THE PLAYER").Count(k => k == kick),
                catalog.Match("zzzz-nothing-matches").Count());
        });

        Assert.Equal(all, blank);
        Assert.Equal(1, byPath);
        Assert.Equal(1, byAlias);
        Assert.Equal(1, byDescription);
        Assert.Equal(0, none);
    }

    [Fact]
    public async Task AntiCheat_ChoiceIsStoredCanonically_AndACrossCheckFailureChangesNothing()
    {
        await using var host = WorldTestHost.Start();
        (OptionSetResult badOrder, string error, float kickAfterRefusal, OptionSetResult choice, AntiCheatAction action, bool reset, AntiCheatAction restored, AntiCheatAction original) = await host.OnWorldAsync(() =>
        {
            OptionScope scope = Scope(host);
            AntiCheatFeature feature = host.WorldServices.GetRequiredService<AntiCheatFeature>();
            var catalog = OptionCatalog.Create(scope);
            AntiCheatAction first = feature.Options.Action;
            float kick = feature.Options.ScoreKick;
            // kick below rubberband breaks "must not decrease"
            OptionSetResult bad = catalog.TrySet(scope, catalog.Find("AntiCheat:ScoreKick")!, "1", Tester, out string e, out _, out _);
            float kickAfter = feature.Options.ScoreKick;
            Assert.Equal(kick, kickAfter);
            Assert.Empty(catalog.Diff(scope));

            AntiCheatAction target = first == AntiCheatAction.Log ? AntiCheatAction.GmAlert : AntiCheatAction.Log;
            OptionSetResult ok = catalog.TrySet(scope, catalog.Find("AntiCheat:Action")!, target.ToString().ToLowerInvariant(), Tester, out _, out _, out _);
            AntiCheatAction now = feature.Options.Action;
            bool wasReset = catalog.Reset(scope, catalog.Find("AntiCheat:Action")!, "Tester");
            return (bad, e, kickAfter, ok, now, wasReset, feature.Options.Action, first);
        });

        Assert.Equal(OptionSetResult.Invalid, badOrder);
        Assert.Contains("must not decrease", error, StringComparison.Ordinal);
        Assert.True(kickAfterRefusal > 1);
        Assert.Equal(OptionSetResult.Ok, choice);
        Assert.NotEqual(original, action);
        Assert.True(reset);
        Assert.Equal(original, restored);
    }

    [Fact]
    public async Task PacketHeuristics_EachSwitch_IsSetThroughTheCatalog_AndTheOldVerbsAreTracked()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await admin.CollectAsync();
        (bool enabled0, bool bots0, bool staff0) = await host.OnWorldAsync(() =>
        {
            PacketHeuristicsOptions o = host.WorldServices.GetRequiredService<PacketHeuristicsFeature>().Options;
            return (o.Enabled, o.ExemptBots, o.ExemptStaff);
        });

        (bool enabled1, bool bots1, bool staff1, int diff1) = await host.OnWorldAsync(() =>
        {
            OptionScope scope = Scope(host);
            var catalog = OptionCatalog.Create(scope);
            Assert.Equal(OptionSetResult.Ok, catalog.TrySet(scope, catalog.Find("packetcheck.enabled")!, (!enabled0).ToString(), Tester, out _, out _, out _));
            Assert.Equal(OptionSetResult.Ok, catalog.TrySet(scope, catalog.Find("packetcheck.exemptbots")!, (!bots0).ToString(), Tester, out _, out _, out _));
            Assert.Equal(OptionSetResult.Ok, catalog.TrySet(scope, catalog.Find("packetcheck.exemptstaff")!, (!staff0).ToString(), Tester, out _, out _, out _));
            PacketHeuristicsOptions o = host.WorldServices.GetRequiredService<PacketHeuristicsFeature>().Options; // the feature swaps its snapshot on every change
            return (o.Enabled, o.ExemptBots, o.ExemptStaff, catalog.Diff(scope).Count);
        });

        Assert.Equal(!enabled0, enabled1);
        Assert.Equal(!bots0, bots1);
        Assert.Equal(!staff0, staff1);
        Assert.Equal(3, diff1);

        await host.OnWorldAsync(() =>
        {
            OptionScope scope = Scope(host);
            Assert.Equal(3, OptionCatalog.Create(scope).ResetAll(scope, "Tester"));
        });
        Assert.Equal((enabled0, bots0, staff0), await host.OnWorldAsync(() =>
        {
            PacketHeuristicsOptions o = host.WorldServices.GetRequiredService<PacketHeuristicsFeature>().Options;
            return (o.Enabled, o.ExemptBots, o.ExemptStaff);
        }));

        // the old verbs: "bots on" includes the bots (ExemptBots false), "off" exempts them again; staff likewise; on/off flips Enabled.
        await admin.SendChatAsync(ChatType.Say, Language.Common, bots0 ? ".packetcheck bots on" : ".packetcheck bots off");
        await admin.SendChatAsync(ChatType.Say, Language.Common, staff0 ? ".packetcheck staff on" : ".packetcheck staff off");
        await admin.SendChatAsync(ChatType.Say, Language.Common, enabled0 ? ".packetcheck off" : ".packetcheck on");
        await admin.CollectAsync(TimeSpan.FromMilliseconds(100));

        string[] ReadTracked()
        {
            OptionScope scope = Scope(host);
            return OptionCatalog.Create(scope).Diff(scope).Select(s => s.Key.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        }

        // The three commands are handled on the world thread; poll (bounded) until all three keys are present instead of guessing a fixed delay.
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while ((await host.OnWorldAsync(ReadTracked)).Length < 3 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        string[] tracked = await host.OnWorldAsync(() =>
        {
            OptionScope scope = Scope(host);
            return OptionCatalog.Create(scope).Diff(scope).Select(s => s.Key.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        });
        Assert.Equal(
            ["World:PacketHeuristics:Enabled", "World:PacketHeuristics:ExemptBots", "World:PacketHeuristics:ExemptStaff"],
            tracked);
    }

    [Fact]
    public async Task Locomotion_SetThroughTheCatalog_ResendsSpeeds_RefusesOutOfRange_AndResetResendsTheBaseline()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await using WorldTestClient other = await host.EnterWorldAsync("OTHER", "Other");
        await admin.CollectAsync();
        await other.CollectAsync();

        OptionSetResult tooFast = await host.OnWorldAsync(() =>
        {
            OptionScope scope = Scope(host);
            var catalog = OptionCatalog.Create(scope);
            return catalog.TrySet(scope, catalog.Find("movement.run")!, "11", Tester, out _, out _, out _);
        });
        Assert.Equal(OptionSetResult.Invalid, tooFast);
        Assert.DoesNotContain(await other.CollectAsync(TimeSpan.FromMilliseconds(300)), p => p.Opcode == WorldOpcode.SmsgForceRunSpeedChange);

        await host.OnWorldAsync(() =>
        {
            OptionScope scope = Scope(host);
            var catalog = OptionCatalog.Create(scope);
            Assert.Equal(OptionSetResult.Ok, catalog.TrySet(scope, catalog.Find("movement.run")!, "2", Tester, out _, out _, out _));
        });
        Assert.Equal(7.0f * 2, ForcedSpeed(await other.ReadUntilAsync(WorldOpcode.SmsgForceRunSpeedChange)));

        await host.OnWorldAsync(() =>
        {
            OptionScope scope = Scope(host);
            var catalog = OptionCatalog.Create(scope);
            Assert.True(catalog.Reset(scope, catalog.Find("movement.run")!, "Tester"));
        });
        Assert.Equal(7.0f, ForcedSpeed(await other.ReadUntilAsync(WorldOpcode.SmsgForceRunSpeedChange)));
        Assert.Equal(1.0f, (await host.OnWorldAsync(() => LocomotionEnvironment.RegisteredOptions(host.World)!)).PlayerRunSpeedRate);
    }

    private static float ForcedSpeed(byte[] order)
    {
        var reader = new PacketReader(order.AsSpan(order.Length - 4));
        return reader.ReadSingle();
    }

    [Fact]
    public async Task ANextLoadRate_IsWritable_AndARefusedRateLeavesNoHistory()
    {
        await using var host = WorldTestHost.Start();
        (OptionSetResult ok, OptionSetResult refused, int history, bool overridden) = await host.OnWorldAsync(() =>
        {
            OptionScope scope = Scope(host);
            var catalog = OptionCatalog.Create(scope);
            OptionKey key = catalog.Find("Rate.Creature.Normal.HP")!;
            Assert.Equal(OptionApplyClass.NextLoad, key.Apply);
            Assert.True(key.Writable);
            OptionSetResult bad = catalog.TrySet(scope, catalog.Find("Rate.XP.Kill")!, "-1", Tester, out _, out _, out _);
            ServerRates rates = ServerRates.For(host.World);
            int before = rates.History.Count;
            OptionSetResult good = catalog.TrySet(scope, key, "2", Tester, out _, out _, out _);
            return (good, bad, before, rates.IsOverridden(ServerRates.Find("Rate.Creature.Normal.HP")!));
        });

        Assert.Equal(OptionSetResult.Ok, ok);
        Assert.Equal(OptionSetResult.Invalid, refused);
        Assert.Equal(0, history);
        Assert.True(overridden);
    }

    [Fact]
    public async Task AReadNeverWritesTheStore_AStaleOverrideIsIgnored_AndTheNextChangeStartsFromTheValueThenInForce()
    {
        await using var host = WorldTestHost.Start();
        (bool staleShown, int storeAfterRead, long freshBaseline, int diffAfterReturn) = await host.OnWorldAsync(() =>
        {
            OptionScope scope = Scope(host);
            var catalog = OptionCatalog.Create(scope);
            OptionKey key = catalog.Find("World:AutosaveIntervalMs")!;
            int original = host.World.Options.AutosaveIntervalMs;
            Assert.Equal(OptionSetResult.Ok, catalog.TrySet(scope, key, (original + 1).ToString(), Tester, out _, out _, out _));

            host.World.Options.AutosaveIntervalMs = original + 5; // another path changed the key: the entry is stale
            bool shown = catalog.Describe(scope, key).Overridden;
            int count = OptionOverrides.For(host.World).Count;

            Assert.Equal(OptionSetResult.Ok, catalog.TrySet(scope, key, (original + 9).ToString(), Tester, out _, out _, out _));
            long baseline = (long)OptionOverrides.For(host.World).Find(key.Path)!.Baseline;
            host.World.Options.AutosaveIntervalMs = original;
            Assert.Equal(OptionSetResult.Ok, catalog.TrySet(scope, key, original.ToString(), Tester, out _, out _, out _));
            return (shown, count, baseline - original, catalog.Diff(scope).Count);
        });

        Assert.False(staleShown);
        Assert.Equal(1, storeAfterRead);
        Assert.Equal(5, freshBaseline);
        Assert.Equal(0, diffAfterReturn);
    }

    [Fact]
    public async Task ARateSetThroughTheEditor_IsLoggedOnce_ByTheRatesOwn()
    {
        await using var host = WorldTestHost.Start();
        var logger = new CapturingLogger();
        await host.OnWorldAsync(() =>
        {
            OptionScope scope = new(host.WorldServices, host.World, logger);
            var catalog = OptionCatalog.Create(scope);
            Assert.Equal(OptionSetResult.Ok, catalog.TrySet(scope, catalog.Find("Rate.XP.Kill")!, "2", Tester, out _, out _, out _));
        });

        Assert.Single(logger.Lines, l => l.Contains("Rate.XP.Kill", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResetAll_CountsEveryOverriddenKey_AcrossSources()
    {
        await using var host = WorldTestHost.Start();
        (int diff, int reset, int after, bool xpBack) = await host.OnWorldAsync(() =>
        {
            OptionScope scope = Scope(host);
            var catalog = OptionCatalog.Create(scope);
            Assert.Equal(OptionSetResult.Ok, catalog.TrySet(scope, catalog.Find("World:AutosaveIntervalMs")!, "31337", Tester, out _, out _, out _));
            Assert.Equal(OptionSetResult.Ok, catalog.TrySet(scope, catalog.Find("xp")!, "3", Tester, out _, out _, out _));
            Assert.Equal(OptionSetResult.Ok, catalog.TrySet(scope, catalog.Find("anticheat.decay")!, "9", Tester, out _, out _, out _));
            int changed = catalog.Diff(scope).Count;
            int count = catalog.ResetAll(scope, "Tester");
            return (changed, count, catalog.Diff(scope).Count, !ServerRates.For(host.World).IsOverridden(ServerRates.Find("xp")!));
        });

        Assert.Equal(3, diff);
        Assert.Equal(3, reset);
        Assert.Equal(0, after);
        Assert.True(xpBack);
    }

    // --- packs -------------------------------------------------------------------------------------------------------

    private sealed class ProviderOverlay(IServiceProvider inner, params IOptionSource[] extra) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IEnumerable<IOptionSource>) ? extra : inner.GetService(serviceType);
    }

    private sealed class CountingPack : IOptionSource
    {
        public int Writes { get; private set; }

        public long Level { get; private set; } = 3;

        public string Token { get; private set; } = "old-secret-value";

        public bool Online { get; set; }

        public void RegisterOptions(IOptionRegistry registry)
        {
            registry.AddInteger("Edge:Pack:Level", "a level", OptionApplyClass.Live, 1, 10, () => Level, v => { Writes++; Level = v; });
            registry.AddText("Edge:Pack:ApiToken", "a credential", OptionApplyClass.Live, () => Token, v => { Writes++; Token = v; });
            registry.AddBool("Edge:Pack:Remote", "only when connected", OptionApplyClass.Live, () => Online ? true : null, _ => Writes++);
            registry.AddBool("Edge:Pack:NoWriter", "live but no writer", OptionApplyClass.Live, () => true);
        }
    }

    private sealed class ThrowingPack : IOptionSource
    {
        public void RegisterOptions(IOptionRegistry registry) => throw new InvalidOperationException("bad pack");
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
    }

    [Fact]
    public async Task APackKey_RefusedBeforeItsWriter_DoesNotCallIt_AndAWriterlessOrOfflineKeyIsReadOnlyOrUnavailable()
    {
        await using var host = WorldTestHost.Start();
        var pack = new CountingPack();
        (OptionSetResult tooLow, OptionSetResult noWriter, OptionSetResult offline, bool offlineAvailable, int writes, bool noWriterWritable) = await host.OnWorldAsync(() =>
        {
            OptionScope scope = new(new ProviderOverlay(host.WorldServices, pack), host.World, NullLogger.Instance);
            var catalog = OptionCatalog.Create(scope);
            OptionSetResult low = catalog.TrySet(scope, catalog.Find("Edge:Pack:Level")!, "0", Tester, out _, out _, out _);
            OptionKey nw = catalog.Find("Edge:Pack:NoWriter")!;
            OptionSetResult noW = catalog.TrySet(scope, nw, "false", Tester, out string noWriterError, out _, out _);
            Assert.Contains("cannot be changed here", noWriterError, StringComparison.Ordinal);
            OptionKey remote = catalog.Find("Edge:Pack:Remote")!;
            OptionSetResult off = catalog.TrySet(scope, remote, "true", Tester, out string offlineError, out _, out _);
            Assert.Contains("is not available", offlineError, StringComparison.Ordinal);
            return (low, noW, off, catalog.Describe(scope, remote).Available, pack.Writes, nw.Writable);
        });

        Assert.Equal(OptionSetResult.Invalid, tooLow);
        Assert.Equal(OptionSetResult.ReadOnly, noWriter);
        Assert.Equal(OptionSetResult.NotAvailable, offline);
        Assert.False(offlineAvailable);
        Assert.False(noWriterWritable);
        Assert.Equal(0, pack.Writes);
        Assert.Equal(3, pack.Level);
    }

    [Fact]
    public async Task ASecretSet_IsRefused_AndTheValueIsNeverPrintedOrLogged()
    {
        // A writable secret would reach the GM audit as typed (the command line is logged), so the editor refuses to write one.
        await using var host = WorldTestHost.Start();
        var pack = new CountingPack();
        var logger = new CapturingLogger();
        (OptionSetResult result, string error, string shown, string described, bool writable) = await host.OnWorldAsync(() =>
        {
            OptionScope scope = new(new ProviderOverlay(host.WorldServices, pack), host.World, logger);
            var catalog = OptionCatalog.Create(scope);
            OptionKey key = catalog.Find("Edge:Pack:ApiToken")!;
            OptionSetResult r = catalog.TrySet(scope, key, "brand-new-secret-value", Tester, out string e, out _, out _);
            OptionStatus status = catalog.Describe(scope, key);
            return (r, e, key.Format(status.Current), key.Format(status.Baseline), key.Writable);
        });

        Assert.Equal(OptionSetResult.ReadOnly, result);
        Assert.False(writable);
        Assert.Contains("secret", error, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", error, StringComparison.Ordinal);
        Assert.Equal("old-secret-value", pack.Token);
        Assert.Equal("********", shown);
        Assert.Equal("********", described);
        Assert.DoesNotContain(logger.Lines, l => l.Contains("secret-value", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AProviderThatThrows_IsLogged_AndTheOtherSourcesStillLoad()
    {
        await using var host = WorldTestHost.Start();
        var logger = new CapturingLogger();
        (int keys, bool hasBuiltIn, bool hasPack) = await host.OnWorldAsync(() =>
        {
            OptionScope scope = new(new ProviderOverlay(host.WorldServices, new ThrowingPack(), new CountingPack()), host.World, logger);
            var catalog = OptionCatalog.Create(scope);
            return (catalog.Keys.Count, catalog.Find("World:AutosaveIntervalMs") is not null, catalog.Find("Edge:Pack:Level") is not null);
        });

        Assert.True(keys > 100);
        Assert.True(hasBuiltIn);
        Assert.True(hasPack);
        Assert.Contains(logger.Lines, l => l.Contains("ThrowingPack", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADuplicatePackPath_IsReportedAsAConflict_AndTheFirstKeyWins()
    {
        await using var host = WorldTestHost.Start();
        (IReadOnlyList<string> conflicts, string description) = await host.OnWorldAsync(() =>
        {
            OptionScope scope = new(new ProviderOverlay(host.WorldServices, new CountingPack(), new CountingPack()), host.World, NullLogger.Instance);
            var catalog = OptionCatalog.Create(scope);
            return (catalog.Conflicts, catalog.Find("Edge:Pack:Level")!.Description);
        });

        Assert.Contains("Edge:Pack:Level", conflicts);
        Assert.Equal("a level", description);
    }

    // --- bounds of the reload keys -----------------------------------------------------------------------------------

    [Fact]
    public async Task TheReloadKeys_ShowTheBoundsTheirOwnCheckEnforces_AndStillRefuseWithTheCheckMessage()
    {
        await using var host = WorldTestHost.Start();
        (string speed, string autosave, OptionSetResult result, string error, bool enforced) = await host.OnWorldAsync(() =>
        {
            OptionScope scope = Scope(host);
            var catalog = OptionCatalog.Create(scope);
            OptionKey run = catalog.Find("Locomotion:PlayerRunSpeedRate")!;
            OptionKey save = catalog.Find("World:AutosaveIntervalMs")!;
            OptionSetResult r = catalog.TrySet(scope, run, "11", Tester, out string message, out _, out _);
            return (run.BoundsText, save.BoundsText, r, message, run.BoundsEnforced);
        });

        Assert.Equal("0.1..10", speed);
        Assert.Equal(">= 0", autosave);
        Assert.Equal(OptionSetResult.Invalid, result);
        Assert.Contains("must be between", error, StringComparison.Ordinal);
        Assert.False(enforced);
    }

    [Fact]
    public void ProbeBounds_FindsTheEdgesOfARange_AndGivesNothingForACheckThatIsNotARange()
    {
        Func<object?, string?> range = v => (int)v! is >= 3 and <= 40 ? null : "no";
        Func<object?, string?> even = v => (int)v! % 2 == 0 ? null : "odd";
        Func<object?, string?> positive = v => (float)v! > 0f ? null : "no";
        Func<object?, string?> rate = v => (float)v! is >= 0.1f and <= 10f ? null : "no";

        Assert.Equal((3d, 40d), WorldConfigKeySource.ProbeBounds(range, typeof(int), OptionType.Integer, 10));
        Assert.Equal((double.NaN, double.NaN), WorldConfigKeySource.ProbeBounds(even, typeof(int), OptionType.Integer, 10));
        Assert.Equal((double.NaN, double.NaN), WorldConfigKeySource.ProbeBounds(null, typeof(int), OptionType.Integer, 10));
        Assert.Equal((double.NaN, double.NaN), WorldConfigKeySource.ProbeBounds(range, typeof(int), OptionType.Integer, 99)); // the seed is refused
        Assert.Equal((0.1d, 10d), WorldConfigKeySource.ProbeBounds(rate, typeof(float), OptionType.Number, 1));
        (double min, double max) = WorldConfigKeySource.ProbeBounds(positive, typeof(float), OptionType.Number, 1);
        Assert.InRange(min, 0d, 1e-6);
        Assert.True(double.IsNaN(max));
    }
}
