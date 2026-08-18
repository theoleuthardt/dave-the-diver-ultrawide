using DR.Save;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace DaveTheDiverUltrawide;

/// <summary>
/// v0.5. Findings so far (see docs/research-notes.md for the full trail):
/// - The core world/camera fix (v0.4, confirmed working): CameraResolution.UpdateCanvasScale
///   computes a centered 16:9 sub-rect and writes it to both the main camera's Camera.rect and the
///   CameraResolution.m_CameraViewRect field. Postfix forces both back to full after the original
///   runs, combined with hiding the LetterBoxModifier side panels.
/// - CameraResolution.k_TargetRatio must never be overwritten (crashes on scene load) and
///   CameraResolution.UpdateCameraRect/UpdateWideResolution/UpdateAutoResolution must not be
///   patched at all (freeze/crash even for read-only logging) — see "Patch safety findings".
/// - HUD/world-tracked UI (item pickup prompts etc.) still renders 16:9-locked. Native
///   decompilation (Cpp2IL IL recovery, see native-decompile/README.md) revealed the positioning
///   is anchor-*fraction* based (Camera.WorldToViewportPoint -> RectTransform.anchorMin/anchorMax),
///   which is resolution-independent — meaning the two earlier Canvas-size-based fix attempts
///   (EnableCanvasResizeFix, EnableCanvasScalerFix) could never have worked; they targeted the
///   wrong layer. EnableIndicatorCameraFix targets the real mechanism instead (forces the camera
///   used by WorldToViewportPoint to a full rect + ResetAspect() every frame, right before the
///   game's own LateUpdate uses it) but is not yet confirmed on real hardware.
/// </summary>
[HarmonyPatch]
internal static class UltrawidePatches
{
    [HarmonyPatch(typeof(CameraResolution), "SetResolution",
        new[] { typeof(float), typeof(float), typeof(WindowModeType) })]
    [HarmonyPrefix]
    private static void SetResolution_Prefix(float width, float height, WindowModeType windowModeType)
    {
        if (height <= 0f)
        {
            return;
        }

        float newRatio = width / height;
        Plugin.Instance.Log.LogInfo(
            $"[Ultrawide] SetResolution({width}x{height}, {windowModeType}) target ratio currently {CameraResolution.k_TargetRatio}, computed {newRatio}, spoof {(Plugin.EnableTargetRatioSpoof.Value ? "ON" : "OFF")}");

        if (Plugin.EnableTargetRatioSpoof.Value)
        {
            CameraResolution.k_TargetRatio = newRatio;
        }
    }

    [HarmonyPatch(typeof(CameraResolution), "SetResolution",
        new[] { typeof(float), typeof(float), typeof(WindowModeType) })]
    [HarmonyPostfix]
    private static void SetResolution_Postfix(CameraResolution __instance)
    {
        Plugin.Instance.Log.LogInfo($"[Ultrawide] SetResolution() EXIT scene={SceneTag()} {DescribeCamera(__instance)} viewRect={__instance.m_CameraViewRect}");
    }

    // UpdateCameraRect is confirmed never called during startup/menu (0 hits across many runs).
    // It is NOT what constrains the render width — UpdateCanvasScale below is. Left unpatched.

    [HarmonyPatch(typeof(LetterBoxModifier), "Awake")]
    [HarmonyPostfix]
    private static void LetterBoxAwake_Postfix(LetterBoxModifier __instance)
    {
        HideLetterBox(__instance, "Awake");
    }

    [HarmonyPatch(typeof(LetterBoxModifier), "RecalcAnchorRect")]
    [HarmonyPostfix]
    private static void LetterBoxRecalc_Postfix(LetterBoxModifier __instance)
    {
        HideLetterBox(__instance, "RecalcAnchorRect");
    }

