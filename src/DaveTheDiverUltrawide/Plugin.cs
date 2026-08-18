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
    public const string PluginVersion = "0.4.0";

    internal static Plugin Instance { get; private set; } = null!;

    // Each patch can be toggled independently in BepInEx/config/theo.davethediver.ultrawidefix.cfg
    // without rebuilding, so we can bisect which one is causing trouble on a real run.
    internal static ConfigEntry<bool> EnableTargetRatioSpoof { get; private set; } = null!;
    internal static ConfigEntry<bool> EnableCameraRectFix { get; private set; } = null!;
    internal static ConfigEntry<bool> EnableLetterboxHide { get; private set; } = null!;
    internal static ConfigEntry<bool> EnableCanvasResizeFix { get; private set; } = null!;
    internal static ConfigEntry<bool> EnableCanvasScalerFix { get; private set; } = null!;

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

        var harmony = new Harmony(PluginGuid);
        harmony.PatchAll(typeof(UltrawidePatches));

        Log.LogInfo("Harmony patches applied.");
    }
}
