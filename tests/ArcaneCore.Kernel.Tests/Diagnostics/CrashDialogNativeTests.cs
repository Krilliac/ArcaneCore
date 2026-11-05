using System.ComponentModel;
using System.Runtime.InteropServices;
using ArcaneCore.Kernel.Diagnostics;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Diagnostics;

/// <summary>
/// The crash dialog's Win32 seam (<see cref="ICrashDialogNative"/>): <see cref="CrashDialogTaskDialog.TryShow"/>, the Copy button's clipboard
/// transfer and the <c>MessageBoxW</c> fallback of <see cref="MessageBoxCrashDialogPresenter"/>, all driven by a recording fake, so no window
/// opens and the real clipboard is never touched. Process-wide seams are swapped, so the class runs in the serial diagnostics collection.
/// </summary>
[Collection(DiagnosticsCollection.Name)]
public sealed class CrashDialogNativeTests : IDisposable
{
    private const uint TdnCreated = 0;
    private const uint TdnButtonClicked = 2;
    private static readonly IntPtr FakeWindow = new(0x1234);

    private readonly FakeNative _native = new();

    public CrashDialogNativeTests()
    {
        CrashDialogTaskDialog.Native = _native;
    }

    public void Dispose()
    {
        CrashDialogTaskDialog.ResetNative();
        MessageBoxCrashDialogPresenter.UseWindowsProbe(null);
    }

    [Fact]
    public void TryShow_ShowsTheBox_OnAnStaThread_AndReleasesTheContextAndManifest()
    {
        bool shown = CrashDialogTaskDialog.TryShow("the title", "the text", out string? failure);

        Assert.True(shown);
        Assert.Null(failure);
        Assert.Equal(new[] { "CreateActCtx", "ActivateActCtx", "TaskDialogIndirect", "DeactivateActCtx", "ReleaseActCtx" }, _native.Calls.Where(static c => c is "CreateActCtx" or "ActivateActCtx" or "TaskDialogIndirect" or "DeactivateActCtx" or "ReleaseActCtx").ToArray());
        Assert.Equal(ApartmentState.STA, _native.DialogApartment);
        Assert.Equal("the title", _native.DialogTitle);
        Assert.Equal("the text", _native.DialogContent);
        Assert.Equal(1u, _native.DialogButtonCount);
        Assert.True(_native.ManifestExistedDuringCall);
        Assert.False(File.Exists(_native.ManifestPath));
    }

    [Fact]
    public void TryShow_WhenTheActivationContextCannotBeCreated_ReportsTheErrorAndNeverOpensTheBox()
    {
        _native.CreateContextResult = new IntPtr(-1);
        _native.CreateContextError = 5;

        Assert.False(CrashDialogTaskDialog.TryShow("t", "x", out string? failure));

        Assert.Equal("CreateActCtxW failed (Win32 error 5)", failure);
        Assert.DoesNotContain("TaskDialogIndirect", _native.Calls);
        Assert.DoesNotContain("ReleaseActCtx", _native.Calls);
        Assert.False(File.Exists(_native.ManifestPath));
    }

    [Fact]
    public void TryShow_WhenTheContextCannotBeActivated_ReleasesItAndReportsTheError()
    {
        _native.ActivateResult = false;
        _native.ActivateError = 87;

        Assert.False(CrashDialogTaskDialog.TryShow("t", "x", out string? failure));

        Assert.Equal("ActivateActCtx failed (Win32 error 87)", failure);
        Assert.DoesNotContain("TaskDialogIndirect", _native.Calls);
        Assert.DoesNotContain("DeactivateActCtx", _native.Calls);
        Assert.Contains("ReleaseActCtx", _native.Calls);
    }

    [Fact]
    public void TryShow_WhenTaskDialogIndirectFails_ReportsTheHresult_AndStillDeactivatesAndReleases()
    {
        _native.DialogResult = unchecked((int)0x80004005);

        Assert.False(CrashDialogTaskDialog.TryShow("t", "x", out string? failure));

        Assert.Equal("TaskDialogIndirect returned HRESULT 0x80004005", failure);
        Assert.Contains("DeactivateActCtx", _native.Calls);
        Assert.Contains("ReleaseActCtx", _native.Calls);
    }

