using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;

public enum GraphicsPreset { Low, Medium, High }

/// <summary>
/// Graphics options (quality preset, render scale, FPS counter), persisted in PlayerPrefs and applied to the quality
/// settings, the global post-processing volume and every camera. Clients only: the dedicated server never loads them.
///
/// What the presets trade (measured in the WebGL build, see PerfProbe): in the built-in forward renderer every
/// shadow cascade and the depth-normals texture that ambient occlusion needs is another full pass over the scene, and
/// bloom/AO are a dozen full-screen passes each at the canvas resolution.
///   High    soft shadows, 2 cascades, 40 units; AO, full bloom, FXAA, HDR; full-detail LODs
///   Medium  soft shadows, 1 cascade, 28 units, 1K map; fast bloom, FXAA, HDR; no AO; LOD 1 at most  (WebGL default)
///   Low     no realtime shadows, no AO, no bloom, no AA, LDR, 75% resolution; colour grading + vignette keep the look
/// Render scale (WebGL) sets the canvas resolution as a fraction of CSS pixels, so it never exceeds 1 device pixel per
/// CSS pixel: a 2x laptop screen is drawn at 1x and scaled up by the browser.
/// -gfx low|medium|high (or ?gfx=) picks a preset for one run without saving it; -gfx none leaves the project defaults.
/// </summary>
public static class GraphicsQuality {

    public const float MinRenderScale = 0.5f;
    public const float MaxRenderScale = 1f;

    public static event Action Changed;

    public static GraphicsPreset Current { get; private set; } = GraphicsPreset.High;
    public static float RenderScale { get; private set; } = 1f;
    public static bool ShowFps { get; private set; }

    /// <summary>Only the WebGL canvas can be resized independently of the window.</summary>
    public static bool SupportsRenderScale => Application.platform == RuntimePlatform.WebGLPlayer;

    public static string PresetLabel(GraphicsPreset p) {
        return p == GraphicsPreset.Low ? L10n.T("낮음") : p == GraphicsPreset.Medium ? L10n.T("중간") : L10n.T("높음");
    }

    /// <summary>Render scale a preset starts from when picked (the slider can move it afterwards).</summary>
    public static float SuggestedScale(GraphicsPreset p) {
        return p == GraphicsPreset.Low ? 0.75f : 1f;
    }

    private static bool loaded;
    private static int version;
    private static Runner runner;

    public static void Load() {
        if (loaded) return;
        loaded = true;
        GraphicsPreset fallback = Application.platform == RuntimePlatform.WebGLPlayer ? GraphicsPreset.Medium : GraphicsPreset.High;
        Current = (GraphicsPreset)Mathf.Clamp(PlayerPrefs.GetInt("opt.gfx", (int)fallback), 0, 2);
        RenderScale = Mathf.Clamp(PlayerPrefs.GetFloat("opt.renderScale", SuggestedScale(Current)), MinRenderScale, MaxRenderScale);
        ShowFps = PlayerPrefs.GetInt("opt.showFps", 0) == 1;
        string arg = NetConfig.GraphicsArg;
        // Diagnostics: the project's own quality level and post-processing, untouched (before/after comparisons).
        if (arg == "none") return;
        if (!string.IsNullOrEmpty(arg) && Enum.TryParse(arg, true, out GraphicsPreset forced)) {
            Current = forced;
            RenderScale = SuggestedScale(forced);
        }
        Apply();
    }

    public static void SetPreset(GraphicsPreset p) {
        Current = p;
        RenderScale = SuggestedScale(p);
        PlayerPrefs.SetInt("opt.gfx", (int)p);
        PlayerPrefs.SetFloat("opt.renderScale", RenderScale);
        Apply();
    }

    public static void SetRenderScale(float v) {
        RenderScale = Mathf.Clamp(v, MinRenderScale, MaxRenderScale);
        PlayerPrefs.SetFloat("opt.renderScale", RenderScale);
        Apply();
    }

    public static void SetShowFps(bool v) {
        ShowFps = v;
        PlayerPrefs.SetInt("opt.showFps", v ? 1 : 0);
        Changed?.Invoke();
    }

    /// <summary>
    /// The renderer's name when it is a software rasteriser (browser hardware acceleration off, no GPU driver), else
    /// null. Such a renderer runs the game at a few frames per second whatever the settings.
    /// </summary>
    public static string SoftwareRenderer {
        get {
            string name = GpuName();
            string n = name.ToLowerInvariant();
            foreach (string s in new[] { "swiftshader", "llvmpipe", "softpipe", "basic render", "software", "lavapipe" }) {
                if (n.Contains(s)) return name;
            }
            return null;
        }
    }

    private static string GpuName() {
#if UNITY_WEBGL && !UNITY_EDITOR
        try {
            string s = FpsGfx_GpuName();
            if (!string.IsNullOrEmpty(s)) return s;
        } catch (Exception) { }
#endif
        return SystemInfo.graphicsDeviceName ?? "";
    }

#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")] private static extern void FpsGfx_SetPixelRatio(float ratio);
    [DllImport("__Internal")] private static extern string FpsGfx_GpuName();
#endif

    // ------------------------------------------------------------------ applying

