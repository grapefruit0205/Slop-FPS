using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Small kit for building a map from code. Boxes, prisms, ramps, stairs and sandbags are merged per material and
/// surface tag into a few meshes, each with matching colliders on the Shootable layer, so a whole map costs a handful
/// of draw calls. Texture coordinates come from world positions (metres / tile) so neighbouring pieces line up.
/// Lives outside FpsTools.Editor like NetSetup because the builders reference game scripts.
/// </summary>
public static class MapKit {

    public const string ShootableLayer = "Shootable";
    public static readonly string[] SurfaceTags = { "Concrete", "Metal", "Wood", "Dirt", "Brick", "Glass", "Water" };

    // ------------------------------------------------------------------ surfaces

    public class Surf {
        public string Name;
        public Material Mat;
        public float Tile = 2f;          // metres per texture repeat
        public bool Tangents;            // normal mapped, so merged meshes need tangents
        public bool CastShadows = true;
    }

    public static Texture2D Tex(string path) {
        if (string.IsNullOrEmpty(path)) return null;
        Texture2D t = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        if (t == null) Debug.LogWarning("[MapKit] Missing texture " + path);
        return t;
    }

    /// <summary>Standard-shader material from the usual map set. Reuses the asset at matDir/name.mat so GUIDs stay stable.</summary>
    public static Surf MakeSurf(string matDir, string name, float tile, Color tint, string albedo, string normal = null,
                                string ms = null, string ao = null, float smoothness = 0.15f, float bump = 1f) {
        Material m = LoadOrCreate(matDir + "/" + name + ".mat");
        m.SetColor("_Color", tint);
        m.SetTexture("_MainTex", Tex(albedo));
        m.SetTexture("_BumpMap", Tex(normal));
        m.SetFloat("_BumpScale", bump);
        SetKeyword(m, "_NORMALMAP", normal != null);
        m.SetTexture("_MetallicGlossMap", Tex(ms));
        SetKeyword(m, "_METALLICGLOSSMAP", ms != null);
        m.SetFloat("_GlossMapScale", 0.9f);
        m.SetFloat("_Glossiness", smoothness);
        m.SetFloat("_Metallic", 0f);
        m.SetTexture("_OcclusionMap", Tex(ao));
        m.SetFloat("_OcclusionStrength", 0.8f);
        EditorUtility.SetDirty(m);
        return new Surf { Name = name, Mat = m, Tile = tile, Tangents = normal != null };
    }

    /// <summary>Alpha-tested variant (chain-link fence): albedo alpha is the cut-out mask.</summary>
    public static Surf MakeCutout(string matDir, string name, float tile, Color tint, string albedo, string normal, float cutoff) {
        Surf s = MakeSurf(matDir, name, tile, tint, albedo, normal, null, null, 0.5f);
        Material m = s.Mat;
        m.SetFloat("_Mode", 1);
        m.SetFloat("_Cutoff", cutoff);
        m.SetInt("_SrcBlend", (int)BlendMode.One);
        m.SetInt("_DstBlend", (int)BlendMode.Zero);
        m.SetInt("_ZWrite", 1);
        m.EnableKeyword("_ALPHATEST_ON");
        m.DisableKeyword("_ALPHABLEND_ON");
        m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        m.renderQueue = (int)RenderQueue.AlphaTest;
        m.SetFloat("_Metallic", 0.6f);
        s.CastShadows = false;
        EditorUtility.SetDirty(m);
        return s;
    }

    /// <summary>Second albedo layer multiplied in at a different scale: breaks up tiling on big flat areas.</summary>
    public static void AddDetail(Surf s, string detail, float repeatsPerTile) {
        s.Mat.SetTexture("_DetailAlbedoMap", Tex(detail));
        s.Mat.SetTextureScale("_DetailAlbedoMap", new Vector2(repeatsPerTile, repeatsPerTile));
        s.Mat.SetFloat("_UVSec", 0f);
        s.Mat.EnableKeyword("_DETAIL_MULX2");
        EditorUtility.SetDirty(s.Mat);
    }

