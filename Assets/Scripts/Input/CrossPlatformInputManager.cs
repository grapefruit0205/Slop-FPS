using UnityEngine;
namespace SlopFPS.Input {
    // Desktop keyboard/mouse input adapter. No Unity Standard Assets source is included.
    public static class CrossPlatformInputManager {
        public static float GetAxis(string name) => UnityEngine.Input.GetAxis(name);
        public static bool GetButton(string name) => UnityEngine.Input.GetButton(name);
        public static bool GetButtonDown(string name) => UnityEngine.Input.GetButtonDown(name);
    }
}
