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

        var harmony = new Harmony(PluginGuid);
        harmony.PatchAll(typeof(UltrawidePatches));

        Log.LogInfo("Harmony patches applied.");
    }
}
