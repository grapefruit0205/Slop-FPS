using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

/// <summary>
/// Automated checks for a built map (run on an instance in an open scene, using real physics queries) and the
/// screenshot helper for the preview scene. Everything is logged with a [MapCheck] prefix; the report counts failures.
/// </summary>
public static class MapChecks {

    public class Report {
        public readonly List<string> Lines = new List<string>();
        public int Failures;
        public void Ok(string s) { Lines.Add("  ok    " + s); }
        public void Info(string s) { Lines.Add("  info  " + s); }
        public void Fail(string s) { Lines.Add("  FAIL  " + s); Failures++; }
        public override string ToString() { return string.Join("\n", Lines); }
    }

    private const QueryTriggerInteraction Ignore = QueryTriggerInteraction.Ignore;

    public static Report Run(GameObject root, MapDefinition def, Bounds playable, Vector3[] mustReach) {
        Report r = new Report();
        Physics.SyncTransforms();
        Stats(r, root);
        Colliders(r, root);
        Coverage(r, root);
        Spawns(r, "FFA", def.spawnPoints);
        Spawns(r, "TeamA", def.teamASpawns);
        Spawns(r, "TeamB", def.teamBSpawns);
        Boundary(r, playable);
        Walk(r, playable, def, mustReach);
        Debug.Log("[MapCheck] " + def.id + ": " + (r.Failures == 0 ? "ALL CHECKS PASSED" : r.Failures + " FAILED") + "\n" + r);
        return r;
    }

    // ------------------------------------------------------------------ stats

