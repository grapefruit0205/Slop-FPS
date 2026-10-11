using UnityEngine;
using SlopFPS.Input;

/// <summary>
/// The local player's grenades: 4 / 5 / 6 (or G to cycle) take one out in place of the gun; hold the left button to
/// pull the pin and aim (the frag's fuse is burning from then on, and it goes off in the hand at zero), release for
/// the long overhand throw; the right button does the same for a short underhand lob. While held, an arc shows where
/// it will go (the same flight the server will fly). The grenade leaves the hand at once (<see cref="GrenadeWorld"/>
/// flies it here) and the throw goes to the server (<see cref="Loadout"/>), which checks it and throws the real one.
/// 1 / 2 or the wheel put it away; after a throw the gun comes back up. Owner only; <see cref="FpsGun"/> adds it.
/// </summary>
public class GrenadeHands : MonoBehaviour {

    // Where the grenade is held, in metres in the camera's space: resting, drawn from, and wound up for each throw.
    private static readonly Vector3 RestPose = new Vector3(0.17f, -0.17f, 0.46f);
    private static readonly Vector3 DrawFrom = new Vector3(0.17f, -0.4f, 0.4f);
    private static readonly Vector3 OverhandPose = new Vector3(0.22f, -0.07f, 0.4f);
    private static readonly Vector3 UnderhandPose = new Vector3(0.12f, -0.28f, 0.46f);
    private const float PoseSpeed = 10f;
    private const int ArcPoints = 120;
    private const float ArcSeconds = 3f;

    /// <summary>The local player's hands, while alive.</summary>
    public static GrenadeHands Local { get; private set; }

    private FpsGun gun;
    private Camera cam;
    private Loadout loadout;
    private PlayerHealth me;
    private AudioSource audioSource;
    private readonly int[] left = new int[4];
    private readonly GameObject[] models = new GameObject[4];
    private Transform holder;
    private float camScale = 1f;
    private Vector3 pose = DrawFrom;
    private float spin;

    private GrenadeId selected;
    private float readyAt;
    private bool holding;
    private int button;
    private float pinAt;
    private float returnAt;
    private ushort seq;

    private LineRenderer arc, landing;
    private readonly Vector3[] arcPoints = new Vector3[ArcPoints];

    // Automated tests: what to throw, how, and how long to hold it first.
    private GrenadeId devWant;
    private bool devOverhand;
    private float devCook;
    private float devReleaseAt;

    /// <summary>A grenade is out (or was just thrown and the gun is not back yet): the gun stays down.</summary>
    public bool InHand => selected != GrenadeId.None || returnAt > 0f;
    /// <summary>Pin out or mid-throw: no switching away now.</summary>
    public bool Busy => holding || returnAt > 0f;
    public GrenadeId Selected => selected;
    public int Left(GrenadeId id) {
        return left[(int)id];
    }

    public void Init(FpsGun owner, Camera view) {
        gun = owner;
        cam = view;
    }

    void Start() {
        loadout = GetComponentInParent<Loadout>();
        me = GetComponentInParent<PlayerHealth>();
        audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.playOnAwake = false;
        audioSource.spatialBlend = 0f;
        foreach (GrenadeId id in Grenades.All) left[(int)id] = Grenades.Get(id).PerLife;
        Local = this;
        if (cam != null) {
            holder = new GameObject("GrenadeHolder").transform;
            holder.gameObject.layer = gun.gameObject.layer;
            holder.SetParent(cam.transform, false);
            // The camera hangs under the scaled avatar: undo that, so the model is its real size.
            camScale = Mathf.Max(0.0001f, cam.transform.lossyScale.x);
            holder.localScale = Vector3.one / camScale;
        }
        arc = Line("GrenadeArc", Grenades.M(0.02f));
        landing = Line("GrenadeLanding", Grenades.M(0.015f));
        landing.loop = true;
        PushHud();
    }

    void OnDestroy() {
        if (Local == this) Local = null;
        if (arc != null) Destroy(arc.gameObject);
        if (landing != null) Destroy(landing.gameObject);
    }

    private LineRenderer Line(string name, float width) {
        GameObject go = new GameObject(name);
        go.layer = GrenadeFx.FxLayer;
        LineRenderer lr = go.AddComponent<LineRenderer>();
        lr.sharedMaterial = GrenadeFx.LineMaterial;
        lr.useWorldSpace = true;
        // Wider further out, so the far end of the arc still reads at a distance.
        lr.startWidth = width;
        lr.endWidth = width * 3f;
        lr.startColor = UITheme.WithAlpha(UITheme.Amber, 0.85f);
        lr.endColor = UITheme.WithAlpha(UITheme.Amber, 0.35f);
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        lr.numCapVertices = 2;
        lr.enabled = false;
        return lr;
    }

