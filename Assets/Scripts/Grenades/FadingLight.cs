using UnityEngine;

/// <summary>A light that dies away over <see cref="Time"/> seconds (grenade blasts). Its own file so prefab-style copies keep it.</summary>
public class FadingLight : MonoBehaviour {

    public float Time = 0.2f;

    private Light l;
    private float start, age;

    void OnEnable() {
        l = GetComponent<Light>();
        start = l.intensity;
        age = 0f;
    }

    void Update() {
        age += UnityEngine.Time.deltaTime;
        float k = 1f - age / Time;
        if (k <= 0f) {
            l.enabled = false;
            enabled = false;
            return;
        }
        l.intensity = start * k * k;
    }

}
