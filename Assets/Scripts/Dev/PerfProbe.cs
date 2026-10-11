using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

/// <summary>
/// Diagnostics (-perflog [seconds]): what a frame costs to draw. Logs render counters averaged over each interval
/// ([Perf] lines), and once in game a census of the scene: renderers, shadow casters, the
/// heaviest meshes, LOD groups, lights, cameras and the post-processing effects in use.
/// The census needs no graphics device, so it also runs in a -batchmode -nographics client. The render counters need a
/// player that actually draws and only exist in development builds (ProjectSetup.BuildLinuxProfiling). Batch mode skips
/// the camera loop, so a batch-mode client with a graphics device (-force-vulkan, no display) renders every camera into
/// a 1920x1080 render texture itself: measurements without a window.
/// -perfquit N quits after N seconds.
/// </summary>
public class PerfProbe : MonoBehaviour {

    private static bool parsed;
    private static float interval;
    private static float quitAfter;

    public static bool Enabled {
        get {
            if (!parsed) {
                parsed = true;
                string[] args = Environment.GetCommandLineArgs();
                int i = Array.IndexOf(args, "-perflog");
                if (i >= 0) {
                    interval = i + 1 < args.Length && float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float s) && s > 0f ? s : 5f;
                }
                int q = Array.IndexOf(args, "-perfquit");
                if (q >= 0 && q + 1 < args.Length) float.TryParse(args[q + 1], out quitAfter);
            }
            return interval > 0f;
        }
    }

    private struct Counter {
        public string Label;
        public ProfilerRecorder Recorder;
        public double Sum;
    }

    private readonly List<Counter> counters = new List<Counter>();
    private float windowStart;
    private int frames;
    private double frameMs;
    private bool censusDone;
    private float spawnedAt = -1f;
    private RenderTexture offscreen;
    private readonly List<Camera> cams = new List<Camera>();

    void OnEnable() {
        Application.runInBackground = true;
        Add("draws", ProfilerCategory.Render, "Draw Calls Count");
        Add("batches", ProfilerCategory.Render, "Batches Count");
        Add("setpass", ProfilerCategory.Render, "SetPass Calls Count");
        Add("tris", ProfilerCategory.Render, "Triangles Count");
        Add("verts", ProfilerCategory.Render, "Vertices Count");
        Add("shadowCasters", ProfilerCategory.Render, "Shadow Casters Count");
        Add("skinned", ProfilerCategory.Render, "Visible Skinned Meshes Count");
        Add("rtChanges", ProfilerCategory.Render, "Render Textures Changes Count");
        windowStart = Time.realtimeSinceStartup;
        if (Application.isBatchMode && SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null) {
            offscreen = new RenderTexture(1920, 1080, 24) { name = "PerfProbe" };
        }
    }

    void OnDisable() {
        foreach (Counter c in counters) c.Recorder.Dispose();
        counters.Clear();
        if (offscreen != null) offscreen.Release();
    }

    /// <summary>What the camera loop would draw, in depth order (the gun camera after the main one).</summary>
    private void RenderOffscreen() {
        cams.Clear();
        foreach (Camera c in Camera.allCameras) {
            if (c.targetTexture == null || c.targetTexture == offscreen) cams.Add(c);
        }
        cams.Sort((a, b) => a.depth.CompareTo(b.depth));
        foreach (Camera c in cams) {
            c.targetTexture = offscreen;
            c.Render();
        }
    }

    private void Add(string label, ProfilerCategory category, string name) {
        counters.Add(new Counter { Label = label, Recorder = ProfilerRecorder.StartNew(category, name) });
    }

    void LateUpdate() {
        if (offscreen != null) RenderOffscreen();
        frames++;
        frameMs += Time.unscaledDeltaTime * 1000.0;
        for (int i = 0; i < counters.Count; i++) {
            Counter c = counters[i];
            c.Sum += c.Recorder.LastValue;
            counters[i] = c;
        }
        if (PlayerNetworkMover.Local != null && spawnedAt < 0f) spawnedAt = Time.realtimeSinceStartup;
        if (!censusDone && spawnedAt > 0f && Time.realtimeSinceStartup - spawnedAt > 4f) {
            censusDone = true;
            Debug.Log(Census());
        }
        if (Time.realtimeSinceStartup - windowStart >= interval) Flush();
        if (quitAfter > 0f && Time.realtimeSinceStartup > quitAfter) {
            quitAfter = 0f;
            Debug.Log("[Perf] quitting");
            Application.Quit();
        }
    }

    private void Flush() {
        StringBuilder sb = new StringBuilder("[Perf] ");
        sb.Append(PlayerNetworkMover.Local != null ? "ingame" : "lobby");
        sb.Append(" map=").Append(MapLoader.CurrentId);
        sb.Append(" frames=").Append(frames).Append(" ms=").Append((frameMs / Mathf.Max(1, frames)).ToString("F1"));
        sb.Append(" screen=").Append(ScreenSize());
        sb.Append(" quality=").Append(NetConfig.GraphicsArg == "none" ? "project" : GraphicsQuality.Current.ToString()).Append('@').Append((GraphicsQuality.RenderScale * 100f).ToString("0")).Append('%');
        for (int i = 0; i < counters.Count; i++) {
            Counter c = counters[i];
            sb.Append(' ').Append(c.Label).Append('=').Append((c.Sum / Mathf.Max(1, frames)).ToString("0"));
            c.Sum = 0;
            counters[i] = c;
        }
        Debug.Log(sb.ToString());
        frames = 0;
        frameMs = 0;
        windowStart = Time.realtimeSinceStartup;
    }

    private string ScreenSize() {
        return offscreen != null ? offscreen.width + "x" + offscreen.height + " (offscreen)" : Screen.width + "x" + Screen.height;
    }

    private static string Census() {
        StringBuilder sb = new StringBuilder("[Perf] census on map " + MapLoader.CurrentId + "\n");
        // Only the first level of a LOD group counts (the worst case, up close); the others are listed per group below.
        var lodGroups = FindObjectsOfType<LODGroup>().Where(g => g.enabled && g.gameObject.activeInHierarchy).ToArray();
        var lowerLods = new HashSet<Renderer>();
        var lodSummary = new Dictionary<string, (int count, string levels)>();
        foreach (LODGroup g in lodGroups) {
            LOD[] lods = g.GetLODs();
            var levels = new List<string>();
            for (int i = 0; i < lods.Length; i++) {
                int t = 0;
                foreach (Renderer r in lods[i].renderers) {
                    if (r == null) continue;
                    if (i > 0) lowerLods.Add(r);
                    MeshFilter f = r.GetComponent<MeshFilter>();
                    if (f != null && f.sharedMesh != null) t += TriangleCount(f.sharedMesh);
                }
                levels.Add(t + "@" + lods[i].screenRelativeTransitionHeight.ToString("0.###"));
            }
            string key = g.name;
            lodSummary[key] = (lodSummary.TryGetValue(key, out var e) ? e.count + 1 : 1, string.Join(" ", levels));
        }
        Renderer[] all = FindObjectsOfType<Renderer>().Where(r => r.enabled && r.gameObject.activeInHierarchy && !lowerLods.Contains(r)).ToArray();
        int meshes = 0, skinned = 0, particles = 0, other = 0, casters = 0, materials = 0;
        long tris = 0, casterTris = 0;
        var heavy = new List<(int tris, string what)>();
        var mats = new HashSet<Material>();
        foreach (Renderer r in all) {
            Mesh mesh = null;
            if (r is MeshRenderer) {
                meshes++;
                MeshFilter f = r.GetComponent<MeshFilter>();
                mesh = f != null ? f.sharedMesh : null;
            } else if (r is SkinnedMeshRenderer smr) {
                skinned++;
                mesh = smr.sharedMesh;
            } else if (r is ParticleSystemRenderer) particles++;
            else other++;
            foreach (Material m in r.sharedMaterials) if (m != null) mats.Add(m);
            materials += r.sharedMaterials.Length;
            int t = mesh != null ? TriangleCount(mesh) : 0;
            tris += t;
            bool casts = r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off;
            if (casts) {
                casters++;
                casterTris += t;
            }
            heavy.Add((t, Path(r.transform) + " [" + r.GetType().Name + (casts ? ", casts" : "") + ", " + r.sharedMaterials.Length + " mats, layer " +
                          LayerMask.LayerToName(r.gameObject.layer) + "]"));
        }
        sb.AppendFormat("  renderers {0}: {1} mesh, {2} skinned, {3} particle, {4} other; {5} submeshes, {6} distinct materials\n",
                        all.Length, meshes, skinned, particles, other, materials, mats.Count);
        sb.AppendFormat("  triangles {0:N0} total, {1} shadow casters with {2:N0} triangles\n", tris, casters, casterTris);
        foreach (var h in heavy.OrderByDescending(h => h.tris).Take(25)) sb.AppendFormat("    {0,8:N0}  {1}\n", h.tris, h.what);
        foreach (var kv in lodSummary) sb.AppendFormat("  LOD group '{0}' x{1}: tris@height {2}\n", kv.Key, kv.Value.count, kv.Value.levels);
        // Group by top-level object so a scene of many small pieces shows up as such.
        foreach (var g in all.GroupBy(r => r.transform.root.name).OrderByDescending(g => g.Count()).Take(10)) {
            sb.AppendFormat("  root '{0}': {1} renderers\n", g.Key, g.Count());
        }
        foreach (Light l in FindObjectsOfType<Light>()) {
            if (!l.enabled || !l.gameObject.activeInHierarchy) continue;
            sb.AppendFormat("  light {0} {1} shadows={2} res={3} mode={4} range={5:0}\n", Path(l.transform), l.type, l.shadows, l.shadowResolution, l.renderMode, l.range);
        }
        foreach (Camera c in Camera.allCameras) {
            PostProcessLayer layer = c.GetComponent<PostProcessLayer>();
            sb.AppendFormat("  camera {0} hdr={1} msaa={2} depthMode={3} far={4:0} post={5} aa={6}\n", Path(c.transform), c.allowHDR, c.allowMSAA,
                            c.depthTextureMode, c.farClipPlane, layer != null && layer.enabled, layer != null ? layer.antialiasingMode.ToString() : "-");
        }
        foreach (PostProcessVolume v in FindObjectsOfType<PostProcessVolume>()) {
            PostProcessProfile p = v.HasInstantiatedProfile() ? v.profile : v.sharedProfile;
            if (p == null) continue;
            sb.Append("  post volume ").Append(v.name).Append(':');
            foreach (PostProcessEffectSettings s in p.settings) sb.Append(' ').Append(s.GetType().Name).Append(s.enabled.value && s.active ? "+" : "-");
            sb.Append('\n');
        }
        sb.AppendFormat("  quality '{0}' shadows={1} res={2} cascades={3} dist={4} pixelLights={5} msaa={6} probes={7} lodBias={8} vsync={9}",
                        QualitySettings.names[QualitySettings.GetQualityLevel()], QualitySettings.shadows, QualitySettings.shadowResolution,
                        QualitySettings.shadowCascades, QualitySettings.shadowDistance, QualitySettings.pixelLightCount, QualitySettings.antiAliasing,
                        QualitySettings.realtimeReflectionProbes, QualitySettings.lodBias, QualitySettings.vSyncCount);
        return sb.ToString();
    }

    private static int TriangleCount(Mesh m) {
        long n = 0;
        for (int i = 0; i < m.subMeshCount; i++) n += m.GetIndexCount(i);
        return (int)(n / 3);
    }

    private static string Path(Transform t) {
        string p = t.name;
        for (int i = 0; i < 3 && t.parent != null; i++) {
            t = t.parent;
            p = t.name + "/" + p;
        }
        return p;
    }

}
