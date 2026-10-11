using System.Collections.Generic;
using UnityEngine;
using SlopFPS.Input;

/// <summary>
/// Elimination, client side: while the local player is out of the round (killed, or joined after it went live), a
/// camera follows a living teammate from just behind their shoulder. Click or Space moves on to the next one. With no
/// teammate left standing it watches whoever is. Added to the match object on every client; idle in other modes.
/// </summary>
public class Spectator : MonoBehaviour {

    /// <summary>After a death, the killed-in-action screen shows this long before the view moves to a teammate.</summary>
    public const float DeathCamTime = 3.5f;

    private const float RefreshInterval = 0.5f;

    public static Spectator Instance { get; private set; }

    /// <summary>The spectator camera is showing someone.</summary>
    public bool Active { get; private set; }

    /// <summary>Who is being watched (null when nobody is).</summary>
    public NetPlayer Target { get; private set; }

    private Camera cam;
    private PlayerHealth targetBody;
    private float outSince = -1f;
    private float refreshAt;
    private readonly List<Camera> muted = new List<Camera>();
    private bool snap;
    private UnityEngine.UI.Text hiddenTag;

    void Awake() {
        Instance = this;
    }

    void OnDestroy() {
        if (Instance == this) Instance = null;
        Stop();
        if (cam != null) Destroy(cam.gameObject);
    }

    void LateUpdate() {
        MatchManager mm = MatchManager.Instance;
        NetPlayer me = NetPlayer.Local;
        bool inRound = mm != null && mm.IsRoundBased && !mm.IsOver &&
                       (mm.Phase == RoundPhase.Freeze || mm.Phase == RoundPhase.Live || mm.Phase == RoundPhase.RoundEnd);
        bool out_ = inRound && me != null && !me.Alive.Value && GameUI.Instance != null && GameUI.Instance.InGame;
        if (!out_) {
            outSince = -1f;
            if (Active) Stop();
            return;
        }
        if (outSince < 0f) outSince = Time.time;
        // Killed just now: the death screen first. Joined mid-round (no body of our own): straight to the team.
        PlayerNetworkMover own = PlayerNetworkMover.Local;
        if (own != null && Time.time - outSince < DeathCamTime) return;

        if (Time.time > refreshAt || targetBody == null || targetBody.IsDead || Target == null || !Target.Alive.Value) {
            refreshAt = Time.time + RefreshInterval;
            if (Target == null || !Target.Alive.Value || targetBody == null || targetBody.IsDead) Pick(0);
        }
        if (!FpsInputGate.Blocked && (Input.GetMouseButtonDown(0) || Input.GetKeyDown(KeyCode.Space))) Next();

        if (targetBody == null) {
            if (Active) Stop();
            return;
        }
        if (!Active) Begin();
        Follow();
    }

    /// <summary>Watch the next living player (teammates first).</summary>
    public void Next() {
        Pick(1);
    }

    /// <summary>
    /// Living players to watch, teammates first, in a stable order; <paramref name="step"/> 0 keeps the current one if
    /// it is still standing, 1 moves on.
    /// </summary>
    private void Pick(int step) {
        NetPlayer me = NetPlayer.Local;
        List<NetPlayer> mates = new List<NetPlayer>(), others = new List<NetPlayer>();
        foreach (NetPlayer p in NetPlayer.All) {
            if (p == me || !p.Alive.Value) continue;
            if (me == null || me.Team == Team.None || p.Team == me.Team) mates.Add(p);
            else others.Add(p);
        }
        List<NetPlayer> pool = mates.Count > 0 ? mates : others;
        pool.Sort((a, b) => a.ClientId.CompareTo(b.ClientId));
        NetPlayer next = null;
        if (pool.Count > 0) {
            int at = Target != null ? pool.IndexOf(Target) : -1;
            next = at < 0 ? pool[0] : pool[(at + step) % pool.Count];
        }
        PlayerHealth body = next != null ? BodyOf(next) : null;
        if (next != Target || body != targetBody) {
            Target = next;
            targetBody = body;
            HideTag(body);
            snap = true;
            if (next != null) Debug.Log("[Spectator] Watching " + next.Callsign.Value + " (client " + next.ClientId + ", team " + next.Team + ")");
        }
    }

