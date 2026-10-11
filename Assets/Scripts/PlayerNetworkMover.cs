using FishNet.Object;
using UnityEngine;
using SlopFPS.Input;
using SlopFPS.Input;

/// <summary>
/// Turns a spawned avatar into either "me" (first-person camera and controls) or "someone else" (third-person body).
/// Position, rotation and animator state are replicated by the NetworkTransform / NetworkAnimator on the prefab,
/// both client-authoritative so movement feels exactly like single player.
/// </summary>
[RequireComponent(typeof(FirstPersonController))]
public class PlayerNetworkMover : NetworkBehaviour {

    [SerializeField]
    private Animator animator;
    [SerializeField]
    private GameObject cameraObject;
    [SerializeField]
    private GameObject gunObject;
    [SerializeField]
    private GameObject playerObject;
    [SerializeField]
    private NameTag nameTag;

    private FpsGun fpsGun;
    private FirstPersonController controller;

    /// <summary>Dev tests (DevBot): the movement input the body animates with, when the bot moves it itself.</summary>
    public static Vector2? DevInput;

    /// <summary>The avatar this machine controls, if it is alive.</summary>
    public static PlayerNetworkMover Local { get; private set; }

    void MoveToLayer(GameObject go, int layer) {
        go.layer = layer;
        foreach (Transform child in go.transform) {
            MoveToLayer(child.gameObject, layer);
        }
    }

    public override void OnStartClient() {
        Debug.Log("[Net] Avatar " + name + " (object " + ObjectId + ") spawned for client " + OwnerId + " at " + transform.position.ToString("F2") + (IsOwner ? " (mine)" : "") + " @" + System.DateTime.Now.ToString("HH:mm:ss.fff"));
        if (!IsOwner) return;
        Local = this;
        fpsGun = GetComponentInChildren<FpsGun>(true);
        controller = GetComponent<FirstPersonController>();
        // FirstPersonController reads the camera in its Start, so the camera goes live before the controller.
        cameraObject.SetActive(true);
        GetComponent<FirstPersonController>().enabled = true;
        MoveToLayer(gunObject, LayerMask.NameToLayer("Hidden"));
        MoveToLayer(playerObject, LayerMask.NameToLayer("Hidden"));
        // Your own callsign floats right over the camera; looking up would show it from below.
        if (nameTag != null) MoveToLayer(nameTag.gameObject, LayerMask.NameToLayer("Hidden"));
        if (GameNetwork.Instance != null) GameNetwork.Instance.OnLocalAvatarSpawned(this);
    }

    public override void OnStopClient() {
        if (Local != this) return;
        Local = null;
        if (GameNetwork.Instance != null) GameNetwork.Instance.OnLocalAvatarDespawned();
    }

    void FixedUpdate() {
        if (!IsOwner) return;
        bool blocked = FpsInputGate.Blocked || FpsInputGate.Frozen;
        Vector2 move = DevInput ?? (blocked ? Vector2.zero : new Vector2(CrossPlatformInputManager.GetAxis("Horizontal"), CrossPlatformInputManager.GetAxis("Vertical")));
        animator.SetFloat("Horizontal", move.x);
        animator.SetFloat("Vertical", move.y);
        // Crouched or lying down, jump stands you up instead (and a trigger left set would jump later).
        if (!blocked && CrossPlatformInputManager.GetButtonDown("Jump") && (controller == null || controller.CanJump)) {
            animator.SetTrigger("IsJumping");
        }
        animator.SetBool("Running", !blocked && Input.GetKey(KeyCode.LeftShift));
        // Aiming or shooting: the third-person body shoulders the rifle.
        animator.SetBool("Firing", fpsGun != null && fpsGun.Shouldered);
    }

}
