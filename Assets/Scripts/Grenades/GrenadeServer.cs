using System.Collections;
using System.Collections.Generic;
using FishNet;
using FishNet.Connection;
using FishNet.Managing;
using UnityEngine;

/// <summary>
/// Server side of every grenade in the session: flies the real one (<see cref="GrenadeFlight"/>, the same steps the
/// clients fly their copies with), times the fuse and sets it off where the server's copy is. Frags hurt everyone in
/// reach with a clear line from the blast to their body (cover shields, teammates are spared, the thrower is not) and
/// credit the kill to the thrower's connection, so a grenade still counts after its thrower died. Flashes and smokes
/// are effects for the clients; the server keeps the clouds to hand to late joiners. Elimination rounds start clean.
/// Throws arrive through <see cref="Loadout"/>, which checks them first. Nothing here renders.
/// </summary>
public class GrenadeServer : MonoBehaviour {

    private class Live {
        public ushort Id;
        public GrenadeSpec Spec;
        public int Thrower;
        public GrenadeBody Body;
        public float FuseLeft;
    }

    private class Cloud {
        public ushort Id;
        public int Thrower;
        public Vector3 Floor;
        public float Born;
    }

    private static GrenadeServer instance;

    private readonly List<Live> live = new List<Live>();
    private readonly List<Cloud> clouds = new List<Cloud>();
    private ushort nextId;
    private float pending;
    private MatchManager hooked;
    private NetworkManager net;

    /// <summary>Server: the one instance (made on first use).</summary>
    public static GrenadeServer Instance {
        get {
            if (instance == null) {
                GameObject go = new GameObject("GrenadeServer");
                instance = go.AddComponent<GrenadeServer>();
            }
            return instance;
        }
    }