    [HarmonyPatch(typeof(LetterBoxModifier), "SetAnchorRect", new[] { typeof(Vector2), typeof(Vector2) })]
    [HarmonyPostfix]
    private static void LetterBoxSetAnchor_Postfix(LetterBoxModifier __instance)
    {
        HideLetterBox(__instance, "SetAnchorRect");
    }

    private static void HideLetterBox(LetterBoxModifier instance, string from)
    {
        if (!Plugin.EnableLetterboxHide.Value)
        {
            return;
        }

        if (instance == null || instance.gameObject == null)
        {
            return;
        }

        if (instance.gameObject.activeSelf)
        {
            Plugin.Instance.Log.LogInfo(
                $"[Ultrawide] Hiding LetterBoxModifier '{instance.gameObject.name}' (triggered by {from}) scene={SceneTag()}");
        }

        instance.gameObject.SetActive(false);
    }

    // ---- Pure diagnostics below: log only, never mutate anything ----

    // InitResolution and CheckResoultionForWindow are NOT patched here anymore: patching them
    // (logging-only, no mutation) caused the game to hang on the very first splash icons, before
    // even the publisher logo. Both run extremely early at boot, and the log helper touched
    // CameraResolution.MainCamera, which may not exist yet at that point — suspected deadlock
    // between the Harmony detour and the game's own boot sequence. Needs a narrower experiment
    // (e.g. entry log only, no property access) before re-attempting.
    //
    // SetResolution and UpdateCanvasScale, by contrast, have now run cleanly multiple times
    // (including a MainCamera access in SetResolution's postfix below) — by that point in the
    // pipeline a camera clearly already exists, so it's safe to inspect here.

    private static void LogCanvases(string from)
    {
        try
        {
            var canvases = Object.FindObjectsOfType<Canvas>();
            Plugin.Instance.Log.LogInfo($"[Ultrawide] Canvas dump ({from}, scene={SceneTag()}): {canvases.Length} canvas(es)");
            foreach (Canvas c in canvases)
            {
                if (c == null)
                {
                    continue;
                }

                RectTransform rt = c.GetComponent<RectTransform>();
                string rectInfo = rt != null ? $"rect={rt.rect}" : "no-rect";

                Camera cam = c.renderMode != RenderMode.ScreenSpaceOverlay ? c.worldCamera : null;
                string camInfo = cam != null
                    ? $"cam='{cam.name}' camRect={cam.rect} camPixelRect={cam.pixelRect}"
                    : "no-camera";

                CanvasScaler scaler = c.GetComponent<CanvasScaler>();
                string scalerInfo = scaler != null
                    ? $"refRes={scaler.referenceResolution} matchMode={scaler.screenMatchMode} match={scaler.matchWidthOrHeight}"
                    : "no-scaler";

                Plugin.Instance.Log.LogInfo(
                    $"[Ultrawide]   Canvas '{c.name}' renderMode={c.renderMode} {rectInfo} {camInfo} {scalerInfo}");

                // Confirmed: some Screen-Space-Camera canvases (InteractionRoot, CutsceneUI,
                // DamageTextPoolPanel, EmojiPanel) have no CanvasScaler and stay hardcoded at
                // 1920x1080 instead of following the camera's real (now-widened) pixel size like
                // MainCanvas/TalkCanvas do. InteractionRoot specifically drives world-tracked HUD
                // prompts (item pickup buttons etc.), confirmed visibly mispositioned outside the
                // old 16:9 area. Resize just the canvas bounds (not the unit system — 1 canvas
                // unit stays 1 pixel, existing children keep their exact pixel offsets) to match.
                if (Plugin.EnableCanvasResizeFix.Value
                    && rt != null && scaler == null && cam != null
                    && c.renderMode == RenderMode.ScreenSpaceCamera)
                {
                    Vector2 targetSize = new Vector2(cam.pixelWidth, cam.pixelHeight);
                    if (rt.sizeDelta != targetSize)
                    {
                        Vector2 before = rt.sizeDelta;
                        rt.sizeDelta = targetSize;
                        Plugin.Instance.Log.LogInfo(
                            $"[Ultrawide]   -> Resized Canvas '{c.name}' sizeDelta {before} -> {rt.sizeDelta}");
                    }
                }

                // Different approach to the same problem, mirroring the CanvasScaler config that
                // already correctly widens MainCanvas/TalkCanvas instead of hand-computing a size.
                if (Plugin.EnableCanvasScalerFix.Value
                    && scaler == null && cam != null
                    && c.renderMode == RenderMode.ScreenSpaceCamera)
                {
                    CanvasScaler added = c.gameObject.AddComponent<CanvasScaler>();
                    added.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                    added.referenceResolution = new Vector2(1920f, 1080f);
                    added.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                    added.matchWidthOrHeight = 1f;
                    Plugin.Instance.Log.LogInfo(
                        $"[Ultrawide]   -> Added CanvasScaler to '{c.name}' (refRes 1920x1080, match=1)");
                }

                if (rt == null)
                {
                    continue;
                }

                int childCount = rt.childCount;
                for (int i = 0; i < childCount; i++)
                {
                    RectTransform child = rt.GetChild(i) as RectTransform;
                    if (child == null)
                    {
                        continue;
                    }

                    Plugin.Instance.Log.LogInfo(
                        $"[Ultrawide]     child '{child.name}' anchorMin={child.anchorMin} anchorMax={child.anchorMax} anchoredPosition={child.anchoredPosition} sizeDelta={child.sizeDelta} rect={child.rect}");
                }
            }
        }
        catch (System.Exception ex)
        {
            Plugin.Instance.Log.LogInfo($"[Ultrawide] Canvas dump failed: {ex}");
        }
    }

