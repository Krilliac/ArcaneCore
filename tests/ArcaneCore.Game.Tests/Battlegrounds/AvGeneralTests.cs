using ArcaneCore.Game.Battlegrounds.AlteracValleyScripts;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.Battlegrounds.AvScriptRig;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.Battlegrounds;

/// <summary>The Alterac Valley generals and war masters (vmangos battleground_alterac.cpp:71-1148) on the manual world clock.</summary>
public sealed class AvGeneralTests
{
    private const uint WarMaster = 14762;

    private static AvScriptRig Rig(uint entry, float x, float y, params (uint Entry, float X, float Y)[] others)
    {
        List<uint> entries = [entry, .. others.Select(o => o.Entry).Distinct()];
        List<CreatureSpawn> spawns = [Spawn(1, entry, x, y, Z)];
        uint guid = 2;
        foreach ((uint e, float ox, float oy) in others)
        {
            spawns.Add(Spawn(guid++, e, ox, oy, Z));
        }

        return Create([.. entries.Select(e => AvNpc(e, b => b.Faction = 14))], spawns, x + 5, y, hostility: new AlwaysHostile());
    }

    [Fact]
    public void Vanndar_LeashedPast35Yards_ResetsFullyAndSaysTheResetLine()
    {
        using AvScriptRig rig = Rig(AvVanndarAI.EntryVanndar, AvVanndarAI.CenterX, AvVanndarAI.CenterY);
        Creature vanndar = rig.Single(AvVanndarAI.EntryVanndar);
        Assert.IsType<AvVanndarAI>(vanndar.AI);
        rig.Run(200);

        vanndar.AI!.AttackStart(rig.Player);
        rig.Run(300);
        Assert.True(vanndar.Combat.IsInCombat);

        vanndar.Health = vanndar.MaxHealth / 2;
        rig.Session.Clear();
        vanndar.Relocate(AvVanndarAI.CenterX + 40f, AvVanndarAI.CenterY, Z, 0, 0);
        rig.Run(300);

        Assert.False(vanndar.Combat.IsInCombat);
        Assert.Equal(vanndar.MaxHealth, vanndar.Health);
        Assert.Contains(AvVanndarAI.SayReset, rig.Said());
    }

    [Fact]
    public void Vanndar_InsideTheLeash_StaysInTheFight()
    {
        using AvScriptRig rig = Rig(AvVanndarAI.EntryVanndar, AvVanndarAI.CenterX, AvVanndarAI.CenterY);
        Creature vanndar = rig.Single(AvVanndarAI.EntryVanndar);
        rig.Run(200);
        vanndar.AI!.AttackStart(rig.Player);
        vanndar.Relocate(AvVanndarAI.CenterX + 30f, AvVanndarAI.CenterY, Z, 0, 0);
        rig.Run(500);
        Assert.True(vanndar.Combat.IsInCombat);
    }

    [Fact]
    public void Vanndar_PullsTheLinkedWarMasters_WithHisThreatList()
    {
        using AvScriptRig rig = Rig(AvVanndarAI.EntryVanndar, AvVanndarAI.CenterX, AvVanndarAI.CenterY,
            (WarMaster, AvVanndarAI.CenterX + 20f, AvVanndarAI.CenterY));
        Creature vanndar = rig.Single(AvVanndarAI.EntryVanndar);
        Creature marshal = rig.Single(WarMaster);
        Assert.IsType<AvWarMasterAI>(marshal.AI);
        rig.Run(200);
        Assert.False(marshal.Combat.IsInCombat);

        vanndar.AI!.AttackStart(rig.Player);
        rig.Run(600);

        Assert.True(marshal.Combat.IsInCombat);
        Assert.Same(rig.Player, marshal.Combat.Victim);
    }

    [Fact]
    public void Vanndar_CastsStormboltThunderclapAndAvatarOnTheManualClock()
    {
        using AvScriptRig rig = Rig(AvVanndarAI.EntryVanndar, AvVanndarAI.CenterX, AvVanndarAI.CenterY);
        Creature vanndar = rig.Single(AvVanndarAI.EntryVanndar);
        rig.Run(200);
        vanndar.AI!.AttackStart(rig.Player);

        rig.RunUntil(() => rig.Caster.Casts.Any(c => c.Spell == AvVanndarAI.SpellThunderclap), "Thunderclap (5 s)", 7000);
        Assert.DoesNotContain(rig.Caster.Casts, c => c.Spell == AvVanndarAI.SpellStormbolt);
        rig.RunUntil(() => rig.Caster.Casts.Any(c => c.Spell == AvVanndarAI.SpellStormbolt), "Stormbolt (8 s)", 6000);
        Assert.Contains(rig.Caster.Casts, c => c.Spell == AvVanndarAI.SpellStormbolt && ReferenceEquals(c.Target, rig.Player));
        rig.RunUntil(() => rig.Caster.Casts.Any(c => c.Spell == AvVanndarAI.SpellAvatar), "Avatar (12-20 s)", 20_000);
        Assert.Contains(rig.Caster.Casts, c => c.Spell == AvVanndarAI.SpellAvatar && ReferenceEquals(c.Target, vanndar));
    }

    [Fact]
    public void Galvangar_CastsCleaveAndMortalStrike_AndLeashesPast45Yards()
    {
        using AvScriptRig rig = Rig(AvGalvangarAI.EntryGalvangar, AvGalvangarAI.CenterX, AvGalvangarAI.CenterY);
        Creature galvangar = rig.Single(AvGalvangarAI.EntryGalvangar);
        rig.Run(200);
        galvangar.AI!.AttackStart(rig.Player);

        rig.RunUntil(() => rig.Caster.Casts.Any(c => c.Spell == AvGalvangarAI.SpellCleave), "Cleave (4 s)", 6000);
        rig.RunUntil(() => rig.Caster.Casts.Any(c => c.Spell == AvGalvangarAI.SpellMortalStrike), "Mortal Strike (7 s)", 8000);

        galvangar.Health = galvangar.MaxHealth / 2;
        galvangar.Relocate(AvGalvangarAI.CenterX + 50f, AvGalvangarAI.CenterY, Z, 0, 0);
        rig.Run(300);
        Assert.False(galvangar.Combat.IsInCombat);
        Assert.Equal(galvangar.MaxHealth, galvangar.Health);
    }

    [Fact]
    public void DrekThar_CastsWhirlwindAndKnockdown()
    {
        using AvScriptRig rig = Rig(AvDrekTharAI.EntryDrekThar, AvDrekTharAI.CenterX, AvDrekTharAI.CenterY);
        Creature drekThar = rig.Single(AvDrekTharAI.EntryDrekThar);
        rig.Run(200);
        drekThar.AI!.AttackStart(rig.Player);
        rig.RunUntil(() => rig.Caster.Casts.Any(c => c.Spell == AvDrekTharAI.SpellWhirlwind), "Whirlwind (8-12 s)", 13_000);
        rig.RunUntil(() => rig.Caster.Casts.Any(c => c.Spell == AvDrekTharAI.SpellKnockdown), "Knockdown (18 s)", 20_000);
    }
}
