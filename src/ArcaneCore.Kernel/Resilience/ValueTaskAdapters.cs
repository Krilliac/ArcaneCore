namespace ArcaneCore.Kernel.Resilience;

/// <summary>Shapes a result-less <see cref="ValueTask"/> as <c>ValueTask&lt;bool&gt;</c> without a state machine when it already completed.</summary>
internal static class ValueTaskAdapters
{
    private static readonly ValueTask<bool> True = new(true);

    public static ValueTask<bool> ToBool(ValueTask pending)
        => pending.IsCompletedSuccessfully ? True : AwaitAsync(pending);

    private static async ValueTask<bool> AwaitAsync(ValueTask pending)
    {
        await pending.ConfigureAwait(false);
        return true;
    }
}
