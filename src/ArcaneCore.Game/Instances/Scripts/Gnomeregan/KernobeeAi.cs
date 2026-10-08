using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Instances.Scripts.Gnomeregan;

/// <summary>ScriptDev2 npc_kernobeeAI (mangos-classic gnomeregan/gnomeregan.cpp:
/// QuestAccept_npc_kernobee, ReceiveAIEvent, UpdateFollowerAI). The source itself marks the follower as incomplete.</summary>
public sealed class KernobeeAi(Creature creature) : CreatureAI(creature)
{
    public const uint Entry = 7850, Quest = 2904;
    private const float EndX = -330.92f, EndY = -3.03f, EndZ = -152.85f;
    private Player? _leader;
    private uint _checkMs = 10_000;
    private bool _complete;

    public bool AcceptQuest(Player player)
    {
        if (_leader is not null || _complete) return false;
        _leader = player;
        Me.Motion.MoveFollow(player, 1, 0);
        return true;
    }

    public override void OnRespawn()
    {
        _leader = null;
        _complete = false;
        _checkMs = 10_000;
    }

    public override void OnUpdate(uint diffMs)
    {
        if (_leader is null || _complete || Me.Combat.IsInCombat) { UpdateVictim(); return; }
        if (_checkMs > diffMs) { _checkMs -= diffMs; return; }
        _checkMs = 500;
        float dx = Me.X - EndX, dy = Me.Y - EndY, dz = Me.Z - EndZ;
        if (dx * dx + dy * dy + dz * dz > 10 * 10) return;
        _complete = true;
        System?.AiServices.QuestEvents?.EventHappened(_leader, Quest, Me, rewardGroup: true);
        Me.Motion.MovePoint(1, -297.32f, -7.32f, -152.85f, run: false);
        System?.ForcedDespawn(Me, 2000);
    }
}
