using System.Collections.Generic;
using UnityEngine;

/// <summary>A grenade in the air or on the floor, in world units.</summary>
public struct GrenadeBody {
    public Vector3 Position;
    public Vector3 Velocity;
    public bool Resting;
    public float Age;           // seconds since the throw
    public float RestTime;      // seconds lying still
}

/// <summary>
/// How a thrown grenade flies, bounces, rolls and comes to rest. A small fixed-step simulation instead of a Rigidbody:
/// the server flies the real grenade with it, every client flies its copy from the same start with the same steps (so
/// all of them land in the same place without streaming positions), and the thrower's arc preview is the same code.
/// Players don't stop grenades; walls, floors and the play area's invisible walls do. Also holds the line-of-sight
/// test the blasts use.
/// </summary>
public static class GrenadeFlight {

    private const float Skin = 0.01f;            // world units kept between the grenade and a surface
    private const float ContactSpeed = 1.2f;     // m/s: slower hits are resting contact, not bounces

    private static int worldMask = -1, sightMask = -1;
    private static readonly RaycastHit[] hits = new RaycastHit[16];
    private static readonly Dictionary<int, bool> playerColliders = new Dictionary<int, bool>();

    /// <summary>What grenades bounce off: level geometry and the invisible play-area walls.</summary>
    public static int WorldMask {
        get {
            if (worldMask == -1) worldMask = LayerMask.GetMask("Default", "Shootable", "Ignore Raycast");
            return worldMask;
        }
    }

    /// <summary>What blocks a blast or a flash: level geometry (bodies don't shield each other).</summary>
    public static int SightMask {
        get {
            if (sightMask == -1) sightMask = LayerMask.GetMask("Default", "Shootable");
            return sightMask;
        }
    }

    /// <summary>The velocity a throw along <paramref name="aim"/> leaves the hand with (world units/s).</summary>
    public static Vector3 Launch(Vector3 aim, bool overhand) {
        aim.Normalize();
        Vector3 side = Vector3.Cross(Vector3.up, aim);
        if (side.sqrMagnitude < 0.0001f) side = Vector3.right;
        float lift = overhand ? Grenades.OverhandLift : Grenades.UnderhandLift;
        Vector3 dir = Quaternion.AngleAxis(-lift, side.normalized) * aim;
        return dir * Grenades.M(overhand ? Grenades.OverhandSpeed : Grenades.UnderhandSpeed);
    }

    /// <summary>Where the grenade leaves the hand: a little in front of the eye, short of any wall right there.</summary>
    public static Vector3 StartPoint(Vector3 eye, Vector3 aim) {
        aim.Normalize();
        float ahead = Grenades.M(Grenades.LaunchAhead);
        float r = Grenades.M(Grenades.BodyRadius);
        if (Cast(eye, r, aim, ahead + r, out RaycastHit hit)) ahead = Mathf.Max(0f, hit.distance - r);
        return eye + aim * ahead;
    }

    /// <summary>
    /// Advance <paramref name="b"/> by <paramref name="dt"/> (use <see cref="Grenades.SimStep"/>). Returns the speed
    /// (m/s) of a bounce in this step, or 0.
    /// </summary>
    public static float Step(ref GrenadeBody b, float dt) {
        b.Age += dt;
        if (b.Resting) {
            b.RestTime += dt;
            return 0f;
        }
        float g = Grenades.M(Grenades.Gravity);
        float r = Grenades.M(Grenades.BodyRadius);
        b.Velocity += Vector3.down * g * dt;
        Vector3 move = b.Velocity * dt;
        float len = move.magnitude;
        float bounce = 0f;
        bool onFloor = false;
        if (len > 1e-6f) {
            Vector3 dir = move / len;
            if (Cast(b.Position, r, dir, len + Skin, out RaycastHit hit)) {
                b.Position += dir * Mathf.Max(0f, hit.distance - Skin);
                Vector3 n = hit.normal;
                float into = -Vector3.Dot(b.Velocity, n);
                if (into > 0f) {
                    Vector3 slide = b.Velocity + n * into;
                    if (into > Grenades.M(ContactSpeed)) {
                        b.Velocity = slide * (1f - Grenades.BounceFriction) + n * into * Grenades.Restitution;
                        bounce = into / Grenades.M(1f);
                    } else {
                        // Resting contact: keep the slide, lose the push into the surface.
                        b.Velocity = slide;
                    }
                }
                onFloor = n.y > 0.7f;
            } else {
                b.Position += move;
            }
        }
        // Rolling along the floor slows it down until it lies still.
        if (onFloor || Grounded(b.Position, r)) {
            Vector3 flat = new Vector3(b.Velocity.x, 0f, b.Velocity.z);
            float speed = flat.magnitude;
            float drop = Grenades.M(Grenades.RollDrag) * dt;
            flat = speed > drop ? flat * ((speed - drop) / speed) : Vector3.zero;
            b.Velocity = new Vector3(flat.x, b.Velocity.y, flat.z);
            if (b.Velocity.magnitude < Grenades.M(Grenades.RestSpeed)) {
                b.Velocity = Vector3.zero;
                b.Resting = true;
            }
        }
        return bounce;
    }

