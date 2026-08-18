# Research notes

## The fix (confirmed working, v0.4.0)

`CameraResolution.UpdateCanvasScale` is the actual mechanism: it computes a centered 16:9 sub-rect
and writes it to both the main camera's `Camera.rect` and the `CameraResolution.m_CameraViewRect`
field. Confirmed by logging camera state before/after on a real 3440x1440 run:

- Right after `SetResolution` returns: `camera.rect=(0,0,1,1)`, `pixelRect=3440x1440`,
  `aspect=2.39` — full window, not yet constrained.
- By the end of the very next `UpdateCanvasScale()` call: `camera.rect=(0.13,0,0.74,1)`,
  `pixelRect=440,0,2560x1440`, `aspect=1.78` — a centered 2560x1440 (exactly 16:9) slice, pillared
  by 440px on each side. `UpdateCanvasScale` is what applies the pillarbox, not `SetResolution`
  itself and not `UpdateCameraRect` (which is never even called, see below).

The fix is a `HarmonyPostfix` on `UpdateCanvasScale` that, after the original runs, forces both
`CameraResolution.m_CameraViewRect` and `MainCamera.rect` back to `(0,0,1,1)`, combined with
hiding the `LetterBoxModifier` side panels so they don't paint over the now-widened view. See
`UltrawidePatches.UpdateCanvasScale_Postfix` for the implementation.

**Confirmed on a real 3440x1440/21:9 run:** in-game (diving) now renders across the full width.

**Known remaining issues:**
- **Main menu still shows black bars — confirmed to be a content limitation, not a bug.**
  `CameraResolution`'s widened camera from boot (`DR_Start`) persists across all scenes including
  the menu (confirmed: particle effects — birds, smoke on the title screens — visibly render into
  the bar area). The bars themselves are genuine empty space: the title-screen background art
  (Blue Hole / Jungle DLC scenes) was simply never painted past the original 16:9 bounds. No
  `LetterBoxModifier`-style covering panel is involved for these specific screens (`DR.Title.TitleManager`
  / `EvilFactory.MainMenuManager` were checked; neither holds a relevant camera or background-image
  reference). Fixing this would need new background art, not a code patch — treated as a permanent
  limitation.
- **HUD/world-tracked UI elements render for a 16:9 canvas, not the real screen — attempted a fix,
  made it worse, reverted.** See "The HUD problem" below for the full story.
- Loading-screen transition glitch (brief black center + oversized character portrait during the
  transition into a dive) not revisited after the HUD investigation; still open.

## The HUD problem

