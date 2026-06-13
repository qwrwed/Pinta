// 
// RecentFileManager.cs
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
using System.Collections.Immutable;
using System.Linq;
using Gtk;

namespace Pinta.Core;

public sealed class RecentFileManager
{
	/// <summary>
	/// The maximum number of files shown in the "Open Recent" menu.
	/// </summary>
	private const int MaxRecentFiles = 10;

	private const string RecentFilesSettingKey = "recent-files";

	// URIs are percent-encoded, so a newline can never appear within one and is safe as a separator.
	private static readonly char[] uri_separator = ['\n'];

	private readonly ISettingsService settings;
	private readonly List<string> recent_uris;

	private Gio.File? last_dialog_directory;

	/// <summary>
	/// Raised whenever the list of recently-used files changes.
	/// </summary>
	public event EventHandler? RecentFilesChanged;

	public RecentFileManager (ISettingsService settings)
	{
		this.settings = settings;

		last_dialog_directory = DefaultDialogDirectory;

		recent_uris = settings
			.GetSetting (RecentFilesSettingKey, string.Empty)
			.Split (uri_separator, StringSplitOptions.RemoveEmptyEntries)
			.Take (MaxRecentFiles)
			.ToList ();
	}

	/// <summary>
	/// The recently-used file URIs, ordered from most to least recently used.
	/// </summary>
	public ImmutableArray<string> RecentFiles => [.. recent_uris];

	public Gio.File? LastDialogDirectory {
		get => last_dialog_directory;
		set {
			// The file chooser dialog may return null for the current folder in certain cases,
			// such as the Recently Used pane in the Gnome file chooser.
			if (value != null)
				last_dialog_directory = value;
		}
	}

	public Gio.File? DefaultDialogDirectory {
		get {
			string path = System.Environment.GetFolderPath (Environment.SpecialFolder.MyPictures);
			return !string.IsNullOrEmpty (path) ? Gio.FileHelper.NewForPath (path) : null;
		}
	}

	/// <summary>
	/// Returns a directory for use in a dialog. The last dialog directory is
	/// returned if it exists, otherwise the default directory is used.
	/// </summary>
	public Gio.File? GetDialogDirectory ()
	{
		return (last_dialog_directory != null && last_dialog_directory.QueryExists (null)) ? last_dialog_directory : DefaultDialogDirectory;
	}

	/// <summary>
	/// Add a file to the list of recently-used files.
	/// </summary>
	public void AddFile (Gio.File file)
	{
		string uri = file.GetUri ();

		RecentManager.GetDefault ().AddItem (uri);

		// Move the file to the front of the list, removing any earlier occurrence.
		recent_uris.RemoveAll (u => u == uri);
		recent_uris.Insert (0, uri);

		if (recent_uris.Count > MaxRecentFiles)
			recent_uris.RemoveRange (MaxRecentFiles, recent_uris.Count - MaxRecentFiles);

		OnRecentFilesChanged ();
	}

	/// <summary>
	/// Remove a file from the list of recently-used files, e.g. if it no longer exists.
	/// </summary>
	public void RemoveFile (string uri)
	{
		if (recent_uris.RemoveAll (u => u == uri) == 0)
			return;

		OnRecentFilesChanged ();
	}

	/// <summary>
	/// Clear the list of recently-used files.
	/// </summary>
	public void ClearRecentFiles ()
	{
		if (recent_uris.Count == 0)
			return;

		recent_uris.Clear ();

		OnRecentFilesChanged ();
	}

	private void OnRecentFilesChanged ()
	{
		settings.PutSetting (RecentFilesSettingKey, string.Join ('\n', recent_uris));
		RecentFilesChanged?.Invoke (this, EventArgs.Empty);
	}
}
