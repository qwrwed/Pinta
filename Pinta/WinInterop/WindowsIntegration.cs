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
	private static IntPtr GetRootHwnd (Gtk.ApplicationWindow window)
	{
		IntPtr surface = GtkNativeGetSurface (window.Handle.DangerousGetHandle ());
		if (surface == IntPtr.Zero) return IntPtr.Zero;

		IntPtr hwnd = GdkWin32SurfaceGetHandle (surface);
		if (hwnd == IntPtr.Zero) return IntPtr.Zero;

		IntPtr root = GetAncestor (hwnd, GA_ROOT);
		return root != IntPtr.Zero ? root : hwnd;
	}

	/// <summary>
	/// Applies a dark native title bar to the window once it is realized.
	/// </summary>
	public static void ApplyDarkTitleBar (Gtk.ApplicationWindow window)
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
