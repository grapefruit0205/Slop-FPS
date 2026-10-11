using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;
using SlopFPS.Input;

public enum Stance : byte { Stand, Crouch, Prone }

/// <summary>
/// Crouch (C), prone (Z), stand up (Space) and lean / peek (hold Q or E), PUBG style.
/// The owner picks the stance, resizes its own capsule and moves the camera; the server keeps the stance for checking
/// shots; every other machine poses the body and resizes the hitbox to match.
/// Sizes are in the avatar's own units (the root is scaled, so they scale with it).
/// </summary>
public class PlayerStance : NetworkBehaviour {

    private static readonly float[] CapsuleHeight = { 1.7f, 1.15f, 0.6f };
    private static readonly float[] EyeHeight = { 1.59f, 1.02f, 0.35f };
    private static readonly float[] EyeForward = { 0f, 0.1f, 0.5f };
    private static readonly float[] MoveScale = { 1f, 0.55f, 0.25f };
    private static readonly float[] Accuracy = { 1f, 0.8f, 0.55f };
    private const float CapsuleBottom = 0.05f;
    private const float StandingEye = 1.59f;
    private const float LeanAngle = 13f;           // degrees, the body tips over about the feet
    private const float LeanSpeed = 6f;            // full lean in ~0.17 s
    private const float ProneTime = 0.6f;          // getting down or up, during which the gun is lowered
    private const float CrouchTime = 0.2f;
    private const float MinChangeInterval = 0.12f; // server: stance changes faster than this are ignored

    /// <summary>
    /// The prone body, front to back, in the avatar's own units (for hitboxes and shot checks): the Mixamo prone clips
    /// lie with the hips over the feet position, the top of the head ~0.75 in front and the toes ~0.95 behind, the
    /// raised head and shoulders up to ~0.45.
    /// </summary>
    public const float ProneHead = 0.8f;
    public const float ProneFeet = -1.0f;
    public const float ProneHeight = 0.2f;
    public const float ProneRadius = 0.2f;

    private readonly SyncVar<byte> stance = new SyncVar<byte>();
    private readonly SyncVar<sbyte> lean = new SyncVar<sbyte>();

    private CharacterController cc;
    private FirstPersonController fpc;
    private PlayerHealth health;
    private Animator animator;
    private bool hasStanceParam;
    private Transform model;
    private Vector3 modelPosition;
    private Quaternion modelRotation;
    private CapsuleCollider proneHitbox;
    private Transform gunHolder;       // what the third-person gun hangs off (IKControl keeps it in the right hand)
    private Transform rightHand;
    private bool gunDropped;           // dead: the gun stays in the (no longer IK'd) right hand as the body falls
    private Vector3 gunInHand;
    private Quaternion gunInHandRotation;
    private Transform head;            // owner: inserted between the root and the camera

    private Stance local;              // owner: what the player chose (the SyncVar follows via the server)
    private float leanTarget, leanNow; // -1 left .. +1 right
    private float eyeNow = StandingEye, forwardNow;
    private float busyUntil;
    private float lastSentLean;
    private float lastSend;

    // Server: when each stance was last held, to forgive shots that cross a change.
    private readonly float[] heldAt = new float[3];
    private float lastChange;

    /// <summary>Dev tests (DevBot, DevShots): hold this stance and lean instead of reading the keys.</summary>
    public static Stance? DevStance;
    public static float DevLean;

    /// <summary>The local player's stance, for the gun and the HUD (null when not spawned).</summary>
    public static PlayerStance Local { get; private set; }

    public Stance Current => (Stance)stance.Value;
    /// <summary>-1 leaning left .. +1 leaning right.</summary>
    public float Lean => lean.Value / 100f;
    /// <summary>Spread and kick multiplier: steadier crouched, steadier still prone.</summary>
    public float AccuracyScale => Accuracy[(int)(IsOwner ? local : Current)];
    /// <summary>Getting down or up: the gun is lowered and won't fire.</summary>
    public bool Busy => Time.time < busyUntil;

