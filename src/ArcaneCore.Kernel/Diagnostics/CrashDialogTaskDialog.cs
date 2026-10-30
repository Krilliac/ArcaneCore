using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace ArcaneCore.Kernel.Diagnostics;

/// <summary>
/// The comctl32 <c>TaskDialogIndirect</c> box behind <see cref="MessageBoxCrashDialogPresenter"/>: an error icon, the text, a custom Copy button
/// that puts the whole text on the clipboard and leaves the box open, and an OK button that closes it. Windows only; every member that touches
/// Win32 is <c>[SupportedOSPlatform("windows")]</c> and reached behind <see cref="OperatingSystem.IsWindows"/>.
/// <para>
/// <c>TaskDialogIndirect</c> exists only in comctl32 version 6, which a process gets only through an application manifest, and the daemons carry
/// none. So <see cref="TryShow"/> activates an activation context for the call (a manifest written to a temp file that depends on
/// <c>Microsoft.Windows.Common-Controls</c> 6.0.0.0, loaded with <c>CreateActCtxW</c>, activated for the calling thread, deactivated and released
/// in a <c>finally</c>, the temp file deleted). The context is per call and per thread; nothing is left active.
/// </para>
/// <para>
/// Layout: <c>TASKDIALOGCONFIG</c> and <c>TASKDIALOG_BUTTON</c> sit between <c>pshpack1.h</c> and <c>poppack.h</c> in commctrl.h for
/// every architecture, so they are <c>Pack = 1</c> on x64 too (hwndParent at offset 4; 160 bytes on x64, checked with a C compile of
/// <c>offsetof</c> against the Windows SDK 10.0.26100 header). The structs hold only integers and pointers, so they are blittable and the
/// strings are unmanaged copies owned by <see cref="TryShow"/>.
/// </para>
/// </summary>
internal static class CrashDialogTaskDialog
{
    /// <summary>The id of the Copy button. Custom ids must not collide with the common buttons (IDOK is 1).</summary>
    internal const int CopyButtonId = 100;

    /// <summary>The label of the Copy button.</summary>
    internal const string CopyButtonText = "Copy";

    /// <summary>commctrl.h <c>TDF_ALLOW_DIALOG_CANCELLATION</c>: Esc and the close box dismiss the dialog although it has no Cancel button.</summary>
    internal const int FlagAllowDialogCancellation = 0x0008;

    /// <summary>commctrl.h <c>TDCBF_OK_BUTTON</c> (the button's id is IDOK, 1).</summary>
    internal const int CommonButtonOk = 0x0001;

    /// <summary>commctrl.h <c>TD_ERROR_ICON</c>, <c>MAKEINTRESOURCEW(-2)</c>.</summary>
    internal const int ErrorIconResource = 0xFFFE;

    /// <summary>The flags the dialog is created with.</summary>
    internal const int Flags = FlagAllowDialogCancellation;

    /// <summary>The common-controls dependency that makes comctl32 resolve to version 6.</summary>
    internal const string Manifest =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n"
        + "<assembly xmlns=\"urn:schemas-microsoft-com:asm.v1\" manifestVersion=\"1.0\">\r\n"
        + "  <dependency>\r\n"
        + "    <dependentAssembly>\r\n"
        + "      <assemblyIdentity type=\"win32\" name=\"Microsoft.Windows.Common-Controls\" version=\"6.0.0.0\" processorArchitecture=\"*\" publicKeyToken=\"6595b64144ccf1df\" language=\"*\" />\r\n"
        + "    </dependentAssembly>\r\n"
        + "  </dependency>\r\n"
        + "</assembly>\r\n";

    // commctrl.h notifications and results.
    private const uint TdnCreated = 0;
    private const uint TdnButtonClicked = 2;
    private const int SOk = 0;
    private const int SFalse = 1;

    // winuser.h SetWindowPos.
    private static readonly IntPtr HwndTopMost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;

    // winbase.h / winuser.h.
    private const uint GmemMoveable = 0x0002;
    private const uint CfUnicodeText = 13;
    private const uint MbIconError = 0x00000010;
    private const int ClipboardAttempts = 10;
    private const int ClipboardRetryMilliseconds = 20;

    // ACTCTX: ACTCTX_FLAG_SET_PROCESS_DEFAULT is not used; only the calling thread sees the context.
    private static readonly IntPtr InvalidHandle = new(-1);