    [Fact]
    public void TryShow_ANativeLayerThatThrows_IsAFailureNotAnException()
    {
        _native.ThrowInTaskDialog = new InvalidOperationException("no entry point");

        Assert.False(CrashDialogTaskDialog.TryShow("t", "x", out string? failure));

        Assert.Equal("InvalidOperationException: no entry point", failure);
        Assert.Contains("ReleaseActCtx", _native.Calls);
    }

    [Fact]
    public void TheCreatedNotification_MakesTheBoxTopMostAndForeground()
    {
        _native.OnDialog = static callback => callback(FakeWindow, TdnCreated, UIntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        Assert.True(CrashDialogTaskDialog.TryShow("t", "x", out _));

        var positioned = Assert.NotNull(_native.WindowPositioned);
        Assert.Equal(FakeWindow, positioned.Window);
        Assert.Equal(new IntPtr(-1), positioned.InsertAfter);
        Assert.Equal(0x0001u | 0x0002u, positioned.Flags);
        Assert.Equal(FakeWindow, Assert.NotNull(_native.ForegroundWindow));
    }

    [Fact]
    public void TheCopyButton_PutsTitleAndTextOnTheClipboard_AndKeepsTheBoxOpen()
    {
        int copyResult = -1;
        int okResult = -1;
        _native.OnDialog = callback =>
        {
            copyResult = callback(FakeWindow, TdnButtonClicked, (UIntPtr)(uint)CrashDialogTaskDialog.CopyButtonId, IntPtr.Zero, IntPtr.Zero);
            okResult = callback(FakeWindow, TdnButtonClicked, (UIntPtr)1u, IntPtr.Zero, IntPtr.Zero);
        };

        Assert.True(CrashDialogTaskDialog.TryShow("Realm fault: X", "line one\nline two", out _));

        Assert.Equal(1, copyResult); // S_FALSE: the box stays open
        Assert.Equal(0, okResult); // S_OK: OK closes it
        Assert.Equal("Realm fault: X\r\n\r\nline one\r\nline two", _native.ClipboardContent);
        Assert.Equal(1, _native.ClipboardSetCount); // the OK click copied nothing
        Assert.Empty(_native.Beeps.ToArray());
        Assert.Equal(_native.Allocated, _native.FreedByCaller + _native.HandedToSystem); // every block accounted for
    }

    [Fact]
    public void Clipboard_BusyThenFree_RetriesWithASleepEachTime_AndHandsTheBlockToTheSystem()
    {
        _native.ClipboardBusyAttempts = 3;

        bool copied = CrashDialogTaskDialog.TryCopyToClipboard(FakeWindow, "abc");

        Assert.True(copied);
        Assert.Equal(3, _native.Sleeps);
        Assert.Equal(4, _native.OpenAttempts);
        Assert.Equal("abc", _native.ClipboardContent);
        Assert.Equal(0, _native.FreedByCaller); // the system owns the block after SetClipboardData
        Assert.Equal(1, _native.CloseCount);
        Assert.Equal(1, _native.LockCount);
        Assert.Equal(1, _native.UnlockCount);
    }

    [Fact]
    public void Clipboard_NeverFree_GivesUpAfterTenAttempts_FreesTheBlock_AndTheButtonBeeps()
    {
        _native.ClipboardBusyAttempts = int.MaxValue;
        int copyResult = -1;
        _native.OnDialog = callback => copyResult = callback(FakeWindow, TdnButtonClicked, (UIntPtr)(uint)CrashDialogTaskDialog.CopyButtonId, IntPtr.Zero, IntPtr.Zero);

        Assert.True(CrashDialogTaskDialog.TryShow("t", "x", out _));

        Assert.Equal(10, _native.OpenAttempts);
        Assert.Equal(10, _native.Sleeps);
        Assert.Equal(0, _native.CloseCount); // never opened
        Assert.Equal(1, _native.FreedByCaller);
        Assert.Null(_native.ClipboardContent);
        Assert.Equal(new[] { 0x10u }, _native.Beeps.ToArray());
        Assert.Equal(1, copyResult);
    }

    [Fact]
    public void Clipboard_WhenSetClipboardDataFails_ClosesTheClipboard_AndFreesTheBlock()
    {
        _native.SetClipboardResult = IntPtr.Zero;

        Assert.False(CrashDialogTaskDialog.TryCopyToClipboard(FakeWindow, "abc"));

        Assert.Equal(1, _native.CloseCount);
        Assert.Equal(1, _native.FreedByCaller);
    }

    [Fact]
    public void Clipboard_WhenEmptyClipboardFails_ClosesTheClipboard_AndFreesTheBlock()
    {
        _native.EmptyResult = false;

        Assert.False(CrashDialogTaskDialog.TryCopyToClipboard(FakeWindow, "abc"));

        Assert.Equal(1, _native.CloseCount);
        Assert.Equal(1, _native.FreedByCaller);
        Assert.Equal(0, _native.ClipboardSetCount);
    }

    [Fact]
    public void Clipboard_WhenTheBlockCannotBeAllocatedOrLocked_FailsWithoutOpeningTheClipboard()
    {
        _native.AllocResult = IntPtr.Zero;
        Assert.False(CrashDialogTaskDialog.TryCopyToClipboard(FakeWindow, "abc"));
        Assert.Equal(0, _native.OpenAttempts);

        _native.AllocResult = null;
        _native.LockResult = IntPtr.Zero;
        Assert.False(CrashDialogTaskDialog.TryCopyToClipboard(FakeWindow, "abc"));
        Assert.Equal(0, _native.OpenAttempts);
        Assert.Equal(1, _native.FreedByCaller);
    }

    [Theory]
    [InlineData(typeof(OutOfMemoryException))]
    [InlineData(typeof(InvalidOperationException))]
    public void TheCallback_NeverLetsAnExceptionEscape_AndKeepsTheBoxOpenAfterCopy(Type exceptionType)
    {
        // An exception (an OutOfMemoryException included) unwinding out of the callback would cross comctl32's native frames.
        _native.ThrowInOpenClipboard = (Exception)Activator.CreateInstance(exceptionType)!;
        int copyResult = -1;
        int createdResult = -1;
        _native.ThrowInSetWindowPos = (Exception)Activator.CreateInstance(exceptionType)!;
        _native.OnDialog = callback =>
        {
            createdResult = callback(FakeWindow, TdnCreated, UIntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            copyResult = callback(FakeWindow, TdnButtonClicked, (UIntPtr)(uint)CrashDialogTaskDialog.CopyButtonId, IntPtr.Zero, IntPtr.Zero);
        };

        Assert.True(CrashDialogTaskDialog.TryShow("t", "x", out string? failure));

        Assert.Null(failure);
        Assert.Equal(0, createdResult);
        Assert.Equal(1, copyResult); // still S_FALSE: the box stays open
        Assert.Equal(new[] { 0x10u }, _native.Beeps.ToArray()); // the failed copy beeped
        Assert.Equal(1, _native.FreedByCaller); // the block was not leaked
    }

    [Fact]
    public void Presenter_WhenTheTaskDialogWorks_NeverOpensAMessageBox()
    {
        MessageBoxCrashDialogPresenter.UseWindowsProbe(static () => true);

        MessageBoxCrashDialogPresenter.Instance.Show("title", "text");

        Assert.Empty(_native.MessageBoxes);
    }

    [Fact]
    public void Presenter_WhenTheTaskDialogFails_FallsBackToMessageBoxW_WithTheErrorForegroundTopMostFlags_AndSaysWhy()
    {
        MessageBoxCrashDialogPresenter.UseWindowsProbe(static () => true);
        _native.DialogResult = unchecked((int)0x80004005);
        TextWriter original = Console.Error;
        using var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            MessageBoxCrashDialogPresenter.Instance.Show("the title", "the text");
        }
        finally
        {
            Console.SetError(original);
        }

        (string text, string title, uint type) = Assert.Single(_native.MessageBoxes);
        Assert.Equal("the text", text);
        Assert.Equal("the title", title);
        Assert.Equal(0x00000010u | 0x00010000u | 0x00040000u, type); // MB_OK | MB_ICONERROR | MB_SETFOREGROUND | MB_TOPMOST
        Assert.Equal(MessageBoxCrashDialogPresenter.MessageBoxType, type);
        Assert.Contains("using a plain message box", captured.ToString(), StringComparison.Ordinal);
        Assert.Contains("0x80004005", captured.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Presenter_WhenMessageBoxWAlsoFails_ThrowsTheWin32Error_ForTheCallerToRecord()
    {
        MessageBoxCrashDialogPresenter.UseWindowsProbe(static () => true);
        _native.DialogResult = unchecked((int)0x80004005);
        _native.MessageBoxResult = 0;
        _native.MessageBoxError = 1400;
        TextWriter original = Console.Error;
        Console.SetError(TextWriter.Null);
        try
        {
            var thrown = Assert.Throws<Win32Exception>(() => MessageBoxCrashDialogPresenter.Instance.Show("t", "x"));
            Assert.Equal(1400, thrown.NativeErrorCode);
        }
        finally
        {
            Console.SetError(original);
        }
    }

    [Fact]
    public void Presenter_OffWindows_ThrowsBeforeAnyNativeCall()
    {
        MessageBoxCrashDialogPresenter.UseWindowsProbe(static () => false);

        Assert.Throws<PlatformNotSupportedException>(() => MessageBoxCrashDialogPresenter.Instance.Show("t", "x"));

        Assert.Empty(_native.Calls);
    }

    /// <summary>Records every call, answers from its knobs, and keeps a clipboard of its own. Memory blocks are real, so the code under test can write to them.</summary>
    private sealed class FakeNative : ICrashDialogNative
    {
        private readonly object _gate = new();
        private readonly List<string> _calls = [];
        private readonly HashSet<IntPtr> _live = [];

        public IReadOnlyList<string> Calls
        {
            get
            {
                lock (_gate)
                {
                    return [.. _calls];
                }
            }
        }

        public IntPtr CreateContextResult { get; set; } = new(0x5000);

        public int CreateContextError { get; set; }

        public bool ActivateResult { get; set; } = true;

        public int ActivateError { get; set; }

        public int DialogResult { get; set; }

        public Exception? ThrowInTaskDialog { get; set; }

        public Action<CrashDialogTaskDialog.TaskDialogCallback>? OnDialog { get; set; }

        public string? ManifestPath { get; private set; }

        public bool ManifestExistedDuringCall { get; private set; }

        public ApartmentState DialogApartment { get; private set; }

        public string? DialogTitle { get; private set; }

        public string? DialogContent { get; private set; }

        public uint DialogButtonCount { get; private set; }

        public (IntPtr Window, IntPtr InsertAfter, uint Flags)? WindowPositioned { get; private set; }

        public IntPtr? ForegroundWindow { get; private set; }

        public Exception? ThrowInSetWindowPos { get; set; }

        public int ClipboardBusyAttempts { get; set; }

        public Exception? ThrowInOpenClipboard { get; set; }

        public IntPtr? AllocResult { get; set; }

        public IntPtr? LockResult { get; set; }

        public IntPtr SetClipboardResult { get; set; } = new(1);

        public bool EmptyResult { get; set; } = true;

        public int OpenAttempts { get; private set; }

        public int Sleeps { get; private set; }

        public int CloseCount { get; private set; }

        public int LockCount { get; private set; }

        public int UnlockCount { get; private set; }

        public int Allocated { get; private set; }

        public int FreedByCaller { get; private set; }

        public int HandedToSystem { get; private set; }

        public int ClipboardSetCount { get; private set; }

        public string? ClipboardContent { get; private set; }

        public List<uint> Beeps { get; } = [];

        public int MessageBoxResult { get; set; } = 1;

        public int MessageBoxError { get; set; }

        public List<(string Text, string Title, uint Type)> MessageBoxes { get; } = [];

        private void Record(string call)
        {
            lock (_gate)
            {
                _calls.Add(call);
            }
        }

        public IntPtr CreateActCtx(string manifestPath, out int error)
        {
            Record("CreateActCtx");
            ManifestPath = manifestPath;
            ManifestExistedDuringCall = File.Exists(manifestPath);
            error = CreateContextError;
            return CreateContextResult;
        }

        public bool ActivateActCtx(IntPtr context, out UIntPtr cookie, out int error)
        {
            Record("ActivateActCtx");
            cookie = new UIntPtr(7);
            error = ActivateError;
            return ActivateResult;
        }

        public void DeactivateActCtx(UIntPtr cookie) => Record("DeactivateActCtx");

        public void ReleaseActCtx(IntPtr context) => Record("ReleaseActCtx");

        public int TaskDialogIndirect(ref CrashDialogTaskDialog.TaskDialogConfig config)
        {
            Record("TaskDialogIndirect");
            if (ThrowInTaskDialog is not null)
            {
                throw ThrowInTaskDialog;
            }

            DialogApartment = Thread.CurrentThread.GetApartmentState();
            DialogTitle = Marshal.PtrToStringUni(config.pszWindowTitle);
            DialogContent = Marshal.PtrToStringUni(config.pszContent);
            DialogButtonCount = config.cButtons;
            if (OnDialog is not null)
            {
                OnDialog(Marshal.GetDelegateForFunctionPointer<CrashDialogTaskDialog.TaskDialogCallback>(config.pfCallback));
            }

            return DialogResult;
        }

        public IntPtr GlobalAlloc(uint flags, UIntPtr bytes)
        {
            Record("GlobalAlloc");
            if (AllocResult is { } fixedResult && fixedResult == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            IntPtr block = Marshal.AllocHGlobal((int)bytes.ToUInt32());
            lock (_gate)
            {
                _live.Add(block);
                Allocated++;
            }

            return block;
        }

        public IntPtr GlobalLock(IntPtr memory)
        {
            Record("GlobalLock");
            LockCount++;
            return LockResult ?? memory;
        }

        public void GlobalUnlock(IntPtr memory)
        {
            Record("GlobalUnlock");
            UnlockCount++;
        }

        public void GlobalFree(IntPtr memory)
        {
            Record("GlobalFree");
            lock (_gate)
            {
                if (_live.Remove(memory))
                {
                    Marshal.FreeHGlobal(memory);
                    FreedByCaller++;
                }
            }
        }

        public bool OpenClipboard(IntPtr owner)
        {
            Record("OpenClipboard");
            OpenAttempts++;
            if (ThrowInOpenClipboard is not null)
            {
                throw ThrowInOpenClipboard;
            }

            return OpenAttempts > ClipboardBusyAttempts;
        }

        public bool EmptyClipboard()
        {
            Record("EmptyClipboard");
            return EmptyResult;
        }

        public IntPtr SetClipboardData(uint format, IntPtr memory)
        {
            Record("SetClipboardData");
            if (SetClipboardResult == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            ClipboardContent = Marshal.PtrToStringUni(memory);
            ClipboardSetCount++;
            lock (_gate)
            {
                // The system owns the block now: the fake frees it itself, which the caller must not.
                if (_live.Remove(memory))
                {
                    Marshal.FreeHGlobal(memory);
                    HandedToSystem++;
                }
            }

            return SetClipboardResult;
        }

        public void CloseClipboard()
        {
            Record("CloseClipboard");
            CloseCount++;
        }

        public void SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags)
        {
            Record("SetWindowPos");
            if (ThrowInSetWindowPos is not null)
            {
                throw ThrowInSetWindowPos;
            }

            WindowPositioned = (hwnd, insertAfter, flags);
        }

        public void SetForegroundWindow(IntPtr hwnd)
        {
            Record("SetForegroundWindow");
            ForegroundWindow = hwnd;
        }

        public void MessageBeep(uint type)
        {
            Record("MessageBeep");
            Beeps.Add(type);
        }

        public void Sleep(int milliseconds)
        {
            Record("Sleep");
            Sleeps++;
        }

        public int MessageBox(string text, string title, uint type, out int error)
        {
            Record("MessageBox");
            MessageBoxes.Add((text, title, type));
            error = MessageBoxError;
            return MessageBoxResult;
        }
    }
}
