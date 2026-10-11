using System;
using FishNet.Object;
using FishNet.Object.Synchronizing;

/// <summary>
/// The guns one avatar carries: the primary (and optic) its player picked, the pistol, and which is in hand. The owner
/// switches and reloads locally and reports it; the server keeps a model of each gun that reported shots must fit, and
/// tells everyone else what to draw. The grenades it carries are in Loadout.Grenades.cs.
/// </summary>
public partial class Loadout : NetworkBehaviour {

    public const int PrimarySlot = 0;
    public const int SecondarySlot = 1;
    private const double DrawTolerance = 0.8;

    private readonly SyncVar<byte> primary = new SyncVar<byte>();
    private readonly SyncVar<byte> optic = new SyncVar<byte>();
    private readonly SyncVar<byte> slot = new SyncVar<byte>();

    /// <summary>Client side: the gun in hand (or its optic) changed.</summary>
    public event Action Changed;

    private ServerWeapon[] weapons;
    private double readyAt = double.NegativeInfinity;

    public WeaponId Primary => (WeaponId)primary.Value;
    public OpticId Optic => (OpticId)optic.Value;
    public int Slot => slot.Value;
    public WeaponId InHand => WeaponIn(slot.Value);

    public WeaponId WeaponIn(int s) {
        return s == PrimarySlot ? Primary : Weapons.Secondary;
    }

    /// <summary>Optic on the gun in slot <paramref name="s"/>.</summary>
    public OpticId OpticIn(int s) {
        return s == PrimarySlot ? Optic : OpticId.Iron;
    }

    void Awake() {
        primary.OnChange += (a, b, asServer) => { if (!asServer) Changed?.Invoke(); };
        optic.OnChange += (a, b, asServer) => { if (!asServer) Changed?.Invoke(); };
        slot.OnChange += (a, b, asServer) => { if (!asServer) Changed?.Invoke(); };
        AwakeGrenades();
    }

    /// <summary>Server, before spawning the avatar: what it carries.</summary>
    public void Equip(WeaponId gun, OpticId sight) {
        if (!Weapons.IsPrimary(gun)) gun = Weapons.Primaries[0];
        WeaponSpec spec = Weapons.Get(gun);
        if (!spec.Fits(sight)) sight = spec.Optics[0];
        primary.Value = (byte)gun;
        optic.Value = (byte)sight;
        slot.Value = PrimarySlot;
    }

    public override void OnStartServer() {
        weapons = new ServerWeapon[2];
        for (int i = 0; i < weapons.Length; i++) {
            WeaponSpec s = Weapons.Get(WeaponIn(i));
            weapons[i] = new ServerWeapon(s.Magazine, s.Reserve, s.ReloadTime, s.Interval);
        }
        IssueGrenades();
    }

    // ------------------------------------------------------------------ owner → server

    /// <param name="time">The owner's clock (<see cref="AntiCheat.ClientClock"/>).</param>
    public void ReportSwitch(int to, double time) {
        if (IsOwner) ServerSwitch((byte)to, time);
    }

    public void ReportReload(double time) {
        if (IsOwner) ServerReload(time);
    }

    [ServerRpc]
    private void ServerSwitch(byte to, double time) {
        if (weapons == null || to > SecondarySlot || to == slot.Value) return;
        // Always follow the owner, or every later shot would be judged against the wrong gun.
        string why = AntiCheat.CheckClock(Owner, time);
        if (why != null) AntiCheat.Strike(Owner, "weapon: " + why);
        weapons[slot.Value].CancelReload(time);
        slot.Value = to;
        readyAt = time + Weapons.Get(WeaponIn(to)).DrawTime * DrawTolerance;
    }

    [ServerRpc]
    private void ServerReload(double time) {
        if (weapons != null) weapons[slot.Value].BeginReload(time);
    }

    /// <summary>Server: may <paramref name="id"/> fire at <paramref name="time"/>? Null if so (and a round is spent).</summary>
    public string TryFire(WeaponId id, double time) {
        if (weapons == null) return "no weapon";
        if (Held != GrenadeId.None) return "fired a " + Weapons.Get(id).Name + " while holding a " + Grenades.Get(Held).Name;
        if (id != InHand) return "fired a " + Weapons.Get(id).Name + " while holding a " + Weapons.Get(InHand).Name;
        if (time < readyAt) return "fired while drawing the weapon";
        return weapons[slot.Value].TryFire(time);
    }

}