    private static string SceneTag()
    {
        try
        {
            return SceneManager.GetActiveScene().name;
        }
        catch
        {
            return "?";
        }
    }

    private static string DescribeCamera(CameraResolution instance)
    {
        Camera cam = instance?.MainCamera;
        if (cam == null)
        {
            return "MainCamera=null";
        }

        return $"MainCamera.rect={cam.rect} pixelRect={cam.pixelRect} aspect={cam.aspect} orthographicSize={cam.orthographicSize}";
    }

    [HarmonyPatch(typeof(CameraResolution), "UpdateCanvasScale")]
    [HarmonyPrefix]
    private static void UpdateCanvasScale_Prefix()
    {
        Plugin.Instance.Log.LogInfo("[Ultrawide] UpdateCanvasScale() called");
    }

    [HarmonyPatch(typeof(CameraResolution), "UpdateCanvasScale")]
    [HarmonyPostfix]
    private static void UpdateCanvasScale_Postfix(CameraResolution __instance)
    {
        Plugin.Instance.Log.LogInfo($"[Ultrawide] UpdateCanvasScale() EXIT (before fix) scene={SceneTag()} {DescribeCamera(__instance)} viewRect={__instance.m_CameraViewRect}");

        if (!Plugin.EnableCameraRectFix.Value)
        {
            return;
        }

        Rect full = new Rect(0f, 0f, 1f, 1f);
        __instance.m_CameraViewRect = full;

        Camera cam = __instance.MainCamera;
        if (cam != null)
        {
            cam.rect = full;
        }

        // A EvilFactory.CameraManager cross-check used to live here, but UpdateCanvasScale fires
        // too early per scene (before that scene's CameraManager exists — hasInstance was false
        // for 100% of samples across a full play session) AND accessing
        // EvilFactory.SceneSingleton<CameraManager>.Instance at boot crashed the game outright.
        // Moved to InputActionIndicatorPanel_LateUpdate_Prefix below, which only ever runs once a
        // target is assigned — i.e. only during real gameplay, when a CameraManager is guaranteed
        // to already exist. See docs/research-notes.md for the full story.

        // m_CameraViewRect is presumably meant to drive whatever consumes it (HUD safe-area
        // sizing, going by the field/method names) only when UpdateCanvasViewRect() itself runs —
        // our direct field write above bypasses that. Call it ourselves so the corrected value
        // actually propagates, instead of only taking effect the next time the game happens to
        // call it on its own (which our logging showed doesn't reliably happen again per scene).
        __instance.UpdateCanvasViewRect();

        Plugin.Instance.Log.LogInfo($"[Ultrawide] UpdateCanvasScale() EXIT (after fix)  scene={SceneTag()} {DescribeCamera(__instance)} viewRect={__instance.m_CameraViewRect}");

        LogCanvases("UpdateCanvasScale");
    }