    void Awake() {
        cc = GetComponent<CharacterController>();
        fpc = GetComponent<FirstPersonController>();
        health = GetComponent<PlayerHealth>();
        stance.OnChange += OnStanceChanged;

        // Lying down, the body runs front to back; a separate capsule covers it (others' shots only).
        GameObject hb = new GameObject("ProneHitbox") { layer = gameObject.layer, tag = "Player" };
        hb.transform.SetParent(transform, false);
        proneHitbox = hb.AddComponent<CapsuleCollider>();
        proneHitbox.direction = 2;
        proneHitbox.radius = ProneRadius;
        proneHitbox.height = ProneHead - ProneFeet;
        proneHitbox.center = new Vector3(0f, ProneHeight, (ProneHead + ProneFeet) / 2f);
        proneHitbox.enabled = false;
    }

    public override void OnStartClient() {
        IKControl ik = GetComponentInChildren<IKControl>(true);
        animator = ik != null ? ik.GetComponent<Animator>() : GetComponentInChildren<Animator>();
        if (animator != null) {
            model = animator.transform;
            modelPosition = model.localPosition;
            modelRotation = model.localRotation;
            foreach (AnimatorControllerParameter p in animator.parameters) {
                if (p.name == "Stance" && p.type == AnimatorControllerParameterType.Int) hasStanceParam = true;
            }
        }
        if (!IsOwner) {
            TpsGun tps = GetComponentInChildren<TpsGun>(true);
            if (tps != null && tps.transform.parent != null) gunHolder = tps.transform.parent;
            if (animator != null && animator.isHuman) rightHand = animator.GetBoneTransform(HumanBodyBones.RightHand);
            ApplyRemote(Current);
            return;
        }
        Local = this;
        // The camera hangs off a pivot at the feet: height and lean move and roll the pivot, so mouse look and head
        // bob keep working on the camera itself.
        Camera cam = GetComponentInChildren<Camera>(true);
        if (cam != null && cam.transform.parent == transform) {
            head = new GameObject("StanceHead").transform;
            head.SetParent(transform, false);
            cam.transform.SetParent(head, false);
        }
    }

    public override void OnStopClient() {
        if (Local == this) Local = null;
    }

    public override void OnStartServer() {
        float now = AntiCheat.Now;
        for (int i = 0; i < heldAt.Length; i++) heldAt[i] = -100f;
        heldAt[0] = now;
    }

    void Update() {
        if (!IsClientInitialized) return;
        if (IsOwner) {
            OwnerUpdate();
            return;
        }
    }

    void LateUpdate() {
        if (!IsClientInitialized || IsOwner || model == null) return;
        PoseBody();
    }

    // ------------------------------------------------------------------ owner

    private void OwnerUpdate() {
        bool alive = health == null || !health.IsDead;
        bool input = alive && !FpsInputGate.Blocked;
        Stance want = local;
        if (input) {
            if (Input.GetKeyDown(KeyCode.C)) want = local == Stance.Crouch ? Stance.Stand : Stance.Crouch;
            if (Input.GetKeyDown(KeyCode.Z)) want = local == Stance.Prone ? Stance.Stand : Stance.Prone;
            if (local != Stance.Stand && Input.GetButtonDown("Jump")) want = Stance.Stand;
            // Sprinting gets you up, as in PUBG.
            if (local == Stance.Crouch && Input.GetKey(KeyCode.LeftShift) && Input.GetAxisRaw("Vertical") > 0.1f) want = Stance.Stand;
        }
        if (DevStance.HasValue) want = DevStance.Value;
        // A body falls from the pose it was in (the animator picks the death by stance); the respawn is a new avatar.
        if (!alive) want = local;
        if (want != local) TryChange(want);

        leanTarget = 0f;
        if (input && local != Stance.Prone && !(fpc != null && fpc.IsSprinting)) {
            if (Input.GetKey(KeyCode.Q)) leanTarget -= 1f;
            if (Input.GetKey(KeyCode.E)) leanTarget += 1f;
        }
        if (DevStance.HasValue && local != Stance.Prone) leanTarget = DevLean;
        leanNow = Mathf.MoveTowards(leanNow, leanTarget, LeanSpeed * Time.deltaTime);
        leanNow = ClampLeanToWalls(leanNow);

        if (fpc != null) {
            fpc.SpeedScale = MoveScale[(int)local];
            fpc.CanRun = local == Stance.Stand;
            fpc.CanJump = local == Stance.Stand;
        }
        MoveCamera();
        SendPose();
    }

