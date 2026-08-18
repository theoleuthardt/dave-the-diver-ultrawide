using DR.Save;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DaveTheDiverUltrawide;

/// <summary>
/// v0.4. Findings so far (see docs/research-notes.md):
/// - Spoofing CameraResolution.k_TargetRatio to the real screen ratio reliably crashes the game
///   natively (no managed exception) right as the CameraResolution singleton is destroyed during
///   the logo->menu scene transition. Something downstream apparently assumes k_TargetRatio stays
///   at its original (16:9) value. Left in as an opt-in, OFF by default.
/// - CameraResolution.UpdateCameraRect is never called during startup (0 hits in logs) — not the
///   real mechanism.
/// - The real mechanism: CameraResolution.UpdateCanvasScale computes a centered 16:9 sub-rect and
///   writes it to both the main camera's Camera.rect and the CameraResolution.m_CameraViewRect
///   field (confirmed via logging: camera.rect went from the full (0,0,1,1)/3440x1440 right after
///   SetResolution to a pillarboxed (0.13,0,0.74,1)/2560x1440 by the end of UpdateCanvasScale).
///   This is the actual fix target: force both back to full after the original runs.
/// - Hiding the LetterBoxModifier panels (MaskLeft/MaskRight) alone does NOT crash and does remove
///   the decorative art, but on its own reveals plain black, not extended game content — expected,
///   since without the UpdateCanvasScale fix the camera is still only rendering the 16:9 slice.
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

        Plugin.Instance.Log.LogInfo($"[Ultrawide] UpdateCanvasScale() EXIT (after fix)  scene={SceneTag()} {DescribeCamera(__instance)} viewRect={__instance.m_CameraViewRect}");
    }

    [HarmonyPatch(typeof(CameraResolution), "UpdateCanvasViewRect")]
    [HarmonyPostfix]
    private static void UpdateCanvasViewRect_Postfix(CameraResolution __instance)
    {
        Plugin.Instance.Log.LogInfo($"[Ultrawide] UpdateCanvasViewRect() EXIT scene={SceneTag()} viewRect={__instance.m_CameraViewRect}");
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
}
