using ArcaneCore.Game.Spells;
using ArcaneCore.World.Playerbots.Qa;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>A transport that records the issues it is asked to create and finds none.</summary>
internal sealed class RecordingTransport : IQaIssueTransport
{
    private int _next = 500;

    public List<(string Title, string Body, IReadOnlyList<string> Labels)> Created { get; } = [];

    public bool SupportsSearch => false;

    public Task<QaIssueIndex> ListIndexAsync(CancellationToken cancellationToken) => Task.FromResult(new QaIssueIndex(complete: true));

    public Task<(QaIssueRef Issue, string Body)?> GetIssueAsync(int number, CancellationToken cancellationToken) => Task.FromResult<(QaIssueRef, string)?>(null);

    public Task<QaIssueRef?> SearchByMarkerAsync(string id, CancellationToken cancellationToken) => Task.FromResult<QaIssueRef?>(null);

    public Task<QaIssueRef> CreateIssueAsync(string title, string body, IReadOnlyList<string> labels, CancellationToken cancellationToken)
    {
        lock (Created) Created.Add((title, body, labels));
        int number = Interlocked.Increment(ref _next);
        return Task.FromResult(new QaIssueRef(number, true, $"https://github.com/{QaData.Repo}/issues/{number}"));
    }

    public Task CommentAsync(int number, string body, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ReopenAsync(int number, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CloseAsDuplicateAsync(int number, CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose()
    {
    }
}

/// <summary>
/// Each spell kind end to end: a bot's cast through the spell system, the QA spell monitor, the direct sink and the signal queue, into a finding of the
/// feature, and out as the issue the publisher creates (with the spell section, the reproduction and the id reference).
/// </summary>
public sealed class PlayerbotQaSpellFeatureTests : IDisposable
{
    private readonly string _directory = QaData.NewTempDirectory();

    public void Dispose() => QaData.DeleteQuietly(_directory);

    private (PlayerbotQaFeature Feature, IPlayerbotQaSink Sink, RecordingTransport Transport, ServiceProvider Services) Feature(Action<PlayerbotQaGitHubOptions>? configure = null)
    {
        var options = new PlayerbotQaOptions { Directory = _directory, GitHub = { Enabled = true, Publish = true, Repository = QaData.Repo } };
        configure?.Invoke(options.GitHub);
        var transport = new RecordingTransport();
        ServiceProvider services = new ServiceCollection()
            .AddSingleton<IOptions<PlayerbotQaOptions>>(Options.Create(options))
            .AddSingleton(new PlayerbotQaSignalBus(256))
            .AddSingleton<IPlayerbotQaSink>(sp => new PlayerbotQaSink(sp.GetRequiredService<PlayerbotQaSignalBus>(), sp.GetRequiredService<IOptions<PlayerbotQaOptions>>()))
            .BuildServiceProvider();
        var feature = new PlayerbotQaFeature(services) { EnvironmentReader = _ => QaData.FakeToken, TransportFactory = _ => transport };
        feature.Attach(null!);
        return (feature, services.GetRequiredService<IPlayerbotQaSink>(), transport, services);
    }

    private static async Task PassAsync(PlayerbotQaFeature feature)
    {
        feature.RunPass(CancellationToken.None);
        await feature.Publishing.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task SpellUnsupported_OneBotCast_BecomesAnIssue_WithTheSpellSection()
    {
        var (feature, sink, transport, services) = Feature();
        using (services)
        {
            using var kit = new QaSpellKit(sink);
            kit.Cast(kit.Bot, QaSpellKit.UnsupportedAura);

            Assert.Equal(1, feature.Drain());
            await PassAsync(feature);

            QaFindingView finding = Assert.Single(feature.Aggregator!.Snapshot());
            Assert.Equal((QaSignalKind.SpellUnsupported, QaCause.NotImplemented, 1L), (finding.Kind, finding.Cause, finding.Count));
            (string title, string body, _) = Assert.Single(transport.Created);
            Assert.Equal($"[bot-qa] spell unsupported: spell 990002 Spell 990002 (Rank 2) effect 0 aura {kit.UnsupportedAuraType} not implemented", title);
            Assert.Contains("## Spell\n", body, StringComparison.Ordinal);
            Assert.Contains("| Spell | 990002 Spell 990002 (Rank 2) |", body, StringComparison.Ordinal);
            Assert.Contains("| Effect | #0 ApplyAura, value 3 |", body, StringComparison.Ordinal);
            Assert.Contains($"| Aura type | {(uint)kit.UnsupportedAuraType} {kit.UnsupportedAuraType} |", body, StringComparison.Ordinal);
            Assert.Contains("| Classes | Mage |", body, StringComparison.Ordinal);
            Assert.Contains("`.learn 990002`", body, StringComparison.Ordinal);
            Assert.Contains("`.cast 990002`", body, StringComparison.Ordinal);
            Assert.Contains("Wowhead classic spell=990002", body, StringComparison.Ordinal);
            await feature.StopAsync();
        }
    }

    [Fact]
    public async Task SpellRefused_ASuspiciousResultRepeated_BecomesAnIssue()
    {
        var (feature, sink, transport, services) = Feature(git => git.MinDistinctBots = 1); // the kit has one bot
        using (services)
        {
            using var kit = new QaSpellKit(sink);
            for (int i = 0; i < 3; i++)
            {
                kit.Monitor.OnCastRefused(kit.Bot, QaSpellKit.SelfHeal, SpellCastResult.Error);
                kit.NowMs += PlayerbotQaSpellMonitor.ThrottleMs;
            }

            Assert.Equal(3, feature.Drain());
            await PassAsync(feature);

            QaFindingView finding = Assert.Single(feature.Aggregator!.Snapshot());
            Assert.Equal((QaSignalKind.SpellRefused, QaCause.NotImplemented, 3L, "Error"), (finding.Kind, finding.Cause, finding.Count, finding.Reason));
            (string title, string body, _) = Assert.Single(transport.Created);
            Assert.Equal("[bot-qa] spell refused: spell 990007 Spell 990007 (Rank 2) refused: Error", title);
            Assert.Contains("| Cast result | Error |", body, StringComparison.Ordinal);
            Assert.Contains("SPELL_FAILED_ERROR", body, StringComparison.Ordinal);
            await feature.StopAsync();
        }
    }

    [Fact]
    public async Task SpellNoEffect_RepeatedHitsThatDoNothing_BecomeAnIssue()
    {
        var (feature, sink, transport, services) = Feature(git => git.MinDistinctBots = 1);
        using (services)
        {
            using var kit = new QaSpellKit(sink, brokenHeal: true);
            kit.Bot.Health = 30;
            for (int report = 0; report < 3; report++)
            {
                for (int i = 0; i < PlayerbotQaSpellMonitor.NoEffectRepeats; i++) kit.Cast(kit.Bot, QaSpellKit.SelfHeal);
                kit.NowMs += PlayerbotQaSpellMonitor.ThrottleMs;
            }

            Assert.Equal(3, feature.Drain());
            await PassAsync(feature);

            QaFindingView finding = Assert.Single(feature.Aggregator!.Snapshot());
            Assert.Equal((QaSignalKind.SpellNoEffect, QaCause.NotImplemented, 3L), (finding.Kind, finding.Cause, finding.Count));
            (string title, string body, _) = Assert.Single(transport.Created);
            Assert.Equal("[bot-qa] spell no effect: spell 990007 Spell 990007 (Rank 2) lands with no damage, healing or aura", title);
            Assert.Contains("expected healing", body, StringComparison.Ordinal);
            Assert.Contains("`.cast 990007`", body, StringComparison.Ordinal);
            await feature.StopAsync();
        }
    }

    /// <summary>
    /// GitHub review of #w22-qa-spells: a Battle Stance warrior casts Overpower unconditionally, the server refuses it with BadTargets at any target
    /// without combo points (ComboPointService, vmangos Spell.cpp:7035-7038), and the bot retries every 4 s. Ten minutes of that from two warriors, each
    /// on its own rank, opens nothing.
    /// </summary>
    [Fact]
    public async Task AWarriorsOverpowerRefusalStream_PublishesNothing()
    {
        var (feature, sink, transport, services) = Feature();
        using (services)
        {
            SpellInfo[] overpower =
            [
                QaSpellKit.Spell(7384, QaSpellKit.Effect(SpellEffectName.WeaponDamage, 5, SpellImplicitTarget.UnitEnemy)) with { Name = "Overpower", Rank = "Rank 1" },
                QaSpellKit.Spell(11585, QaSpellKit.Effect(SpellEffectName.WeaponDamage, 35, SpellImplicitTarget.UnitEnemy)) with { Name = "Overpower", Rank = "Rank 4" },
            ];
            using var kit = new QaSpellKit(sink, extra: overpower);
            var warriors = new PlayerbotQaSpellMonitor(sink, kit.System,
                unit => unit is ArcaneCore.Game.Entities.Player p ? new QaBot(PlayerbotQaBots.KeyOf(p.Guid), (byte)ArcaneCore.Game.Class.Warrior) : null,
                () => kit.NowMs);
            for (long at = 0; at < 10 * 60_000; at += PlayerbotCombatSpellsBackoff)
            {
                warriors.OnCastRefused(kit.Bot, 7384, SpellCastResult.BadTargets);
                warriors.OnCastRefused(kit.Human, 11585, SpellCastResult.BadTargets);
                kit.NowMs += PlayerbotCombatSpellsBackoff;
            }

            feature.Drain();
            await PassAsync(feature);

            Assert.Empty(transport.Created);
            Assert.DoesNotContain(feature.Aggregator!.Snapshot(), f => f.Kind == QaSignalKind.SpellRefused);
            await feature.StopAsync();
        }
    }

    private const long PlayerbotCombatSpellsBackoff = ArcaneCore.World.Playerbots.PlayerbotCombatSpells.RefusalBackoffMs;

    [Fact]
    public async Task WithQaDisabled_TheSinkIsInert_AndNothingIsQueued()
    {
        var bus = new PlayerbotQaSignalBus(64);
        var sink = new PlayerbotQaSink(bus, Options.Create(new PlayerbotQaOptions { Enabled = false }));
        using var kit = new QaSpellKit(sink);

        kit.Cast(kit.Bot, QaSpellKit.UnsupportedAura); // the monitor still sees the bot, the sink drops the report

        Assert.Equal(0, bus.Received);
        await Task.CompletedTask;
    }
}
