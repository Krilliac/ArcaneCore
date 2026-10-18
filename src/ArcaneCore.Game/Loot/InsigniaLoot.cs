using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Loot;

namespace ArcaneCore.Game.Loot;

/// <summary>
/// Remove Insignia (22027, SPELL_EFFECT_SKIN_PLAYER_CORPSE) on one map: vmangos Spell::EffectSkinPlayerCorpse (SpellEffects.cpp:5849-5875),
/// Player::RemovedInsignia (Player.cpp:7573-7595) and the HIGHGUID_CORPSE branch of Player::SendLoot (Player.cpp:7756-7795).
/// <list type="bullet">
/// <item>The victim is a dead player of a battleground: a body not yet released is released first (BuildPlayerRepop, ScheduleRepopAtGraveyard;
/// a battleground body is lootable), the victim is sent SMSG_PLAYER_SKINNED (spirit released false) and the body becomes bones
/// (<see cref="MapCombat.ConvertCorpseToBones"/>), flagged CORPSE_DYNFLAG_LOOTABLE.</item>
/// <item>The bones' loot is made once: money of urand(50, 150) * 0.016 * (level / 5.76)^2.5 copper times the money rate for the victim's level
/// (none when the victim is not online) plus the battleground's player skin reference loot (<see cref="RefLootOf"/>: Alterac Valley's
/// battleground_template.PlayerSkinReflootId); everyone may take it (ALL_PERMISSION), and the money is not split.</item>
/// <item>The window opens for the looter at once; anyone may open the bones again while something is left. When the last of it is taken
/// and the window closes, the bones stop being lootable (DoLootRelease).</item>
/// </list>
/// World thread.
/// </summary>
public sealed class InsigniaLoot(LootService loot, Random? random = null)
{
    /// <summary>Remove Insignia.</summary>
    public const uint RemoveInsignia = 22027;

    private readonly Random _random = random ?? Random.Shared;

    /// <summary>The player skin reference loot of the victim's battleground (0 for none). Set by the world.</summary>
    public Func<Player, uint>? RefLootOf { get; set; }

    /// <summary>Whether the victim is in a battleground (vmangos RemovedInsignia: GetBattleGroundId()). Set by the world; null reads the corpse flag only.</summary>
    public Func<Player, bool>? InBattleground { get; set; }

    /// <summary>vmangos Player::SendLoot (Player.cpp:7778): the copper of a level <paramref name="level"/> body for a roll of 50-150.</summary>
    public static uint MoneyFor(uint level, int roll, float moneyRate)
        => (uint)(roll * 0.016f * MathF.Pow(level / 5.76f, 2.5f) * moneyRate);

    /// <summary>
    /// The effect for <paramref name="looter"/> on <paramref name="victim"/> (a dead player, or null when only its <paramref name="corpse"/>
    /// was targeted and its owner is not here). Returns the bones, or null when nothing was skinned.
    /// </summary>
    public Corpse? Remove(Player looter, Player? victim, Corpse? corpse)
    {
        ArgumentNullException.ThrowIfNull(looter);
        if (looter.Map is not { } map)
        {
            return null;
        }

        if (victim is not null)
        {
            if (victim.IsAlive || ReferenceEquals(victim, looter) || (InBattleground is { } inBg && !inBg(victim)))
            {
                return null;
            }

            if (victim.Combat.DeathState != DeathState.Dead)
            {
                if ((victim.UnitFlags & UnitFlags.Skinnable) == 0)
                {
                    return null; // the battleground marks its dead skinnable (BattleGround.cpp:1788)
                }

                map.Combat.RepopPlayer(victim); // "If not released spirit, do it !"
            }

            corpse ??= victim.Combat.Corpse;
        }

        if (corpse is null || corpse.IsBones || (corpse.GetUInt32(UpdateFields.CorpseFieldFlags) & Corpse.FlagLootable) == 0)
        {
            return null;
        }

        victim?.Session.Send(Protocol.WorldOpcode.SmsgPlayerSkinned, [0]);
        if (map.Combat.ConvertCorpseToBones(corpse) is not { } bones)
        {
            return null;
        }

        bones.SetUInt32(UpdateFields.CorpseFieldDynamicFlags, Corpse.DynFlagLootable);
        LootBag bag = Create(bones, looter, victim);
        loot.ShowSpecial(looter, bones, bag);
        return bones;
    }

    private LootBag Create(Corpse bones, Player looter, Player? victim)
    {
        uint refLoot = victim is not null ? RefLootOf?.Invoke(victim) ?? 0 : 0;
        LootBag bag = loot.Generate(bones.Guid, LootSourceKind.Bones, LootType.Insignia, LootTableKind.Reference, refLoot, [looter]);
        bag.Recipients.Clear(); // ALL_PERMISSION: "Everyone can loot"
        if (victim is not null)
        {
            bag.Gold = Math.Min(MoneyFor(victim.Level, _random.Next(50, 151), loot.Options.MoneyRate), LootService.MaxMoneyAmount);
        }

        bag.ShareMoney = false;
        bag.ReleaseHandler = new BonesRelease(loot, bones);
        bag.SourceCheck = viewer => bones.IsInWorld && viewer.IsAlive && ReferenceEquals(viewer.Map, bones.Map)
            && Distance(viewer, bones) <= loot.Options.LootDistance;
        return bag;
    }

    private static float Distance(WorldObject a, WorldObject b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>vmangos DoLootRelease for bones: once looted out, the loot is cleared and the bones lose CORPSE_DYNFLAG_LOOTABLE.</summary>
    private sealed class BonesRelease(LootService loot, Corpse bones) : ILootReleaseHandler
    {
        public void OnReleased(Player player, LootBag bag)
        {
            if (!bag.IsEmpty)
            {
                return;
            }

            bones.SetUInt32(UpdateFields.CorpseFieldDynamicFlags, bones.GetUInt32(UpdateFields.CorpseFieldDynamicFlags) & ~Corpse.DynFlagLootable);
            loot.RemoveSpecial(bones);
        }
    }
}
