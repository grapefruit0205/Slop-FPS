using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Invisible walls along a map's play area (<see cref="MapDefinition.boundary"/>), built at load on the server and on every
/// client, so nobody walks, jumps or drops off a roof out of the arena. They stand on Ignore Raycast: the
/// CharacterController (and the server's movement checks, which test against everything the body collides with) is
/// stopped by them, bullets (Shootable only) and the camera are not. Only the walls of the map in play are switched on.
/// The warehouse is not a prefab, so its outline lives here (<see cref="WarehouseOutline"/>).
/// </summary>
public class MapBounds : MonoBehaviour {

    private const float WallThickness = 2f;     // map units, outward from the outline
    private const float BelowFloor = 5f;         // walls start this far under the floor
    private const float EndOverlap = 0.3f;       // each wall runs this far past its corners

    /// <summary>
    /// The warehouse compound in Start.unity, along the inner face of its perimeter fence (world units; the scene map sits
    /// at the origin, unscaled): the two warehouse halves and the fenced yard round them. The outline keeps ~0.3 clear of
    /// the fence's top rail, so nobody can end up standing on the fence. The fence is 2.8 high and has a gap on the east
    /// side (the gate between concrete blocks), and a box by it or a roof edge gets anyone over it, so the walls rise well
    /// above the roofs. Measured with an editor probe of the scene's colliders.
    /// </summary>
    public static readonly Vector2[] WarehouseOutline = {
        new Vector2(-37.2f, 63.75f),    // north-west corner (the west fence runs slightly askew)
        new Vector2(31.95f, 63.75f),    // north-east
        new Vector2(32.75f, 29.3f),     // east fence ends at the gate
        new Vector2(34.2f, 27.5f),
        new Vector2(34.2f, -12.0f),     // just outside the gate's concrete blocks
        new Vector2(36.45f, -14.5f),    // south-east fence starts
        new Vector2(33.75f, -48.35f),   // south-east corner
        new Vector2(-29.25f, -48.35f),  // south-west corner
    };
    private const float WarehouseFloor = 0f;
    private const float WarehouseMaxSpawnHeight = 3f; // ground spawns sit at y=2; rooftop spawns were y=11.8 / 17
    private const float WarehouseWallHeight = 45f;   // roofs top out at ~19.5

    private MapDefinition map;
    private GameObject walls;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Hook() {
        MapLoader.Loaded -= OnMapLoaded;
        MapLoader.Loaded += OnMapLoaded;
    }

    /// <summary>
    /// The scene's warehouse definition: give it its outline, and keep only the spawn points inside the compound (the scene
    /// also has some out in the streets and on rooftops, which nobody should start from). Returns the spawns to use. Call
    /// <see cref="Install"/> once its team spawns are set too.
    /// </summary>
    public static Transform[] ConfigureWarehouse(MapDefinition def, Transform[] spawns) {
        def.boundary = WarehouseOutline;
        def.boundaryFloor = WarehouseFloor;
        def.boundaryHeight = WarehouseWallHeight;
        def.boundaryWalls = true;
        List<Transform> kept = new List<Transform>();
        List<string> dropped = new List<string>();
        foreach (Transform t in spawns ?? new Transform[0]) {
            if (t == null) continue;
            if (def.InPlayArea(t.position) && Mathf.Abs(t.position.y - WarehouseFloor) <= WarehouseMaxSpawnHeight) kept.Add(t);
            else dropped.Add(t.name + " " + t.position.ToString("F0"));
        }
        if (dropped.Count > 0) Debug.Log("[MapBounds] " + def.id + ": " + dropped.Count + " spawn point(s) outside the ground-level play area left out: " + string.Join(", ", dropped));
        // Do not restore rejected roof/outside points as a fallback. Invalid map setup should fail visibly.
        if (kept.Count == 0) Debug.LogError("[MapBounds] Warehouse has no valid ground-level spawn points.");
        Transform[] result = kept.ToArray();
        def.spawnPoints = result;
        return result;
    }

    private static void OnMapLoaded(MapDefinition def) {
        foreach (MapBounds b in FindObjectsOfType<MapBounds>(true)) {
            if (b.walls != null) b.walls.SetActive(b.map == def);
        }
        Install(def);
    }

    /// <summary>Build the walls of <paramref name="def"/> once (if it wants them) and log what the play area holds.</summary>
    public static void Install(MapDefinition def) {
        if (def == null || !def.HasBoundary) return;
        MapBounds b = def.GetComponent<MapBounds>();
        if (b == null) {
            b = def.gameObject.AddComponent<MapBounds>();
            b.map = def;
            if (def.boundaryWalls) b.Build();
            Debug.Log("[MapBounds] " + def.id + ": play area of " + def.boundary.Length + " corners" +
                      (def.boundaryWalls ? ", " + def.boundary.Length + " invisible walls" : " (walls in the map itself)") +
                      "; spawns inside: FFA " + Inside(def, def.spawnPoints) +
                      (def.teamASpawns != null && def.teamASpawns.Length > 0 ? ", A " + Inside(def, def.teamASpawns) + ", B " + Inside(def, def.teamBSpawns) : ""));
        }
        if (b.walls != null) b.walls.SetActive(true);
    }

    private static string Inside(MapDefinition def, Transform[] points) {
        if (points == null) return "0/0";
        int n = 0;
        foreach (Transform t in points) if (t != null && def.InPlayArea(t.position)) n++;
        return n + "/" + points.Length;
    }

    /// <summary>One box per edge, its inner face on the outline, in the map's own space so it scales with the map.</summary>
    private void Build() {
        walls = new GameObject("PlayAreaWalls");
        walls.layer = LayerMask.NameToLayer("Ignore Raycast");
        walls.transform.SetParent(map.transform, false);
        Vector2[] o = map.boundary;
        float area = 0f;
        for (int i = 0, j = o.Length - 1; i < o.Length; j = i++) area += o[j].x * o[i].y - o[i].x * o[j].y;
        float outward = area > 0f ? 1f : -1f;     // counter-clockwise (seen from above): outside is to the right
        float bottom = map.boundaryFloor - BelowFloor, top = map.boundaryFloor + map.boundaryHeight;
        for (int i = 0; i < o.Length; i++) {
            Vector2 a = o[i], b = o[(i + 1) % o.Length];
            Vector2 d = b - a;
            float len = d.magnitude;
            if (len < 0.01f) continue;
            d /= len;
            Vector2 n = new Vector2(d.y, -d.x) * outward;
            Vector2 mid = (a + b) * 0.5f + n * (WallThickness * 0.5f);
            GameObject w = new GameObject("Wall_" + i);
            w.layer = walls.layer;
            w.transform.SetParent(walls.transform, false);
            w.transform.localPosition = new Vector3(mid.x, (bottom + top) * 0.5f, mid.y);
            w.transform.localRotation = Quaternion.LookRotation(new Vector3(d.x, 0f, d.y));
            BoxCollider box = w.AddComponent<BoxCollider>();
            box.size = new Vector3(WallThickness, top - bottom, len + EndOverlap * 2f);
        }
        Physics.SyncTransforms();
    }

}
