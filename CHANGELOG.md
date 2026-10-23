# Changelog

## 1.6.1

- Fixed: Solve stopped with "exceeds the supported placement grid size" on very long fabric. AutoSew now only plans on the part of the fabric that is open on the table.
- Clearer messages when pieces don't fit: AutoSew now tells you whether the fabric is too short or you need to trim it to open more.
- Pieces are packed much tighter: after placing, AutoSew slides each piece as close to its neighbours as the game allows, with no orange "too close" warnings.
- Very large patterns: the biggest pieces are always placed first, and big dresses get a bit more search time for a tighter layout.
- Optimize no longer shows a confusing warning after a tightly packed Solve.

## 1.6.0 – Pattern placement overhaul

- Solve no longer freezes the game. Placement runs in the background, so you can keep moving the camera while it works.
- Large patterns are fully placed. Dresses with many pieces no longer run out of time or leave pieces unplaced.
- About twice as fast, with tighter layouts that use less fabric.
- Pieces keep a safe distance from each other, so no more orange "too close" warnings.
- The status message now updates on its own when placement finishes.
- Leaving the cutting room during placement cancels it cleanly instead of getting stuck.

## 1.5.19

### Pause menu
- Closing Automation, including with Esc, clears the grey shade so the pause menu can be clicked again. The Automation close button does not change the language.

### Measuring and mannequin
- Auto measure and Auto mannequin stay off until you press F4 on that screen. F4 again, the Stop (F4) row, or turning the switch off cancels the pass. F4 does not start while the game is paused, and it does not start sewing or layout. Confirm stays for you to press.
- If a pass cannot finish, it stops and puts the tape and camera away. Turning the switch off does the same, including when the measuring scene has stopped updating. Leaving the scene or a new customer stops it too.
- A finished measurement lets the camera zoom out again, and a finished pass does not pull the tape out a second time.
- A size is written only when the tape reading stayed still. On the dress form, the tape is lined up with the bust, waist, and hip stripes before a size is recorded. Hips stay 0.1 cm under the body measurement.

### Layout
- Solve searches the full length of fabric you own, including the part already used at the start of the bolt. A fresh long roll, including a 20 m bolt, fits the placement grid.
- If a piece does not fit on a corner, the search tries the gaps between pieces. Large pieces go down first, then the gaps are filled.
- Optimize starts from the pieces already on the cloth, including ones you placed by hand. It keeps a result only when more pieces fit, or the same pieces use at least 2 mm less length.
- Solve and Optimize ignore a pattern that has already been cut. Each solve reads the game's current hole masks. A discarded slice is skipped, a hole that crosses the cloth edge is clipped, and a hole off the remaining cloth is ignored.
- If one placement candidate overlaps or sits off the cloth, that candidate is skipped and the valid pieces are still placed. Pieces that do not fit go back to the pattern menu.

### Sewing
- While Auto sew is running and the cloth is feeding, the hand follows the cloth. At the reach limit, when the cloth stops, or when the game is paused, the hand goes back to its start and takes a new grip.
- If a seam is invalid, or auto sew fails on a piece, that piece is left to the game.

## 1.5.18

### Pause menu
- Auto sew, Speed, Auto measure, Auto mannequin, and Place patterns are in an Automation menu. The pause menu has an Automation entry beside Language, in the same style. Pressing it opens that menu in its own window, the same way Language does. The window uses the language menu's font and one text size. Opening it darkens the whole screen, the same way Language does. Turning Auto sew on or off changes the window's height so the Speed row can show. The width stays the same. The window's close button returns to the pause menu.
- The pause menu scrolls with the mouse wheel, by dragging, and with the bar on the right when the list is taller than the screen.

## 1.5.17

Auto sew is unchanged. It is still on by default, Speed is still under that switch, and a seam still finishes at 100%. W, S, the mouse wheel, and R behave the same way during a seam.

The pause menu has three new switches. They start off.

### Auto measure
- Takes the measuring tape out, then holds it on the bust, waist, and hips so the game records each one.
- Places the bust tape on the chest.
- Matches the numbers to the same tenth the game shows.
- Leaves Confirm for you to press.

### Auto mannequin
- Waits until the measuring tape is out, then turns the bust, waist, and hip gears. The form, the tape, and the numbers all move with the gears.
- Bust and waist match the body measurement. Hips are set 0.1 cm under the body measurement.
- Leaves Confirm for you to press.

### Pause menu
- Turning Auto measure off no longer moves the accessory rotation gizmo.
- Letting go of the mouse while Auto measure or Auto mannequin is working no longer zooms the camera out.

### Place patterns
- Adds a Layout tab in the cutting room, in the same sidebar as Patterns and Fabric.
- Nothing is placed until you press Solve. Reset sends the pieces back to the pattern menu.
- The fabric already on the table is included. You can use 1 to 3 fabrics. The last fabric cannot be removed, and the same fabric cannot be added twice.
- The Fabrics row shows how many are in use. Starred fabrics are listed first. More than one star is allowed.
- With more than one fabric, pieces are grouped as Blouse, Skirt, Collar, and Sleeves. Left click opens a group. Right click changes that group or piece to another fabric you added. The fabric name is shown on the right.
- In the fabric list, left click chooses the fabric.
- Solve keeps 100% grain, keeps pieces on the cloth, and does not overlap them. Pieces sit close to the edge of the fabric.
- If a piece does not fit, it goes back to the pattern menu and the page says how many were left out because of lack of space.

### Settings
- The config file is now `BepInEx/config/com.Venus.dressmaker.autosew.cfg`. A 1.4.0 config named `com.erann.dressmaker.autosew.cfg` is left in place and is not read, so Speed starts from the default until you set it again.

### Install
- Vortex: install Dressmaker Vortex Extension 1.1.0 first, then Dressmaker Auto Sew 1.5.17. The extension is unchanged.
- Dressmaker Mod Loader: the loader already has BepInEx. Extract the Mod Loader zip into the Dressmaker folder so `DressmakerAutoSew.dll` lands in `BepInEx\plugins` next to `DressmakerMods.dll`. Do not install the Vortex extension on top of the Mod Loader.

## 1.4.0

- First public release: Auto sew feeds the fabric along the stitch line by itself and finishes every seam at 100% quality, with an adjustable speed.
