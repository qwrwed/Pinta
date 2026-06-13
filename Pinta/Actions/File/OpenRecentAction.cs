//
// OpenRecentAction.cs
//
// Author:
//       Claude <noreply@anthropic.com>
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
using Cairo;
using Pinta.Core;

namespace Pinta.Actions;

/// <summary>
/// Implements the File > Open Recent flyout as a paint.net-style list of recently-used
/// files, each with a thumbnail preview.
///
/// GTK4 menus can't display images, and custom widgets are only allowed at the top level
/// of a menu popover (not inside a submenu). So "Open Recent" is a custom top-level menu
/// row that, on hover, opens a secondary popover containing the thumbnail list - giving
/// the hover-to-open behaviour and ">" arrow of a submenu while still showing thumbnails.
/// </summary>
internal sealed class OpenRecentAction : IActionHandler
{
	private const int ThumbnailWidth = 60;
	private const int ThumbnailHeight = 45;

	// Delay before closing the flyout once the pointer leaves both the row and the flyout,
	// so the user can move the pointer between them without it closing.
	private const uint CloseDelayMs = 150;

	private readonly FileActions file;
	private readonly ChromeManager chrome;
	private readonly WorkspaceManager workspace;
	private readonly RecentFileManager recent_files;
	private readonly ImageConverterManager image_formats;

	// Cache of flattened thumbnails, keyed by file URI, so previews are only rendered once.
	private readonly Dictionary<string, ImageSurface?> thumbnail_cache = [];

	private readonly Gtk.Button row;        // the "Open Recent  ▸" custom menu row
	private readonly Gtk.Label arrow;
	private readonly Gtk.Popover flyout;    // popover holding the thumbnail list
	private readonly Gtk.Box flyout_list;   // flyout contents, rebuilt on change
	private readonly Gtk.EventControllerMotion row_motion;
	private readonly Gtk.EventControllerMotion flyout_motion;

	private uint close_source;
	private bool hooked_menu_closed;

