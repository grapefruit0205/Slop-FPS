using UnityEngine;
namespace SlopFPS.Input {
    /// <summary>Original minimal controller for the public source edition. Attach to an avatar with a child camera.</summary>
    [RequireComponent(typeof(CharacterController))]
    public sealed class FirstPersonController : MonoBehaviour {
        [SerializeField] private float walkSpeed = 5f;
        [SerializeField] private float runSpeed = 8f;
        [SerializeField] private float jumpSpeed = 8f;
        [SerializeField] private float gravityScale = 2f;
        public float SpeedScale = 1f;
        public bool CanRun = true;
        public bool CanJump = true;
        public bool IsSprinting { get; private set; }
        public bool IsGrounded => body != null && body.isGrounded;
        private CharacterController body;
        private Transform view;
        private float pitch, verticalSpeed;
        private void Start() {
            body = GetComponent<CharacterController>();
            Camera camera = GetComponentInChildren<Camera>(true);
            view = camera != null ? camera.transform : null;
            if (view != null) pitch = Mathf.DeltaAngle(0f, view.localEulerAngles.x);
        }
        private void Update() {
            if (body == null) return;
            bool blocked = FpsInputGate.Blocked;
            bool frozen = blocked || FpsInputGate.Frozen;
            if (!blocked && view != null) {
                float sensitivity = FpsInputGate.Sensitivity * FpsInputGate.SensitivityScale;
                transform.Rotate(0f, UnityEngine.Input.GetAxis("Mouse X") * sensitivity, 0f);
                float y = UnityEngine.Input.GetAxis("Mouse Y") * sensitivity;
                pitch = Mathf.Clamp(pitch + (FpsInputGate.InvertY ? y : -y), -85f, 85f);
                view.localRotation = Quaternion.Euler(pitch, 0f, 0f);
            }
            Vector2 input = frozen ? Vector2.zero : new Vector2(UnityEngine.Input.GetAxis("Horizontal"), UnityEngine.Input.GetAxis("Vertical"));
            input = Vector2.ClampMagnitude(input, 1f);
            IsSprinting = !frozen && CanRun && input.y > 0f && UnityEngine.Input.GetKey(KeyCode.LeftShift);
            Vector3 move = (transform.right * input.x + transform.forward * input.y) * (IsSprinting ? runSpeed : walkSpeed) * SpeedScale;
            if (body.isGrounded) {
                verticalSpeed = -2f;
                if (!frozen && CanJump && UnityEngine.Input.GetButtonDown("Jump")) verticalSpeed = jumpSpeed;
            } else verticalSpeed += Physics.gravity.y * gravityScale * Time.deltaTime;
            move.y = verticalSpeed;
            body.Move(move * Time.deltaTime);
        }
        private void OnDisable() { IsSprinting = false; verticalSpeed = 0f; }
    }
}
