using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Sits on the root of a map prefab: which spawn points the map has and how it should look (sky, ambient light, sun,
/// fog). The game instantiates the prefab and calls <see cref="ApplyEnvironment"/>; nothing here touches the network.
/// Spawn points are empty children placed just above the floor, facing into the map.
/// Maps can be authored in real metres and scaled up to the game's world units at load (<see cref="worldScale"/>).
/// The play area is an outline on the ground (<see cref="boundary"/>): <see cref="MapBounds"/> walls it in with invisible
/// colliders and the server's <see cref="MovementGuard"/> puts back anyone found outside it.
/// </summary>
public class MapDefinition : MonoBehaviour {

    /// <summary>
    /// World units per real metre: the player avatars' root is scaled x1.65, so a 1.7 m tall CharacterController is
    /// 2.8 units. A map built in metres for a ~1.8 m player is instantiated at this scale.
    /// </summary>
    public const float MetresToWorld = 1.65f;

    [Header("Identity")]
    public string id = "map";                 // lowercase key used by the backend / room settings
    public string displayName = "Map";
    [Tooltip("Uniform scale the game instantiates this map at. Maps built in metres use MetresToWorld (1.65) so doors and stairs fit the avatars.")]
    public float worldScale = 1f;

    [Header("Spawns")]
    public Transform[] spawnPoints;           // free-for-all, spread over the whole map
    public Transform[] teamASpawns;           // team deathmatch, opposite sides
    public Transform[] teamBSpawns;

    [Header("Play area")]
    [Tooltip("Outline of the playable area on the ground, in the map's own units (x, z), either winding. Empty: no limit.")]
    public Vector2[] boundary;
    [Tooltip("Lowest floor in the play area (map units). Falling well below it means falling out of the map.")]
    public float boundaryFloor;
    [Tooltip("How high the invisible walls along the outline reach above the floor (map units).")]
    public float boundaryHeight = 40f;
    [Tooltip("Build invisible walls along the outline at load. Off for maps whose prefab carries its own (Outpost's Boundary).")]
    public bool boundaryWalls = true;

    [Header("Environment")]
    public Material skybox;
    public Color ambientColor = new Color(0.5f, 0.55f, 0.6f);   // flat ambient, used when there is no skybox
    [Range(0f, 2f)] public float ambientIntensity = 1f;         // skybox ambient / reflection brightness
    [Tooltip("Three-colour ambient instead of the skybox probe, so down-facing surfaces and interiors do not go black.")]
    public bool trilightAmbient;
    public Color ambientSky = new Color(0.5f, 0.56f, 0.64f);
    public Color ambientEquator = new Color(0.42f, 0.42f, 0.4f);
    public Color ambientGround = new Color(0.27f, 0.25f, 0.22f);
    [Range(0f, 2f)] public float reflectionIntensity = 0.8f;
    [Tooltip("Optional baked reflection of this map. Used as the default reflection, so metal looks right at once instead of waiting for the skybox probe to update.")]
    public Cubemap reflection;
    public Light sun;
    public Color sunColor = new Color(0.93f, 0.92f, 0.88f);
    public float sunIntensity = 0.95f;
    public bool fog = true;
    public FogMode fogMode = FogMode.ExponentialSquared;
    public Color fogColor = new Color(0.58f, 0.61f, 0.63f);
    public float fogDensity = 0.0065f;
    [Tooltip("Turn off directional lights that belong to the scene (another map's sun) so they do not stack with this one.")]
    public bool disableOtherSuns = true;

    /// <summary>Where a player of <paramref name="team"/> may appear: their side's points in a team mode (falling back to the
    /// free-for-all list if the map has none), the free-for-all list otherwise.</summary>
    public Transform[] SpawnsFor(Team team) {
        Transform[] side = team == Team.A ? teamASpawns : team == Team.B ? teamBSpawns : null;
        return side != null && side.Length > 0 ? side : spawnPoints;
    }

    public bool HasBoundary => boundary != null && boundary.Length >= 3;

    /// <summary>World height of <see cref="boundaryFloor"/>.</summary>
    public float FloorY => transform.TransformPoint(new Vector3(0f, boundaryFloor, 0f)).y;

    /// <summary>
    /// Is <paramref name="world"/> inside the outline (seen from above), or no more than <paramref name="slack"/> world
    /// units outside it? Always true for a map without an outline.
    /// </summary>
    public bool InPlayArea(Vector3 world, float slack = 0f) {
        if (!HasBoundary) return true;
        Vector3 local = transform.InverseTransformPoint(world);
        Vector2 p = new Vector2(local.x, local.z);
        bool inside = false;
        float nearest = float.MaxValue;
        for (int i = 0, j = boundary.Length - 1; i < boundary.Length; j = i++) {
            Vector2 a = boundary[i], b = boundary[j];
            if ((a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x) inside = !inside;
            Vector2 ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(1e-6f, ab.sqrMagnitude));
            nearest = Mathf.Min(nearest, Vector2.Distance(p, a + ab * t));
        }
        return inside || nearest * Mathf.Abs(transform.lossyScale.x) <= slack;
    }

    /// <summary>Push this map's sky, lighting and fog into <see cref="RenderSettings"/> (call after instantiating).</summary>
    public void ApplyEnvironment() {
        RenderSettings.skybox = skybox;
        if (trilightAmbient) {
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = ambientSky * ambientIntensity;
            RenderSettings.ambientEquatorColor = ambientEquator * ambientIntensity;
            RenderSettings.ambientGroundColor = ambientGround * ambientIntensity;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
            RenderSettings.reflectionIntensity = reflectionIntensity;
        } else if (skybox != null) {
            RenderSettings.ambientMode = AmbientMode.Skybox;
            RenderSettings.ambientIntensity = ambientIntensity;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
            RenderSettings.reflectionIntensity = reflectionIntensity;
        } else {
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = ambientColor * ambientIntensity;
        }

        if (reflection != null) {
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
            RenderSettings.customReflectionTexture = reflection;
            RenderSettings.reflectionIntensity = reflectionIntensity;
        }

        if (sun != null) {
            sun.color = sunColor;
            sun.intensity = sunIntensity;
            sun.enabled = true;
            RenderSettings.sun = sun;
            if (disableOtherSuns) {
                foreach (Light l in FindObjectsOfType<Light>()) {
                    if (l != sun && l.type == LightType.Directional && !l.transform.IsChildOf(transform)) l.enabled = false;
                }
            }
        }

        RenderSettings.fog = fog;
        RenderSettings.fogMode = fogMode;
        RenderSettings.fogColor = fogColor;
        // Density is per world unit; a map scaled up for the avatars keeps the same look per metre.
        RenderSettings.fogDensity = fogDensity / Mathf.Max(0.01f, transform.lossyScale.x);
        // Re-bake the ambient probe and reflection from the new sky.
        DynamicGI.UpdateEnvironment();
    }

}
