using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// What grenades look and sound like, built in code: low-poly models from primitives (frag: olive egg with its spoon and
/// ring; flash: grey can; smoke: green can with a pale band), the explosion (flash, fireball, smoke, debris, sparks),
/// the flashbang's burst, and procedurally synthesised sounds (no clips to ship). Particles use the project's own
/// particle shaders through the materials in Resources/Grenades, so they work in WebGL; counts stay small for laptops.
/// Client only.
/// </summary>
public static class GrenadeFx {

    private static Material smokeMat, fireMat, glowMat, lineMat, bodyMat;
    private static readonly Dictionary<Color, Material> tinted = new Dictionary<Color, Material>();
    private static GameObject explosionTemplate, flashTemplate;

    public static Material SmokeMaterial => Load(ref smokeMat, "GrenadeSmoke");
    public static Material FireMaterial => Load(ref fireMat, "GrenadeFire");
    public static Material GlowMaterial => Load(ref glowMat, "GrenadeGlow");
    public static Material LineMaterial => Load(ref lineMat, "GrenadeLine");

    /// <summary>World effects go on TransparentFX: every gameplay camera draws it and nothing shoots or collides with it.</summary>
    public static int FxLayer => LayerMask.NameToLayer("TransparentFX");

    private static Material Load(ref Material cache, string name) {
        if (cache == null) cache = Resources.Load<Material>("Grenades/" + name);
        return cache;
    }

    private static Material Tint(Color c) {
        if (!tinted.TryGetValue(c, out Material m)) {
            Material source = Load(ref bodyMat, "GrenadeBody");
            m = source != null ? new Material(source) : new Material(Shader.Find("Standard"));
            m.color = c;
            tinted[c] = m;
        }
        return m;
    }

    // ------------------------------------------------------------------ models

    /// <summary>A grenade of kind <paramref name="id"/>, real size in world units, its middle at the root.</summary>
    public static GameObject Model(GrenadeId id, int layer) {
        GrenadeSpec spec = Grenades.Get(id);
        GameObject root = new GameObject(spec != null ? "Grenade_" + id : "Grenade");
        root.layer = layer;
        Color dark = new Color(0.16f, 0.17f, 0.16f), metal = new Color(0.55f, 0.55f, 0.52f);
        Color body = spec != null ? spec.Body : Color.grey;
        float top;
        switch (id) {
            case GrenadeId.Frag:
                Part(root, PrimitiveType.Sphere, body, Vector3.zero, new Vector3(0.064f, 0.074f, 0.064f), Quaternion.identity);
                top = 0.034f;
                break;
            case GrenadeId.Flash:
                Part(root, PrimitiveType.Cylinder, body, Vector3.zero, new Vector3(0.044f, 0.056f, 0.044f), Quaternion.identity);
                // The vent holes read as two dark rings.
                Part(root, PrimitiveType.Cylinder, dark, new Vector3(0f, 0.022f, 0f), new Vector3(0.0455f, 0.004f, 0.0455f), Quaternion.identity);
                Part(root, PrimitiveType.Cylinder, dark, new Vector3(0f, -0.022f, 0f), new Vector3(0.0455f, 0.004f, 0.0455f), Quaternion.identity);
                top = 0.056f;
                break;
            default:
                Part(root, PrimitiveType.Cylinder, body, Vector3.zero, new Vector3(0.062f, 0.058f, 0.062f), Quaternion.identity);
                Part(root, PrimitiveType.Cylinder, new Color(0.78f, 0.78f, 0.72f), new Vector3(0f, 0.034f, 0f), new Vector3(0.0635f, 0.007f, 0.0635f), Quaternion.identity);
                top = 0.058f;
                break;
        }
        // Fuse head, spoon down the side and the pull ring.
        Part(root, PrimitiveType.Cylinder, dark, new Vector3(0f, top + 0.008f, 0f), new Vector3(0.022f, 0.009f, 0.022f), Quaternion.identity);
        Part(root, PrimitiveType.Cube, dark, new Vector3(0f, top - 0.012f, 0.026f), new Vector3(0.014f, 0.06f, 0.004f), Quaternion.Euler(-12f, 0f, 0f));
        Part(root, PrimitiveType.Cylinder, metal, new Vector3(0.018f, top + 0.01f, 0f), new Vector3(0.022f, 0.0015f, 0.022f), Quaternion.Euler(0f, 0f, 90f));
        root.transform.localScale = Vector3.one * Grenades.M(1f);
        return root;
    }

