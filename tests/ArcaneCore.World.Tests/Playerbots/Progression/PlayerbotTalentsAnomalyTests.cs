using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Talents;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Progression;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Talents;
using ArcaneCore.World.Tests.Npc;
using ArcaneCore.World.Tests.Talents;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Talents.TalentWorldFixture;

namespace ArcaneCore.World.Tests.Playerbots.Progression;

/// <summary>
/// H1 hardening: <c>bot-progression/talent-refused</c>, reported when the server's CMSG_LEARN_TALENT handler did not teach the rank the bot
/// asked for (no reply packet exists, so the bot reads its spellbook). Same synthetic warrior catalog as <see cref="PlayerbotTalentsWorldTests"/>.
/// </summary>
public sealed class PlayerbotTalentsAnomalyTests
{
    private static readonly string TalentRefused = PlayerbotAnomalies.Key("progression", "talent-refused");

    private static WorldTestHost Start()
    {
        TalentTestServices.Current.Value = new TalentWorldFixture();
        QuestInteractionTestServices.Current.Value = new QuestInteractionFixture();
        try
        {
            return WorldTestHost.Start();
        }
        finally
        {
            TalentTestServices.Current.Value = null;
            QuestInteractionTestServices.Current.Value = null;
        }
    }

    [Fact]
    public async Task TalentRefused_Fires_OnceForTheRefusedRank_AndNotForTheNextTalentTheBotTakes()
    {
        await using WorldTestHost host = Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            TalentService talents = host.WorldServices.GetRequiredService<TalentFeature>().Service!;
            SpellSystem spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                player.Level = 10;
                talents.InitTalentForLevel(player);
                Assert.Equal(1u, talents.FreePoints(player));
                using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);
                var veto = new Veto(T1R1);
                spells.AddLearnObserver(veto);
                var upkeep = new PlayerbotEquipment(session);

                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(upkeep.Update(player));
                Assert.Equal(1, veto.Vetoed);
                Assert.Equal(1, capture.Count(TalentRefused));
                AnomalyNotice notice = capture.Notices.Single(n => n.Key == TalentRefused);
                Assert.Equal(AnomalySeverity.Warning, notice.Severity);
                Assert.Contains("refused", notice.Detail, StringComparison.Ordinal);

                // The refusal is remembered: the next think asks for another talent, which is taught, and reports nothing more.
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(upkeep.Update(player));
                Assert.True(talents.HasSpell(player, P1));
                Assert.Equal(1, capture.Count(TalentRefused));
                spells.RemoveLearnObserver(veto);
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task TalentRefused_Silent_WhenEveryRequestedRankIsTaught_AndWhenNoPointIsFree()
    {
        await using WorldTestHost host = Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            TalentService talents = host.WorldServices.GetRequiredService<TalentFeature>().Service!;
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                player.Level = 12;
                talents.InitTalentForLevel(player);
                Assert.Equal(3u, talents.FreePoints(player));
                using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);
                var upkeep = new PlayerbotEquipment(session);
                for (uint spent = 1; spent <= 3; spent++)
                {
                    session.ManagedBudget = new ManagedActionBudget(1);
                    Assert.True(upkeep.Update(player));
                    Assert.Equal(spent, talents.UsedPoints(player));
                }

                // All points spent: the bot asks for nothing, so nothing can be refused.
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.False(upkeep.Update(player));
                Assert.False(capture.Any(TalentRefused), capture.Describe());
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    private sealed class Veto(uint spell) : ISpellLearnObserver
    {
        public int Vetoed { get; private set; }

        public bool BeforeLearn(Player player, uint spellId)
        {
            if (spellId != spell) return true;
            Vetoed++;
            return false;
        }
    }
}
