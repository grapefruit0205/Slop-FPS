using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>
/// Puts the session's map into the world, on the server (colliders for movement and shot checks) and on every client.
/// The warehouse is baked into Start.unity: when the scene loads, its map objects are collected under a runtime
/// <see cref="MapDefinition"/> so they can be switched off as one. Every other map is a prefab from
/// <see cref="MapCatalog"/>, instantiated at its <see cref="MapDefinition.worldScale"/>. Nothing here is networked; the
/// server picks the id and clients load the one it tells them (<see cref="FpsAuthenticator"/>, <see cref="MatchManager"/>).
/// </summary>
public static class MapLoader {

    public const string SceneMapId = "warehouse";
    public const string DefaultMap = SceneMapId;

    /// <summary>The map in play (the warehouse until something else is loaded).</summary>
    public static MapDefinition Current { get; private set; }

    public static string CurrentId => Current != null ? Current.id : "";

    /// <summary>A different map has just been put into the world.</summary>
    public static event Action<MapDefinition> Loaded;

    private static MapDefinition sceneMap;
    private static readonly List<GameObject> sceneContent = new List<GameObject>();
    private static SceneLighting sceneEnvironment;
    private static Camera sceneCamera;
    private static Vector3 sceneCameraPos;
    private static Quaternion sceneCameraRot;
    private static GameObject instance;

    /// <summary>Lowercase id, or the default for unknown/empty ones.</summary>
    public static string Normalize(string id) {
        string k = (id ?? "").Trim().ToLowerInvariant();
        return IsKnown(k) ? k : DefaultMap;
    }

    public static bool IsKnown(string id) {
        string k = (id ?? "").Trim().ToLowerInvariant();
        return k == SceneMapId || (MapCatalog.Instance != null && MapCatalog.Instance.Find(k) != null);
    }

    /// <summary>Display name for an id, without loading it (lobby list, scoreboard).</summary>
    public static string DisplayName(string id) {
        string k = Normalize(id);
        if (k == SceneMapId) return L10n.T("창고");
        MapDefinition def = MapCatalog.Instance != null ? MapCatalog.Instance.Find(k) : null;
        return def != null ? L10n.T(def.displayName) : k;
    }

    // ------------------------------------------------------------------ the map baked into the scene

    /// <summary>
    /// Runs after Start.unity's Awake calls and before anything builds runtime objects (UI, network objects), so the
    /// scene's own roots are exactly what the level designer put there. Everything that is not infrastructure is the
    /// warehouse.
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void CaptureScene() {
        sceneContent.Clear();
        sceneMap = null;
        Current = null;
        instance = null;
        GameNetwork game = Object.FindObjectOfType<GameNetwork>();
        if (game == null) return;
        sceneCamera = game.SceneCamera;
        if (sceneCamera != null) {
            sceneCameraPos = sceneCamera.transform.position;
            sceneCameraRot = sceneCamera.transform.rotation;
        }
        foreach (GameObject root in SceneManager.GetActiveScene().GetRootGameObjects()) {
            if (root.activeSelf && !IsInfrastructure(root, sceneCamera)) sceneContent.Add(root);
        }
        sceneEnvironment = SceneLighting.Capture();
        // Start.unity has no baked lighting data: without this, its skybox ambient light and reflections are only in a
        // build if the editor's GI cache happened to have them, and everything in shade renders black.
        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null) DynamicGI.UpdateEnvironment();

