using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Object;
using UnityEngine;

/// <summary>
/// Server side sanity checks for the client-authoritative avatar movement: horizontal speed, flying, and walking
/// through walls. An impossible move snaps the avatar back to its last good position (on the server and on the
/// owner) and counts as an <see cref="AntiCheat"/> strike. A body outside the map's play area (past the invisible walls of
/// <see cref="MapBounds"/>) or fallen through the floor goes back to the last place it was inside. Also keeps a short position history that hit
/// validation uses to see where a target was when the shooter fired (lag compensation).
/// </summary>
public class MovementGuard : NetworkBehaviour {

    // FirstPersonController: run 8 m/s, jump speed 8 with gravity x2 (apex ~1.6 m, ~0.8 s in the air).
    private const float MaxSpeed = 8f;
    private const float SpeedTolerance = 1.35f;
    private const float SpeedSlack = 1.5f;          // metres; covers jitter in how updates arrive
    private const float CheckInterval = 0.5f;
    private const float MaxRise = 2.3f;             // highest legal point above the last ground contact
    private const float MaxHoverTime = 2.5f;        // in the air without falling
    private const float ChestHeight = 1.0f;
    private const float GraceAfterCorrection = 0.75f;
    private const float HistoryLength = 1.2f;
    private const float BoundsSlack = 1.0f;         // world units past the outline (the walls keep the body ~0.5 inside it)
    private const float FallLimit = 10f;            // world units under the play area's floor

    private struct Sample {
        public float Time;
        public Vector3 Position;
        public Quaternion Yaw;
        public float Lean;
    }

    private static bool loggedMask;
    private readonly List<Sample> history = new List<Sample>();
    private PlayerStance stance;
    private Vector3 anchor;            // last position that passed the speed check
    private float anchorTime;
    private Vector3 lastPos;           // previous frame, for the wall check
    private float groundY;
    private Vector3 lastSeen;
    private float lastChange;
    private float airTime;
    private float graceUntil;
    private PlayerHealth health;
    private CharacterController controller;
    private int worldMask;
    private Vector3 lastInside;        // last position inside the map's play area

    void Awake() {
        stance = GetComponent<PlayerStance>();
        health = GetComponent<PlayerHealth>();
        controller = GetComponent<CharacterController>();
    }

    public override void OnStartServer() {
        worldMask = BodyCollisionMask();
        if (!loggedMask) {
            loggedMask = true;
            string names = "";
            for (int i = 0; i < 32; i++) if ((worldMask & (1 << i)) != 0) names += LayerMask.LayerToName(i) + ",";
            Debug.Log("[MovementGuard] Body collides with: " + names);
        }
        Rebase(transform.position);
        lastInside = transform.position;
    }

    /// <summary>
    /// Layers the owner's body (FPSPlayer) actually collides with, minus other players: anything in here is a floor
    /// to stand on or a wall the CharacterController cannot pass, exactly as the physics settings define it.
    /// </summary>
    public static int BodyCollisionMask() {
        int body = LayerMask.NameToLayer("FPSPlayer");
        int mask = 0;
        for (int i = 0; i < 32; i++) {
            if (!Physics.GetIgnoreLayerCollision(body, i)) mask |= 1 << i;
        }
        return mask & ~LayerMask.GetMask("FPSPlayer", "FPSGun", "TPSPlayer", "TPSGun", "Hidden", "UI");
    }

    private void Rebase(Vector3 pos) {
        anchor = pos;
        anchorTime = AntiCheat.Now;
        lastPos = pos;
        groundY = pos.y;
        airTime = 0f;
    }

    private float traceAt;
    private float lastUpdate;

