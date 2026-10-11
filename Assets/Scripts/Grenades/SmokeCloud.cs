using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A smoke grenade's cloud on a client: ~120 big soft billboards packed into a squat ellipsoid around the canister,
/// billowing out over the first second and a half and thinning away at the end (<see cref="GrenadeFlight.SmokeSize"/>
/// and <see cref="GrenadeFlight.SmokeThickness"/>, the same shape the sight checks use). Puffs that would sit inside a
/// wall are pulled back to it, so the cloud fills a room instead of leaking through. Soft particles stay off and the
/// count stays low enough for a laptop's integrated graphics in WebGL. Standing inside, the HUD fogs the screen.
/// </summary>
public class SmokeCloud : MonoBehaviour {

    /// <summary>Every cloud standing on this client.</summary>
    public static readonly List<SmokeCloud> All = new List<SmokeCloud>();

    private const int CorePuffs = 8;

    public ushort Id { get; private set; }
    /// <summary>Where the canister lies.</summary>
    public Vector3 Floor { get; private set; }
    public float Age { get; private set; }

    private ParticleSystem ps;
    private ParticleSystem.Particle[] puffs;
    private float[] alpha;
    private float shownThickness = -1f;

    public static SmokeCloud Create(ushort id, Vector3 floor, float elapsed) {
        GameObject go = new GameObject("SmokeCloud" + id);
        go.layer = GrenadeFx.FxLayer;
        go.transform.position = floor;
        SmokeCloud c = go.AddComponent<SmokeCloud>();
        c.Id = id;
        c.Floor = floor;
        c.Age = elapsed;
        c.Build();
        if (elapsed < 1f) GrenadeFx.PlayAt(GrenadeFx.Sounds.Hiss, floor, 0.8f, Grenades.M(45f));
        return c;
    }

    void OnEnable() {
        All.Add(this);
    }

    void OnDisable() {
        All.Remove(this);
    }

    private void Build() {
        ps = gameObject.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        ParticleSystem.MainModule main = ps.main;
        main.loop = true;
        main.duration = 5f;
        main.playOnAwake = false;
        main.startLifetime = Grenades.SmokeDuration + 10f;
        main.startSpeed = 0f;
        main.simulationSpace = ParticleSystemSimulationSpace.Local;
        main.scalingMode = ParticleSystemScalingMode.Hierarchy;
        main.maxParticles = Grenades.SmokePuffs + CorePuffs;
        ParticleSystem.EmissionModule em = ps.emission;
        em.enabled = false;
        ParticleSystem.ShapeModule shape = ps.shape;
        shape.enabled = false;
        ParticleSystemRenderer r = GetComponent<ParticleSystemRenderer>();
        r.sharedMaterial = GrenadeFx.SmokeMaterial;
        r.sortMode = ParticleSystemSortMode.Distance;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        // Big puffs right in front of the camera must not be squashed away.
        r.maxParticleSize = 4f;
        ps.Play();

        GrenadeFlight.SmokeShape(Floor, Grenades.SmokeBloom, out Vector3 center, out Vector3 radii);
        Vector3 localCenter = center - Floor;
        float m = Grenades.M(1f);
        System.Random rng = new System.Random(Id * 7919 + 17);
        ParticleSystem.EmitParams p = new ParticleSystem.EmitParams();
        for (int i = 0; i < Grenades.SmokePuffs + CorePuffs; i++) {
            bool core = i < CorePuffs;
            Vector3 unit = InBall(rng) * (core ? 0.45f : 1f);
            Vector3 local = localCenter + Vector3.Scale(unit, radii);
            float size = core ? 4.5f * m : Mathf.Lerp(2.0f, 3.4f, (float)rng.NextDouble()) * m;
            local.y = Mathf.Max(local.y, size * 0.25f);
            local = KeepInside(center, local, size);
            float shade = Mathf.Lerp(0.56f, 0.68f, (float)rng.NextDouble());
            p.position = local;
            p.startSize = size;
            p.rotation = (float)rng.NextDouble() * 360f;
            p.angularVelocity = Mathf.Lerp(-8f, 8f, (float)rng.NextDouble());
            p.startColor = new Color(shade, shade, shade * 0.98f, core ? 1f : 0.88f);
            p.velocity = Vector3.zero;
            ps.Emit(p, 1);
        }
        puffs = new ParticleSystem.Particle[main.maxParticles];
        int n = ps.GetParticles(puffs);
        alpha = new float[n];
        for (int i = 0; i < n; i++) alpha[i] = puffs[i].startColor.a / 255f;
        Apply();
    }

    /// <summary>A point in the unit ball, a little more of them low down (smoke pools on the floor).</summary>
    private static Vector3 InBall(System.Random rng) {
        while (true) {
            Vector3 v = new Vector3((float)rng.NextDouble() * 2f - 1f, (float)rng.NextDouble() * 2f - 1f, (float)rng.NextDouble() * 2f - 1f);
            if (v.sqrMagnitude > 1f) continue;
            if (v.y > 0.3f && rng.NextDouble() < 0.3) continue;
            return v;
        }
    }

    /// <summary>Pull a puff back from any wall between it and the middle of the cloud.</summary>
    private Vector3 KeepInside(Vector3 center, Vector3 local, float size) {
        Vector3 world = Floor + local;
        Vector3 d = world - center;
        float len = d.magnitude;
        if (len < 0.01f) return local;
        if (Physics.Raycast(center, d / len, out RaycastHit hit, len + size * 0.3f, GrenadeFlight.SightMask, QueryTriggerInteraction.Ignore)) {
            world = center + d / len * Mathf.Max(0f, hit.distance - size * 0.3f);
        }
        return world - Floor;
    }

    void Update() {
        Age += Time.deltaTime;
        if (Age >= Grenades.SmokeDuration) {
            Destroy(gameObject);
            return;
        }
        Apply();
    }

    private void Apply() {
        transform.localScale = Vector3.one * GrenadeFlight.SmokeSize(Age);
        float thick = GrenadeFlight.SmokeThickness(Age);
        if (Mathf.Abs(thick - shownThickness) < 0.01f || puffs == null) return;
        shownThickness = thick;
        int n = ps.GetParticles(puffs);
        for (int i = 0; i < n && i < alpha.Length; i++) {
            Color32 c = puffs[i].startColor;
            c.a = (byte)Mathf.RoundToInt(255f * alpha[i] * thick);
            puffs[i].startColor = c;
        }
        ps.SetParticles(puffs, n);
    }

}