    [HarmonyPatch(typeof(CameraResolution), "UpdateCanvasViewRect")]
    [HarmonyPostfix]
    private static void UpdateCanvasViewRect_Postfix(CameraResolution __instance)
    {
        Plugin.Instance.Log.LogInfo($"[Ultrawide] UpdateCanvasViewRect() EXIT (before fix) scene={SceneTag()} viewRect={__instance.m_CameraViewRect}");

        if (!Plugin.EnableCameraRectFix.Value)
        {
            return;
        }

        // Defensive/idempotent: if something else calls this with the original pillarboxed value
        // before our UpdateCanvasScale postfix gets a chance to run, force it back to full here
        // too. Setting the field again if it's already (0,0,1,1) is a cheap no-op either way.
        __instance.m_CameraViewRect = new Rect(0f, 0f, 1f, 1f);

        Plugin.Instance.Log.LogInfo($"[Ultrawide] UpdateCanvasViewRect() EXIT (after fix)  scene={SceneTag()} viewRect={__instance.m_CameraViewRect}");
    }

    // UpdateAutoResolution and UpdateWideResolution are NOT patched here anymore: patching
    // UpdateWideResolution (logging-only, no mutation) produced 1507 back-to-back calls to itself
    // and froze the game before the publisher logo. It's presumably self-recursive with some
    // termination/reentrancy check that a Harmony detour on it defeats. UpdateAutoResolution is
    // the same shape (int, int, WindowModeType) and left unpatched too until proven safe on its
    // own, in isolation.

    [HarmonyPatch(typeof(CameraResolution), "IsWideResolution", new[] { typeof(int), typeof(int) })]
    [HarmonyPostfix]
    private static void IsWideResolution_Postfix(int width, int height, bool __result)
    {
        Plugin.Instance.Log.LogInfo($"[Ultrawide] IsWideResolution({width}x{height}) -> {__result}");
    }

    [HarmonyPatch(typeof(CameraResolution), "IsTargetRatio", new[] { typeof(int), typeof(int) })]
    [HarmonyPostfix]
    private static void IsTargetRatio_Postfix(int width, int height, bool __result)
    {
        Plugin.Instance.Log.LogInfo($"[Ultrawide] IsTargetRatio({width}x{height}) -> {__result}");
    }

    // GetBestResolution(out int, out int) intentionally not patched: matching an out-parameter
    // overload by argument types needs typeof(int).MakeByRefType(), not typeof(int), and getting
    // that wrong makes Harmony reject the whole PatchAll batch (as it just did here) rather than
    // just skipping this one patch. Not essential to the diagnosis, so left unpatched for now.

    // ---- Item-pickup / interact-prompt investigation ----
    //
    // InteractionIndicatorPanel : InputActionIndicatorPanel drives the world-tracked "press X to
    // pick up" prompt. The base class holds m_TargetTransform (the world object), m_Camera, and
    // m_RectTransform, and does its positioning in Update()/LateUpdate(). Logging here (not the
    // Canvas-resize approach, which made things worse) to see the actual numbers involved before
    // guessing at a fix. Only logs while a target is actually assigned (prompt visible), so this
    // shouldn't spam the log during normal play with no nearby interactable.
    private static int _indicatorLogFrameCounter;

