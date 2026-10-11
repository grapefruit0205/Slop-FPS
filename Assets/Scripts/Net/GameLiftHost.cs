using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Debug = UnityEngine.Debug;
#if UNITY_SERVER
using Aws.GameLift.Server;
using Aws.GameLift;
using Aws.GameLift.Server.Model;
#endif

/// <summary>
/// Thin wrapper over the GameLift Server SDK. Compiles to no-ops outside dedicated-server builds, so client and
/// WebGL builds never reference the SDK. SDK callbacks arrive on a socket thread and are queued for <see cref="Pump"/>.
/// </summary>
public static class GameLiftHost {

    public static bool Active { get; private set; }

    /// <summary>
    /// A game session was assigned to this process: its name as given by the lobby, and its game properties (the lobby
    /// sets `mode` = tdm|ffa and `map` = warehouse|outpost; never null).
    /// </summary>
    public static event Action<string, Dictionary<string, string>> SessionStarted;

    /// <summary>GameLift wants this process gone (scale-in or fleet deletion).</summary>
    public static event Action TerminationRequested;

    private static readonly ConcurrentQueue<Action> mainThread = new ConcurrentQueue<Action>();

    public static void Pump() {
        while (mainThread.TryDequeue(out Action a)) a();
    }

#if UNITY_SERVER
    public static bool Init(int port, string logPath) {
        GenericOutcome init = GameLiftServerAPI.InitSDK();
        if (!init.Success) {
            Debug.LogError("[GameLift] InitSDK failed: " + init.Error);
            return false;
        }
        ProcessParameters parameters = new ProcessParameters(
            session => mainThread.Enqueue(() => OnStartGameSession(session)),
            update => { },
            () => mainThread.Enqueue(() => TerminationRequested?.Invoke()),
            () => true,
            port,
            new LogParameters(new List<string> { logPath }));
        GenericOutcome ready = GameLiftServerAPI.ProcessReady(parameters);
        if (!ready.Success) {
            Debug.LogError("[GameLift] ProcessReady failed: " + ready.Error);
            return false;
        }
        Active = true;
        Debug.Log("[GameLift] Process ready on port " + port);
        return true;
    }

    private static void OnStartGameSession(GameSession session) {
        GenericOutcome outcome = GameLiftServerAPI.ActivateGameSession();
        Dictionary<string, string> props = session.GameProperties != null
            ? new Dictionary<string, string>(session.GameProperties) : new Dictionary<string, string>();
        Debug.Log("[GameLift] Session " + session.GameSessionId + " '" + session.Name + "' activate: " + outcome.Success +
                  ", properties: " + string.Join(", ", System.Linq.Enumerable.Select(props, kv => kv.Key + "=" + kv.Value)));
        SessionStarted?.Invoke(session.Name, props);
    }

    public static bool AcceptPlayer(string playerSessionId) {
        if (!Active) return true;
        if (string.IsNullOrEmpty(playerSessionId)) return false;
        GenericOutcome outcome = GameLiftServerAPI.AcceptPlayerSession(playerSessionId);
        if (!outcome.Success) Debug.LogWarning("[GameLift] AcceptPlayerSession " + playerSessionId + ": " + outcome.Error);
        return outcome.Success;
    }

    public static void RemovePlayer(string playerSessionId) {
        if (!Active || string.IsNullOrEmpty(playerSessionId)) return;
        GameLiftServerAPI.RemovePlayerSession(playerSessionId);
    }

    /// <summary>
    /// The fleet generates a TLS certificate per instance (PEM files). Bayou wants a PKCS#12 file, so convert it with
    /// the openssl binary that ships with Amazon Linux. Returns the .pfx path, or null when TLS is unavailable.
    /// </summary>
    public static string PrepareCertificate(string password) {
        if (!Active) return null;
        GetComputeCertificateOutcome outcome = GameLiftServerAPI.GetComputeCertificate();
        if (!outcome.Success || outcome.Result == null) {
            Debug.LogWarning("[GameLift] No compute certificate: " + outcome.Error);
            return null;
        }
        string path = outcome.Result.CertificatePath;
        string pfx = PemToPfx(Directory.Exists(path) ? path : Path.GetDirectoryName(path), password);
        if (pfx == null) return null;
        Debug.Log("[GameLift] TLS certificate for " + outcome.Result.ComputeName + " ready");
        return pfx;
    }

    /// <summary>Tell GameLift this process is done; it will start a fresh one in its place.</summary>
    public static void EndProcess() {
        if (!Active) return;
        GameLiftServerAPI.ProcessEnding();
        GameLiftServerAPI.Destroy();
        Active = false;
    }
#else
    public static bool Init(int port, string logPath) { return false; }
    public static bool AcceptPlayer(string playerSessionId) { return true; }
    public static void RemovePlayer(string playerSessionId) { }
    public static string PrepareCertificate(string password) { return null; }
    public static void EndProcess() { }
#endif

    /// <summary>
    /// Bayou wants a PKCS#12 file, so convert certificate.pem / privateKey.pem (/ certificateChain.pem) in
    /// <paramref name="dir"/> with the openssl binary (installed by the build's install.sh). Mono cannot read
    /// OpenSSL 3's default AES-encrypted PKCS#12, hence the legacy 3DES/SHA1 parameters. Returns the path or null.
    /// </summary>
    public static string PemToPfx(string dir, string password) {
        string cert = Path.Combine(dir, "certificate.pem");
        string key = Path.Combine(dir, "privateKey.pem");
        string chain = Path.Combine(dir, "certificateChain.pem");
        if (!File.Exists(cert) || !File.Exists(key)) {
            Debug.LogWarning("[TLS] Certificate files missing in " + dir + ": " +
                             (Directory.Exists(dir) ? string.Join(", ", Directory.GetFiles(dir)) : "(no such directory)"));
            return null;
        }
        string pfx = Path.Combine(Path.GetTempPath(), "fps-server-" + Process.GetCurrentProcess().Id + ".pfx");
        string args = "pkcs12 -export -out \"" + pfx + "\" -inkey \"" + key + "\" -in \"" + cert + "\"" +
                      (File.Exists(chain) ? " -certfile \"" + chain + "\"" : "") +
                      " -keypbe PBE-SHA1-3DES -certpbe PBE-SHA1-3DES -macalg sha1 -passout pass:" + password;
        try {
            using (Process p = Process.Start(new ProcessStartInfo("openssl", args) { UseShellExecute = false, RedirectStandardError = true })) {
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0) {
                    Debug.LogError("[TLS] openssl failed (" + p.ExitCode + "): " + err);
                    return null;
                }
            }
        } catch (System.ComponentModel.Win32Exception e) {
            Debug.LogError("[TLS] openssl not available: " + e.Message);
            return null;
        }
        return pfx;
    }

}