        GameObject go = new GameObject("Warehouse (scene map)");
        sceneMap = go.AddComponent<MapDefinition>();
        sceneMap.id = SceneMapId;
        sceneMap.displayName = DisplayName(SceneMapId);
        sceneMap.spawnPoints = MapBounds.ConfigureWarehouse(sceneMap, game.SpawnPoints);   // the compound only
        SplitSides(sceneMap.spawnPoints, out sceneMap.teamASpawns, out sceneMap.teamBSpawns);
        MapBounds.Install(sceneMap);
        Current = sceneMap;
    }

    /// <summary>Networking, UI, the scene camera and global post-processing stay whatever map is loaded.</summary>
    private static bool IsInfrastructure(GameObject root, Camera keepCamera) {
        if (keepCamera != null && keepCamera.transform.IsChildOf(root.transform)) return true;
        if (root.GetComponentInChildren<FishNet.Managing.NetworkManager>(true) != null) return true;
        if (root.GetComponentInChildren<GameNetwork>(true) != null) return true;
        if (root.GetComponentInChildren<UnityEngine.EventSystems.EventSystem>(true) != null) return true;
        if (root.GetComponentInChildren<Canvas>(true) != null) return true;
        foreach (MonoBehaviour mb in root.GetComponentsInChildren<MonoBehaviour>(true)) {
            if (mb != null && mb.GetType().Name == "PostProcessVolume") return true;
        }
        return false;
    }

    /// <summary>
    /// The warehouse has one list of spawn points spread over the whole level. For team modes, cut it in two along the
    /// line between the two points farthest apart, so each side starts at its own end.
    /// </summary>
    private static void SplitSides(Transform[] points, out Transform[] a, out Transform[] b) {
        a = b = new Transform[0];
        if (points == null || points.Length < 2) return;
        Vector3 p0 = points[0].position, p1 = points[1].position;
        float best = -1f;
        foreach (Transform x in points) {
            foreach (Transform y in points) {
                Vector3 d = x.position - y.position;
                d.y = 0f;
                if (d.sqrMagnitude > best) {
                    best = d.sqrMagnitude;
                    p0 = x.position;
                    p1 = y.position;
                }
            }
        }
        Vector3 axis = p1 - p0;
        axis.y = 0f;
        List<Transform> sorted = new List<Transform>(points);
        sorted.Sort((x, y) => Vector3.Dot(x.position - p0, axis).CompareTo(Vector3.Dot(y.position - p0, axis)));
        int half = sorted.Count / 2;
        a = sorted.GetRange(0, half).ToArray();
        b = sorted.GetRange(half, sorted.Count - half).ToArray();
    }

    // ------------------------------------------------------------------ loading

    /// <summary>
    /// Make <paramref name="id"/> the map in play (unknown ids fall back to the warehouse). Loading the map that is
    /// already in play does nothing, so the server and a client in the same process (host) can both ask.
    /// </summary>
    public static MapDefinition Load(string id) {
        string key = Normalize(id);
        if (!string.IsNullOrEmpty(id) && key != id.Trim().ToLowerInvariant()) {
            Debug.LogWarning("[Map] Unknown map '" + id + "', using " + key);
        }
        if (Current != null && Current.id == key) return Current;

        if (instance != null) {
            instance.SetActive(false);
            Object.Destroy(instance);
            instance = null;
        }

        MapDefinition def;
        if (key == SceneMapId) {
            foreach (GameObject go in sceneContent) {
                if (go != null) go.SetActive(true);
            }
            if (sceneEnvironment != null) sceneEnvironment.Restore();
            if (sceneCamera != null) sceneCamera.transform.SetPositionAndRotation(sceneCameraPos, sceneCameraRot);
            def = sceneMap;
        } else {
            // Off first, so the new map's ApplyEnvironment does not see (and switch off) the warehouse's lights.
            foreach (GameObject go in sceneContent) {
                if (go != null) go.SetActive(false);
            }
            MapDefinition prefab = MapCatalog.Instance.Find(key);
            instance = Object.Instantiate(prefab.gameObject);
            instance.name = prefab.gameObject.name;
            instance.transform.localScale = Vector3.one * Mathf.Max(0.01f, prefab.worldScale);
            Physics.SyncTransforms();
            MapBatching.Combine(instance);
            def = instance.GetComponent<MapDefinition>();
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null) def.ApplyEnvironment();
            FrameSceneCamera(instance);
        }
        Current = def;
        Debug.Log("[Map] Loaded " + key + " (" + def.displayName + ") at scale " + def.transform.lossyScale.x.ToString("0.##") +
                  ": " + Count(def.spawnPoints) + " FFA spawns, " + Count(def.teamASpawns) + "/" + Count(def.teamBSpawns) + " team spawns");
        Loaded?.Invoke(def);
        return def;
    }

    private static int Count(Transform[] t) {
        return t != null ? t.Length : 0;
    }

    /// <summary>The scene camera (lobby backdrop, between respawns) looks over the new map from a high corner.</summary>
    private static void FrameSceneCamera(GameObject map) {
        if (sceneCamera == null) return;
        int ignore = LayerMask.NameToLayer("Ignore Raycast");
        bool any = false;
        Bounds b = new Bounds(map.transform.position, Vector3.zero);
        foreach (Collider c in map.GetComponentsInChildren<Collider>()) {
            if (c.gameObject.layer == ignore) continue;   // invisible boundary walls
            if (any) b.Encapsulate(c.bounds);
            else b = c.bounds;
            any = true;
        }
        Vector3 e = b.extents;
        Vector3 pos = b.center + new Vector3(-0.9f * e.x, 0.75f * Mathf.Max(e.x, e.z), -0.9f * e.z);
        sceneCamera.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(b.center - pos));
    }

    /// <summary>The scene's sky, ambient light and fog, to put back when returning to the warehouse.</summary>
    private class SceneLighting {
        private Material skybox;
        private AmbientMode ambientMode;
        private Color ambientLight, ambientSky, ambientEquator, ambientGround;
        private float ambientIntensity, reflectionIntensity;
        private DefaultReflectionMode reflectionMode;
        private Texture customReflection;
        private bool fog;
        private FogMode fogMode;
        private Color fogColor;
        private float fogDensity, fogStart, fogEnd;
        private Light sun;

        public static SceneLighting Capture() {
            return new SceneLighting {
                skybox = RenderSettings.skybox, ambientMode = RenderSettings.ambientMode, ambientLight = RenderSettings.ambientLight,
                ambientSky = RenderSettings.ambientSkyColor, ambientEquator = RenderSettings.ambientEquatorColor,
                ambientGround = RenderSettings.ambientGroundColor, ambientIntensity = RenderSettings.ambientIntensity,
                reflectionIntensity = RenderSettings.reflectionIntensity, reflectionMode = RenderSettings.defaultReflectionMode,
                customReflection = RenderSettings.customReflectionTexture, fog = RenderSettings.fog, fogMode = RenderSettings.fogMode,
                fogColor = RenderSettings.fogColor, fogDensity = RenderSettings.fogDensity, fogStart = RenderSettings.fogStartDistance,
                fogEnd = RenderSettings.fogEndDistance, sun = RenderSettings.sun,
            };
        }

        public void Restore() {
            RenderSettings.skybox = skybox;
            RenderSettings.ambientMode = ambientMode;
            RenderSettings.ambientLight = ambientLight;
            RenderSettings.ambientSkyColor = ambientSky;
            RenderSettings.ambientEquatorColor = ambientEquator;
            RenderSettings.ambientGroundColor = ambientGround;
            RenderSettings.ambientIntensity = ambientIntensity;
            RenderSettings.reflectionIntensity = reflectionIntensity;
            RenderSettings.defaultReflectionMode = reflectionMode;
            RenderSettings.customReflectionTexture = customReflection;
            RenderSettings.fog = fog;
            RenderSettings.fogMode = fogMode;
            RenderSettings.fogColor = fogColor;
            RenderSettings.fogDensity = fogDensity;
            RenderSettings.fogStartDistance = fogStart;
            RenderSettings.fogEndDistance = fogEnd;
            RenderSettings.sun = sun;
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null) DynamicGI.UpdateEnvironment();
        }
    }

}