    private static string DescribeIndicator(string from, InputActionIndicatorPanel instance)
    {
        Transform target = instance.m_TargetTransform;
        RectTransform rt = instance.m_RectTransform;
        Camera cam = instance.m_Camera;

        string targetInfo = target != null ? $"target='{target.name}' targetWorldPos={target.position}" : "target=null";
        string rtInfo = rt != null
            ? $"anchoredPosition={rt.anchoredPosition} worldPos={rt.position} localPos={rt.localPosition} rect={rt.rect} parent='{(rt.parent != null ? rt.parent.name : "null")}'"
            : "m_RectTransform=null";
        string camInfo = cam != null
            ? $"cam='{cam.name}' rect={cam.rect} pixelRect={cam.pixelRect}"
            : "m_Camera=null";

        return $"[Ultrawide] {from} {targetInfo} {rtInfo} {camInfo}";
    }

    // Native decompile of InputActionIndicatorPanel.LateUpdate (Cpp2IL IL-recovery — see
    // native-decompile/README.md to regenerate) shows the real logic, despite garbled Vector2
    // handling in a few spots:
    //   Vector3 vector = m_Camera.WorldToViewportPoint(target.position + offset);
    //   ... (a Singleton<EvilFactory.CameraManager>.Instance reference the decompile couldn't
    //        fully resolve) ...
    //   m_RectTransform.anchorMin = vector2;   // vector2 derived from `vector`
    //   m_RectTransform.anchorMax = vector2;
    //
    // Positioning is anchor-fraction based (anchorMin/anchorMax, both set to the same [0,1]
    // viewport fraction), which is inherently resolution/canvas-size independent — this is why
    // EnableCanvasResizeFix and EnableCanvasScalerFix (which only ever changed canvas *size*)
    // could never have fixed this: the bug isn't the canvas, it's the fraction computed by
    // WorldToViewportPoint itself.
    //
    // Two concrete, evidence-based hypotheses for why that fraction comes out wrong toward the
    // edges ("pulled toward center", matching a too-narrow effective FOV):
    //  1. Camera.aspect can be manually locked in Unity (persists across resolution/rect changes
    //     until Camera.ResetAspect() is called) — if something set it once for the original 16:9
    //     pillarbox and never reset it, WorldToViewportPoint would keep computing fractions for
    //     the old narrow aspect even though rendering itself (camera.rect) is already correct.
    //  2. m_Camera here is cached via Camera.main in Awake(), which might not be the exact same
    //     Camera object as EvilFactory.CameraManager.mainCamera (that class independently caches
    //     GetComponent<Camera>() on itself) — if these differ, fixing one doesn't fix the other.
    //     (Tried checking this from CameraResolution.UpdateCanvasScale — wrong place/timing, see
    //     above.) LateUpdate only runs once m_TargetTransform is set, i.e. only during real
    //     gameplay, when a CameraManager is guaranteed to already exist — the right place to check.
    //
    // Fix: right before the original method runs each frame, force whichever camera(s) are
    // actually in play back to a full rect and un-lock their aspect. Cheap (a few property
    // writes), and covers both hypotheses without needing to know which one is actually true.
    [HarmonyPatch(typeof(InputActionIndicatorPanel), "LateUpdate")]
    [HarmonyPrefix]
    private static void InputActionIndicatorPanel_LateUpdate_Prefix(InputActionIndicatorPanel __instance)
    {
        if (!Plugin.EnableIndicatorCameraFix.Value || __instance.m_TargetTransform == null)
        {
            return;
        }

        try
        {
            Rect full = new Rect(0f, 0f, 1f, 1f);
            Camera indicatorCam = __instance.m_Camera;
            if (indicatorCam != null)
            {
                if (indicatorCam.rect != full)
                {
                    indicatorCam.rect = full;
                }

                indicatorCam.ResetAspect();
            }

            if (Plugin.EnableCameraManagerCrossCheck.Value
                && EvilFactory.SceneSingleton<EvilFactory.CameraManager>.hasInstance)
            {
                EvilFactory.CameraManager gameplayCameraManager = EvilFactory.SceneSingleton<EvilFactory.CameraManager>.Instance;
                Camera gameplayCam = gameplayCameraManager != null ? gameplayCameraManager.mainCamera : null;
                if (gameplayCam != null)
                {
                    bool sameCamera = indicatorCam != null && indicatorCam.GetInstanceID() == gameplayCam.GetInstanceID();
                    if (!sameCamera)
                    {
                        if (gameplayCam.rect != full)
                        {
                            gameplayCam.rect = full;
                        }

                        gameplayCam.ResetAspect();

                        _indicatorLogFrameCounter++;
                        if (_indicatorLogFrameCounter % 30 == 0)
                        {
                            Plugin.Instance.Log.LogInfo(
                                $"[Ultrawide] LateUpdate: indicator camera '{(indicatorCam != null ? indicatorCam.name : "null")}' differs from EvilFactory.CameraManager.mainCamera '{gameplayCam.name}' — fixed both.");
                        }
                    }
                }
            }
        }
        catch (System.Exception ex)
        {
            Plugin.Instance.Log.LogInfo($"[Ultrawide] InputActionIndicatorPanel camera fix failed: {ex}");
        }
    }

