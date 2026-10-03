namespace ArcaneCore.Game.Entities;

public sealed partial class Player
{
    private Guid? _questSettlementOperationId;
    private bool _publishingQuestSettlement;

    /// <summary>World-owned operation identity; an old session cannot release a newer settlement.</summary>
    public Guid? QuestSettlementOperationId => _questSettlementOperationId;

    public bool IsQuestSettlementPending => _questSettlementOperationId is not null;

    /// <summary>Only the synchronous world-thread publication may change staged character state.</summary>
    public bool CanMutateQuestSettlementState => !IsQuestSettlementPending || _publishingQuestSettlement;

    public bool BeginQuestSettlement(Guid operationId)
    {
        Map?.EnsureWorldThread();
        if (operationId == System.Guid.Empty)
        {
            throw new ArgumentException("settlement requires an operation identity", nameof(operationId));
        }

        if (IsQuestSettlementPending)
        {
            return false;
        }

        _questSettlementOperationId = operationId;
        return true;
    }

    public bool EndQuestSettlement(Guid operationId)
    {
        Map?.EnsureWorldThread();
        if (_questSettlementOperationId != operationId)
        {
            return false;
        }

        if (_publishingQuestSettlement)
        {
            throw new InvalidOperationException("finish reward publication before releasing settlement");
        }

        _questSettlementOperationId = null;
        return true;
    }

    /// <summary>
    /// Publish the exact durable result on the world thread while every gameplay observer still
    /// sees the character as pending. The caller checks the exact live Player and operation first.
    /// </summary>
    public IDisposable BeginQuestSettlementPublication(Guid operationId)
    {
        Map?.EnsureWorldThread();
        if (_questSettlementOperationId != operationId || _publishingQuestSettlement)
        {
            throw new InvalidOperationException("quest settlement publication identity mismatch");
        }

        _publishingQuestSettlement = true;
        return new QuestSettlementPublication(this);
    }

    internal void EnsureQuestSettlementMutationAllowed()
    {
        Map?.EnsureWorldThread();
        if (!CanMutateQuestSettlementState)
        {
            throw new InvalidOperationException("character state is held by a pending quest reward");
        }
    }

    private sealed class QuestSettlementPublication(Player player) : IDisposable
    {
        private Player? _player = player;

        public void Dispose()
        {
            if (_player is not { } current)
            {
                return;
            }

            current.Map?.EnsureWorldThread();
            current._publishingQuestSettlement = false;
            _player = null;
        }
    }
}