    /// <summary>The arc a throw would take, sampled every few steps, up to the first rest or <paramref name="seconds"/>.</summary>
    public static int Predict(Vector3 start, Vector3 velocity, float seconds, Vector3[] into) {
        GrenadeBody b = new GrenadeBody { Position = start, Velocity = velocity };
        int n = 0;
        into[n++] = start;
        int steps = Mathf.CeilToInt(seconds / Grenades.SimStep);
        for (int i = 1; i <= steps && n < into.Length; i++) {
            float bounce = Step(ref b, Grenades.SimStep);
            if (i % 2 == 0 || bounce > 0f || b.Resting) into[n++] = b.Position;
            if (b.Resting) break;
        }
        return n;
    }

    /// <summary>Nothing solid between <paramref name="from"/> and <paramref name="to"/> (players don't count).</summary>
    public static bool Clear(Vector3 from, Vector3 to) {
        Vector3 d = to - from;
        float len = d.magnitude;
        if (len < 0.01f) return true;
        int n = Physics.RaycastNonAlloc(from, d / len, hits, len, SightMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++) {
            if (!IsPlayer(hits[i].collider)) return false;
        }
        return true;
    }

    // ---------------------------------------------------------------- smoke clouds

    /// <summary>How much of its full size a cloud <paramref name="age"/> seconds old has (it billows out, then thins).</summary>
    public static float SmokeSize(float age) {
        return Mathf.Lerp(0.3f, 1f, Mathf.SmoothStep(0f, 1f, age / Grenades.SmokeBloom));
    }

    /// <summary>How thick a cloud <paramref name="age"/> seconds old is (0 once it has gone).</summary>
    public static float SmokeThickness(float age) {
        float left = Grenades.SmokeDuration - age;
        if (age < 0f || left <= 0f) return 0f;
        return Mathf.Clamp01(left / Grenades.SmokeFadeOut) * Mathf.Clamp01(age / 0.4f);
    }

    /// <summary>The middle of a cloud whose canister lies at <paramref name="floor"/>, and its half-extents.</summary>
    public static void SmokeShape(Vector3 floor, float age, out Vector3 center, out Vector3 radii) {
        float s = SmokeSize(age);
        float r = Grenades.M(Grenades.Get(GrenadeId.Smoke).Radius) * s;
        float h = Grenades.M(Grenades.SmokeHeight) * s;
        center = floor + Vector3.up * h * 0.45f;
        radii = new Vector3(r, h * 0.55f, r);
    }

    /// <summary>0 outside the cloud, rising to 1 well inside it (for the fog over the screen of someone standing in it).</summary>
    public static float SmokeDensity(Vector3 floor, float age, Vector3 point) {
        float thick = SmokeThickness(age);
        if (thick <= 0f) return 0f;
        SmokeShape(floor, age, out Vector3 c, out Vector3 radii);
        Vector3 q = Vector3.Scale(point - c, new Vector3(1f / radii.x, 1f / radii.y, 1f / radii.z));
        return Mathf.Clamp01((1f - q.magnitude) / 0.3f) * thick;
    }

    /// <summary>Does the line from <paramref name="a"/> to <paramref name="b"/> pass through the thick of a cloud?</summary>
    public static bool SmokeBlocks(Vector3 floor, float age, Vector3 a, Vector3 b) {
        if (SmokeThickness(age) < 0.35f) return false;
        SmokeShape(floor, age, out Vector3 c, out Vector3 radii);
        Vector3 inv = new Vector3(1f / radii.x, 1f / radii.y, 1f / radii.z);
        Vector3 qa = Vector3.Scale(a - c, inv), qb = Vector3.Scale(b - c, inv);
        Vector3 ab = qb - qa;
        float t = ab.sqrMagnitude > 1e-8f ? Mathf.Clamp01(-Vector3.Dot(qa, ab) / ab.sqrMagnitude) : 0f;
        return (qa + ab * t).magnitude < 0.8f;
    }

    private static bool Grounded(Vector3 pos, float r) {
        return Cast(pos, r * 0.9f, Vector3.down, r * 0.3f + Skin * 2f, out RaycastHit hit) && hit.normal.y > 0.7f;
    }

    /// <summary>The nearest thing (other than a player) a sphere moving along <paramref name="dir"/> would touch.</summary>
    private static bool Cast(Vector3 from, float radius, Vector3 dir, float distance, out RaycastHit nearest) {
        nearest = default;
        int n = Physics.SphereCastNonAlloc(from, radius, dir, hits, distance, WorldMask, QueryTriggerInteraction.Ignore);
        bool any = false;
        for (int i = 0; i < n; i++) {
            RaycastHit h = hits[i];
            // A cast that starts touching something reports it at distance 0 with no useful normal: skip it, the
            // grenade is leaving it.
            if (h.distance <= 0f && h.point == Vector3.zero) continue;
            if (IsPlayer(h.collider)) continue;
            if (!any || h.distance < nearest.distance) {
                nearest = h;
                any = true;
            }
        }
        return any;
    }

    private static bool IsPlayer(Collider c) {
        int id = c.GetInstanceID();
        if (!playerColliders.TryGetValue(id, out bool player)) {
            player = c.GetComponentInParent<PlayerHealth>() != null;
            // Bodies come and go with every respawn; keep the cache from growing without end.
            if (playerColliders.Count > 4096) playerColliders.Clear();
            playerColliders[id] = player;
        }
        return player;
    }

}
