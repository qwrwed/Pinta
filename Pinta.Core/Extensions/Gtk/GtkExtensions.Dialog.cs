//
// GtkExtensions.cs
//
// Author:
//       Jonathan Pobst <monkey@jpobst.com>
//
// Copyright (c) 2010 Jonathan Pobst
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Pinta.Core;

partial class GtkExtensions
{
	/// <summary>
	/// Platform hook invoked with a modal dialog when it is shown, to apply any
	/// platform-specific fixes. Set on Windows (see Main.cs) to apply the dark
	/// native title bar and to correct a z-order glitch where dismissing the
	/// dialog, after the app was deactivated and reactivated (alt-tabbing away
	/// and back), drops the parent window behind another application's window.
	/// Null (no-op) on other platforms.
	/// </summary>
	public static Action<Gtk.Window>? PlatformPrepareModalDialog { get; set; }

	public enum DialogButtonStyle { Normal, Suggested, Destructive }

	/// <summary>
	/// Shows a modal message/confirmation dialog built on <see cref="Gtk.Dialog"/>
	/// (rather than <see cref="Adw.MessageDialog"/>, which is always client-side
	/// decorated). On Windows this gives the native Win32 title bar and the standard
	/// modal behaviour (disabled parent + ding + title-bar flash), matching Pinta's
	/// other dialogs. <paramref name="title"/> is the (short) title-bar text;
	/// <paramref name="heading"/> is an optional bold line in the body; the chosen
	/// button's response id is returned.
	/// </summary>
	public static async Task<int> ShowMessageDialogAsync (
		Gtk.Window? parent,
		string title,
		string? heading,
		string body,
		(string Label, int Response, DialogButtonStyle Style)[] buttons,
		int defaultResponse,
		Gtk.Widget? extraChild = null)
	{
		Gtk.Dialog dialog = Gtk.Dialog.New ();
		dialog.TransientFor = parent;
		dialog.Modal = true;
		dialog.Title = title;

		Gtk.Box content = dialog.GetContentAreaBox ();
		content.Spacing = 6;
		content.SetAllMargins (12);

		if (!string.IsNullOrEmpty (heading)) {
			Gtk.Label heading_label = Gtk.Label.New (heading);
			heading_label.Wrap = true;
			heading_label.Xalign = 0;
			heading_label.AddCssClass ("title-4");
			content.Append (heading_label);
		}

		if (!string.IsNullOrEmpty (body)) {
			Gtk.Label body_label = Gtk.Label.New (body);
			body_label.Wrap = true;
			body_label.Xalign = 0;
			content.Append (body_label);
		}

		if (extraChild is not null)
			content.Append (extraChild);

		foreach (var (label, response, style) in buttons) {
			Gtk.Widget button = dialog.AddButton (label, response);
			if (style == DialogButtonStyle.Suggested)
				button.AddCssClass ("suggested-action");
			else if (style == DialogButtonStyle.Destructive)
				button.AddCssClass ("destructive-action");
		}

		dialog.SetDefaultResponse ((Gtk.ResponseType) defaultResponse);

		int result = (int) await dialog.RunAsync ();
		dialog.Destroy ();
		return result;
	}

	public static async Task<Gio.File?> OpenFileAsync (
		this Gtk.FileDialog fileDialog,
		Gtk.Window parent)
	{
		Gio.File? choice;
		try {
			choice = await fileDialog.OpenAsync (parent);
		} catch (GLib.GException) {
			// Docs: https://docs.gtk.org/gtk4/method.FileDialog.open_finish.html
			// According to the documentation, an error is set if the user cancels
			// TODO: filter by error code once gir.core allows for that
			return null;
		}

		return choice;
	}

	public static async Task<Gio.File?> SaveFileAsync (
		this Gtk.FileDialog fileDialog,
		Gtk.Window parent)
	{
		try {
			return await fileDialog.SaveAsync (parent);
		} catch (GLib.GException) {
			// Docs: https://docs.gtk.org/gtk4/method.FileDialog.save_finish.html
			// An error is set if the user cancels.
			// TODO: filter by error code once gir.core allows for that
			return null;
		}
	}

