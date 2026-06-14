//
// SaveDocumentImplmentationAction.cs
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
using System.Linq;
using System.Threading.Tasks;
using Pinta.Core;

namespace Pinta.Actions;

internal sealed class SaveDocumentImplmentationAction : IActionHandler
{
	private readonly FileActions file;
	private readonly ImageActions image;
	private readonly ChromeManager chrome;
	private readonly ImageConverterManager image_formats;
	private readonly RecentFileManager recent_files;
	private readonly ToolManager tools;
	internal SaveDocumentImplmentationAction (
		FileActions file,
		ImageActions image,
		ChromeManager chrome,
		ImageConverterManager imageFormats,
		RecentFileManager recentFiles,
		ToolManager tools)
	{
		this.file = file;
		this.image = image;
		this.chrome = chrome;
		image_formats = imageFormats;
		recent_files = recentFiles;
		this.tools = tools;
	}

	void IActionHandler.Initialize ()
	{
		file.SaveDocument += Activated;
	}

	void IActionHandler.Uninitialize ()
	{
		file.SaveDocument -= Activated;
	}

	private async Task<bool> Activated (FileActions sender, DocumentSaveEventArgs e)
	{
		// Prompt for a new filename for "Save As", or a document that hasn't been saved before
		if (e.SaveAs || !e.Document.HasFile) {
			return await SaveFileAs (e.Document);
		}

		// Document hasn't changed, don't re-save it
		if (!e.Document.IsDirty)
			return true;

		// If the document already has a filename, just re-save it
		return await SaveFile (e.Document, null, null, chrome.MainWindow);
	}

	// This is actually both for "Save As" and saving a file that never
	// been saved before.  Either way, we need to prompt for a filename.
	private async Task<bool> SaveFileAs (Document document)
	{
		// Add all the formats we support to the save dialog.
		using Gio.ListStore filters = Gio.ListStore.New (Gtk.FileFilter.GetGType ());
		foreach (var format in image_formats.Formats) {
			if (!format.IsExportAvailable ())
				continue;
			filters.Append (format.Filter);
		}

		// Determine which format's filter to pre-select, and suggest a matching filename.
		FormatDescriptor? format_desc = document.HasFile
			? image_formats.GetFormatByFile (document.DisplayName)
			: null;

		// How to seed the dialog each time it's shown. Either an existing file (keep
		// its name + folder) or a folder + suggested name. Updated on a re-prompt.
		Gio.File? initialFile = null;
		Gio.File? initialFolder = null;
		string? initialName = null;

		if (document.HasFile && format_desc is not null && format_desc.IsExportAvailable ()) {
			// The document's own format can be written: keep its existing name and extension.
			initialFile = document.File!;
		} else {
			// Either an unsaved document or one whose format Pinta can't write (e.g. an
			// imported .pdn). Fall back to a writable default and suggest a filename whose
			// extension matches it, so the name and the selected type stay consistent.
			format_desc = image_formats.GetDefaultSaveFormat ();
			string default_ext = format_desc.Extensions.First ();

			if (document.HasFile) {
				initialFolder = document.File!.GetParent ();
				string baseName = System.IO.Path.GetFileNameWithoutExtension (document.DisplayName);
				initialName = $"{baseName}.{default_ext}";
			} else {
				if (recent_files.GetDialogDirectory () is Gio.File dir && dir.QueryExists (null))
					initialFolder = dir;

				// Append the default extension, producing e.g. "Unsaved Image 1.png"
				initialName = $"{document.DisplayName}.{default_ext}";
			}
		}

		while (true) {

			using Gtk.FileDialog fileDialog = Gtk.FileDialog.New ();
			fileDialog.SetTitle (Translations.GetString ("Save Image File"));
			fileDialog.SetFilters (filters);
			fileDialog.SetDefaultFilter (format_desc.Filter);
			fileDialog.Modal = true;

			if (initialFile is not null) {
				fileDialog.SetInitialFile (initialFile);
			} else {
				if (initialFolder is not null)
					fileDialog.SetInitialFolder (initialFolder);
				if (initialName is not null)
					fileDialog.SetInitialName (initialName);
			}

			Gio.File? file = await fileDialog.SaveFileAsync (chrome.MainWindow);
			if (file is null)
				return false;

			// Note that we can't use file.GetDisplayName() because the file doesn't exist.
			string displayName = file.GetParent ()!.GetRelativePath (file)!;
			Gio.File? directory = file.GetParent ();

			// Seed any re-prompt with the chosen name and folder.
			initialFile = null;
			initialFolder = directory;
			initialName = displayName;

			// Always follow the extension rather than the file type drop down
			// ie: if the user chooses to save a "jpeg" as "foo.png", we are going
			// to assume they just didn't update the dropdown and really want png
			FormatDescriptor? format = image_formats.GetFormatByFile (displayName);

			if (format is not null && !format.IsExportAvailable ()) {
				// The typed extension maps to a format Pinta can only open, not write
				// (e.g. .pdn). Don't silently write a different format under this extension;
				// re-prompt so the user can choose a writable name/type.
				await GtkExtensions.ShowMessageDialogAsync (
					chrome.MainWindow,
					Translations.GetString ("Unsupported Format"),
					UnsupportedFormatHeading (displayName),
					Translations.GetString ("Pinta does not support saving images in this file format."),
					[(Translations.GetString ("_OK"), (int) Gtk.ResponseType.Ok, GtkExtensions.DialogButtonStyle.Normal)],
					defaultResponse: (int) Gtk.ResponseType.Ok);
				continue;
			}

			// No recognized extension: fall back to a writable default.
			format ??= image_formats.GetDefaultSaveFormat ();

			if (!await ConfirmFlatten (document, format)) {
				continue;
			}

			if (directory is not null)
				recent_files.LastDialogDirectory = directory;

			// If saving the file failed or was cancelled, let the user select
			// a different file type.
			if (!await SaveFile (document, file, format, chrome.MainWindow)) {
				continue;
			}

			//The user is saving the Document to a new file, so technically it
			//hasn't been saved to its associated file in this session.
			document.HasBeenSavedInSession = false;

			recent_files.AddFile (file);
			image_formats.SetDefaultFormat (format.Extensions.First ());

			document.File = file;
			document.FileType = format.Extensions.First ();
			return true;
		}
	}

