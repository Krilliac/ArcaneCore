namespace ArcaneCore.Kernel.Accounts;

/// <summary>An account whose effective status changed (raised after the change committed).</summary>
public sealed record AccountStatusChange(int AccountId, AccountStatus EffectiveStatus, int? ActorAccountId = null);

/// <summary>An address that was just banned (raised after the change committed).</summary>
public sealed record IpBanChange(string Ip, int? ActorAccountId = null);

/// <summary>
/// In-process notifications from the account and ban stores, so the world daemon can disconnect
/// live sessions the moment a ban lands (retail: World::BanAccount kicks, World.cpp:2469-2486).
/// In-process only: a ban written by another process is covered by the optional periodic re-check.
/// A subscriber that throws never fails the mutation and never starves later subscribers.
/// </summary>
public sealed class AccountStatusEvents
{
    public event Action<AccountStatusChange>? StatusChanged;

    public event Action<IpBanChange>? IpBanned;

    /// <summary>Exceptions swallowed from subscribers (observable for tests and diagnostics).</summary>
    public event Action<Exception>? SubscriberFaulted;

    public void Publish(AccountStatusChange change) => Raise(StatusChanged, change);

    public void Publish(IpBanChange change) => Raise(IpBanned, change);

    private void Raise<T>(Action<T>? handlers, T arg)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (Delegate d in handlers.GetInvocationList())
        {
            try
            {
                ((Action<T>)d)(arg);
            }
            catch (Exception ex)
            {
                try
                {
                    SubscriberFaulted?.Invoke(ex);
                }
                catch (Exception)
                {
                    // diagnostics must never throw back into the mutation
                }
            }
        }
    }
}