    private static void Apply() {
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
        GraphicsPreset p = Current;
        QualitySettings.antiAliasing = 0;    // FXAA in post instead; MSAA is not resolved through the post stack
        QualitySettings.softParticles = false;
        QualitySettings.realtimeReflectionProbes = p == GraphicsPreset.High;
        QualitySettings.pixelLightCount = p == GraphicsPreset.Low ? 1 : 2;
        QualitySettings.skinWeights = p == GraphicsPreset.Low ? SkinWeights.TwoBones : SkinWeights.FourBones;
        // The warehouse's road barriers are 61K triangles at LOD 0 (15 of them); LOD 1 is 11K and looks the same in play.
        QualitySettings.maximumLODLevel = p == GraphicsPreset.High ? 0 : 1;
        QualitySettings.lodBias = p == GraphicsPreset.Low ? 0.6f : p == GraphicsPreset.Medium ? 0.8f : 1f;
        switch (p) {
            case GraphicsPreset.Low:
                QualitySettings.shadows = ShadowQuality.Disable;
                break;
            case GraphicsPreset.Medium:
                QualitySettings.shadows = ShadowQuality.All;
                QualitySettings.shadowResolution = ShadowResolution.Medium;
                QualitySettings.shadowCascades = 1;
                QualitySettings.shadowDistance = 28f;
                break;
            default:
                QualitySettings.shadows = ShadowQuality.All;
                QualitySettings.shadowResolution = ShadowResolution.Medium;
                QualitySettings.shadowCascades = 2;
                QualitySettings.shadowCascade2Split = 1f / 3f;
                QualitySettings.shadowDistance = 40f;
                break;
        }
        // A desktop player renders as fast as it can without this. In WebGL 1 means "every requestAnimationFrame", the
        // browser's own pacing; 0 would switch the main loop to setTimeout, which judders.
        QualitySettings.vSyncCount = 1;
#if UNITY_WEBGL && !UNITY_EDITOR
        FpsGfx_SetPixelRatio(RenderScale);
#endif
        version++;
        if (runner == null) {
            GameObject go = new GameObject("GraphicsQuality");
            UnityEngine.Object.DontDestroyOnLoad(go);
            runner = go.AddComponent<Runner>();
        }
        Changed?.Invoke();
    }

    /// <summary>Post-processing settings of the active preset, applied to a volume's own copy of its profile.</summary>
    private static void ApplyPost(PostProcessVolume volume) {
        PostProcessProfile profile = volume.profile;   // instantiates a per-volume copy; the asset stays untouched
        GraphicsPreset p = Current;
        if (profile.TryGetSettings(out AmbientOcclusion ao)) ao.enabled.Override(p == GraphicsPreset.High);
        if (profile.TryGetSettings(out Bloom bloom)) {
            bloom.enabled.Override(p != GraphicsPreset.Low);
            bloom.fastMode.Override(p != GraphicsPreset.High);
        }
        if (profile.TryGetSettings(out Grain grain)) grain.enabled.Override(p != GraphicsPreset.Low);
        // Effects the profile may gain later that are too heavy below High.
        if (profile.TryGetSettings(out MotionBlur mb)) mb.enabled.Override(mb.enabled.value && p == GraphicsPreset.High);
        if (profile.TryGetSettings(out DepthOfField dof)) dof.enabled.Override(dof.enabled.value && p == GraphicsPreset.High);
        if (profile.TryGetSettings(out ScreenSpaceReflections ssr)) ssr.enabled.Override(false);
    }

    private static void ApplyCamera(Camera cam) {
        GraphicsPreset p = Current;
        cam.allowHDR = p != GraphicsPreset.Low;
        cam.allowMSAA = false;
        PostProcessLayer layer = cam.GetComponent<PostProcessLayer>();
        if (layer != null) {
            layer.antialiasingMode = p == GraphicsPreset.Low ? PostProcessLayer.Antialiasing.None : PostProcessLayer.Antialiasing.FastApproximateAntialiasing;
            layer.fastApproximateAntialiasing.fastMode = true;
        }
    }

    /// <summary>
    /// Keeps cameras and volumes that appear later (the player camera is created on every spawn, maps bring their own
    /// volumes) on the current preset.
    /// </summary>
    private class Runner : MonoBehaviour {

        private readonly System.Collections.Generic.Dictionary<int, int> applied = new System.Collections.Generic.Dictionary<int, int>();
        private Camera[] cams = new Camera[8];
        private float volumeScan;
        private int scannedVersion = -1;

        void LateUpdate() {
            int n = Camera.allCamerasCount;
            if (cams.Length < n) cams = new Camera[n * 2];
            Camera.GetAllCameras(cams);
            for (int i = 0; i < n; i++) {
                Camera cam = cams[i];
                if (cam == null) continue;
                if (!applied.TryGetValue(cam.GetInstanceID(), out int v) || v != version) {
                    applied[cam.GetInstanceID()] = version;
                    ApplyCamera(cam);
                }
                // PostProcessLayer only ever adds depth flags; once AO is off the depth-normals pass (a whole extra
                // render of the scene) must be dropped by hand.
                if (Current != GraphicsPreset.High && (cam.depthTextureMode & DepthTextureMode.DepthNormals) != 0) {
                    cam.depthTextureMode &= ~DepthTextureMode.DepthNormals;
                }
            }
            volumeScan -= Time.unscaledDeltaTime;
            if (volumeScan <= 0f || scannedVersion != version) {
                scannedVersion = version;
                volumeScan = 1f;
                foreach (PostProcessVolume vol in FindObjectsOfType<PostProcessVolume>()) {
                    if (vol.sharedProfile == null && !vol.HasInstantiatedProfile()) continue;
                    if (!applied.TryGetValue(vol.GetInstanceID(), out int v) || v != version) {
                        applied[vol.GetInstanceID()] = version;
                        ApplyPost(vol);
                    }
                }
            }
        }

    }

}