The core camera fix (above) only widens the 3D/2D *world* rendering. Confirmed via a live Canvas
dump (`UltrawidePatches.LogCanvases`, enumerating `Object.FindObjectsOfType<Canvas>()` plus each
canvas's direct children) that HUD/UI canvases are a separate, inconsistent story:

- Canvases **with a `CanvasScaler`** (`MainCanvas`, `TalkCanvas`, `LobbyMainCanvas`, …;
  `referenceResolution=(1920,1080)`, `screenMatchMode=MatchWidthOrHeight`, `matchWidthOrHeight=1`)
  correctly end up at `width=2580` in canvas units on our 3440x1440/2.39-aspect run — Unity's own
  CanvasScaler math already accounts for the widened camera correctly, no patch needed.
- Canvases **without a `CanvasScaler`** — confirmed: `InteractionRoot` (world-tracked interact/pickup
  prompts — the exact one behind the reported item-pickup-button mispositioning),
  `CutsceneUI`, `DamageTextPoolPanel`, `EmojiPanel` — stay hardcoded at `1920x1080`, centered,
  regardless of the real camera size. `PauseMenuPanel` is `RenderMode.WorldSpace`, a different
  case entirely, not investigated.

**Tried:** `EnableCanvasResizeFix` — a `HarmonyPostfix` on `UpdateCanvasScale` that, for any
scaler-less `ScreenSpaceCamera` canvas, sets `RectTransform.sizeDelta` directly to
`(camera.pixelWidth, camera.pixelHeight)`. Deliberately conservative: doesn't touch the
canvas-unit-to-pixel ratio (still 1:1), just enlarges the bounds, so existing children's absolute
pixel offsets shouldn't have needed to change.

**Result: confirmed harmful on real hardware, not just "incomplete."** Item-pickup button
positioning got measurably *worse* and further from the actual item, part of the boat-scene HUD
disappeared entirely (right half missing), and a fisheye-like distortion appeared near the screen
edges while diving that wasn't present before. Bottom-right/top-left notification banners
(ammo pickup, fish-caught) appeared to spawn already inside their old off-canvas "slide in from
outside" start position, now landing inside the visible area at the true edge instead.

**Interpretation:** the game evidently has *multiple, independent* systems that each make their
own assumptions about screen/canvas dimensions (world-to-screen conversion for pickup prompts,
whatever drives the boat-scene HUD, notification slide-in animations, and something producing the
edge distortion — possibly a post-process effect keyed to `Screen.width`/`Screen.height` or a
canvas size). Resizing only the canvases we could identify made some of these systems consistent
with each other and others *more* inconsistent, net negative. `EnableCanvasResizeFix` is left in
the code, default OFF, for future research only — do not enable it for normal play.

### Second attempt: mirror the working CanvasScaler config instead of hand-resizing

`EnableCanvasScalerFix` — instead of setting `sizeDelta` directly, `AddComponent<CanvasScaler>()`
on the same scaler-less canvases, configured identically to the already-correct `MainCanvas`/
`TalkCanvas` (`ScaleWithScreenSize`, `referenceResolution=(1920,1080)`, `MatchWidthOrHeight`,
`match=1`). Hypothesis: since this exact config already produces correct results elsewhere in the
same game, reusing Unity's own proven-working sizing path (rather than hand-computing a size)
should be safer than the first attempt.

**Result: no crash, but no improvement either.** Item-pickup prompt positioning was unchanged.

### What the interact-prompt code actually looks like (interop stubs only, no native decompile)

`InteractionIndicatorPanel : InputActionIndicatorPanel` (the "press X to pick up" prompt) —
`InputActionIndicatorPanel` holds `m_TargetTransform` (world object), `m_Camera`, and
`m_RectTransform`, with positioning logic in `Update()`/`LateUpdate()`; `InteractionIndicatorPanel`
adds `ShowInteractionButton(Transform target, Vector3 offset)` /
`ShowInstantInteractionButton(...)`. Patched all four (postfix, log-only) and captured live data
while diving:

- `m_Camera` is consistently `MainCamera` with the already-corrected `rect=(0,0,1,1)`,
  `pixelRect=3440x1440` — the camera reference itself is not stale or wrong.
- `m_RectTransform.anchoredPosition` and `.rect` read as exactly `(0,0)` / `(0,0,0,0)` on *every*
  observation, regardless of the target's world position, even though the prompt visibly does
  move (just to the wrong place) in game. This means the field we're reading either isn't the
  transform actually being repositioned, or the real positioning write happens somewhere we
  haven't patched (a pooling/manager class, or a different transform than the one cached in this
  field) — not resolved with the tools available (interop stubs only show signatures, not bodies).

### Confirmed: only some game modes are affected

Player report from real testing: the interact-prompt positioning bug is **not present** in the
Sea Blue infiltration (stealth) mission or the Seevolk-Stadt (Sea Tribe city) area — prompts there
are correctly positioned in ultrawide. It **is** present in normal diving and the sushi restaurant
(both presumably routed through the broken `InteractionRoot` canvas / `CameraResolution` main
pipeline, whereas the working areas apparently use a different, already-ultrawide-correct UI path
— not yet identified which). The mispositioning pattern while diving: correct near screen center,
increasingly pulled *toward center* the further Dave is from it — consistent with a position
calculation using a narrower reference width than the real screen, though the fixed `m_RectTransform`
readings above mean this hasn't been pinned to a specific formula. Also reported as looking
"relative to camera angle" rather than purely player screen-position, which doesn't fit a simple
canvas-width mismatch and further points to native projection/FOV-related code we can't see from
the interop stubs.

**Conclusion: further progress needs native decompilation of `GameAssembly.dll`** (Cpp2IL/
Il2CppDumper + Ghidra) to actually read the positioning math, rather than continuing to guess at
symptoms from the managed interop stubs, which only expose signatures and field layouts — not
method bodies for IL2CPP-compiled code.

## Patch safety findings (from real on-device testing)

Live-tested against the actual 3440x1440 setup, one change at a time. These are load-bearing
constraints for any future patch on this class, not just historical trivia:

- **`CameraResolution.k_TargetRatio` must not be overwritten.** Spoofing it to the real screen
  ratio in a `SetResolution` prefix reliably crashes the game natively (no managed exception)
  right as the `CameraResolution` singleton is destroyed during the logo→menu scene transition.
  Confirmed the crash is caused specifically by this field write, not by the camera/letterbox
  patches (isolated via a config toggle — crashed with only the ratio spoof active, nothing else).
- **`CameraResolution.UpdateCameraRect` is never called during startup** (0 hits logged over
  multiple full boots). Whatever constrains the render width, it isn't this method — don't rely on
  patching it.
- **Hiding the `LetterBoxModifier` panels (`MaskLeft`/`MaskRight`) is safe** (postfix on `Awake` /
  `RecalcAnchorRect` / `SetAnchorRect`, forcing `gameObject.SetActive(false)`) — no crash across
  several runs. Visually this just reveals black, not extended game content, confirming the camera
  itself is only ever rendering a 16:9-equivalent slice — the panels aren't covering already-wider
  content.
