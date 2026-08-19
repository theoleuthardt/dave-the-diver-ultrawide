# Dave the Diver Ultrawide Mod

An experimental [BepInEx 6](https://github.com/BepInEx/BepInEx) plugin that tries to get rid of
the themed pillarbox bars Dave the Diver renders on ultrawide monitors (e.g. 21:9 3440x1440),
instead of actually using the extra horizontal space.

**Status: core fix confirmed working on a real 3440x1440/21:9 setup — in-game (diving) now renders
across the full width.** HUD/world-tracked UI elements (item pickup prompts etc.) still render as
if the screen were 16:9; two Canvas-based fix attempts didn't work, but native decompilation (see
[docs/research-notes.md](docs/research-notes.md)) found the real mechanism and a third attempt
targeting it is implemented and awaiting real-hardware testing. The sushi restaurant turned out to
run its own, entirely separate camera/UI system with the same root-cause bug (locked camera
aspect); a first patch attempt at it (`EnableSushiBarCameraFix`) **confirmed crashed the game
outright on real hardware** (fatal native AccessViolationException at boot, see
[docs/research-notes.md](docs/research-notes.md)) and is disabled by default pending a safer
implementation. The main menu's pillarbox bars are a content limitation, not fixable via code. See
[Known limitations](#known-limitations--open-questions) for details.

## Background

Dave the Diver (Unity 6000.0.52f1, IL2CPP) already detects ultrawide resolutions and lets you
select them in the options menu — but it keeps the actual play area locked to a 16:9 slice in
the center of the screen and fills the rest with decorative, game-themed side panels rather than
expanding the camera/UI. No existing ultrawide mod fixes this; the only previously-known
workaround ([Steam guide](https://steamcommunity.com/sharedfiles/filedetails/?id=3000737847))
is forcing the game into a 16:9 borderless window with a tool like Borderless Gaming, which
doesn't touch the game's internal resolution handling at all and causes off-center/misplaced UI.

This mod instead hooks the game's own resolution/camera code directly via
[HarmonyX](https://github.com/BepInEx/HarmonyX) Il2Cpp patches, using the class the game itself
uses for this (`CameraResolution`, see research notes) instead of fighting it from the outside.

## Installation

This mod is a [BepInEx](https://github.com/BepInEx/BepInEx) plugin, so BepInEx itself has to be
installed first — it's the loader that actually runs this mod's code inside the game.

### 1. Install BepInEx 6 (IL2CPP)

1. Go to the **[BepInEx 6 Bleeding Edge builds page](https://builds.bepinex.dev/projects/bepinex_be)**.
2. Download the newest **`BepInEx-Unity.IL2CPP-win-x64-...zip`** (IL2CPP + win-x64 — Dave the
   Diver needs this exact variant, not `win-x86` and not a Mono build; use the win-x64 build even
   if you're on Linux/Steam Deck, since it still runs through Proton).
3. Extract the **contents** of the zip directly into your Dave the Diver install folder — the same
   folder that contains `DaveTheDiver.exe` (Steam → right-click Dave the Diver → Manage → Browse
   local files). Afterwards that folder should directly contain a `BepInEx/` subfolder,
   `winhttp.dll`, and `doorstop_config.ini` next to `DaveTheDiver.exe`.

**On Linux/Steam Deck (Proton) only:** Wine ships its own stub `winhttp.dll`, which silently
prevents BepInEx from loading unless you tell Proton to prefer the real one. In Steam, right-click
Dave the Diver → Properties → **Launch Options**, and set it to:

```
WINEDLLOVERRIDES="winhttp=n,b" %command%
```

4. Launch Dave the Diver once through Steam and let it fully reach the main menu, then close it
   again. This first launch is what makes BepInEx generate the `BepInEx/interop/` and
   `BepInEx/plugins/` folders the mod needs — without this step, installing the mod itself won't
   do anything yet.

### 2. Install this mod

1. Grab the newest zip from this repo's **[Releases](../../releases)** page.
2. Extract it into `Dave the Diver/BepInEx/plugins/` so you end up with
   `Dave the Diver/BepInEx/plugins/DaveTheDiverUltrawide/DaveTheDiverUltrawide.dll`.
3. Launch the game. If your monitor's resolution isn't already selected, pick it in
   Options → Display.

To confirm it's working, open `Dave the Diver/BepInEx/LogOutput.log` in a text editor and look for
lines starting with `[Ultrawide]` — those are this mod's own log output.

## Building (for development)

Requires .NET SDK 6.0+ and BepInEx already installed in the game folder as in step 1 above (that's
where the `BepInEx/interop/*.dll` assemblies this project builds against come from).

Uses [Taskfile](https://taskfile.dev) (`brew install go-task` / see [Taskfile.yml](Taskfile.yml)
for the full list). Set `DTD_GAME_DIR` once and building/deploying/log-watching all pick it up:

```sh
export DTD_GAME_DIR="/path/to/steamapps/common/Dave the Diver"

task build          # compile only
task deploy          # copy the last build's DLL+PDB into $DTD_GAME_DIR/BepInEx/plugins/
task dev             # build, then deploy — the usual iteration loop
task log             # tail BepInEx/LogOutput.log live
task log:ultrawide   # just this mod's log lines from the last run
task decompile TYPE=CameraResolution   # re-decompile a game type for further research
```

`GAME_DIR` can also be passed per-invocation (`task deploy GAME_DIR=/other/path`), which overrides
`DTD_GAME_DIR`. Without Task, the plain `dotnet build src/DaveTheDiverUltrawide/DaveTheDiverUltrawide.csproj`
also still works — it just won't deploy for you.

Alternatively, create a gitignored `Directory.Build.local.props` at the repo root instead of using
the environment variable — see [Directory.Build.props](Directory.Build.props) for the format.

## Releasing

The [Release workflow](.github/workflows/release.yml) is manually triggered
(`workflow_dispatch`) and only packages + publishes — it does not compile, since that needs the
game's interop DLLs which aren't (and shouldn't be) available in CI. To cut a release:

```sh
task build CONFIG=Release
task package CONFIG=Release   # copies dll+pdb into dist/DaveTheDiverUltrawide/
git add dist && git commit -m "Package vX.Y.Z"
git push
```

Then run the *Release* workflow from the GitHub Actions tab (or `gh workflow run release.yml -f
version=vX.Y.Z`), which zips `dist/` and publishes it as a GitHub Release.

## How it works (current approach)

See [`UltrawidePatches.cs`](src/DaveTheDiverUltrawide/UltrawidePatches.cs) and
[docs/research-notes.md](docs/research-notes.md) for the full trail. The short version:

1. **`CameraResolution.UpdateCanvasScale` (postfix)** — this is the method that actually
   pillarboxes the game: it computes a centered 16:9 sub-rect and writes it to both the main
   camera's `Camera.rect` and the internal `CameraResolution.m_CameraViewRect` field. The postfix
   forces both back to the full window (`0,0,1,1`) after the original runs.
2. **`LetterBoxModifier.Awake` / `RecalcAnchorRect` / `SetAnchorRect` (postfix)** — hides the
   decorative side-panel GameObjects (`MaskLeft`/`MaskRight`) so they don't paint over the
   now-widened view.
3. **`CameraResolution.SetResolution` (prefix)** — can also spoof the internal `k_TargetRatio`
   field, but this is **confirmed to crash the game** on scene load and is OFF by default
   (`EnableTargetRatioSpoof`, see `Plugin.cs`). Not needed for the actual fix — left in as a
   documented dead end so nobody retries it blindly.
4. **`InputActionIndicatorPanel.LateUpdate` (prefix)** — targets the item-pickup-prompt
   mispositioning specifically: forces the camera used by the prompt's `WorldToViewportPoint` call
   to a full rect and un-locks its aspect (`Camera.ResetAspect()`) every frame, right before the
   game's own positioning code runs. Not yet confirmed working — see Known limitations.

All patches log what they see/change to `BepInEx/LogOutput.log` (`[Ultrawide]` prefix) so behavior
can be diagnosed without a debugger attached to a Proton process. `CameraResolution.UpdateCameraRect`
and `UpdateWideResolution`/`UpdateAutoResolution` are deliberately **not** patched — see the
"Patch safety findings" in the research notes for why (the latter two froze the game solid, even
just for read-only logging).

## Known limitations / open questions

- **Main menu still shows black bars — this is a content limitation, not a bug.** The widened
  camera from boot persists into the menu (particle effects visibly render into the bar area), but
  the title-screen background art itself was never painted past the original 16:9 bounds. Fixing
  this needs new art, not a code patch.
- **HUD and world-tracked UI elements (item pickup prompts, notification banners, etc.) still
  render as if the screen were 16:9.** Two Canvas-size-based fix attempts didn't work
  (`EnableCanvasResizeFix` was actively harmful on real hardware; `EnableCanvasScalerFix` was a
  no-op) — native decompilation (Cpp2IL, see [docs/research-notes.md](docs/research-notes.md),
  "The HUD problem") revealed why: the positioning is anchor-*fraction* based
  (`Camera.WorldToViewportPoint` → `RectTransform.anchorMin`/`anchorMax`), which doesn't depend on
  canvas size at all — both attempts were targeting the wrong layer. A third attempt
  (`EnableIndicatorCameraFix`) targets the actual mechanism (forces the camera used by
  `WorldToViewportPoint` to a full rect + `Camera.ResetAspect()` every frame, right before the
  game's own code uses it) — implemented and deployed, but **not yet confirmed working on real
  hardware**. Both older experimental patches are left in the code, OFF by default — do not enable
  them for normal play; only `EnableIndicatorCameraFix` (and its `EnableCameraManagerCrossCheck`
  sub-part) are worth testing here.
- **Brief visual glitch during the loading-screen transition into a dive**: the center 16:9 area
  renders black while the edges already show the widened game world behind it, and a character
  portrait can appear oversized/cropped for a frame or two. Resolves itself once loading finishes.
  Not yet investigated.
- Cutscenes not yet checked.

## Contributing / testing

Run the game with the mod installed, then check `Dave the Diver/BepInEx/LogOutput.log` for lines
prefixed `[Ultrawide]`. Screenshots of before/after plus that log are the most useful bug report.

## Credits

- Built with [BepInEx](https://github.com/BepInEx/BepInEx) and
  [HarmonyX](https://github.com/BepInEx/HarmonyX).
- Class names/structure identified via `ilspycmd` decompilation of the BepInEx-generated
  `Assembly-CSharp.dll` interop assembly — see [docs/research-notes.md](docs/research-notes.md).

## License

MIT, see [LICENSE](LICENSE).
