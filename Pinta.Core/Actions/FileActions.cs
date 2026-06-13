// 
// FileActions.cs
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
using System.Linq;
using System.Threading.Tasks;

namespace Pinta.Core;

public sealed class FileActions
{
	public Command New { get; }
	public Command NewScreenshot { get; }
	public Command Open { get; }
	public Command Close { get; }
	public Command Save { get; }
	public Command SaveAs { get; }
	public Command ExportAs { get; }
	public Command Print { get; }

	public event EventHandler<ModifyCompressionEventArgs>? ModifyCompression;

	private const string OpenRecentActionName = "open_recent";
	private const string ClearRecentActionName = "clear_recent";

	private readonly Gio.SimpleAction open_recent_action;
	private readonly Gio.SimpleAction clear_recent_action;

	// The section of the "Open Recent" submenu that holds the file entries (rebuilt on change).
	private Gio.Menu recent_files_section = null!; // NRT - set in RegisterActions

	/// <remarks>
	/// The returned value is
	/// <see langword="true" /> if was save succeeded
	/// and
	/// <see langword="false" /> otherwise
	/// </remarks>
	public event AsyncEventHandler<FileActions, DocumentSaveEventArgs>.Returning<bool>? SaveDocument;

	private readonly SystemManager system;
	private readonly AppActions app;
	private readonly RecentFileManager recent_files;
	private readonly WorkspaceManager workspace;
	public FileActions (SystemManager system, AppActions app, RecentFileManager recentFiles, WorkspaceManager workspace)
	{
		New = new Command (
			"new",
			Translations.GetString ("New..."),
			null,
			Resources.StandardIcons.DocumentNew,
			shortcuts: ["<Primary>N"]
		) { ShortLabel = Translations.GetString ("New") };

		NewScreenshot = new Command (
			"NewScreenshot",
			Translations.GetString ("New Screenshot..."),
			null,
			Resources.StandardIcons.ViewFullscreen);

		Open = new Command (
			"open",
			Translations.GetString ("Open..."),
			null,
			Resources.StandardIcons.DocumentOpen,
			shortcuts: ["<Primary>O"]
		) { ShortLabel = Translations.GetString ("Open") };

		Close = new Command (
			"close",
			Translations.GetString ("Close"),
			null,
			Resources.StandardIcons.WindowClose,
			shortcuts: ["<Primary>W"]);

		Save = new Command (
			"save",
			Translations.GetString ("Save"),
			null,
			Resources.StandardIcons.DocumentSave,
			shortcuts: ["<Primary>S"]);

		SaveAs = new Command (
			"saveAs",
			Translations.GetString ("Save As..."),
			null,
			Resources.StandardIcons.DocumentSaveAs,
			shortcuts: ["<Primary><Shift>S"]);

		ExportAs = new Command (
			"exportAs",
			Translations.GetString ("Export As..."),
			null,
			Resources.StandardIcons.DocumentSave,
			shortcuts: ["<Primary>E"]);

		Print = new Command (
			"print",
			Translations.GetString ("Print"),
			null,
			Resources.StandardIcons.DocumentPrint);

		open_recent_action = Gio.SimpleAction.New (OpenRecentActionName, GtkExtensions.IntVariantType);
		open_recent_action.OnActivate += (_, e) => OpenRecentFile (e.Parameter!.GetInt32 ());

		clear_recent_action = Gio.SimpleAction.New (ClearRecentActionName, null);
		clear_recent_action.OnActivate += (_, _) => recentFiles.ClearRecentFiles ();

		this.system = system;
		this.app = app;
		recent_files = recentFiles;
		this.workspace = workspace;
	}

