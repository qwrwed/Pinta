using System;
using System.Runtime.InteropServices;

namespace Pinta.WinInterop;

/// <summary>
/// Small Windows-only helpers for the native Win32 title bar that GTK draws
/// when GTK_CSD=0 is set (see Main.cs): a dark title bar and system-DPI
/// awareness.
/// </summary>
internal static partial class WindowsIntegration
{
	// DWM window attribute: use the dark (immersive) title bar.
	// Supported on Windows 10 build 19041+ and Windows 11.
	private const uint DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

	// GetAncestor(GA_ROOT): the HWND returned by gdk_win32_surface_get_handle
	// is a child rendering surface; the real top-level window is its root.
	private const uint GA_ROOT = 2;

	// Window messages / system commands used by the keep-maximized subclass.
	private const int GWLP_WNDPROC = -4;
	private const uint WM_WINDOWPOSCHANGING = 0x0046;
	private const uint WM_SYSCOMMAND = 0x0112;
	private const uint WM_NCDESTROY = 0x0082;
	private const uint SC_MAXIMIZE = 0xF030;
	private const uint SC_RESTORE = 0xF120;
	private const uint SC_MINIMIZE = 0xF020;
	private const uint SWP_NOSIZE = 0x0001;
	private const uint SWP_HIDEWINDOW = 0x0080;
	private const uint SWP_SHOWWINDOW = 0x0040;

	[LibraryImport ("user32.dll")]
	private static partial IntPtr GetAncestor (IntPtr hWnd, uint gaFlags);

	[LibraryImport ("user32.dll")]
	private static partial IntPtr SetThreadDpiAwarenessContext (IntPtr dpiContext);

	[LibraryImport ("dwmapi.dll")]
	private static partial int DwmSetWindowAttribute (IntPtr hwnd, uint dwAttribute, ref int pvAttribute, uint cbAttribute);

	[LibraryImport ("libgtk-4-1.dll", EntryPoint = "gtk_native_get_surface")]
	private static partial IntPtr GtkNativeGetSurface (IntPtr native);

	[LibraryImport ("libgtk-4-1.dll", EntryPoint = "gdk_win32_surface_get_handle")]
	private static partial IntPtr GdkWin32SurfaceGetHandle (IntPtr surface);

	[LibraryImport ("user32.dll", EntryPoint = "SetWindowLongPtrW")]
	private static partial IntPtr SetWindowLongPtr (IntPtr hWnd, int nIndex, IntPtr dwNewLong);