- **`CameraResolution.UpdateWideResolution` must not be patched at all, not even for read-only
  logging.** A no-op logging prefix on it produced 1507 back-to-back self-calls and froze the game
  before the publisher logo (the process ends up `<defunct>`/zombie under Proton). It's presumably
  self-recursive with a termination or reentrancy check that a Harmony detour defeats merely by
  existing — no field/property was touched, only logged. `UpdateAutoResolution` has the identical
  `(int, int, WindowModeType)` shape and was preemptively left unpatched too, on suspicion it may
  behave the same way; not yet proven either way in isolation.
- **`InitResolution` and `CheckResoultionForWindow`, patched read-only including a `MainCamera`
  property access, froze the game at the very first boot splash icons** (before even the publisher
  logo — earlier than the `UpdateWideResolution` freeze). Not yet determined whether the freeze
  comes from patching the method at all (same class of problem as `UpdateWideResolution`) or
  specifically from touching `MainCamera` this early in boot before a camera exists. Needs a
  narrower experiment (entry/exit log only, no property access) before retrying.
- **Confirmed safe and in active use:** `SetResolution` prefix+postfix, `UpdateCanvasScale`
  prefix+postfix (this one now also mutates — see "The fix" above), the three `LetterBoxModifier`
  postfixes, `UpdateCanvasViewRect` postfix (log-only).
