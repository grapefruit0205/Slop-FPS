using System;
using UnityEngine;
using SlopFPS.Input;

/// <summary>
/// Player options persisted in PlayerPrefs and pushed to the systems that use them.
/// </summary>
public static class GameSettings {

    public static readonly Color[] CrosshairColors = {
        new Color(0.94f, 0.94f, 0.925f), new Color(0.486f, 1f, 0.42f),
        new Color(1f, 0.824f, 0.247f), new Color(0.263f, 0.851f, 1f),
    };

    public static event Action Changed;

    public static float Sensitivity { get; private set; }
    public static bool InvertY { get; private set; }
    public static float FieldOfView { get; private set; }
    public static float MasterVolume { get; private set; }
    public static int CrosshairColorIndex { get; private set; }
    public static Color CrosshairColor => CrosshairColors[CrosshairColorIndex];

    private static bool loaded;

    public static void Load() {
        if (loaded) return;
        loaded = true;
        Sensitivity = PlayerPrefs.GetFloat("opt.sensitivity", 2f);
        InvertY = PlayerPrefs.GetInt("opt.invertY", 0) == 1;
        FieldOfView = PlayerPrefs.GetFloat("opt.fov", 75f);
        MasterVolume = PlayerPrefs.GetFloat("opt.volume", 0.8f);
        CrosshairColorIndex = Mathf.Clamp(PlayerPrefs.GetInt("opt.crosshair", 0), 0, CrosshairColors.Length - 1);
        Apply();
    }

    public static void SetSensitivity(float v) { Sensitivity = v; PlayerPrefs.SetFloat("opt.sensitivity", v); Apply(); }
    public static void SetInvertY(bool v) { InvertY = v; PlayerPrefs.SetInt("opt.invertY", v ? 1 : 0); Apply(); }
    public static void SetFieldOfView(float v) { FieldOfView = v; PlayerPrefs.SetFloat("opt.fov", v); Apply(); }
    public static void SetMasterVolume(float v) { MasterVolume = v; PlayerPrefs.SetFloat("opt.volume", v); Apply(); }
    public static void SetCrosshairColor(int i) { CrosshairColorIndex = i; PlayerPrefs.SetInt("opt.crosshair", i); Apply(); }

    private static void Apply() {
        FpsInputGate.Sensitivity = Sensitivity;
        FpsInputGate.InvertY = InvertY;
        FpsInputGate.FieldOfView = FieldOfView;
        AudioListener.volume = MasterVolume;
        // The local player camera is the only one tagged MainCamera while alive.
        Camera cam = Camera.main;
        if (cam != null && cam.GetComponentInParent<FirstPersonController>() != null) {
            cam.fieldOfView = FieldOfView;
        }
        Changed?.Invoke();
    }

}