    /// <summary>The watched player's floating callsign would sit right in front of the camera; the HUD names them instead.</summary>
    private void HideTag(PlayerHealth body) {
        if (hiddenTag != null) hiddenTag.enabled = true;
        hiddenTag = null;
        NameTag tag = body != null ? body.GetComponentInChildren<NameTag>(true) : null;
        if (tag == null) return;
        hiddenTag = tag.GetComponentInChildren<UnityEngine.UI.Text>(true);
        if (hiddenTag != null) hiddenTag.enabled = false;
    }

    private static PlayerHealth BodyOf(NetPlayer p) {
        foreach (PlayerHealth h in FindObjectsOfType<PlayerHealth>()) {
            if (h.OwnerId == p.ClientId && !h.IsDead) return h;
        }
        return null;
    }

    private void Begin() {
        if (cam == null) {
            GameObject go = new GameObject("SpectatorCamera");
            cam = go.AddComponent<Camera>();
            Camera look = GameNetwork.Instance != null && GameNetwork.Instance.SceneCamera != null ? GameNetwork.Instance.SceneCamera : Camera.main;
            if (look != null) cam.CopyFrom(look);
            cam.depth = 90f;
            cam.fieldOfView = 70f;
            cam.nearClipPlane = 0.05f;
            cam.cullingMask &= ~LayerMask.GetMask("Hidden", "FPSGun", "UI");
            // Name tags and the HUD compass follow Camera.main.
            go.tag = "MainCamera";
        }
        // The fallen body's own first-person camera would render underneath for nothing.
        muted.Clear();
        PlayerNetworkMover own = PlayerNetworkMover.Local;
        if (own != null) {
            foreach (Camera c in own.GetComponentsInChildren<Camera>()) {
                if (c.enabled) {
                    c.enabled = false;
                    muted.Add(c);
                }
            }
        }
        cam.gameObject.SetActive(true);
        Active = true;
        snap = true;
    }

    private void Stop() {
        Active = false;
        Target = null;
        targetBody = null;
        HideTag(null);
        // Only a body that is still ours gets its camera back (the match ended with us down); a body being
        // replaced by the next round's stays dark until it is gone.
        PlayerNetworkMover own = PlayerNetworkMover.Local;
        foreach (Camera c in muted) {
            if (c != null && own != null && c.transform.IsChildOf(own.transform)) c.enabled = true;
        }
        muted.Clear();
        if (cam != null) cam.gameObject.SetActive(false);
    }

    /// <summary>Over the shoulder: behind the head and a little above it, pulled in front of any wall in the way.</summary>
    private void Follow() {
        Transform body = targetBody.transform;
        Camera eyeCam = targetBody.GetComponentInChildren<Camera>(true);
        Vector3 eye = eyeCam != null ? eyeCam.transform.position : body.position + Vector3.up * 1.6f * MapDefinition.MetresToWorld;
        float height = Mathf.Max(0.5f, eye.y - body.position.y);
        Vector3 forward = body.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
        forward.Normalize();
        Vector3 pivot = eye + body.right * height * 0.22f;
        Vector3 want = pivot - forward * height * 1.1f + Vector3.up * height * 0.18f;
        int mask = LayerMask.GetMask("Shootable", "Default");
        Vector3 d = want - pivot;
        if (Physics.SphereCast(pivot, 0.2f, d.normalized, out RaycastHit hit, d.magnitude, mask, QueryTriggerInteraction.Ignore)) {
            want = pivot + d.normalized * Mathf.Max(0.1f, hit.distance - 0.05f);
        }
        Quaternion rot = Quaternion.LookRotation(pivot + forward * height * 6f - want);
        float k = snap ? 1f : 1f - Mathf.Exp(-12f * Time.unscaledDeltaTime);
        snap = false;
        cam.transform.SetPositionAndRotation(Vector3.Lerp(cam.transform.position, want, k), Quaternion.Slerp(cam.transform.rotation, rot, k));
    }

}
