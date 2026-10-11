using System;
using System.Collections.Generic;
using FishNet;
using FishNet.Managing;
using FishNet.Transporting;
using UnityEngine;

/// <summary>
/// Client side of every grenade: listens for the server's <see cref="GrenadeThrown"/> / <see cref="GrenadeBlast"/> /
/// <see cref="GrenadeClear"/>, flies a copy of each grenade with the same fixed steps as the server (the thrower's
/// own starts the moment it leaves the hand), and on the blast puts the effect where the server says it went off:
/// the explosion with a camera shake for anyone close, the flashbang's burst and this player's own whiteout (worked
/// out from the exact view here: distance, how far off the view axis, walls and smoke in between), or a smoke cloud.
/// The clouds also answer "is this line of sight smoked out?" for name tags and the test bots.
/// Never created on a dedicated server.
/// </summary>
public class GrenadeWorld : MonoBehaviour {

    private const float LostAfter = 12f;       // seconds a copy may fly without hearing of its blast

    private class Flying {
        public ushort Id;
        public GrenadeId Type;
        public int Thrower;
        public ushort Seq;
        public GrenadeBody Body;
        public Transform Model;
        public Vector3 SpinAxis;
    }

    public static GrenadeWorld Instance { get; private set; }

    /// <summary>A grenade went off (type, where). Dev screenshots time their shots by it.</summary>
    public static event Action<GrenadeId, Vector3> Blasted;

    /// <summary>Dev screenshots: judge the smoke fog over the screen from here instead of the main camera.</summary>
    public static Transform ViewOverride;

    private readonly List<Flying> flying = new List<Flying>();
    private float pending;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Boot() {
        if (NetConfig.IsServer || Instance != null) return;
        NetworkManager net = InstanceFinder.NetworkManager;
        if (net == null) return;
        GameObject go = new GameObject("GrenadeWorld");
        DontDestroyOnLoad(go);
        Instance = go.AddComponent<GrenadeWorld>();
        net.ClientManager.RegisterBroadcast<GrenadeThrown>(Instance.OnThrown);
        net.ClientManager.RegisterBroadcast<GrenadeBlast>(Instance.OnBlast);
        net.ClientManager.RegisterBroadcast<GrenadeClear>(Instance.OnClear);
        net.ClientManager.OnClientConnectionState += args => {
            if (args.ConnectionState == LocalConnectionState.Stopped && Instance != null) Instance.ClearAll();
        };
    }

    private static int LocalClientId {
        get {
            NetworkManager net = InstanceFinder.NetworkManager;
            return net != null && net.ClientManager.Connection != null ? net.ClientManager.Connection.ClientId : -1;
        }
    }

    // ------------------------------------------------------------------ flights

    /// <summary>The local thrower's grenade, in the air at once; the server's copy is matched to it by <paramref name="seq"/>.</summary>
    public void Predict(GrenadeId type, Vector3 start, Vector3 velocity, ushort seq) {
        Add(0, type, LocalClientId, seq, start, velocity);
    }

    private void OnThrown(GrenadeThrown msg, Channel channel) {
        if (msg.Thrower == LocalClientId && msg.Seq != 0) {
            foreach (Flying f in flying) {
                if (f.Id == 0 && f.Seq == msg.Seq) {
                    f.Id = msg.Id;
                    return;
                }
            }
        }
        Add(msg.Id, (GrenadeId)msg.Type, msg.Thrower, msg.Seq, msg.Position, msg.Velocity);
    }

    private void Add(ushort id, GrenadeId type, int thrower, ushort seq, Vector3 start, Vector3 velocity) {
        if (Grenades.Get(type) == null) return;
        GameObject model = GrenadeFx.Model(type, GrenadeFx.FxLayer);
        model.transform.position = start;
        Vector3 axis = Vector3.Cross(Vector3.up, velocity);
        flying.Add(new Flying {
            Id = id, Type = type, Thrower = thrower, Seq = seq, Model = model.transform,
            Body = new GrenadeBody { Position = start, Velocity = velocity },
            SpinAxis = axis.sqrMagnitude > 0.0001f ? axis.normalized : Vector3.right,
        });
    }

    void Update() {
        if (flying.Count == 0) {
            pending = 0f;
            return;
        }
        pending = Mathf.Min(pending + Time.deltaTime, 0.25f);
        while (pending >= Grenades.SimStep) {
            pending -= Grenades.SimStep;
            for (int i = flying.Count - 1; i >= 0; i--) {
                Flying f = flying[i];
                float bounce = GrenadeFlight.Step(ref f.Body, Grenades.SimStep);
                if (bounce > 1.5f) GrenadeFx.PlayAt(GrenadeFx.Sounds.Clink, f.Body.Position, Mathf.Clamp01(bounce / 8f), Grenades.M(30f));
                if (f.Body.Age > LostAfter) {
                    Remove(i);
                }
            }
        }
        foreach (Flying f in flying) {
            if (f.Model == null) continue;
            f.Model.position = f.Body.Position;
            if (!f.Body.Resting) {
                f.Model.rotation = Quaternion.AngleAxis(f.Body.Velocity.magnitude * 40f * Time.deltaTime, f.SpinAxis) * f.Model.rotation;
            } else if (f.Type != GrenadeId.Frag) {
                // Cans come to rest on their side.
                Vector3 e = f.Model.eulerAngles;
                f.Model.rotation = Quaternion.Slerp(f.Model.rotation, Quaternion.Euler(90f, e.y, 0f), 10f * Time.deltaTime);
            }
        }
    }