    [HarmonyPatch(typeof(InputActionIndicatorPanel), "LateUpdate")]
    [HarmonyPostfix]
    private static void InputActionIndicatorPanel_LateUpdate_Postfix(InputActionIndicatorPanel __instance)
    {
        try
        {
            if (__instance.m_TargetTransform == null)
            {
                return;
            }

            // One line roughly every 30 frames (~0.5s at 60fps) while a prompt is visible —
            // enough to see it drift as the player moves, without flooding the log.
            _indicatorLogFrameCounter++;
            if (_indicatorLogFrameCounter % 30 != 0)
            {
                return;
            }

            Plugin.Instance.Log.LogInfo(DescribeIndicator("LateUpdate", __instance));
        }
        catch (System.Exception ex)
        {
            Plugin.Instance.Log.LogInfo($"[Ultrawide] InputActionIndicatorPanel diagnostic failed: {ex}");
        }
    }

    [HarmonyPatch(typeof(InputActionIndicatorPanel), "Update")]
    [HarmonyPostfix]
    private static void InputActionIndicatorPanel_Update_Postfix(InputActionIndicatorPanel __instance)
    {
        try
        {
            if (__instance.m_TargetTransform == null)
            {
                return;
            }

            _indicatorLogFrameCounter++;
            if (_indicatorLogFrameCounter % 30 != 0)
            {
                return;
            }

            Plugin.Instance.Log.LogInfo(DescribeIndicator("Update", __instance));
        }
        catch (System.Exception ex)
        {
            Plugin.Instance.Log.LogInfo($"[Ultrawide] InputActionIndicatorPanel diagnostic failed: {ex}");
        }
    }

    [HarmonyPatch(typeof(InteractionIndicatorPanel), "ShowInteractionButton",
        new[] { typeof(Transform), typeof(Vector3) })]
    [HarmonyPostfix]
    private static void ShowInteractionButton_Postfix(InteractionIndicatorPanel __instance, Transform target, Vector3 offset)
    {
        Plugin.Instance.Log.LogInfo(
            $"{DescribeIndicator("ShowInteractionButton", __instance)} offset={offset}");
    }

    [HarmonyPatch(typeof(InteractionIndicatorPanel), "ShowInstantInteractionButton",
        new[] { typeof(Transform), typeof(Vector3) })]
    [HarmonyPostfix]
    private static void ShowInstantInteractionButton_Postfix(InteractionIndicatorPanel __instance, Transform target, Vector3 offset)
    {
        Plugin.Instance.Log.LogInfo(
            $"{DescribeIndicator("ShowInstantInteractionButton", __instance)} offset={offset}");
    }
}
