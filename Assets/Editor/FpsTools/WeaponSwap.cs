using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Replaces the low-poly AK on the player prefabs (first- and third-person) with the CC0 Stein Games AK-47.
/// The old renderers are only disabled, so the gun animators, muzzle point and IK handles keep working.
///   Unity -batchmode -quit -projectPath . -executeMethod WeaponSwap.Apply
/// </summary>
public static class WeaponSwap {

    private const string Dir = "Assets/Art/Military/Weapons/AK47/";
    private const string ModelName = "AK47 Model";
    private static readonly string[] PlayerPrefabs = {
        "Assets/Prefabs/Resources/PolicemanController.prefab",
        "Assets/Prefabs/Resources/RobotXController.prefab",
        "Assets/Prefabs/Resources/RobotYController.prefab",
    };
    private static readonly string[] OldParts = { "Ak47Body", "Ak47Mag", "Cube_001" };

    [MenuItem("Tools/FPS/Swap Weapon Model")]
    public static void Apply() {
        ConfigureTextures();
        Mesh mesh = BakeStaticMesh(out float length);
        Material mat = BuildMaterial();
        foreach (string path in PlayerPrefabs) {
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            foreach (string gunName in new[] { "FpsGun_AK47", "TpsGun_AK47" }) {
                Transform gun = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == gunName);
                if (gun == null) continue;
                Fit(gun, mesh, mat, length);
            }
            PrefabUtility.SaveAsPrefabAsset(root, path);
            PrefabUtility.UnloadPrefabContents(root);
        }
        AssetDatabase.SaveAssets();
        Debug.Log("[WeaponSwap] Applied. Model length " + length.ToString("F3") + " m");
    }

    private static void ConfigureTextures() {
        foreach (string name in new[] { "AK47_Normal", "AK47_MetallicSmoothness", "AK47_AO", "AK47_Albedo" }) {
            TextureImporter ti = (TextureImporter)AssetImporter.GetAtPath(Dir + name + ".png");
            if (name.EndsWith("Normal")) ti.textureType = TextureImporterType.NormalMap;
            else if (!name.EndsWith("Albedo")) ti.sRGBTexture = false;
            ti.anisoLevel = 4;
            ti.SaveAndReimport();
        }
    }

    /// <summary>
    /// The pack ships an Unreal-style skinned mesh. Bake it in its rest pose into one static mesh, oriented the way the
    /// old gun is authored: +Z toward the muzzle, +Y up, muzzle tip (bore centre) at the origin, real-world metres.
    /// </summary>
    private static Mesh BakeStaticMesh(out float length) {
        GameObject model = (GameObject)Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Dir + "AK47.fbx"));
        model.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        model.transform.localScale = Vector3.one;
        var parts = model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        CombineInstance[] combine = new CombineInstance[parts.Length];
        for (int i = 0; i < parts.Length; i++) {
            Mesh baked = new Mesh();
            parts[i].BakeMesh(baked, true);
            combine[i] = new CombineInstance {
                mesh = baked,
                transform = Matrix4x4.TRS(parts[i].transform.position, parts[i].transform.rotation, Vector3.one),
            };
        }
        Mesh world = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        world.CombineMeshes(combine, true, true);
        Object.DestroyImmediate(model);

        Vector3[] v = world.vertices;
        Bounds b = world.bounds;
        // Long axis = barrel; the narrower end is the muzzle; the barrel sits above the magazine.
        int longAxis = MaxAxis(b.size);
        int sideAxis = MinAxis(b.size, longAxis);
        int upAxis = 3 - longAxis - sideAxis;
        float lo = b.min[longAxis], hi = b.max[longAxis], slice = (hi - lo) * 0.06f;
        Bounds endLo = Slice(v, longAxis, lo, lo + slice), endHi = Slice(v, longAxis, hi - slice, hi);
        bool muzzleHigh = endHi.size[upAxis] < endLo.size[upAxis];
        Bounds muzzle = muzzleHigh ? endHi : endLo;
        Vector3 forward = Axis(longAxis) * (muzzleHigh ? 1 : -1);
        Vector3 up = Axis(upAxis) * Mathf.Sign(muzzle.center[upAxis] - b.center[upAxis]);
        Quaternion toGun = Quaternion.Inverse(Quaternion.LookRotation(forward, up));
        Vector3 tip = muzzle.center;
        tip[longAxis] = muzzleHigh ? hi : lo;

        Vector3[] n = world.normals;
        Vector4[] t = world.tangents;
        for (int i = 0; i < v.Length; i++) {
            v[i] = toGun * (v[i] - tip);
            if (n.Length > 0) n[i] = toGun * n[i];
            if (t.Length > 0) {
                Vector3 tt = toGun * new Vector3(t[i].x, t[i].y, t[i].z);
                t[i] = new Vector4(tt.x, tt.y, tt.z, t[i].w);
            }
        }
        world.vertices = v;
        if (n.Length > 0) world.normals = n;
        if (t.Length > 0) world.tangents = t; else world.RecalculateTangents();
        world.RecalculateBounds();
        world.name = "AK47_Static";
        length = world.bounds.size.z;
        Debug.Log("[WeaponSwap] baked " + v.Length + " verts, axes long=" + longAxis + " up=" + upAxis +
                  " muzzleHigh=" + muzzleHigh + " bounds " + world.bounds.ToString("F3"));

        string meshPath = Dir + "AK47_Static.asset";
        AssetDatabase.DeleteAsset(meshPath);
        AssetDatabase.CreateAsset(world, meshPath);
        return world;
    }

    private static Material BuildMaterial() {
        string path = Dir + "AK47.mat";
        Material m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null) {
            m = new Material(Shader.Find("Standard"));
            AssetDatabase.CreateAsset(m, path);
        }
        m.SetTexture("_MainTex", AssetDatabase.LoadAssetAtPath<Texture2D>(Dir + "AK47_Albedo.png"));
        m.SetTexture("_BumpMap", AssetDatabase.LoadAssetAtPath<Texture2D>(Dir + "AK47_Normal.png"));
        m.EnableKeyword("_NORMALMAP");
        m.SetTexture("_MetallicGlossMap", AssetDatabase.LoadAssetAtPath<Texture2D>(Dir + "AK47_MetallicSmoothness.png"));
        m.EnableKeyword("_METALLICGLOSSMAP");
        m.SetFloat("_GlossMapScale", 1f);
        m.SetTexture("_OcclusionMap", AssetDatabase.LoadAssetAtPath<Texture2D>(Dir + "AK47_AO.png"));
        m.SetFloat("_OcclusionStrength", 1f);
        m.SetColor("_Color", Color.white);
        EditorUtility.SetDirty(m);
        return m;
    }

    /// <summary>Hide the old parts and drop the new mesh in with its muzzle on the old muzzle and the same overall length.</summary>
    private static void Fit(Transform gun, Mesh mesh, Material mat, float length) {
        Bounds old = LocalBounds(gun);
        Renderer template = null;
        foreach (Renderer r in gun.GetComponentsInChildren<Renderer>(true)) {
            if (!OldParts.Contains(r.name)) continue;
            if (r.name == "Ak47Body") template = r;
            r.enabled = false;
        }
        Transform existing = gun.Find(ModelName);
        if (existing != null) Object.DestroyImmediate(existing.gameObject);

        Transform barrel = gun.Find("GunBarrelEnd");
        float boreY = barrel != null ? barrel.localPosition.y : old.center.y;
        GameObject go = new GameObject(ModelName, typeof(MeshFilter), typeof(MeshRenderer));
        go.layer = gun.gameObject.layer;
        go.transform.SetParent(gun, false);
        go.transform.localPosition = new Vector3(old.center.x, boreY, old.max.z);
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = Vector3.one * (old.size.z / length);
        go.GetComponent<MeshFilter>().sharedMesh = mesh;
        MeshRenderer mr = go.GetComponent<MeshRenderer>();
        mr.sharedMaterial = mat;
        if (template != null) {
            mr.shadowCastingMode = template.shadowCastingMode;
            mr.receiveShadows = template.receiveShadows;
        }
    }

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

    public static void Probe() {
        GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(Dir + "AK47.fbx");
        foreach (MeshFilter mf in model.GetComponentsInChildren<MeshFilter>(true)) {
            Debug.Log("[WeaponSwap] new mesh " + mf.name + " bounds " + mf.sharedMesh.bounds.ToString("F3") +
                      " local " + mf.transform.localRotation.eulerAngles.ToString("F1") + " scale " + mf.transform.lossyScale.ToString("F3"));
        }
        foreach (SkinnedMeshRenderer sr in model.GetComponentsInChildren<SkinnedMeshRenderer>(true)) {
            Debug.Log("[WeaponSwap] new skinned " + sr.name + " bounds " + sr.sharedMesh.bounds.ToString("F3") +
                      " bones " + sr.bones.Length + " rot " + sr.transform.rotation.eulerAngles.ToString("F1") + " scale " + sr.transform.lossyScale.ToString("F3"));
        }
        GameObject root = PrefabUtility.LoadPrefabContents(PlayerPrefabs[0]);
        foreach (string gunName in new[] { "FpsGun_AK47", "TpsGun_AK47" }) {
            Transform gun = root.GetComponentsInChildren<Transform>(true).First(t => t.name == gunName);
            Debug.Log("[WeaponSwap] " + gunName + " parent " + gun.parent.name + " lpos " + gun.localPosition.ToString("F3") +
                      " lrot " + gun.localEulerAngles.ToString("F1") + " lscale " + gun.localScale.ToString("F3") +
                      " layer " + LayerMask.LayerToName(gun.gameObject.layer));
            Bounds b = LocalBounds(gun);
            Debug.Log("[WeaponSwap]   old bounds (gun space) " + b.ToString("F3"));
            foreach (Transform t in gun.GetComponentsInChildren<Transform>(true)) {
                if (t == gun) continue;
                Debug.Log("[WeaponSwap]   child " + t.name + " lpos(gun) " + gun.InverseTransformPoint(t.position).ToString("F3") +
                          " renderer " + (t.GetComponent<Renderer>() != null));
            }
        }
        PrefabUtility.UnloadPrefabContents(root);
    }

    /// <summary>Bounds of the old gun's renderers, expressed in the gun's local space.</summary>
    private static Bounds LocalBounds(Transform gun) {
        bool first = true;
        Bounds b = new Bounds();
        foreach (MeshFilter mf in gun.GetComponentsInChildren<MeshFilter>(true)) {
            if (!OldParts.Contains(mf.name) || mf.sharedMesh == null) continue;
            foreach (Vector3 v in Corners(mf.sharedMesh.bounds)) {
                Vector3 p = gun.InverseTransformPoint(mf.transform.TransformPoint(v));
                if (first) { b = new Bounds(p, Vector3.zero); first = false; } else b.Encapsulate(p);
            }
        }
        return b;
    }

    private static Vector3[] Corners(Bounds b) {
        Vector3 mn = b.min, mx = b.max;
        return new[] {
            new Vector3(mn.x, mn.y, mn.z), new Vector3(mx.x, mn.y, mn.z), new Vector3(mn.x, mx.y, mn.z), new Vector3(mx.x, mx.y, mn.z),
            new Vector3(mn.x, mn.y, mx.z), new Vector3(mx.x, mn.y, mx.z), new Vector3(mn.x, mx.y, mx.z), new Vector3(mx.x, mx.y, mx.z),
        };
    }

}
