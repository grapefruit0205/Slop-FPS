using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;

/// <summary>
/// Applies the military-realistic art pass: CC0 surfaces on the existing map materials,
/// repainted containers, plain window glass, CC0 props scattered around the spawns,
/// an overcast HDRI sky with fog, post-processing, and new weapon/footstep sounds.
/// Safe to re-run:  Unity -batchmode -quit -projectPath . -executeMethod MilitaryLook.Apply
/// </summary>
public static class MilitaryLook {

    private const string Art = "Assets/Art/Military/";
    private const string EnvMats = "Assets/Models/Enviroment/Materials/";
    private const string ScenePath = "Assets/Scenes/Start.unity";
    private const string PropsRootName = "Military Props";
    private static readonly string[] PlayerPrefabs = {
        "Assets/Prefabs/Resources/PolicemanController.prefab",
        "Assets/Prefabs/Resources/RobotXController.prefab",
        "Assets/Prefabs/Resources/RobotYController.prefab",
    };

    private struct PropDef {
        public string Name, Tag;
        public float Weight, Scale;
    }

    private static readonly PropDef[] Props = {
        // Scale brings the crate and barrier up to cover height; ammo boxes only get stacked on crates.
        new PropDef { Name = "wooden_military_crate", Tag = "Wood", Weight = 0.4f, Scale = 1.6f },
        new PropDef { Name = "concrete_road_barrier", Tag = "Concrete", Weight = 0.35f, Scale = 1.3f },
        new PropDef { Name = "Barrel_01", Tag = "Metal", Weight = 0.25f, Scale = 1.05f },
        new PropDef { Name = "ammo_box", Tag = "Metal", Weight = 0f, Scale = 1.4f },
    };

    [MenuItem("Tools/FPS/Apply Military Look")]
    public static void Apply() {
        ConfigureTextures();
        AssetDatabase.Refresh();
        ForceAnisotropic();
        RetextureMap();
        Material glass = WindowGlass();
        SwapMaterialInPrefab("Assets/Prefabs/Enviroment.prefab", "GlassRefractive", glass);
        Dictionary<string, GameObject> props = BuildPropPrefabs();
        PostProcessProfile profile = PostProfile();
        UpdatePlayerPrefabs();
        UpdateScene(props, profile);
        AssetDatabase.SaveAssets();
        Debug.Log("[MilitaryLook] Applied.");
    }

    // ------------------------------------------------------------------ import settings