    private static void Stats(Report r, GameObject root) {
        int go = root.GetComponentsInChildren<Transform>(true).Length;
        int rend = 0, tris = 0, verts = 0;
        foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>()) {
            if (mf.sharedMesh == null) continue;
            rend++;
            tris += mf.sharedMesh.triangles.Length / 3;
            verts += mf.sharedMesh.vertexCount;
        }
        int cols = root.GetComponentsInChildren<Collider>(true).Length;
        int lights = root.GetComponentsInChildren<Light>(true).Length;
        r.Info(go + " GameObjects, " + rend + " mesh renderers (draw calls before batching), " + tris + " triangles, " + verts + " vertices, "
               + cols + " colliders, " + lights + " lights");
    }

    // ------------------------------------------------------------------ colliders: layer and surface tag

    private static void Colliders(Report r, GameObject root) {
        int shootable = LayerMask.NameToLayer(MapKit.ShootableLayer);
        int ignore = LayerMask.NameToLayer("Ignore Raycast");
        Transform boundary = root.transform.Find("Boundary");
        int total = 0, bad = 0, withRenderer = 0;
        StringBuilder sb = new StringBuilder();
        foreach (Collider c in root.GetComponentsInChildren<Collider>(true)) {
            total++;
            if (boundary != null && c.transform.IsChildOf(boundary)) {
                if (c.gameObject.layer != ignore || c.isTrigger || c.GetComponent<Renderer>() != null) { bad++; sb.Append(" ").Append(c.name).Append("(boundary)"); }
                continue;
            }
            if (c.GetComponent<Renderer>() != null || c.GetComponentInParent<Renderer>() != null) withRenderer++;
            bool ok = c.gameObject.layer == shootable && MapKit.SurfaceTags.Contains(c.gameObject.tag) && !c.isTrigger;
            if (!ok) {
                bad++;
                if (bad < 12) sb.Append(" ").Append(c.name).Append("[").Append(LayerMask.LayerToName(c.gameObject.layer)).Append("/").Append(c.gameObject.tag).Append("]");
            }
        }
        if (bad == 0) r.Ok(total + " colliders (" + withRenderer + " with renderers): all on Shootable with a valid surface tag, boundary on Ignore Raycast");
        else r.Fail(bad + " of " + total + " colliders on the wrong layer/tag:" + sb);
    }

    // ------------------------------------------------------------------ every visible surface has a collider

    private static bool Skipped(Transform t, Transform root) {
        for (Transform p = t; p != null && p != root; p = p.parent) {
            if (p.name == "Decor" || p.name == "Backdrop") return true;
        }
        return false;
    }

    private static void Coverage(Report r, GameObject root) {
        int mask = 1 << LayerMask.NameToLayer(MapKit.ShootableLayer);
        const float tolerance = 0.15f;
        long checkedTris = 0, missing = 0;
        List<string> offenders = new List<string>();
        foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>()) {
            if (mf.sharedMesh == null || Skipped(mf.transform, root.transform)) continue;
            MeshRenderer mr = mf.GetComponent<MeshRenderer>();
            if (mr == null || !mr.enabled) continue;
            if (mf.transform.parent != null && mf.transform.parent.name == "Props") continue;
            Mesh m = mf.sharedMesh;
            if (!m.isReadable) continue;
            Vector3[] v = m.vertices;
            int[] t = m.triangles;
            Matrix4x4 l2w = mf.transform.localToWorldMatrix;
            int bad = 0, n = t.Length / 3;
            for (int i = 0; i < t.Length; i += 3) {
                Vector3 c = l2w.MultiplyPoint3x4((v[t[i]] + v[t[i + 1]] + v[t[i + 2]]) / 3f);
                if (!Physics.CheckSphere(c, tolerance, mask, Ignore)) bad++;
            }
            checkedTris += n;
            missing += bad;
            if (bad > 0) offenders.Add(mf.name + " " + bad + "/" + n);
        }
        // Prop models: every prop root must carry a collider of its own.
        int props = 0, propBad = 0;
        foreach (Transform grp in root.GetComponentsInChildren<Transform>(true)) {
            if (grp.name != "Props" || Skipped(grp, root.transform)) continue;
            foreach (Transform prop in grp) {
                props++;
                if (prop.GetComponentInChildren<Collider>() == null) {
                    propBad++;
                    offenders.Add("prop without collider: " + prop.name);
                }
            }
        }
        if (checkedTris == 0) r.Fail("no readable geometry to check");
        else if (missing == 0 && propBad == 0) r.Ok("every one of " + checkedTris + " visible triangles lies within " + (tolerance * 100f) + " cm of a Shootable collider; " + props + " prop models all carry colliders");
        else r.Fail(missing + "/" + checkedTris + " triangles without a nearby collider, " + propBad + " props without one: " + string.Join(", ", offenders.Take(14)));
    }

    // ------------------------------------------------------------------ spawn points

    private static void Spawns(Report r, string label, Transform[] spawns) {
        if (spawns == null || spawns.Length == 0) { r.Fail(label + ": no spawn points"); return; }
        int bad = 0;
        StringBuilder sb = new StringBuilder();
        foreach (Transform s in spawns) {
            Vector3 p = s.position;
            string problem = null;
            if (!Physics.Raycast(p + Vector3.up * 0.3f, Vector3.down, out RaycastHit hit, 3f, ~0, Ignore)) problem = "no floor below";
            else {
                float drop = p.y - hit.point.y;
                if (drop < 0f || drop > 0.5f) problem = "floor " + drop.ToString("F2") + " m below";
            }
            if (problem == null && Physics.CheckCapsule(p + Vector3.up * 0.35f, p + Vector3.up * 1.45f, 0.3f, ~0, Ignore)) problem = "inside a collider";
            if (problem == null) {
                Vector3 f = s.forward;
                f.y = 0f;
                if (Physics.Raycast(p + Vector3.up * 1.6f, f.normalized, out RaycastHit wall, 3f, ~0, Ignore)) problem = "faces a wall " + wall.distance.ToString("F1") + " m away";
            }
            if (problem != null) { bad++; sb.Append(" ").Append(s.name).Append(": ").Append(problem).Append(";"); }
        }
        float minD = float.MaxValue;
        string pair = "";
        for (int i = 0; i < spawns.Length; i++) {
            for (int j = i + 1; j < spawns.Length; j++) {
                float d = Vector3.Distance(spawns[i].position, spawns[j].position);
                if (d < minD) { minD = d; pair = spawns[i].name + "-" + spawns[j].name; }
            }
        }
        if (bad == 0) r.Ok(label + ": " + spawns.Length + " spawns, all have floor within 0.5 m below, none inside a collider, none facing a wall");
        else r.Fail(label + ": " + bad + " bad spawns:" + sb);
        if (spawns.Length > 1) {
            if (minD >= 3f) r.Ok(label + ": closest pair " + pair + " is " + minD.ToString("F1") + " m apart (min 3)");
            else r.Fail(label + ": " + pair + " only " + minD.ToString("F1") + " m apart");
        }
    }

    // ------------------------------------------------------------------ boundary

    private static void Boundary(Report r, Bounds area) {
        int leaks = 0, rays = 0, far = 0;
        float maxHit = 0f;
        StringBuilder sb = new StringBuilder();
        float[] heights = { 1.2f, 5.0f, 7.0f };
        for (float x = area.min.x + 1f; x < area.max.x; x += 6f) {
            for (float z = area.min.z + 1f; z < area.max.z; z += 6f) {
                foreach (float h in heights) {
                    Vector3 o = new Vector3(x, h, z);
                    if (Physics.CheckSphere(o, 0.3f, ~0, Ignore)) continue;      // inside a wall or prop
                    // Only points with a floor under them within reach count as places a player can be.
                    if (!Physics.Raycast(o, Vector3.down, out RaycastHit under, h + 0.5f, ~0, Ignore)) continue;
                    if (under.distance > 2.0f) continue;                       // not a height anyone stands at
                    for (int k = 0; k < 24; k++) {
                        float a = k * Mathf.PI * 2f / 24f;
                        Vector3 d = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                        rays++;
                        if (!Physics.Raycast(o, d, out RaycastHit hit, 200f, ~0, Ignore)) {
                            leaks++;
                            if (leaks < 6) sb.Append(" (").Append(o.x.ToString("F0")).Append(",").Append(o.y.ToString("F0")).Append(",").Append(o.z.ToString("F0")).Append(")->").Append(a.ToString("F1"));
                            continue;
                        }
                        maxHit = Mathf.Max(maxHit, Mathf.Max(Mathf.Abs(hit.point.x), Mathf.Abs(hit.point.z)));
                        if (Mathf.Max(Mathf.Abs(hit.point.x), Mathf.Abs(hit.point.z)) > Mathf.Max(area.extents.x, area.extents.z) + 2.5f) far++;
                    }
                }
            }
        }
        if (leaks == 0 && far == 0) r.Ok("boundary closed: " + rays + " rays from inside the playable area (floor level, upper floors, tower decks) all stop at x/z <= " + maxHit.ToString("F1"));
        else r.Fail("boundary leaks: " + leaks + " rays escape, " + far + " land beyond the walls:" + sb);
    }

    // ------------------------------------------------------------------ walkability

    private static bool Free(Vector3 p) {
        return !Physics.CheckCapsule(p + Vector3.up * 0.58f, p + Vector3.up * 1.42f, 0.28f, ~0, Ignore);   // bottom sphere starts at the 0.3 m step offset
    }

    /// <summary>
    /// Flood fill over a 0.5 m grid with the player's step height: can every spawn reach every other, and the places
    /// players are meant to get to (towers, upper floor)? Unreached floor clusters are listed for inspection.
    /// </summary>
    private static void Walk(Report r, Bounds area, MapDefinition def, Vector3[] mustReach) {
        const float cell = 0.5f;
        int nx = Mathf.CeilToInt(area.size.x / cell), nz = Mathf.CeilToInt(area.size.z / cell);
        List<float>[,] floors = new List<float>[nx, nz];
        for (int ix = 0; ix < nx; ix++) {
            for (int iz = 0; iz < nz; iz++) {
                floors[ix, iz] = new List<float>();
                Vector3 o = new Vector3(area.min.x + (ix + 0.5f) * cell, 16f, area.min.z + (iz + 0.5f) * cell);
                RaycastHit[] hits = Physics.RaycastAll(o, Vector3.down, 20f, ~0, Ignore);
                foreach (RaycastHit h in hits.OrderByDescending(h => h.point.y)) {
                    if (h.normal.y < 0.7f) continue;
                    if (Free(h.point)) floors[ix, iz].Add(h.point.y);
                }
            }
        }
        // visited flags per (ix, iz, floor index)
        Dictionary<long, bool> visited = new Dictionary<long, bool>();
        System.Func<int, int, int, long> key = (a, b, c) => ((long)a * 100000L + b) * 16L + c;
        Queue<Vector3Int> q = new Queue<Vector3Int>();
        System.Func<Vector3, Vector3Int?> locate = p => {
            int ix = Mathf.FloorToInt((p.x - area.min.x) / cell), iz = Mathf.FloorToInt((p.z - area.min.z) / cell);
            if (ix < 0 || iz < 0 || ix >= nx || iz >= nz) return null;
            int best = -1;
            float bd = 0.8f;
            for (int k = 0; k < floors[ix, iz].Count; k++) {
                float d = Mathf.Abs(floors[ix, iz][k] - p.y);
                if (d < bd) { bd = d; best = k; }
            }
            return best < 0 ? (Vector3Int?)null : new Vector3Int(ix, iz, best);
        };

        List<Transform> seeds = new List<Transform>();
        if (def.spawnPoints != null) seeds.AddRange(def.spawnPoints);
        if (def.teamASpawns != null) seeds.AddRange(def.teamASpawns);
        if (def.teamBSpawns != null) seeds.AddRange(def.teamBSpawns);
        Vector3Int? start = null;
        foreach (Transform s in seeds) {
            start = locate(s.position - Vector3.up * 0.2f);
            if (start.HasValue) break;
        }
        if (!start.HasValue) { r.Fail("walkability: first spawn is not on a standable cell"); return; }
        q.Enqueue(start.Value);
        visited[key(start.Value.x, start.Value.y, start.Value.z)] = true;
        int reached = 0;
        while (q.Count > 0) {
            Vector3Int c = q.Dequeue();
            reached++;
            float y = floors[c.x, c.y][c.z];
            for (int dx = -1; dx <= 1; dx++) {
                for (int dz = -1; dz <= 1; dz++) {
                    if (dx == 0 && dz == 0) continue;
                    int jx = c.x + dx, jz = c.y + dz;
                    if (jx < 0 || jz < 0 || jx >= nx || jz >= nz) continue;
                    for (int k = 0; k < floors[jx, jz].Count; k++) {
                        float y2 = floors[jx, jz][k];
                        if (Mathf.Abs(y2 - y) > 0.4f) continue;
                        long kk = key(jx, jz, k);
                        if (visited.ContainsKey(kk)) continue;
                        Vector3 mid = new Vector3(area.min.x + (c.x + 0.5f + dx * 0.5f) * cell, Mathf.Max(y, y2), area.min.z + (c.y + 0.5f + dz * 0.5f) * cell);
                        if (!Free(mid)) continue;
                        visited[kk] = true;
                        q.Enqueue(new Vector3Int(jx, jz, k));
                    }
                }
            }
        }

        int stranded = 0;
        StringBuilder sb = new StringBuilder();
        foreach (Transform s in seeds) {
            Vector3Int? l = locate(s.position - Vector3.up * 0.2f);
            if (!l.HasValue || !visited.ContainsKey(key(l.Value.x, l.Value.y, l.Value.z))) { stranded++; sb.Append(" ").Append(s.name); }
        }
        if (stranded == 0) r.Ok("walkability: all " + seeds.Count + " spawn points are mutually reachable on foot (0.5 m grid, step 0.4 m)");
        else r.Fail("walkability: " + stranded + " spawn points cut off from the first:" + sb);

        if (mustReach != null) {
            StringBuilder miss = new StringBuilder();
            foreach (Vector3 p in mustReach) {
                Vector3Int? l = locate(p);
                if (!l.HasValue || !visited.ContainsKey(key(l.Value.x, l.Value.y, l.Value.z))) miss.Append(" ").Append(p.ToString("F1"));
            }
            if (miss.Length == 0) r.Ok("walkability: all " + mustReach.Length + " must-reach spots (towers, upper floor, container roofs) are reachable");
            else r.Fail("walkability: unreachable target spots:" + miss);
        }

        // Standable cells nobody can walk to, grouped (roofs and prop tops are expected here).
        int total = 0;
        for (int ix = 0; ix < nx; ix++) for (int iz = 0; iz < nz; iz++) total += floors[ix, iz].Count;
        bool[,,] done = new bool[nx, nz, 8];
        List<string> clusters = new List<string>();
        for (int ix = 0; ix < nx; ix++) {
            for (int iz = 0; iz < nz; iz++) {
                for (int k = 0; k < floors[ix, iz].Count && k < 8; k++) {
                    if (done[ix, iz, k] || visited.ContainsKey(key(ix, iz, k))) continue;
                    // flood the cluster of unreached cells at similar height
                    Queue<Vector3Int> cq = new Queue<Vector3Int>();
                    cq.Enqueue(new Vector3Int(ix, iz, k));
                    done[ix, iz, k] = true;
                    int cnt = 0;
                    Vector3 min = new Vector3(1e9f, 1e9f, 1e9f), max = new Vector3(-1e9f, -1e9f, -1e9f);
                    while (cq.Count > 0) {
                        Vector3Int c = cq.Dequeue();
                        cnt++;
                        float y = floors[c.x, c.y][c.z];
                        Vector3 w = new Vector3(area.min.x + (c.x + 0.5f) * cell, y, area.min.z + (c.y + 0.5f) * cell);
                        min = Vector3.Min(min, w);
                        max = Vector3.Max(max, w);
                        for (int dx = -1; dx <= 1; dx++) {
                            for (int dz = -1; dz <= 1; dz++) {
                                int jx = c.x + dx, jz = c.y + dz;
                                if (jx < 0 || jz < 0 || jx >= nx || jz >= nz) continue;
                                for (int kk = 0; kk < floors[jx, jz].Count && kk < 8; kk++) {
                                    if (done[jx, jz, kk] || visited.ContainsKey(key(jx, jz, kk))) continue;
                                    if (Mathf.Abs(floors[jx, jz][kk] - y) > 0.4f) continue;
                                    done[jx, jz, kk] = true;
                                    cq.Enqueue(new Vector3Int(jx, jz, kk));
                                }
                            }
                        }
                    }
                    if (cnt >= 12) clusters.Add((cnt * cell * cell).ToString("F0") + " m2 at y=" + min.y.ToString("F1") + " x " + min.x.ToString("F0") + ".." + max.x.ToString("F0") + " z " + min.z.ToString("F0") + ".." + max.z.ToString("F0"));
                }
            }
        }
        r.Info("walkable cells reached: " + reached + " of " + total + " standable (" + (reached * cell * cell).ToString("F0") + " m2 reachable); unreached clusters >= 3 m2: "
               + (clusters.Count == 0 ? "none" : string.Join(" | ", clusters.Take(24))));
    }

    // ------------------------------------------------------------------ screenshots

    /// <summary>Render the camera into a PNG (Camera.Render into a RenderTexture, ReadPixels, EncodeToPNG).</summary>
    public static void Shoot(Camera cam, string file, int w = 1280, int h = 720) {
        RenderTexture rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
        RenderTexture prev = RenderTexture.active;
        cam.targetTexture = rt;
        cam.aspect = (float)w / h;
        cam.Render();
        RenderTexture.active = rt;
        Texture2D tex = new Texture2D(w, h, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        tex.Apply();
        File.WriteAllBytes(file, tex.EncodeToPNG());
        cam.targetTexture = null;
        RenderTexture.active = prev;
        rt.Release();
        Object.DestroyImmediate(rt);
        Object.DestroyImmediate(tex);
    }

    public static void AddPost(Camera cam, PostProcessProfile profile) {
        PostProcessLayer layer = cam.GetComponent<PostProcessLayer>();
        if (layer == null) layer = cam.gameObject.AddComponent<PostProcessLayer>();
        PostProcessResources res = AssetDatabase.LoadAssetAtPath<PostProcessResources>(
            "Packages/com.unity.postprocessing/PostProcessing/PostProcessResources.asset");
        layer.Init(res);
        layer.volumeTrigger = cam.transform;
        layer.volumeLayer = 1 << LayerMask.NameToLayer("Default");
        layer.antialiasingMode = PostProcessLayer.Antialiasing.FastApproximateAntialiasing;
        layer.fastApproximateAntialiasing.fastMode = true;
        cam.allowHDR = true;
        GameObject volGo = new GameObject("Post Process Volume");
        PostProcessVolume vol = volGo.AddComponent<PostProcessVolume>();
        vol.isGlobal = true;
        vol.sharedProfile = profile;
        vol.priority = 1;
    }

}
