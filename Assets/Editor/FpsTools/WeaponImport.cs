using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Bakes the CC0 pistol (1911), SMG (MP5), sniper (M700) and shotgun models into static meshes next to the AK-47, builds one
/// first-person view prefab per weapon, and builds two procedural optics (red dot, 4x scope). Only adds assets; the player
/// prefabs are not touched.
///   Unity -batchmode -quit -projectPath . -executeMethod WeaponImport.Apply
///   Unity -batchmode -quit -projectPath . -executeMethod WeaponImport.Preview -previewDir /some/dir
/// Safe to re-run: existing meshes, materials and prefabs are overwritten in place so their GUIDs (and any references) stay.
/// </summary>
public static class WeaponImport {

    private const string Root = "Assets/Art/Military/Weapons/";
    private const string OpticsRoot = Root + "Optics/";
    private const int FpsGunLayer = 9;

    // The AK in FpsGun_AK47 is "AK47 Model": scale 171.42331, muzzle tip (mesh origin) at (0, 0.11, 0.8521187) in gun space,
    // +Z toward the muzzle, +Y up. A real AK-47 is 0.88 m, which fixes how many gun-space units one metre is.
    private const float AkModelScale = 171.42331f;
    private const float AkRealLength = 0.88f;
    private static readonly Vector3 Anchor = new Vector3(0f, 0.11f, 0.8521187f);
    // Where an optic sits on the AK, in the AK mesh's own space (metres, muzzle tip at the origin, +Z toward the muzzle).
    private static readonly Vector3 AkRail = new Vector3(0f, 0.024f, -0.47f);

    private class Spec {
        public string Name;
        public string Fbx;                 // file name inside the weapon folder
        public float Length;               // length in metres the baked mesh is fitted to
        public bool Pbr = true;            // false: albedo only (Shotgun)
        public bool FlipForward, FlipUp;   // corrections when the automatic orientation guess is wrong
        public int SideAxis = -1;          // force the model axis that is the gun's width (0/1/2)
        public float PitchFix;             // degrees, tilts the muzzle down (positive) when the model is authored slightly nose-up
        public Vector3 BoreShift;          // metres, moves the origin from the muzzle slice centre onto the barrel axis
        public Vector3 Grip;               // right hand, metres, mesh space (muzzle at origin, +Z toward the muzzle, +Y up)
        public Vector3 Handguard;          // left hand, same space
        public Vector3 Rail;               // where an optic's Mount sits (top of the rail / receiver), same space
        public bool TwoHanded = true;
        public float Metallic, Smoothness; // used when Pbr is false
    }

    private static readonly Spec[] Specs = {
        new Spec { Name = "Pistol", Fbx = "Pistol.fbx", Length = 0.2f, TwoHanded = false,
                   Grip = new Vector3(0f, -0.06f, -0.16f), Rail = new Vector3(0f, 0.02f, -0.088f) },
        new Spec { Name = "SMG", Fbx = "SMG.fbx", Length = 0.7f, BoreShift = new Vector3(0f, -0.028f, 0f),
                   Grip = new Vector3(0f, -0.115f, -0.405f), Handguard = new Vector3(0f, -0.025f, -0.145f),
                   Rail = new Vector3(0f, 0.06f, -0.279f) },
        new Spec { Name = "Sniper", Fbx = "Sniper.fbx", Length = 1.15f,
                   Grip = new Vector3(0f, -0.072f, -0.84f), Handguard = new Vector3(0f, -0.04f, -0.4f),
                   Rail = new Vector3(0f, 0.023f, -0.647f) },
        new Spec { Name = "Shotgun", Fbx = "Shotgun.fbx", Length = 1.0f, Pbr = false, Metallic = 0.15f, Smoothness = 0.3f,
                   PitchFix = 3f, BoreShift = new Vector3(0f, 0.0152f, 0f),
                   Grip = new Vector3(0f, -0.06f, -0.72f), Handguard = new Vector3(0f, -0.05f, -0.38f),
                   Rail = new Vector3(0f, 0.012f, -0.54f) },
    };

    // Optics are authored in metres with the Mount (base of the sight, sits on the rail) at the origin, +Z toward the target.
    private const float DotAxisHeight = 0.034f;

    [MenuItem("Tools/FPS/Import Weapons")]
    public static void Apply() {
        AssetDatabase.Refresh();
        float k = GunUnitsPerMetre();
        foreach (Spec s in Specs) {
            ConfigureTextures(s);
            ConfigureModel(s);
            Mesh mesh = BakeStaticMesh(s);
            Material mat = BuildMaterial(s);
            BuildViewPrefab(s, mesh, mat, k);
        }
        BuildOptics(k);
        AssetDatabase.SaveAssets();
        Debug.Log("[WeaponImport] Applied. 1 m = " + k.ToString("F4") + " gun-space units");
    }

    /// <summary>Import and render the preview in one editor session (batch mode runs a single method).</summary>
    public static void ApplyAndPreview() {
        Apply();
        Preview();
    }

    private static string Dir(Spec s) {
        return Root + s.Name + "/";
    }

    /// <summary>Gun-space units per real metre, derived from the AK's baked mesh and its model scale.</summary>
    private static float GunUnitsPerMetre() {
        Mesh ak = AssetDatabase.LoadAssetAtPath<Mesh>(Root + "AK47/AK47_Static.asset");
        float length = ak != null ? ak.bounds.size.z : 0.0103056672f;
        return length * AkModelScale / AkRealLength;
    }

    // ------------------------------------------------------------------ import settings

