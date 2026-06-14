
# Pinta - [Simple Gtk# Paint Program](http://pinta-project.com/)

This is a fork of [PintaProject/Pinta](https://github.com/PintaProject/Pinta) with some added features and Windows-specific fixes. See [Differences from upstream](#differences-from-upstream) below.

Pinta is a GTK clone of [Paint.Net 3.0](http://www.getpaint.net/), with support for Linux, Windows, and macOS.

Original Pinta code is licensed under the MIT License:
See `license-mit.txt` for the MIT License

Code from Paint.Net 3.36 is used under the MIT License and retains the
original headers on source files.

See `license-pdn.txt` for Paint.Net's original license.

## Differences from upstream

This fork adds the following on top of upstream Pinta:

- **Non-destructive `File > Export As...` (Ctrl+E)** - exports the active document to any supported format without touching the document's file/dirty state or flattening the working layers. Long-requested on the Paint.NET forums: [110653](https://forums.getpaint.net/topic/110653-save-as-should-not-force-me-to-flatten-my-image/), [124416](https://forums.getpaint.net/topic/124416-feature-request-export-png-without-having-to-flatten-layers/), [119152](https://forums.getpaint.net/topic/119152-is-there-anyway-to-save-a-png-without-flattening/), [121384](https://forums.getpaint.net/topic/121384-export-as-png-without-save-as/), [113986](https://forums.getpaint.net/topic/113986-request-change-save-as-behaviour/).
- **Native Windows title bar** - uses the native Win32 title bar instead of GTK4/libadwaita client-side decorations:
  - Native min/max/close buttons, Aero Snap, and edge resize.
  - Correct HiDPI scaling on fractional-scaled displays.
  - Fixes the related window-stability bugs this introduces: staying maximized correctly on launch (without getting stuck at monitor size), avoiding the GTK4/DPI layout bug, and keeping a maximized window stable when opening menu-bar popovers.
  - Keeps the main window in front when a modal dialog is dismissed: previously, dismissing a dialog (such as "Save changes before closing?" or an effect's settings) after switching to another app and back dropped the main window behind that other app.
  - Modal file dialogs (Save As, Export As, Save Palette) are properly modal to the main window: clicking the main window's X (or other title-bar buttons) while one is open gives the standard Windows behaviour (system ding + the dialog's title bar flashes) instead of starting to close the app underneath the open dialog. These use the modern `Gtk.FileDialog` API rather than the legacy file chooser, which wasn't modal on Windows.
  - The other modal dialogs (effect/adjustment dialogs and message prompts like "Save changes before closing?", flatten, and unsupported-format) are likewise modal to the main window: while one is open the main window is disabled, so clicking its title-bar buttons gives the system ding + a flash of the dialog instead of starting to close the app underneath. The message prompts were switched from `Adw.MessageDialog` (always client-side decorated) to a shared native `Gtk.Dialog` helper so they get the native title bar and flash like the rest, rather than being the only client-side-decorated dialogs in the app.
  - Clicking the native title bar (X, minimize/maximize, or the caption) while a menu is open dismisses the menu and performs the click, instead of the click being swallowed by the menu's grab and the title bar appearing dead. GDK can't see clicks on the native frame, so this is done with a low-level mouse hook that runs only while a menu is open.
  - Dark title bar on dialogs too (e.g. effect settings), so they match Pinta's dark UI instead of defaulting to a light Windows title bar.
  - Addresses upstream issues [#1528](https://github.com/PintaProject/Pinta/issues/1528), [#1465](https://github.com/PintaProject/Pinta/issues/1465), [#964](https://github.com/PintaProject/Pinta/issues/964), [#1999](https://github.com/PintaProject/Pinta/issues/1999) and [#1365](https://github.com/PintaProject/Pinta/issues/1365) (HiDPI).
- **`File > Open Recent` menu** - a hover-opening flyout listing recently opened/saved files (most recent first, up to 10), each with a thumbnail preview (rendered via Pinta's own loaders, so layered `.ora` and Paint.NET `.pdn` files are previewed too, not just the formats GDK can decode), plus a "Clear List" entry. The list is persisted between sessions, and entries that can no longer be opened are dropped automatically. Because GTK4 menus can't display images or host custom widgets inside a submenu, the flyout is a custom widget slotted into the menu, which requires Pinta to host its own menu bar.
- **Friendly "file not found" dialog** - opening a file that no longer exists (e.g. a moved or deleted recent file) shows a simple "could not be found" message with the path wrapped at directory separators, instead of the generic crash-style error dialog with a "Report Bug" button.
- **Open Paint.NET `.pdn` files** - imports Paint.NET's native layered format (layers, names, visibility, opacity, and supported blend modes), so `.pdn` files can be opened directly rather than only re-exported from Paint.NET. Import only; the four blend modes Pinta does not implement (Additive, Reflect, Glow, Negation) fall back to Normal. Because the format can't be written, saving a `.pdn` prompts to Save As in a writable format (such as `.ora`) instead of overwriting it.
- **Reliable "save changes before closing?" on quit** - closing the window with unsaved work now prompts for each unsaved image in turn and then actually closes the app (Paint.NET behaviour), with Cancel on any prompt aborting the quit. Previously the quit handler didn't wait for the asynchronous save prompt, so it gave up after the first image: clicking Discard closed only the current image and left the window open.
- **Top layer selected on open** - opening a multi-layer image selects the topmost layer (as other image editors do) instead of the bottom layer, so the first edit lands on the visible top of the stack.
- **Tool order matching Paint.NET** - the toolbox is reordered to match the Paint.NET layout. Addresses upstream issues [#1219](https://github.com/PintaProject/Pinta/issues/1219) and [#2127](https://github.com/PintaProject/Pinta/issues/2127).
- **Cursor/stroke offset fix** - corrects the cursor and stroke position when the window is smaller than the canvas. Addresses upstream issue [#2165](https://github.com/PintaProject/Pinta/issues/2165).
- **Windows dev install + release tooling** - `install-windows-dev.ps1` builds a self-contained app and installs over a system Pinta (preserving user-added files like addin DLLs), and CI publishes a Windows installer to GitHub Releases on tag.

## Icons are from:

- [Paint.Net 3.0](http://www.getpaint.net/)
Used under [MIT License](http://www.opensource.org/licenses/mit-license.php)

- [Silk icon set](https://github.com/markjames/famfamfam-silk-icons)
Used under [Creative Commons Attribution 3.0 License](http://creativecommons.org/licenses/by/3.0/)

- [Fugue icon set](https://p.yusukekamiyamane.com)
Used under [Creative Commons Attribution 3.0 License](http://creativecommons.org/licenses/by/3.0/)

- Pinta contributors, under the same license as the project itself
(see `Pinta.Resources/icons/pinta-icons.md` for the list of such icons)

## Building on Windows

First, install the required GTK-related dependencies:
- Install [MSYS2](https://www.msys2.org)
- From the CLANG64 terminal, run `pacman -S mingw-w64-clang-x86_64-libadwaita mingw-w64-clang-x86_64-webp-pixbuf-loader`.
  - For ARM64 Windows, use the `CLANGARM64` terminal and replace `clang-x86_64` with `clang-aarch64`.

Pinta can then be built by opening `Pinta.sln` in [Visual Studio](https://visualstudio.microsoft.com/).
Ensure that .NET 8 is installed via the Visual Studio installer.

For building on the command line:
- [Install the .NET 8 SDK](https://dotnet.microsoft.com/).
- Build:
  - `dotnet build`
- Run:
  - `dotnet run --project Pinta`

## Building on macOS

- Install .NET 8 and GTK4
  - `brew install dotnet-sdk libadwaita adwaita-icon-theme gettext webp-pixbuf-loader`
  - For Apple Silicon, set `DYLD_LIBRARY_PATH=/opt/homebrew/lib` in the environment so that Pinta can load the GTK libraries
  - For Intel, you may need to set `DYLD_LIBRARY_PATH=/usr/local/lib` when using .NET 9 or higher
- Build:
  - `dotnet build`
- Run:
  - `dotnet run --project Pinta`

## Building on Linux

- Install [.NET 8](https://dotnet.microsoft.com/) following the instructions for your Linux distribution.
- Install other dependencies (instructions are for Ubuntu 22.10, but should be similar for other distros):
  - `sudo apt install autotools-dev autoconf-archive gettext intltool libadwaita-1-dev`
  - Minimum library versions: `gtk` >= 4.18 and `libadwaita` >= 1.7
  - Optional dependencies: `webp-pixbuf-loader`
- Build (option 1, for development and testing):
  - `dotnet build`
  - `dotnet run --project Pinta`
- Build (option 2, for installation):
  - `./autogen.sh`
    - If building from a tarball, run `./configure` instead.
    - Add the `--prefix=<install directory>` argument to install to a directory other than `/usr/local`.
  - `make install`

## Building and Debugging in Docker

Follow the instructions of the corresponding [pinta-virtual-dev-environment](https://github.com/janrothkegel/pinta-virtual-dev-environment) project

## Getting help / contributing:

- You can get [technical help](https://github.com/PintaProject/Pinta/discussions).
- You can report [bugs/issues](https://github.com/PintaProject/Pinta/issues).
- You can make [suggestions](https://github.com/PintaProject/Pinta/discussions/categories/ideas).
- You can help [translate Pinta to your native language](https://hosted.weblate.org/engage/pinta/).
- You can fork the project on [Github](https://github.com/PintaProject/Pinta).
- You can get help in #pinta on irc.gnome.org.
- For details on notable changes of each release, take a look at the [CHANGELOG](https://github.com/PintaProject/Pinta/blob/master/CHANGELOG.md).
- For details on patching, take a look at `patch-guidelines.md` in the repo.