	internal OpenRecentAction (
		FileActions file,
		ChromeManager chrome,
		WorkspaceManager workspace,
		RecentFileManager recentFiles,
		ImageConverterManager imageFormats)
	{
		this.file = file;
		this.chrome = chrome;
		this.workspace = workspace;
		recent_files = recentFiles;
		image_formats = imageFormats;

		// --- The menu row: "Open Recent" with a submenu-style arrow.
		Gtk.Box row_box = Gtk.Box.New (Gtk.Orientation.Horizontal, 6);
		Gtk.Label label = Gtk.Label.New (Translations.GetString ("Open Recent"));
		label.Halign = Gtk.Align.Start;
		label.Hexpand = true;
		arrow = Gtk.Label.New ("▸"); // ▸
		row_box.Append (label);
		row_box.Append (arrow);

		row = Gtk.Button.New ();
		row.AddCssClass ("flat");
		row.AddCssClass ("pinta-recent-files");
		row.SetChild (row_box);
		row.OnClicked += (_, _) => ShowFlyout ();

		// --- The flyout popover holding the thumbnail list.
		flyout_list = Gtk.Box.New (Gtk.Orientation.Vertical, 0);
		flyout_list.AddCssClass ("pinta-recent-files");

		Gtk.ScrolledWindow scroll = Gtk.ScrolledWindow.New ();
		scroll.SetPolicy (Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
		scroll.PropagateNaturalHeight = true;
		scroll.PropagateNaturalWidth = true;
		scroll.MaxContentHeight = 500;
		scroll.SetChild (flyout_list);

		flyout = new Gtk.Popover {
			Autohide = false, // managed manually; an autohide popover would fight the menu's.
			HasArrow = false,
			Position = Gtk.PositionType.Right,
		};
		flyout.SetChild (scroll);
		flyout.SetParent (row);

		// --- Hover handling to open/close the flyout like a submenu. The close timer checks
		// the controllers' live pointer state rather than tracking it manually, so a missed
		// enter/leave event can't leave the flyout stuck open.
		row_motion = Gtk.EventControllerMotion.New ();
		row_motion.OnEnter += (_, _) => { CancelClose (); ShowFlyout (); };
		row_motion.OnLeave += (_, _) => ScheduleClose ();
		row.AddController (row_motion);

		flyout_motion = Gtk.EventControllerMotion.New ();
		flyout_motion.OnEnter += (_, _) => CancelClose ();
		flyout_motion.OnLeave += (_, _) => ScheduleClose ();
		scroll.AddController (flyout_motion);
	}

	void IActionHandler.Initialize ()
	{
		recent_files.RecentFilesChanged += OnRecentFilesChanged;
		RebuildList ();
		UpdateSensitivity ();
		InjectIntoMenu ();
	}

	void IActionHandler.Uninitialize ()
	{
		recent_files.RecentFilesChanged -= OnRecentFilesChanged;
	}

	private void InjectIntoMenu ()
	{
		Gtk.PopoverMenuBar? bar = FindDescendant<Gtk.PopoverMenuBar> (chrome.MainWindow);
		if (bar is null) {
			Console.Error.WriteLine ("[OpenRecent] PopoverMenuBar not found; recent files unavailable.");
			return;
		}

		if (!bar.AddChild (row, FileActions.OpenRecentCustomWidgetId))
			Console.Error.WriteLine ("[OpenRecent] AddChild failed; recent files unavailable.");
	}

	private void OnRecentFilesChanged (object? sender, EventArgs e)
	{
		var current = new HashSet<string> (recent_files.RecentFiles);
		foreach (var uri in new List<string> (thumbnail_cache.Keys))
			if (!current.Contains (uri))
				thumbnail_cache.Remove (uri);

		RebuildList ();
		UpdateSensitivity ();
	}

	private void UpdateSensitivity ()
	{
		bool any = recent_files.RecentFiles.Length > 0;
		row.Sensitive = any;
		arrow.Opacity = any ? 1.0 : 0.0;
	}

	private void ShowFlyout ()
	{
		if (recent_files.RecentFiles.Length == 0)
			return;

		// Close the flyout whenever the File menu itself closes (click away, Escape, etc.).
		if (!hooked_menu_closed && row.GetAncestor (Gtk.Popover.GetGType ()) is Gtk.Popover menu) {
			menu.OnClosed += (_, _) => flyout.Popdown ();
			hooked_menu_closed = true;
		}

		flyout.Popup ();
	}

	private void ScheduleClose ()
	{
		if (close_source != 0)
			return;

		close_source = GLib.Functions.TimeoutAdd (0, CloseDelayMs, () => {
			close_source = 0;
			if (!row_motion.ContainsPointer && !flyout_motion.ContainsPointer)
				flyout.Popdown ();
			return false;
		});
	}

	private void CancelClose ()
	{
		if (close_source == 0)
			return;
		GLib.Functions.SourceRemove (close_source);
		close_source = 0;
	}

	private void CloseMenu ()
	{
		flyout.Popdown ();
		if (row.GetAncestor (Gtk.Popover.GetGType ()) is Gtk.Popover menu)
			menu.Popdown ();
	}

	private void RebuildList ()
	{
		flyout_list.RemoveAll ();

		var recent = recent_files.RecentFiles;

		for (int i = 0; i < recent.Length; ++i)
			flyout_list.Append (CreateRecentFileRow (i + 1, recent[i]));

		flyout_list.Append (Gtk.Separator.New (Gtk.Orientation.Horizontal));

		Gtk.Button clear = Gtk.Button.NewWithLabel (Translations.GetString ("Clear List"));
		clear.AddCssClass ("flat");
		clear.GetFirstChild ()?.SetHalign (Gtk.Align.Start);
		clear.OnClicked += (_, _) => {
			CloseMenu ();
			recent_files.ClearRecentFiles ();
		};
		flyout_list.Append (clear);
	}

	private Gtk.Widget CreateRecentFileRow (int number, string uri)
	{
		Gio.File recentFile = Gio.FileHelper.NewForUri (uri);
		string name = recentFile.GetBasename () ?? uri;

		Gtk.Box content = Gtk.Box.New (Gtk.Orientation.Horizontal, 8);

		Gtk.DrawingArea thumbnail = Gtk.DrawingArea.New ();
		thumbnail.WidthRequest = ThumbnailWidth;
		thumbnail.HeightRequest = ThumbnailHeight;
		ImageSurface? surface = GetThumbnail (recentFile, uri);
		thumbnail.SetDrawFunc ((_, context, width, height) => DrawThumbnail (context, width, height, surface));
		content.Append (thumbnail);

		Gtk.Label label = Gtk.Label.New ($"{number}  {name}");
		label.Halign = Gtk.Align.Start;
		label.Valign = Gtk.Align.Center;
		content.Append (label);

		Gtk.Button button = Gtk.Button.New ();
		button.AddCssClass ("flat");
		button.SetChild (content);
		button.OnClicked += (_, _) => {
			CloseMenu ();
			OpenRecentFile (recentFile, uri);
		};

		return button;
	}

	private ImageSurface? GetThumbnail (Gio.File recentFile, string uri)
	{
		if (thumbnail_cache.TryGetValue (uri, out ImageSurface? cached))
			return cached;

		ImageSurface? surface = null;

		try {
			string name = recentFile.GetBasename () ?? string.Empty;
			IImageImporter? importer = image_formats.GetImporterByFile (name);
			if (importer is not null) {
				Document document = importer.Import (recentFile);
				surface = document.GetFlattenedImage ();
			}
		} catch (Exception) {
			// The file may have been moved/deleted or be in an unsupported format.
			surface = null;
		}

		thumbnail_cache[uri] = surface;
		return surface;
	}

	private static void DrawThumbnail (Context g, int width, int height, ImageSurface? surface)
	{
		if (surface is null)
			return;

		double scale = Math.Min (width / (double) surface.Width, height / (double) surface.Height);
		int drawWidth = Math.Max (1, (int) (surface.Width * scale));
		int drawHeight = Math.Max (1, (int) (surface.Height * scale));

		PointI offset = new (
			X: (width - drawWidth) / 2,
			Y: (height - drawHeight) / 2);

		g.Save ();
		g.Rectangle (offset.X, offset.Y, drawWidth, drawHeight);
		g.Clip ();
		g.Scale (scale, scale);
		g.SetSourceSurface (surface, (int) (offset.X / scale), (int) (offset.Y / scale));
		g.Paint ();
		g.Restore ();
	}

	private void OpenRecentFile (Gio.File recentFile, string uri)
	{
		if (workspace.OpenFile (recentFile)) {
			recent_files.AddFile (recentFile);

			if (recentFile.GetParent () is Gio.File directory)
				recent_files.LastDialogDirectory = directory;
		} else {
			// The file could not be opened (e.g. it was moved or deleted), so drop it from the list.
			recent_files.RemoveFile (uri);
		}
	}

	private static T? FindDescendant<T> (Gtk.Widget root) where T : Gtk.Widget
	{
		for (Gtk.Widget? child = root.GetFirstChild (); child is not null; child = child.GetNextSibling ()) {
			if (child is T match)
				return match;
			if (FindDescendant<T> (child) is T nested)
				return nested;
		}
		return null;
	}
}