    private void TryChange(Stance want) {
        if (Time.time < busyUntil - 0.3f) return;
        if (CapsuleHeight[(int)want] > CapsuleHeight[(int)local] && !Clear(want)) return;
        float time = local == Stance.Prone || want == Stance.Prone ? ProneTime : CrouchTime;
        busyUntil = local == Stance.Prone || want == Stance.Prone ? Time.time + time : busyUntil;
        local = want;
        ApplyCapsule(want);
        // The animator's parameters travel to the other machines with the NetworkAnimator.
        if (hasStanceParam && animator != null) animator.SetInteger("Stance", (int)want);
        if (GameUI.Instance != null) GameUI.Instance.Hud.SetStance(want);
        lastSend = 0f;
    }

    /// <summary>Is there room to grow into <paramref name="to"/>? Standing up under a table should fail, not clip.</summary>
    private bool Clear(Stance to) {
        float scale = transform.lossyScale.y;
        float r = cc.radius * scale * 0.95f;
        Vector3 bottom = transform.position + Vector3.up * (CapsuleBottom * scale + r);
        Vector3 top = transform.position + Vector3.up * ((CapsuleBottom + CapsuleHeight[(int)to]) * scale - r);
        foreach (Collider c in Physics.OverlapCapsule(bottom + Vector3.up * 0.05f, top, r, ~LayerMask.GetMask("FPSPlayer", "FPSGun", "Hidden", "Ignore Raycast"),
                                                     QueryTriggerInteraction.Ignore)) {
            if (c.transform.IsChildOf(transform)) continue;
            return false;
        }
        return true;
    }

    private void ApplyCapsule(Stance s) {
        if (cc == null) return;
        float h = CapsuleHeight[(int)s];
        cc.height = Mathf.Max(h, cc.radius * 2f);
        cc.center = new Vector3(0f, CapsuleBottom + cc.height / 2f, 0f);
    }

    /// <summary>Lean no further than the walls allow, so nobody peeks or shoots through one.</summary>
    private float ClampLeanToWalls(float amount) {
        if (Mathf.Abs(amount) < 0.001f || head == null) return amount;
        float scale = transform.lossyScale.y;
        Vector3 eye = transform.position + Vector3.up * eyeNow * scale;
        Vector3 leaned = transform.position + transform.rotation * (Quaternion.Euler(0f, 0f, -amount * LeanAngle) * Vector3.up) * eyeNow * scale;
        Vector3 d = leaned - eye;
        float len = d.magnitude;
        if (len < 0.001f) return amount;
        const float radius = 0.2f;
        float free = len;
        foreach (RaycastHit h in Physics.SphereCastAll(eye, radius, d / len, len, LayerMask.GetMask("Default", "Shootable"), QueryTriggerInteraction.Ignore)) {
            if (h.collider.transform.IsChildOf(transform) || h.collider.GetComponentInParent<PlayerHealth>() != null) continue;
            free = Mathf.Min(free, h.distance);
        }
        return amount * Mathf.Clamp01(free / len);
    }

