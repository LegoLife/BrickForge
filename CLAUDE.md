# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

BrickForge is a browser-based 3D brick-building sandbox: Blazor WebAssembly (.NET 10) plus a Three.js viewport.

## Commands

```sh
dotnet run --project src/BrickForge.Web          # dev server at http://localhost:5192
dotnet test                                      # all xUnit tests (tests/BrickForge.Core.Tests)
dotnet test --filter "FullyQualifiedName~EditorTests"                       # one test class
dotnet test --filter "FullyQualifiedName~EditorTests.Undo_and_redo_a_placement"  # one test
dotnet publish src/BrickForge.Web -c Release -o publish                     # what CI deploys
```

There is no JavaScript toolchain (no npm or bundler). Three.js is vendored under `src/BrickForge.Web/wwwroot/lib/three`, and the JS files use `// @ts-check` with JSDoc types for editor checking only.

CI (`.github/workflows/pages.yml`) runs `dotnet test` and then deploys to GitHub Pages on every push to `master`. CI uses sed to rewrite the literal `<base href="/" />` in `wwwroot/index.html` to `/BrickForge/`, and fails if that string is not there, so keep it exactly as it is.

## Architecture

**C# owns the model and every rule. JS only draws and reports input.** That split is the main design constraint: put placement, selection and editing logic in `BrickForge.Core`, where it can be unit-tested without a browser, never in `viewport.js`.

### Core (`src/BrickForge.Core`, no UI dependencies)

- **Grid**: `GridPos` uses integer studs on x and z and integer *plates* on y. A brick is 3 plates tall, and plates and tiles are 1. `PlacedPart.Position` is the part's minimum corner *after* rotation, and `PartType.Footprint(rotation)` swaps width and depth at 90° and 270°. `LegoUnits` converts to world units (1 unit = 1 stud pitch).
- **`Build`** holds parts and a cell-occupancy map, and enforces the rules: a part must be on the baseplate, must not overlap anything, and must connect to at least one stud. It connects either by sitting on a studded part or the baseplate, or by its studs pushing into the underside of a part above. Tiles have no studs on top. `CheckGroup` validates several parts together, and a group only needs one part to connect. `Restore` (internal) skips the connection rule; it is used by undo/redo and file loading, where floating parts are legitimate. The baseplate can only be swapped while the build is empty.
- **`Editor`** is the single entry point for every user action: tool state, modes, placing, painting, the eyedropper, selection, the clipboard, moves and undo/redo. Every call raises `Changed(BuildChange)`, with `BuildChange.None` when only tool or selection state changed. A `BuildChange` lists removed ids and added parts. A modified part appears in both lists under the same id, and the view applies removals before additions. That ordering is why `CancelHoldFirst()` raises the end of a hold as its own change.
- **Undo** is a stack of `Step(Before, After, PlateBefore?, PlateAfter?)` that swaps one set of parts for another. Whole-build operations (import, new, clear, resize) are single steps that remove every part, swap the baseplate, and re-add the parts.
- **Held groups** (`HeldGroup`): a *paste* holds the clipboard or a component and stamps a copy on every click until it is cancelled. A *move* holds parts that were picked up. Those parts stay in the `Build`, hidden in the view, and are passed as `ignore` ids so they neither block nor support placement checks. On drop they keep their ids.
- **`PartGroup`** stores parts as offsets from the group's minimum corner. It is used for the ghost preview, the clipboard, moves and components, and `Rotated()` turns the whole group around the vertical axis.
- **`ComponentLibrary`** is kept apart from the build: it survives New and Import, and it has no undo.

### File formats

`BuildFile` (`"format":"brickforge"`) and `ComponentFile` (`"format":"brickforge-components"`) are used both for autosave and for import/export. Each part is a compact array, `["brick-2x4",x,y,z,rotation,colorId]`, for size and speed. Consequences:
- Part ids (`PartCatalog`, generated as `{kind}-{w}x{d}`) and colour ids (`Palette`) are persisted. Never renumber or rename existing ones; only append new ones.
- Serialization uses source-generated `JsonSerializerContext`s so it survives WASM trimming. A new serialized type must be registered with `[JsonSerializable]`.
- On load, parts get fresh ids, and invalid entries throw `BuildFileException` with a message that is shown to the user.
- `SampleFileTests` loads `samples/*.json`, which the README links to, so update the samples if a format changes.

### Web (`src/BrickForge.Web`)

- `Pages/Home.razor` is the whole panel UI. It owns the `Editor` and `ComponentLibrary`, runs a debounced autosave (1 s, only for non-empty changes), and restores the autosave at startup, then calls `ForgetHistory()`.
- `Components/Viewport.razor` is the interop bridge to `wwwroot/js/viewport.js`:
  - **C# → JS**: it forwards each `BuildChange` to JS `update(...)`, together with the current tool state (mode, ghost group, selection). Parts are sent as flat `int[]` arrays (`[id, catalogIndex, x, y, z, rotation, colorId]` per part) because marshalling objects is too slow for large builds. `catalogIndex` is the part's index in `PartCatalog.All`, which is sent to JS once at `createViewport`.
  - **JS → C#**: input arrives through `[JSInvokable]` methods (`Aim`, `CanPlace`, `Click`, `BoxSelect`, `Drop`, `DragEnded`, `Remove`, `Key`). `Aim` is called *synchronously* (`invokeMethod`, which only works in WebAssembly) on every pointer move. JS reports the raycast hit, and `Aiming` in Core decides where the ghost goes. Keyboard shortcuts are mapped to editor calls in `Viewport.Key`, not in JS.
- `Services/BuildStorage.cs` wraps `wwwroot/js/files.js` for localStorage and downloads. If stored data can't be read, it is copied to a `*.unreadable` key so the next save doesn't overwrite it.
- `viewport.js` handles rendering, the camera, raycasting, hover, the ghost preview, box-select, drag-and-drop and thumbnail rendering (`renderThumbnail`). It also skips drawing studs that are covered by other parts.