    // ------------------------------------------------------------------ input

    void Update() {
        if (me != null && me.IsDead) {
            if (selected != GrenadeId.None || holding || returnAt > 0f) Drop();
            return;
        }
        bool blocked = FpsInputGate.Blocked;
        bool frozen = FpsInputGate.Frozen;
        if (returnAt > 0f) {
            if (Time.time >= returnAt) {
                returnAt = 0f;
                if (gun != null) gun.DrawAfterGrenade();
            }
            Animate();
            return;
        }
        if (!blocked && !holding) {
            foreach (GrenadeId id in Grenades.All) {
                if (Input.GetKeyDown(Grenades.Get(id).Key)) Select(id);
            }
            if (Input.GetKeyDown(KeyCode.G)) Cycle();
        }
        if (devWant != GrenadeId.None && !holding && selected != devWant) Select(devWant);

        if (selected != GrenadeId.None) {
            GrenadeSpec spec = Grenades.Get(selected);
            bool ready = Time.time >= readyAt && !frozen && !blocked;
            if (!holding && ready) {
                if (devWant == selected) {
                    BeginHold(devOverhand ? 0 : 1);
                    devReleaseAt = Time.time + devCook;
                } else if (Input.GetMouseButtonDown(0)) {
                    BeginHold(0);
                } else if (Input.GetMouseButtonDown(1)) {
                    BeginHold(1);
                }
            }
            if (holding) {
                if (spec.Cookable && Time.time - pinAt >= spec.Fuse) {
                    CookedOff();
                    return;
                }
                bool down = devWant == selected ? Time.time < devReleaseAt : blocked || Input.GetMouseButton(button);
                if (!down) {
                    Throw();
                    return;
                }
                DrawArc();
            }
            PushHud();
        }
        Animate();
    }

    private void Cycle() {
        int start = selected == GrenadeId.None ? 0 : (int)selected;
        for (int k = 1; k <= Grenades.All.Length; k++) {
            GrenadeId id = (GrenadeId)((start + k - 1) % Grenades.All.Length + 1);
            if (left[(int)id] > 0) {
                Select(id);
                return;
            }
        }
    }

    /// <summary>Take <paramref name="id"/> out (if there is one left).</summary>
    public void Select(GrenadeId id) {
        if (holding || returnAt > 0f || id == selected) return;
        if (left[(int)id] <= 0) {
            UISounds.DryFire();
            return;
        }
        bool fromGun = selected == GrenadeId.None;
        selected = id;
        readyAt = Time.time + Grenades.DrawTime;
        pose = DrawFrom;
        ShowModel(id);
        if (fromGun && gun != null) gun.HolsterForGrenade();
        if (loadout != null) loadout.ReportHold(id, AntiCheat.ClientClock);
        PushHud();
    }

    /// <summary>Back to the gun (1 / 2 / the wheel): the grenade goes back in the pouch.</summary>
    public void PutAway() {
        selected = GrenadeId.None;
        holding = false;
        returnAt = 0f;
        ShowModel(GrenadeId.None);
        ShowArc(false);
        if (loadout != null) loadout.ReportHold(GrenadeId.None, AntiCheat.ClientClock);
        PushHud();
    }

    private void BeginHold(int which) {
        holding = true;
        button = which;
        pinAt = Time.time;
        audioSource.PlayOneShot(GrenadeFx.Sounds.Pin, 0.7f);
        if (loadout != null) loadout.ReportPin(selected, AntiCheat.ClientClock);
    }

    private void Throw() {
        GrenadeId type = selected;
        bool overhand = button == 0;
        Vector3 origin = cam.transform.position;
        Vector3 aim = cam.transform.forward;
        Vector3 start = GrenadeFlight.StartPoint(origin, aim);
        Vector3 velocity = GrenadeFlight.Launch(aim, overhand);
        if (++seq == 0) seq = 1;
        if (GrenadeWorld.Instance != null) GrenadeWorld.Instance.Predict(type, start, velocity, seq);
        if (loadout != null) loadout.ReportThrow(type, AntiCheat.ClientClock, origin, aim, overhand, seq);
        left[(int)type]--;
        float cooked = Time.time - pinAt;
        Debug.Log("[Grenade] Threw " + Grenades.Get(type).Name + (overhand ? " overhand" : " underhand") + " after " + cooked.ToString("F1") +
                  " s, " + left[(int)type] + " left");
        Finish(Grenades.RecoverTime);
    }

