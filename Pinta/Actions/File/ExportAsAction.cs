//
// ExportAsAction.cs
//
// Exports the active document to a chosen image format without changing the
// open document (unlike Save As, the original file, file type and dirty state
// are untouched). The format dropdown defaults to PNG.
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
using System.IO;
using System.Threading.Tasks;
using Pinta.Core;

namespace Pinta.Actions;

internal sealed class ExportAsAction : IActionHandler
{
	private readonly FileActions file;
	private readonly ChromeManager chrome;
	private readonly WorkspaceManager workspace;
	private readonly ImageConverterManager image_formats;
	private readonly ToolManager tools;
	internal ExportAsAction (
		FileActions file,
		ChromeManager chrome,
		WorkspaceManager workspace,
		ImageConverterManager imageFormats,
		ToolManager tools)
	{
		this.file = file;
		this.chrome = chrome;
		this.workspace = workspace;
		image_formats = imageFormats;
		this.tools = tools;
	}

	void IActionHandler.Initialize ()
	{
		file.ExportAs.Activated += Activated;
	}

	void IActionHandler.Uninitialize ()
	{
		file.ExportAs.Activated -= Activated;
	}

	private async void Activated (object sender, EventArgs e)
	{
		if (!workspace.HasOpenDocuments)
			return;

		Document document = workspace.ActiveDocument;
		Gtk.Window parent = chrome.MainWindow;

		// Add every format we can export to.
		using Gio.ListStore filters = Gio.ListStore.New (Gtk.FileFilter.GetGType ());
		foreach (var exportable in image_formats.Formats) {
			if (!exportable.IsExportAvailable ())
				continue;
			filters.Append (exportable.Filter);
		}

		// Default the dropdown (and filename extension) to PNG.
		FormatDescriptor png = image_formats.GetFormatByExtension ("png")!;
		string baseName = Path.GetFileNameWithoutExtension (document.DisplayName);

		using Gtk.FileDialog fileDialog = Gtk.FileDialog.New ();
		fileDialog.SetTitle (Translations.GetString ("Export As"));
		fileDialog.SetFilters (filters);
		fileDialog.SetDefaultFilter (png.Filter);
		fileDialog.SetInitialName ($"{baseName}.png");
		fileDialog.Modal = true;

		Gio.File? destination = await fileDialog.SaveFileAsync (parent);
		if (destination is null)
			return;

		string displayName = destination.GetParent ()!.GetRelativePath (destination)!;

		// Follow the extension the user typed, falling back to PNG.
		FormatDescriptor? format = image_formats.GetFormatByFile (displayName);
		if (format is null || !format.IsExportAvailable ())
			format = png;

		if (format.Exporter is not IImageExporter exporter)
			return;

		// Commit any pending tool changes so they are reflected in the export.
		tools.Commit ();

		try {
			// The exporters composite the layers into a throwaway surface
			// (GetFlattenedImage) or write them directly (ORA); the document's
			// layers, File, FileType and dirty state are never modified.
			exporter.Export (document, destination, parent);
		} catch (Exception ex) {
			await chrome.ShowMessageDialog (
				parent,
				Translations.GetString ("Failed to export image"),
				ex.Message);
		}
	}
}
