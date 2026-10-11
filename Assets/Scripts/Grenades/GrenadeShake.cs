using UnityEngine;

/// <summary>
/// Shakes what a camera shows after a nearby blast. Only the rendered view moves (its view matrix, just before
/// culling): the camera's transform, the aim and the mouse look are left alone, so shots still go where the crosshair is.
/// </summary>
[RequireComponent(typeof(Camera))]
public class GrenadeShake : MonoBehaviour {

    private const float MaxDegrees = 2.2f;
    private const float Decay = 2.2f;      // per second
    private const float Frequency = 23f;

    private Camera cam;
    private float amount;
    private float seed;
    private bool shifted;

    /// <summary>Shake <paramref name="cam"/> by <paramref name="strength"/> (0..1), on top of any shake under way.</summary>
    public static void Add(Camera cam, float strength) {
        if (cam == null || strength <= 0f) return;
        GrenadeShake s = cam.GetComponent<GrenadeShake>();
        if (s == null) s = cam.gameObject.AddComponent<GrenadeShake>();
        s.amount = Mathf.Min(1f, s.amount + strength);
        s.seed = Random.value * 100f;
        s.enabled = true;
    }

    void Awake() {
        cam = GetComponent<Camera>();
    }

    void Update() {
        amount = Mathf.MoveTowards(amount, 0f, Decay * Time.deltaTime * Mathf.Max(0.3f, amount));
    }

    void OnPreCull() {
        if (cam == null) return;
        cam.ResetWorldToCameraMatrix();
        shifted = false;
        if (amount <= 0.001f) {
            enabled = false;
            return;
        }
        float t = Time.time * Frequency + seed;
        float a = amount * amount * MaxDegrees;
        Quaternion q = Quaternion.Euler((Mathf.PerlinNoise(t, 0.3f) - 0.5f) * 2f * a, (Mathf.PerlinNoise(0.7f, t) - 0.5f) * 2f * a,
                                        (Mathf.PerlinNoise(t, t * 0.5f) - 0.5f) * a);
        cam.worldToCameraMatrix = Matrix4x4.Rotate(q) * cam.worldToCameraMatrix;
        shifted = true;
    }

    void OnPostRender() {
        // Back to the transform's view straight after drawing, so aiming between frames is unshaken.
        if (shifted && cam != null) cam.ResetWorldToCameraMatrix();
        shifted = false;
    }

    void OnDisable() {
        if (shifted && cam != null) cam.ResetWorldToCameraMatrix();
        shifted = false;
    }

}
