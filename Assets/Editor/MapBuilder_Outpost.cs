using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;
using Batcher = MapKit.Batcher;
using Opening = MapKit.Batcher.Opening;
using Surf = MapKit.Surf;

/// <summary>
/// Builds the "Outpost" map (id "outpost"): a fortified forward operating base, 68 x 68 m inside blast walls and HESCO,
/// with a two-storey command post in the middle, two guard towers, a container yard, sandbag positions and a vehicle
/// checkpoint. Everything is generated from CC0 textures and models, so the prefab can be rebuilt at any time:
///   Unity -batchmode -quit -projectPath . -executeMethod MapBuilder_Outpost.Build
///   Unity -batchmode -projectPath . -executeMethod MapBuilder_Outpost.BuildAndShoot -shotDir /some/dir   (needs a GPU context)
/// The layout is built for one half of the map and mirrored by 180 degrees about the centre, so both teams get the same
/// ground. Origin is the map centre, ground at y = 0, +z is "north" (team B), -z is "south" (team A).
/// </summary>
public static class MapBuilder_Outpost {

    private const string Dir = "Assets/Maps/Outpost";
    private const string Art = "Assets/Art/Military/";
    private const string PrefabPath = Dir + "/Outpost.prefab";
    private const string ScenePath = Dir + "/Outpost_Preview.unity";
    private const string MeshPath = Dir + "/Meshes/Outpost_Meshes.asset";
    private const string PropMeshPath = Dir + "/Meshes/Outpost_PropMeshes.asset";
    private const string MatDir = Dir + "/Materials";

    // Surfaces (material + texture scale), created by MakeSurfaces.
    private static Surf Ground, Hills, Gravel, Asphalt, Floor, TWall, Plaster, ContOlive, ContSand, ContGrey, Roof, Hesco,
                        SandA, SandB, Wood, WoodDark, Fence, Steel, Canvas;

    private static MapKit.MeshStore meshes, propMeshes;
    private static Dictionary<string, GameObject> props;
    private static Dictionary<string, Vector3> propSize;

    private struct Rect2 {
        public string Name;
        public float X0, Z0, X1, Z1;
    }
    private static List<Rect2> lint;

    private static Vector3 V(float x, float y, float z) { return new Vector3(x, y, z); }

    // ------------------------------------------------------------------ entry points

    [MenuItem("Tools/FPS/Maps/Build Outpost")]
    public static void Build() {
        Directory.CreateDirectory(Dir + "/Meshes");
        Directory.CreateDirectory(MatDir);
        AssetDatabase.Refresh();
        MapKit.ConfigureTextures(Dir);
        foreach (string guid in AssetDatabase.FindAssets("t:Model", new[] { Dir })) MapKit.ConfigureModel(AssetDatabase.GUIDToAssetPath(guid));
        AssetDatabase.Refresh();

        MakeSurfaces();
        MakeProps();

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        meshes = new MapKit.MeshStore(MeshPath);
        lint = new List<Rect2>();

        GameObject root = new GameObject("Outpost");
        Transform geo = MapKit.Group(root.transform, "Geometry");

        Batcher ground = NewBatcher(geo, "Ground");
        BuildGround(ground);
        Batcher perimeter = NewBatcher(geo, "Perimeter");
        BuildPerimeter(perimeter);
        Batcher post = NewBatcher(geo, "CommandPost");
        BuildCommandPost(post);
        Batcher towers = NewBatcher(geo, "Towers");
        Tower(towers);
        Batcher yard = NewBatcher(geo, "ContainerYard");
        BuildYard(yard);
        Batcher west = NewBatcher(geo, "WestLane");
        BuildWestLane(west);
        Batcher center = NewBatcher(geo, "Checkpoint");
        BuildCenter(center);
        Batcher decor = NewBatcher(root.transform, "Decor", true);
        BuildDecor(decor);
        Batcher backdrop = NewBatcher(root.transform, "Backdrop", true);
        BuildBackdrop(backdrop);
        foreach (Batcher b in new[] { ground, perimeter, post, towers, yard, west, center, decor, backdrop }) b.Flush();

        Transform boundary = BuildBoundary(root.transform);
        Light sun = BuildSun(root.transform);

        Physics.SyncTransforms();
        MapDefinition def = root.AddComponent<MapDefinition>();
        BuildSpawns(root.transform, def);
        SetPlayArea(def);
        ConfigureDefinition(def, sun);
        BakeReflection(root.transform, def);

        meshes.Save();
        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        Object.DestroyImmediate(root);
        ReportLayout(ground, perimeter, post, towers, yard, west, center);

        // Check the saved asset, not the in-memory build.
        GameObject inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        MapChecks.Report report = MapChecks.Run(inst, inst.GetComponent<MapDefinition>(), new Bounds(new Vector3(0f, 3f, 0f), new Vector3(68f, 10f, 68f)), MustReach());
        Object.DestroyImmediate(inst);

        BuildPreviewScene(prefab);
        AssetDatabase.SaveAssets();
        Debug.Log("[MapBuilder_Outpost] Built " + PrefabPath + (report.Failures == 0 ? " (all checks passed)" : " (" + report.Failures + " CHECKS FAILED)"));
    }

    [MenuItem("Tools/FPS/Maps/Outpost Screenshots")]
    public static void Screenshots() {
        string outDir = ShotDir();
        Directory.CreateDirectory(outDir);
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        Camera cam = GameObject.Find("PreviewCamera").GetComponent<Camera>();
        MapDefinition def = Object.FindObjectOfType<MapDefinition>();
        def.ApplyEnvironment();
        Physics.SyncTransforms();

        Transform A(int i) { return def.teamASpawns[Mathf.Min(i, def.teamASpawns.Length - 1)]; }
        Transform B(int i) { return def.teamBSpawns[Mathf.Min(i, def.teamBSpawns.Length - 1)]; }
        Transform F(int i) { return def.spawnPoints[Mathf.Min(i, def.spawnPoints.Length - 1)]; }

        Overview(cam, outDir, "01_overview_sw", V(-64f, 52f, -64f), V(0f, 0f, 0f), 55f);
        Overview(cam, outDir, "02_overview_se", V(66f, 44f, -58f), V(0f, 0f, 0f), 55f);
        Overview(cam, outDir, "03_overview_topdown", V(0f, 98f, -8f), V(0f, 0f, 0f), 52f);
        EyeLevel(cam, outDir, "04_teamA_spawn", A(2), 0f);
        EyeLevel(cam, outDir, "05_teamB_spawn", B(2), 0f);
        EyeLevel(cam, outDir, "06_ffa_west_lane", F(0), 0f);
        EyeLevel(cam, outDir, "07_ffa_container_yard", F(4), 0f);
        Look(cam, outDir, "08_tower_deck", V(-30.5f, 5.6f, -11.8f), V(-28f, 4.6f, 4f), 70f);
        Look(cam, outDir, "09_command_post_interior", V(-5.2f, 1.66f, -3.9f), V(-3.5f, 1.3f, 4f), 70f);
        Look(cam, outDir, "10_upper_floor", V(-6.2f, 4.95f, 0.9f), V(2f, 4.6f, 0.4f), 70f);
        Look(cam, outDir, "11_staging_props", V(7.5f, 1.6f, -29f), V(-2.5f, 0.6f, -23.5f), 70f);
        Look(cam, outDir, "12_sandbag_nest", V(-29.4f, 1.6f, -18.5f), V(-29.4f, 0.7f, -26.5f), 70f);
        Look(cam, outDir, "13_tower_outside", V(-23f, 1.7f, -4f), V(-30.4f, 3.4f, -11f), 65f);
        Look(cam, outDir, "14_yard_roof_ramp", V(19f, 1.6f, -8f), V(25.4f, 1.5f, -20f), 70f);
        Look(cam, outDir, "15_gate", V(0f, 1.6f, -22f), V(0f, 2.4f, -34f), 70f);
        Debug.Log("[MapBuilder_Outpost] Screenshots written to " + outDir);
    }

