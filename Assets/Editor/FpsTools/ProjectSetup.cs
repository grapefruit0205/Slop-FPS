using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

/// <summary>
/// One-off project maintenance, runnable from the menu or headless:
///   Unity -batchmode -quit -projectPath . -executeMethod ProjectSetup.ConvertToText
/// Lives in its own assembly so it still compiles while game scripts have errors.
/// </summary>
public static class ProjectSetup {

    private const string FontDir = "Assets/UI/Resources/Fonts & Materials/";

    /// <summary>First headless run on a fresh clone: TMP essentials, then text serialization.</summary>
    public static void Bootstrap() {
        ImportTmpEssentials();
        ConvertToText();
    }

    /// <summary>Switch to text serialization so scenes and prefabs diff and merge in git.</summary>
    [MenuItem("Tools/FPS/Convert Assets To Text")]
    public static void ConvertToText() {
        EditorSettings.serializationMode = SerializationMode.ForceText;
        AssetDatabase.ForceReserializeAssets();
        AssetDatabase.SaveAssets();
        Debug.Log("[ProjectSetup] Serialization is now Force Text.");
    }

    [MenuItem("Tools/FPS/Import TMP Essentials")]
    public static void ImportTmpEssentials() {
        if (AssetDatabase.FindAssets("t:TMP_Settings").Length > 0) {
            Debug.Log("[ProjectSetup] TMP essentials already present.");
            if (Application.isBatchMode) EditorApplication.Exit(0);
            return;
        }
        if (Application.isBatchMode) {
            // ImportPackage is asynchronous. Keep the batch editor alive until import is complete.
            AssetDatabase.importPackageCompleted += _ => EditorApplication.delayCall += () => {
                AssetDatabase.Refresh();
                bool ready = AssetDatabase.FindAssets("t:TMP_Settings").Length > 0;
                Debug.Log("[ProjectSetup] TMP essentials ready: " + ready);
                EditorApplication.Exit(ready ? 0 : 1);
            };
            AssetDatabase.importPackageFailed += (_, error) => { Debug.LogError(error); EditorApplication.Exit(1); };
            AssetDatabase.importPackageCancelled += _ => EditorApplication.Exit(1);
        }
        string pkg = Path.GetFullPath("Packages/com.unity.textmeshpro/Package Resources/TMP Essential Resources.unitypackage");
        AssetDatabase.ImportPackage(pkg, false);
        AssetDatabase.Refresh();
        Debug.Log("[ProjectSetup] Imported " + pkg);
    }

    /// <summary>Create dynamic SDF font assets: Barlow Condensed with Noto Sans KR fallback for Hangul.</summary>
    [MenuItem("Tools/FPS/Setup UI Fonts")]
    public static void SetupFonts() {
        TMP_FontAsset bodyRegular = MakeFont("NotoSansKR-Regular.otf", "Body-Regular", null);
        TMP_FontAsset bodyBold = MakeFont("NotoSansKR-Bold.otf", "Body-Bold", null);
        MakeFont("BarlowCondensed-Medium.ttf", "Cond-Medium", bodyRegular);
        MakeFont("BarlowCondensed-SemiBold.ttf", "Cond-SemiBold", bodyBold);
        MakeFont("BarlowCondensed-Bold.ttf", "Cond-Bold", bodyBold);
        AssetDatabase.SaveAssets();
        Debug.Log("[ProjectSetup] UI fonts ready.");
    }

    private static TMP_FontAsset MakeFont(string source, string name, TMP_FontAsset fallback) {
        string path = FontDir + name + ".asset";
        TMP_FontAsset existing = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
        if (existing != null) return existing;

        Font font = AssetDatabase.LoadAssetAtPath<Font>(FontDir + source);
        if (font == null) {
            Debug.LogError("[ProjectSetup] Missing font file " + FontDir + source);
            return null;
        }
        TMP_FontAsset fa = TMP_FontAsset.CreateFontAsset(font, 72, 8, GlyphRenderMode.SDFAA, 1024, 1024,
                                                         AtlasPopulationMode.Dynamic, true);
        fa.name = name;
        AssetDatabase.CreateAsset(fa, path);
        Texture2D atlas = fa.atlasTexture;
        atlas.name = name + " Atlas";
        AssetDatabase.AddObjectToAsset(atlas, fa);
        fa.material.name = name + " Material";
        AssetDatabase.AddObjectToAsset(fa.material, fa);
        if (fallback != null) {
            fa.fallbackFontAssetTable = new List<TMP_FontAsset> { fallback };
        }
        // Pre-bake the ASCII range so common text doesn't hit the dynamic path at runtime.
        fa.TryAddCharacters(Enumerable.Range(32, 95).Select(c => (char)c).Aggregate("", (s, c) => s + c));
        EditorUtility.SetDirty(fa);
        return fa;
    }

    /// <summary>Build a Linux player into Builds/Linux for local testing and screenshots.</summary>
    [MenuItem("Tools/FPS/Build Linux Player")]
    public static void BuildLinux() {
        BuildLinux("Builds/Linux/FPS.x86_64", BuildOptions.None);
    }

    /// <summary>
    /// Development player in Builds/LinuxProfile (or -buildPath DIR): the render counters PerfProbe logs (draw calls,
    /// SetPass, triangles, shadow casters) only exist in development builds.
    /// </summary>
    [MenuItem("Tools/FPS/Build Linux Player (profiling)")]
    public static void BuildLinuxProfiling() {
        string[] args = System.Environment.GetCommandLineArgs();
        int i = System.Array.IndexOf(args, "-buildPath");
        string dir = i >= 0 && i + 1 < args.Length ? args[i + 1] : "Builds/LinuxProfile";
        BuildLinux(dir.TrimEnd('/') + "/FPS.x86_64", BuildOptions.Development);
    }

    private static void BuildLinux(string path, BuildOptions options) {
        // An interrupted WebGL build (GameBuilds.BuildWebGL) can leave its texture cap behind.
        EditorUserBuildSettings.overrideMaxTextureSize = 0;
        string[] scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        if (scenes.Length == 0) scenes = new[] { "Assets/Scenes/Start.unity" };
        BuildPlayerOptions opts = new BuildPlayerOptions {
            scenes = scenes,
            locationPathName = path,
            target = BuildTarget.StandaloneLinux64,
            options = options,
        };
        var report = BuildPipeline.BuildPlayer(opts);
        Debug.Log("[ProjectSetup] Build " + report.summary.result + " (" + report.summary.totalErrors + " errors)");
        if (Application.isBatchMode && report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded) {
            EditorApplication.Exit(1);
        }
    }

}
