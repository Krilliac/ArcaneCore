using System.Runtime.InteropServices;
using System.Xml.Linq;
using ArcaneCore.Kernel.Diagnostics;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Diagnostics;

/// <summary>
/// The parts of the crash dialog's TaskDialog (<see cref="CrashDialogTaskDialog"/>) that need no desktop: the clipboard text, the button and
/// flag configuration, the comctl32 version 6 manifest and the native struct layout. Nothing here opens a window or touches the clipboard;
/// the Copy button itself can only be tried by hand with <c>--diagnostics-probe fault-dialog-live</c>.
/// </summary>
public sealed class CrashDialogTaskDialogTests
{
    [Fact]
    public void ClipboardText_IsTitleBlankLineText_WithWindowsLineBreaks()
    {
        Assert.Equal("Realm fault: X at site\r\n\r\nline one\r\nline two", CrashDialogTaskDialog.ClipboardText("Realm fault: X at site", "line one\nline two"));
    }

    [Fact]
    public void ClipboardText_DoesNotDoubleExistingCarriageReturns()
    {
        string text = CrashDialogTaskDialog.ClipboardText("t", "a\r\nb\nc");
        Assert.Equal("t\r\n\r\na\r\nb\r\nc", text);
        Assert.DoesNotContain("\r\r", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ClipboardText_KeepsTheWholeTextWithoutTruncation()
    {
        string long400 = new('x', 5000);
        Assert.EndsWith(long400, CrashDialogTaskDialog.ClipboardText("t", long400), StringComparison.Ordinal);
    }

    [Fact]
    public void Configuration_IsAnErrorBoxWithCopyAndOkAndEscapeCancellation()
    {
        Assert.Equal("Copy", CrashDialogTaskDialog.CopyButtonText);
        Assert.Equal(0x0008, CrashDialogTaskDialog.Flags); // TDF_ALLOW_DIALOG_CANCELLATION only
        Assert.Equal(0x0001, CrashDialogTaskDialog.CommonButtonOk); // TDCBF_OK_BUTTON
        Assert.Equal(0xFFFE, CrashDialogTaskDialog.ErrorIconResource); // MAKEINTRESOURCEW(-2)
        Assert.InRange(CrashDialogTaskDialog.CopyButtonId, 100, int.MaxValue); // clear of IDOK..IDCLOSE (1..8)
    }

    [Fact]
    public void Manifest_DependsOnCommonControlsVersion6()
    {
        XNamespace asm = "urn:schemas-microsoft-com:asm.v1";
        XDocument document = XDocument.Parse(CrashDialogTaskDialog.Manifest);
        XElement identity = Assert.Single(document.Descendants(asm + "assemblyIdentity"));
        Assert.Equal("Microsoft.Windows.Common-Controls", (string?)identity.Attribute("name"));
        Assert.Equal("6.0.0.0", (string?)identity.Attribute("version"));
        Assert.Equal("6595b64144ccf1df", (string?)identity.Attribute("publicKeyToken"));
        Assert.Equal("win32", (string?)identity.Attribute("type"));
        Assert.NotNull(document.Root?.Element(asm + "dependency"));
    }

    [Fact]
    public void TaskDialogConfig_IsPackedLikeCommctrlH()
    {
        // commctrl.h wraps TASKDIALOGCONFIG and TASKDIALOG_BUTTON in pshpack1.h on every architecture. The x64 offsets are what a C compiler
        // reports for offsetof against the Windows SDK 10.0.26100 header (sizeof 160, button 12).
        if (!Environment.Is64BitProcess)
        {
            Assert.Equal(96, Marshal.SizeOf<CrashDialogTaskDialog.TaskDialogConfig>());
            Assert.Equal(8, Marshal.SizeOf<CrashDialogTaskDialog.TaskDialogButton>());
            return;
        }

        Assert.Equal(160, Marshal.SizeOf<CrashDialogTaskDialog.TaskDialogConfig>());
        Assert.Equal(12, Marshal.SizeOf<CrashDialogTaskDialog.TaskDialogButton>());
        Assert.Equal(4, (int)Marshal.OffsetOf<CrashDialogTaskDialog.TaskDialogButton>("pszButtonText"));
        (string Field, int Offset)[] expected =
        [
            ("hwndParent", 4), ("hInstance", 12), ("dwFlags", 20), ("dwCommonButtons", 24), ("pszWindowTitle", 28), ("pszMainIcon", 36),
            ("pszMainInstruction", 44), ("pszContent", 52), ("cButtons", 60), ("pButtons", 64), ("nDefaultButton", 72), ("cRadioButtons", 76),
            ("pRadioButtons", 80), ("nDefaultRadioButton", 88), ("pszVerificationText", 92), ("pszExpandedInformation", 100),
            ("pszExpandedControlText", 108), ("pszCollapsedControlText", 116), ("pszFooterIcon", 124), ("pszFooter", 132), ("pfCallback", 140),
            ("lpCallbackData", 148), ("cxWidth", 156),
        ];
        foreach ((string field, int offset) in expected)
        {
            Assert.Equal(offset, (int)Marshal.OffsetOf<CrashDialogTaskDialog.TaskDialogConfig>(field));
        }
    }
}