	[LibraryImport ("user32.dll", EntryPoint = "CallWindowProcW")]
	private static partial IntPtr CallWindowProc (IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

	[LibraryImport ("user32.dll")]
	[return: MarshalAs (UnmanagedType.Bool)]
	private static partial bool IsZoomed (IntPtr hWnd);

	[LibraryImport ("user32.dll")]
	[return: MarshalAs (UnmanagedType.Bool)]
	private static partial bool SetForegroundWindow (IntPtr hWnd);

	[LibraryImport ("user32.dll")]
	[return: MarshalAs (UnmanagedType.Bool)]
	private static partial bool BringWindowToTop (IntPtr hWnd);

	[LibraryImport ("user32.dll")]
	private static partial IntPtr GetWindow (IntPtr hWnd, uint uCmd);

	[LibraryImport ("user32.dll")]
	[return: MarshalAs (UnmanagedType.Bool)]
	private static partial bool EnableWindow (IntPtr hWnd, [MarshalAs (UnmanagedType.Bool)] bool bEnable);

	[LibraryImport ("user32.dll")]
	private static partial IntPtr WindowFromPoint (POINT point);

	[LibraryImport ("user32.dll")]
	private static partial IntPtr GetCapture ();

	[LibraryImport ("user32.dll")]
	[return: MarshalAs (UnmanagedType.Bool)]
	private static partial bool ReleaseCapture ();

	[LibraryImport ("user32.dll", EntryPoint = "SendMessageW")]
	private static partial IntPtr SendMessage (IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

	[LibraryImport ("user32.dll", EntryPoint = "PostMessageW")]
	[return: MarshalAs (UnmanagedType.Bool)]
	private static partial bool PostMessage (IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

	[LibraryImport ("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
	private static partial IntPtr SetWindowsHookEx (int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

	[LibraryImport ("user32.dll")]
	[return: MarshalAs (UnmanagedType.Bool)]
	private static partial bool UnhookWindowsHookEx (IntPtr hhk);

	[LibraryImport ("user32.dll")]
	private static partial IntPtr CallNextHookEx (IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

	[LibraryImport ("kernel32.dll", EntryPoint = "GetModuleHandleW")]
	private static partial IntPtr GetModuleHandle (IntPtr lpModuleName);

	private const int WH_MOUSE_LL = 14;
	private delegate IntPtr LowLevelMouseProc (int nCode, IntPtr wParam, IntPtr lParam);

	[StructLayout (LayoutKind.Sequential)]
	private struct POINT
	{
		public int X, Y;
	}

	[StructLayout (LayoutKind.Sequential)]
	private struct MSLLHOOKSTRUCT
	{
		public POINT pt;
		public uint mouseData;
		public uint flags;
		public uint time;
		public UIntPtr dwExtraInfo;
	}

	private static IntPtr MakeLParam (int x, int y) => (IntPtr) ((y << 16) | (x & 0xFFFF));

	// GetWindow command: the window's owner.
	private const uint GW_OWNER = 4;

	// Dismissing an open GTK menu/popover when the native title bar is clicked.
	// An open menu holds the pointer grab and GDK never sees clicks on the native
	// (Win32) title bar, so the menu would otherwise stay open with the title bar
	// unresponsive. While a menu is open we run a low-level mouse hook (installed
	// only for that period - no always-on overhead). It uses GetCapture() to find
	// whichever menu currently holds the grab (this stays correct as the user
	// navigates the menu bar, unlike tracking one specific popup), and on a click
	// on the main window's native frame it dismisses the menu and forwards the
	// title-bar command.
	private static IntPtr _menuMainWindow;
	private static IntPtr _menuMouseHook;
	private static LowLevelMouseProc? _menuMouseProc;

	// Called when the main window loses the capture to a popup (a menu opening).
	private static void OnMenuOpened (IntPtr mainWindow)
	{
		_menuMainWindow = mainWindow;
		if (_menuMouseHook != IntPtr.Zero) return;
		_menuMouseProc ??= MenuMouseHookProc;
		_menuMouseHook = SetWindowsHookEx (WH_MOUSE_LL, _menuMouseProc, GetModuleHandle (IntPtr.Zero), 0);
	}

	private static void RemoveMenuMouseHook ()
	{
		if (_menuMouseHook == IntPtr.Zero) return;
		UnhookWindowsHookEx (_menuMouseHook);
		_menuMouseHook = IntPtr.Zero;
	}

	private static IntPtr MenuMouseHookProc (int nCode, IntPtr wParam, IntPtr lParam)
	{
		if (nCode >= 0) {
			IntPtr menu = GetCapture (); // whichever menu/popover currently holds the grab

			if (menu == IntPtr.Zero) {
				// No menu open any more; the hook has done its job until next time.
				RemoveMenuMouseHook ();
			} else if (wParam == (IntPtr) 0x0201 /*WM_LBUTTONDOWN*/) {
				MSLLHOOKSTRUCT data = Marshal.PtrToStructure<MSLLHOOKSTRUCT> (lParam);
				POINT pt = data.pt;

				bool insideMenu = GetWindowRect (menu, out RECT mr)
					&& pt.X >= mr.Left && pt.X < mr.Right && pt.Y >= mr.Top && pt.Y < mr.Bottom;

				if (!insideMenu) {
					IntPtr target = WindowFromPoint (pt);
					IntPtr root = target != IntPtr.Zero ? GetAncestor (target, GA_ROOT) : IntPtr.Zero;
					if (root == _menuMainWindow && root != IntPtr.Zero) {
						long hit = SendMessage (root, 0x0084 /*WM_NCHITTEST*/, IntPtr.Zero, MakeLParam (pt.X, pt.Y)).ToInt64 ();

						// Only act on the native title-bar (non-client) area; for clicks
						// in the client area (the canvas) let GTK dismiss the menu itself.
						// We don't trust the exact hit value to pick a command (the
						// hooked coordinates can hit-test to the wrong button), so we
						// only use it to tell title-bar from canvas, then release the
						// grab and let the *real* click through - Windows then hit-tests
						// it natively and performs the correct min/max/restore/close.
						if (hit != 1 /*HTCLIENT*/ && hit != 0 /*HTNOWHERE*/) {
							ReleaseCapture ();
							PostMessage (menu, 0x0100 /*WM_KEYDOWN*/, (IntPtr) 0x1B /*VK_ESCAPE*/, IntPtr.Zero);
							PostMessage (menu, 0x0101 /*WM_KEYUP*/, (IntPtr) 0x1B, IntPtr.Zero);
							// Don't swallow: let the click reach the title bar.
						}
					}
				}
			}
		}

		return CallNextHookEx (_menuMouseHook, nCode, wParam, lParam);
	}

	[LibraryImport ("user32.dll")]
	[return: MarshalAs (UnmanagedType.Bool)]
	private static partial bool GetWindowRect (IntPtr hWnd, out RECT lpRect);

	[StructLayout (LayoutKind.Sequential)]
	private struct RECT
	{
		public int Left, Top, Right, Bottom;
	}

	private delegate IntPtr WndProcDelegate (IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

	// DPI_AWARENESS_CONTEXT_SYSTEM_AWARE = (HANDLE)-2
	private static readonly IntPtr DPI_AWARENESS_CONTEXT_SYSTEM_AWARE = -2;

	/// <summary>
	/// Switches the calling (GTK main) thread to system-DPI awareness, and must
	/// be called before GTK creates any window. GTK4 on Windows only does integer
	/// surface scaling, so under the per-monitor awareness that the .NET host
	/// forces it renders the UI at 1x on a fractionally-scaled (e.g. 150%) display
	/// - everything looks tiny. The .NET host's per-monitor setting cannot be
	/// changed at the process level, but the per-thread DPI context can: setting
	/// the GTK thread to system-aware makes GTK scale the whole UI by the display
	/// scale (matching the released Pinta build).
	/// </summary>
	public static void ApplySystemDpiAwareness ()
	{
		SetThreadDpiAwarenessContext (DPI_AWARENESS_CONTEXT_SYSTEM_AWARE);
	}

	// Returns the native top-level HWND for a realized window, or Zero.
	private static IntPtr GetRootHwnd (Gtk.Window window)
	{
		IntPtr surface = GtkNativeGetSurface (window.Handle.DangerousGetHandle ());
		if (surface == IntPtr.Zero) return IntPtr.Zero;

		IntPtr hwnd = GdkWin32SurfaceGetHandle (surface);
		if (hwnd == IntPtr.Zero) return IntPtr.Zero;

		IntPtr root = GetAncestor (hwnd, GA_ROOT);
		return root != IntPtr.Zero ? root : hwnd;
	}

	// Subclass state for the modal-dialog z-order fix. Keyed by HWND; the
	// delegate is rooted in a static field so the GC can't collect it while
	// Windows still holds the function pointer.
	private static readonly System.Collections.Generic.Dictionary<IntPtr, IntPtr> _modalDialogWndProc = new ();
	private static WndProcDelegate? _modalDialogProc;

	// Owner-disable bookkeeping so a GTK-drawn modal dialog (Adw.MessageDialog,
	// effect dialogs, etc.) makes its parent window inert like a real Win32 modal:
	// dead title-bar buttons + system ding + a flash of the dialog when the parent
	// is clicked. GTK's own modality is just a grab, which leaves the parent's
	// native title bar live (its X does nothing, with no ding/flash). Refcounted
	// per owner HWND for stacked dialogs; _dialogDisabledOwner records which owner
	// each dialog disabled so it is re-enabled exactly once.
	private static readonly System.Collections.Generic.Dictionary<IntPtr, int> _ownerDisableCount = new ();
	private static readonly System.Collections.Generic.Dictionary<IntPtr, IntPtr> _dialogDisabledOwner = new ();

	private static void DisableOwner (IntPtr owner)
	{
		if (owner == IntPtr.Zero) return;
		int count = _ownerDisableCount.TryGetValue (owner, out int c) ? c : 0;
		_ownerDisableCount[owner] = count + 1;
		if (count == 0)
			EnableWindow (owner, false);
	}

	private static void EnableOwner (IntPtr owner)
	{
		if (owner == IntPtr.Zero) return;
		if (!_ownerDisableCount.TryGetValue (owner, out int count)) return;
		count--;
		if (count <= 0) {
			_ownerDisableCount.Remove (owner);
			EnableWindow (owner, true);
		} else {
			_ownerDisableCount[owner] = count;
		}
	}

	/// <summary>
	/// Applies the Windows-specific fixes a modal dialog needs: the dark native
	/// title bar (so dialogs with a native frame, such as the effect-settings
	/// Gtk.Dialog, match Pinta's dark UI instead of defaulting to light) and the
	/// z-order fix below.
	/// </summary>
	public static void PrepareModalDialog (Gtk.Window dialog)
	{
		ApplyDarkTitleBar (dialog);
		FixModalDialogZOrder (dialog);
	}

	/// <summary>
	/// Fixes a z-order glitch where dismissing a modal dialog, after the app was
	/// deactivated and reactivated (alt-tabbing away and back), drops the main
	/// window behind another application's window. GTK hides the dialog before
	/// destroying it, and on hide Windows activates the next top-level window in
	/// the global z-order - which after an alt-tab can be another app rather than
	/// the dialog's owner (verified: the owned-window-activates-owner rule never
	/// applies because the dialog is no longer foreground by destroy time). This
	/// subclasses the dialog and, the instant it is hidden (while it still holds
	/// the foreground), activates its owner so the owner stays foreground.
	/// </summary>
	private static void FixModalDialogZOrder (Gtk.Window dialog)
	{
		dialog.OnRealize += (_, _) => {
			IntPtr hwnd = GetRootHwnd (dialog);
			if (hwnd == IntPtr.Zero) return;
			if (_modalDialogWndProc.ContainsKey (hwnd)) return;

			_modalDialogProc ??= ModalDialogWndProc;
			IntPtr proc = Marshal.GetFunctionPointerForDelegate (_modalDialogProc);
			IntPtr original = SetWindowLongPtr (hwnd, GWLP_WNDPROC, proc);
			_modalDialogWndProc[hwnd] = original;
		};
	}

	private static IntPtr ModalDialogWndProc (IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
	{
		if (!_modalDialogWndProc.TryGetValue (hWnd, out IntPtr original))
			return IntPtr.Zero;

		switch (msg) {
			case WM_WINDOWPOSCHANGING:
				// WINDOWPOS flags live at offset 32 on x64 (see SubclassWndProc).
				if (lParam != IntPtr.Zero) {
					uint flags = (uint) Marshal.ReadInt32 (lParam, 32);

					// On show, disable the owner so its native title bar is inert while
					// the dialog is open (dead buttons + ding + flash, like a real modal).
					if ((flags & SWP_SHOWWINDOW) != 0 && !_dialogDisabledOwner.ContainsKey (hWnd)) {
						IntPtr owner = GetWindow (hWnd, GW_OWNER);
						if (owner != IntPtr.Zero) {
							_dialogDisabledOwner[hWnd] = owner;
							DisableOwner (owner);
						}
					}

					if ((flags & SWP_HIDEWINDOW) != 0) {
						// Re-enable the owner first (a disabled window can't be brought
						// to the foreground), then apply the z-order fix.
						if (_dialogDisabledOwner.TryGetValue (hWnd, out IntPtr disabledOwner)) {
							_dialogDisabledOwner.Remove (hWnd);
							EnableOwner (disabledOwner);
						}

						IntPtr owner = GetWindow (hWnd, GW_OWNER);
						if (owner != IntPtr.Zero) {
							BringWindowToTop (owner);
							SetForegroundWindow (owner);
						}
					}
				}
				break;
			case WM_NCDESTROY:
				// Safety net: re-enable the owner if the dialog is destroyed without a
				// hide, so a missed enable can never leave the main window frozen.
				if (_dialogDisabledOwner.TryGetValue (hWnd, out IntPtr ownerOnDestroy)) {
					_dialogDisabledOwner.Remove (hWnd);
					EnableOwner (ownerOnDestroy);
				}
				// Restore the original WndProc and drop our state before destruction.
				SetWindowLongPtr (hWnd, GWLP_WNDPROC, original);
				_modalDialogWndProc.Remove (hWnd);
				return CallWindowProc (original, hWnd, msg, wParam, lParam);
		}

		return CallWindowProc (original, hWnd, msg, wParam, lParam);
	}

	/// <summary>
	/// Applies a dark native title bar to the window once it is realized.
	/// </summary>
	public static void ApplyDarkTitleBar (Gtk.Window window)
	{
		window.OnRealize += (_, _) => {
			IntPtr hwnd = GetRootHwnd (window);
			if (hwnd == IntPtr.Zero) return;
			int dark = 1;
			DwmSetWindowAttribute (hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof (int));
		};
	}

	// State for the keep-maximized subclass. Keyed by HWND so each window has
	// its own original WndProc. The delegate must be kept alive (rooted in a
	// static field) for as long as the subclass is installed, or the GC will
	// collect it and Windows will call into freed memory.
	private static readonly System.Collections.Generic.Dictionary<IntPtr, IntPtr> _originalWndProc = new ();
	private static readonly System.Collections.Generic.Dictionary<IntPtr, bool> _wantMaximized = new ();
	private static WndProcDelegate? _subclassProc;

	/// <summary>
	/// Keeps a maximized window from being shrunk by GTK's spurious relayouts.
	/// On Windows GTK 4 reacts to certain relayouts (e.g. opening a menu-bar
	/// popover such as File/Edit) while maximized by resizing the window away
	/// from the monitor rectangle, even though Windows still considers it
	/// maximized (restore icon shown, window anchored top-left). This subclasses
	/// the window's WndProc and, while the window should be maximized, blocks
	/// GTK's shrink in WM_WINDOWPOSCHANGING while still allowing a genuine
	/// restore/minimize.
	/// </summary>
	public static void KeepMaximizedStable (Gtk.ApplicationWindow window)
	{
		window.OnRealize += (_, _) => {
			IntPtr hwnd = GetRootHwnd (window);
			if (hwnd == IntPtr.Zero) return;
			if (_originalWndProc.ContainsKey (hwnd)) return;

			_subclassProc ??= SubclassWndProc;
			_wantMaximized[hwnd] = IsZoomed (hwnd);

			IntPtr proc = Marshal.GetFunctionPointerForDelegate (_subclassProc);
			IntPtr original = SetWindowLongPtr (hwnd, GWLP_WNDPROC, proc);
			_originalWndProc[hwnd] = original;
		};
	}

	private static IntPtr SubclassWndProc (IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
	{
		if (!_originalWndProc.TryGetValue (hWnd, out IntPtr original))
			return IntPtr.Zero;

		// When a GTK menu/popover takes the pointer grab it shows up here as the
		// main window losing the capture to another window (the popup). Start the
		// menu mouse hook so a click on the native title bar dismisses the menu and
		// acts (see OnMenuOpened / MenuMouseHookProc); GDK otherwise can't see
		// native-frame clicks and the menu stays open with the title bar dead.
		if (msg == 0x0215 /*WM_CAPTURECHANGED*/ && lParam != IntPtr.Zero && lParam != hWnd)
			OnMenuOpened (hWnd);

		switch (msg) {
			case WM_SYSCOMMAND: {
					uint cmd = (uint) (wParam.ToInt64 () & 0xFFF0);
					if (cmd == SC_MAXIMIZE)
						_wantMaximized[hWnd] = true;
					else if (cmd == SC_RESTORE || cmd == SC_MINIMIZE)
						_wantMaximized[hWnd] = false;
					break;
				}
			case WM_WINDOWPOSCHANGING: {
					// WINDOWPOS layout (x64): hwnd(0) hwndInsertAfter(8) x(16) y(20)
					// cx(24) cy(28) flags(32).
					bool want = _wantMaximized.TryGetValue (hWnd, out bool w) && w;
					if (lParam != IntPtr.Zero && IsZoomed (hWnd)) {
						int cx = Marshal.ReadInt32 (lParam, 24);
						int cy = Marshal.ReadInt32 (lParam, 28);
						uint flags = (uint) Marshal.ReadInt32 (lParam, 32);

						if (GetWindowRect (hWnd, out RECT r)) {
							int curW = r.Right - r.Left;
							int curH = r.Bottom - r.Top;

							// A grow while zoomed means a real (re)maximize - covers Aero
							// snap-to-top, which doesn't send WM_SYSCOMMAND SC_MAXIMIZE.
							if (cx > curW || cy > curH)
								_wantMaximized[hWnd] = want = true;

							// While we should stay maximized, block GTK's spurious shrink
							// below the current (monitor) size by telling Windows to keep
							// the existing size.
							if (want && (flags & SWP_NOSIZE) == 0 && (cx < curW || cy < curH)) {
								flags |= SWP_NOSIZE;
								Marshal.WriteInt32 (lParam, 32, (int) flags);
							}
						}
					}
					break;
				}
			case WM_NCDESTROY: {
					// Restore the original WndProc and drop our state before the window
					// is destroyed.
					SetWindowLongPtr (hWnd, GWLP_WNDPROC, original);
					_originalWndProc.Remove (hWnd);
					_wantMaximized.Remove (hWnd);
					return CallWindowProc (original, hWnd, msg, wParam, lParam);
				}
		}

		return CallWindowProc (original, hWnd, msg, wParam, lParam);
	}
}
