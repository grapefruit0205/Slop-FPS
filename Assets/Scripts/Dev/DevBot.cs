using System;
using System.Collections.Generic;
using UnityEngine;
using SlopFPS.Input;

/// <summary>
/// Automated network tests for standalone builds.
///   -autofire       play fair: walk towards the nearest player until it is in sight, then hold the trigger on it.
///   -loadout GUN    deploy with this primary (ak47, smg, sniper, shotgun).
///   -walkout SECS   first walk this long along the most open direction (away from spawn walls), e.g. for screenshots.
///   -aim            hold the sights up (others see the rifle shouldered).
///   -wander         walk a small square (forward, right, back, left) in whatever stance it holds, animating as it goes.
///   -stance MODE    crouch, prone, or cycle (stand, crouch, prone and leans in turn, every 3 s). On its own the bot
///                   just holds the pose; with -autofire it fights in it.
///   -escape         try to leave the map: sprint and jump straight at one edge of the play area after another (nearest
///                   first, 10 s each) and log whether the invisible walls held.
///   -cheat MODE     behave like a modified client so the server's checks can be seen rejecting it:
///                   speed, teleport, fly, rapidfire, fastclock, ammo, wallshot, silentaim, ghost,
///                   outside (teleport past the play area's edge), underfloor (teleport under the map)
///   -teamkill       in a team mode, go for teammates instead of enemies (to see the server refuse friendly fire).
///   -grenades       every 7-11 s, lob a grenade (frag, flash, smoke in turn, while any are left) at the nearest enemy
///                   4-32 m away, through the normal GrenadeHands path; with -autofire between bursts, alone it just
///                   stands, faces the nearest enemy and throws. -grenadewait SECS: not before this long after
///                   deploying (default 3).
///   -cheat grenade-far       report frag throws from 10 m away from the body (refused)
///   -cheat grenade-infinite  report a frag throw every 2 s whatever is left (the first goes, the rest are refused)
/// Cheats go through the same calls a hacked client would make (moving the body, PlayerHealth.ReportShot,
/// Loadout.ReportThrow).
/// In elimination the bot keeps still through the freeze (only turning to face the nearest enemy) and, while out of
/// the round, moves the spectator camera on to the next teammate every few seconds.
/// </summary>
public class DevBot : MonoBehaviour {

    private const float WalkSpeed = 4f;
    private const float ShotInterval = 0.2f;