    private void MoveCamera() {
        if (head == null) return;
        float eyeTarget = EyeHeight[(int)local];
        float speed = local == Stance.Prone || eyeNow < EyeHeight[1] - 0.05f ? (StandingEye - EyeHeight[2]) / ProneTime : (StandingEye - EyeHeight[1]) / CrouchTime;
        eyeNow = Mathf.MoveTowards(eyeNow, eyeTarget, speed * Time.deltaTime);
        forwardNow = Mathf.MoveTowards(forwardNow, EyeForward[(int)local], Time.deltaTime);
        // The camera itself sits at the standing eye; the pivot carries it down and tips it over about the feet.
        Quaternion roll = Quaternion.Euler(0f, 0f, -leanNow * LeanAngle);
        head.localRotation = roll;
        head.localPosition = roll * new Vector3(0f, eyeNow - StandingEye, forwardNow);
    }

    private void SendPose() {
        sbyte leanByte = (sbyte)Mathf.RoundToInt(leanNow * 100f);
        bool leanMoved = Mathf.Abs(leanNow - lastSentLean) > 0.1f || (leanByte == 0 && lastSentLean != 0f) || (Mathf.Abs(leanByte) == 100 && Mathf.Abs(lastSentLean) < 1f);
        bool stanceMoved = (byte)local != stance.Value;
        if (!stanceMoved && !leanMoved) return;
        if (Time.time - lastSend < 0.1f && !stanceMoved) return;
        if (Time.time - lastSend < MinChangeInterval && stanceMoved) return;
        lastSend = Time.time;
        lastSentLean = leanByte / 100f;
        ServerPose((byte)local, leanByte);
    }

    [ServerRpc]
    private void ServerPose(byte s, sbyte leanValue) {
        if (s > (byte)Stance.Prone) {
            AntiCheat.Strike(Owner, "stance: malformed", 2);
            return;
        }
        float now = AntiCheat.Now;
        if (s != stance.Value) {
            if (now - lastChange < MinChangeInterval * 0.5f) return;
            heldAt[stance.Value] = now;
            lastChange = now;
            stance.Value = s;
            ApplyCapsule((Stance)s);
        }
        heldAt[s] = now;
        lean.Value = s == (byte)Stance.Prone ? (sbyte)0 : (sbyte)Mathf.Clamp(leanValue, -100, 100);
    }

    // ------------------------------------------------------------------ server

    /// <summary>Server: was the body in <paramref name="s"/> at any moment in the last <paramref name="window"/> seconds?</summary>
    public bool HeldWithin(Stance s, float window) {
        return stance.Value == (byte)s || AntiCheat.Now - heldAt[(int)s] <= window;
    }

    /// <summary>Server: did the stance change in the last <paramref name="window"/> seconds (the body was between poses)?</summary>
    public bool ChangedWithin(float window) {
        return AntiCheat.Now - lastChange <= window + ProneTime;
    }

    // ------------------------------------------------------------------ everyone else

    private void OnStanceChanged(byte prev, byte next, bool asServer) {
        if (asServer || IsOwner) return;
        ApplyRemote((Stance)next);
    }

    private void ApplyRemote(Stance s) {
        ApplyCapsule(s);
        if (proneHitbox != null) proneHitbox.enabled = s == Stance.Prone;
        if (hasStanceParam && animator != null) animator.SetInteger("Stance", (int)s);
    }

    /// <summary>
    /// The body as others see it: standing, crouching and lying down all come from the animator; leaning tips the model
    /// sideways about the feet, the same way the owner's camera moves. Once dead, the gun stays in the right hand.
    /// </summary>
    private void PoseBody() {
        Quaternion tipSide = Quaternion.Euler(0f, 0f, -Lean * LeanAngle);
        model.localRotation = tipSide * modelRotation;
        model.localPosition = tipSide * modelPosition;

        if (gunHolder == null || rightHand == null) return;
        bool dead = health != null && health.IsDead;
        if (!dead) {
            gunDropped = false;
            return;
        }
        if (!gunDropped) {
            gunDropped = true;
            gunInHand = rightHand.InverseTransformPoint(gunHolder.position);
            gunInHandRotation = Quaternion.Inverse(rightHand.rotation) * gunHolder.rotation;
        }
        gunHolder.SetPositionAndRotation(rightHand.TransformPoint(gunInHand), rightHand.rotation * gunInHandRotation);
    }

}
