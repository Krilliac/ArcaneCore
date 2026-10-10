using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Scripting.Modules;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Duel;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Scripting;

/// <summary>AzerothCore mod-duel-reset (DuelReset.cpp, DuelReset_scripts.cpp) on the script hooks.</summary>
public sealed class DuelResetScriptTests
{
    private const uint ShortCooldown = 930310; // 60 s recovery
    private const uint LongCooldown = 930311;  // 30 min recovery (a profession cooldown)

    private static DuelRig NewRig() => new(extraSpells:
    [
        Spell(ShortCooldown, Effect(SpellEffectName.Dummy, 0)) with { RecoveryTime = 60_000 },
        Spell(LongCooldown, Effect(SpellEffectName.Dummy, 0)) with { RecoveryTime = 30 * 60_000 },
    ]);

    private static DuelResetScript Install(DuelRig rig, DuelResetSettings settings)
    {
        var script = new DuelResetScript(settings, () => rig.Kit.System);
        rig.World.Scripts.Register(script);
        return script;
    }

    private static void StartCooldown(DuelRig rig, Player player, uint spellId, uint leftMs)
        => rig.Kit.System.RestoreCooldowns(player, [new PersistedCooldown(SpellCooldownKind.Spell, spellId, leftMs)], nowUnixMs: 0);

    private static uint Left(DuelRig rig, Player player, uint spellId)
        => rig.Kit.System.GetActiveCooldowns(player).FirstOrDefault(c => c.SpellId == spellId).CooldownMs;

    [Fact]
    public void DuelStart_ResetsOldShortCooldowns_FillsHealth_AndAWonDuelRestoresBoth()
    {
        using var rig = NewRig();
        Install(rig, new DuelResetSettings { Zones = "" });
        StartCooldown(rig, rig.A, ShortCooldown, 20_000); // has run 40 s: reset
        StartCooldown(rig, rig.B, ShortCooldown, 50_000); // has run 10 s (< 30 s age): kept
        StartCooldown(rig, rig.A, LongCooldown, 600_000); // over ten minutes: never touched
        rig.A.Health = 400;
        rig.B.Health = 700;

        rig.Challenge();
        rig.AcceptAndStart();

        Assert.Equal(0u, Left(rig, rig.A, ShortCooldown));
        Assert.Equal(50_000u, Left(rig, rig.B, ShortCooldown));
        Assert.Equal(600_000u, Left(rig, rig.A, LongCooldown));
        Assert.Equal(1000u, rig.A.Health);
        Assert.Equal(1000u, rig.B.Health);

        // Fighting in the duel: A uses the short cooldown again and takes damage.
        StartCooldown(rig, rig.A, ShortCooldown, 60_000);
        rig.A.Health = 300;
        rig.Kit.Now += 5_000;

        rig.Service.Cancel(rig.B); // B forfeits: A wins

        Assert.Equal(400u, rig.A.Health);
        Assert.Equal(700u, rig.B.Health);
        Assert.Equal(15_000u, Left(rig, rig.A, ShortCooldown)); // the pre-duel cooldown, five seconds later
        Assert.Equal(45_000u, Left(rig, rig.B, ShortCooldown));
        Assert.Equal(595_000u, Left(rig, rig.A, LongCooldown));
    }

    [Fact]
    public void OutsideTheWhitelist_NothingIsReset()
    {
        using var rig = NewRig();
        DuelResetScript script = Install(rig, new DuelResetSettings { Zones = "1519", Areas = "" });
        StartCooldown(rig, rig.A, ShortCooldown, 20_000);
        rig.A.Health = 400;

        rig.Challenge();
        rig.AcceptAndStart();

        Assert.Equal(20_000u, Left(rig, rig.A, ShortCooldown));
        Assert.Equal(400u, rig.A.Health);
        Assert.False(script.HasSavedState(rig.A));
    }

    [Fact]
    public void AFledDuel_RestoresNothing_AndDropsTheSavedState()
    {
        using var rig = NewRig();
        DuelResetScript script = Install(rig, new DuelResetSettings { Zones = "" });
        rig.A.Health = 400;
        rig.Challenge();
        rig.AcceptAndStart();
        Assert.True(script.HasSavedState(rig.A));
        rig.A.Health = 900;

        rig.Service.Complete(rig.A, Combat.DuelCompleteType.Fled);

        Assert.Equal(900u, rig.A.Health);
        Assert.False(script.HasSavedState(rig.A));
        Assert.False(script.HasSavedState(rig.B));
    }

    /// <summary>A pet owned by A (UNIT_FIELD_SUMMON), standing on map 0 next to its owner.</summary>
    private static Creature AddPet(DuelRig rig)
    {
        const uint entry = 900101;
        var template = Template(entry, b => b.Faction = 14);
        var pet = new Creature(entry, template, null, Content([template], []), new Random(1));
        pet.MapId = 0;
        pet.Relocate(rig.A.X + 1, rig.A.Y, rig.A.Z, 0, rig.Kit.Now);
        rig.Map.AddObject(pet);
        rig.A.SetPetGuid(pet.Guid);
        Assert.Same(pet, rig.A.GetPet());
        return pet;
    }

