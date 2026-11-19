# SnapMeasure 2.0 – Mac / cross-platform plan

The plan for bringing SnapMeasure to macOS (and later moving Windows onto the same code).
**If you are an AI assistant picking this up:** read this whole file first, then start at *Phase 1*.
The owner is new to git: explain every git/GitHub step plainly, one click at a time.

## Where things stand (Windows 1.2)
- `main` = **SnapMeasure 1.2** for Windows, released on the [Releases](../../releases) page. **Keep `main` and the 1.0 release untouched** until 2.0 is ready.
- Built with C# 5 / Windows Forms on .NET Framework 4 (`build.cmd`, no SDK), a single portable `SnapMeasure.exe`.
- `SnapMeasure.exe --selftest <folder>` runs 108 built-in checks. They are the reference behaviour for the port.

| File | What it holds | Reuse on Mac |
|---|---|---|
| `src/Model.cs` | Shapes, geometry, measuring (dimension, area, angle, scale), rendering, `.snapm` project file (XML, hardened), effects | **High** – core logic; drawing code moves from GDI+ to SkiaSharp/Avalonia |
| `src/Output.cs` | File-name pattern, GIF/ICO/PDF export, About window | **High** for naming/exports, the About window is rebuilt |
| `src/Canvas.cs` | Editor canvas: tools, selection, undo, crop, rulers/guides, clipboard | **Medium** – the logic carries over, the input/painting is rebuilt |
| `src/Editor.cs` | Editor window: toolbars, style bar, scale panel, save | Rebuild in Avalonia |
| `src/Capture.cs`, `src/Native.cs` | Windows screen capture, hotkeys, window list (Win32) | Replace with macOS APIs |
| `src/Overlays.cs` | On-screen ruler (calibratable) & protractor (layered windows) | Rebuild as macOS overlay windows |
| `src/App.cs` | Tray, settings (`.ini` next to exe), dialogs, icons, self-test | Split: shared settings/self-test, platform tray/menu bar |

## Decisions already made
- **Toolkit:** [Avalonia UI](https://avaloniaui.net) on **.NET 8** (C#), one codebase for macOS and Windows (Linux possible later).
- **Repo:** same repository, new branch **`cross-platform`**. Merge into `main` only when 2.0 is released.
- **Version:** release as **SnapMeasure 2.0**.
- **Apple Developer Program ($99/yr):** buy it **only at release time** (Phase 5). Until then, test with local / ad-hoc signed builds.
- **Development setup:** a Mac with Xcode and the .NET 8 SDK; on Windows, prefer portable (no-admin) tool installs.
- **Author / licence:** MIT, "Copyright (c) 2026 sARATH". GitHub account `sARATH-3D`. Commits should use the GitHub no-reply e-mail of that account, never a personal address.

## Phases

### Phase 1 – Shared core (no UI)
- Create a .NET 8 class library `SnapMeasure.Core` and move the platform-free parts into it:
  - shapes and geometry;
  - measuring: dimension, area, perimeter, 3-point angle, scale and calibration, marker series;
  - the `.snapm` read/write, including the input hardening: no DTD/XXE, shape sanitising, image size limits;
  - file-name patterns.
- Port the self-tests to an xUnit (or similar) test project. All of today's checks must pass.
- A `.snapm` saved by 1.0 must open in 2.0, and the other way round where possible.

### Phase 2 – Cross-platform editor (Avalonia)
Rebuild the editor window with the same layout as 1.0:
- tool bar on the left grouped under headings, style bar above the canvas, step-by-step status-bar guide, settings window (Capture / Output / About), scale & measurements panel on the right (toggle), status bar with the version;
- all tools: select / multi-select, dimension, area with cut-outs, path length, perimeter, angle, calibrate, scale presets, counts with labels, polygon, highlight (4 modes), rectangle, ellipse, line/arrow (heads none/end/start/both), freehand, text (bold/italic/align), speech bubble, number + letter markers, revision cloud with callouts, stamps (typed in place), text boxes with frames, text markup, notes (not printed), coordinates (origin, Y up), line styles incl. cloud, 10 line ends (also on freehand), highlight shapes and pixelate mode, pixelate, adjustable crop;
- rulers and guides, undo/redo, copy/paste of shapes and pictures, draw order;
- rotate / mirror / effects;
- save: PNG, JPG, BMP, GIF, TIFF, PDF, ICO, plus `.snapm`.

Run it on Windows first, then on the Mac.

### Phase 3 – macOS features
| Windows 1.0 | macOS 2.0 |
|---|---|
| Region / window / screen capture (GDI) | ScreenCaptureKit (macOS 12.3+). Ask for **Screen Recording** permission with a clear first-run explanation. |
| PrtSc / Ctrl+Shift+1…4 hotkeys | ⌘⇧1…4 style global hotkeys (avoid clashing with macOS ⌘⇧3/4/5). Needs **Accessibility** permission. |
| Tray icon | Menu-bar icon with the same menu |
| On-screen ruler & protractor | Borderless transparent always-on-top windows |
| Ctrl shortcuts | ⌘ shortcuts (⌘S, ⌘⇧S, ⌘C/V/X, ⌘Z …) |
| Settings `.ini` next to exe | `~/Library/Application Support/SnapMeasure/` |
| Window-title file names (`2026-11-18 11_34_09-<app title>.png`) | Front app / window name from ScreenCaptureKit |
| Per-monitor DPI handling | Retina scaling (backing scale factor) |

### Phase 4 – Automatic Mac build
- GitHub Actions on a `macos-latest` runner builds `SnapMeasure.app`, as a universal build (Apple Silicon + Intel) if practical.
- Each push to `cross-platform` produces a downloadable artifact for the owner to test on the Mac.
- Optionally also build the Windows 2.0 exe from the same code.

### Phase 5 – Release 2.0
1. Buy the Apple Developer Program.
2. Sign with a Developer ID, notarise, and staple.
3. Package as `SnapMeasure-2.0-mac.dmg`.
4. Merge `cross-platform` → `main`, tag `v2.0`, and publish the dmg (plus the Windows build) on Releases.

## Done when
- Every 1.0 feature works on the Mac, and the ported self-tests pass on both platforms.
- macOS opens the downloaded app without warnings (signed and notarised).
- 1.0 projects (`.snapm`) open in 2.0.
