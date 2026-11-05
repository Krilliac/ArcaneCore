using ArcaneCore.Game.Instances.Scripts.Naxxramas;
using Xunit;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>
/// Entry guards of the Naxxramas clean-up spell script: a despawn spell only removes the adds listed for it in the ClassicDB spell_script_target
/// rows (mangos-classic boss_anubrekhan.cpp DespawnCryptGuards, boss_grobbulus.cpp DespawnBossAdds, boss_noth.cpp DespawnSummonsNoth).
/// </summary>
public sealed class NaxxramasScriptTests
{
    [Theory]
    [InlineData(NaxxramasDespawnSummonsScript.DespawnCryptGuards, 16573u, true)]
    [InlineData(NaxxramasDespawnSummonsScript.DespawnCryptGuards, 16698u, true)]
    [InlineData(NaxxramasDespawnSummonsScript.DespawnCryptGuards, 16363u, false)]
    [InlineData(NaxxramasDespawnSummonsScript.DespawnBossAdds, 16363u, true)]
    [InlineData(NaxxramasDespawnSummonsScript.DespawnBossAdds, 16290u, true)]
    [InlineData(NaxxramasDespawnSummonsScript.DespawnBossAdds, 15931u, false)]
    [InlineData(NaxxramasDespawnSummonsScript.DespawnSummons, 16981u, true)]
    [InlineData(NaxxramasDespawnSummonsScript.DespawnSummons, 16984u, true)]
    [InlineData(NaxxramasDespawnSummonsScript.DespawnSummons, 16985u, false)]
    [InlineData(NaxxramasDespawnSummonsScript.DespawnSummons, 15954u, false)]
    [InlineData(12345u, 16573u, false)]
    public void DespawnSummonsScript_OnlyAcceptsTheListedAddsOfEachSpell(uint spellId, uint entry, bool listed)
        => Assert.Equal(listed, NaxxramasDespawnSummonsScript.IsListedAdd(spellId, entry));
}
