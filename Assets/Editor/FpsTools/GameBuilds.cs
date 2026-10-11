using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Release builds for the hosted game:
///   Builds/Server  Linux dedicated server for GameLift (FPSServer.x86_64 + logs/), uploaded by Server/deploy/deploy-server.sh
///   Builds/WebGL   browser client with the lobby API baked in, uploaded by Server/deploy/deploy-web.sh
/// Headless:
///   Unity -batchmode -quit -projectPath . -executeMethod GameBuilds.BuildServer
///   Unity -batchmode -quit -projectPath . -executeMethod GameBuilds.BuildWebGL -apiBase https://xxxx.execute-api.ap-northeast-2.amazonaws.com
/// </summary>
public static class GameBuilds {

    private const string NetConfigPath = "Assets/Resources/netconfig.json";

    private static string[] Scenes() {
        string[] scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        return scenes.Length > 0 ? scenes : new[] { "Assets/Scenes/Start.unity" };
    }

    [MenuItem("Tools/FPS/Build Dedicated Server (Linux)")]
    public static void BuildServer() {
        string dir = Arg("-buildOutput") ?? "Builds/Server";
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        // An interrupted WebGL build can leave its texture cap behind.
        EditorUserBuildSettings.overrideMaxTextureSize = 0;
        EditorUserBuildSettings.standaloneBuildSubtarget = StandaloneBuildSubtarget.Server;
        BuildPlayerOptions opts = new BuildPlayerOptions {
            scenes = Scenes(),
            locationPathName = dir + "/FPSServer.x86_64",
            target = BuildTarget.StandaloneLinux64,
            subtarget = (int)StandaloneBuildSubtarget.Server,
            options = BuildOptions.None,
        };
        bool ok = Finish("Server", BuildPipeline.BuildPlayer(opts));
        EditorUserBuildSettings.standaloneBuildSubtarget = StandaloneBuildSubtarget.Player;
        if (ok) {
            // The fleet's launch parameters log to /local/game/logs, which Unity will not create itself.
            Directory.CreateDirectory(dir + "/logs");
            File.WriteAllText(dir + "/logs/.keep", "");
            // GameLift runs install.sh as root once per instance. The server converts the fleet's TLS certificate with
            // the openssl CLI, which a minimal Amazon Linux 2023 image may not have.
            File.WriteAllText(dir + "/install.sh",
                "#!/bin/bash\nset -e\ncommand -v openssl >/dev/null 2>&1 || dnf install -y openssl\nmkdir -p /local/game/logs\n");
            // Debug symbols only bloat the GameLift upload.
            foreach (string sym in Directory.GetFiles(dir, "*_s.debug")) File.Delete(sym);
        }
        ExitOnFailure(ok);
    }

    [MenuItem("Tools/FPS/Build WebGL")]
    public static void BuildWebGL() {
        string apiBase = Arg("-apiBase") ?? Environment.GetEnvironmentVariable("FPS_API_BASE") ?? "";
        string original = File.Exists(NetConfigPath) ? File.ReadAllText(NetConfigPath) : null;
        bool ok;
        try {
            File.WriteAllText(NetConfigPath, "{\n  \"apiBase\": \"" + apiBase.TrimEnd('/') + "\",\n  \"devHost\": \"127.0.0.1\",\n  \"devPort\": 7770\n}\n");
            AssetDatabase.ImportAsset(NetConfigPath);

            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Brotli;
            // S3 + CloudFront serve the .br files with Content-Encoding headers, so no JS decompressor is needed;
            // keep it anyway so a misconfigured host still loads (slower) instead of failing.
            PlayerSettings.WebGL.decompressionFallback = true;
            PlayerSettings.WebGL.dataCaching = true;
            PlayerSettings.WebGL.memorySize = 512;
            PlayerSettings.runInBackground = true;
            // Download size matters more than texel density in a browser: cap textures at 1K for this build only.
            EditorUserBuildSettings.overrideMaxTextureSize = 1024;

            string dir = Arg("-buildOutput") ?? "Builds/WebGL";
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            BuildPlayerOptions opts = new BuildPlayerOptions {
                scenes = Scenes(),
                locationPathName = dir,
                target = BuildTarget.WebGL,
                options = BuildOptions.None,
            };
            ok = Finish("WebGL (api " + (apiBase.Length > 0 ? apiBase : "none, dev server") + ")", BuildPipeline.BuildPlayer(opts));
        } finally {
            EditorUserBuildSettings.overrideMaxTextureSize = 0;
            // Don't leave a deployment URL in the working tree.
            if (original != null) File.WriteAllText(NetConfigPath, original);
            AssetDatabase.ImportAsset(NetConfigPath);
        }
        ExitOnFailure(ok);
    }

    private static bool Finish(string what, BuildReport report) {
        Debug.Log("[GameBuilds] " + what + ": " + report.summary.result + " (" + report.summary.totalErrors + " errors, " +
                  (report.summary.totalSize / (1024 * 1024)) + " MB)");
        return report.summary.result == BuildResult.Succeeded;
    }

    private static void ExitOnFailure(bool ok) {
        if (Application.isBatchMode && !ok) EditorApplication.Exit(1);
    }

    private static string Arg(string name) {
        string[] args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

}