    public static string Cheat {
        get {
#if UNITY_WEBGL
            return null;
#else
            string[] args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "-cheat");
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
#endif
        }
    }

    public static bool Enabled {
        get {
#if UNITY_WEBGL
            return false;
#else
            string[] args = Environment.GetCommandLineArgs();
            return Array.IndexOf(args, "-autofire") >= 0 || Array.IndexOf(args, "-stance") >= 0 || Array.IndexOf(args, "-wander") >= 0 ||
                   Array.IndexOf(args, "-escape") >= 0 || Array.IndexOf(args, "-grenades") >= 0 || Cheat != null;
#endif
        }
    }

    private string mode;
    private string stanceMode;
    private bool fight;
    private bool wander;
    private bool escape;
    private float walkout;
    private float spawnedAt;
    private float nextAction;
    private float sightSince = -1f;
    private Vector3 lane;
    private float laneSign = 1f;
    private PlayerNetworkMover lastBody;
    private int forged;
    private double fakeClock;
    private float watchNextAt;
    private bool grenades;
    private float nextGrenadeAt;
    private int grenadeTurn;
    private float lobPitch;
    private float grenadeWait = 3f;
    private PlayerHealth lobTarget;
    private ushort forgedSeq;

    void Start() {
        mode = Cheat;
        string[] args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "-loadout");
        if (i >= 0 && i + 1 < args.Length && Enum.TryParse(args[i + 1], true, out WeaponId gun)) LoadoutChoice.Set(gun, OpticId.Iron, false);
        int k = Array.IndexOf(args, "-stance");
        if (k >= 0 && k + 1 < args.Length) stanceMode = args[k + 1].ToLowerInvariant();
        fight = Array.IndexOf(args, "-autofire") >= 0;
        wander = Array.IndexOf(args, "-wander") >= 0;
        escape = Array.IndexOf(args, "-escape") >= 0;
        grenades = Array.IndexOf(args, "-grenades") >= 0;
        int gw = Array.IndexOf(args, "-grenadewait");
        if (gw >= 0 && gw + 1 < args.Length) float.TryParse(args[gw + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out grenadeWait);
        FpsGun.DevAim = Array.IndexOf(args, "-aim") >= 0;
        int w = Array.IndexOf(args, "-walkout");
        if (w >= 0 && w + 1 < args.Length) float.TryParse(args[w + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out walkout);
        Debug.Log("[DevBot] Mode: " + (mode ?? "fair") + ", loadout " + LoadoutChoice.Primary + " " + LoadoutChoice.Optic);
    }

    void LateUpdate() {
        FpsGun.DevTrigger = false;
        PlayerNetworkMover me = PlayerNetworkMover.Local;
        if (me == null || me.GetComponent<PlayerHealth>().IsDead) {
            WatchTeam();
            return;
        }
        watchNextAt = 0f;
        if (me != lastBody) {
            lastBody = me;
            sightSince = -1f;
            nextAction = Time.time + 1.5f;
            lane = OpenDirection(me.transform.position);
            spawnedAt = Time.time;
            escapeTargets = null;
            nextGrenadeAt = Time.time + grenadeWait + UnityEngine.Random.Range(0f, 3f);
            lobTarget = null;
        }
        // Mouse look would fight the scripted aim, so the bot steers the body itself.
        FirstPersonController fpc = me.GetComponent<FirstPersonController>();
        if (fpc.enabled) fpc.enabled = false;
        CharacterController cc = me.GetComponent<CharacterController>();
        PlayerHealth target = Nearest(me);
        float dt = Time.deltaTime;

        PlayerNetworkMover.DevInput = Vector2.zero;
        if (FpsInputGate.Frozen) {
            // Round about to start: settle onto the floor and look at the nearest enemy, nothing else.
            if (target != null) {
                Vector3 to = target.transform.position - me.transform.position;
                to.y = 0f;
                if (to.sqrMagnitude > 0.01f) me.transform.rotation = Quaternion.LookRotation(to);
            }
            cc.Move(Vector3.down * 9.8f * dt);
            spawnedAt = Time.time;
            return;
        }
        if (Time.time - spawnedAt < walkout) {
            PlayerStance.DevStance = Stance.Stand;
            me.transform.rotation = Quaternion.LookRotation(lane);
            cc.Move(lane * WalkSpeed * dt + Vector3.down * 9.8f * dt);
            PlayerNetworkMover.DevInput = new Vector2(0f, 1f);
            return;
        }
        HoldStance();
        switch (mode) {
            case null:
                if (escape) Escape(me, cc, dt);
                else if (fight) Fair(me, cc, target, dt);
                else if (grenades) Lob(me, cc, target, dt);
                else if (wander) Wander(me, cc, dt);
                break;
            case "grenade-far":       // a throw from 10 m off the body (over the head and to the side)
            case "grenade-infinite":  // a frag every 2 s, carried or not
                if (Time.time > nextAction) {
                    nextAction = Time.time + (mode == "grenade-far" ? 3f : 2f);
                    ForgeThrow(me, target);
                }
                break;
            case "speed":   // 2.5x the run speed, back and forth along an open lane
                if (Time.time > nextAction) {
                    laneSign = -laneSign;
                    nextAction = Time.time + 0.6f;
                }
                cc.Move(lane * laneSign * 20f * dt + Vector3.down * 2f * dt);
                break;
            case "teleport":
                if (Time.time > nextAction) {
                    nextAction = Time.time + 2f;
                    laneSign = -laneSign;
                    cc.enabled = false;
                    me.transform.position += lane * laneSign * 10f;
                    cc.enabled = true;
                }
                break;
            case "fly":
                cc.Move(Vector3.up * 3f * dt);
                break;
            case "outside":     // pop up a few metres past the nearest edge of the play area
            case "underfloor":  // or straight down under the map
                if (Time.time > nextAction && MapLoader.Current != null && MapLoader.Current.HasBoundary) {
                    nextAction = Time.time + 3f;
                    Vector3 from = me.transform.position;
                    Vector3 edge = EdgePoint(from);
                    Vector3 to = mode == "underfloor" ? from + Vector3.down * 20f : edge + Flat3(edge - from).normalized * 6f;
                    cc.enabled = false;
                    me.transform.position = to;
                    cc.enabled = true;
                    Debug.Log("[DevBot] Cheat " + mode + ": jumped from " + from.ToString("F1") + " to " + to.ToString("F1"));
                }
                break;
            case "rapidfire":   // every frame, ~12x the real rate
                Forge(me, target, false);
                break;
            case "fastclock":   // 3x the real rate, with timestamps forged to look correctly spaced
                if (Time.time > nextAction) {
                    nextAction = Time.time + ShotInterval / 3f;
                    Forge(me, target, false);
                }
                break;
            case "ammo":        // legal rate, but never reloads
                if (Time.time > nextAction) {
                    nextAction = Time.time + ShotInterval;
                    Forge(me, target, false);
                }
                break;
            case "wallshot":    // "hit" the target wherever it is, walls or not
            case "silentaim":   // "hit" the target while looking the other way
            case "ghost":       // report a hit on a spot the target is nowhere near
                if (target == null) break;
                Face(me, target, mode == "silentaim");
                if (Time.time > nextAction) {
                    nextAction = Time.time + ShotInterval;
                    Forge(me, target, true);
                }
                break;
        }
    }

    private static readonly (Stance pose, float lean)[] Cycle = {
        (Stance.Stand, 0f), (Stance.Crouch, 0f), (Stance.Prone, 0f), (Stance.Stand, -1f), (Stance.Crouch, 1f), (Stance.Stand, 1f),
    };

    /// <summary>Out of an elimination round: cycle through the teammates being watched, as a player clicking would.</summary>
    private void WatchTeam() {
        Spectator spec = Spectator.Instance;
        if (spec == null || !spec.Active || Time.time < watchNextAt) return;
        if (watchNextAt > 0f) spec.Next();
        watchNextAt = Time.time + 4f;
    }

    private void HoldStance() {
        switch (stanceMode) {
            case "crouch": PlayerStance.DevStance = Stance.Crouch; break;
            case "prone": PlayerStance.DevStance = Stance.Prone; break;
            case "cycle":
                var step = Cycle[(int)(Time.time / 3f) % Cycle.Length];
                PlayerStance.DevStance = step.pose;
                PlayerStance.DevLean = step.lean;
                break;
        }
    }

    private void Fair(PlayerNetworkMover me, CharacterController cc, PlayerHealth target, float dt) {
        if (target == null) return;
        if (grenades && Grenade(me, target)) {
            FpsGun.DevTrigger = false;
            cc.Move(Vector3.down * 9.8f * dt);
            return;
        }
        Face(me, target, false);
        if (InSight(me, target)) {
            if (sightSince < 0f) sightSince = Time.time;
            // Give the server a moment to see the new avatar before the first shot.
            FpsGun.DevTrigger = Time.time - sightSince > 0.75f;
            return;
        }
        sightSince = -1f;
        // Lost sight (round a corner, into smoke): stop shooting at what can't be seen.
        FpsGun.DevTrigger = false;
        if (Smoked(me, target)) {
            // Hold the angle until the smoke clears rather than walk blind into the enemy.
            cc.Move(Vector3.down * 9.8f * dt);
            return;
        }
        Vector3 toward = target.transform.position - me.transform.position;
        toward.y = 0f;
        cc.Move(toward.normalized * WalkSpeed * dt + Vector3.down * 9.8f * dt);
        PlayerNetworkMover.DevInput = new Vector2(0f, 1f);
    }

    // ------------------------------------------------------------------ -grenades

    /// <summary>-grenades without -autofire: stand, face the nearest enemy, throw now and then.</summary>
    private void Lob(PlayerNetworkMover me, CharacterController cc, PlayerHealth target, float dt) {
        cc.Move(Vector3.down * 9.8f * dt);
        if (target == null) return;
        if (!Grenade(me, target)) Face(me, target, false);
    }

    /// <summary>
    /// Throw at <paramref name="target"/> when it is time and it is at a sensible distance; while a grenade is out, aim
    /// the lob. True while busy with a grenade (the gun is away).
    /// </summary>
    private bool Grenade(PlayerNetworkMover me, PlayerHealth target) {
        GrenadeHands hands = GrenadeHands.Local;
        if (hands == null) return false;
        if (hands.InHand) {
            if (lobTarget != null && !lobTarget.IsDead) AimLob(me, lobTarget);
            return true;
        }
        if (Time.time < nextGrenadeAt) return false;
        Vector3 flat = Flat3(target.transform.position - me.transform.position);
        float metres = flat.magnitude / Grenades.M(1f);
        if (metres < 4f || metres > 32f) return false;
        GrenadeId pick = GrenadeId.None;
        for (int k = 0; k < Grenades.All.Length && pick == GrenadeId.None; k++) {
            GrenadeId id = Grenades.All[(grenadeTurn + k) % Grenades.All.Length];
            if (hands.Left(id) > 0) pick = id;
        }
        if (pick == GrenadeId.None) {
            nextGrenadeAt = Time.time + 5f;
            return false;
        }
        grenadeTurn++;
        bool overhand = metres > 9f;
        float cook = pick == GrenadeId.Frag ? UnityEngine.Random.Range(0.3f, 1.3f) : 0.25f;
        lobPitch = LobPitch(metres, overhand);
        if (!BestLob(me, target, flat, overhand, Grenades.M(pick == GrenadeId.Frag ? 4f : 8f))) {
            // Walls, roofs or a low ceiling in the way: it would not come down near them (or would bounce back at our
            // own feet). Try again later.
            nextGrenadeAt = Time.time + 2f;
            return false;
        }
        lobTarget = target;
        if (!hands.DevThrow(pick, overhand, cook)) return false;
        nextGrenadeAt = Time.time + UnityEngine.Random.Range(7f, 11f);
        Debug.Log("[DevBot] Grenade: " + Grenades.Get(pick).Name + (overhand ? " overhand" : " underhand") + " at client " + target.OwnerId + " " +
                  metres.ToString("F1") + " m away, aim " + lobPitch.ToString("F0") + "° up");
        AimLob(me, target);
        return true;
    }

    private static readonly Vector3[] arc = new Vector3[256];

    /// <summary>
    /// Pick the lob pitch whose predicted flight (walls, ceilings and bounces included) ends nearest the target, as a
    /// player would after a few rounds on the map. False when no pitch brings it within <paramref name="near"/> of the
    /// target without also coming down close to our own feet.
    /// </summary>
    private bool BestLob(PlayerNetworkMover me, PlayerHealth target, Vector3 flat, bool overhand, float near) {
        Camera cam = me.GetComponentInChildren<Camera>();
        if (cam == null) return true;
        Vector3 eye = cam.transform.position;
        Vector3 goal = target.transform.position;
        float best = float.MaxValue, bestPitch = lobPitch;
        for (float pitch = -10f; pitch <= 40f; pitch += 2.5f) {
            Vector3 aim = Quaternion.LookRotation(flat) * Quaternion.Euler(-pitch, 0f, 0f) * Vector3.forward;
            int n = GrenadeFlight.Predict(GrenadeFlight.StartPoint(eye, aim), GrenadeFlight.Launch(aim, overhand), 3f, arc);
            Vector3 end = arc[n - 1];
            if (Flat3(end - eye).magnitude < Grenades.M(3.5f)) continue;
            float miss = (end - goal).magnitude;
            if (miss < best) {
                best = miss;
                bestPitch = pitch;
            }
        }
        if (best > near) return false;
        lobPitch = bestPitch;
        return true;
    }

    private void AimLob(PlayerNetworkMover me, PlayerHealth target) {
        Vector3 flat = Flat3(target.transform.position - me.transform.position);
        if (flat.sqrMagnitude < 0.01f) return;
        me.transform.rotation = Quaternion.LookRotation(flat);
        Camera cam = me.GetComponentInChildren<Camera>();
        if (cam != null) cam.transform.rotation = Quaternion.LookRotation(flat) * Quaternion.Euler(-lobPitch, 0f, 0f);
    }

    /// <summary>The crosshair pitch (degrees up) that lands a throw about <paramref name="metres"/> away on level ground.</summary>
    public static float LobPitch(float metres, bool overhand) {
        float v = overhand ? Grenades.OverhandSpeed : Grenades.UnderhandSpeed;
        float lift = overhand ? Grenades.OverhandLift : Grenades.UnderhandLift;
        float g = Grenades.Gravity, h = 1.5f;
        float best = 0f, bestErr = float.MaxValue;
        for (float pitch = -15f; pitch <= 40f; pitch += 0.5f) {
            float a = (pitch + lift) * Mathf.Deg2Rad;
            float vy = v * Mathf.Sin(a), vx = v * Mathf.Cos(a);
            float t = (vy + Mathf.Sqrt(vy * vy + 2f * g * h)) / g;
            // It rolls on a bit after landing; aim a little short.
            float err = Mathf.Abs(vx * t * 0.9f - metres);
            if (err < bestErr) {
                bestErr = err;
                best = pitch;
            }
        }
        return best;
    }

    /// <summary>-cheat grenade-*: report a throw the way a modified client would, bypassing GrenadeHands.</summary>
    private void ForgeThrow(PlayerNetworkMover me, PlayerHealth target) {
        Loadout loadout = me.GetComponent<Loadout>();
        Camera cam = me.GetComponentInChildren<Camera>();
        if (loadout == null || cam == null) return;
        if (target != null) Face(me, target, false);
        Vector3 origin = cam.transform.position;
        if (mode == "grenade-far") origin += Vector3.up * Grenades.M(6f) + me.transform.right * Grenades.M(8f);
        Vector3 aim = me.transform.forward;
        if (++forgedSeq == 0) forgedSeq = 1;
        loadout.ReportThrow(GrenadeId.Frag, AntiCheat.ClientClock, origin, aim, true, forgedSeq);
        forged++;
        Debug.Log("[DevBot] Forged grenade throw #" + forged + " (" + mode + ")");
    }

    // ------------------------------------------------------------------ -escape

    private const float RunSpeed = 8f;           // FirstPersonController's sprint
    private const float JumpSpeed = 8f;
    private const float Gravity = 19.62f;        // gravity x2, as the controller has it
    private const float AttemptTime = 10f;

    private List<Vector3> escapeTargets;
    private int escapeIndex;
    private float attemptStart, logAt, vy, jumpedAt;
    private float farthestOut;

    /// <summary>
    /// Sprint and bunny-hop at a point on the play area's outline (aiming 15 m past it), changing target every 10 s: the
    /// corners and edge midpoints, nearest first. Logs the outcome of every attempt.
    /// </summary>
    private void Escape(PlayerNetworkMover me, CharacterController cc, float dt) {
        MapDefinition map = MapLoader.Current;
        if (map == null || !map.HasBoundary) return;
        Vector3 pos = me.transform.position;
        if (escapeTargets == null) {
            escapeTargets = new List<Vector3>();
            Vector2[] o = map.boundary;
            for (int i = 0; i < o.Length; i++) {
                Vector2 a = o[i], b = o[(i + 1) % o.Length];
                escapeTargets.Add(map.transform.TransformPoint(new Vector3(a.x, 0f, a.y)));
                escapeTargets.Add(map.transform.TransformPoint(new Vector3((a.x + b.x) * 0.5f, 0f, (a.y + b.y) * 0.5f)));
            }
            escapeTargets.Sort((x, y) => Flat3(x - pos).sqrMagnitude.CompareTo(Flat3(y - pos).sqrMagnitude));
            escapeIndex = 0;
            attemptStart = Time.time;
            Debug.Log("[DevBot] Escape: " + escapeTargets.Count + " targets on the " + map.id + " outline, starting at " + pos.ToString("F1"));
        }
        float fromEdge = Vector3.Distance(Flat3(pos), Flat3(EdgePoint(pos)));
        bool inside = map.InPlayArea(pos);
        if (Time.time - attemptStart > AttemptTime) {
            Debug.Log("[DevBot] Escape attempt " + (escapeIndex + 1) + " toward " + escapeTargets[escapeIndex].ToString("F0") +
                      (inside ? ": held inside at " : ": ended OUTSIDE at ") + pos.ToString("F1") + ", " + fromEdge.ToString("F1") + " from the outline");
            escapeIndex = (escapeIndex + 1) % escapeTargets.Count;
            attemptStart = Time.time;
        }
        Vector3 center = map.transform.position;
        Vector3 goal = escapeTargets[escapeIndex];
        Vector3 beyond = goal + Flat3(goal - center).normalized * 15f;
        Vector3 dir = Flat3(beyond - pos).normalized;
        if (dir.sqrMagnitude < 0.01f) dir = Vector3.forward;
        me.transform.rotation = Quaternion.LookRotation(dir);
        // Hop now and then on the way, and keep hopping once at the wall (onto anything climbable next to it).
        // A player mashing Space only jumps off the ground, at most about once a landing.
        bool hop = Time.time - jumpedAt > (fromEdge < 4f ? 0.9f : 1.5f);
        if (cc.isGrounded && hop) {
            vy = JumpSpeed;
            jumpedAt = Time.time;
        } else if (cc.isGrounded) {
            vy = -2f;
        } else {
            vy -= Gravity * dt;
        }
        cc.Move(dir * RunSpeed * dt + Vector3.up * vy * dt);
        PlayerNetworkMover.DevInput = new Vector2(0f, 1f);

        if (!inside && fromEdge > farthestOut + 0.5f) {
            farthestOut = fromEdge;
            Debug.LogWarning("[DevBot] ESCAPED the play area: at " + pos.ToString("F1") + ", " + fromEdge.ToString("F1") + " past the outline");
        }
        if (Time.time > logAt) {
            logAt = Time.time + 2f;
            Debug.Log("[DevBot] Escape at " + pos.ToString("F1") + (inside ? " inside, " : " OUTSIDE, ") + fromEdge.ToString("F1") + " from the outline");
        }
    }

    private static Vector3 Flat3(Vector3 v) {
        v.y = 0f;
        return v;
    }

    /// <summary>The closest point on the play area's outline to <paramref name="world"/> (at its height).</summary>
    private static Vector3 EdgePoint(Vector3 world) {
        MapDefinition map = MapLoader.Current;
        Vector3 local = map.transform.InverseTransformPoint(world);
        Vector2 p = new Vector2(local.x, local.z), best = p;
        float bestD = float.MaxValue;
        Vector2[] o = map.boundary;
        for (int i = 0; i < o.Length; i++) {
            Vector2 a = o[i], ab = o[(i + 1) % o.Length] - a;
            Vector2 c = a + ab * Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(1e-6f, ab.sqrMagnitude));
            float d = (c - p).sqrMagnitude;
            if (d < bestD) {
                bestD = d;
                best = c;
            }
        }
        Vector3 w = map.transform.TransformPoint(new Vector3(best.x, local.y, best.y));
        w.y = world.y;
        return w;
    }

    private static readonly Vector2[] Square = { new Vector2(0f, 1f), new Vector2(1f, 0f), new Vector2(0f, -1f), new Vector2(-1f, 0f) };

    private void Wander(PlayerNetworkMover me, CharacterController cc, float dt) {
        Vector2 dir = Square[(int)(Time.time / 1.5f) % Square.Length];
        PlayerStance s = me.GetComponent<PlayerStance>();
        float scale = s == null ? 1f : s.Current == Stance.Crouch ? 0.55f : s.Current == Stance.Prone ? 0.25f : 1f;
        Vector3 move = me.transform.TransformDirection(new Vector3(dir.x, 0f, dir.y));
        cc.Move(move * WalkSpeed * scale * dt + Vector3.down * 9.8f * dt);
        PlayerNetworkMover.DevInput = dir;
    }

    private static Vector3 Chest(PlayerHealth p) {
        PlayerStance s = p.GetComponent<PlayerStance>();
        if (s == null) return p.transform.position + Vector3.up * 1.2f;
        float scale = p.transform.lossyScale.y;
        if (s.Current == Stance.Prone) return p.transform.TransformPoint(new Vector3(0f, 0.2f, 0.1f));
        float height = s.Current == Stance.Crouch ? 0.75f : 1.0f;   // the avatar's own units
        Vector3 up = p.transform.rotation * (Quaternion.Euler(0f, 0f, -s.Lean * 13f) * Vector3.up);
        return p.transform.position + up * height * scale;
    }

    private static void Face(PlayerNetworkMover me, PlayerHealth target, bool away) {
        Vector3 flat = Chest(target) - me.transform.position;
        flat.y = 0f;
        if (flat.sqrMagnitude < 0.01f) return;
        me.transform.rotation = Quaternion.LookRotation(away ? -flat : flat);
        Camera cam = me.GetComponentInChildren<Camera>();
        if (cam != null && !away) cam.transform.rotation = Quaternion.LookRotation(Chest(target) - cam.transform.position);
    }

    private static bool Smoked(PlayerNetworkMover me, PlayerHealth target) {
        Camera cam = me.GetComponentInChildren<Camera>();
        return cam != null && GrenadeWorld.BlocksSight(cam.transform.position, Chest(target));
    }

    private static bool InSight(PlayerNetworkMover me, PlayerHealth target) {
        Camera cam = me.GetComponentInChildren<Camera>();
        if (cam == null) return false;
        Vector3 from = cam.transform.position;
        Vector3 to = Chest(target);
        // Nobody can see through a smoke cloud.
        if (GrenadeWorld.BlocksSight(from, to)) return false;
        foreach (RaycastHit h in Physics.RaycastAll(from, (to - from).normalized, Vector3.Distance(from, to),
                                                   LayerMask.GetMask("Shootable"), QueryTriggerInteraction.Ignore)) {
            if (h.collider.GetComponentInParent<PlayerHealth>() == null) return false;
        }
        return true;
    }

    /// <summary>Send a shot report the way a modified client would, bypassing FpsGun entirely.</summary>
    private void Forge(PlayerNetworkMover me, PlayerHealth target, bool claimHit) {
        PlayerHealth self = me.GetComponent<PlayerHealth>();
        Camera cam = me.GetComponentInChildren<Camera>();
        Vector3 origin = cam != null ? cam.transform.position : me.transform.position + Vector3.up * 1.59f;
        double now = AntiCheat.ClientClock;
        if (mode == "fastclock") {
            fakeClock = System.Math.Max(fakeClock + ShotInterval, now);
            now = fakeClock;
        }
        Loadout loadout = me.GetComponent<Loadout>();
        WeaponId gun = loadout != null ? loadout.InHand : WeaponId.AK47;
        if (!claimHit || target == null) {
            Vector3 fwd = me.transform.forward;
            self.ReportShot(now, gun, origin, fwd, new[] { new Pellet { Direction = fwd } });
        } else {
            Vector3 aimAt = Chest(target);
            if (mode == "ghost") aimAt += target.transform.right * 6f;
            Vector3 dir = (aimAt - origin).normalized;
            self.ReportShot(now, gun, origin, dir, new[] {
                new Pellet { Direction = dir, Hit = true, Point = aimAt, Normal = -dir, Surface = "Flesh", Target = target, Headshot = true },
            });
        }
        forged++;
        if (forged % 50 == 1) Debug.Log("[DevBot] Forged shot #" + forged + " (" + mode + ")");
    }

    /// <summary>A horizontal direction with at least 12 m of open floor, for the movement cheats.</summary>
    private static Vector3 OpenDirection(Vector3 from) {
        Vector3 chest = from + Vector3.up * 1.0f;
        for (int i = 0; i < 16; i++) {
            Vector3 dir = Quaternion.Euler(0f, i * 22.5f, 0f) * Vector3.forward;
            if (!Physics.Raycast(chest, dir, 12f, ~LayerMask.GetMask("FPSPlayer", "FPSGun", "TPSPlayer", "TPSGun", "Hidden"),
                                 QueryTriggerInteraction.Ignore)) {
                return dir;
            }
        }
        return Vector3.forward;
    }

    private static readonly bool TeamKill = Array.IndexOf(Environment.GetCommandLineArgs(), "-teamkill") >= 0;

    private static PlayerHealth Nearest(PlayerNetworkMover me) {
        PlayerHealth best = null;
        float bestDist = float.MaxValue;
        foreach (PlayerHealth h in FindObjectsOfType<PlayerHealth>()) {
            if (h.gameObject == me.gameObject || h.IsDead || Teams.IsHostileToLocal(h) == TeamKill) continue;
            float d = (h.transform.position - me.transform.position).sqrMagnitude;
            if (d < bestDist) {
                bestDist = d;
                best = h;
            }
        }
        return best;
    }

}
