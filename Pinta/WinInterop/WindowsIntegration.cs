using System;
using System.Runtime.InteropServices;

namespace Pinta.WinInterop;

/// <summary>
/// Small Windows-only helpers for the native Win32 title bar that GTK draws
/// when GTK_CSD=0 is set (see Main.cs). The frame itself is fully native;
/// the only thing Windows does not infer automatically is the title bar
/// color, so we opt into the dark title bar to match Pinta's dark UI.
/// </summary>
internal static partial class WindowsIntegration
{
	// DWM window attribute: use the dark (immersive) title bar.
	// Supported on Windows 10 build 19041+ and Windows 11.
	private const uint DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

	// GetAncestor(GA_ROOT): the HWND returned by gdk_win32_surface_get_handle
	// is a child rendering surface; the real top-level window is its root.
	private const uint GA_ROOT = 2;

	[LibraryImport ("user32.dll")]
	private static partial IntPtr GetAncestor (IntPtr hWnd, uint gaFlags);

	[LibraryImport ("user32.dll")]
	private static partial IntPtr SetThreadDpiAwarenessContext (IntPtr dpiContext);

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

	[LibraryImport ("dwmapi.dll")]
	private static partial int DwmSetWindowAttribute (IntPtr hwnd, uint dwAttribute, ref int pvAttribute, uint cbAttribute);

	[LibraryImport ("libgtk-4-1.dll", EntryPoint = "gtk_native_get_surface")]
	private static partial IntPtr GtkNativeGetSurface (IntPtr native);

	[LibraryImport ("libgtk-4-1.dll", EntryPoint = "gdk_win32_surface_get_handle")]
	private static partial IntPtr GdkWin32SurfaceGetHandle (IntPtr surface);

	[LibraryImport ("user32.dll")]
	private static partial IntPtr SendMessageW (IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

	private const uint WM_SYSCOMMAND = 0x0112;
	private static readonly IntPtr SC_MAXIMIZE = 0xF030;

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

	/// <summary>
	/// Maximizes the window natively (Win32) once realized, instead of via GTK.
	/// GTK's own Gtk.Window.Maximize() on Windows pins the window's minimum size
	/// to the monitor size under system-DPI awareness, leaving it unable to be
	/// resized, snapped, or un-maximized. The Win32 path does not.
	///
	/// Uses WM_SYSCOMMAND/SC_MAXIMIZE (the same message the maximize button
	/// sends) rather than ShowWindow(SW_MAXIMIZE) so that GTK's window procedure
	/// observes the maximize and tracks the maximized state. Otherwise GTK still
	/// thinks the window is at a restored size and reverts it (un-maximizes)
	/// when it next re-applies its geometry, e.g. after the add-in scan.
	/// </summary>
	public static void MaximizeNative (Gtk.ApplicationWindow window)
	{
		window.OnRealize += (_, _) => {
			// Defer to an idle callback so the window is mapped/shown first;
			// maximizing during realize is overridden by the subsequent show.
			GLib.Functions.IdleAdd (0, () => {
				IntPtr hwnd = GetRootHwnd (window);
				if (hwnd != IntPtr.Zero)
					SendMessageW (hwnd, WM_SYSCOMMAND, SC_MAXIMIZE, IntPtr.Zero);
				return false;
			});
		};
	}
}
