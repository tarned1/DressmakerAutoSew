# Dressmaker Auto Sew

Turns the fabric sewing screen into an automatic seam. With **Auto sew** on, opening a fabric's sewing screen feeds the cloth along the stitch line by itself. You do not hold Space or steer. The seam finishes at **100%** quality. **Speed**, under the Auto sew switch, chooses how long that seam takes.

The game is Unity 6 Mono (`6000.3.4f1`, Steam app `4019220`). The plugin runs on BepInEx 5.4.23.5, the Doorstop build that matches this Unity version. Auto sew runs the seam on its own while the switch is on.

## In the pause menu

- **Auto sew** — on by default. Turn it off to sew a seam by hand.
- **Speed** — under Auto sew, and only while Auto sew is on. The left end is the sewing machine's slowest lever and the right end is its fastest, the same kind of bar as the volume sliders.

While a seam is running:

- `W` or Up feeds this piece faster. `S` or Down feeds this piece slower. The pause-menu speed stays the base, so the next piece starts there again.
- The mouse wheel changes speed.
- `R` starts that seam over. It still finishes at 100%.

The same values are stored in `BepInEx/config/com.erann.dressmaker.autosew.cfg` after the first launch.

## Sharing

Give people both files in `dist/`:

- `Dressmaker Vortex Extension-1.1.0.zip`
- `Dressmaker Auto Sew-1.4.0.zip`

`dist/INSTALL.txt` is the install order. The extension adds Dressmaker to Vortex and installs BepInEx. The mod zip is what shows up in the Mods list and deploys `DressmakerAutoSew.dll` to `BepInEx/plugins`.

## Vortex

The extension lives in `%AppData%\Roaming\Vortex\plugins\game-dressmaker`. Restart Vortex, then on the Games page choose **Dressmaker** and manage it. Setup copies BepInEx into the game folder if `winhttp.dll` is not there yet, and copies `DressmakerAutoSew.dll` into `BepInEx/plugins`. Other BepInEx plugin zips install into that same plugins folder.

Source for the extension is `vortex/game-dressmaker` in this project.