	private async Task<bool> SaveFile (Document document, Gio.File? file, FormatDescriptor? format, Gtk.Window parent)
	{
		file ??= document.File;

		if (file is null)
			throw new ArgumentException ("Attempted to save a document with no associated file", nameof (file));

		if (format is null) {

			if (string.IsNullOrEmpty (document.FileType))
				throw new ArgumentException ($"{nameof (document.FileType)} must contain value.", nameof (document));

			format = image_formats.GetFormatByExtension (document.FileType);
		}

		if (format is null || !format.IsExportAvailable ()) {

			// This format can only be opened, not written (e.g. an imported .pdn).
			// Offer to pick a writable format rather than dead-ending on "OK".
			string heading = UnsupportedFormatHeading (file.GetDisplayName ());
			string body = Translations.GetString ("Pinta does not support saving images in this file format.");

			const int cancel = (int) Gtk.ResponseType.Cancel;
			const int save_as = 1;

			int response = await GtkExtensions.ShowMessageDialogAsync (
				parent,
				Translations.GetString ("Unsupported Format"),
				heading,
				body,
				[
					(Translations.GetString ("_Cancel"), cancel, GtkExtensions.DialogButtonStyle.Normal),
					(Translations.GetString ("Save _As…"), save_as, GtkExtensions.DialogButtonStyle.Suggested),
				],
				defaultResponse: save_as);

			if (response == save_as)
				return await SaveFileAs (document);

			return false;
		}

		if (!await ConfirmFlatten (document, format)) {
			return false;
		}

		// Commit any pending changes
		tools.Commit ();

		try {
			format.Exporter.Export (document, file, parent);

		} catch (GLib.GException e) when (e.Message == "Image too large to be saved as ICO") {

			string primary = Translations.GetString ("Image too large");
			string secondary = Translations.GetString ("ICO files can not be larger than 255 x 255 pixels.");

			await chrome.ShowMessageDialog (parent, primary, secondary);

			return false;

		} catch (GLib.GException e) when (e.Message.Contains ("Permission denied") && e.Message.Contains ("Failed to open")) {

			string primary = Translations.GetString ("Failed to save image");

			// Translators: {0} is the name of a file that the user does not have write permission for.
			string secondary = Translations.GetString ("You do not have access to modify '{0}'. The file or folder may be read-only.", file);

			await chrome.ShowMessageDialog (parent, primary, secondary);

			return false;

		} catch (OperationCanceledException) {

			return false;
		}

		document.File = file;
		document.FileType = format.Extensions.First ();

		tools.DoAfterSave (document);

		// Mark the document as clean following the tool's after-save handler, which might
		// adjust history (e.g. undo changes that were committed before saving).
		document.Workspace.History.SetClean ();

		//Now the Document has been saved to the file it's associated with in this session.
		document.HasBeenSavedInSession = true;

		return true;
	}

	// Builds the heading for the "unsupported format" dialogs, leading with the file's
	// extension so the format - the relevant detail - is the emphasized part.
	private static string UnsupportedFormatHeading (string fileName)
	{
		string ext = System.IO.Path.GetExtension (fileName);

		if (string.IsNullOrEmpty (ext))
			return Translations.GetString ("Unsupported format");

		// Translators: {0} is a file extension such as ".pdn".
		return Translations.GetString ("Unsupported format: {0}", ext);
	}

	private async Task<bool> ConfirmFlatten (Document document, FormatDescriptor format)
	{
		// If the format doesn't support layers but there is more than one layer, ask to flatten the image
		if (!format.SupportsLayers
			&& document.Layers.Count () > 1) {

			string heading = Translations.GetString ("This format does not support layers. Flatten image?");
			string body = Translations.GetString ("Flattening the image will merge all layers into a single layer.");

			const int cancel = (int) Gtk.ResponseType.Cancel;
			const int flatten = 1;

			int response = await GtkExtensions.ShowMessageDialogAsync (
				chrome.MainWindow,
				Translations.GetString ("Flatten Image?"),
				heading,
				body,
				[
					(Translations.GetString ("_Cancel"), cancel, GtkExtensions.DialogButtonStyle.Normal),
					(Translations.GetString ("Flatten"), flatten, GtkExtensions.DialogButtonStyle.Suggested),
				],
				defaultResponse: flatten);

			if (response == cancel) {
				return false;
			}

			// Flatten the image
			tools.Commit ();
			image.Flatten.Activate ();
		}
		return true;
	}
}