    public static Material LoadOrCreate(string path) {
        Material m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null) {
            m = new Material(Shader.Find("Standard"));
            AssetDatabase.CreateAsset(m, path);
        }
        m.shader = Shader.Find("Standard");
        return m;
    }

    private static void SetKeyword(Material m, string keyword, bool on) {
        if (on) m.EnableKeyword(keyword); else m.DisableKeyword(keyword);
    }

    /// <summary>Import settings by file name suffix: _Normal, _MetallicSmoothness, _AO are data, everything else colour.</summary>
    public static void ConfigureTextures(string folder, int maxSize = 1024) {
        foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { folder })) {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            TextureImporter ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null) continue;
            bool data = path.Contains("_MetallicSmoothness") || path.Contains("_AO.");
            bool normal = path.Contains("_Normal.");
            bool changed = false;
            if (normal && ti.textureType != TextureImporterType.NormalMap) { ti.textureType = TextureImporterType.NormalMap; changed = true; }
            if (data && ti.sRGBTexture) { ti.sRGBTexture = false; changed = true; }
            if (ti.maxTextureSize != maxSize) { ti.maxTextureSize = maxSize; changed = true; }
            if (ti.anisoLevel < 4) { ti.anisoLevel = 4; changed = true; }
            if (ti.mipmapEnabled == false) { ti.mipmapEnabled = true; changed = true; }
            if (path.EndsWith("_Albedo.png") && !ti.alphaIsTransparency) { ti.alphaIsTransparency = true; changed = true; }
            if (path.Contains("Fence") && path.EndsWith("_Albedo.png") && !ti.mipMapsPreserveCoverage) {
                ti.mipMapsPreserveCoverage = true;
                ti.alphaTestReferenceValue = 0.5f;
                changed = true;
            }
            if (changed) ti.SaveAndReimport();
        }
    }

    /// <summary>Models are used for geometry only: no cameras, lights, animation or embedded materials.</summary>
    public static void ConfigureModel(string path) {
        ModelImporter mi = AssetImporter.GetAtPath(path) as ModelImporter;
        if (mi == null) return;
        bool changed = false;
        if (mi.materialImportMode != ModelImporterMaterialImportMode.None) { mi.materialImportMode = ModelImporterMaterialImportMode.None; changed = true; }
        if (mi.animationType != ModelImporterAnimationType.None) { mi.animationType = ModelImporterAnimationType.None; changed = true; }
        if (mi.importCameras || mi.importLights) { mi.importCameras = false; mi.importLights = false; changed = true; }
        if (changed) mi.SaveAndReimport();
    }

    // ------------------------------------------------------------------ mesh assets

    /// <summary>
    /// One .asset file holding every generated mesh as a sub-asset. Meshes are reused by name on a rebuild so the
    /// file and the prefab that points into it keep stable ids; meshes nobody asked for any more are removed.
    /// </summary>
    public class MeshStore {
        private readonly string path;
        private readonly Dictionary<string, Mesh> existing = new Dictionary<string, Mesh>();
        private readonly List<Mesh> fresh = new List<Mesh>();
        private readonly HashSet<string> used = new HashSet<string>();
        private Mesh root;

        public MeshStore(string assetPath) {
            path = assetPath;
            if (!System.IO.File.Exists(path)) return;
            foreach (UnityEngine.Object o in AssetDatabase.LoadAllAssetsAtPath(path)) {
                Mesh m = o as Mesh;
                if (m == null) continue;
                existing[m.name] = m;
                if (m.name == "_root") root = m;
            }
        }

        public Mesh Get(string name) {
            used.Add(name);
            if (existing.TryGetValue(name, out Mesh m)) {
                m.Clear();
                return m;
            }
            m = new Mesh { name = name };
            existing[name] = m;
            fresh.Add(m);
            return m;
        }

        public void Save(bool prune = true) {
            used.Add("_root");
            if (root == null) {
                root = new Mesh { name = "_root" };
                existing["_root"] = root;
                if (System.IO.File.Exists(path)) AssetDatabase.AddObjectToAsset(root, path);
                else AssetDatabase.CreateAsset(root, path);
            }
            foreach (Mesh m in fresh) AssetDatabase.AddObjectToAsset(m, path);
            fresh.Clear();
            foreach (string name in existing.Keys.ToList()) {
                if (!prune || used.Contains(name)) continue;
                UnityEngine.Object.DestroyImmediate(existing[name], true);
                existing.Remove(name);
            }
            AssetDatabase.SaveAssets();
        }
    }

    // ------------------------------------------------------------------ geometry batcher

    private class Col {
        public Vector3 Center, Size;
        public float Yaw;
    }

    private class Batch {
        public Surf S;
        public string Tag;
        public int Index;
        public readonly List<Vector3> V = new List<Vector3>();
        public readonly List<Vector3> N = new List<Vector3>();
        public readonly List<Vector2> UV = new List<Vector2>();
        public readonly List<int> T = new List<int>();
        public readonly List<Col> Cols = new List<Col>();
    }

    private class Placement {
        public string Name;
        public Vector3 Pos;
        public float Yaw, Scale;
    }

    /// <summary>
    /// Collects geometry for one group of the map (a building, a lane...). Set <see cref="Flip"/> (or use
    /// <see cref="Sym"/>) to build the same thing rotated 180 degrees about the map centre: (x,z) becomes (-x,-z),
    /// which is how the map stays fair for two teams on opposite sides.
    /// </summary>
    public class Batcher {
        public readonly Transform Root;
        public readonly string Name;
        public bool Flip;
        public readonly bool Decor;                 // visual only: no colliders, not Shootable
        public Func<string, GameObject> PropLoader;
        public int Boxes, Tris;

        private readonly MeshStore store;
        private readonly Dictionary<string, Batch> open = new Dictionary<string, Batch>();
        private readonly List<Batch> all = new List<Batch>();
        private readonly List<Mesh> colliderMeshes = new List<Mesh>();
        private readonly List<KeyValuePair<Mesh, string>> meshCols = new List<KeyValuePair<Mesh, string>>();
        private readonly List<Placement> props = new List<Placement>();
        private System.Random rng;

        public Batcher(Transform parent, string name, MeshStore store, bool decor = false) {
            Root = new GameObject(name).transform;
            Root.SetParent(parent, false);
            Name = name;
            this.store = store;
            Decor = decor;
            int seed = 17;
            foreach (char ch in name) seed = seed * 31 + ch;     // string.GetHashCode is not stable across runtimes
            rng = new System.Random(seed);
        }

        /// <summary>Run a build step twice: as written, then rotated 180 degrees about the centre.</summary>
        public void Sym(Action build) {
            Flip = false;
            build();
            Flip = true;
            build();
            Flip = false;
        }

        private Vector3 P(Vector3 p) { return Flip ? new Vector3(-p.x, p.y, -p.z) : p; }
        private float Yaw(float y) { return Flip ? y + 180f : y; }

        // ---- raw polygons

        private Batch Get(Surf s, string tag) {
            string key = s.Name + "|" + tag;
            if (!open.TryGetValue(key, out Batch b) || b.V.Count > 60000) {
                b = new Batch { S = s, Tag = tag, Index = all.Count(x => x.S == s && x.Tag == tag) };
                open[key] = b;
                all.Add(b);
            }
            return b;
        }

        private static void Tangents(Vector3 n, Vector3 hint, out Vector3 tu, out Vector3 tv) {
            if (hint != Vector3.zero) tu = (hint - n * Vector3.Dot(hint, n)).normalized;
            else if (Mathf.Abs(n.y) > 0.98f) tu = Vector3.right;
            else tu = Vector3.Cross(n, Vector3.up).normalized;
            tv = Vector3.Cross(tu, n);
        }

        /// <summary>One quad; corners ordered so that cross(p1 - p0, p3 - p0) is the outward normal (Unity front face: cross(B - A, C - A) along the normal).</summary>
        public void Quad(Surf s, string tag, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 uHint = default(Vector3)) {
            Vector3 n = Vector3.Cross(p1 - p0, p3 - p0);
            if (n.sqrMagnitude < 1e-10f) return;
            n.Normalize();
            Batch b = Get(s, tag);
            Tangents(n, uHint, out Vector3 tu, out Vector3 tv);
            int i = b.V.Count;
            Vector3[] ps = { p0, p1, p2, p3 };
            foreach (Vector3 p in ps) {
                b.V.Add(p);
                b.N.Add(n);
                b.UV.Add(new Vector2(Vector3.Dot(p, tu) / s.Tile, Vector3.Dot(p, tv) / s.Tile));
            }
            b.T.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
            Tris += 2;
        }

        public void Tri(Surf s, string tag, Vector3 p0, Vector3 p1, Vector3 p2) {
            Vector3 n = Vector3.Cross(p1 - p0, p2 - p0);
            if (n.sqrMagnitude < 1e-10f) return;
            n.Normalize();
            Batch b = Get(s, tag);
            Tangents(n, Vector3.zero, out Vector3 tu, out Vector3 tv);
            int i = b.V.Count;
            foreach (Vector3 p in new[] { p0, p1, p2 }) {
                b.V.Add(p);
                b.N.Add(n);
                b.UV.Add(new Vector2(Vector3.Dot(p, tu) / s.Tile, Vector3.Dot(p, tv) / s.Tile));
            }
            b.T.AddRange(new[] { i, i + 1, i + 2 });
            Tris++;
        }

        /// <summary>Heightfield patch with smooth normals (hills): nx * nz cells of `step` metres from (x0, z0); cells whose centre `skip` accepts are left out.</summary>
        public void Terrain(Surf s, string tag, float x0, float z0, float step, int nx, int nz, Func<float, float, float> height, Func<float, float, bool> skip = null) {
            float e = step * 0.5f;
            Func<float, float, Vector3> nrm = (x, z) => new Vector3(height(x - e, z) - height(x + e, z), 2f * e, height(x, z - e) - height(x, z + e)).normalized;
            for (int i = 0; i < nx; i++) {
                for (int j = 0; j < nz; j++) {
                    float xa = x0 + i * step, xb = xa + step, za = z0 + j * step, zb = za + step;
                    if (skip != null && skip(xa + e, za + e)) continue;
                    Batch b = Get(s, tag);
                    int v = b.V.Count;
                    foreach (Vector2 c in new[] { new Vector2(xa, za), new Vector2(xa, zb), new Vector2(xb, zb), new Vector2(xb, za) }) {
                        b.V.Add(new Vector3(c.x, height(c.x, c.y), c.y));
                        b.N.Add(nrm(c.x, c.y));
                        b.UV.Add(new Vector2(c.x, c.y) / s.Tile);
                    }
                    b.T.AddRange(new[] { v, v + 1, v + 2, v, v + 2, v + 3 });
                    Tris += 2;
                }
            }
        }

        // ---- boxes

        private void Face(Surf s, string tag, Vector3 c, Vector3 a, Vector3 b, float ha, float hb, Vector3 hint) {
            Quad(s, tag, c - a * ha - b * hb, c + a * ha - b * hb, c + a * ha + b * hb, c - a * ha + b * hb, hint);
        }

        /// <summary>
        /// Box by centre and size, rotated by yaw degrees. The bottom face is skipped when it rests on the ground.
        /// <paramref name="top"/> puts a different material on the top face (dirt on a sandbag bastion).
        /// </summary>
        public void Box(Surf s, string tag, Vector3 center, Vector3 size, float yaw = 0f, Surf top = null, bool collide = true, Surf bottom = null) {
            center = P(center);
            yaw = Yaw(yaw);
            Quaternion q = Quaternion.Euler(0f, yaw, 0f);
            Vector3 ex = q * Vector3.right, ey = Vector3.up, ez = q * Vector3.forward;
            Vector3 h = size * 0.5f;
            Face(s, tag, center + ex * h.x, ey, ez, h.y, h.z, Vector3.zero);
            Face(s, tag, center - ex * h.x, ez, ey, h.z, h.y, Vector3.zero);
            Face(top ?? s, tag, center + ey * h.y, ez, ex, h.z, h.x, ex);
            if (center.y - h.y > 0.03f) Face(bottom ?? s, tag, center - ey * h.y, ex, ez, h.x, h.z, ex);
            Face(s, tag, center + ez * h.z, ex, ey, h.x, h.y, Vector3.zero);
            Face(s, tag, center - ez * h.z, ey, ex, h.y, h.x, Vector3.zero);
            Boxes++;
            if (collide && !Decor) Get(s, tag).Cols.Add(new Col { Center = center, Size = size, Yaw = yaw });
        }

        /// <summary>Box by min/max corners (axis aligned).</summary>
        public void Slab(Surf s, string tag, float x0, float x1, float y0, float y1, float z0, float z1, Surf top = null, bool collide = true, Surf bottom = null) {
            Box(s, tag, new Vector3((x0 + x1) * 0.5f, (y0 + y1) * 0.5f, (z0 + z1) * 0.5f),
                new Vector3(Mathf.Abs(x1 - x0), Mathf.Abs(y1 - y0), Mathf.Abs(z1 - z0)), 0f, top, collide, bottom);
        }

        /// <summary>A visual-only box that is allowed to have no collider (bars and stencils a few centimetres proud of a solid).</summary>
        public void Detail(Surf s, string tag, Vector3 center, Vector3 size, float yaw = 0f) {
            Box(s, tag, center, size, yaw, null, false);
        }

        // ---- walls with openings

        public struct Opening {
            public float Center, Width, Sill, Head;   // along the wall, and heights (absolute y)
            public Opening(float center, float width, float sill, float head) { Center = center; Width = width; Sill = sill; Head = head; }
        }

        /// <summary>Wall along X (fixed z) from x0 to x1, thickness t, with openings cut at the given x centres.</summary>
        public void WallX(Surf s, string tag, float x0, float x1, float z, float t, float y0, float y1, params Opening[] ops) {
            WallAlong(s, tag, true, x0, x1, z, t, y0, y1, ops);
        }

        /// <summary>Wall along Z (fixed x) from z0 to z1, thickness t, with openings cut at the given z centres.</summary>
        public void WallZ(Surf s, string tag, float z0, float z1, float x, float t, float y0, float y1, params Opening[] ops) {
            WallAlong(s, tag, false, z0, z1, x, t, y0, y1, ops);
        }

        private void WallAlong(Surf s, string tag, bool alongX, float a0, float a1, float fix, float t, float y0, float y1, Opening[] ops) {
            Opening[] sorted = ops.OrderBy(o => o.Center).ToArray();
            float cursor = Mathf.Min(a0, a1);
            float end = Mathf.Max(a0, a1);
            Action<float, float, float, float> piece = (u0, u1, ya, yb) => {
                if (u1 - u0 < 0.01f || yb - ya < 0.01f) return;
                if (alongX) Slab(s, tag, u0, u1, ya, yb, fix - t * 0.5f, fix + t * 0.5f);
                else Slab(s, tag, fix - t * 0.5f, fix + t * 0.5f, ya, yb, u0, u1);
            };
            foreach (Opening o in sorted) {
                float l = o.Center - o.Width * 0.5f, r = o.Center + o.Width * 0.5f;
                piece(cursor, l, y0, y1);                    // solid up to the opening
                piece(l, r, y0, o.Sill);                     // below a window
                piece(l, r, o.Head, y1);                     // lintel
                cursor = r;
            }
            piece(cursor, end, y0, y1);
        }

        // ---- prisms, ramps, stairs

        /// <summary>
        /// Convex prism: a counter-clockwise profile in the local XY plane (x right, y up), extruded along local Z from
        /// z0 to z1, placed at origin and rotated by yaw. Gable roofs, ramps and tarp arches are prisms.
        /// </summary>
        public void Prism(Surf s, string tag, Vector2[] profile, Vector3 origin, float yaw, float z0, float z1, bool collide = true) {
            origin = P(origin);
            yaw = Yaw(yaw);
            Quaternion q = Quaternion.Euler(0f, yaw, 0f);
            int n = profile.Length;
            Vector3[] a0 = new Vector3[n], a1 = new Vector3[n];
            for (int i = 0; i < n; i++) {
                a0[i] = origin + q * new Vector3(profile[i].x, profile[i].y, z0);
                a1[i] = origin + q * new Vector3(profile[i].x, profile[i].y, z1);
            }
            for (int i = 1; i < n - 1; i++) {
                Tri(s, tag, a1[0], a1[i], a1[i + 1]);          // front cap
                Tri(s, tag, a0[0], a0[i + 1], a0[i]);          // back cap
            }
            for (int i = 0; i < n; i++) {
                int j = (i + 1) % n;
                Quad(s, tag, a0[i], a0[j], a1[j], a1[i]);
            }
            if (!collide || Decor) return;
            // Convex mesh collider in world space (its GameObject sits at the origin).
            List<Vector3> v = new List<Vector3>();
            List<int> t = new List<int>();
            v.AddRange(a0);
            v.AddRange(a1);
            for (int i = 1; i < n - 1; i++) {
                t.AddRange(new[] { n, n + i + 1, n + i });
                t.AddRange(new[] { 0, i, i + 1 });
            }
            for (int i = 0; i < n; i++) {
                int j = (i + 1) % n;
                t.AddRange(new[] { i, n + j, j });
                t.AddRange(new[] { i, n + i, n + j });
            }
            Mesh m = store.Get(Name + "_col_" + colliderMeshes.Count);
            m.SetVertices(v);
            m.SetTriangles(t, 0);
            m.RecalculateBounds();
            colliderMeshes.Add(m);
            meshCols.Add(new KeyValuePair<Mesh, string>(m, tag));
        }

        /// <summary>Solid ramp: foot is the bottom-front centre, it climbs `rise` over `run` towards compass yaw (0 = +z).</summary>
        public void Ramp(Surf s, string tag, Vector3 foot, float compassYaw, float run, float width, float rise) {
            Prism(s, tag, new[] { new Vector2(0, 0), new Vector2(run, 0), new Vector2(run, rise) }, foot, compassYaw - 90f, -width * 0.5f, width * 0.5f);
        }

        /// <summary>Solid stairs climbing towards compass yaw (0 = +z) from the foot (centre of the first riser, at the floor).</summary>
        public void Stairs(Surf s, string tag, Vector3 foot, float compassYaw, int steps, float rise, float run, float width) {
            Quaternion q = Quaternion.Euler(0f, compassYaw, 0f);
            for (int i = 0; i < steps; i++) {
                float h = rise * (i + 1);
                Vector3 c = foot + q * new Vector3(0f, h * 0.5f, run * (i + 0.5f));
                Box(s, tag, c, new Vector3(width, h, run), compassYaw);
            }
        }

        // ---- sandbags

        private static Mesh bagTemplate;

        private static Mesh BagTemplate() {
            if (bagTemplate != null) return bagTemplate;
            // A cube, 2 quads per face, pulled halfway towards a sphere: a plump rounded pillow.
            List<Vector3> v = new List<Vector3>(), n = new List<Vector3>();
            List<int> t = new List<int>();
            Vector3[] axes = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
            foreach (Vector3 nrm in axes) {
                Vector3 a = Vector3.Cross(nrm, Mathf.Abs(nrm.y) > 0.9f ? Vector3.right : Vector3.up).normalized;
                Vector3 b = Vector3.Cross(nrm, a);
                int baseI = v.Count;
                for (int j = 0; j <= 2; j++) {
                    for (int i = 0; i <= 2; i++) {
                        Vector3 p = nrm + a * (i - 1) + b * (j - 1);
                        Vector3 r = Vector3.Lerp(p, p.normalized, 0.42f);
                        v.Add(r * 0.5f);
                        n.Add(Vector3.Lerp(p.normalized, nrm, 0.25f).normalized);
                    }
                }
                for (int j = 0; j < 2; j++) {
                    for (int i = 0; i < 2; i++) {
                        int i0 = baseI + j * 3 + i;
                        // front faces need cross(B - A, C - A) along the outward normal
                        Vector3 e1 = v[i0 + 1] - v[i0], e2 = v[i0 + 3] - v[i0];
                        bool aligned = Vector3.Dot(Vector3.Cross(e1, e2), nrm) > 0f;
                        if (aligned) t.AddRange(new[] { i0, i0 + 1, i0 + 4, i0, i0 + 4, i0 + 3 });
                        else t.AddRange(new[] { i0, i0 + 3, i0 + 4, i0, i0 + 4, i0 + 1 });
                    }
                }
            }
            bagTemplate = new Mesh { name = "bag", hideFlags = HideFlags.HideAndDontSave };
            bagTemplate.SetVertices(v);
            bagTemplate.SetNormals(n);
            bagTemplate.SetTriangles(t, 0);
            return bagTemplate;
        }

        private void AddBag(Surf s, string tag, Vector3 pos, float yaw, Vector3 size) {
            Mesh tpl = BagTemplate();
            Matrix4x4 m = Matrix4x4.TRS(pos, Quaternion.Euler(0f, yaw, 0f), size);
            Batch b = Get(s, tag);
            Vector3[] tv = tpl.vertices;
            Vector3[] tn = tpl.normals;
            int[] tt = tpl.triangles;
            int i0 = b.V.Count;
            for (int i = 0; i < tv.Length; i++) {
                Vector3 w = m.MultiplyPoint3x4(tv[i]);
                Vector3 nn = m.MultiplyVector(tn[i]).normalized;
                b.V.Add(w);
                b.N.Add(nn);
                // box projection: pick the dominant axis of the normal
                Vector3 an = new Vector3(Mathf.Abs(nn.x), Mathf.Abs(nn.y), Mathf.Abs(nn.z));
                Vector2 uv = an.y >= an.x && an.y >= an.z ? new Vector2(w.x, w.z) : an.x >= an.z ? new Vector2(w.z, w.y) : new Vector2(w.x, w.y);
                b.UV.Add(uv / s.Tile);
            }
            foreach (int ti in tt) b.T.Add(i0 + ti);
            Tris += tt.Length / 3;
        }

        /// <summary>
        /// Sandbag wall: `length` bags per course, `courses` high, `depth` bags thick, running bond. The collider is one
        /// slightly smaller box. `start` is the middle of the left end, wall runs along compass yaw (0 = +z).
        /// </summary>
        public void SandbagWall(Surf sandA, Surf sandB, Vector3 start, float compassYaw, int length, int courses, int depth = 2) {
            start = P(start);
            float yaw = Yaw(compassYaw);
            Quaternion q = Quaternion.Euler(0f, yaw, 0f);
            Vector3 dir = q * Vector3.forward, side = q * Vector3.right;
            const float L = 0.5f, H = 0.18f, D = 0.3f;
            for (int c = 0; c < courses; c++) {
                bool odd = (c & 1) == 1;
                int count = odd ? length - 1 : length;
                for (int d = 0; d < depth; d++) {
                    for (int i = 0; i < count; i++) {
                        float along = (odd ? L * 0.5f : 0f) + L * (i + 0.5f);
                        float across = (d - (depth - 1) * 0.5f) * D;
                        Vector3 jitter = new Vector3(Rnd(0.012f), Rnd(0.01f), Rnd(0.012f));
                        Vector3 pos = start + dir * along + side * across + Vector3.up * (H * 0.5f + c * H * 0.93f) + jitter;
                        Surf sf = rng.NextDouble() < 0.7 ? sandA : sandB;
                        AddBag(sf, "Dirt", pos, yaw + 90f + Rnd(4f), new Vector3(L * (1f + Rnd(0.04f)), H * 1.12f, D * (1f + Rnd(0.04f))));
                    }
                }
            }
            if (Decor) return;
            float len = length * L - 0.1f;
            float hgt = courses * H * 0.93f + H * 0.1f;
            float thick = depth * D - 0.04f;
            Vector3 center = start + dir * (length * L * 0.5f) + Vector3.up * (hgt * 0.5f);
            // Colliders are registered on the first sand surface's batch.
            Get(sandA, "Dirt").Cols.Add(new Col { Center = center, Size = new Vector3(thick, hgt, len), Yaw = yaw });
        }

        private float Rnd(float amp) { return ((float)rng.NextDouble() * 2f - 1f) * amp; }

        // ---- props (model prefabs with their own colliders)

        public void Prop(string name, Vector3 pos, float yaw = 0f, float scale = 1f) {
            props.Add(new Placement { Name = name, Pos = P(pos), Yaw = Yaw(yaw), Scale = scale });
        }

        // ---- output

        /// <summary>Create the GameObjects: one per material/tag with the merged mesh and its box colliders.</summary>
        public void Flush(bool isStatic = true) {
            int layer = LayerMask.NameToLayer(ShootableLayer);
            foreach (Batch b in all) {
                if (b.V.Count == 0 && b.Cols.Count == 0) continue;
                string nm = Name + "_" + b.S.Name + "_" + b.Tag + (b.Index > 0 ? "_" + b.Index : "");
                GameObject go = new GameObject(nm);
                go.transform.SetParent(Root, false);
                if (!Decor) {
                    go.layer = layer;
                    go.tag = b.Tag;
                }
                if (b.V.Count > 0) {
                    Mesh m = store.Get(nm);
                    m.indexFormat = b.V.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
                    m.SetVertices(b.V);
                    m.SetNormals(b.N);
                    m.SetUVs(0, b.UV);
                    m.SetTriangles(b.T, 0);
                    m.RecalculateBounds();
                    if (b.S.Tangents) m.RecalculateTangents();
                    go.AddComponent<MeshFilter>().sharedMesh = m;
                    MeshRenderer mr = go.AddComponent<MeshRenderer>();
                    mr.sharedMaterial = b.S.Mat;
                    mr.shadowCastingMode = b.S.CastShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
                }
                foreach (Col c in b.Cols) AddBoxCollider(go, c, layer, b.Tag);
                go.isStatic = isStatic;
            }
            foreach (KeyValuePair<Mesh, string> mc in meshCols) {
                GameObject go = new GameObject(mc.Key.name);
                go.transform.SetParent(Root, false);
                go.layer = layer;
                go.tag = mc.Value;
                MeshCollider col = go.AddComponent<MeshCollider>();
                col.sharedMesh = mc.Key;
                col.convex = true;
                go.isStatic = isStatic;
            }
            if (props.Count > 0 && PropLoader != null) {
                Transform parent = new GameObject("Props").transform;
                parent.SetParent(Root, false);
                parent.gameObject.isStatic = isStatic;
                foreach (Placement p in props) {
                    GameObject prefab = PropLoader(p.Name);
                    if (prefab == null) {
                        Debug.LogWarning("[MapKit] Missing prop " + p.Name);
                        continue;
                    }
                    GameObject inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
                    inst.transform.SetPositionAndRotation(p.Pos, Quaternion.Euler(0f, p.Yaw, 0f));
                    inst.transform.localScale = Vector3.one * p.Scale;
                    SetStaticRecursive(inst, isStatic);
                }
            }
        }

        private static void SetStaticRecursive(GameObject go, bool isStatic) {
            foreach (Transform t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.isStatic = isStatic;
        }

        private static void AddBoxCollider(GameObject go, Col c, int layer, string tag) {
            float yaw = ((c.Yaw % 360f) + 360f) % 360f;
            bool quarter = Mathf.Abs(yaw % 90f) < 0.01f || Mathf.Abs(yaw % 90f - 90f) < 0.01f;
            if (quarter) {
                bool swap = Mathf.Abs(yaw % 180f - 90f) < 0.01f;
                BoxCollider bc = go.AddComponent<BoxCollider>();
                bc.center = c.Center;
                bc.size = swap ? new Vector3(c.Size.z, c.Size.y, c.Size.x) : c.Size;
                return;
            }
            GameObject child = new GameObject("rot_" + Mathf.RoundToInt(yaw));
            child.transform.SetParent(go.transform, false);
            child.transform.SetPositionAndRotation(c.Center, Quaternion.Euler(0f, yaw, 0f));
            child.layer = layer;
            child.tag = tag;
            child.AddComponent<BoxCollider>().size = c.Size;
            child.isStatic = go.isStatic;
        }
    }

    // ------------------------------------------------------------------ prop simplification

    public static int TriCount(Mesh m) {
        int n = 0;
        for (int i = 0; i < m.subMeshCount; i++) n += (int)m.GetIndexCount(i) / 3;
        return n;
    }

    private class Clustered {
        public readonly List<Vector3> V = new List<Vector3>(), N = new List<Vector3>();
        public readonly List<Vector2> UV = new List<Vector2>();
        public readonly List<int> T = new List<int>();
    }

    /// <summary>
    /// Reduce a prop mesh to about `target` triangles by quadric vertex clustering, into one submesh of `dst`. Vertices merge
    /// only when they share a grid cell AND a texture-space cell, so UV seams stay intact; every merged vertex moves to where
    /// the planes around it meet, which keeps box corners sharp. The grid is refined until the budget is just met.
    /// </summary>
    public static void Simplify(Mesh src, int target, Mesh dst) {
        Vector3[] p = src.vertices, n = src.normals;
        Vector2[] uv = src.uv;
        int[] t = src.triangles;
        if (n.Length != p.Length) n = new Vector3[p.Length];
        if (uv.Length != p.Length) uv = new Vector2[p.Length];
        double areaP = 0, areaU = 0;
        for (int i = 0; i < t.Length; i += 3) {
            areaP += Vector3.Cross(p[t[i + 1]] - p[t[i]], p[t[i + 2]] - p[t[i]]).magnitude * 0.5;
            Vector2 a = uv[t[i + 1]] - uv[t[i]], b = uv[t[i + 2]] - uv[t[i]];
            areaU += Mathf.Abs(a.x * b.y - a.y * b.x) * 0.5;
        }
        float uvPerMetre = areaP > 1e-12 && areaU > 1e-12 ? (float)System.Math.Sqrt(areaU / areaP) : 1f;
        Vector3 size = src.bounds.size;
        float maxDim = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
        float lo = maxDim / 400f, hi = maxDim / 2f;
        Clustered best = null;
        for (int it = 0; it < 16; it++) {
            float c = Mathf.Sqrt(lo * hi);
            Clustered r = Cluster(p, n, uv, t, c, uvPerMetre);
            if (r.T.Count / 3 <= target) { hi = c; best = r; } else lo = c;
        }
        if (best == null) best = Cluster(p, n, uv, t, hi, uvPerMetre);
        dst.Clear();
        dst.indexFormat = best.V.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        dst.SetVertices(best.V);
        dst.SetNormals(best.N);
        dst.SetUVs(0, best.UV);
        dst.SetTriangles(best.T, 0);
        dst.RecalculateBounds();
        dst.RecalculateTangents();
    }

    private static Clustered Cluster(Vector3[] p, Vector3[] n, Vector2[] uv, int[] t, float cell, float uvPerMetre) {
        float uvCell = cell * uvPerMetre;
        Dictionary<long, int> cells = new Dictionary<long, int>();
        Dictionary<(int, int, int), int> verts = new Dictionary<(int, int, int), int>();
        List<Vector3> cellSum = new List<Vector3>(), outN = new List<Vector3>();
        List<Vector2> outUV = new List<Vector2>();
        List<int> cellCount = new List<int>(), outCount = new List<int>(), outCell = new List<int>();
        int[] cellOf = new int[p.Length], vertOf = new int[p.Length];
        for (int i = 0; i < p.Length; i++) {
            long ix = Mathf.FloorToInt(p[i].x / cell) + 100000, iy = Mathf.FloorToInt(p[i].y / cell) + 100000, iz = Mathf.FloorToInt(p[i].z / cell) + 100000;
            long key = (ix << 42) | (iy << 21) | iz;
            if (!cells.TryGetValue(key, out int ci)) {
                ci = cells.Count;
                cells[key] = ci;
                cellSum.Add(Vector3.zero);
                cellCount.Add(0);
            }
            cellOf[i] = ci;
            cellSum[ci] += p[i];
            cellCount[ci]++;
            (int, int, int) vk = (ci, Mathf.FloorToInt(uv[i].x / uvCell), Mathf.FloorToInt(uv[i].y / uvCell));
            if (!verts.TryGetValue(vk, out int vi)) {
                vi = outN.Count;
                verts[vk] = vi;
                outN.Add(Vector3.zero);
                outUV.Add(Vector2.zero);
                outCount.Add(0);
                outCell.Add(ci);
            }
            vertOf[i] = vi;
            outN[vi] += n[i];
            outUV[vi] += uv[i];
            outCount[vi]++;
        }

        // Plane quadrics per grid cell (area weighted): A = sum w n n^T, b = sum w d n.
        int nc = cells.Count;
        double[] A = new double[nc * 6], B = new double[nc * 3];
        for (int i = 0; i < t.Length; i += 3) {
            Vector3 cr = Vector3.Cross(p[t[i + 1]] - p[t[i]], p[t[i + 2]] - p[t[i]]);
            float area = cr.magnitude * 0.5f;
            if (area < 1e-10f) continue;
            Vector3 nn = cr.normalized;
            double d = -Vector3.Dot(nn, p[t[i]]);
            for (int k = 0; k < 3; k++) {
                int c = cellOf[t[i + k]];
                A[c * 6] += area * nn.x * nn.x; A[c * 6 + 1] += area * nn.x * nn.y; A[c * 6 + 2] += area * nn.x * nn.z;
                A[c * 6 + 3] += area * nn.y * nn.y; A[c * 6 + 4] += area * nn.y * nn.z; A[c * 6 + 5] += area * nn.z * nn.z;
                B[c * 3] += area * d * nn.x; B[c * 3 + 1] += area * d * nn.y; B[c * 3 + 2] += area * d * nn.z;
            }
        }
        Vector3[] pos = new Vector3[nc];
        for (int c = 0; c < nc; c++) {
            Vector3 mean = cellSum[c] / cellCount[c];
            double sc = (A[c * 6] + A[c * 6 + 3] + A[c * 6 + 5]) / 3.0 + 1e-12, lambda = 0.05;     // normalised, so the pull to the mean is relative
            double a00 = A[c * 6] / sc + lambda, a01 = A[c * 6 + 1] / sc, a02 = A[c * 6 + 2] / sc, a11 = A[c * 6 + 3] / sc + lambda, a12 = A[c * 6 + 4] / sc, a22 = A[c * 6 + 5] / sc + lambda;
            double r0 = -B[c * 3] / sc + lambda * mean.x, r1 = -B[c * 3 + 1] / sc + lambda * mean.y, r2 = -B[c * 3 + 2] / sc + lambda * mean.z;
            double det = a00 * (a11 * a22 - a12 * a12) - a01 * (a01 * a22 - a12 * a02) + a02 * (a01 * a12 - a11 * a02);
            Vector3 x = mean;
            if (System.Math.Abs(det) > 1e-6) {
                x = new Vector3(
                    (float)((r0 * (a11 * a22 - a12 * a12) - a01 * (r1 * a22 - a12 * r2) + a02 * (r1 * a12 - a11 * r2)) / det),
                    (float)((a00 * (r1 * a22 - a12 * r2) - r0 * (a01 * a22 - a12 * a02) + a02 * (a01 * r2 - r1 * a02)) / det),
                    (float)((a00 * (a11 * r2 - r1 * a12) - a01 * (a01 * r2 - r1 * a02) + r0 * (a01 * a12 - a11 * a02)) / det));
            }
            float lim = cell * 0.75f;
            pos[c] = new Vector3(Mathf.Clamp(x.x, mean.x - lim, mean.x + lim), Mathf.Clamp(x.y, mean.y - lim, mean.y + lim), Mathf.Clamp(x.z, mean.z - lim, mean.z + lim));
        }

        Clustered res = new Clustered();
        int[] remap = new int[outN.Count];
        for (int i = 0; i < remap.Length; i++) remap[i] = -1;
        HashSet<(int, int, int)> seen = new HashSet<(int, int, int)>();
        for (int i = 0; i < t.Length; i += 3) {
            int a = vertOf[t[i]], b = vertOf[t[i + 1]], c = vertOf[t[i + 2]];
            int ca = cellOf[t[i]], cb = cellOf[t[i + 1]], cc = cellOf[t[i + 2]];
            if (ca == cb || cb == cc || ca == cc) continue;
            (int, int, int) tri = a < b && a < c ? (a, b, c) : b < c ? (b, c, a) : (c, a, b);
            if (!seen.Add(tri)) continue;
            Vector3 was = Vector3.Cross(p[t[i + 1]] - p[t[i]], p[t[i + 2]] - p[t[i]]);
            Vector3 now = Vector3.Cross(pos[cb] - pos[ca], pos[cc] - pos[ca]);
            if (Vector3.Dot(was, now) < 0f) continue;                           // would fold over
            foreach (int v in new[] { a, b, c }) {
                if (remap[v] < 0) {
                    remap[v] = res.V.Count;
                    res.V.Add(pos[outCell[v]]);
                    res.N.Add(outN[v].sqrMagnitude > 1e-8f ? outN[v].normalized : Vector3.up);
                    res.UV.Add(outUV[v] / outCount[v]);
                }
                res.T.Add(remap[v]);
            }
        }
        return res;
    }

    public static Transform Group(Transform parent, string name) {
        Transform t = new GameObject(name).transform;
        t.SetParent(parent, false);
        return t;
    }

}