    public static void BuildAndShoot() {
        Build();
        Screenshots();
    }

    private static string ShotDir() {
        string[] args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "-shotDir") return args[i + 1];
        return Path.GetFullPath(Path.Combine(Application.dataPath, "../Temp/OutpostShots"));
    }

    private static void Overview(Camera cam, string dir, string name, Vector3 pos, Vector3 look, float fov) {
        cam.transform.position = pos;
        cam.transform.LookAt(look, name.Contains("topdown") ? Vector3.forward : Vector3.up);
        cam.fieldOfView = fov;
        MapChecks.Shoot(cam, Path.Combine(dir, name + ".png"));
    }

    private static void Look(Camera cam, string dir, string name, Vector3 pos, Vector3 target, float fov) {
        cam.transform.position = pos;
        cam.transform.LookAt(target);
        cam.fieldOfView = fov;
        MapChecks.Shoot(cam, Path.Combine(dir, name + ".png"));
    }

    private static void EyeLevel(Camera cam, string dir, string name, Transform spawn, float pitch) {
        cam.transform.position = spawn.position + Vector3.up * 1.4f;       // spawns float 0.2 m above the floor, eyes at 1.6 m
        cam.transform.rotation = Quaternion.Euler(pitch, spawn.eulerAngles.y, 0f);
        cam.fieldOfView = 70f;
        MapChecks.Shoot(cam, Path.Combine(dir, name + ".png"));
    }

    private static Vector3[] MustReach() {
        // Spots a player is meant to get to: tower decks, both balconies, upper floor, a container roof (and their mirror images).
        List<Vector3> pts = new List<Vector3> {
            V(-29.0f, 4.0f, -12.2f), V(-5f, 3.36f, -2f), V(-4f, 3.36f, 2.5f), V(8.7f, 3.36f, 0.5f), V(25.4f, 2.6f, -10.5f),
        };
        int n = pts.Count;
        for (int i = 0; i < n; i++) pts.Add(new Vector3(-pts[i].x, pts[i].y, -pts[i].z));
        return pts.ToArray();
    }

    private static Batcher NewBatcher(Transform parent, string name, bool decor = false) {
        Batcher b = new Batcher(parent, name, meshes, decor);
        b.PropLoader = n => props.TryGetValue(n, out GameObject g) ? g : null;
        return b;
    }

    // ------------------------------------------------------------------ surfaces

    private static void MakeSurfaces() {
        string T = Dir + "/Textures/";
        string A = Art + "Textures/";
        Ground = MapKit.MakeSurf(MatDir, "Ground", 2.4f, new Color(0.9f, 0.84f, 0.72f), T + "Gravel032/Gravel032_Albedo.jpg",
            T + "Gravel032/Gravel032_Normal.jpg", T + "Gravel032/Gravel032_MetallicSmoothness.png");
        MapKit.AddDetail(Ground, T + "Generated/DirtVariation.jpg", 2.4f / 28f);
        Hills = MapKit.MakeSurf(MatDir, "Hills", 4f, new Color(0.88f, 0.84f, 0.72f), T + "Gravel032/Gravel032_Albedo.jpg",
            T + "Gravel032/Gravel032_Normal.jpg", T + "Gravel032/Gravel032_MetallicSmoothness.png");
        MapKit.AddDetail(Hills, T + "Generated/HillsVariation.jpg", 4f / 160f);
        Gravel = MapKit.MakeSurf(MatDir, "Gravel", 1.8f, new Color(0.95f, 0.93f, 0.88f), T + "Generated/GravelLight_Albedo.jpg",
            T + "Gravel043/Gravel043_Normal.jpg", T + "Gravel043/Gravel043_MetallicSmoothness.png");
        Asphalt = MapKit.MakeSurf(MatDir, "Asphalt", 3f, new Color(0.9f, 0.9f, 0.9f), A + "Asphalt012/Asphalt012_Albedo.jpg",
            A + "Asphalt012/Asphalt012_Normal.jpg", A + "Asphalt012/Asphalt012_MetallicSmoothness.png");
        Floor = MapKit.MakeSurf(MatDir, "Floor", 2.5f, new Color(1f, 0.94f, 0.84f), T + "Generated/FloorLight_Albedo.jpg",
            A + "Concrete015/Concrete015_Normal.jpg", A + "Concrete015/Concrete015_MetallicSmoothness.png");
        TWall = MapKit.MakeSurf(MatDir, "TWall", 2.4f, new Color(0.96f, 0.94f, 0.9f), T + "Generated/ConcreteLight_Albedo.jpg",
            T + "Concrete031/Concrete031_Normal.jpg", T + "Concrete031/Concrete031_MetallicSmoothness.png", T + "Concrete031/Concrete031_AO.jpg");
        Plaster = MapKit.MakeSurf(MatDir, "Plaster", 1.6f, new Color(0.92f, 0.84f, 0.7f), T + "Concrete034/Concrete034_Albedo.jpg",
            T + "Concrete034/Concrete034_Normal.jpg", T + "Concrete034/Concrete034_MetallicSmoothness.png");
        string cs = T + "CorrugatedSteel006A/CorrugatedSteel006A_";
        ContOlive = MapKit.MakeSurf(MatDir, "ContOlive", 1.2f, Color.white, T + "Generated/Container_Olive_Albedo.jpg", cs + "Normal.jpg", cs + "MetallicSmoothness.png", cs + "AO.jpg");
        ContSand = MapKit.MakeSurf(MatDir, "ContSand", 1.2f, Color.white, T + "Generated/Container_Sand_Albedo.jpg", cs + "Normal.jpg", cs + "MetallicSmoothness.png", cs + "AO.jpg");
        ContGrey = MapKit.MakeSurf(MatDir, "ContGrey", 1.2f, Color.white, T + "Generated/Container_Grey_Albedo.jpg", cs + "Normal.jpg", cs + "MetallicSmoothness.png", cs + "AO.jpg");
        Roof = MapKit.MakeSurf(MatDir, "Roof", 1.2f, new Color(0.72f, 0.74f, 0.7f), A + "CorrugatedSteel005/CorrugatedSteel005_Albedo.jpg",
            A + "CorrugatedSteel005/CorrugatedSteel005_Normal.jpg", T + "Generated/Roof_MS.png");
        Hesco = MapKit.MakeSurf(MatDir, "Hesco", 0.8f, Color.white, T + "Generated/Hesco_Albedo.jpg", T + "Generated/Hesco_Normal.jpg", null, null, 0.08f);
        SandA = MapKit.MakeSurf(MatDir, "SandbagA", 0.5f, Color.white, T + "Generated/Sandbag_Albedo.jpg", null, null, null, 0.1f);
        SandB = MapKit.MakeSurf(MatDir, "SandbagB", 0.5f, new Color(0.8f, 0.76f, 0.66f), T + "Generated/Sandbag_Albedo.jpg", null, null, null, 0.1f);
        Wood = MapKit.MakeSurf(MatDir, "Wood", 1f, new Color(1f, 0.98f, 0.94f), T + "Generated/WoodLight_Albedo.jpg",
            T + "Planks012/Planks012_Normal.jpg", T + "Planks012/Planks012_MetallicSmoothness.png");
        WoodDark = MapKit.MakeSurf(MatDir, "WoodDark", 1f, new Color(0.68f, 0.64f, 0.6f), T + "Generated/WoodLight_Albedo.jpg",
            T + "Planks012/Planks012_Normal.jpg", T + "Planks012/Planks012_MetallicSmoothness.png");
        Fence = MapKit.MakeCutout(MatDir, "Fence", 2.4f, new Color(0.8f, 0.82f, 0.8f), T + "Fence003/Fence003_Albedo.png", T + "Fence003/Fence003_Normal.jpg", 0.5f);
        Steel = MapKit.MakeSurf(MatDir, "Steel", 1f, new Color(0.36f, 0.4f, 0.34f), null, null, null, null, 0.4f);
        Steel.Mat.SetFloat("_Metallic", 0.25f);
        Fence.Mat.SetFloat("_Metallic", 0.15f);
        Canvas = MapKit.MakeSurf(MatDir, "Canvas", 0.6f, new Color(0.5f, 0.52f, 0.36f), T + "Fabric066/Fabric066_Albedo.jpg", T + "Fabric066/Fabric066_Normal.jpg", null, null, 0.05f);
        AssetDatabase.SaveAssets();
    }

    // ------------------------------------------------------------------ props (model prefabs with colliders)

    private static void MakeProps() {
        props = new Dictionary<string, GameObject>();
        propSize = new Dictionary<string, Vector3>();
        propMeshes = new MapKit.MeshStore(PropMeshPath);
        string P = Dir + "/Props/";
        // The Poly Haven scans are 6 to 26k triangles each; they are cut down to a budget below (a crate is seen at a few metres).
        BuildProp("crate_mil", P + "old_military_crate/old_military_crate.fbx", PropMat(P + "old_military_crate", "old_military_crate"), "Wood", "box", 1.0f, 2200);
        BuildProp("jerrycan", P + "metal_jerrycan_green/metal_jerrycan_green.fbx", PropMat(P + "metal_jerrycan_green", "metal_jerrycan_green"), "Metal", "box", 0.52f, 900);
        BuildProp("burn_barrel", P + "barrel_stove/barrel_stove.fbx", PropMat(P + "barrel_stove", "barrel_stove"), "Metal", "capsule", 0.92f, 1400);
        BuildProp("tyre", P + "old_tyre/old_tyre.fbx", PropMat(P + "old_tyre", "old_tyre"), "Dirt", "box", 0.9f, 1100, true);
        BuildProp("generator", P + "portable_generator/portable_generator.fbx", PropMat(P + "portable_generator", "portable_generator"), "Metal", "box", 0.95f, 2600);
        BuildProp("tarp_car", P + "covered_car/covered_car.fbx", PropMat(P + "covered_car", "covered_car"), "Metal", "box", 4.5f, 2600);
        BuildProp("utility_box", P + "utility_box_02/utility_box_02.fbx", PropMat(P + "utility_box_02", "utility_box_02"), "Metal", "box", 1.3f, 1300);
        // The first map's CC0 props, rebuilt the same way (their own prefabs carry full-detail meshes).
        BuildExisting("barrel", "Barrel_01", "Metal", "capsule", 1200);
        BuildExisting("crate_wood", "wooden_military_crate", "Wood", "box", 1500);
        BuildExisting("ammo_box", "ammo_box", "Metal", "box", 700);
        BuildExisting("jersey", "concrete_road_barrier", "Concrete", "box", 1100);
        propMeshes.Save();
        foreach (KeyValuePair<string, Vector3> kv in propSize) Debug.Log("[MapBuilder_Outpost] prop " + kv.Key + " size " + kv.Value.ToString("F2"));
    }

    private static void BuildExisting(string name, string folder, string tag, string kind, int maxTris) {
        string dir = Art + "Props/" + folder + "/";
        GameObject src = AssetDatabase.LoadAssetAtPath<GameObject>(dir + folder + ".prefab");
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(dir + folder + ".mat");
        if (folder == "ammo_box") {
            // The shipped map is metal 1 / smoothness 0, which renders flat black without a sky reflection.
            mat = MapKit.MakeSurf(MatDir, "AmmoBox", 1f, Color.white, dir + "ammo_box_Albedo.jpg", dir + "ammo_box_Normal.png", Dir + "/Textures/Generated/AmmoBox_MS.png").Mat;
        }
        BoxCollider bc = src != null ? src.GetComponent<BoxCollider>() : null;
        if (src == null || mat == null || bc == null) {
            Debug.LogWarning("[MapBuilder_Outpost] Existing prop " + folder + " is not usable");
            return;
        }
        BuildProp(name, dir + folder + ".fbx", mat, tag, kind, Mathf.Max(bc.size.x, Mathf.Max(bc.size.y, bc.size.z)), maxTris);
    }

    private static Material PropMat(string folder, string name) {
        return MapKit.MakeSurf(folder, name, 1f, Color.white, folder + "/" + name + "_Albedo.jpg", folder + "/" + name + "_Normal.jpg",
            folder + "/" + name + "_MetallicSmoothness.png", folder + "/" + name + "_AO.jpg").Mat;
    }

    private static Bounds RenderBounds(GameObject go) {
        Renderer[] rs = go.GetComponentsInChildren<Renderer>();
        Bounds b = rs[0].bounds;
        foreach (Renderer r in rs) b.Encapsulate(r.bounds);
        return b;
    }

    /// <summary>Instantiate a model, cut it to a triangle budget, scale it so its longest side is `fit` metres, stand it on the origin, add a collider.</summary>
    private static void BuildProp(string name, string modelPath, Material mat, string tag, string kind, float fit, int maxTris, bool lieFlat = false) {
        GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
        if (model == null || mat == null) {
            Debug.LogWarning("[MapBuilder_Outpost] Missing model or material for prop " + name);
            return;
        }
        int shootable = LayerMask.NameToLayer(MapKit.ShootableLayer);
        GameObject root = new GameObject(name);
        GameObject inst = (GameObject)PrefabUtility.InstantiatePrefab(model);
        PrefabUtility.UnpackPrefabInstance(inst, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
        inst.transform.SetParent(root.transform, false);

        MeshFilter[] filters = inst.GetComponentsInChildren<MeshFilter>();
        int before = filters.Sum(f => MapKit.TriCount(f.sharedMesh)), index = 0;
        foreach (MeshFilter f in filters) {
            int tris = MapKit.TriCount(f.sharedMesh);
            Renderer r = f.GetComponent<Renderer>();
            if (before > maxTris) {
                Mesh lod = propMeshes.Get("prop_" + name + "_" + index++);
                MapKit.Simplify(f.sharedMesh, Mathf.Max(60, Mathf.RoundToInt(maxTris * (float)tris / before)), lod);
                f.sharedMesh = lod;
                r.sharedMaterials = new[] { mat };
            } else {
                r.sharedMaterials = Enumerable.Repeat(mat, r.sharedMaterials.Length).ToArray();
            }
        }

        Bounds b = RenderBounds(inst);
        if (lieFlat) {
            // Tyres: put the thinnest side upright.
            Vector3 s = b.size;
            if (s.z < s.x && s.z < s.y) inst.transform.Rotate(90f, 0f, 0f, Space.World);
            else if (s.x < s.y && s.x < s.z) inst.transform.Rotate(0f, 0f, 90f, Space.World);
            b = RenderBounds(inst);
        }
        inst.transform.localScale *= fit / Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
        b = RenderBounds(inst);
        if (b.size.x > b.size.z * 1.05f && !lieFlat && kind == "box") {
            // Long things (the covered car) lie along local Z.
            inst.transform.Rotate(0f, 90f, 0f, Space.World);
            b = RenderBounds(inst);
        }
        inst.transform.position -= new Vector3(b.center.x, b.min.y, b.center.z);
        b = RenderBounds(inst);

        Vector3 size = b.size;
        if (kind == "capsule") {
            CapsuleCollider cc = root.AddComponent<CapsuleCollider>();
            cc.direction = 1;
            cc.radius = Mathf.Min(size.x, size.z) * 0.5f;
            cc.height = size.y;
            cc.center = new Vector3(0f, size.y * 0.5f, 0f);
        } else {
            BoxCollider bc = root.AddComponent<BoxCollider>();
            bc.center = new Vector3(0f, size.y * 0.5f, 0f);
            bc.size = size;
        }
        root.tag = tag;
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = shootable;
        root.isStatic = true;
        int after = root.GetComponentsInChildren<MeshFilter>().Sum(f => MapKit.TriCount(f.sharedMesh));
        Debug.Log("[MapBuilder_Outpost] prop " + name + " triangles " + before + " -> " + after);
        propMeshes.Save(false);                                      // the prefab must find its meshes on disk
        Directory.CreateDirectory(Dir + "/Props/Prefabs");
        props[name] = PrefabUtility.SaveAsPrefabAsset(root, Dir + "/Props/Prefabs/" + name + ".prefab");
        propSize[name] = size;
        Object.DestroyImmediate(root);
    }

    // ------------------------------------------------------------------ layout lint

    /// <summary>Remember a footprint (centre + half sizes, mirrored when the batcher is flipped) so overlaps can be reported.</summary>
    private static void Reserve(Batcher b, string name, Vector3 c, float hx, float hz) {
        if (b.Flip) { c.x = -c.x; c.z = -c.z; }
        lint.Add(new Rect2 { Name = name, X0 = c.x - hx, X1 = c.x + hx, Z0 = c.z - hz, Z1 = c.z + hz });
    }

    private static void ReportLayout(params Batcher[] batchers) {
        int overlaps = 0;
        for (int i = 0; i < lint.Count; i++) {
            for (int j = i + 1; j < lint.Count; j++) {
                Rect2 a = lint[i], b = lint[j];
                float ox = Mathf.Min(a.X1, b.X1) - Mathf.Max(a.X0, b.X0);
                float oz = Mathf.Min(a.Z1, b.Z1) - Mathf.Max(a.Z0, b.Z0);
                bool bags = a.Name == "sandbags" && b.Name == "sandbags";
                if (ox > (bags ? 0.4f : 0.1f) && oz > (bags ? 0.4f : 0.1f)) {
                    overlaps++;
                    Debug.LogWarning("[MapBuilder_Outpost] Layout overlap: " + a.Name + " and " + b.Name + " (" + ox.ToString("F2") + " x " + oz.ToString("F2") + " m)");
                }
            }
            Rect2 r = lint[i];
            if (r.X0 < -34.05f || r.X1 > 34.05f || r.Z0 < -34.05f || r.Z1 > 34.05f)
                Debug.LogWarning("[MapBuilder_Outpost] " + r.Name + " pokes through the perimeter: x " + r.X0.ToString("F1") + ".." + r.X1.ToString("F1") + " z " + r.Z0.ToString("F1") + ".." + r.Z1.ToString("F1"));
        }
        int boxes = batchers.Sum(x => x.Boxes), tris = batchers.Sum(x => x.Tris);
        Debug.Log("[MapBuilder_Outpost] Layout: " + lint.Count + " footprints, " + overlaps + " overlaps; " + boxes + " boxes, " + tris + " procedural quads/tris");
    }

    // ------------------------------------------------------------------ placement helpers

    private static void PutProp(Batcher b, string name, float x, float z, float yaw = 0f, float y = 0f, bool stacked = false) {
        b.Prop(name, V(x, y, z), yaw);
        if (stacked || !propSize.TryGetValue(name, out Vector3 s)) return;
        float r = Mathf.Max(s.x, s.z) * 0.5f;
        Reserve(b, "prop " + name, V(x, 0, z), r, r);
    }

    private static void TWallRun(Batcher b, float cx, float cz, float length, float height, bool alongX) {
        Vector3 size = alongX ? V(length, height, 0.8f) : V(0.8f, height, length);
        b.Box(TWall, "Concrete", V(cx, height * 0.5f, cz), size);
        Reserve(b, "t-wall", V(cx, 0, cz), size.x * 0.5f, size.z * 0.5f);
    }

    private static void HescoRun(Batcher b, float cx, float cz, float length, bool alongX, float height = 2.4f) {
        Vector3 size = alongX ? V(length, height, 1.0f) : V(1.0f, height, length);
        b.Box(Hesco, "Dirt", V(cx, height * 0.5f, cz), size, 0f, Ground);
        Reserve(b, "hesco", V(cx, 0, cz), size.x * 0.5f, size.z * 0.5f);
    }

    private static void Container(Batcher b, Surf s, float x, float z, float yaw = 0f, int level = 0) {
        Vector3 size = V(2.44f, 2.6f, 6.1f);
        Vector3 c = V(x, 1.3f + level * 2.6f, z);
        b.Box(s, "Metal", c, size, yaw);
        // Locking bars on the door end (a few centimetres proud of the wall, so no collider of their own).
        Quaternion q = Quaternion.Euler(0f, yaw, 0f);
        foreach (float dx in new[] { -0.42f, 0.42f }) b.Detail(Steel, "Metal", c + q * V(dx, 0f, 3.1f + 0.025f), V(0.07f, 2.3f, 0.05f), yaw);
        if (level == 0) {
            bool along = Mathf.Abs(Mathf.Sin(yaw * Mathf.Deg2Rad)) < 0.5f;
            Reserve(b, "container", V(x, 0, z), along ? 1.22f : 3.05f, along ? 3.05f : 1.22f);
        }
    }

    private static void Sandbags(Batcher b, Vector3 start, float yaw, int length, int courses) {
        b.SandbagWall(SandA, SandB, start, yaw, length, courses, 2);
        Quaternion q = Quaternion.Euler(0f, yaw, 0f);
        float len = length * 0.5f;
        Vector3 c = start + q * V(0f, 0f, len * 0.5f);
        bool along = Mathf.Abs(Mathf.Sin(yaw * Mathf.Deg2Rad)) < 0.5f;
        Reserve(b, "sandbags", c, along ? 0.3f : len * 0.5f, along ? len * 0.5f : 0.3f);
    }

    // ------------------------------------------------------------------ ground and pads

    private static void BuildGround(Batcher g) {
        g.Slab(Ground, "Dirt", -40f, 40f, -1f, 0f, -40f, 40f);
        g.Sym(() => {
            g.Slab(Asphalt, "Concrete", -26f, -21f, 0f, 0.04f, -34f, -3f);        // west lane service road
            g.Slab(Asphalt, "Concrete", -3.2f, 3.2f, 0f, 0.04f, -34f, -6.5f);     // from the gate to the command post
            g.Slab(Gravel, "Dirt", 17f, 34f, 0f, 0.03f, -34f, 0f);                // container yard hardstand
            g.Slab(Gravel, "Dirt", -15f, 15f, 0f, 0.03f, -23.5f, -8f);            // checkpoint area
        });
        g.Slab(Floor, "Concrete", -9f, 9f, 0f, 0.06f, -6.5f, 6.5f);               // command post apron and floor
    }

    // ------------------------------------------------------------------ perimeter

    private static void BuildPerimeter(Batcher b) {
        b.Sym(() => {
            // South blast wall with the main gate in the middle (the north wall is the mirror image).
            b.Slab(TWall, "Concrete", -35f, -3.9f, 0f, 3.6f, -35f, -34f);
            b.Slab(TWall, "Concrete", 3.9f, 35f, 0f, 3.6f, -35f, -34f);
            b.Slab(TWall, "Concrete", -3.9f, -3f, 0f, 4.8f, -35.3f, -33.7f);
            b.Slab(TWall, "Concrete", 3f, 3.9f, 0f, 4.8f, -35.3f, -33.7f);
            b.Slab(TWall, "Concrete", -3f, 3f, 4.2f, 4.8f, -35.3f, -33.7f);
            b.Slab(Steel, "Metal", -3f, 3f, 0f, 4.2f, -34.75f, -34.45f);            // closed gate leaf
            // West HESCO bastion line (east is the mirror image).
            b.Slab(Hesco, "Dirt", -35f, -34f, 0f, 2.4f, -34f, 34f, Ground);
        });
    }

    private static Transform BuildBoundary(Transform root) {
        Transform t = MapKit.Group(root, "Boundary");
        int ignore = LayerMask.NameToLayer("Ignore Raycast");
        // Invisible, movement only (not Shootable): the walls and fences carry the visuals, these just stop anyone
        // from vaulting from a tower onto the wall top.
        Vector3[] centers = { V(0f, 20f, -34.6f), V(0f, 20f, 34.6f), V(-34.6f, 20f, 0f), V(34.6f, 20f, 0f) };
        Vector3[] sizes = { V(72f, 40f, 1.4f), V(72f, 40f, 1.4f), V(1.4f, 40f, 72f), V(1.4f, 40f, 72f) };
        string[] names = { "South", "North", "West", "East" };
        for (int i = 0; i < 4; i++) {
            GameObject go = new GameObject("Boundary_" + names[i]);
            go.transform.SetParent(t, false);
            go.layer = ignore;
            BoxCollider bc = go.AddComponent<BoxCollider>();
            bc.center = centers[i];
            bc.size = sizes[i];
            go.isStatic = true;
        }
        return t;
    }

    // ------------------------------------------------------------------ command post

    private static void BuildCommandPost(Batcher b) {
        const float g = 0.06f, slabBot = 3.06f, f2 = 3.36f, roofBot = 6.36f, roofTop = 6.66f, parTop = 7.46f;
        Reserve(b, "command post", V(0f, 0f, 0f), 9.4f, 5.0f);
        b.Sym(() => {
            // ---- outer walls: south (north by symmetry) and east (west)
            b.WallX(Plaster, "Concrete", -8f, 8f, -4.8f, 0.4f, g, f2,
                new Opening(-3.8f, 1.4f, g, 2.4f), new Opening(-6.4f, 1.6f, 1.15f, 2.25f), new Opening(-1.0f, 1.6f, 1.15f, 2.25f),
                new Opening(2.0f, 1.6f, 1.15f, 2.25f), new Opening(5.0f, 1.6f, 1.15f, 2.25f));
            b.WallX(Plaster, "Concrete", -8f, 8f, -4.8f, 0.4f, f2, roofBot,
                new Opening(-6.0f, 1.6f, f2 + 1.1f, f2 + 2.2f), new Opening(-2.5f, 1.6f, f2 + 1.1f, f2 + 2.2f),
                new Opening(1.0f, 1.6f, f2 + 1.1f, f2 + 2.2f), new Opening(4.5f, 1.6f, f2 + 1.1f, f2 + 2.2f));
            b.WallZ(Plaster, "Concrete", -4.6f, 4.6f, 7.8f, 0.4f, g, f2, new Opening(3.0f, 1.4f, g, 2.4f));
            b.WallZ(Plaster, "Concrete", -4.6f, 4.6f, 7.8f, 0.4f, f2, roofBot,
                new Opening(0.5f, 1.4f, f2, f2 + 2.3f), new Opening(-3.4f, 1.6f, f2 + 1.1f, f2 + 2.2f), new Opening(3.4f, 1.6f, f2 + 1.1f, f2 + 2.2f));

            // ---- ground floor partitions: four rooms linked by doorways
            b.WallX(Plaster, "Concrete", -7.6f, 0f, 0f, 0.2f, g, slabBot, new Opening(-5f, 1.2f, g, 2.3f));
            b.WallZ(Plaster, "Concrete", -4.6f, 0f, 0f, 0.2f, g, slabBot, new Opening(-2.3f, 1.2f, g, 2.3f));
            // ---- upper floor: a central wall to fight around
            b.WallX(Plaster, "Concrete", -3f, 0f, 0f, 0.2f, f2, roofBot);

            // ---- first-floor slab with a stairwell (north-west; the south-east one is the mirror image)
            b.Slab(Floor, "Concrete", -7.6f, -2.5f, slabBot, f2, -4.6f, 3.4f, null, true, Plaster);
            b.Stairs(Floor, "Concrete", V(-7.54f, g, 4.0f), 90f, 18, (f2 - g) / 18f, 0.28f, 1.2f);
            b.Slab(Plaster, "Concrete", -7.6f, -2.5f, f2, f2 + 1.0f, 3.2f, 3.4f);       // balustrade round the stairwell

            // ---- roof parapet
            b.Slab(Plaster, "Concrete", -8f, 8f, roofTop, parTop, -5f, -4.7f);
            b.Slab(Plaster, "Concrete", 7.7f, 8f, roofTop, parTop, -4.7f, 4.7f);

            // ---- outside stairs up to a balcony on the east side (west by symmetry)
            b.Stairs(Floor, "Concrete", V(8.8f, 0f, -5.54f), 0f, 18, f2 / 18f, 0.28f, 1.2f);
            b.Slab(Floor, "Concrete", 8f, 9.4f, f2 - 0.2f, f2, -0.5f, 1.5f);
            b.Slab(Plaster, "Concrete", 9.25f, 9.4f, f2, f2 + 1.0f, -0.5f, 1.5f);
            b.Slab(Plaster, "Concrete", 8f, 9.4f, f2, f2 + 1.0f, 1.35f, 1.5f);
            b.Slab(Plaster, "Concrete", 9.1f, 9.35f, 0f, f2 - 0.2f, 1.15f, 1.4f);

            // ---- furniture: desks and cabinets to break up the rooms
            b.Box(Wood, "Wood", V(-6.4f, g + 0.4f, -3.0f), V(1.6f, 0.8f, 0.8f));
            b.Box(Steel, "Metal", V(-0.7f, g + 0.7f, -4.2f), V(0.5f, 1.4f, 0.6f));
            b.Box(Wood, "Wood", V(-3.4f, g + 0.4f, 1.3f), V(1.5f, 0.8f, 0.8f), 90f);
            b.Box(Wood, "Wood", V(-6.0f, f2 + 0.4f, -3.4f), V(1.6f, 0.8f, 0.8f));
            b.Box(Wood, "Wood", V(-1.4f, f2 + 0.4f, 3.2f), V(1.6f, 0.8f, 0.8f));
            b.Box(Steel, "Metal", V(-6.9f, f2 + 0.7f, 1.0f), V(0.5f, 1.4f, 0.6f));
        });
        // The centre of the slab and the roof (self-symmetric, built once).
        b.Slab(Floor, "Concrete", -2.5f, 2.5f, slabBot, f2, -4.6f, 4.6f, null, true, Plaster);
        b.Slab(Floor, "Concrete", -8f, 8f, roofBot, roofTop, -5f, 5f, null, true, Plaster);
        b.Prop("crate_mil", V(-2.6f, g, -3.6f), 20f);
        b.Prop("crate_mil", V(2.6f, g, 3.6f), 200f);
        b.Prop("ammo_box", V(-6.9f, f2, -1.2f), 80f);
        b.Prop("ammo_box", V(6.9f, f2, 1.2f), 260f);
    }

    // ------------------------------------------------------------------ guard towers

    private static void Tower(Batcher b) {
        b.Sym(() => {
            Reserve(b, "tower", V(-30.4f, 0f, -12f), 2.8f, 2f);
            Reserve(b, "tower stairs", V(-29f, 0f, -7.06f), 0.6f, 2.94f);
            foreach (float x in new[] { -32.6f, -30.4f, -28.2f }) {
                foreach (float z in new[] { -13.6f, -10.4f }) b.Box(WoodDark, "Wood", V(x, 1.9f, z), V(0.35f, 3.8f, 0.35f));
            }
            b.Box(WoodDark, "Wood", V(-30.4f, 1.7f, -13.6f), V(4.4f, 0.14f, 0.14f));
            b.Box(WoodDark, "Wood", V(-30.4f, 1.7f, -10.4f), V(4.4f, 0.14f, 0.14f));
            b.Box(WoodDark, "Wood", V(-32.6f, 1.7f, -12f), V(0.14f, 0.14f, 3.2f));
            b.Box(WoodDark, "Wood", V(-28.2f, 1.7f, -12f), V(0.14f, 0.14f, 3.2f));
            b.Slab(Wood, "Wood", -33f, -27.8f, 3.8f, 4f, -14f, -10f);                       // deck
            b.Slab(Wood, "Wood", -33f, -32.85f, 4f, 5.15f, -14f, -10f);                     // plank parapets
            b.Slab(Wood, "Wood", -33f, -27.8f, 4f, 5.15f, -14f, -13.85f);
            b.Slab(Wood, "Wood", -27.95f, -27.8f, 4f, 5.15f, -14f, -10f);
            b.Slab(Wood, "Wood", -33f, -30.2f, 4f, 5.15f, -10.15f, -10f);
            foreach (Vector2 p in new[] { new Vector2(-32.8f, -13.8f), new Vector2(-32.8f, -10.2f), new Vector2(-27.88f, -13.8f), new Vector2(-27.88f, -10.1f) })
                b.Box(WoodDark, "Wood", V(p.x, 5.15f, p.y), V(0.18f, 2.3f, 0.18f));
            b.Slab(Roof, "Metal", -33.5f, -27.3f, 6.3f, 6.45f, -14.5f, -9.5f);              // corrugated roof
            b.Stairs(Wood, "Wood", V(-29f, 0f, -4.12f), 180f, 21, 4f / 21f, 0.28f, 1.2f);
            b.Prop("generator", V(-30.5f, 4f, -12.4f), 25f);
        });
    }

    // ------------------------------------------------------------------ container yard (south-east; mirrored to the north-west)

    private static void BuildYard(Batcher b) {
        b.Sym(() => {
            Container(b, ContOlive, 19.7f, -29.5f);
            Container(b, ContSand, 19.7f, -17.0f);
            Container(b, ContOlive, 25.4f, -25.0f);
            Container(b, ContSand, 25.4f, -10.5f);
            Container(b, ContGrey, 31.1f, -29.5f);
            Container(b, ContGrey, 31.1f, -29.5f, 0f, 1);
            Container(b, ContOlive, 31.1f, -17.0f);
            Container(b, ContSand, 31.1f, -4.5f);
            Container(b, ContOlive, 22.0f, -4.0f, 90f);
            // Wooden ramp up to the roof of the container at (25.4, -10.5), with sandbags up there.
            b.Ramp(Wood, "Wood", V(25.4f, 0f, -18.4f), 0f, 4.85f, 1.8f, 2.6f);
            Reserve(b, "ramp", V(25.4f, 0f, -15.97f), 0.9f, 2.43f);
            b.SandbagWall(SandA, SandB, V(26.45f, 2.6f, -12.9f), 0f, 5, 4, 1);
            // Clutter in the corridors.
            PutProp(b, "barrel", 22.5f, -22.8f);
            PutProp(b, "barrel", 23.2f, -23.5f);
            PutProp(b, "burn_barrel", 22.4f, -12.0f);
            PutProp(b, "crate_mil", 28.3f, -23.0f, 25f);
            PutProp(b, "jerrycan", 28.9f, -23.7f, 80f);
            PutProp(b, "tyre", 28.0f, -14.4f, 10f);
            PutProp(b, "tyre", 28.1f, -14.4f, 70f, 0.22f, true);
            PutProp(b, "generator", 33.0f, -23.0f, 90f);
            PutProp(b, "utility_box", 17.9f, -22.0f, 90f);
        });
    }

    // ------------------------------------------------------------------ west lane, south half (mirrored to the east lane, north half)

    private static void BuildWestLane(Batcher b) {
        b.Sym(() => {
            // Sandbag firing position at the south end, facing north.
            Sandbags(b, V(-31.5f, 0f, -26.5f), 90f, 8, 6);
            Sandbags(b, V(-31.2f, 0f, -26.5f), 0f, 5, 6);
            Sandbags(b, V(-27.3f, 0f, -26.5f), 0f, 5, 6);
            // Blast walls staggered across the service road.
            TWallRun(b, -24.5f, -18f, 3.2f, 2.4f, true);
            TWallRun(b, -22.5f, -13f, 3.2f, 2.4f, true);
            TWallRun(b, -24.5f, -8f, 3.2f, 2.4f, true);
            // HESCO line between the west lane and the middle, with a gap in it.
            HescoRun(b, -17.5f, -28f, 10f, false);
            HescoRun(b, -17.5f, -14.5f, 11f, false);
            PutProp(b, "tarp_car", -23.5f, -30f, 0f);
            PutProp(b, "crate_mil", -29.0f, -21.5f, 15f);
            PutProp(b, "crate_mil", -29.0f, -21.5f, 40f, 0.5f, true);
            PutProp(b, "jerrycan", -30.2f, -21.8f, 40f);
            PutProp(b, "tyre", -32.3f, -17.0f, 0f);
            PutProp(b, "tyre", -32.3f, -17.0f, 50f, 0.22f, true);
            PutProp(b, "tyre", -32.3f, -17.0f, 110f, 0.44f, true);
            PutProp(b, "barrel", -32.6f, -7.6f);
            PutProp(b, "barrel", -31.8f, -8.3f);
            PutProp(b, "burn_barrel", -33.1f, -6.2f);
            PutProp(b, "utility_box", -33.4f, -2.6f, 90f);
        });
    }

    // ------------------------------------------------------------------ middle: staging area, checkpoint, booth, nest

    private static void BuildCenter(Batcher b) {
        b.Sym(() => {
            // Chicane of blast walls in front of the command post.
            TWallRun(b, -8.5f, -19.5f, 6f, 2.4f, true);
            TWallRun(b, -0.5f, -17f, 5f, 2.4f, true);
            TWallRun(b, 8.5f, -19.5f, 6f, 2.4f, true);

            // Guard booth: slab, four walls with a door and windows, flat roof.
            Reserve(b, "booth", V(-5f, 0f, -12f), 1.7f, 1.7f);
            b.Slab(Floor, "Concrete", -6.7f, -3.3f, 0f, 0.1f, -13.7f, -10.3f);
            b.WallX(Plaster, "Concrete", -6.5f, -3.5f, -13.375f, 0.25f, 0.1f, 2.9f, new Opening(-5f, 1.1f, 0.1f, 2.2f));
            b.WallX(Plaster, "Concrete", -6.5f, -3.5f, -10.625f, 0.25f, 0.1f, 2.9f, new Opening(-5f, 1.0f, 1.1f, 2.0f));
            b.WallZ(Plaster, "Concrete", -13.25f, -10.75f, -6.375f, 0.25f, 0.1f, 2.9f, new Opening(-12f, 1.0f, 1.1f, 2.0f));
            b.WallZ(Plaster, "Concrete", -13.25f, -10.75f, -3.625f, 0.25f, 0.1f, 2.9f, new Opening(-12f, 1.0f, 1.1f, 2.0f));
            b.Slab(Floor, "Concrete", -6.7f, -3.3f, 2.9f, 3.1f, -13.7f, -10.3f, null, true, Plaster);

            // Sandbag nest beside the command post.
            Sandbags(b, V(4.5f, 0f, -12.5f), 90f, 7, 6);
            Sandbags(b, V(4.6f, 0f, -12.5f), 0f, 4, 6);
            Sandbags(b, V(8.3f, 0f, -12.5f), 0f, 4, 6);

            // Tent behind the staging area.
            Reserve(b, "tent", V(-14.5f, 0f, -29f), 2.1f, 3.6f);
            b.Prism(Canvas, "Dirt", new[] { new Vector2(-2f, 0f), new Vector2(2f, 0f), new Vector2(2f, 1.5f), new Vector2(0f, 2.8f), new Vector2(-2f, 1.5f) },
                V(-14.5f, 0f, -29f), 0f, -3.5f, 3.5f);

            PutProp(b, "crate_mil", -3.5f, -24.0f, 10f);
            PutProp(b, "crate_mil", -2.2f, -24.4f, -15f);
            PutProp(b, "crate_mil", -3.0f, -24.2f, 60f, 0.62f, true);
            PutProp(b, "crate_wood", 2.5f, -23.5f, 20f);
            PutProp(b, "ammo_box", 2.5f, -23.5f, 110f, 0.7f, true);
            PutProp(b, "generator", 11.0f, -24.5f, 20f);
            PutProp(b, "jerrycan", 12.1f, -23.6f, 70f);
            PutProp(b, "barrel", -14.6f, -11.0f);
            PutProp(b, "barrel", -13.8f, -11.7f);
            PutProp(b, "burn_barrel", 14.0f, -8.5f);
            PutProp(b, "jersey", 12.5f, -15.0f, 90f);
            PutProp(b, "jersey", 12.5f, -11.8f, 90f);
        });
    }

    // ------------------------------------------------------------------ decor: fences on the walls (visual only)

    private static void FenceRun(Batcher d, float x0, float z0, float x1, float z1, float y0, float y1) {
        Vector3 a = V(x0, y0, z0), c = V(x1, y0, z1), up = V(0f, y1 - y0, 0f);
        d.Quad(Fence, "Metal", a, c, c + up, a + up);
        d.Quad(Fence, "Metal", a, a + up, c + up, c);
        int posts = Mathf.Max(2, Mathf.CeilToInt(Vector3.Distance(a, c) / 3f) + 1);
        for (int i = 0; i < posts; i++) {
            Vector3 p = Vector3.Lerp(a, c, i / (float)(posts - 1));
            d.Box(Steel, "Metal", V(p.x, (y0 + y1) * 0.5f + 0.15f, p.z), V(0.1f, y1 - y0 + 0.3f, 0.1f));
        }
    }

    private static void BuildDecor(Batcher d) {
        d.Sym(() => {
            // Chain-link on top of the south blast wall and the west HESCO line; the invisible boundary continues above.
            FenceRun(d, -34.8f, -34.5f, 34.8f, -34.5f, 3.6f, 5.6f);
            FenceRun(d, -34.5f, -33.8f, -34.5f, 33.8f, 2.4f, 5.6f);
        });
    }

    // ------------------------------------------------------------------ backdrop: hills round the base (visual only)

    private static float HillHeight(float x, float z) {
        float r = Mathf.Max(Mathf.Abs(x), Mathf.Abs(z));
        float t = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(40f, 190f, r));
        float n = Mathf.PerlinNoise(x * 0.009f + 11.3f, z * 0.009f + 5.7f) * 0.72f + Mathf.PerlinNoise(x * 0.035f + 3.1f, z * 0.035f + 8.3f) * 0.28f;
        return t * (2f + n * 30f);
    }

    private static void BuildBackdrop(Batcher d) {
        // 10 m cells from -260 to 260; the 80 x 80 m square under the base stays out (the ground slab covers it).
        d.Terrain(Hills, "Dirt", -260f, -260f, 10f, 52, 52, HillHeight, (x, z) => Mathf.Max(Mathf.Abs(x), Mathf.Abs(z)) < 40f);
    }

    // ------------------------------------------------------------------ lights, sun, spawns, definition

    private static Light BuildSun(Transform root) {
        GameObject go = new GameObject("Sun");
        go.transform.SetParent(root, false);
        go.transform.rotation = Quaternion.Euler(46f, -32f, 0f);
        Light l = go.AddComponent<Light>();
        l.type = LightType.Directional;
        l.color = new Color(1f, 0.95f, 0.86f);
        l.intensity = 1.15f;
        l.shadows = LightShadows.Soft;
        l.shadowStrength = 0.75f;
        l.lightmapBakeType = LightmapBakeType.Realtime;
        return l;
    }

    private static Transform Spawn(Transform parent, string name, Vector3 pos, Vector3 lookAt) {
        float floor = 0f;
        if (Physics.Raycast(new Vector3(pos.x, 6f, pos.z), Vector3.down, out RaycastHit hit, 12f, ~0, QueryTriggerInteraction.Ignore)) floor = hit.point.y;
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.position = new Vector3(pos.x, floor + 0.2f, pos.z);
        Vector3 d = lookAt - pos;
        d.y = 0f;
        go.transform.rotation = Quaternion.LookRotation(d.normalized);
        return go.transform;
    }

    private static void BuildSpawns(Transform root, MapDefinition def) {
        Transform spawns = MapKit.Group(root, "Spawns");
        Transform ffa = MapKit.Group(spawns, "FFA"), ta = MapKit.Group(spawns, "TeamA"), tb = MapKit.Group(spawns, "TeamB");

        // Free-for-all: six spots in the south half plus their mirror images, all looking at the middle.
        Vector3[] ffaBase = {
            V(-28.5f, 0f, -31f), V(-22.5f, 0f, -5f), V(-14f, 0f, -21.5f), V(12.5f, 0f, -26.5f), V(28.25f, 0f, -31f), V(22.5f, 0f, -11f),
        };
        List<Transform> list = new List<Transform>();
        int n = 0;
        foreach (Vector3 p in ffaBase) {
            list.Add(Spawn(ffa, "FFA_" + (++n).ToString("00"), p, Vector3.zero));
            list.Add(Spawn(ffa, "FFA_" + (++n).ToString("00"), new Vector3(-p.x, p.y, -p.z), Vector3.zero));
        }
        def.spawnPoints = list.ToArray();

        // Teams: A along the south wall facing north, B the mirror image on the north wall.
        Vector3[] team = { V(-10f, 0f, -30f), V(-5.5f, 0f, -28f), V(-1f, 0f, -31f), V(3.5f, 0f, -28.5f), V(8f, 0f, -31f), V(12f, 0f, -28f) };
        List<Transform> a = new List<Transform>(), bl = new List<Transform>();
        for (int i = 0; i < team.Length; i++) {
            a.Add(Spawn(ta, "A_" + (i + 1).ToString("00"), team[i], team[i] + Vector3.forward));
            bl.Add(Spawn(tb, "B_" + (i + 1).ToString("00"), new Vector3(-team[i].x, 0f, -team[i].z), new Vector3(-team[i].x, 0f, -team[i].z) + Vector3.back));
        }
        def.teamASpawns = a.ToArray();
        def.teamBSpawns = bl.ToArray();
    }

    /// <summary>
    /// The play area is everything inside the perimeter: the inner faces of the Boundary colliders (34.6 - 0.7 m from the
    /// centre). Those colliders already wall it in, so MapBounds builds none; the outline is for the server's check.
    /// </summary>
    private static void SetPlayArea(MapDefinition def) {
        const float e = 33.9f;
        def.boundary = new[] { V2(-e, -e), V2(e, -e), V2(e, e), V2(-e, e) };
        def.boundaryFloor = 0f;
        def.boundaryHeight = 40f;
        def.boundaryWalls = false;
    }

    private static Vector2 V2(float x, float z) { return new Vector2(x, z); }

    /// <summary>
    /// Write the play area into the existing prefab without rebuilding the map:
    ///   Unity -batchmode -quit -projectPath . -executeMethod MapBuilder_Outpost.ApplyPlayArea
    /// </summary>
    [MenuItem("Tools/FPS/Maps/Outpost Play Area")]
    public static void ApplyPlayArea() {
        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        MapDefinition def = root.GetComponent<MapDefinition>();
        SetPlayArea(def);
        int inside = def.spawnPoints.Concat(def.teamASpawns).Concat(def.teamBSpawns).Count(t => def.InPlayArea(t.position));
        PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        PrefabUtility.UnloadPrefabContents(root);
        Debug.Log("[MapBuilder_Outpost] Play area written to " + PrefabPath + ": " + inside + "/" +
                  (def.spawnPoints.Length + def.teamASpawns.Length + def.teamBSpawns.Length) + " spawn points inside");
    }

    private static void ConfigureDefinition(MapDefinition def, Light sun) {
        Material sky = MapKit.LoadOrCreate(MatDir + "/OutpostSky.mat");
        sky.shader = Shader.Find("Skybox/Panoramic");
        sky.SetTexture("_MainTex", AssetDatabase.LoadAssetAtPath<Texture>(Art + "Sky/kloofendal_overcast_2k.hdr"));
        sky.SetFloat("_Exposure", 0.9f);
        sky.SetFloat("_Rotation", 215f);
        EditorUtility.SetDirty(sky);

        def.id = "outpost";
        def.displayName = "전초기지";
        // Built (and checked by MapChecks) in metres for a ~1.8 m player at scale 1; the game loads it at the avatars'
        // 1.65 world units per metre, so the avatar's local size (1.7 m capsule) matches the map's metres.
        def.worldScale = MapDefinition.MetresToWorld;
        def.skybox = sky;
        def.ambientColor = new Color(0.52f, 0.55f, 0.58f);
        def.trilightAmbient = true;                                  // the sky probe leaves ceilings and interiors black
        def.ambientSky = new Color(0.62f, 0.66f, 0.72f);
        def.ambientEquator = new Color(0.55f, 0.55f, 0.52f);
        def.ambientGround = new Color(0.42f, 0.38f, 0.33f);
        def.ambientIntensity = 1.0f;
        def.reflectionIntensity = 0.7f;
        def.sun = sun;
        def.sunColor = sun.color;
        def.sunIntensity = sun.intensity;
        def.fog = true;
        def.fogMode = FogMode.ExponentialSquared;
        def.fogColor = new Color(0.66f, 0.65f, 0.6f);
        def.fogDensity = 0.0058f;
        def.disableOtherSuns = true;
    }

    /// <summary>Bake a reflection cube from this map's sky and geometry (a temporary probe at the centre) for MapDefinition.reflection.</summary>
    private static void BakeReflection(Transform root, MapDefinition def) {
        def.ApplyEnvironment();
        GameObject go = new GameObject("ReflectionProbe");
        go.transform.SetParent(root, false);
        go.transform.position = V(0f, 3f, 0f);
        ReflectionProbe rp = go.AddComponent<ReflectionProbe>();
        rp.mode = ReflectionProbeMode.Baked;
        rp.size = V(76f, 24f, 76f);
        rp.boxProjection = true;
        rp.resolution = 128;
        rp.hdr = true;
        rp.intensity = 1f;
        string path = Dir + "/Outpost_Reflection.exr";
        if (!Lightmapping.BakeReflectionProbe(rp, path)) {
            Debug.LogWarning("[MapBuilder_Outpost] Reflection probe bake failed");
            return;
        }
        AssetDatabase.ImportAsset(path);
        def.reflection = AssetDatabase.LoadAssetAtPath<Cubemap>(path);
        Object.DestroyImmediate(go);                                 // the cube is applied as the default reflection by MapDefinition
        def.ApplyEnvironment();
    }

    // ------------------------------------------------------------------ preview scene

    private static void BuildPreviewScene(GameObject prefab) {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        GameObject inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        inst.GetComponent<MapDefinition>().ApplyEnvironment();
        GameObject camGo = new GameObject("PreviewCamera") { tag = "MainCamera" };
        Camera cam = camGo.AddComponent<Camera>();
        cam.nearClipPlane = 0.1f;
        cam.farClipPlane = 700f;
        camGo.transform.position = new Vector3(-64f, 52f, -64f);
        camGo.transform.LookAt(Vector3.zero);
        MapChecks.AddPost(cam, AssetDatabase.LoadAssetAtPath<PostProcessProfile>(Art + "MilitaryPost.asset"));
        EditorSceneManager.SaveScene(scene, ScenePath);
    }

}