    /// <summary>Held the frag to the end of its fuse: the server sets it off in the hand.</summary>
    private void CookedOff() {
        left[(int)selected]--;
        Debug.Log("[Grenade] Held the 수류탄 too long");
        Finish(0.3f);
    }

    private void Finish(float recover) {
        holding = false;
        selected = GrenadeId.None;
        devWant = GrenadeId.None;
        ShowModel(GrenadeId.None);
        ShowArc(false);
        returnAt = Time.time + recover;
        PushHud();
    }

    /// <summary>Dead: everything goes (a cooking frag is the server's to drop).</summary>
    private void Drop() {
        selected = GrenadeId.None;
        holding = false;
        returnAt = 0f;
        devWant = GrenadeId.None;
        ShowModel(GrenadeId.None);
        ShowArc(false);
    }

    // ------------------------------------------------------------------ automated tests

    /// <summary>Tests: take <paramref name="id"/> out, pull the pin, hold it <paramref name="cook"/> seconds, throw.</summary>
    public bool DevThrow(GrenadeId id, bool overhand, float cook) {
        if (Busy || left[(int)id] <= 0) return false;
        devWant = id;
        devOverhand = overhand;
        devCook = cook;
        devReleaseAt = float.MaxValue;
        return true;
    }

    /// <summary>Tests: let go of a grenade held by <see cref="DevThrow"/> now.</summary>
    public void DevRelease() {
        devReleaseAt = 0f;
    }

    // ------------------------------------------------------------------ view

    private void ShowModel(GrenadeId id) {
        if (holder == null) return;
        for (int i = 1; i < models.Length; i++) {
            if (models[i] != null) models[i].SetActive(i == (int)id);
        }
        if (id == GrenadeId.None || models[(int)id] != null) return;
        GameObject m = GrenadeFx.Model(id, holder.gameObject.layer);
        m.transform.SetParent(holder, false);
        m.transform.localPosition = Vector3.zero;
        m.transform.localRotation = Quaternion.Euler(-10f, -25f, 8f);
        models[(int)id] = m;
    }

    private void Animate() {
        if (holder == null) return;
        Vector3 target = RestPose;
        if (holding) target = button == 0 ? OverhandPose : UnderhandPose;
        pose = Vector3.Lerp(pose, target, 1f - Mathf.Exp(-PoseSpeed * Time.deltaTime));
        holder.localPosition = pose * Grenades.M(1f) / camScale;
        // A little life: the hand rolls the grenade slightly; wound up for an overhand throw it tips back.
        spin += Time.deltaTime;
        float tip = holding && button == 0 ? -35f : holding ? 20f : 0f;
        holder.localRotation = Quaternion.Slerp(holder.localRotation, Quaternion.Euler(tip, Mathf.Sin(spin * 1.3f) * 4f, 0f), 1f - Mathf.Exp(-8f * Time.deltaTime));
    }

    private void DrawArc() {
        if (cam == null) return;
        Vector3 origin = cam.transform.position, aim = cam.transform.forward;
        int n = GrenadeFlight.Predict(GrenadeFlight.StartPoint(origin, aim), GrenadeFlight.Launch(aim, button == 0), ArcSeconds, arcPoints);
        // Skip the first metre so the line doesn't start in the player's face.
        int from = 0;
        while (from < n - 2 && Vector3.Distance(arcPoints[from], origin) < Grenades.M(1f)) from++;
        arc.positionCount = n - from;
        for (int i = from; i < n; i++) arc.SetPosition(i - from, arcPoints[i]);
        Vector3 end = arcPoints[n - 1];
        const int ring = 20;
        landing.positionCount = ring;
        float r = Grenades.M(0.3f);
        for (int i = 0; i < ring; i++) {
            float a = i * Mathf.PI * 2f / ring;
            landing.SetPosition(i, end + new Vector3(Mathf.Cos(a) * r, Grenades.M(0.03f), Mathf.Sin(a) * r));
        }
        ShowArc(true);
    }

    private void ShowArc(bool on) {
        if (arc != null) arc.enabled = on;
        if (landing != null) landing.enabled = on;
    }

    private void PushHud() {
        GameUI ui = GameUI.Instance;
        if (ui == null || ui.Hud.Grenades == null) return;
        ui.Hud.Grenades.Refresh(id => left[(int)id], selected);
        if (selected == GrenadeId.None) return;
        GrenadeSpec spec = Grenades.Get(selected);
        float fuse = holding && spec.Cookable ? Mathf.Max(0f, spec.Fuse - (Time.time - pinAt)) : -1f;
        ui.Hud.SetThrowable(spec, left[(int)selected], fuse);
    }

}