	public static async Task<IReadOnlyList<Gio.File>?> OpenFilesAsync (
		this Gtk.FileDialog fileDialog,
		Gtk.Window parent)
	{
		Gio.ListModel? selection;
		try {
			selection = await fileDialog.OpenMultipleAsync (parent);
		} catch (GLib.GException) {
			// Docs: https://docs.gtk.org/gtk4/method.FileDialog.open_multiple_finish.html
			// According to the documentation, an error is set if the user cancels
			// TODO: filter by error code once gir.core allows for that
			return null;
		}

		if (selection is null) return null;

		// TODO-GTK4 (bindings) - Gio.ListModel.GetObject doesn't return a Gio.File instance (https://github.com/gircore/gir.core/issues/838)
		uint itemCount = selection.GetNItems ();
		var result = new Gio.File[itemCount];
		for (uint i = 0; i < itemCount; i++) {
			nint g_ref = selection.GetItem (i);
			result[i] = (Gio.FileHelper) GObject.Internal.InstanceWrapper.WrapHandle<Gio.FileHelper> (g_ref, ownedRef: true);
		}
		return result;
	}

	/// <summary>
	/// Similar to gtk_dialog_run() in GTK3, this runs the dialog in a blocking manner with a nested event loop.
	/// This can be useful for compatibility with old code that relies on this behaviour, but new code should be
	/// structured to use event handlers.
	/// </summary>
	public static Gtk.ResponseType RunBlocking (this Gtk.NativeDialog dialog)
	{
		Gtk.ResponseType response = Gtk.ResponseType.None;
		GLib.MainLoop loop = GLib.MainLoop.New (null, false);

		if (!dialog.Modal)
			dialog.Modal = true;

		dialog.OnResponse += (_, args) => {
			response = (Gtk.ResponseType) args.ResponseId;
			if (loop.IsRunning ())
				loop.Quit ();
		};

		dialog.Show ();
		loop.Run ();

		return response;
	}

	/// <summary>
	/// Similar to gtk_dialog_run() in GTK3, this runs the dialog in a blocking manner with a nested event loop.
	/// This can be useful for compatibility with old code that relies on this behaviour, but new code should be
	/// structured to use event handlers.
	/// </summary>
	public static Gtk.ResponseType RunBlocking (this Gtk.Dialog dialog)
	{
		Gtk.ResponseType response = Gtk.ResponseType.None;
		GLib.MainLoop loop = GLib.MainLoop.New (null, false);

		if (!dialog.Modal)
			dialog.Modal = true;

		dialog.OnResponse += (_, args) => {

			response = (Gtk.ResponseType) args.ResponseId;

			if (loop.IsRunning ())
				loop.Quit ();
		};

		PlatformPrepareModalDialog?.Invoke (dialog);
		dialog.Show ();
		loop.Run ();

		return response;
	}

	public static Task<Gtk.ResponseType> RunAsync (this Gtk.NativeDialog dialog)
	{
		TaskCompletionSource<Gtk.ResponseType> completionSource = new ();

		void ResponseCallback (
			Gtk.NativeDialog sender,
			Gtk.NativeDialog.ResponseSignalArgs args)
		{
			completionSource.SetResult ((Gtk.ResponseType) args.ResponseId);
			dialog.OnResponse -= ResponseCallback;
		}

		dialog.OnResponse += ResponseCallback;
		dialog.Show ();

		return completionSource.Task;
	}

	public static Task<Gtk.ResponseType> RunAsync (this Gtk.Dialog dialog)
	{
		TaskCompletionSource<Gtk.ResponseType> completionSource = new ();

		void ResponseCallback (
			Gtk.Dialog sender,
			Gtk.Dialog.ResponseSignalArgs args)
		{
			completionSource.SetResult ((Gtk.ResponseType) args.ResponseId);
			dialog.OnResponse -= ResponseCallback;
		}

		dialog.OnResponse += ResponseCallback;
		PlatformPrepareModalDialog?.Invoke (dialog);
		dialog.Present ();

		return completionSource.Task;
	}

	public static Task PresentAsync (this Gtk.Window window)
	{
		TaskCompletionSource completionSource = new ();

		bool CloseRequestCallback (
			Gtk.Window sender,
			System.EventArgs args)
		{
			completionSource.SetResult ();
			window.OnCloseRequest -= CloseRequestCallback;
			return false; // Allow the dialog to close normally
		}

		window.OnCloseRequest += CloseRequestCallback;
		window.Present ();

		return completionSource.Task;
	}

	public static void SetDefaultResponse (
		this Gtk.Dialog dialog,
		Gtk.ResponseType response
	) => dialog.SetDefaultResponse ((int) response);
}
