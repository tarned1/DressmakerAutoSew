# AutoSew for Dressmaker

A BepInEx mod for the Unity game [Dressmaker](https://store.steampowered.com/app/4019220/) that automates the repetitive parts of dressmaking: sewing seams, measuring clients, sizing the mannequin, and laying pattern pieces out on fabric.

**Download:** [Nexus Mods](https://www.nexusmods.com/dressmaker/mods/56) · **Changelog:** [CHANGELOG.md](CHANGELOG.md)

<!-- TODO: add a GIF of Solve placing the pieces here -->

## Features

| Feature | What it does |
|---|---|
| **Auto sew** | Feeds the fabric along the stitch line by itself, with the sewing animation, and finishes every seam at 100% quality. Speed is adjustable. |
| **Auto measure** | Press F4 on the measuring screen: the tape is placed on the bust, waist and hips, and each value is recorded. |
| **Auto mannequin** | Press F4 on the mannequin screen: the gears turn until the form matches the client's measurements. Confirm stays with the player. |
| **Pattern layout** | A Layout tab in the cutting room places every pattern piece on up to three fabrics, with correct grain and no overlaps, then packs them tightly. |

All switches are in the pause menu under **Automation**.

## Installation

| Download | For | Requirements |
|---|---|---|
| **Mod Loader** | Players using Yuuma's Dressmaker ModLoader | The Mod Loader |
| **Standalone** | Everyone else, including Linux and Steam Deck | None. BepInEx 5.4.23.5 is included |

- **Mod Loader:** in the Mod Loader, press *Install/Update Mod* and choose the file, then restart the game.
- **Standalone:** extract the zip into the Dressmaker folder (the one with `Dressmaker.exe`).
- **Linux / Steam Deck:** if AutoSew doesn't show up in the pause menu, set the Steam launch options to `WINEDLLOVERRIDES="winhttp=n,b" %command%`.
- **Already have BepInEx?** Copy `DressmakerAutoSew.dll` into `BepInEx/plugins`.

Don't install the Standalone file on top of the Mod Loader: it would overwrite the loader's own BepInEx files.

## How it works

AutoSew is a BepInEx plugin. It uses **Harmony** to hook into the game's own methods and **reflection** to read and change the game's internal state.

| Game method | Hook | Feature |
|---|---|---|
| `SewingController.Update` | prefix (replaces the game's update while auto sew runs) | Auto sew |
| `PersonMeasurer.Update` | postfix | Auto measure |
| `Gear.Update` | postfix | Auto mannequin |
| `CuttingRoom.Update` | postfix | Pattern layout and the Layout tab |
| `PauseMenu.OnEnable` / `Hide` | postfix | The Automation window |

Every hook is installed defensively: if a game update renames a method, that feature logs a warning instead of crashing the mod.

### Pattern layout

The layout problem, packing irregular shapes onto fabric, is a form of 2D nesting, which is NP-hard. AutoSew uses a heuristic that gives good layouts in bounded time:

1. **Rasterize.** The open part of the fabric becomes a grid of 3 mm cells. Each piece becomes a *stamp*: the set of cells it covers, once per orientation the grain allows.
2. **Greedy placement.** Pieces are placed one at a time, biggest first, each at the best free corner, the one that uses the least fabric length.
3. **Several orders.** Different orderings of the pieces are tried, and the best layout wins.
4. **Improve and settle.** Each piece is lifted out and re-placed if a better spot exists.
5. **Snap.** After placement, each piece is slid closer to its neighbours using the game's own collision check, so pieces end up as close as the game allows.

The search runs on a **background thread**, so the game keeps running while it works. Everything that touches Unity objects stays on the main thread.

## Performance work

Measured on a 28-piece dress on a 7.6 m bolt. Each row is one problem found by profiling with timing logs.

| Problem | Cause | Fix | Result |
|---|---|---|---|
| Every Solve hit the 4 s time cap | The cap hid the real cost of each phase | Timing logs per phase | Found where the time went |
| Each piece order took ~300 ms | A full-fabric gap search ran on every order instead of once | Run it only on the first order | 80 orders in ~1.9 s |
| Only 5–21 of 28 pieces placed per order | No placement anchor existed on fresh cloth past curved pieces | Always offer the first empty column | **28/28 placed** |
| Orders used 3.8 s, leaving no time to polish | Too many random orders for big dresses | Fewer orders for large inputs | Orders done in 0.6 s |
| Pose search: 1849 ms | Candidates were evaluated even when they couldn't win | Prune candidates that can't beat the best | **165 ms** |
| Repaint: 1285 ms | Every cell visited 8 neighbours to draw a piece's margin | Precompute each piece's margin ring once | **188 ms** |
| The game froze during Solve | The search ran on Unity's main thread | Move the search to a background task | **No freeze** |

Overall: from capped at 4 s with missing pieces, to about **1.7 s with every piece placed**.

## Building

Requirements: the .NET SDK and a copy of Dressmaker.

1. Copy `BepInEx.dll` and `0Harmony.dll` from BepInEx 5.4.23.5 (`BepInEx/core`) into a `lib/` folder in the project root.
2. The project finds the game's Unity DLLs automatically in the default Steam folder on Windows and Linux. For another location, change `DressmakerManaged` in `DressmakerAutoSew.csproj`.
3. Run `dotnet build`. The plugin is written to `bin/DressmakerAutoSew.dll`.

The game's own files are not part of this repository.

## Project structure

```
src/
  Plugin.cs            entry point: settings, Harmony hooks, F4 key
  GameAccess.cs        reflection helpers for the game's fields and methods
  AutoSew.cs           auto sew
  AutoMeasure.cs       auto measure
  AutoMannequin.cs     auto mannequin
  AutoPlace.cs         pattern layout: solver, placement and snap
  PlaceBoard.cs        the Layout tab in the cutting room
  PauseMenuOptions.cs  the Automation window in the pause menu
vortex/                Vortex extension for Dressmaker
```

## Roadmap

- **Refactor** (in progress on the `refactor` branch): split `AutoPlace.cs` into solver, placement and UI parts, keep all game field names in one place, and add tests for the solver.
- **2.0:** Cut all, and placing cut pieces on the mannequin.

## Credits

- Built with [BepInEx](https://github.com/BepInEx/BepInEx) and [HarmonyX](https://github.com/BepInEx/HarmonyX).
- Thanks to the players on Nexus whose detailed bug reports made many of these fixes possible.

AutoSew is an unofficial fan-made mod and is not affiliated with the developers of Dressmaker.
