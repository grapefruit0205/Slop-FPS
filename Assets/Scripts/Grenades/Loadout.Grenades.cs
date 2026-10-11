using System;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

/// <summary>
/// The grenades an avatar carries (one of each per life, so per round in elimination, where every round deploys a new
/// avatar) and which one is in hand. The owner picks, pulls the pin and throws locally (<see cref="GrenadeHands"/>) and
/// reports it; the server counts them, checks every throw (alive, not frozen, has one, not too soon after the last,
/// thrown from where the body is and roughly where it faces), times the fuse from its own record of the pin, and then
/// flies the grenade itself (<see cref="GrenadeServer"/>). A frag cooked too long goes off in the hand; one cooking
/// when its thrower dies drops at the body.
/// </summary>
public partial class Loadout {

    private readonly SyncVar<byte> frags = new SyncVar<byte>();
    private readonly SyncVar<byte> flashes = new SyncVar<byte>();
    private readonly SyncVar<byte> smokes = new SyncVar<byte>();
    private readonly SyncVar<byte> held = new SyncVar<byte>();

    /// <summary>Client side: a grenade count or the grenade in hand changed.</summary>
    public event Action GrenadesChanged;

    /// <summary>The grenade in hand (None while a gun is).</summary>
    public GrenadeId Held => (GrenadeId)held.Value;

    // Server: the pin, and the last accepted throw.
    private GrenadeId pinned;
    private float pinnedAt;
    private float lastThrow = -100f;
    private PlayerHealth body;
    private MovementGuard guard;

    public int GrenadeCount(GrenadeId id) {
        switch (id) {
            case GrenadeId.Frag: return frags.Value;
            case GrenadeId.Flash: return flashes.Value;
            case GrenadeId.Smoke: return smokes.Value;
            default: return 0;
        }
    }

    private SyncVar<byte> CountOf(GrenadeId id) {
        return id == GrenadeId.Frag ? frags : id == GrenadeId.Flash ? flashes : id == GrenadeId.Smoke ? smokes : null;
    }

    private void AwakeGrenades() {
        body = GetComponent<PlayerHealth>();
        guard = GetComponent<MovementGuard>();
        frags.OnChange += (a, b, asServer) => { if (!asServer) GrenadesChanged?.Invoke(); };
        flashes.OnChange += (a, b, asServer) => { if (!asServer) GrenadesChanged?.Invoke(); };
        smokes.OnChange += (a, b, asServer) => { if (!asServer) GrenadesChanged?.Invoke(); };
        held.OnChange += (a, b, asServer) => { if (!asServer) GrenadesChanged?.Invoke(); };
    }

    /// <summary>Server, on deploying: a full set.</summary>
    private void IssueGrenades() {
        foreach (GrenadeId id in Grenades.All) CountOf(id).Value = (byte)Grenades.Get(id).PerLife;
        held.Value = (byte)GrenadeId.None;
        pinned = GrenadeId.None;
        // Made here so it is listening for joiners before the first throw.
        _ = GrenadeServer.Instance;
        Debug.Log("[Grenade] Client " + OwnerId + " deploys with " + frags.Value + " 수류탄, " + flashes.Value + " 섬광탄, " + smokes.Value + " 연막탄");
    }

    // ------------------------------------------------------------------ owner → server

    /// <summary>Owner: <paramref name="id"/> is now in hand (None: back to the gun).</summary>
    public void ReportHold(GrenadeId id, double time) {
        if (IsOwner) ServerHold((byte)id, time);
    }

    /// <summary>Owner: the pin is out (the frag's fuse runs from here).</summary>
    public void ReportPin(GrenadeId id, double time) {
        if (IsOwner) ServerPin((byte)id, time);
    }

    /// <summary>
    /// Owner: thrown from the eye at <paramref name="origin"/> along <paramref name="aim"/> (the camera's forward).
    /// <paramref name="seq"/> lets the thrower match the server's grenade to the one it already has in the air.
    /// </summary>
    public void ReportThrow(GrenadeId id, double time, Vector3 origin, Vector3 aim, bool overhand, ushort seq) {
        if (IsOwner) ServerThrow((byte)id, time, origin, aim, overhand, seq);
    }

    [ServerRpc]
    private void ServerHold(byte id, double time) {
        if (weapons == null) return;
        if (id == (byte)GrenadeId.None) {
            if (held.Value == id) return;
            held.Value = id;
            pinned = GrenadeId.None;
            // The gun comes back up; it may not fire before it could have been drawn.
            readyAt = time + Weapons.Get(InHand).DrawTime * DrawTolerance;
            return;
        }
        if (!Grenades.IsValid(id)) {
            AntiCheat.Strike(Owner, "grenade: malformed pick", 2);
            return;
        }
        if (GrenadeCount((GrenadeId)id) <= 0 || body == null || body.IsDead) return;
        weapons[slot.Value].CancelReload(time);
        held.Value = id;
    }

