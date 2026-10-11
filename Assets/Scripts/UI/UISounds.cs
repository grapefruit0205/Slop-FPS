using UnityEngine;

/// <summary>
/// Tiny procedurally generated UI/feedback sounds, so the HUD has audio cues
/// without shipping extra clips. Real recorded clips can replace these later.
/// </summary>
public class UISounds : MonoBehaviour {

    private static UISounds instance;
    private AudioSource source;
    private AudioClip click, hit, headshot, kill, dryFire;

    public static void Click() { Play(i => i.click, 0.35f); }
    public static void Hit(bool isHeadshot) { Play(i => isHeadshot ? i.headshot : i.hit, 0.55f); }
    public static void Kill() { Play(i => i.kill, 0.6f); }
    public static void DryFire() { Play(i => i.dryFire, 0.7f); }

    private static void Play(System.Func<UISounds, AudioClip> pick, float volume) {
        if (instance == null) {
            GameObject go = new GameObject("UISounds");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<UISounds>();
        }
        instance.source.PlayOneShot(pick(instance), volume);
    }

    void Awake() {
        source = gameObject.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.spatialBlend = 0f;
        click = Tone("ui_click", 0.035f, 2200f, 1400f, 0.0f, 60f);
        hit = Tone("hit_tick", 0.05f, 3200f, 2600f, 0.15f, 70f);
        headshot = Tone("hit_head", 0.09f, 4200f, 3000f, 0.25f, 40f);
        kill = Tone("hit_kill", 0.16f, 1600f, 900f, 0.2f, 22f);
        dryFire = Tone("dry_fire", 0.045f, 900f, 300f, 0.7f, 90f);
    }

    /// <summary>Decaying sine sweep with a little noise: a short metallic tick.</summary>
    private static AudioClip Tone(string name, float length, float f0, float f1, float noise, float decay) {
        const int rate = 44100;
        int n = Mathf.CeilToInt(length * rate);
        float[] data = new float[n];
        float phase = 0f;
        System.Random rng = new System.Random(name.GetHashCode());
        for (int i = 0; i < n; i++) {
            float t = i / (float)rate;
            float f = Mathf.Lerp(f0, f1, i / (float)n);
            phase += 2f * Mathf.PI * f / rate;
            float env = Mathf.Exp(-t * decay) * Mathf.Clamp01(i / 40f);
            float s = Mathf.Sin(phase) * (1f - noise) + ((float)rng.NextDouble() * 2f - 1f) * noise;
            data[i] = s * env * 0.8f;
        }
        AudioClip clip = AudioClip.Create(name, n, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }

}
