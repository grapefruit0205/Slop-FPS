namespace SlopFPS.Input
{
    // Shared switches the game UI (compiled in Assembly-CSharp) uses to steer the
    // first-person scripts here in the firstpass assembly, which cannot reference it.
    public static class FpsInputGate
    {
        // True while a menu or overlay owns the mouse; movement, look and fire are ignored.
        public static bool Blocked;
        // True while the round is frozen (elimination): look around, but no moving, jumping or firing.
        public static bool Frozen;
        public static float Sensitivity = 2f;
        // Multiplies Sensitivity while zoomed in, so aim moves the same distance on screen.
        public static float SensitivityScale = 1f;
        public static bool InvertY;
        // Vertical field of view applied to the player camera; 0 keeps the camera's own value.
        public static float FieldOfView;
    }
}