    void Awake() {
        net = InstanceFinder.NetworkManager;
        if (net != null) net.SceneManager.OnClientLoadedStartScenes += OnClientLoaded;
        if (System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-grenadetest") >= 0) StartCoroutine(WallTest());
    }

    void OnDestroy() {
        if (net != null) net.SceneManager.OnClientLoadedStartScenes -= OnClientLoaded;
        Hook(null);
        if (instance == this) instance = null;
    }

    // ------------------------------------------------------------------ throws

    /// <summary>Put a grenade into the air for everyone. Returns its id.</summary>
    public ushort Launch(GrenadeSpec spec, int thrower, Vector3 start, Vector3 velocity, float fuse, ushort seq) {
        Live g = new Live {
            Id = ++nextId, Spec = spec, Thrower = thrower, FuseLeft = fuse,
            Body = new GrenadeBody { Position = start, Velocity = velocity },
        };
        if (g.Id == 0) g.Id = ++nextId;
        live.Add(g);
        Send(new GrenadeThrown { Id = g.Id, Type = (byte)spec.Id, Thrower = thrower, Seq = seq, Position = start, Velocity = velocity });
        return g.Id;
    }

    /// <summary>A cooked frag nobody threw: it goes off right where it is, in the hand.</summary>
    public void GoOffInHand(GrenadeSpec spec, int thrower, Vector3 at) {
        Live g = new Live { Id = ++nextId, Spec = spec, Thrower = thrower, Body = new GrenadeBody { Position = at, Resting = true } };
        Detonate(g);
    }

    // ------------------------------------------------------------------ flight

    void Update() {
        Hook(MatchManager.Instance);
        float now = AntiCheat.Now;
        clouds.RemoveAll(c => now - c.Born > Grenades.SmokeDuration);
        if (live.Count == 0) {
            pending = 0f;
            return;
        }
        // Fixed steps, as the clients take them, so every copy follows the same path.
        pending = Mathf.Min(pending + Time.deltaTime, 0.25f);
        while (pending >= Grenades.SimStep) {
            pending -= Grenades.SimStep;
            for (int i = live.Count - 1; i >= 0; i--) {
                Live g = live[i];
                GrenadeFlight.Step(ref g.Body, Grenades.SimStep);
                g.FuseLeft -= Grenades.SimStep;
                bool due = g.Spec.FuseAfterRest
                    ? (g.Body.Resting && g.Body.RestTime >= g.Spec.Fuse) || g.Body.Age >= g.Spec.MaxFlight
                    : g.FuseLeft <= 0f;
                MapDefinition map = MapLoader.Current;
                if (!due && map != null && g.Body.Position.y < map.FloorY - Grenades.M(40f)) {
                    // Fell out of the world: gone without a bang.
                    live.RemoveAt(i);
                    continue;
                }
                if (!due) continue;
                live.RemoveAt(i);
                Detonate(g);
            }
        }
    }

    private void Detonate(Live g) {
        Vector3 at = g.Body.Position;
        Send(new GrenadeBlast { Id = g.Id, Type = (byte)g.Spec.Id, Thrower = g.Thrower, Position = at });
        string where = " (client " + g.Thrower + ") at " + at.ToString("F1") + " after " + g.Body.Age.ToString("F1") + " s";
        switch (g.Spec.Id) {
            case GrenadeId.Frag:
                Debug.Log("[Grenade] " + g.Spec.Name + " #" + g.Id + where + ": " + Frag(g, at));
                break;
            case GrenadeId.Flash:
                Debug.Log("[Grenade] " + g.Spec.Name + " #" + g.Id + where + ": " + Flash(at));
                break;
            case GrenadeId.Smoke:
                clouds.Add(new Cloud { Id = g.Id, Thrower = g.Thrower, Floor = at, Born = AntiCheat.Now });
                Debug.Log("[Grenade] " + g.Spec.Name + " #" + g.Id + where + ": cloud for " + Grenades.SmokeDuration + " s");
                break;
        }
    }

    // ------------------------------------------------------------------ frag

    /// <summary>Damage everyone in reach with a line of sight to the blast. Returns what happened, for the log.</summary>
    private string Frag(Live g, Vector3 at) {
        MatchManager mm = MatchManager.Instance;
        bool counts = mm == null || mm.AllowsDamage;
        Vector3 blast = at + Vector3.up * Grenades.M(0.15f);
        List<string> report = new List<string>();
        foreach (PlayerHealth p in FindObjectsOfType<PlayerHealth>()) {
            if (p.IsDead || !p.IsSpawned) continue;
            Bounds body = BodyBounds(p);
            float metres = Mathf.Sqrt(body.SqrDistance(blast)) / Grenades.M(1f);
            if (metres >= g.Spec.Radius) continue;
            string who = "client " + p.OwnerId + " " + metres.ToString("F1") + " m";
            int amount = FragDamage(blast, body, out int seen);
            if (seen == 0) {
                report.Add(who + " behind cover, unhurt");
                continue;
            }
            if (amount <= 0) continue;
            if (!counts) {
                report.Add(who + " " + amount + " (no damage in this phase)");
                continue;
            }
            report.Add(who + (seen < ExposurePoints ? " partly covered" : "") + " " + p.ApplyBlast(amount, g.Thrower, blast, g.Spec.Name));
        }
        return report.Count > 0 ? string.Join("; ", report) : "nobody in reach";
    }

    private const int ExposurePoints = 3;

    /// <summary>A frag's damage to <paramref name="body"/>: by distance, less when only part of it is in view.</summary>
    public static int FragDamage(Vector3 blast, Bounds body, out int seen) {
        float metres = Mathf.Sqrt(body.SqrDistance(blast)) / Grenades.M(1f);
        seen = Exposure(blast, body);
        if (seen == 0) return 0;
        float cover = Mathf.Lerp(Grenades.CoveredScale, 1f, (seen - 1) / (float)(ExposurePoints - 1));
        return Mathf.RoundToInt(Grenades.FragDamageAt(metres) * cover);
    }

    /// <summary>
    /// -grenadetest: once someone is on the map, find walls around them and log what a frag 1.5 m in front of each
    /// does to a body 1.5 m behind it, against the same distance in the open. Evidence that cover works.
    /// </summary>
    private IEnumerator WallTest() {
        PlayerHealth p = null;
        while (p == null) {
            yield return new WaitForSeconds(1f);
            foreach (PlayerHealth h in FindObjectsOfType<PlayerHealth>()) {
                if (h.IsSpawned && !h.IsDead) p = h;
            }
        }
        yield return new WaitForSeconds(1f);
        if (p == null) yield break;
        float m = Grenades.M(1f);
        Bounds me = BodyBounds(p);
        Vector3 from = me.center;
        int found = 0;
        for (int i = 0; i < 36 && found < 3; i++) {
            Vector3 dir = Quaternion.Euler(0f, i * 10f, 0f) * Vector3.forward;
            if (!Physics.Raycast(from, dir, out RaycastHit front, 10f * m, GrenadeFlight.SightMask, QueryTriggerInteraction.Ignore)) continue;
            if (front.distance < 2f * m) continue;
            // Only walls that hide a whole standing body (not crates the blast sees over).
            Vector3 low = new Vector3(from.x, me.min.y + 0.3f * m, from.z), high = new Vector3(from.x, me.max.y, from.z);
            if (!Physics.Raycast(low, dir, out RaycastHit l, 10f * m, GrenadeFlight.SightMask, QueryTriggerInteraction.Ignore) ||
                !Physics.Raycast(high, dir, out RaycastHit u, 10f * m, GrenadeFlight.SightMask, QueryTriggerInteraction.Ignore) ||
                Mathf.Abs(l.distance - front.distance) > 0.5f * m || Mathf.Abs(u.distance - front.distance) > 0.5f * m) continue;
            Vector3 beyond = front.point + dir * 2.5f * m;
            if (!Physics.Raycast(beyond, -dir, out RaycastHit back, 2.5f * m, GrenadeFlight.SightMask, QueryTriggerInteraction.Ignore)) continue;
            Vector3 blast = front.point - dir * 1.5f * m;
            blast.y = me.min.y + Grenades.M(0.15f);
            Bounds hidden = new Bounds(back.point + dir * 1.5f * m, me.size);
            hidden.center = new Vector3(hidden.center.x, me.center.y, hidden.center.z);
            if (Physics.CheckSphere(hidden.center, 0.3f * m, GrenadeFlight.WorldMask, QueryTriggerInteraction.Ignore)) continue;
            float metres = Mathf.Sqrt(hidden.SqrDistance(blast)) / m;
            if (metres >= Grenades.Get(GrenadeId.Frag).Radius) continue;
            int through = FragDamage(blast, hidden, out int seenThrough);
            // The same distance in some direction with nothing in between.
            float flat = new Vector2(hidden.center.x - blast.x, hidden.center.z - blast.z).magnitude;
            int inOpen = -1, seenOpen = 0;
            for (int k = 0; k < 36 && inOpen < 0; k++) {
                Vector3 d = Quaternion.Euler(0f, k * 10f, 0f) * Vector3.forward;
                Bounds open = new Bounds(new Vector3(blast.x, me.center.y, blast.z) + d * flat, me.size);
                int dmg = FragDamage(blast, open, out int seen);
                if (seen == ExposurePoints) {
                    inOpen = dmg;
                    seenOpen = seen;
                }
            }
            found++;
            Debug.Log("[GrenadeTest] Wall " + ((front.distance + 2.5f * m - back.distance) / m).ToString("F1") + " m thick at " + front.point.ToString("F1") +
                      ": frag " + metres.ToString("F1") + " m from a body behind it sees " + seenThrough + "/" + ExposurePoints + " -> " + through +
                      " damage; same distance in the open sees " + seenOpen + "/" + ExposurePoints + " -> " + inOpen + " damage");
        }
        if (found == 0) Debug.Log("[GrenadeTest] No wall within 10 m of client " + p.OwnerId + " to test against");
    }

    /// <summary>How many of the body's sample points (feet, middle, head) the blast can see.</summary>
    public static int Exposure(Vector3 blast, Bounds body) {
        Vector3 c = body.center;
        float half = body.extents.y;
        int seen = 0;
        if (GrenadeFlight.Clear(blast, c - Vector3.up * half * 0.6f)) seen++;
        if (GrenadeFlight.Clear(blast, c)) seen++;
        if (GrenadeFlight.Clear(blast, c + Vector3.up * half * 0.75f)) seen++;
        return seen;
    }

    /// <summary>Where the body is: the capsule the stance has sized (lying down it is low and short).</summary>
    public static Bounds BodyBounds(PlayerHealth p) {
        CharacterController cc = p.GetComponent<CharacterController>();
        if (cc != null && cc.enabled) return cc.bounds;
        return new Bounds(p.transform.position + Vector3.up * Grenades.M(0.9f), new Vector3(Grenades.M(0.5f), Grenades.M(1.8f), Grenades.M(0.5f)));
    }

    // ------------------------------------------------------------------ flash (the clients blind themselves)

    /// <summary>
    /// The server's estimate of who a flash catches, for the log: clients work out their own whiteout from their exact
    /// view, and the server only knows which way the body faces, not how far up or down it looks.
    /// </summary>
    private string Flash(Vector3 at) {
        float reach = Grenades.Get(GrenadeId.Flash).Radius;
        List<string> report = new List<string>();
        foreach (PlayerHealth p in FindObjectsOfType<PlayerHealth>()) {
            if (p.IsDead || !p.IsSpawned) continue;
            Bounds body = BodyBounds(p);
            Vector3 eye = new Vector3(body.center.x, body.max.y - Grenades.M(0.12f), body.center.z);
            float metres = Vector3.Distance(eye, at) / Grenades.M(1f);
            if (metres > reach) continue;
            string who = "client " + p.OwnerId + " " + metres.ToString("F1") + " m";
            if (!GrenadeFlight.Clear(at + Vector3.up * Grenades.M(0.1f), eye)) {
                report.Add(who + " behind cover");
                continue;
            }
            if (BehindSmoke(at, eye)) {
                report.Add(who + " behind smoke");
                continue;
            }
            Vector3 to = at - eye;
            to.y = 0f;
            Vector3 f = p.transform.forward;
            f.y = 0f;
            float angle = to.sqrMagnitude > 0.0001f && f.sqrMagnitude > 0.0001f ? Vector3.Angle(f, to) : 0f;
            report.Add(who + " " + angle.ToString("F0") + "° off, ~" + Grenades.FlashStrength(metres, angle).ToString("F2"));
        }
        return report.Count > 0 ? string.Join("; ", report) : "nobody in reach";
    }

    private bool BehindSmoke(Vector3 a, Vector3 b) {
        float now = AntiCheat.Now;
        foreach (Cloud c in clouds) {
            if (GrenadeFlight.SmokeBlocks(c.Floor, now - c.Born, a, b)) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ rounds and joiners

    private void Hook(MatchManager mm) {
        if (mm == hooked) return;
        if (hooked != null) {
            hooked.ServerRoundBegin -= OnRoundBegin;
            hooked.ServerRoundRestart -= OnMatchRestart;
        }
        hooked = mm;
        if (mm != null) {
            mm.ServerRoundBegin += OnRoundBegin;
            mm.ServerRoundRestart += OnMatchRestart;
        }
    }

    private void OnRoundBegin(bool first) {
        Clear(hooked != null ? hooked.Round : 0);
    }

    private void OnMatchRestart() {
        Clear(0);
    }

    /// <summary>Everything in the air and every cloud goes (a new round deploys everyone fresh).</summary>
    private void Clear(int round) {
        if (live.Count > 0 || clouds.Count > 0) {
            Debug.Log("[Grenade] Round " + round + ": cleared " + live.Count + " grenade(s) in flight and " + clouds.Count + " cloud(s)");
        }
        live.Clear();
        clouds.Clear();
        Send(new GrenadeClear { Round = round });
    }

    private void OnClientLoaded(NetworkConnection conn, bool asServer) {
        if (asServer && clouds.Count > 0) StartCoroutine(SendClouds(conn));
    }

    /// <summary>Someone joining sees the clouds already standing (after their world has come up).</summary>
    private IEnumerator SendClouds(NetworkConnection conn) {
        yield return new WaitForSeconds(1.5f);
        if (conn == null || !conn.IsActive) yield break;
        float now = AntiCheat.Now;
        foreach (Cloud c in clouds) {
            conn.Broadcast(new GrenadeBlast { Id = c.Id, Type = (byte)GrenadeId.Smoke, Thrower = c.Thrower, Position = c.Floor, Elapsed = now - c.Born });
        }
    }

    private void Send<T>(T message) where T : struct, FishNet.Broadcast.IBroadcast {
        if (net != null && net.ServerManager.Started) net.ServerManager.Broadcast(message);
    }

}