    /// <summary>What goes on the clipboard: the title, a blank line, then the text, with Windows line breaks. Pure.</summary>
    internal static string ClipboardText(string title, string text)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(text);
        string joined = title + "\n\n" + text;
        return joined.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
    }

    /// <summary>
    /// Show the box and block until it is dismissed. Returns true when the box was shown; false when it could not be (comctl32 version 6 could not
    /// be activated, the entry point is missing, or <c>TaskDialogIndirect</c> failed), with the reason in <paramref name="failure"/>, so the caller
    /// can fall back. Never throws except <see cref="OutOfMemoryException"/>. Runs on the calling thread, which pumps the box's messages.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static bool TryShow(string title, string text, out string? failure)
    {
        // DirectUI's UI Automation provider needs a single-threaded apartment, and the fatal path runs on whatever thread crashed and the fault
        // path on a plain background thread (both MTA). So the box runs on its own STA thread that this call joins. The activation context
        // is per thread, so it is created, activated and released on that same thread, inside ShowCore.
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            return ShowCore(title, text, out failure);
        }

        bool shown = false;
        string? threadFailure = null;
        var thread = new Thread(() =>
        {
            try
            {
                shown = ShowCore(title, text, out threadFailure);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                threadFailure = ex.GetType().Name + ": " + ex.Message;
            }
        })
        {
            IsBackground = true,
            Name = "ArcaneCore.CrashDialog.Sta",
        };
        try
        {
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failure = "could not start the STA dialog thread: " + ex.GetType().Name + ": " + ex.Message;
            return false;
        }

        thread.Join();
        failure = threadFailure;
        return shown;
    }

    [SupportedOSPlatform("windows")]
    private static bool ShowCore(string title, string text, out string? failure)
    {
        failure = null;
        string manifestPath = Path.Combine(Path.GetTempPath(), "arcanecore-comctl6-" + Guid.NewGuid().ToString("N") + ".manifest");
        IntPtr titlePtr = IntPtr.Zero;
        IntPtr contentPtr = IntPtr.Zero;
        IntPtr copyTextPtr = IntPtr.Zero;
        IntPtr buttonsPtr = IntPtr.Zero;
        IntPtr context = InvalidHandle;
        UIntPtr cookie = UIntPtr.Zero;
        bool activated = false;
        var callback = new CopyCallback(ClipboardText(title, text));
        try
        {
            File.WriteAllText(manifestPath, Manifest, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var actCtx = new ActCtx { cbSize = (uint)Marshal.SizeOf<ActCtx>(), lpSource = manifestPath };
            context = CreateActCtxW(ref actCtx);
            if (context == InvalidHandle)
            {
                failure = "CreateActCtxW failed (Win32 error " + Marshal.GetLastPInvokeError() + ")";
                return false;
            }

            if (!ActivateActCtx(context, out cookie))
            {
                failure = "ActivateActCtx failed (Win32 error " + Marshal.GetLastPInvokeError() + ")";
                return false;
            }

            activated = true;

            titlePtr = Marshal.StringToHGlobalUni(title);
            contentPtr = Marshal.StringToHGlobalUni(text);
            copyTextPtr = Marshal.StringToHGlobalUni(CopyButtonText);
            buttonsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<TaskDialogButton>());
            Marshal.StructureToPtr(new TaskDialogButton { nButtonID = CopyButtonId, pszButtonText = copyTextPtr }, buttonsPtr, fDeleteOld: false);

            TaskDialogConfig config = default;
            config.cbSize = (uint)Marshal.SizeOf<TaskDialogConfig>();
            config.dwFlags = Flags;
            config.dwCommonButtons = CommonButtonOk;
            config.pszWindowTitle = titlePtr;
            config.pszMainIcon = new IntPtr(ErrorIconResource);
            config.pszContent = contentPtr;
            config.cButtons = 1;
            config.pButtons = buttonsPtr;
            config.nDefaultButton = 1; // IDOK
            config.pfCallback = Marshal.GetFunctionPointerForDelegate(callback.Handler);

            int hr = TaskDialogIndirect(ref config, out _, IntPtr.Zero, IntPtr.Zero);
            GC.KeepAlive(callback);
            if (hr < 0)
            {
                failure = "TaskDialogIndirect returned HRESULT 0x" + hr.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Not displayed yet (a missing comctl32 v6 entry point, a temp directory that cannot be written, a bad manifest): the caller falls back
            // to MessageBoxW. The callback catches its own exceptions, so nothing thrown here comes from a box that was on the screen.
            failure = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
        finally
        {
            if (activated)
            {
                DeactivateActCtx(0, cookie);
            }

            if (context != InvalidHandle)
            {
                ReleaseActCtx(context);
            }

            Free(titlePtr);
            Free(contentPtr);
            Free(copyTextPtr);
            Free(buttonsPtr);
            TryDelete(manifestPath);
        }
    }

    private static void Free(IntPtr pointer)
    {
        if (pointer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover empty manifest in the temp directory is harmless; the dialog already ran.
        }
    }

    /// <summary>Copy <paramref name="text"/> to the clipboard as CF_UNICODETEXT. Returns false when the clipboard stayed busy or any call failed.</summary>
    [SupportedOSPlatform("windows")]
    private static bool TryCopyToClipboard(IntPtr owner, string text)
    {
        int bytes = (text.Length + 1) * sizeof(char);
        IntPtr memory = GlobalAlloc(GmemMoveable, (UIntPtr)(uint)bytes);
        if (memory == IntPtr.Zero)
        {
            return false;
        }

        bool transferred = false;
        try
        {
            IntPtr locked = GlobalLock(memory);
            if (locked == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                Marshal.Copy(text.ToCharArray(), 0, locked, text.Length);
                Marshal.WriteInt16(locked, text.Length * sizeof(char), 0);
            }
            finally
            {
                GlobalUnlock(memory);
            }

            bool opened = false;
            for (int attempt = 0; attempt < ClipboardAttempts && !opened; attempt++)
            {
                opened = OpenClipboard(owner);
                if (!opened)
                {
                    Thread.Sleep(ClipboardRetryMilliseconds);
                }
            }

            if (!opened)
            {
                return false;
            }

            try
            {
                if (!EmptyClipboard() || SetClipboardData(CfUnicodeText, memory) == IntPtr.Zero)
                {
                    return false;
                }

                transferred = true; // the system owns the block now
                return true;
            }
            finally
            {
                CloseClipboard();
            }
        }
        finally
        {
            if (!transferred)
            {
                GlobalFree(memory);
            }
        }
    }

    /// <summary>The dialog's notification callback. Holds the clipboard text; its delegate must outlive the <c>TaskDialogIndirect</c> call.</summary>
    [SupportedOSPlatform("windows")]
    private sealed class CopyCallback
    {
        private readonly string _clipboardText;

        public CopyCallback(string clipboardText)
        {
            _clipboardText = clipboardText;
            Handler = Notify;
        }

        public TaskDialogCallback Handler { get; }

        private int Notify(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam, IntPtr refData)
        {
            try
            {
                if (message == TdnCreated)
                {
                    // MB_TOPMOST | MB_SETFOREGROUND of the old message box.
                    SetWindowPos(hwnd, HwndTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize);
                    SetForegroundWindow(hwnd);
                }
                else if (message == TdnButtonClicked && (int)wParam.ToUInt32() == CopyButtonId)
                {
                    bool copied;
                    try
                    {
                        copied = TryCopyToClipboard(hwnd, _clipboardText);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        copied = false; // a failed copy beeps; the box stays open either way
                    }

                    if (!copied)
                    {
                        MessageBeep(MbIconError);
                    }

                    return SFalse; // S_FALSE: the box stays open
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Deliberately swallowed: an exception must not unwind through comctl32's frames. A failed copy beeps and the box stays open.
            }

            return SOk;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int TaskDialogCallback(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam, IntPtr refData);

    /// <summary>commctrl.h <c>TASKDIALOG_BUTTON</c>, packed (12 bytes on x64).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct TaskDialogButton
    {
        public int nButtonID;
        public IntPtr pszButtonText;
    }

    /// <summary>commctrl.h <c>TASKDIALOGCONFIG</c>, packed for every architecture (160 bytes on x64). The icon unions are one pointer each.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct TaskDialogConfig
    {
        public uint cbSize;
        public IntPtr hwndParent;
        public IntPtr hInstance;
        public int dwFlags;
        public int dwCommonButtons;
        public IntPtr pszWindowTitle;
        public IntPtr pszMainIcon;
        public IntPtr pszMainInstruction;
        public IntPtr pszContent;
        public uint cButtons;
        public IntPtr pButtons;
        public int nDefaultButton;
        public uint cRadioButtons;
        public IntPtr pRadioButtons;
        public int nDefaultRadioButton;
        public IntPtr pszVerificationText;
        public IntPtr pszExpandedInformation;
        public IntPtr pszExpandedControlText;
        public IntPtr pszCollapsedControlText;
        public IntPtr pszFooterIcon;
        public IntPtr pszFooter;
        public IntPtr pfCallback;
        public IntPtr lpCallbackData;
        public uint cxWidth;
    }

    /// <summary>winbase.h <c>ACTCTXW</c> (natural alignment).</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ActCtx
    {
        public uint cbSize;
        public uint dwFlags;
        public string lpSource;
        public ushort wProcessorArchitecture;
        public ushort wLangId;
        public IntPtr lpAssemblyDirectory;
        public IntPtr lpResourceName;
        public IntPtr lpApplicationName;
        public IntPtr hModule;
    }

#pragma warning disable SYSLIB1054 // LibraryImport needs AllowUnsafeBlocks; these calls run only on Windows behind OperatingSystem.IsWindows
    [SupportedOSPlatform("windows")]
    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern int TaskDialogIndirect(ref TaskDialogConfig config, out int button, IntPtr radioButton, IntPtr verificationFlagChecked);

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CreateActCtxW(ref ActCtx context);

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ActivateActCtx(IntPtr context, out UIntPtr cookie);

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeactivateActCtx(uint flags, UIntPtr cookie);

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern void ReleaseActCtx(IntPtr context);

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GlobalFree(IntPtr memory);

    [SupportedOSPlatform("windows")]
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);

    [SupportedOSPlatform("windows")]
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [SupportedOSPlatform("windows")]
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [SupportedOSPlatform("windows")]
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [SupportedOSPlatform("windows")]
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [SupportedOSPlatform("windows")]
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [SupportedOSPlatform("windows")]
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MessageBeep(uint type);
#pragma warning restore SYSLIB1054
}
