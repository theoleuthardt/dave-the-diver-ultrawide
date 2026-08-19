using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace DaveTheDiverUltrawide;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public class Plugin : BasePlugin
{
    public const string PluginGuid = "theo.davethediver.ultrawidefix";
    public const string PluginName = "Dave the Diver Ultrawide Fix";
    public const string PluginVersion = "0.6.1";

    internal static Plugin Instance { get; private set; } = null!;

    // Each patch can be toggled independently in BepInEx/config/theo.davethediver.ultrawidefix.cfg
    // without rebuilding, so we can bisect which one is causing trouble on a real run.
    internal static ConfigEntry<bool> EnableTargetRatioSpoof { get; private set; } = null!;
    internal static ConfigEntry<bool> EnableCameraRectFix { get; private set; } = null!;
    internal static ConfigEntry<bool> EnableLetterboxHide { get; private set; } = null!;
    internal static ConfigEntry<bool> EnableCanvasResizeFix { get; private set; } = null!;
    internal static ConfigEntry<bool> EnableCanvasScalerFix { get; private set; } = null!;
    internal static ConfigEntry<bool> EnableCameraManagerCrossCheck { get; private set; } = null!;
    internal static ConfigEntry<bool> EnableIndicatorCameraFix { get; private set; } = null!;
    internal static ConfigEntry<bool> EnableSushiBarCameraFix { get; private set; } = null!;

    public override void Load()
    {
        Instance = this;
        Log.LogInfo($"{PluginName} {PluginVersion} loading...");

        EnableTargetRatioSpoof = Config.Bind(
            "Patches", "EnableTargetRatioSpoof", false,
            "Override CameraResolution.k_TargetRatio to match the applied resolution. Confirmed to crash the game natively on scene load (possible div-by-zero/zero-size viewport when it exactly matches screen ratio). Keep this OFF — it is not needed for the actual fix.");
        EnableCameraRectFix = Config.Bind(
            "Patches", "EnableCameraRectFix", true,
            "The actual ultrawide fix: after CameraResolution.UpdateCanvasScale pillarboxes the main camera to a centered 16:9 rect, force it (and the internal m_CameraViewRect field) back to fill the whole window. Confirmed working on 3440x1440.");
        EnableLetterboxHide = Config.Bind(
            "Patches", "EnableLetterboxHide", true,
            "Hide the decorative LetterBoxModifier side panels (MaskLeft/MaskRight) so they don't cover the now-widened camera view. Confirmed safe.");
        EnableCanvasResizeFix = Config.Bind(
            "Patches", "EnableCanvasResizeFix", false,
            "Resize Screen-Space-Camera Canvases that have no CanvasScaler (InteractionRoot, CutsceneUI, DamageTextPoolPanel, EmojiPanel) from their hardcoded 1920x1080 up to the camera's real pixel size. CONFIRMED HARMFUL on real hardware: made world-tracked HUD prompt positions (e.g. item pickup buttons) worse, broke part of the boat-scene HUD, and introduced a fisheye-looking distortion near the screen edges while diving. Keep this OFF — left in only for further research, do not enable for normal play.");
        EnableCanvasScalerFix = Config.Bind(
            "Patches", "EnableCanvasScalerFix", false,
            "Different approach to the same problem as EnableCanvasResizeFix (keep that one OFF too): instead of directly resizing scaler-less Screen-Space-Camera canvases (InteractionRoot, CutsceneUI, DamageTextPoolPanel, EmojiPanel), add a CanvasScaler to them configured exactly like the already-correctly-widening MainCanvas/TalkCanvas (ScaleWithScreenSize, referenceResolution 1920x1080, MatchWidthOrHeight, match=1). CONFIRMED INEFFECTIVE on real hardware: no crash, but item-pickup prompt positioning was unchanged — still appears tied to camera angle, not just canvas size. Root cause needs native decompilation (see docs/research-notes.md). Keep this OFF.");
        EnableIndicatorCameraFix = Config.Bind(
            "Patches", "EnableIndicatorCameraFix", true,
            "NEW (untested on real hardware yet): every frame the item-pickup prompt is visible, forces InputActionIndicatorPanel.m_Camera back to a full rect and calls Camera.ResetAspect() on it, right before the game's own LateUpdate computes the prompt's screen position via WorldToViewportPoint. Targets the actual native-decompiled positioning code directly, unlike the two Canvas-based attempts above (which could never have worked — the positioning is anchor-fraction based, not canvas-size based). See docs/research-notes.md.");
        EnableCameraManagerCrossCheck = Config.Bind(
            "Patches", "EnableCameraManagerCrossCheck", true,
            "Sub-part of EnableIndicatorCameraFix (has no effect if that's off): also checks whether EvilFactory.CameraManager.mainCamera is a *different* Camera object than the one InputActionIndicatorPanel uses, and fixes that one too if so. Previously lived in CameraResolution.UpdateCanvasScale and CONFIRMED TO CRASH THE GAME there (ran too early, before any dive scene/CameraManager existed) — moved here, where it only ever runs once an interact prompt is actually visible, i.e. guaranteed to be in a real gameplay scene. Should be safe now, but watch BepInEx/LogOutput.log after enabling.");
        EnableSushiBarCameraFix = Config.Bind(
            "Patches", "EnableSushiBarCameraFix", false,
            "CONFIRMED TO CRASH THE GAME (fatal System.AccessViolationException / native memory corruption inside Camera.ResetAspect(), process dies outright — see BepInEx/ErrorLog.log). The SushiBarManager.mainCamera getter this patches is called extremely early during boot (observed: the DR_Logo scene, right after the publisher logo, long before the main menu or any real sushi-bar gameplay) — same class of problem as the EvilFactory.CameraManager.Instance boot crash documented for EnableCameraManagerCrossCheck below, just on a different singleton. Keep this OFF. Needs the same treatment that fixed that one: move the fix to a call site guaranteed to only run during actual sushi-bar gameplay (e.g. CustomerActionInfo.UpdateAnchor, which only runs once a prompt/action is actually bound) instead of patching the shared getter directly. See docs/research-notes.md.");

        var harmony = new Harmony(PluginGuid);
        harmony.PatchAll(typeof(UltrawidePatches));

        Log.LogInfo("Harmony patches applied.");
    }
}