    void Update() {
        if (NetConfig.TraceMovement && Time.time > traceAt) {
            traceAt = Time.time + 0.25f;
            Debug.Log("[MovementGuard] " + (IsServerInitialized ? "server" : IsOwner ? "owner" : "observer") + " sees client " + OwnerId +
                      " (object " + ObjectId + ") at " + transform.position.ToString("F2") + " @" + System.DateTime.Now.ToString("HH:mm:ss.fff"));
        }
        if (!IsServerInitialized || Owner == null || !Owner.IsActive) return;
        float now = AntiCheat.Now;
        float dt = lastUpdate > 0f ? now - lastUpdate : 0f;
        lastUpdate = now;
        Vector3 pos = transform.position;
        Record(pos, now);
        if (health != null && health.IsDead) {
            Rebase(pos);
            return;
        }
        // Left the arena (the walls should make that impossible) or fell out of the world: back inside, whatever else.
        MapDefinition map = MapLoader.Current;
        if (map != null && map.HasBoundary) {
            bool outside = !map.InPlayArea(pos, BoundsSlack);
            if (outside || pos.y < map.FloorY - FallLimit) {
                // Just sent back: updates from before the owner got there are still arriving.
                if (now < graceUntil) return;
                OutOfBounds(map, pos, outside ? "outside the play area" : (map.FloorY - pos.y).ToString("F1") + " under the floor");
                return;
            }
            lastInside = pos;
        }
        if (now < graceUntil) {
            Rebase(pos);
            return;
        }

        // Through a wall since last frame? Moves are short, so a straight segment at chest height is enough (lower
        // when crouched or lying down, so crawling under things isn't walking through them).
        float chest = ChestHeight;
        if (stance != null && stance.HeldWithin(Stance.Crouch, 0.5f)) chest = Mathf.Min(chest, 0.7f);
        if (stance != null && stance.HeldWithin(Stance.Prone, 0.5f)) chest = Mathf.Min(chest, 0.25f);
        Vector3 from = lastPos + Vector3.up * chest;
        Vector3 to = pos + Vector3.up * chest;
        if ((to - from).sqrMagnitude > 0.0001f && Blocked(from, to, out string wall)) {
            Violation("moved through geometry (" + wall + ")", lastPos, 2);
            return;
        }
        lastPos = pos;

        // Flying: rising far above the last ground contact, or hanging in the air. Hang time only counts while
        // updates keep arriving: a client whose tab was paused mid-jump simply stops sending, and is not flying.
        if ((pos - lastSeen).sqrMagnitude > 0.000001f) {
            lastSeen = pos;
            lastChange = now;
        }
        if (Grounded(pos)) {
            groundY = pos.y;
            airTime = 0f;
        } else {
            if (now - lastChange < 0.3f) airTime += dt;
            if (pos.y - groundY > MaxRise) {
                Violation("too high above ground (" + (pos.y - groundY).ToString("F1") + " m)", anchor, 2);
                return;
            }
            if (airTime > MaxHoverTime && pos.y >= anchor.y - 0.5f) {
                Violation("airborne for " + airTime.ToString("F1") + " s", anchor, 2);
                return;
            }
        }

        // Speed / teleport, measured over a window so single late packets don't count.
        float elapsed = now - anchorTime;
        if (elapsed >= CheckInterval) {
            Vector3 delta = pos - anchor;
            delta.y = 0f;
            float allowed = MaxSpeed * SpeedTolerance * elapsed + SpeedSlack;
            if (delta.magnitude > allowed) {
                Violation("moved " + delta.magnitude.ToString("F1") + " m in " + elapsed.ToString("F2") + " s", anchor, 1);
                return;
            }
            anchor = pos;
            anchorTime = now;
        }
    }

    private bool Blocked(Vector3 from, Vector3 to, out string wall) {
        Vector3 d = to - from;
        float len = d.magnitude;
        foreach (RaycastHit hit in Physics.RaycastAll(from, d / len, len, worldMask, QueryTriggerInteraction.Ignore)) {
            if (hit.collider.GetComponentInParent<PlayerHealth>() != null) continue;
            wall = hit.collider.name;
            return true;
        }
        wall = null;
        return false;
    }

    private bool Grounded(Vector3 pos) {
        if (controller != null && controller.isGrounded) return true;
        // Server-side the controller does not move itself, so probe for the floor under the feet.
        return Physics.SphereCast(pos + Vector3.up * 0.5f, 0.25f, Vector3.down, out _, 0.75f, worldMask,
                                  QueryTriggerInteraction.Ignore);
    }

    private void Violation(string what, Vector3 backTo, int weight) {
        Vector3 pos = transform.position;
        string below = Physics.Raycast(pos + Vector3.up * 0.5f, Vector3.down, out RaycastHit h, 50f, ~0, QueryTriggerInteraction.Ignore)
            ? h.collider.name + "/" + LayerMask.LayerToName(h.collider.gameObject.layer) + " " + (pos.y - h.point.y).ToString("F2") + " m below"
            : "nothing below";
        what += " at " + pos.ToString("F1") + ", ground " + below;
        AntiCheat.Strike(Owner, "movement: " + what, weight);
        Snap(backTo);
    }

    private void OutOfBounds(MapDefinition map, Vector3 pos, string what) {
        Debug.LogWarning("[MapBounds] Client " + OwnerId + " at " + pos.ToString("F1") + " is " + what + " of " + map.id +
                         "; back to " + lastInside.ToString("F1"));
        AntiCheat.Strike(Owner, "movement: " + what + " at " + pos.ToString("F1"), 1);
        Snap(lastInside);
    }

