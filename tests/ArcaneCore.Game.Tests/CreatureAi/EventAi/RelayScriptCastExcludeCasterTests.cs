using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.CreatureAi.EventAi;

/// <summary>
/// Relay CAST_SPELL (15) with a spell that has SPELL_ATTR_EX_EXCLUDE_CASTER cast on its own source: cmangos clears the target
/// (DBScripts/ScriptMgr.cpp:2167-2168), for a creature source and for a player or other unit source alike. Rows and ids are synthetic.
/// </summary>
public sealed class RelayScriptCastExcludeCasterTests
{
    private const uint TextEmoteWave = 101;
    private const uint Relay = 939000;
    private const uint ExcludingSpell = 4321;
    private const uint PlainSpell = 4322;
    private const uint FlagReverse = 0x002;
    private const uint FlagSourceTargetsSelf = 0x004;

    private sealed class Queries : IUnitSpellQueries
    {
        public int GetAuraStacks(Unit unit, uint spellId) => 0;

        public bool IsCasting(Unit unit) => false;

        public bool ExcludesCaster(uint spellId) => spellId == ExcludingSpell;
    }

    private static RelayScriptStep Cast(uint spell, uint flags)
        => new(Relay, 0, 0, 15, spell, 0, 0, 0, 0, flags, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static (WorldRuntime World, Creature Elly, Player Player, FakeCaster Spells) Start(RelayScriptStep step)
    {
        var ai = new CreatureAiContent(
            [new CreatureAiEvent
            {
                Id = 1,
                CreatureId = WolfEntry,
                EventType = 22,
                Flags = 1,
                Param1 = (int)TextEmoteWave,
                Action1 = new CreatureAiAction((byte)EventAiActionType.StartRelayScript, (int)Relay, 7, 0),
            }], [], new BroadcastTextCatalog([]))
        {
            RelayScripts = new RelayScriptCatalog([step], []),
        };
        CreatureContent content = new([Template() with { AIName = CreatureAiFactory.EventAIName, Civilian = true }], [Spawn(1, WolfEntry, 0, 0)], [], [], [], ai);
        var spells = new FakeCaster();
        (WorldRuntime world, Map _, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Spells = spells, UnitSpells = new Queries() });
        (Player player, FakeSession _) = AddPlayer(world, 1, 0, 10);
        return (world, system.Creatures.Single(c => c.Template.Entry == WolfEntry), player, spells);
    }

    [Fact]
    public void AUnitSource_CastingAnExcludeCasterSpellOnItself_HasNoTarget()
    {
        // The player starts the relay (source), Elly is the target; SOURCE_TARGETS_SELF makes the player the target too.
        (WorldRuntime world, Creature elly, Player player, FakeCaster spells) = Start(Cast(ExcludingSpell, FlagSourceTargetsSelf));
        using (world)
        {
            elly.ReceiveEmote(player, TextEmoteWave);

            (Unit caster, uint spell, Unit? target, bool _) = Assert.Single(spells.UnitCasts);
            Assert.Same(player, caster);
            Assert.Equal(ExcludingSpell, spell);
            Assert.Null(target);
        }
    }

    [Fact]
    public void ACreatureSource_CastingAnExcludeCasterSpellOnItself_HasNoTarget()
    {
        // Reverse direction makes Elly the source; SOURCE_TARGETS_SELF then makes her the target too.
        (WorldRuntime world, Creature elly, Player player, FakeCaster spells) = Start(Cast(ExcludingSpell, FlagReverse | FlagSourceTargetsSelf));
        using (world)
        {
            elly.ReceiveEmote(player, TextEmoteWave);

            (uint spell, Unit? target, bool _) = Assert.Single(spells.Casts);
            Assert.Equal(ExcludingSpell, spell);
            Assert.Null(target);
        }
    }

    [Fact]
    public void ASpellWithoutTheAttribute_StillTargetsItsOwnSource()
    {
        (WorldRuntime world, Creature elly, Player player, FakeCaster spells) = Start(Cast(PlainSpell, FlagSourceTargetsSelf));
        using (world)
        {
            elly.ReceiveEmote(player, TextEmoteWave);

            (Unit caster, uint _, Unit? target, bool _) = Assert.Single(spells.UnitCasts);
            Assert.Same(player, caster);
            Assert.Same(player, target);
        }
    }

    [Fact]
    public void AnExcludeCasterSpell_AtAnotherUnit_KeepsItsTarget()
    {
        (WorldRuntime world, Creature elly, Player player, FakeCaster spells) = Start(Cast(ExcludingSpell, FlagReverse));
        using (world)
        {
            elly.ReceiveEmote(player, TextEmoteWave);

            (uint _, Unit? target, bool _) = Assert.Single(spells.Casts);
            Assert.Same(player, target);
        }
    }
}
