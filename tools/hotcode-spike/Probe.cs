internal interface IMarker
{
}

internal sealed class FirstMarker : IMarker
{
}

internal static class Probe
{
    // run-spike.ps1 edits this method: first the body (hot-applied), then the signature (restart).
    public static string Value() => "v1";
}

// run-spike.ps1 inserts a second IMarker class on the next line (hot-applied: a new type).
//ADDED-HERE