	public void RegisterActions (Gtk.Application application, Gio.Menu menu)
	{
		bool isMac = system.OperatingSystem == OS.Mac;

		Gio.Menu save_section = Gio.Menu.New ();
		save_section.AppendItem (Save.CreateMenuItem ());
		save_section.AppendItem (SaveAs.CreateMenuItem ());
		save_section.AppendItem (ExportAs.CreateMenuItem ());

		Gio.Menu close_section = Gio.Menu.New ();
		close_section.AppendItem (Close.CreateMenuItem ());
		if (!isMac) close_section.AppendItem (app.Exit.CreateMenuItem ()); // This is part of the application menu on macOS

		// "Open Recent" submenu, with the file entries in one section and "Clear List" in another.
		recent_files_section = Gio.Menu.New ();

		Gio.Menu clear_recent_section = Gio.Menu.New ();
		clear_recent_section.AppendItem (Gio.MenuItem.New (Translations.GetString ("Clear List"), $"app.{ClearRecentActionName}"));

		Gio.Menu recent_menu = Gio.Menu.New ();
		recent_menu.AppendSection (null, recent_files_section);
		recent_menu.AppendSection (null, clear_recent_section);

		menu.AppendItem (New.CreateMenuItem ());
		menu.AppendItem (NewScreenshot.CreateMenuItem ());
		menu.AppendItem (Open.CreateMenuItem ());
		menu.AppendSubmenu (Translations.GetString ("Open Recent"), recent_menu);
		menu.AppendSection (null, save_section);
		menu.AppendSection (null, close_section);
#if false
		// Printing is disabled for now until it is fully functional.
		menu.Append (Print.CreateAcceleratedMenuItem (Gdk.Key.P, Gdk.ModifierType.ControlMask));
		menu.AppendSeparator ();
#endif
		application.AddCommands ([
			New,
			NewScreenshot,
			Open,

			Save,
			SaveAs,
			ExportAs,

			Close]);

		if (!isMac)
			application.AddCommand (app.Exit); // This is part of the application menu on macOS

		application.AddAction (open_recent_action);
		application.AddAction (clear_recent_action);

		recent_files.RecentFilesChanged += (_, _) => RebuildRecentFilesMenu ();
		RebuildRecentFilesMenu ();
	}

	public void RegisterHandlers () { }

	private void RebuildRecentFilesMenu ()
	{
		recent_files_section.RemoveAll ();

		var recent = recent_files.RecentFiles;

		if (recent.Length == 0) {
			// A menu item with no action is shown disabled, matching paint.net's greyed-out entry.
			recent_files_section.AppendItem (Gio.MenuItem.New (Translations.GetString ("(No recent files)"), null));
			clear_recent_action.Enabled = false;
			return;
		}

		for (int i = 0; i < recent.Length; ++i) {
			string name = Gio.FileHelper.NewForUri (recent[i]).GetBasename () ?? recent[i];
			// Double underscores so they aren't consumed as menu mnemonic markers.
			string label = $"{i + 1} {name}".Replace ("_", "__");
			recent_files_section.AppendItem (Gio.MenuItem.New (label, $"app.{OpenRecentActionName}({i})"));
		}

		clear_recent_action.Enabled = true;
	}

	private void OpenRecentFile (int index)
	{
		var recent = recent_files.RecentFiles;

		if (index < 0 || index >= recent.Length)
			return;

		string uri = recent[index];
		Gio.File file = Gio.FileHelper.NewForUri (uri);

		if (workspace.OpenFile (file)) {
			recent_files.AddFile (file);

			if (file.GetParent () is Gio.File directory)
				recent_files.LastDialogDirectory = directory;
		} else {
			// The file could not be opened (e.g. it was moved or deleted), so drop it from the list.
			recent_files.RemoveFile (uri);
		}
	}

	/// <returns>
	/// <see langword="true"/> if the save succeeded,
	/// <see langword="false"/> otherwise (for example, if it was canceled)
	/// </returns>
	internal async Task<bool> RaiseSaveDocument (Document document, bool saveAs)
	{
		if (SaveDocument is null)
			throw new InvalidOperationException ("GUI is not handling Workspace.SaveDocument");

		DocumentSaveEventArgs e = new (document, saveAs);
		var results = await SaveDocument.InvokeSequential (this, e);
		return results.All (succeeded => succeeded);
	}

	internal int RaiseModifyCompression (int defaultCompression, Gtk.Window parent)
	{
		ModifyCompressionEventArgs e = new (defaultCompression, parent);
		ModifyCompression?.Invoke (this, e);
		return
			e.Cancel
			? -1
			: e.Quality;
	}
}
