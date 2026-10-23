using ArcaneCore.Kernel.Logging;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Logging;

/// <summary>
/// Pins the exact drop and priority-drop notice lines each sink writes: text (plain and coloured) and JSON, per sink name,
/// so moving the notice construction into the base class cannot change a byte of them (timestamps aside).
/// </summary>
public sealed class LogSinkNoticeTests
{
    private const string Esc = "\u001b";
    private const string Dropped = "console sink dropped 3 log line(s): the queue was full.";
    private const string Lost = "console sink LOST 7 Error/Critical log line(s): the queue was full of Error/Critical lines.";

    [Fact]
    public void TextSink_DropNotice_IsAWarningLineWithTheSinkNameAndCount()
    {
        var sink = new TextLogSink("console", NullLineWriter.Instance, color: false);
        Assert.EndsWith($" WARN ArcaneCore.Logging: {Dropped}\n", sink.DropNotice(3), StringComparison.Ordinal);
    }

    [Fact]
    public void TextSink_PriorityDropNotice_IsAnErrorLineWithTheSinkNameAndCount()
    {
        var sink = new TextLogSink("console", NullLineWriter.Instance, color: false);
        Assert.EndsWith($" FAIL ArcaneCore.Logging: {Lost}\n", sink.PriorityDropNotice(7), StringComparison.Ordinal);
    }

    [Fact]
    public void TextSink_Notices_FollowTheLiveColourSetting()
    {
        var sink = new TextLogSink("file", NullLineWriter.Instance, color: false);
        Assert.DoesNotContain(Esc, sink.DropNotice(1), StringComparison.Ordinal);
        Assert.DoesNotContain(Esc, sink.PriorityDropNotice(1), StringComparison.Ordinal);

        sink.Color = true;
        string coloured = sink.DropNotice(1);
        Assert.Contains(Esc, coloured, StringComparison.Ordinal);
        Assert.Contains("file sink dropped 1 log line(s): the queue was full.", coloured, StringComparison.Ordinal);
        Assert.EndsWith("\n", coloured, StringComparison.Ordinal);
        string colouredPriority = sink.PriorityDropNotice(1);
        Assert.Contains(Esc, colouredPriority, StringComparison.Ordinal);
        Assert.Contains("file sink LOST 1 Error/Critical log line(s)", colouredPriority, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonSink_DropNotice_IsAWarningObject()
    {
        var sink = new JsonLogSink("console", NullLineWriter.Instance);
        Assert.EndsWith($"\",\"level\":\"Warning\",\"category\":\"ArcaneCore.Logging\",\"message\":\"{Dropped}\"}}\n", sink.DropNotice(3), StringComparison.Ordinal);
    }

    [Fact]
    public void JsonSink_PriorityDropNotice_IsAnErrorObject()
    {
        var sink = new JsonLogSink("console", NullLineWriter.Instance);
        Assert.EndsWith($"\",\"level\":\"Error\",\"category\":\"ArcaneCore.Logging\",\"message\":\"{Lost}\"}}\n", sink.PriorityDropNotice(7), StringComparison.Ordinal);
    }

    [Fact]
    public void Notices_FormatZeroAndLargeCountsVerbatim()
    {
        var sink = new JsonLogSink("json", NullLineWriter.Instance);
        Assert.Contains("json sink dropped 0 log line(s)", sink.DropNotice(0), StringComparison.Ordinal);
        Assert.Contains("json sink LOST 9223372036854775807 Error/Critical", sink.PriorityDropNotice(long.MaxValue), StringComparison.Ordinal);
    }
}
