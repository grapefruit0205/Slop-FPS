using System;
using UnityEngine;

/// <summary>
/// Where to find the lobby API and how this process should run.
/// Defaults come from Resources/netconfig.json (written by the build tools); command-line arguments override them:
///   -server            run as a dedicated server (implied by Linux dedicated-server builds)
///   -port 7770         server listen port
///   -gamelift / -nogamelift   force GameLift integration on/off (default: on when running from /local/game)
///   -api https://...   lobby API base URL (clients)
///   -connect host:port skip the lobby API and join a server directly (local testing)
///   -tlsdir <dir>      serve wss:// with certificate.pem/privateKey.pem from dir (dev servers without GameLift)
///   -mode tdm|ffa|elim (server without GameLift) game mode; GameLift sessions bring it as the game property `mode`
///   -map warehouse|outpost   (server without GameLift) map; GameLift sessions bring it as the game property `map`
///   -scorelimit N      (server) kills that end a round, instead of the mode's 50 / 30 (round wins in elim, 7)
///   -roundtime S       (server) round length in seconds, instead of 600 (120 per round in elim)
///   -devspawn          (server) spawn everyone within sight of each other, for automated tests
///   -autojoin          join the first listed room as soon as the lobby has one (automated tests)
///   -gfx low|medium|high   (clients) graphics preset for this run, not saved
///   -nomapbatch        (clients) skip runtime static batching of map geometry (before/after measurements)
/// WebGL builds also accept ?api=..., ?connect=..., ?autojoin, ?name=..., ?gfx=... and ?debuginput in the page URL.
/// </summary>
public static class NetConfig {

    public const ushort DefaultPort = 7770;
    public const int MaxPlayersPerRoom = 8;
    public const int MinPlayersToStart = 2;
    /// <summary>Bumped whenever client and server stop being wire-compatible.</summary>
    public const string ProtocolVersion = "7";

    [Serializable]
    private class FileConfig {
        public string apiBase = "";
        public string devHost = "127.0.0.1";
        public int devPort = DefaultPort;
    }

    private static bool loaded;
    private static FileConfig file;

    public static bool IsServer {
        get {
#if UNITY_SERVER
            return true;
#else
            return HasArg("-server");
#endif
        }
    }

    public static ushort ServerPort => ushort.TryParse(Arg("-port"), out ushort p) ? p : DefaultPort;

    public static bool UseGameLift {
        get {
            if (HasArg("-nogamelift")) return false;
            return HasArg("-gamelift") || Application.dataPath.StartsWith("/local/game");
        }
    }

    /// <summary>Lobby API base URL without a trailing slash; empty means "no directory, use the dev server".</summary>
    public static string ApiBase {
        get {
            string v = Arg("-api") ?? Query("api") ?? File.apiBase;
            return string.IsNullOrEmpty(v) ? "" : v.TrimEnd('/');
        }
    }

    public static bool HasDirectory => ApiBase.Length > 0 && DirectConnect == null;

    /// <summary>"host:port" for a server to join without the lobby API, or null.</summary>
    public static string DirectConnect => Arg("-connect") ?? Query("connect");

    public static string DevHost {
        get {
            string direct = DirectConnect;
            if (direct != null) return direct.Split(':')[0];
            return File.devHost;
        }
    }

    public static ushort DevPort {
        get {
            string direct = DirectConnect;
            if (direct != null && direct.Contains(":") && ushort.TryParse(direct.Split(':')[1], out ushort p)) return p;
            return (ushort)File.devPort;
        }
    }

    public static bool AutoJoin => HasArg("-autojoin") || Query("autojoin") != null;

    /// <summary>Callsign preset for automated runs (-name / ?name=).</summary>
    public static string PresetName => Arg("-name") ?? Query("name");
    public static string LanguageArg => Arg("-lang") ?? Query("lang");

    /// <summary>Servers without GameLift: -mode / -map (null when not given; GameLift sessions carry their own).</summary>
    public static string ModeArg => Arg("-mode");
    public static string MapArg => Arg("-map");

    /// <summary>Servers: -scorelimit N / -roundtime S for quick tests; 0 = the mode's defaults.</summary>
    public static int ScoreLimitOverride => int.TryParse(Arg("-scorelimit"), out int n) && n > 0 ? n : 0;
    public static float RoundTimeOverride => float.TryParse(Arg("-roundtime"), System.Globalization.NumberStyles.Float,
                                                            System.Globalization.CultureInfo.InvariantCulture, out float t) && t > 0f ? t : 0f;

    /// <summary>Dev servers: spawn everyone within sight of each other (-devspawn), for automated tests.</summary>
    public static bool DevSpawn => HasArg("-devspawn");

    /// <summary>Log avatar positions every 2 s on the server and the owner (-tracemove).</summary>
    public static bool TraceMovement => HasArg("-tracemove");

    /// <summary>Clients: graphics preset for this run only (-gfx low|medium|high / ?gfx=), see <see cref="GraphicsQuality"/>.</summary>
    public static string GraphicsArg => Arg("-gfx") ?? Query("gfx");

    /// <summary>Clients: leave map renderers unbatched (-nomapbatch / ?nomapbatch), see <see cref="MapBatching"/>.</summary>
    public static bool NoMapBatching => HasArg("-nomapbatch") || Query("nomapbatch") != null;

    /// <summary>Log mouse clicks and what the UI raycast hit (-debuginput / ?debuginput).</summary>
    public static bool DebugInput => HasArg("-debuginput") || Query("debuginput") != null;

    /// <summary>Dev servers: folder with certificate.pem + privateKey.pem to serve wss:// without GameLift.</summary>
    public static string TlsDir => Arg("-tlsdir");

    private static FileConfig File {
        get {
            if (!loaded) {
                loaded = true;
                TextAsset t = Resources.Load<TextAsset>("netconfig");
                file = t != null ? JsonUtility.FromJson<FileConfig>(t.text) : null;
                file = file ?? new FileConfig();
            }
            return file;
        }
    }

    private static bool HasArg(string name) {
        return Array.IndexOf(Environment.GetCommandLineArgs(), name) >= 0;
    }

    private static string Arg(string name) {
        string[] args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string Query(string key) {
#if UNITY_WEBGL && !UNITY_EDITOR
        string url = Application.absoluteURL;
        int q = url.IndexOf('?');
        if (q < 0) return null;
        foreach (string pair in url.Substring(q + 1).Split('&')) {
            int eq = pair.IndexOf('=');
            if (eq < 0 && pair == key) return "";  // bare flag such as ?autojoin
            if (eq > 0 && pair.Substring(0, eq) == key) return Uri.UnescapeDataString(pair.Substring(eq + 1));
        }
#endif
        return null;
    }

}