    [ServerRpc]
    private void ServerPin(byte id, double time) {
        if (weapons == null || !Grenades.IsValid(id) || id != held.Value || GrenadeCount((GrenadeId)id) <= 0) return;
        pinned = (GrenadeId)id;
        pinnedAt = AntiCheat.Now;
    }

    [ServerRpc]
    private void ServerThrow(byte id, double time, Vector3 origin, Vector3 aim, bool overhand, ushort seq) {
        // A throw racing its thrower's death is simply too late.
        if (weapons == null || body == null || body.IsDead) return;
        string why = AntiCheat.CheckClock(Owner, time);
        if (why != null) {
            AntiCheat.Strike(Owner, "grenade: " + why);
            return;
        }
        if (!Grenades.IsValid(id)) {
            AntiCheat.Strike(Owner, "grenade: malformed throw", 2);
            return;
        }
        GrenadeSpec spec = Grenades.Get((GrenadeId)id);
        MatchManager mm = MatchManager.Instance;
        if (mm != null && mm.InFreeze) {
            AntiCheat.Strike(Owner, "grenade: thrown during the freeze");
            return;
        }
        SyncVar<byte> count = CountOf(spec.Id);
        if (count.Value <= 0) {
            AntiCheat.Strike(Owner, "grenade: threw a " + spec.Name + " with none left", 2);
            return;
        }
        float now = AntiCheat.Now;
        if (now - lastThrow < Grenades.MinThrowInterval) {
            AntiCheat.Strike(Owner, "grenade: throw rate (" + ((now - lastThrow) * 1000f).ToString("F0") + " ms after the last)");
            return;
        }
        float sq = aim.sqrMagnitude;
        if (float.IsNaN(sq) || sq < 0.5f || sq > 1.5f || float.IsNaN(origin.x + origin.y + origin.z)) {
            AntiCheat.Strike(Owner, "grenade: malformed throw", 2);
            return;
        }
        aim.Normalize();
        float window = body.LagWindow();
        if (guard != null && !guard.WasNear(origin, window, PlayerHealth.OriginSlack, out _, out float off)) {
            AntiCheat.Strike(Owner, "grenade: thrown " + (off / Grenades.M(1f)).ToString("F1") + " m away from the body", 2);
            return;
        }
        if (!body.FacedRecently(aim, window)) {
            AntiCheat.Strike(Owner, "grenade: thrown away from where the player was looking");
            return;
        }

        count.Value--;
        held.Value = (byte)GrenadeId.None;
        lastThrow = now;
        // The frag's fuse has been burning since the pin came out (by the server's clock, not the thrower's word).
        float fuse = spec.Fuse;
        if (spec.Cookable && pinned == spec.Id) fuse = Mathf.Max(0.05f, spec.Fuse - (now - pinnedAt));
        float cooked = spec.Fuse - fuse;
        pinned = GrenadeId.None;
        Vector3 start = GrenadeFlight.StartPoint(origin, aim);
        Vector3 velocity = GrenadeFlight.Launch(aim, overhand);
        ushort gid = GrenadeServer.Instance.Launch(spec, OwnerId, start, velocity, fuse, seq);
        Debug.Log("[Grenade] Client " + OwnerId + " throws " + spec.Name + " #" + gid + (overhand ? " overhand" : " underhand") +
                  (spec.Cookable ? ", cooked " + cooked.ToString("F1") + " s" : "") + " from " + start.ToString("F1") + ", " + count.Value + " left");
    }

    // ------------------------------------------------------------------ server: the pin

    void Update() {
        if (!IsServerInitialized || pinned != GrenadeId.Frag || weapons == null) return;
        GrenadeSpec spec = Grenades.Get(GrenadeId.Frag);
        SyncVar<byte> count = CountOf(GrenadeId.Frag);
        if (body != null && body.IsDead) {
            // Killed with the pin out: it rolls out of the dead hand with what is left of the fuse.
            pinned = GrenadeId.None;
            if (count.Value <= 0) return;
            count.Value--;
            float left = Mathf.Max(0.05f, spec.Fuse - (AntiCheat.Now - pinnedAt));
            GrenadeServer.Instance.Launch(spec, OwnerId, Hand(), Vector3.zero, left, 0);
            Debug.Log("[Grenade] Client " + OwnerId + " died with the pin out: the 수류탄 drops, " + left.ToString("F1") + " s left");
            return;
        }
        if (AntiCheat.Now - pinnedAt < spec.Fuse + Grenades.CookGrace) return;
        pinned = GrenadeId.None;
        if (count.Value <= 0) return;
        count.Value--;
        held.Value = (byte)GrenadeId.None;
        Debug.Log("[Grenade] Client " + OwnerId + " held a cooked 수류탄 too long: it goes off in the hand");
        GrenadeServer.Instance.GoOffInHand(spec, OwnerId, Hand());
    }

    /// <summary>About where the throwing hand is: chest high on the body.</summary>
    private Vector3 Hand() {
        Bounds b = GrenadeServer.BodyBounds(body);
        return b.center + Vector3.up * b.extents.y * 0.3f;
    }

}