- **Still unpatched, never exercised in any test session** (`IsWideResolution`/`IsTargetRatio` had
  0 hits even in runs that reached the menu and a dive — they're presumably only called from the
  resolution dropdown's list-filtering in the options menu, not during normal boot/play):
  `IsWideResolution` postfix, `IsTargetRatio` postfix. `UpdateCameraRect` is patchable but
  confirmed to never fire (see above) so left unpatched now instead of kept as a no-op.

Practical upshot: patch this class one method at a time, log-only first, and treat "the game hangs
before it even gets to the menu" as seriously as an outright crash — on this codebase, patching a
method (even to just observe it) can break its own internal control flow, independent of whether
the patch body mutates anything.

## Game/engine

- Unity **6000.0.52f1**, **IL2CPP** scripting backend (confirmed via `BepInEx/interop` generation
  and `global-metadata.dat` presence).
- Studio: MINTROCKET (formerly "Evil Factory" — explains the `EvilFactory.*` namespace used
  throughout the codebase for engine-level/framework code).
- Modding is already established for this game: BepInEx 6 IL2CPP + HarmonyX, used by e.g.
  [DaveDiverExpansion](https://github.com/WhiteMinds/dave-diver-expansion) and a "BepInEx 6 IL2CPP
  Pack" on Nexus Mods. No existing mod touches camera/resolution/UI.

## Prior art / why the existing workaround is bad

The only previously documented ultrawide workaround
([Steam guide](https://steamcommunity.com/sharedfiles/filedetails/?id=3000737847)) uses
"Borderless Gaming" to force a 16:9-rendered window into fullscreen on an ultrawide desktop. This
never touches Unity's `Screen.SetResolution` or the game's own resolution pipeline at all — it's
a pure window-manager trick. Reported issues (off-center restaurant delivery UI, stray textures
visible at the edges, cutscene bars only covering the original 16:9 area) are consistent with the
game's UI/camera code never being told the "real" resolution changed.

## Confirmed in-game behavior (from direct testing, 3440x1440/21:9)

Contrary to the assumption in the Steam guide, **the game's native resolution picker does list
and accept 3440x1440**. Selecting it does not crash or misrender — but the game renders
game-themed decorative bars on the left/right rather than expanding the play area. It's unclear
without deeper native decompilation whether the world is actually rendered at full width and
covered, or whether the camera's render viewport is genuinely restricted to a 16:9 sub-rect. Both
are plausible given the classes found below; v0.1 of the patch tries to rule this out empirically
by forcing the camera viewport open and logging the result.

## Relevant classes (found via `ilspycmd -l c` over `BepInEx/interop/Assembly-CSharp.dll`, then
targeted `ilspycmd -t <Type>` decompiles)

All of these are **interop stubs** — IL2CPP method bodies are compiled to native code inside
`GameAssembly.dll`, so the interop assembly only shows field layouts, signatures, and
`il2cpp_runtime_invoke` trampolines, not the actual algorithm. Understanding *why* a given field
is set the way it is would need native decompilation (Cpp2IL/Il2CppDumper + Ghidra) of
`GameAssembly.dll`, which hasn't been done yet — the patches below work by overriding effects
after the fact rather than by understanding the original logic.

### `CameraResolution : Singleton<CameraResolution>` (global namespace)

The central resolution/camera-rect manager. Key members (from field/method signatures):

- `static float k_TargetRatio` — the aspect ratio the game treats as "correct" (almost certainly
  16:9, i.e. `1.7778`). Everything else appears to get letterboxed relative to this.
- `LetterBoxModifier m_LeftLetterBoxModifier / m_RightLetterBoxModifier / m_TopLetterBoxModifier / m_DownLetterBoxModifier`
  — the four decorative panel controllers.
- `Rect m_CameraViewRect`, `bool m_UpdatedViewRect` — camera viewport tracking.
- `void UpdateCameraRect(Camera camera)` — sets a camera's viewport rect.
- `void InitResolution()`, `void CheckResoultionForWindow()` [sic], `void UpdateCanvasScale()`,
  `void UpdateCanvasViewRect()` — resolution/canvas lifecycle.
- `void SetResolution(float width, float height, DR.Save.WindowModeType windowType)` — the entry
  point called when the user changes resolution in the options menu.
- `static bool IsWideResolution(int width, int height)`, `static bool IsTargetRatio(int width, int height)`,
  `static bool IsEqualRatio(float a, float b)`, `static bool GetBestResolution(out int, out int)` —
  resolution classification helpers. Names strongly imply the game already has a deliberate
  "wide resolution" code path (as opposed to treating ultrawide as an error case).
  `UpdateAutoResolution` / `UpdateWideResolution` overloads exist too.
- VSync/refresh-rate watchdog logic (unrelated to ultrawide, large chunk of the class).

### `LetterBoxModifier : DRMonoBehaviour` (global namespace)

Thin wrapper around a `RectTransform`:

- `RectTransform rectTransform`
- `Vector2 anchorMin`, `Vector2 anchorMax`
- `void SetAnchorRect(Vector2 min, Vector2 max)`
- `void RecalcAnchorRect()`

This is almost certainly the actual side-panel UI element — a RectTransform-anchored panel whose
anchors get recomputed by `CameraResolution` based on `k_TargetRatio` vs. the real screen ratio.

### `ResolutionPopup : VerticalScrollController` / `ResolutionCellData` / `ResolutionCell`

The options-menu resolution list UI. `ResolutionPopup.m_UsingResolution` is a
`List<UnityEngine.Resolution>`; a `Predicate<Resolution>` filter (`_Show_b__3_0` /
`_Show_b__1`, compiler-generated lambda names) decides which of `Screen.resolutions` get listed.
Since 3440x1440 does show up in-game, this filter is not the blocker — the letterbox behavior is
happening after a valid resolution is already applied, inside `CameraResolution`.

### `EvilFactory.AspectRatioConverter` / `EvilFactory.TransformAspectRatioConverter` (namespace `EvilFactory`)

Per-object components with `scale` / `scaleX` / `scaleY` / `posY` / `height` / `top` / `bottom`
arrays of `ConvertRectTransformInfo`, applied in `Apply()`. Looks like a generic "reposition this
RectTransform based on current aspect ratio" utility used on specific UI elements (candidate
explanation for the restaurant-minigame UI misplacement reported for the old borderless-window
workaround — not yet patched or investigated further).

### `EvilFactory.ScaleWidthCamera`

`targetWidth` / `targetHeight` / `pixelsToUnits` / `m_Camera` fields — a camera sizing helper
used somewhere for orthographic size adjustment based on target pixel dimensions. Not yet tied to
a specific camera instance/usage site.

### Not yet investigated

- `EvilFactory.CameraManager`, `OrthographicCameraManager` (1000+ lines each, main gameplay
  camera controllers, not yet read in detail).
- `EvilFactory.NotchScreenChangeCameraSize`, `EvilFactory.CameraSizeConvertInfo`.
- Native decompilation of `GameAssembly.dll` (would require Cpp2IL/Il2CppDumper output run through
  Ghidra or IDA; not attempted — the interop-stub approach above was sufficient to identify patch
  targets without it).

## Toolchain used for this research

```sh
# BepInEx 6 IL2CPP Bleeding Edge, win-x64, from https://builds.bepinex.dev/projects/bepinex_be
# unzipped into the game folder, then the game launched once (via Proton, with
# WINEDLLOVERRIDES="winhttp=n,b" %command% as a Steam launch option) to generate
# BepInEx/interop/*.dll

dotnet tool install -g ilspycmd

# list all classes without fully decompiling (fast, ~20k classes in this game)
ilspycmd -l c "BepInEx/interop/Assembly-CSharp.dll" > classlist.txt
ilspycmd -l e "BepInEx/interop/Assembly-CSharp.dll" > enumlist.txt

# decompile one specific type
ilspycmd -t CameraResolution "BepInEx/interop/Assembly-CSharp.dll"
```
