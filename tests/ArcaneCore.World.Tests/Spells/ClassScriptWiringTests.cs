using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.ClassScripts;
using ArcaneCore.Game.Spells.Paladin;
using ArcaneCore.Game.Spells.Shaman;
using ArcaneCore.Game.Spells.Warlock;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Spells.Utility;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

/// <summary>The class scripts are installed on the production world spell system (docs/areas/class-scripts.md).</summary>
public sealed class ClassScriptWiringTests
{
    [Fact]
    public async Task TheWorldSpellSystem_CarriesTheClassScripts()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await host.OnWorldAsync(() =>
        {
            SpellSystem spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
            var scripts = host.WorldServices.GetRequiredService<SpellScriptFeature>().Dispatcher!.Registry;

            Assert.IsType<JudgementScript>(scripts.Find(PaladinSpells.Judgement));
            Assert.IsType<HolyShockScript>(scripts.Find(20473));
            Assert.IsType<HolyLightScript>(scripts.Find(635));
            Assert.IsType<CurseOfDoomScript>(scripts.Find(CurseOfDoomScript.CurseOfDoom));
            Assert.IsType<FlametongueProcScript>(scripts.Find(8026));
            Assert.IsType<RockbiterProcScript>(scripts.Find(20865));
            Assert.IsType<ExecuteDummyScript>(scripts.Find(5308));
            Assert.IsType<ColdSnapScript>(scripts.Find(12472));

            Assert.IsType<SealOfRighteousnessProc>(spells.FindProcScript(21084));
            Assert.IsType<JudgementOfLightWisdomProc>(spells.FindProcScript(20185));
            Assert.IsType<ConsecrationScript>(spells.FindPeriodicDamageScript(26573));
            Assert.IsType<CurseOfDoomScript>(spells.FindPeriodicDamageScript(CurseOfDoomScript.CurseOfDoom));
            Assert.True(spells.HasEffectHandler(SpellEffectName.PersistentAreaAura));
            Assert.True(spells.HasEffectHandler(SpellEffectName.SummonDemon));
            Assert.NotNull(host.WorldServices.GetRequiredService<RogueScriptFeature>().Scripts);
        });
    }
}
