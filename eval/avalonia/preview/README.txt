Osmium UI preview (Avalonia 12) — unsigned preview mock
=======================================================

This is NOT Osmium. It is a look-and-feel preview of the user interface Osmium 0.2.0 may move to (Avalonia 12, MIT).
It does no real compression or extraction: every number, file and plan on its pages is made up, and no file you own
is read or changed. It is not signed, so Windows SmartScreen may warn before it starts ("More info", "Run anyway").

Run it
------
1. Unzip the whole folder anywhere (for example your Downloads folder). Keep the files together.
2. Double-click OsmiumUiPreview.exe. Nothing is installed; delete the folder to remove it.
   It writes only to %TEMP%\OsmiumUiPreview (a small icon file, and the stand-in files it drags out).

What to look at (about 15 minutes)
----------------------------------
- Light and dark: switch Windows between light and dark (Settings > Personalization > Colors > Choose your mode).
  The window follows while it runs.
- Your own scaling: whatever Windows uses on your screen (100 %, 125 %, 150 %, 200 %). If you can, also try
  Settings > System > Display > Scale at 150 %, and drag the window to a second monitor if you have one.
- Resize the window down to its minimum, 900 x 600.
- Press Tab repeatedly: every button, the path box, the rail and the lists should show a focus ring in turn.
- The notification area (system tray, beside the clock; it may be under the ^ arrow): right-click the Osmium icon.
  The menu is drawn in the app's colours and follows light or dark. "Show a test notification" sends a Windows
  notification the way finished and failed jobs will; clicking it opens the window. Closing the window keeps the
  preview in the notification area, as Osmium does; use Exit in that menu to quit.
- Drag a row of the Compress page's file list (for example 写真-2025.zip) onto the desktop or into an Explorer window.
  A small text file named after the row appears there (it stands in for the extracted file).
- The Archives page: a folder tree and 100,000 made-up entries. Scroll, sort by Size, switch Grid view / List view.
- Text quality: Avalonia draws text itself (Skia), not with ClearType. Look at small text at your scaling.

Then say "go" or "no-go" on the look and the text quality.

Files
-----
OsmiumUiPreview.exe          the preview
LICENSE.txt                  Osmium's licence (MIT)
THIRD-PARTY-NOTICES.txt      licences of everything else in this folder: Avalonia, SkiaSharp/Skia, HarfBuzz, ANGLE,
                             the fonts, and the Osmium engine's components
SkiaSharp-THIRD-PARTY-NOTICES.txt   SkiaSharp's own notices for the code inside libSkiaSharp.dll / libHarfBuzzSharp.dll
dotnet-LICENSE.txt, dotnet-THIRD-PARTY-NOTICES.txt   the .NET runtime that ships inside this folder
BUILD.txt                    the commit and workflow run this folder was built from