    private void Remove(int i) {
        if (flying[i].Model != null) Destroy(flying[i].Model.gameObject);
        flying.RemoveAt(i);
    }

    // ------------------------------------------------------------------ blasts

    private void OnBlast(GrenadeBlast msg, Channel channel) {
        for (int i = flying.Count - 1; i >= 0; i--) {
            if (flying[i].Id == msg.Id && msg.Id != 0) Remove(i);
        }
        GrenadeId type = (GrenadeId)msg.Type;
        Vector3 at = msg.Position;
        switch (type) {
            case GrenadeId.Frag:
                GrenadeFx.Explosion(at);
                Shake(at);
                break;
            case GrenadeId.Flash:
                GrenadeFx.FlashBurst(at);
                Blind(at);
                break;
            case GrenadeId.Smoke:
                foreach (SmokeCloud c in SmokeCloud.All) {
                    if (c.Id == msg.Id) return;
                }
                SmokeCloud.Create(msg.Id, at, msg.Elapsed);
                break;
            default:
                return;
        }
        Blasted?.Invoke(type, at);
    }

    private void OnClear(GrenadeClear msg, Channel channel) {
        ClearAll();
    }

    private void ClearAll() {
        for (int i = flying.Count - 1; i >= 0; i--) Remove(i);
        foreach (SmokeCloud c in SmokeCloud.All.ToArray()) Destroy(c.gameObject);
    }

    private static void Shake(Vector3 at) {
        Camera cam = Camera.main;
        if (cam == null) return;
        float metres = Vector3.Distance(cam.transform.position, at) / Grenades.M(1f);
        if (metres >= Grenades.ShakeRadius) return;
        float k = 1f - metres / Grenades.ShakeRadius;
        GrenadeShake.Add(cam, k * k);
    }

    /// <summary>
    /// How badly the flash at <paramref name="at"/> blinds this player: nothing behind a wall or a cloud, otherwise by
    /// distance and by how far off the view axis it went off (<see cref="Grenades.FlashStrength"/>).
    /// </summary>
    private static void Blind(Vector3 at) {
        PlayerNetworkMover me = PlayerNetworkMover.Local;
        if (me == null) return;
        PlayerHealth health = me.GetComponent<PlayerHealth>();
        Camera cam = Camera.main;
        if (cam == null || (health != null && health.IsDead)) return;
        Vector3 eye = cam.transform.position;
        Vector3 to = at - eye;
        float metres = to.magnitude / Grenades.M(1f);
        if (metres > Grenades.Get(GrenadeId.Flash).Radius) return;
        Vector3 pop = at + Vector3.up * Grenades.M(0.1f);
        if (!GrenadeFlight.Clear(pop, eye)) {
            Debug.Log("[Grenade] Flash " + metres.ToString("F1") + " m away, behind cover: not blinded");
            return;
        }
        if (BlocksSight(pop, eye)) {
            Debug.Log("[Grenade] Flash " + metres.ToString("F1") + " m away, behind smoke: not blinded");
            return;
        }
        float angle = Vector3.Angle(cam.transform.forward, to);
        float strength = Grenades.FlashStrength(metres, angle);
        if (strength < 0.02f) return;
        GameUI ui = GameUI.Instance;
        float white = 0f;
        if (ui != null && ui.Hud.Grenades != null) white = ui.Hud.Grenades.Flash(strength);
        GrenadeFx.PlayAt(GrenadeFx.Sounds.Ring, eye, 0.35f * strength, Grenades.M(5f));
        Debug.Log("[Grenade] Flashed: strength " + strength.ToString("F2") + " (" + metres.ToString("F1") + " m, " + angle.ToString("F0") +
                  "° off the view), white for " + white.ToString("F1") + " s");
    }

    // ------------------------------------------------------------------ smoke queries

    /// <summary>Does the line from <paramref name="a"/> to <paramref name="b"/> pass through a standing cloud?</summary>
    public static bool BlocksSight(Vector3 a, Vector3 b) {
        foreach (SmokeCloud c in SmokeCloud.All) {
            if (GrenadeFlight.SmokeBlocks(c.Floor, c.Age, a, b)) return true;
        }
        return false;
    }

    /// <summary>How deep in smoke <paramref name="point"/> is (0 clear .. 1 thick).</summary>
    public static float SmokeAt(Vector3 point) {
        float d = 0f;
        foreach (SmokeCloud c in SmokeCloud.All) d = Mathf.Max(d, GrenadeFlight.SmokeDensity(c.Floor, c.Age, point));
        return d;
    }

}