    private static void ConfigureTextures() {
        foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { Art.TrimEnd('/') })) {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            TextureImporter ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null) continue;
            bool changed = false;
            if (path.Contains("_Normal") && ti.textureType != TextureImporterType.NormalMap) {
                ti.textureType = TextureImporterType.NormalMap;
                changed = true;
            }
            if ((path.Contains("_MetallicSmoothness") || path.Contains("_AO")) && ti.sRGBTexture) {
                ti.sRGBTexture = false;
                changed = true;
            }
            if (path.EndsWith(".hdr")) {
                ti.mipmapEnabled = false;
                ti.wrapModeU = TextureWrapMode.Repeat;
                ti.wrapModeV = TextureWrapMode.Clamp;
                ti.maxTextureSize = 4096;
                ti.textureCompression = TextureImporterCompression.CompressedHQ;
                changed = true;
            }
            if (changed) ti.SaveAndReimport();
        }
    }

    /// <summary>Walls and floors are seen at grazing angles; without anisotropic filtering they smear.</summary>
    private static void ForceAnisotropic() {
        int current = QualitySettings.GetQualityLevel();
        for (int i = 0; i < QualitySettings.names.Length; i++) {
            QualitySettings.SetQualityLevel(i, false);
            QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
        }
        QualitySettings.SetQualityLevel(current, false);
    }

    // ------------------------------------------------------------------ map surfaces

    private static void RetextureMap() {
        Surface("RoofConcrete", "Concrete015", new Color(0.86f, 0.85f, 0.82f));
        Surface("Pamatas", "Concrete015", new Color(0.8f, 0.79f, 0.76f));
        Surface("AsphaltDamaged", "Asphalt012", Color.white);
        Surface("AsphaltDamaged_tiling_fixed_warehouse", "Asphalt012", Color.white);
        Surface("AsphaltDamaged_tiling_fixed_warehouse_2", "Asphalt012", Color.white);
        Surface("Grass", "Ground054", new Color(0.9f, 0.88f, 0.84f));
        Surface("MetalPlatesRusted", "CorrugatedSteel005", new Color(0.62f, 0.66f, 0.58f));
        Surface("MetalPlatesZinc", "CorrugatedSteel005", new Color(0.7f, 0.72f, 0.7f));
        Surface("MetalBulkheads", "CorrugatedSteel005", new Color(0.55f, 0.58f, 0.52f));

        // Keep the brick and wood detail, just pull the saturation toward a dusty palette.
        Tint("BrickLargeBrown", new Color(0.78f, 0.74f, 0.7f));
        Tint("BrickLargeBlack", new Color(0.85f, 0.83f, 0.8f));
        Tint("BrickLargeBlack tiling_fixed_warehouse", new Color(0.85f, 0.83f, 0.8f));
        Tint("PlywoodOld", new Color(0.82f, 0.78f, 0.7f));
        Tint("DoorsFrame", new Color(0.75f, 0.75f, 0.72f));

        Material boxes = AssetDatabase.LoadAssetAtPath<Material>(EnvMats + "CombinedBoxes.mat");
        Texture2D containers = AssetDatabase.LoadAssetAtPath<Texture2D>(Art + "Textures/Containers_Military.png");
        if (boxes != null && containers != null) {
            boxes.SetTexture("_MainTex", containers);
            boxes.SetColor("_Color", Color.white);
            boxes.SetFloat("_Glossiness", 0.25f);
            EditorUtility.SetDirty(boxes);
        }
    }

    private static void Surface(string material, string set, Color tint) {
        Material m = AssetDatabase.LoadAssetAtPath<Material>(EnvMats + material + ".mat");
        if (m == null) {
            Debug.LogWarning("[MilitaryLook] Missing material " + material);
            return;
        }
        string dir = Art + "Textures/" + set + "/" + set;
        ApplyMaps(m, Load(dir + "_Albedo.jpg"), Load(dir + "_Normal.jpg"), Load(dir + "_MetallicSmoothness.png"),
                  Load(dir + "_AO.jpg"));
        m.SetColor("_Color", tint);
        EditorUtility.SetDirty(m);
    }

    private static void Tint(string material, Color tint) {
        Material m = AssetDatabase.LoadAssetAtPath<Material>(EnvMats + material + ".mat");
        if (m == null) return;
        m.SetColor("_Color", tint);
        EditorUtility.SetDirty(m);
    }

    private static void ApplyMaps(Material m, Texture albedo, Texture normal, Texture metalSmooth, Texture ao) {
        if (m.shader.name != "Standard") m.shader = Shader.Find("Standard");
        m.SetTexture("_MainTex", albedo);
        if (normal != null) {
            m.SetTexture("_BumpMap", normal);
            m.SetFloat("_BumpScale", 1f);
            m.EnableKeyword("_NORMALMAP");
        }
        if (metalSmooth != null) {
            m.SetTexture("_MetallicGlossMap", metalSmooth);
            m.SetFloat("_GlossMapScale", 0.9f);
            m.EnableKeyword("_METALLICGLOSSMAP");
        }
        if (ao != null) {
            m.SetTexture("_OcclusionMap", ao);
            m.SetFloat("_OcclusionStrength", 0.8f);
        }
        // Old materials used the specular highlight; the new maps carry their own response.
        m.SetFloat("_SpecularHighlights", 1f);
    }

    private static Texture2D Load(string path) {
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    private static Material WindowGlass() {
        string path = Art + "Materials/WindowGlass.mat";
        System.IO.Directory.CreateDirectory(Art + "Materials");
        Material m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null) {
            m = new Material(Shader.Find("Standard"));
            AssetDatabase.CreateAsset(m, path);
        }
        m.SetColor("_Color", new Color(0.3f, 0.34f, 0.36f, 0.5f));
        m.SetFloat("_Glossiness", 0.92f);
        m.SetFloat("_Metallic", 0.15f);
        // Standard shader "Transparent" rendering mode.
        m.SetFloat("_Mode", 3);
        m.SetInt("_SrcBlend", (int)BlendMode.One);
        m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
        m.SetInt("_ZWrite", 0);
        m.DisableKeyword("_ALPHATEST_ON");
        m.DisableKeyword("_ALPHABLEND_ON");
        m.EnableKeyword("_ALPHAPREMULTIPLY_ON");
        m.renderQueue = (int)RenderQueue.Transparent;
        EditorUtility.SetDirty(m);
        return m;
    }

    private static void SwapMaterialInPrefab(string prefabPath, string oldName, Material replacement) {
        GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
        int swapped = 0;
        foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true)) {
            Material[] mats = r.sharedMaterials;
            bool hit = false;
            for (int i = 0; i < mats.Length; i++) {
                if (mats[i] != null && mats[i].name == oldName) {
                    mats[i] = replacement;
                    hit = true;
                    swapped++;
                }
            }
            if (hit) r.sharedMaterials = mats;
        }
        PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        PrefabUtility.UnloadPrefabContents(root);
        Debug.Log("[MilitaryLook] " + prefabPath + ": replaced " + swapped + " '" + oldName + "' slots");
    }

    // ------------------------------------------------------------------ props

    private static Dictionary<string, GameObject> BuildPropPrefabs() {
        Dictionary<string, GameObject> result = new Dictionary<string, GameObject>();
        int shootable = LayerMask.NameToLayer("Shootable");
        foreach (PropDef def in Props) {
            string dir = Art + "Props/" + def.Name + "/";
            GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(dir + def.Name + ".fbx");
            if (model == null) {
                Debug.LogWarning("[MilitaryLook] Missing model " + def.Name);
                continue;
            }
            string matPath = dir + def.Name + ".mat";
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null) {
                mat = new Material(Shader.Find("Standard"));
                AssetDatabase.CreateAsset(mat, matPath);
            }
            Texture albedo = Load(dir + def.Name + "_Albedo.jpg");
            if (albedo == null) albedo = Load(dir + def.Name + "_Albedo.png");
            ApplyMaps(mat, albedo, Load(dir + def.Name + "_Normal.png"), Load(dir + def.Name + "_MetallicSmoothness.png"), null);
            mat.SetColor("_Color", Color.white);
            EditorUtility.SetDirty(mat);

            GameObject root = new GameObject(def.Name);
            GameObject inst = (GameObject)PrefabUtility.InstantiatePrefab(model);
            PrefabUtility.UnpackPrefabInstance(inst, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            inst.transform.SetParent(root.transform, false);
            inst.transform.localScale *= def.Scale;
            Bounds b = new Bounds();
            bool first = true;
            foreach (Renderer r in inst.GetComponentsInChildren<Renderer>()) {
                r.sharedMaterials = Enumerable.Repeat(mat, r.sharedMaterials.Length).ToArray();
                r.shadowCastingMode = ShadowCastingMode.On;
                if (first) { b = r.bounds; first = false; } else b.Encapsulate(r.bounds);
            }
            // Sit the model on the root's origin so placement can drop it straight onto the ground.
            inst.transform.position -= new Vector3(b.center.x, b.min.y, b.center.z);
            BoxCollider col = root.AddComponent<BoxCollider>();
            col.center = new Vector3(0, b.size.y / 2f, 0);
            col.size = b.size;
            root.tag = def.Tag;
            root.layer = shootable;
            foreach (Transform t in root.GetComponentsInChildren<Transform>()) t.gameObject.layer = shootable;
            root.isStatic = true;
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, dir + def.Name + ".prefab");
            Object.DestroyImmediate(root);
            result[def.Name] = prefab;
            Debug.Log("[MilitaryLook] Prop " + def.Name + " size " + b.size.ToString("F2"));
        }
        return result;
    }

    // ------------------------------------------------------------------ post-processing

    private static PostProcessProfile PostProfile() {
        string path = Art + "MilitaryPost.asset";
        PostProcessProfile p = AssetDatabase.LoadAssetAtPath<PostProcessProfile>(path);
        if (p != null) {
            AssetDatabase.DeleteAsset(path);
        }
        p = ScriptableObject.CreateInstance<PostProcessProfile>();
        AssetDatabase.CreateAsset(p, path);

        ColorGrading cg = Add<ColorGrading>(p);
        cg.gradingMode.Override(GradingMode.HighDefinitionRange);
        cg.tonemapper.Override(Tonemapper.ACES);
        cg.postExposure.Override(0.1f);
        cg.temperature.Override(-6f);
        cg.tint.Override(3f);
        cg.saturation.Override(-18f);
        cg.contrast.Override(14f);
        cg.lift.Override(new Vector4(0.98f, 1f, 1.02f, 0f));
        cg.gain.Override(new Vector4(1.03f, 1.01f, 0.97f, 0f));

        Bloom bloom = Add<Bloom>(p);
        bloom.intensity.Override(1.2f);
        bloom.threshold.Override(1.1f);
        bloom.softKnee.Override(0.6f);

        AmbientOcclusion ao = Add<AmbientOcclusion>(p);
        // Scalable AO works without compute shaders, so it also runs in WebGL.
        ao.mode.Override(AmbientOcclusionMode.ScalableAmbientObscurance);
        ao.intensity.Override(0.7f);
        ao.radius.Override(0.35f);

        Vignette v = Add<Vignette>(p);
        v.intensity.Override(0.32f);
        v.smoothness.Override(0.45f);

        Grain g = Add<Grain>(p);
        g.colored.Override(false);
        g.intensity.Override(0.18f);
        g.size.Override(0.8f);

        EditorUtility.SetDirty(p);
        return p;
    }

    private static T Add<T>(PostProcessProfile p) where T : PostProcessEffectSettings {
        T s = p.AddSettings<T>();
        s.enabled.Override(true);
        s.name = typeof(T).Name;
        s.hideFlags = HideFlags.HideInInspector | HideFlags.HideInHierarchy;
        AssetDatabase.AddObjectToAsset(s, p);
        return s;
    }

    private static void AddPostLayer(Camera cam) {
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
    }

    // ------------------------------------------------------------------ player prefabs

    private static void UpdatePlayerPrefabs() {
        AudioClip reload = AssetDatabase.LoadAssetAtPath<AudioClip>(Art + "Audio/Rifle_Reload.wav");
        AudioClip dry = AssetDatabase.LoadAssetAtPath<AudioClip>(Art + "Audio/Dry_Fire.wav");
        AudioClip[] steps = Enumerable.Range(0, 5)
            .Select(i => AssetDatabase.LoadAssetAtPath<AudioClip>(Art + "Audio/Footstep_Concrete_" + i + ".wav"))
            .Where(c => c != null).ToArray();

        foreach (string path in PlayerPrefabs) {
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            foreach (MonoBehaviour mb in root.GetComponentsInChildren<MonoBehaviour>(true)) {
                if (mb == null) continue;
                string type = mb.GetType().Name;
                SerializedObject so = new SerializedObject(mb);
                if (type == "FpsGun") {
                    so.FindProperty("reloadClip").objectReferenceValue = reload;
                    so.FindProperty("dryFireClip").objectReferenceValue = dry;
                    so.ApplyModifiedPropertiesWithoutUndo();
                } else if (type == "FirstPersonController" && steps.Length > 0) {
                    SerializedProperty arr = so.FindProperty("m_FootstepSounds");
                    arr.arraySize = steps.Length;
                    for (int i = 0; i < steps.Length; i++) arr.GetArrayElementAtIndex(i).objectReferenceValue = steps[i];
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
            }
            foreach (Camera cam in root.GetComponentsInChildren<Camera>(true)) {
                if (cam.CompareTag("MainCamera")) AddPostLayer(cam);
            }
            PrefabUtility.SaveAsPrefabAsset(root, path);
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    // ------------------------------------------------------------------ scene

    private static void UpdateScene(Dictionary<string, GameObject> props, PostProcessProfile profile) {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        // Sky, ambient and fog.
        Material sky = AssetDatabase.LoadAssetAtPath<Material>(Art + "Sky/OvercastSky.mat");
        if (sky == null) {
            sky = new Material(Shader.Find("Skybox/Panoramic"));
            AssetDatabase.CreateAsset(sky, Art + "Sky/OvercastSky.mat");
        }
        sky.SetTexture("_MainTex", AssetDatabase.LoadAssetAtPath<Texture>(Art + "Sky/kloofendal_overcast_2k.hdr"));
        sky.SetFloat("_Exposure", 0.85f);
        sky.SetFloat("_Rotation", 40f);
        EditorUtility.SetDirty(sky);
        RenderSettings.skybox = sky;
        RenderSettings.ambientMode = AmbientMode.Skybox;
        RenderSettings.ambientIntensity = 1.0f;
        RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
        RenderSettings.reflectionIntensity = 0.8f;
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogDensity = 0.0065f;
        RenderSettings.fogColor = new Color(0.58f, 0.61f, 0.63f);

        foreach (Light l in Object.FindObjectsOfType<Light>()) {
            if (l.type != LightType.Directional) continue;
            l.color = new Color(0.93f, 0.92f, 0.88f);
            l.intensity = 0.95f;
            l.shadows = LightShadows.Soft;
            l.shadowStrength = 0.7f;
            RenderSettings.sun = l;
        }

        // Post-processing volume + layer on the lobby camera.
        GameObject volGo = GameObject.Find("Post Process Volume");
        if (volGo == null) volGo = new GameObject("Post Process Volume");
        volGo.layer = LayerMask.NameToLayer("Default");
        PostProcessVolume vol = volGo.GetComponent<PostProcessVolume>();
        if (vol == null) vol = volGo.AddComponent<PostProcessVolume>();
        vol.isGlobal = true;
        vol.sharedProfile = profile;
        vol.priority = 1;
        foreach (Camera cam in Object.FindObjectsOfType<Camera>()) AddPostLayer(cam);

        PlaceProps(props);

        DynamicGI.UpdateEnvironment();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
    }

    private static void PlaceProps(Dictionary<string, GameObject> props) {
        GameObject old = GameObject.Find(PropsRootName);
        if (old != null) Object.DestroyImmediate(old);
        if (props.Count == 0) return;
        Transform root = new GameObject(PropsRootName).transform;

        List<Transform> spawns = new List<Transform>();
        foreach (MonoBehaviour mb in Object.FindObjectsOfType<MonoBehaviour>()) {
            if (mb.GetType().Name != "GameNetwork") continue;
            SerializedProperty arr = new SerializedObject(mb).FindProperty("spawnPoints");
            for (int i = 0; i < arr.arraySize; i++) {
                Transform t = arr.GetArrayElementAtIndex(i).objectReferenceValue as Transform;
                if (t != null) spawns.Add(t);
            }
        }
        if (spawns.Count == 0) {
            Debug.LogWarning("[MilitaryLook] No spawn points found; props not placed.");
            return;
        }
        Physics.SyncTransforms();
        float groundY = spawns.Select(s => s.position.y).OrderBy(y => y).ElementAt(spawns.Count / 2);
        Bounds area = new Bounds(spawns[0].position, Vector3.zero);
        foreach (Transform s in spawns) area.Encapsulate(s.position);
        area.Expand(new Vector3(30f, 0f, 30f));

        System.Random rng = new System.Random(1944);
        List<Vector3> placed = new List<Vector3>();
        int count = 0;

        // A few pieces of cover near every spawn, then scatter across the yard.
        foreach (Transform s in spawns) {
            int here = 0;
            for (int attempt = 0; attempt < 40 && here < 3; attempt++) {
                float ang = (float)(rng.NextDouble() * Mathf.PI * 2);
                float r = 3.5f + (float)rng.NextDouble() * 6f;
                Vector3 p = s.position + new Vector3(Mathf.Cos(ang), 0, Mathf.Sin(ang)) * r;
                if (TryPlace(Pick(rng), props, p, s.position.y, spawns, placed, root, rng)) { here++; count++; }
            }
        }
        for (int attempt = 0; attempt < 400 && count < spawns.Count * 3 + 28; attempt++) {
            Vector3 p = new Vector3(Mathf.Lerp(area.min.x, area.max.x, (float)rng.NextDouble()), groundY,
                                    Mathf.Lerp(area.min.z, area.max.z, (float)rng.NextDouble()));
            if (TryPlace(Pick(rng), props, p, groundY, spawns, placed, root, rng)) count++;
        }
        Debug.Log("[MilitaryLook] Placed " + count + " props around " + spawns.Count + " spawn points.");
    }

    private static string Pick(System.Random rng) {
        float x = (float)rng.NextDouble(), acc = 0f;
        foreach (PropDef d in Props) {
            acc += d.Weight;
            if (x <= acc) return d.Name;
        }
        return Props[0].Name;
    }

    private static bool TryPlace(string name, Dictionary<string, GameObject> props, Vector3 p, float expectY,
                                 List<Transform> spawns, List<Vector3> placed, Transform root, System.Random rng) {
        if (!props.TryGetValue(name, out GameObject prefab)) return false;
        if (!Physics.Raycast(p + Vector3.up * 12f, Vector3.down, out RaycastHit hit, 30f, ~0, QueryTriggerInteraction.Ignore)) return false;
        if (Mathf.Abs(hit.point.y - expectY) > 1.5f || hit.normal.y < 0.9f) return false;
        foreach (Transform s in spawns) if (Vector3.Distance(Flat(s.position), Flat(hit.point)) < 2.5f) return false;
        foreach (Vector3 q in placed) if (Vector3.Distance(Flat(q), Flat(hit.point)) < 1.6f) return false;

        BoxCollider col = prefab.GetComponent<BoxCollider>();
        Quaternion rot = Quaternion.Euler(0, rng.Next(0, 24) * 15f, 0);
        Vector3 half = col.size * 0.5f + new Vector3(0.15f, -0.05f, 0.15f);
        Vector3 center = hit.point + Vector3.up * (col.size.y * 0.5f + 0.06f);
        if (Physics.CheckBox(center, half, rot, ~0, QueryTriggerInteraction.Ignore)) return false;
        // Keep a clear line overhead so nothing spawns inside a building or under a stair.
        if (Physics.Raycast(center, Vector3.up, 4f, ~0, QueryTriggerInteraction.Ignore)
            && hit.point.y < expectY - 0.5f) return false;

        GameObject go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, root);
        go.transform.SetPositionAndRotation(hit.point, rot);
        placed.Add(hit.point);
        if (name == "wooden_military_crate" && props.TryGetValue("ammo_box", out GameObject ammo)) {
            int stack = rng.Next(0, 3);
            for (int i = 0; i < stack; i++) {
                Vector3 local = new Vector3((i - 0.5f) * col.size.x * 0.4f, col.size.y, 0f);
                GameObject box = (GameObject)PrefabUtility.InstantiatePrefab(ammo, go.transform);
                box.transform.localPosition = local;
                box.transform.localRotation = Quaternion.Euler(0, 90f + rng.Next(-2, 3) * 8f, 0);
            }
        }
        Physics.SyncTransforms();
        return true;
    }

    private static Vector3 Flat(Vector3 v) {
        return new Vector3(v.x, 0, v.z);
    }

}
