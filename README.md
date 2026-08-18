# Dave the Diver Ultrawide Mod

An experimental [BepInEx 6](https://github.com/BepInEx/BepInEx) plugin that tries to get rid of
the themed pillarbox bars Dave the Diver renders on ultrawide monitors (e.g. 21:9 3440x1440),
instead of actually using the extra horizontal space.

**Status: core fix confirmed working on a real 3440x1440/21:9 setup — in-game (diving) now renders
across the full width.** Some rough edges remain (main menu still pillarboxed, a brief visual
glitch during the loading-screen transition into a dive) — see
[Known limitations](#known-limitations--open-questions). Developed and tested iteratively; see
[docs/research-notes.md](docs/research-notes.md) for the full reverse-engineering trail.

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

## Requirements

- Dave the Diver (Steam), IL2CPP build
- [BepInEx 6 (Unity.IL2CPP, Bleeding Edge)](https://builds.bepinex.dev/projects/bepinex_be)
  installed into the game folder (this generates the `BepInEx/interop/*.dll` assemblies this
  project builds against — run the game once after installing BepInEx before building)
- .NET SDK 6.0+ to build

On Linux/Steam Deck via Proton, BepInEx's doorstop hook needs a DLL override to actually load,
since Wine ships its own stub `winhttp.dll`. Add this Steam launch option for Dave the Diver:

```
WINEDLLOVERRIDES="winhttp=n,b" %command%
```

## Building

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

## Installing (without building)

Grab `DaveTheDiverUltrawide.dll` from a release (once one exists) and drop it into
`Dave the Diver/BepInEx/plugins/DaveTheDiverUltrawide/`.

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

All patches log what they see/change to `BepInEx/LogOutput.log` (`[Ultrawide]` prefix) so behavior
can be diagnosed without a debugger attached to a Proton process. `CameraResolution.UpdateCameraRect`
and `UpdateWideResolution`/`UpdateAutoResolution` are deliberately **not** patched — see the
"Patch safety findings" in the research notes for why (the latter two froze the game solid, even
just for read-only logging).

## Known limitations / open questions

- **Main menu still shows black bars** — not yet investigated why the menu doesn't take the same
  widened-camera path that gameplay does.
- **Brief visual glitch during the loading-screen transition into a dive**: the center 16:9 area
  renders black while the edges already show the widened game world behind it, and a character
  portrait can appear oversized/cropped for a frame or two. Resolves itself once loading finishes.
  Likely a separate loading-screen UI/camera still anchored to the old 16:9 bounds.
- HUD/UI elements that assume a 16:9 safe area (e.g. restaurant minigame prompts) not yet checked.
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