    private static void StartPetCooldown(DuelRig rig, Creature pet, uint spellId, uint leftMs)
        => rig.Kit.System.RestoreCooldowns(pet, [new PersistedCooldown(SpellCooldownKind.Spell, spellId, leftMs)], nowUnixMs: 0);

    private static uint PetLeft(DuelRig rig, Creature pet, uint spellId)
        => rig.Kit.System.GetActiveCooldowns(pet).FirstOrDefault(c => c.SpellId == spellId).CooldownMs;

    /// <summary>The (spell id, GUID) pairs of the SMSG_CLEAR_COOLDOWN packets the owner received.</summary>
    private static List<(uint SpellId, ulong Guid)> ClearedFor(DuelRig rig)
        => [.. Packets(rig.SessionA, WorldOpcode.SmsgClearCooldown).Select(payload =>
        {
            var reader = new PacketReader(payload);
            uint spellId = reader.ReadUInt32();
            return (spellId, reader.ReadUInt64());
        })];

    [Fact]
    public void DuelStart_ClearsEveryPetCooldown_AndTellsTheOwner()
    {
        using var rig = NewRig();
        Install(rig, new DuelResetSettings { Zones = "" });
        Creature pet = AddPet(rig);
        StartPetCooldown(rig, pet, ShortCooldown, 20_000);
        StartPetCooldown(rig, pet, LongCooldown, 30 * 60_000); // no ten-minute filter for pets
        rig.Challenge();
        rig.SessionA.Clear();

        rig.AcceptAndStart();

        Assert.Equal(0u, PetLeft(rig, pet, ShortCooldown));
        Assert.Equal(0u, PetLeft(rig, pet, LongCooldown));
        Assert.Equal(
            [(ShortCooldown, pet.Guid.Value), (LongCooldown, pet.Guid.Value)],
            ClearedFor(rig).OrderBy(c => c.SpellId).ToArray());
    }

    [Fact]
    public void AWonDuel_ClearsPetCooldownsStartedDuringTheDuel_AndDoesNotRestoreThePreDuelOnes()
    {
        using var rig = NewRig();
        Install(rig, new DuelResetSettings { Zones = "" });
        Creature pet = AddPet(rig);
        StartPetCooldown(rig, pet, ShortCooldown, 20_000);
        rig.Challenge();
        rig.AcceptAndStart();
        Assert.Equal(0u, PetLeft(rig, pet, ShortCooldown));

        StartPetCooldown(rig, pet, LongCooldown, 60_000); // started during the duel
        rig.Kit.Now += 5_000;
        rig.Service.Cancel(rig.B); // B forfeits: A wins

        Assert.Empty(rig.Kit.System.GetActiveCooldowns(pet));
    }

    [Fact]
    public void OutsideTheWhitelist_OrWithCooldownsOff_PetCooldownsAreKept()
    {
        foreach (DuelResetSettings settings in new[]
        {
            new DuelResetSettings { Zones = "1519", Areas = "" },
            new DuelResetSettings { Zones = "", Cooldowns = false },
        })
        {
            using var rig = NewRig();
            Install(rig, settings);
            Creature pet = AddPet(rig);
            StartPetCooldown(rig, pet, ShortCooldown, 20_000);
            rig.Challenge();
            rig.SessionA.Clear();

            rig.AcceptAndStart();
            rig.Service.Cancel(rig.B);

            Assert.Equal(20_000u, PetLeft(rig, pet, ShortCooldown));
            Assert.Empty(ClearedFor(rig));
        }
    }

    [Fact]
    public void AFledDuel_LeavesPetCooldownsStartedDuringTheDuel()
    {
        using var rig = NewRig();
        Install(rig, new DuelResetSettings { Zones = "" });
        Creature pet = AddPet(rig);
        rig.Challenge();
        rig.AcceptAndStart();
        StartPetCooldown(rig, pet, ShortCooldown, 40_000);

        rig.Service.Complete(rig.A, Combat.DuelCompleteType.Fled);

        Assert.Equal(40_000u, PetLeft(rig, pet, ShortCooldown));
    }

    [Fact]
    public void Settings_ParseTheModulesLists()
    {
        using var rig = NewRig();
        // The test player stands in zone 12 (Elwynn Forest); the test map has no area data (area 0).
        var script = new DuelResetScript(new DuelResetSettings { Zones = "0", Areas = "12;14;809" }, () => null);
        Assert.Equal(12u, rig.A.ZoneId);
        Assert.False(script.IsAllowedInArea(rig.A)); // zones "0" is none, and area 0 matches no listed area
        script.Apply(new DuelResetSettings { Zones = "1519;12", Areas = "0" });
        Assert.True(script.IsAllowedInArea(rig.A));
        script.Apply(new DuelResetSettings { Zones = "", Areas = "0" });
        Assert.True(script.IsAllowedInArea(rig.A)); // "" is any zone
    }
}