    private static void ConfigureTextures(Spec s) {
        foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { Dir(s).TrimEnd('/') })) {
            string assetPath = AssetDatabase.GUIDToAssetPath(guid);
            TextureImporter ti = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (ti == null) continue;
            bool linearMap = assetPath.Contains("_MetallicSmoothness") || assetPath.Contains("_AO");
            bool normal = assetPath.Contains("_Normal");
            if (normal && ti.textureType != TextureImporterType.NormalMap) ti.textureType = TextureImporterType.NormalMap;
            if (linearMap) ti.sRGBTexture = false;
            ti.maxTextureSize = 1024;
            ti.anisoLevel = 4;
            ti.SaveAndReimport();
        }
    }

    /// <summary>Keep the meshes readable so the bake below can combine them.</summary>
    private static void ConfigureModel(Spec s) {
        ModelImporter mi = AssetImporter.GetAtPath(Dir(s) + s.Fbx) as ModelImporter;
        if (mi == null) {
            Debug.LogError("[WeaponImport] missing model " + Dir(s) + s.Fbx);
            return;
        }
        if (!mi.isReadable) {
            mi.isReadable = true;
            mi.SaveAndReimport();
        }
    }

    // ------------------------------------------------------------------ bake

    /// <summary>
    /// Bakes the model at rest into one static mesh the way WeaponSwap does for the AK: +Z toward the muzzle, +Y up, muzzle tip
    /// (bore centre) at the origin. Skinned meshes come out at the wrong size (bone scale), so the result is fitted to the
    /// weapon's length in metres instead.
    /// </summary>
    private static Mesh BakeStaticMesh(Spec s) {
        GameObject model = (GameObject)Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Dir(s) + s.Fbx));
        model.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        model.transform.localScale = Vector3.one;

        var combine = new List<CombineInstance>();
        int verts = 0;
        foreach (SkinnedMeshRenderer r in model.GetComponentsInChildren<SkinnedMeshRenderer>(true)) {
            Mesh baked = new Mesh();
            r.BakeMesh(baked, true);
            combine.Add(new CombineInstance { mesh = baked, transform = Matrix4x4.TRS(r.transform.position, r.transform.rotation, Vector3.one) });
            verts += baked.vertexCount;
        }
        foreach (MeshFilter mf in model.GetComponentsInChildren<MeshFilter>(true)) {
            if (mf.sharedMesh == null) continue;
            combine.Add(new CombineInstance { mesh = mf.sharedMesh, transform = mf.transform.localToWorldMatrix });
            verts += mf.sharedMesh.vertexCount;
        }
        Mesh world = new Mesh { indexFormat = verts > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
        world.CombineMeshes(combine.ToArray(), true, true);
        Object.DestroyImmediate(model);

        Vector3[] v = world.vertices;
        Bounds b = world.bounds;
        // Long axis = barrel; the narrower end is the muzzle; the barrel sits above the magazine.
        int longAxis = MaxAxis(b.size);
        int sideAxis = s.SideAxis >= 0 && s.SideAxis != longAxis ? s.SideAxis : MinAxis(b.size, longAxis);
        int upAxis = 3 - longAxis - sideAxis;
        float lo = b.min[longAxis], hi = b.max[longAxis], slice = (hi - lo) * 0.06f;
        Bounds endLo = Slice(v, longAxis, lo, lo + slice), endHi = Slice(v, longAxis, hi - slice, hi);
        bool muzzleHigh = endHi.size[upAxis] < endLo.size[upAxis];
        if (s.FlipForward) muzzleHigh = !muzzleHigh;
        Bounds muzzle = muzzleHigh ? endHi : endLo;
        Vector3 forward = Axis(longAxis) * (muzzleHigh ? 1 : -1);
        Vector3 up = Axis(upAxis) * Mathf.Sign(muzzle.center[upAxis] - b.center[upAxis]) * (s.FlipUp ? -1 : 1);
        Quaternion toGun = Quaternion.Inverse(Quaternion.LookRotation(forward, up));
        Quaternion pitch = Quaternion.Euler(s.PitchFix, 0f, 0f);
        Vector3 tip = muzzle.center;
        tip[longAxis] = muzzleHigh ? hi : lo;

        Vector3[] n = world.normals;
        Vector4[] t = world.tangents;
        float fit = s.Length / (hi - lo); // the bone-scale fix: fit to the real length
        for (int i = 0; i < v.Length; i++) {
            v[i] = pitch * (toGun * (v[i] - tip)) * fit - s.BoreShift;
            if (n.Length > 0) n[i] = pitch * (toGun * n[i]);
            if (t.Length > 0) {
                Vector3 tt = pitch * (toGun * new Vector3(t[i].x, t[i].y, t[i].z));
                t[i] = new Vector4(tt.x, tt.y, tt.z, t[i].w);
            }
        }
        world.vertices = v;
        if (n.Length > 0) world.normals = n;
        if (t.Length > 0) world.tangents = t; else world.RecalculateTangents();
        world.RecalculateBounds();
        world.name = s.Name + "_Static";
        Debug.Log("[WeaponImport] " + s.Name + " baked " + v.Length + " verts, long=" + longAxis + " up=" + upAxis + " muzzleHigh=" + muzzleHigh +
                  ", fit x" + fit.ToString("G5") + ", bounds (m) " + world.bounds.ToString("F3"));
        return SaveMesh(Dir(s) + s.Name + "_Static.asset", world);
    }

    /// <summary>Writes the mesh over the existing asset (keeps its GUID) or creates it.</summary>
    private static Mesh SaveMesh(string path, Mesh mesh) {
        Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if (existing != null) {
            existing.Clear();
            EditorUtility.CopySerialized(mesh, existing);
            Object.DestroyImmediate(mesh);
            EditorUtility.SetDirty(existing);
            return existing;
        }
        AssetDatabase.CreateAsset(mesh, path);
        return mesh;
    }

    private static Material BuildMaterial(Spec s) {
        Material m = GetMaterial(Dir(s) + s.Name + ".mat", "Standard");
        m.SetTexture("_MainTex", LoadTexture(s, "_Albedo"));
        m.SetColor("_Color", Color.white);
        if (s.Pbr) {
            m.SetTexture("_BumpMap", LoadTexture(s, "_Normal"));
            m.EnableKeyword("_NORMALMAP");
            m.SetTexture("_MetallicGlossMap", LoadTexture(s, "_MetallicSmoothness"));
            m.EnableKeyword("_METALLICGLOSSMAP");
            m.SetFloat("_GlossMapScale", 1f);
            m.SetTexture("_OcclusionMap", LoadTexture(s, "_AO"));
            m.SetFloat("_OcclusionStrength", 1f);
        } else {
            m.SetFloat("_Metallic", s.Metallic);
            m.SetFloat("_Glossiness", s.Smoothness);
        }
        EditorUtility.SetDirty(m);
        return m;
    }

    private static Material GetMaterial(string path, string shader) {
        Material m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null) {
            m = new Material(Shader.Find(shader));
            AssetDatabase.CreateAsset(m, path);
        }
        return m;
    }

    private static Texture2D LoadTexture(Spec s, string suffix) {
        string p = Dir(s) + s.Name + suffix;
        Texture2D t = AssetDatabase.LoadAssetAtPath<Texture2D>(p + ".png");
        return t != null ? t : AssetDatabase.LoadAssetAtPath<Texture2D>(p + ".jpg");
    }

    // ------------------------------------------------------------------ gun prefab

    /// <summary>
    /// "&lt;Name&gt;_View": drop it under the gun root where "AK47 Model" sits. The model node mirrors the AK's (same position, scale
    /// per real metre), Muzzle lands on GunBarrelEnd, and Grip/Handguard/Rail are in the same gun space.
    /// </summary>
    private static void BuildViewPrefab(Spec s, Mesh mesh, Material mat, float k) {
        GameObject root = new GameObject(s.Name + "_View") { layer = FpsGunLayer };
        GameObject model = new GameObject(s.Name + " Model", typeof(MeshFilter), typeof(MeshRenderer)) { layer = FpsGunLayer };
        model.transform.SetParent(root.transform, false);
        model.transform.localPosition = Anchor;
        model.transform.localScale = Vector3.one * k;
        model.GetComponent<MeshFilter>().sharedMesh = mesh;
        model.GetComponent<MeshRenderer>().sharedMaterial = mat;

        AddMarker(root.transform, "Muzzle", Vector3.zero, k);
        AddMarker(root.transform, "Grip", s.Grip, k);
        if (s.TwoHanded) AddMarker(root.transform, "Handguard", s.Handguard, k);
        AddMarker(root.transform, "Rail", s.Rail, k);

        string path = Dir(s) + s.Name + "_View.prefab";
        PrefabUtility.SaveAsPrefabAsset(root, path);
        Object.DestroyImmediate(root);
    }

    /// <summary>Marker at a mesh-space point (metres), expressed in the view root's gun space.</summary>
    private static void AddMarker(Transform parent, string name, Vector3 meshPoint, float k) {
        GameObject go = new GameObject(name) { layer = FpsGunLayer };
        go.transform.SetParent(parent, false);
        go.transform.localPosition = Anchor + meshPoint * k;
    }

    // ------------------------------------------------------------------ optics

    private static void BuildOptics(float k) {
        Directory.CreateDirectory(Abs(OpticsRoot + "RedDot"));
        Directory.CreateDirectory(Abs(OpticsRoot + "Scope4x"));
        AssetDatabase.Refresh();
        Mesh disc = SaveMesh(OpticsRoot + "Optic_Disc.asset", DiscMesh(24));

        // red dot: tube sight (Aimpoint style), 1x; the dot floats on the objective lens
        {
            MeshBuilder mb = new MeshBuilder();
            AddTubeSight(mb, -0.052f, 0.052f, 0.0185f, 0.0165f, 0.0205f, 0.014f, 0.016f, 0.07f);
            Mesh body = SaveMesh(OpticsRoot + "RedDot/RedDot_Static.asset", mb.ToMesh("RedDot_Static"));
            Texture2D dot = DotTexture(OpticsRoot + "RedDot/RedDot_Dot.png");
            Material metal = OpticMetal(OpticsRoot + "RedDot/RedDot.mat");
            Material lens = Transparent(OpticsRoot + "RedDot/RedDot_Lens.mat", new Color(0.55f, 0.85f, 0.9f, 0.16f), null);
            Material dotMat = Transparent(OpticsRoot + "RedDot/RedDot_Dot.mat", new Color(1f, 0.1f, 0.06f, 1f), dot);
            OpticPrefab("RedDot", body, metal, disc, k, 0.0296f, new[] {
                new Lens { Name = "Lens", Material = lens, Z = 0.03f, Radius = 0.0158f },
                new Lens { Name = "Dot", Material = dotMat, Z = 0.0296f, Radius = 0.003f },
            });
        }
        // 4x scope: ACOG style, a lens at each end
        {
            MeshBuilder mb = new MeshBuilder();
            AddTubeSight(mb, -0.075f, 0.075f, 0.0195f, 0.0175f, 0.0245f, 0.0135f, 0.0215f, 0.1f);
            mb.Cylinder(new Vector3(0f, DotAxisHeight + 0.0175f + 0.004f, 0.005f), Vector3.up, 0.0065f, 0.012f, 16);        // elevation turret
            mb.Cylinder(new Vector3(0.0175f + 0.004f, DotAxisHeight, 0.005f), Vector3.right, 0.0065f, 0.012f, 16);          // windage turret
            Mesh body = SaveMesh(OpticsRoot + "Scope4x/Scope4x_Static.asset", mb.ToMesh("Scope4x_Static"));
            Material metal = OpticMetal(OpticsRoot + "Scope4x/Scope4x.mat");
            Material lens = Transparent(OpticsRoot + "Scope4x/Scope4x_Lens.mat", new Color(0.12f, 0.2f, 0.26f, 0.4f), null);
            OpticPrefab("Scope4x", body, metal, disc, k, -0.0715f, new[] {
                new Lens { Name = "Objective", Material = lens, Z = 0.07f, Radius = 0.0215f },
                new Lens { Name = "Ocular", Material = lens, Z = -0.0715f, Radius = 0.0135f },
            });
        }
    }

    private class Lens {
        public string Name;
        public Material Material;
        public float Z, Radius;
    }

    /// <summary>
    /// A hollow tube sight on a rail base: the tube axis is DotAxisHeight above the Mount at the origin. Ocular and objective ends
    /// flare out; the saddle and base block hold it on the rail.
    /// </summary>
    private static void AddTubeSight(MeshBuilder mb, float z0, float z1, float rOcular, float rTube, float rObjective, float rInnerTube,
                                     float rInnerObjective, float baseLength) {
        float len = z1 - z0, h = DotAxisHeight;
        float[] zs = { 0f, 0.14f, 0.18f, 0.28f, 0.34f, 0.64f, 0.68f, 0.82f, 1f };
        float[] ro = { rOcular, rOcular, rTube, rTube, rTube, rTube, rObjective, rObjective, rObjective };
        float[] ri = { rInnerTube, rInnerTube, rInnerTube, rInnerTube, rInnerTube, rInnerTube, rInnerObjective, rInnerObjective, rInnerObjective };
        Matrix4x4 m = Matrix4x4.Translate(new Vector3(0f, h, 0f));
        var outer = new List<Vector2>();
        var inner = new List<Vector2>();
        for (int i = 0; i < zs.Length; i++) {
            outer.Add(new Vector2(z0 + zs[i] * len, ro[i]));
            inner.Add(new Vector2(z0 + zs[i] * len, ri[i]));
        }
        mb.Lathe(outer, 28, m, false);
        mb.Lathe(inner, 28, m, true);
        mb.Annulus(z0, ri[0], ro[0], 28, m, -1f);
        mb.Annulus(z1, ri[ri.Length - 1], ro[ro.Length - 1], 28, m, 1f);
        // saddle and rail base
        mb.Box(new Vector3(0f, 0.01275f, 0f), new Vector3(0.026f, 0.0135f, baseLength * 0.5f)); // top stays below the bore
        mb.Box(new Vector3(0f, 0.005f, 0f), new Vector3(0.036f, 0.01f, baseLength));
        // clamp screws
        mb.Cylinder(new Vector3(0.0215f, 0.006f, baseLength * 0.28f), Vector3.right, 0.004f, 0.006f, 12);
        mb.Cylinder(new Vector3(0.0215f, 0.006f, -baseLength * 0.28f), Vector3.right, 0.004f, 0.006f, 12);
    }

    /// <summary>
    /// "&lt;Name&gt;_View" for an optic, in the same gun space as the weapon views: the model node is scaled by k, Mount sits at the
    /// root origin (put it on the gun's Rail) and Eye is the lens/aperture centre on the sight axis (look along +Z through it).
    /// </summary>
    private static void OpticPrefab(string name, Mesh body, Material metal, Mesh disc, float k, float eyeZ, Lens[] lenses) {
        GameObject root = new GameObject(name + "_View") { layer = FpsGunLayer };
        GameObject model = new GameObject(name + " Model", typeof(MeshFilter), typeof(MeshRenderer)) { layer = FpsGunLayer };
        model.transform.SetParent(root.transform, false);
        model.transform.localScale = Vector3.one * k;
        model.GetComponent<MeshFilter>().sharedMesh = body;
        MeshRenderer mr = model.GetComponent<MeshRenderer>();
        mr.sharedMaterial = metal;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        // Lenses and the dot are discs facing the shooter, centred on the sight axis.
        foreach (Lens l in lenses) {
            GameObject go = new GameObject(l.Name, typeof(MeshFilter), typeof(MeshRenderer)) { layer = FpsGunLayer };
            go.transform.SetParent(model.transform, false);
            go.transform.localPosition = new Vector3(0f, DotAxisHeight, l.Z);
            go.transform.localScale = Vector3.one * l.Radius;
            go.GetComponent<MeshFilter>().sharedMesh = disc;
            MeshRenderer lr = go.GetComponent<MeshRenderer>();
            lr.sharedMaterial = l.Material;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
        }

        GameObject mount = new GameObject("Mount") { layer = FpsGunLayer };
        mount.transform.SetParent(root.transform, false);
        GameObject eye = new GameObject("Eye") { layer = FpsGunLayer };
        eye.transform.SetParent(root.transform, false);
        eye.transform.localPosition = new Vector3(0f, DotAxisHeight * k, eyeZ * k);

        PrefabUtility.SaveAsPrefabAsset(root, OpticsRoot + name + "/" + name + "_View.prefab");
        Object.DestroyImmediate(root);
    }

    private static Material OpticMetal(string path) {
        Material m = GetMaterial(path, "Standard");
        m.SetColor("_Color", new Color(0.12f, 0.12f, 0.13f));
        m.SetFloat("_Metallic", 0.8f);
        m.SetFloat("_Glossiness", 0.5f);
        EditorUtility.SetDirty(m);
        return m;
    }

    /// <summary>Unlit alpha-blended material (Sprites/Default works in the built-in pipeline and on WebGL).</summary>
    private static Material Transparent(string path, Color color, Texture2D tex) {
        Material m = GetMaterial(path, "Sprites/Default");
        m.SetColor("_Color", color);
        m.SetTexture("_MainTex", tex);
        EditorUtility.SetDirty(m);
        return m;
    }

    /// <summary>Round red dot: bright core with a soft halo (white, tinted by the material).</summary>
    private static Texture2D DotTexture(string path) {
        const int size = 64;
        Texture2D t = new Texture2D(size, size, TextureFormat.RGBA32, false);
        for (int y = 0; y < size; y++) {
            for (int x = 0; x < size; x++) {
                float r = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(size / 2f, size / 2f)) / (size / 2f);
                float a = r < 0.14f ? 1f : Mathf.Pow(1f - Mathf.SmoothStep(0.14f, 1f, r), 1.6f) * 0.55f;
                t.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        }
        File.WriteAllBytes(Abs(path), t.EncodeToPNG());
        Object.DestroyImmediate(t);
        AssetDatabase.ImportAsset(path);
        TextureImporter ti = (TextureImporter)AssetImporter.GetAtPath(path);
        ti.alphaIsTransparency = true;
        ti.mipmapEnabled = false;
        ti.wrapMode = TextureWrapMode.Clamp;
        ti.textureCompression = TextureImporterCompression.Uncompressed;
        ti.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    /// <summary>Unit-radius disc facing -Z (toward the shooter) with radial UVs.</summary>
    private static Mesh DiscMesh(int segments) {
        MeshBuilder mb = new MeshBuilder();
        mb.Disc(segments);
        return mb.ToMesh("Optic_Disc");
    }

    /// <summary>Tiny procedural mesh writer: surfaces of revolution, boxes and discs with flat or smooth normals.</summary>
    private class MeshBuilder {
        private readonly List<Vector3> v = new List<Vector3>();
        private readonly List<Vector3> n = new List<Vector3>();
        private readonly List<Vector2> uv = new List<Vector2>();
        private readonly List<int> tri = new List<int>();

        private void Tri(int a, int b, int c) {
            // keep the winding consistent with the vertex normals
            Vector3 g = Vector3.Cross(v[b] - v[a], v[c] - v[a]);
            if (Vector3.Dot(g, n[a] + n[b] + n[c]) < 0f) { int t = b; b = c; c = t; }
            tri.Add(a); tri.Add(b); tri.Add(c);
        }

        private int Vert(Vector3 p, Vector3 normal, Vector2 uvs) {
            v.Add(p); n.Add(normal.normalized); uv.Add(uvs);
            return v.Count - 1;
        }

        /// <summary>Surface of revolution around Z (x = z along the axis, y = radius). inward flips the normals.</summary>
        public void Lathe(List<Vector2> profile, int segments, Matrix4x4 m, bool inward) {
            for (int i = 0; i < profile.Count - 1; i++) {
                Vector2 a = profile[i], b = profile[i + 1];
                Vector2 d = b - a; // (dz, dr)
                Vector2 nn = new Vector2(d.x, -d.y).normalized; // (radial, axial)
                if (inward) nn = -nn;
                int first = v.Count;
                for (int j = 0; j <= segments; j++) {
                    float ang = j / (float)segments * Mathf.PI * 2f, c = Mathf.Cos(ang), s = Mathf.Sin(ang);
                    Vector3 normal = m.MultiplyVector(new Vector3(c * nn.x, s * nn.x, nn.y));
                    Vert(m.MultiplyPoint3x4(new Vector3(c * a.y, s * a.y, a.x)), normal, new Vector2(j / (float)segments, a.x));
                    Vert(m.MultiplyPoint3x4(new Vector3(c * b.y, s * b.y, b.x)), normal, new Vector2(j / (float)segments, b.x));
                }
                for (int j = 0; j < segments; j++) {
                    int p = first + j * 2;
                    Tri(p, p + 1, p + 2);
                    Tri(p + 2, p + 1, p + 3);
                }
            }
        }

        /// <summary>Flat ring (or disc when rInner is 0) at z, facing +Z or -Z.</summary>
        public void Annulus(float z, float rInner, float rOuter, int segments, Matrix4x4 m, float facing) {
            Vector3 normal = m.MultiplyVector(new Vector3(0f, 0f, facing));
            int first = v.Count;
            for (int j = 0; j <= segments; j++) {
                float ang = j / (float)segments * Mathf.PI * 2f, c = Mathf.Cos(ang), s = Mathf.Sin(ang);
                Vert(m.MultiplyPoint3x4(new Vector3(c * rInner, s * rInner, z)), normal, new Vector2(0.5f + 0.5f * c * rInner / rOuter, 0.5f + 0.5f * s * rInner / rOuter));
                Vert(m.MultiplyPoint3x4(new Vector3(c * rOuter, s * rOuter, z)), normal, new Vector2(0.5f + 0.5f * c, 0.5f + 0.5f * s));
            }
            for (int j = 0; j < segments; j++) {
                int p = first + j * 2;
                Tri(p, p + 1, p + 2);
                Tri(p + 2, p + 1, p + 3);
            }
        }

        /// <summary>Capped cylinder along an arbitrary axis.</summary>
        public void Cylinder(Vector3 center, Vector3 axis, float radius, float length, int segments) {
            Matrix4x4 m = Matrix4x4.TRS(center, Quaternion.FromToRotation(Vector3.forward, axis), Vector3.one);
            Lathe(new List<Vector2> { new Vector2(-length / 2f, radius), new Vector2(length / 2f, radius) }, segments, m, false);
            Annulus(-length / 2f, 0f, radius, segments, m, -1f);
            Annulus(length / 2f, 0f, radius, segments, m, 1f);
        }

        public void Box(Vector3 center, Vector3 size) {
            Vector3 h = size / 2f;
            for (int axis = 0; axis < 3; axis++) {
                for (int sign = -1; sign <= 1; sign += 2) {
                    Vector3 nrm = Vector3.zero;
                    nrm[axis] = sign;
                    Vector3 u = Vector3.zero, w = Vector3.zero;
                    u[(axis + 1) % 3] = h[(axis + 1) % 3];
                    w[(axis + 2) % 3] = h[(axis + 2) % 3];
                    Vector3 c = center + Vector3.Scale(nrm, h);
                    int a = Vert(c - u - w, nrm, new Vector2(0f, 0f)), b = Vert(c + u - w, nrm, new Vector2(1f, 0f));
                    int cc = Vert(c + u + w, nrm, new Vector2(1f, 1f)), d = Vert(c - u + w, nrm, new Vector2(0f, 1f));
                    Tri(a, b, cc);
                    Tri(a, cc, d);
                }
            }
        }

        /// <summary>Unit disc in the XY plane facing -Z.</summary>
        public void Disc(int segments) {
            Vector3 normal = Vector3.back;
            int centre = Vert(Vector3.zero, normal, new Vector2(0.5f, 0.5f));
            int first = v.Count;
            for (int j = 0; j <= segments; j++) {
                float ang = j / (float)segments * Mathf.PI * 2f, c = Mathf.Cos(ang), s = Mathf.Sin(ang);
                Vert(new Vector3(c, s, 0f), normal, new Vector2(0.5f + 0.5f * c, 0.5f + 0.5f * s));
            }
            for (int j = 0; j < segments; j++) Tri(centre, first + j, first + j + 1);
        }

        public Mesh ToMesh(string name) {
            Mesh mesh = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt16 };
            mesh.SetVertices(v);
            mesh.SetNormals(n);
            mesh.SetUVs(0, uv);
            mesh.SetTriangles(tri, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }

    // ------------------------------------------------------------------ preview

    private class Item {
        public string Name;
        public GameObject View;
        public float Length;
    }

    /// <summary>
    /// Side views against a neutral background: all weapons next to the AK in gun space (lineup_side.png), each with its optics
    /// (optics_lineup.png), a close-up per weapon and optic with the markers on a 5 cm grid, three-quarter views, and the view
    /// through each sight. -previewDir sets the output folder.
    /// </summary>
    [MenuItem("Tools/FPS/Preview Weapons")]
    public static void Preview() {
        string dir = CommandLineArg("-previewDir") ?? Path.GetFullPath(Path.Combine(Application.dataPath, "../Library/WeaponPreview"));
        Directory.CreateDirectory(dir);
        float k = GunUnitsPerMetre();
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SetupLighting();
        Camera cam = new GameObject("PreviewCam").AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.62f, 0.64f, 0.67f);
        cam.nearClipPlane = 0.01f;
        cam.farClipPlane = 50f;

        // lineup: AK first, then the new weapons, all in gun space (what the player prefab sees)
        var items = new List<Item> { new Item { Name = "AK47", Length = AkRealLength } };
        foreach (Spec s in Specs) items.Add(new Item { Name = s.Name, Length = s.Length });
        System.Func<Item, GameObject> make = it => it.Name == "AK47" ? AkView(k) : Instance(Dir(Specs.First(x => x.Name == it.Name)) + it.Name + "_View.prefab");
        foreach (Item it in items) it.View = make(it);
        LineupSide(cam, items.Select(i => new[] { i.View }.ToList()).ToList(), k, Path.Combine(dir, "lineup_side.png"));
        foreach (Item it in items) Object.DestroyImmediate(it.View);

        // each weapon with the optics it takes
        var combos = new List<List<GameObject>>();
        foreach (var pair in new[] { ("AK47", "RedDot"), ("AK47", "Scope4x"), ("Pistol", "RedDot"), ("SMG", "RedDot"), ("Sniper", "Scope4x"), ("Shotgun", "RedDot") }) {
            GameObject gun = pair.Item1 == "AK47" ? AkView(k) : Instance(Dir(Specs.First(x => x.Name == pair.Item1)) + pair.Item1 + "_View.prefab");
            GameObject optic = Instance(OpticsRoot + pair.Item2 + "/" + pair.Item2 + "_View.prefab");
            optic.transform.position = gun.transform.Find("Rail").position;
            combos.Add(new List<GameObject> { gun, optic });
        }
        LineupSide(cam, combos, k, Path.Combine(dir, "optics_lineup.png"));
        foreach (var c in combos) foreach (GameObject g in c) Object.DestroyImmediate(g);

        // per weapon, in real metres with the muzzle at the origin
        foreach (Item it in items) {
            GameObject view = make(it);
            Normalise(view, k);
            CloseUp(cam, view, it.Name, it.Length, dir);
            Object.DestroyImmediate(view);
        }
        // AK with each optic from behind
        foreach (string optic in new[] { "RedDot", "Scope4x" }) {
            GameObject gun = AkView(k);
            GameObject o = Instance(OpticsRoot + optic + "/" + optic + "_View.prefab");
            o.transform.position = gun.transform.Find("Rail").position;
            o.transform.SetParent(gun.transform, true);
            Normalise(gun, k);
            cam.orthographic = false;
            cam.fieldOfView = 25f;
            Vector3 center = new Vector3(0f, 0.02f, -0.3f);
            cam.transform.position = center + new Vector3(0.55f, 0.35f, -0.75f) * 1.4f;
            cam.transform.LookAt(center);
            Render(cam, 1200, 800, Path.Combine(dir, "AK47_" + optic + "_persp.png"));
            Object.DestroyImmediate(gun);
        }
        // the optics on their own and through the sight
        foreach (string optic in new[] { "RedDot", "Scope4x" }) {
            GameObject o = Instance(OpticsRoot + optic + "/" + optic + "_View.prefab");
            o.transform.localScale = Vector3.one / k;
            CloseUpOptic(cam, o, optic, dir);
            Object.DestroyImmediate(o);
        }
        Debug.Log("[WeaponImport] Preview written to " + dir);
    }

    private static GameObject Instance(string prefabPath) {
        return (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath));
    }

    /// <summary>The AK laid out like the new view prefabs: model node with the AK's own transform plus Muzzle and Rail markers.</summary>
    private static GameObject AkView(float k) {
        GameObject root = new GameObject("AK47_View");
        GameObject model = new GameObject("AK47 Model", typeof(MeshFilter), typeof(MeshRenderer));
        model.transform.SetParent(root.transform, false);
        model.transform.localPosition = Anchor;
        model.transform.localScale = Vector3.one * AkModelScale;
        model.GetComponent<MeshFilter>().sharedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(Root + "AK47/AK47_Static.asset");
        model.GetComponent<MeshRenderer>().sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(Root + "AK47/AK47.mat");
        AddMarker(root.transform, "Muzzle", Vector3.zero, k);
        AddMarker(root.transform, "Rail", AkRail, k);
        return root;
    }

    /// <summary>Scale a gun-space view down to real metres with the muzzle at the world origin.</summary>
    private static void Normalise(GameObject view, float k) {
        view.transform.localScale = Vector3.one / k;
        view.transform.position = -Anchor / k;
    }

    /// <summary>Rows of gun-space objects (bore line on the row's centre), side view, vertical grid every 10 cm from the muzzle.</summary>
    private static void LineupSide(Camera cam, List<List<GameObject>> rows, float k, string path) {
        const float row = 0.6f;
        for (int i = 0; i < rows.Count; i++) {
            foreach (GameObject g in rows[i]) g.transform.position += new Vector3(0f, (rows.Count / 2 - i) * row - Anchor.y, 0f);
        }
        Grid(k, 0.1f, 0.5f, Anchor.z, 0f, false);
        cam.orthographic = true;
        cam.orthographicSize = rows.Count * row / 2f + 0.3f;
        cam.transform.position = new Vector3(8f, 0f, -0.35f);
        cam.transform.rotation = Quaternion.LookRotation(Vector3.left, Vector3.up);
        const int width = 1400;
        Render(cam, width, Mathf.RoundToInt(width * 2f * cam.orthographicSize / 3.4f), path);
        DestroyByPrefix("GridLine");
    }

    private static void CloseUp(Camera cam, GameObject view, string name, float length, string dir) {
        Dictionary<string, Color> colors = MarkerColors();
        AddBalls(view, colors);
        Grid(1f, 0.05f, 0.1f, 0f, 0f, true);
        cam.orthographic = true;
        cam.orthographicSize = Mathf.Max(length * 1.1f / 3f, 0.12f);
        cam.transform.position = new Vector3(8f, -0.02f, -length * 0.5f);
        cam.transform.rotation = Quaternion.LookRotation(Vector3.left, Vector3.up);
        Render(cam, 1500, 1000, Path.Combine(dir, name + "_side.png"));
        DestroyByPrefix("GridLine");
        DestroyByPrefix("GridMarker"); // the three-quarter view shows the bare model

        cam.orthographic = false;
        cam.fieldOfView = 25f;
        Vector3 center = new Vector3(0f, -0.03f, -length * 0.5f);
        cam.transform.position = center + new Vector3(0.9f, 0.45f, 0.9f) * (length * 1.5f);
        cam.transform.LookAt(center);
        Render(cam, 1200, 800, Path.Combine(dir, name + "_persp.png"));
        DestroyByPrefix("GridMarker");
    }

    /// <summary>Optic alone: side close-up with Mount/Eye, rear three-quarter view, and the camera at Eye looking down +Z.</summary>
    private static void CloseUpOptic(Camera cam, GameObject optic, string name, string dir) {
        AddBalls(optic, MarkerColors());
        Grid(1f, 0.01f, 0.05f, 0f, 0f, true);
        cam.orthographic = true;
        cam.orthographicSize = 0.07f;
        cam.transform.position = new Vector3(8f, 0.03f, 0f);
        cam.transform.rotation = Quaternion.LookRotation(Vector3.left, Vector3.up);
        Render(cam, 1500, 1000, Path.Combine(dir, name + "_side.png"));
        DestroyByPrefix("GridLine");
        DestroyByPrefix("GridMarker");

        cam.orthographic = false;
        cam.fieldOfView = 25f;
        Vector3 center = new Vector3(0f, 0.03f, 0f);
        cam.transform.position = center + new Vector3(0.1f, 0.07f, -0.2f);
        cam.transform.LookAt(center);
        Render(cam, 1200, 800, Path.Combine(dir, name + "_persp.png"));
        DestroyByPrefix("GridMarker");

        // what the player sees: a rail on a neutral wall some way ahead, camera 4 cm behind Eye
        Transform eye = optic.transform.Find("Eye");
        GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wall.name = "GridWall";
        wall.transform.position = new Vector3(0f, 0.03f, 6f);
        wall.transform.localScale = new Vector3(8f, 4f, 0.1f);
        wall.GetComponent<Renderer>().sharedMaterial = Flat(new Color(0.35f, 0.5f, 0.3f));
        Object.DestroyImmediate(wall.GetComponent<Collider>());
        for (int i = -3; i <= 3; i++) {
            GameObject post = GameObject.CreatePrimitive(PrimitiveType.Cube);
            post.name = "GridWall";
            post.transform.position = new Vector3(i * 0.9f, 0.03f, 5.9f);
            post.transform.localScale = new Vector3(0.12f, 3f, 0.1f);
            post.GetComponent<Renderer>().sharedMaterial = Flat(i % 2 == 0 ? new Color(0.8f, 0.8f, 0.75f) : new Color(0.2f, 0.2f, 0.25f));
            Object.DestroyImmediate(post.GetComponent<Collider>());
        }
        optic.SetActive(true);
        cam.orthographic = false;
        cam.fieldOfView = 40f;
        cam.nearClipPlane = 0.005f;
        cam.transform.position = eye.position + Vector3.back * 0.04f;
        cam.transform.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);
        Render(cam, 1000, 700, Path.Combine(dir, name + "_aim.png"));
        cam.nearClipPlane = 0.01f;
        DestroyByPrefix("GridWall");
    }

    private static Dictionary<string, Color> MarkerColors() {
        return new Dictionary<string, Color> {
            { "Muzzle", Color.red }, { "Grip", Color.green }, { "Handguard", Color.blue }, { "Rail", Color.yellow },
            { "Mount", Color.magenta }, { "Eye", Color.cyan },
        };
    }

    /// <summary>Marker balls drawn on the camera side of the model so they are not hidden inside it.</summary>
    private static void AddBalls(GameObject view, Dictionary<string, Color> colors) {
        foreach (var kv in colors) {
            Transform mt = view.transform.Find(kv.Key);
            if (mt == null) continue;
            GameObject ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            ball.name = "GridMarker" + kv.Key;
            ball.transform.position = mt.position + Vector3.right * 0.2f;
            ball.transform.localScale = Vector3.one * (view.name.StartsWith("RedDot") || view.name.StartsWith("Scope") ? 0.004f : 0.016f);
            ball.GetComponent<Renderer>().sharedMaterial = Flat(kv.Value);
            Object.DestroyImmediate(ball.GetComponent<Collider>());
        }
    }

    private static void DestroyByPrefix(string prefix) {
        foreach (GameObject g in Object.FindObjectsOfType<GameObject>().Where(g => g.name.StartsWith(prefix)).ToArray()) Object.DestroyImmediate(g);
    }

    /// <summary>Thin lines every <paramref name="step"/> metres (x k gun units) along Z (and Y when asked); every <paramref name="major"/> darker.</summary>
    private static void Grid(float k, float step, float major, float originZ, float originY, bool horizontal) {
        Material thin = Flat(new Color(0.45f, 0.47f, 0.5f)), thick = Flat(new Color(0.15f, 0.15f, 0.2f));
        int n = Mathf.CeilToInt(3f / (step * k)), majorEvery = Mathf.RoundToInt(major / step);
        if (n > 400) n = 400;
        for (int i = -n; i <= n; i++) {
            float pos = i * step * k;
            bool isMajor = i % majorEvery == 0;
            float w = 0.0016f * k * (isMajor ? 2f : 1f) * (step < 0.02f ? 0.25f : 1f);
            Material m = isMajor ? thick : thin;
            Line(new Vector3(-3f, originY, originZ + pos), new Vector3(0.002f, 8f, w), m);
            if (horizontal) Line(new Vector3(-3f, originY + pos, originZ), new Vector3(0.002f, w, 8f), m);
        }
    }

    private static void Line(Vector3 pos, Vector3 size, Material m) {
        GameObject g = GameObject.CreatePrimitive(PrimitiveType.Cube);
        g.name = "GridLine";
        g.transform.position = pos;
        g.transform.localScale = size;
        g.GetComponent<Renderer>().sharedMaterial = m;
        Object.DestroyImmediate(g.GetComponent<Collider>());
    }

    private static Material Flat(Color c) {
        Material m = new Material(Shader.Find("Unlit/Color"));
        m.color = c;
        return m;
    }

    private static void SetupLighting() {
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.5f, 0.52f, 0.55f);
        RenderSettings.skybox = null;
        RenderSettings.defaultReflectionMode = UnityEngine.Rendering.DefaultReflectionMode.Custom;
        RenderSettings.customReflectionTexture = Environment();
        Light key = new GameObject("Key").AddComponent<Light>();
        key.type = LightType.Directional;
        key.intensity = 1.1f;
        key.shadows = LightShadows.None;
        key.transform.rotation = Quaternion.LookRotation(new Vector3(-1f, -0.7f, 0.5f));
        Light fill = new GameObject("Fill").AddComponent<Light>();
        fill.type = LightType.Directional;
        fill.intensity = 0.45f;
        fill.shadows = LightShadows.None;
        fill.transform.rotation = Quaternion.LookRotation(new Vector3(0.6f, -0.3f, -0.8f));
    }

    /// <summary>Plain sky-to-ground gradient so metal has something to reflect.</summary>
    private static Cubemap Environment() {
        Cubemap cm = new Cubemap(32, TextureFormat.RGBA32, false);
        for (int face = 0; face < 6; face++) {
            for (int y = 0; y < 32; y++) {
                float h = face == 2 ? 1f : (face == 3 ? 0f : 1f - y / 31f);
                Color c = Color.Lerp(new Color(0.22f, 0.22f, 0.24f), new Color(0.9f, 0.93f, 1f), Mathf.SmoothStep(0f, 1f, h));
                for (int x = 0; x < 32; x++) cm.SetPixel((CubemapFace)face, x, y, c);
            }
        }
        cm.Apply();
        return cm;
    }

    private static void Render(Camera cam, int w, int h, string path) {
        RenderTexture rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
        cam.targetTexture = rt;
        cam.aspect = (float)w / h;
        cam.Render();
        RenderTexture.active = rt;
        Texture2D tex = new Texture2D(w, h, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        tex.Apply();
        File.WriteAllBytes(path, tex.EncodeToPNG());
        RenderTexture.active = null;
        cam.targetTexture = null;
        Object.DestroyImmediate(rt);
        Object.DestroyImmediate(tex);
    }

    private static string Abs(string assetPath) {
        return Path.Combine(Path.GetDirectoryName(Application.dataPath), assetPath);
    }

    private static string CommandLineArg(string name) {
        string[] args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
        return null;
    }

    // ------------------------------------------------------------------ helpers

    private static Bounds Slice(Vector3[] v, int axis, float from, float to) {
        bool first = true;
        Bounds b = new Bounds();
        foreach (Vector3 p in v) {
            if (p[axis] < from || p[axis] > to) continue;
            if (first) { b = new Bounds(p, Vector3.zero); first = false; } else b.Encapsulate(p);
        }
        return b;
    }

    private static int MaxAxis(Vector3 s) {
        return s.x >= s.y && s.x >= s.z ? 0 : (s.y >= s.z ? 1 : 2);
    }

    private static int MinAxis(Vector3 s, int except) {
        int best = -1;
        for (int i = 0; i < 3; i++) if (i != except && (best < 0 || s[i] < s[best])) best = i;
        return best;
    }

    private static Vector3 Axis(int i) {
        return i == 0 ? Vector3.right : (i == 1 ? Vector3.up : Vector3.forward);
    }

}