    /// <summary>Server: put the avatar somewhere legal and tell its owner to go there too.</summary>
    [Server]
    public void Snap(Vector3 pos) {
        transform.position = pos;
        Rebase(pos);
        graceUntil = AntiCheat.Now + GraceAfterCorrection;
        RpcSnap(Owner, pos);
    }

    [TargetRpc]
    private void RpcSnap(NetworkConnection conn, Vector3 pos) {
        // The CharacterController keeps its own idea of the position unless it is disabled while moving it.
        bool was = controller != null && controller.enabled;
        if (controller != null) controller.enabled = false;
        transform.position = pos;
        if (controller != null) controller.enabled = was;
    }

    private void Record(Vector3 pos, float now) {
        history.Add(new Sample { Time = now, Position = pos, Yaw = Yaw(), Lean = stance != null ? stance.Lean : 0f });
        int drop = 0;
        while (drop < history.Count && now - history[drop].Time > HistoryLength) drop++;
        if (drop > 0) history.RemoveRange(0, drop);
    }

    /// <summary>
    /// Server: was this avatar's body within <paramref name="radius"/> of <paramref name="point"/> at some moment in the
    /// last <paramref name="window"/> seconds? The body is a line through the middle of the capsule: upright from the
    /// feet to the top of the head (tipped sideways when leaning) or, lying down, along the ground from the feet to the
    /// head. Every stance held in the window counts. Returns whether the point was at the head end then, and how far it
    /// was from the body.
    /// </summary>
    public bool WasNear(Vector3 point, float window, float radius, out bool atHead, out float distance) {
        atHead = false;
        float best = float.MaxValue;
        float now = AntiCheat.Now;
        // Halfway between poses (getting down takes a moment) the limbs are off both lines.
        if (stance != null && stance.ChangedWithin(window)) radius *= 1.6f;
        bool any = false;
        for (int i = history.Count - 1; i >= 0; i--) {
            Sample sample = history[i];
            if (now - sample.Time > window) break;
            any = true;
            Closest(sample, point, window, ref best, ref atHead);
        }
        if (!any) {
            Closest(new Sample { Time = now, Position = transform.position, Yaw = Yaw(), Lean = stance != null ? stance.Lean : 0f },
                    point, window, ref best, ref atHead);
        }
        distance = best;
        return best <= radius;
    }

    private void Closest(Sample sample, Vector3 point, float window, ref float best, ref bool atHead) {
        float scale = transform.lossyScale.y;
        for (int s = 0; s < 3; s++) {
            Stance pose = (Stance)s;
            if (stance == null ? pose != Stance.Stand : !stance.HeldWithin(pose, window)) continue;
            Vector3 a, b;
            float headFrom;
            if (pose == Stance.Prone) {
                a = sample.Position + sample.Yaw * new Vector3(0f, PlayerStance.ProneHeight, PlayerStance.ProneFeet) * scale;
                b = sample.Position + sample.Yaw * new Vector3(0f, PlayerStance.ProneHeight, PlayerStance.ProneHead) * scale;
                headFrom = 1f - HeadSize / (PlayerStance.ProneHead - PlayerStance.ProneFeet);
            } else {
                float h = pose == Stance.Stand ? StandingTop : CrouchingTop;
                // Crouched, the soldier leans in over the rifle: the head is ahead of the feet.
                float lean = pose == Stance.Stand ? 0f : CrouchingLean;
                a = sample.Position;
                b = sample.Position + sample.Yaw * (Quaternion.Euler(0f, 0f, -sample.Lean * LeanAngle) * new Vector3(0f, h, lean)) * scale;
                headFrom = 1f - HeadSize / h;
            }
            Vector3 ab = b - a;
            float t = Mathf.Clamp01(Vector3.Dot(point - a, ab) / ab.sqrMagnitude);
            float d = Vector3.Distance(point, a + ab * t);
            if (d < best) {
                best = d;
                atHead = t >= headFrom;
            }
        }
    }

    // The body in the avatar's own units (the root is scaled): top of the head standing and crouched (the Mixamo soldiers:
    // 1.72 standing, 1.23 crouched still, 1.36 crouch-walking with the head ~0.1-0.4 ahead of the feet), the head's share.
    private const float StandingTop = 1.75f;
    private const float CrouchingTop = 1.25f;
    private const float CrouchingLean = 0.2f;
    private const float HeadSize = 0.4f;
    private const float LeanAngle = 13f;

    private Quaternion Yaw() {
        Vector3 f = transform.forward;
        f.y = 0f;
        return f.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(f) : Quaternion.identity;
    }

}
