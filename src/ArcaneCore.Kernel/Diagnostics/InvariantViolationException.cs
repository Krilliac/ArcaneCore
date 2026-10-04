namespace ArcaneCore.Kernel.Diagnostics;

/// <summary>
/// Thrown by a failed debug-build <see cref="Invariant.Assert(bool, string, string, string, int)"/> under
/// <c>Diagnostics:OnInvariant=Continue</c>. It derives from <see cref="Exception"/> directly so that no
/// handler written for a protocol error (<c>catch (ArgumentOutOfRangeException)</c> and friends) swallows a
/// broken invariant; a session that hits one is torn down by the generic handler and the failure is logged
/// with its call site and count.
/// </summary>
public sealed class InvariantViolationException : Exception
{
    public InvariantViolationException(string message, string member, string file, int line)
        : base(message)
    {
        Member = member;
        File = file;
        Line = line;
    }

    /// <summary>The method that evaluated the invariant.</summary>
    public string Member { get; }

    /// <summary>The source file name (no directory) of the call site.</summary>
    public string File { get; }

    /// <summary>The source line of the call site.</summary>
    public int Line { get; }
}