    private static void Part(GameObject root, PrimitiveType type, Color color, Vector3 pos, Vector3 size, Quaternion rot) {
        GameObject p = GameObject.CreatePrimitive(type);
        Object.DestroyImmediate(p.GetComponent<Collider>());
        p.layer = root.layer;
        p.transform.SetParent(root.transform, false);
        p.transform.localPosition = pos;
        p.transform.localRotation = rot;
        p.transform.localScale = size;
        MeshRenderer r = p.GetComponent<MeshRenderer>();
        r.sharedMaterial = Tint(color);
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }

    /// <summary>Move every renderer under <paramref name="go"/> onto <paramref name="layer"/>.</summary>
    public static void SetLayer(GameObject go, int layer) {
        foreach (Transform t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
    }

    // ------------------------------------------------------------------ blasts

    /// <summary>The frag going off: a flash of light, a fireball, a smoke puff, debris and sparks, and the bang.</summary>
    public static void Explosion(Vector3 at) {
        if (explosionTemplate == null) explosionTemplate = BuildExplosion();
        Spawn(explosionTemplate, at, 6f);
        PlayAt(Sounds.Explosion, at, 1f, Grenades.M(160f));
    }

    /// <summary>The flashbang's burst: a blinding pop of light, sparks and a wisp of smoke, and the crack.</summary>
    public static void FlashBurst(Vector3 at) {
        if (flashTemplate == null) flashTemplate = BuildFlash();
        Spawn(flashTemplate, at, 3f);
        PlayAt(Sounds.Bang, at, 1f, Grenades.M(140f));
    }

    private static void Spawn(GameObject template, Vector3 at, float life) {
        GameObject go = Object.Instantiate(template, at, Quaternion.identity);
        go.SetActive(true);
        Object.Destroy(go, life);
    }

    private static GameObject BuildExplosion() {
        GameObject root = Template("Explosion");
        float m = Grenades.M(1f);
        // Fireball: a few big additive puffs that flare and die fast.
        ParticleSystem fire = System(root, "Fireball", FireMaterial, 12, 0.22f, 0.5f, 1f * m, 4f * m, 1.4f * m, 3.2f * m, 0.4f * m, 0f);
        Fade(fire, new Color(1f, 0.95f, 0.8f), new Color(1f, 0.55f, 0.2f), 0.9f);
        Grow(fire, 0.6f, 1.4f);
        // Smoke: dark grey-brown, rising and spreading for a few seconds.
        ParticleSystem smoke = System(root, "Smoke", SmokeMaterial, 18, 2.2f, 4f, 0.6f * m, 2.8f * m, 1.8f * m, 3.6f * m, 0.6f * m, -0.025f);
        Fade(smoke, new Color(0.24f, 0.22f, 0.2f), new Color(0.42f, 0.41f, 0.39f), 0.8f);
        Grow(smoke, 0.5f, 1.8f);
        Damp(smoke, 0.5f);
        // Debris: dark bits thrown out and falling back.
        ParticleSystem debris = System(root, "Debris", SmokeMaterial, 40, 0.6f, 1.4f, 6f * m, 15f * m, 0.05f * m, 0.13f * m, 0.2f * m, Grenades.M(1f));
        Fade(debris, new Color(0.1f, 0.09f, 0.08f), new Color(0.12f, 0.11f, 0.1f), 1f);
        Stretch(debris, 0.03f);
        // Sparks.
        ParticleSystem sparks = System(root, "Sparks", GlowMaterial, 28, 0.15f, 0.35f, 9f * m, 22f * m, 0.03f * m, 0.08f * m, 0.15f * m, 0.4f);
        Fade(sparks, new Color(1f, 0.85f, 0.5f), new Color(1f, 0.5f, 0.15f), 1f);
        Stretch(sparks, 0.02f);
        Flare(root, new Color(1f, 0.66f, 0.35f), 7f, Grenades.M(16f), 0.3f);
        return root;
    }

    private static GameObject BuildFlash() {
        GameObject root = Template("Flashbang");
        float m = Grenades.M(1f);
        ParticleSystem pop = System(root, "Pop", GlowMaterial, 3, 0.12f, 0.18f, 0f, 0.1f * m, 3.5f * m, 5f * m, 0.05f * m, 0f);
        Fade(pop, Color.white, new Color(0.9f, 0.95f, 1f), 1f);
        ParticleSystem sparks = System(root, "Sparks", GlowMaterial, 30, 0.15f, 0.4f, 5f * m, 12f * m, 0.03f * m, 0.07f * m, 0.1f * m, 0.3f);
        Fade(sparks, Color.white, new Color(1f, 0.9f, 0.7f), 1f);
        Stretch(sparks, 0.02f);
        ParticleSystem wisp = System(root, "Wisp", SmokeMaterial, 6, 1.2f, 2f, 0.3f * m, 1.2f * m, 0.8f * m, 1.5f * m, 0.2f * m, -0.02f);
        Fade(wisp, new Color(0.7f, 0.7f, 0.7f), new Color(0.75f, 0.75f, 0.75f), 0.5f);
        Grow(wisp, 0.6f, 1.6f);
        Flare(root, new Color(0.95f, 0.97f, 1f), 9f, Grenades.M(22f), 0.15f);
        return root;
    }

    private static GameObject Template(string name) {
        GameObject root = new GameObject(name + "Template");
        root.SetActive(false);
        root.layer = FxLayer;
        Object.DontDestroyOnLoad(root);
        return root;
    }

    private static ParticleSystem System(GameObject root, string name, Material mat, int count, float lifeMin, float lifeMax,
                                         float speedMin, float speedMax, float sizeMin, float sizeMax, float radius, float gravity) {
        GameObject go = new GameObject(name);
        go.layer = root.layer;
        go.transform.SetParent(root.transform, false);
        ParticleSystem ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        ParticleSystem.MainModule main = ps.main;
        main.duration = 1f;
        main.loop = false;
        main.playOnAwake = true;
        main.startLifetime = new ParticleSystem.MinMaxCurve(lifeMin, lifeMax);
        main.startSpeed = new ParticleSystem.MinMaxCurve(speedMin, speedMax);
        main.startSize = new ParticleSystem.MinMaxCurve(sizeMin, sizeMax);
        main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
        main.gravityModifier = gravity;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = count;
        ParticleSystem.EmissionModule em = ps.emission;
        em.rateOverTime = 0f;
        em.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)count) });
        ParticleSystem.ShapeModule shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = Mathf.Max(0.001f, radius);
        ParticleSystemRenderer r = go.GetComponent<ParticleSystemRenderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        r.sortMode = ParticleSystemSortMode.Distance;
        return ps;
    }

    private static void Fade(ParticleSystem ps, Color from, Color to, float alpha) {
        ParticleSystem.ColorOverLifetimeModule col = ps.colorOverLifetime;
        col.enabled = true;
        Gradient g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(from, 0f), new GradientColorKey(to, 1f) },
                  new[] { new GradientAlphaKey(alpha, 0f), new GradientAlphaKey(alpha * 0.8f, 0.4f), new GradientAlphaKey(0f, 1f) });
        col.color = new ParticleSystem.MinMaxGradient(g);
    }

    private static void Grow(ParticleSystem ps, float from, float to) {
        ParticleSystem.SizeOverLifetimeModule s = ps.sizeOverLifetime;
        s.enabled = true;
        s.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, from, 1f, to));
    }

    private static void Damp(ParticleSystem ps, float dampen) {
        ParticleSystem.LimitVelocityOverLifetimeModule l = ps.limitVelocityOverLifetime;
        l.enabled = true;
        l.limit = 0f;
        l.dampen = dampen;
    }

    private static void Stretch(ParticleSystem ps, float velocityScale) {
        ParticleSystemRenderer r = ps.GetComponent<ParticleSystemRenderer>();
        r.renderMode = ParticleSystemRenderMode.Stretch;
        r.velocityScale = velocityScale;
        r.lengthScale = 1.5f;
    }

    private static void Flare(GameObject root, Color color, float intensity, float range, float time) {
        GameObject go = new GameObject("Light");
        go.transform.SetParent(root.transform, false);
        go.transform.localPosition = Vector3.up * Grenades.M(0.5f);
        Light l = go.AddComponent<Light>();
        l.type = LightType.Point;
        l.color = color;
        l.range = range;
        l.intensity = intensity;
        l.shadows = LightShadows.None;
        go.AddComponent<FadingLight>().Time = time;
    }

    // ------------------------------------------------------------------ sound

    /// <summary>A 3D one-shot at <paramref name="at"/> that carries <paramref name="reach"/> world units.</summary>
    public static void PlayAt(AudioClip clip, Vector3 at, float volume, float reach) {
        if (clip == null) return;
        GameObject go = new GameObject("GrenadeSound");
        go.transform.position = at;
        AudioSource s = go.AddComponent<AudioSource>();
        s.clip = clip;
        s.volume = volume;
        s.spatialBlend = 1f;
        s.rolloffMode = AudioRolloffMode.Linear;
        s.minDistance = Grenades.M(4f);
        s.maxDistance = reach;
        s.dopplerLevel = 0f;
        s.Play();
        Object.Destroy(go, clip.length + 0.1f);
    }

    /// <summary>Synthesised once, on first use.</summary>
    public static class Sounds {

        private const int Rate = 22050;
        private static AudioClip explosion, bang, ring, hiss, pin, clink;

        public static AudioClip Explosion => explosion != null ? explosion : explosion = Make("grenade_explosion", 2.2f, Boom);
        public static AudioClip Bang => bang != null ? bang : bang = Make("grenade_flash", 1.2f, Crack);
        public static AudioClip Ring => ring != null ? ring : ring = Make("grenade_ring", 3.5f, Tinnitus);
        public static AudioClip Hiss => hiss != null ? hiss : hiss = Make("grenade_hiss", 3.5f, Sizzle);
        public static AudioClip Pin => pin != null ? pin : pin = Make("grenade_pin", 0.22f, PinPull);
        public static AudioClip Clink => clink != null ? clink : clink = Make("grenade_clink", 0.12f, Tink);

        private delegate float Voice(float t, float noise, ref float state);

        private static AudioClip Make(string name, float seconds, Voice voice) {
            int n = Mathf.CeilToInt(seconds * Rate);
            float[] data = new float[n];
            System.Random rng = new System.Random(name.GetHashCode());
            float state = 0f, peak = 0.0001f;
            for (int i = 0; i < n; i++) {
                float t = i / (float)Rate;
                data[i] = voice(t, (float)rng.NextDouble() * 2f - 1f, ref state);
                peak = Mathf.Max(peak, Mathf.Abs(data[i]));
            }
            for (int i = 0; i < n; i++) data[i] = data[i] / peak * 0.95f;
            AudioClip clip = AudioClip.Create(name, n, 1, Rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        /// <summary>Low-passed noise whose cutoff falls as it decays, over a deep thump.</summary>
        private static float Boom(float t, float noise, ref float lp) {
            float cutoff = Mathf.Lerp(0.5f, 0.03f, Mathf.Clamp01(t / 1.2f));
            lp += (noise - lp) * cutoff;
            float env = Mathf.Clamp01(t / 0.004f) * Mathf.Exp(-t * 2.4f);
            float crack = t < 0.04f ? noise * (1f - t / 0.04f) * 0.8f : 0f;
            float thump = Mathf.Sin(2f * Mathf.PI * (38f + 30f * Mathf.Exp(-t * 8f)) * t) * Mathf.Exp(-t * 4f);
            return lp * env * 2.2f + crack + thump * 0.9f;
        }

        private static float Crack(float t, float noise, ref float lp) {
            lp += (noise - lp) * 0.6f;
            float body = lp * Mathf.Exp(-t * 9f);
            float ring = Mathf.Sin(2f * Mathf.PI * 2900f * t) * Mathf.Exp(-t * 3.5f) * 0.18f;
            float thump = Mathf.Sin(2f * Mathf.PI * 70f * t) * Mathf.Exp(-t * 10f) * 0.6f;
            return body + ring + thump;
        }

        private static float Tinnitus(float t, float noise, ref float s) {
            float env = Mathf.Clamp01(t / 0.05f) * Mathf.Clamp01((3.5f - t) / 2.5f);
            return (Mathf.Sin(2f * Mathf.PI * 3700f * t) + 0.3f * Mathf.Sin(2f * Mathf.PI * 3712f * t)) * env;
        }

        private static float Sizzle(float t, float noise, ref float lp) {
            lp += (noise - lp) * 0.35f;
            float hp = noise - lp;
            float env = Mathf.Clamp01(t / 0.15f) * Mathf.Clamp01((3.5f - t) / 1.8f);
            return hp * env * (0.8f + 0.2f * Mathf.Sin(t * 37f));
        }

        private static float PinPull(float t, float noise, ref float s) {
            float a = t < 0.02f ? 1f - t / 0.02f : 0f;
            float b = t > 0.11f && t < 0.13f ? 1f - (t - 0.11f) / 0.02f : 0f;
            float ping = Mathf.Sin(2f * Mathf.PI * 4300f * t) * Mathf.Exp(-t * 30f) * 0.5f;
            return noise * (a + b * 0.7f) * 0.6f + ping;
        }

        private static float Tink(float t, float noise, ref float s) {
            float env = Mathf.Exp(-t * 45f);
            return (Mathf.Sin(2f * Mathf.PI * 3100f * t) * 0.6f + Mathf.Sin(2f * Mathf.PI * 5200f * t) * 0.3f + noise * 0.3f) * env;
        }

    }

}
