using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

/// <summary>
/// One per connected player, alive for the whole connection (avatars come and go with deaths).
/// Holds the scoreboard state; only the server writes it.
/// </summary>
public class NetPlayer : NetworkBehaviour {

    public const int KillScore = 100;
    public const int HeadshotBonus = 50;
    private const float PingInterval = 4f;

    public static readonly List<NetPlayer> All = new List<NetPlayer>();
    public static NetPlayer Local { get; private set; }

    public readonly SyncVar<string> Callsign = new SyncVar<string>("");
    public readonly SyncVar<int> Kills = new SyncVar<int>();
    public readonly SyncVar<int> Deaths = new SyncVar<int>();
    public readonly SyncVar<int> Score = new SyncVar<int>();
    public readonly SyncVar<int> Ping = new SyncVar<int>();
    public readonly SyncVar<bool> Alive = new SyncVar<bool>();
    private readonly SyncVar<byte> primary = new SyncVar<byte>();
    private readonly SyncVar<byte> optic = new SyncVar<byte>();
    private readonly SyncVar<byte> team = new SyncVar<byte>();

    /// <summary>What this player's next avatar will carry.</summary>
    public WeaponId Primary => (WeaponId)primary.Value;
    public OpticId Optic => (OpticId)optic.Value;

    /// <summary>Side in a team mode (None in free-for-all). Assigned and balanced by the server.</summary>
    public Team Team {
        get => (Team)team.Value;
        set => team.Value = (byte)value;
    }

    private float pingTimer;

    public int ClientId => Owner != null ? Owner.ClientId : -1;

    public static NetPlayer ForClient(int clientId) {
        foreach (NetPlayer p in All) {
            if (p.ClientId == clientId) return p;
        }
        return null;
    }

    public override void OnStartNetwork() {
        if (!All.Contains(this)) All.Add(this);
    }

    public override void OnStopNetwork() {
        All.Remove(this);
        if (Local == this) Local = null;
    }

    public override void OnStartClient() {
        if (IsOwner) {
            Local = this;
            pingTimer = PingInterval;
        }
    }

    void Update() {
        if (!IsOwner || !IsClientInitialized) return;
        pingTimer += Time.unscaledDeltaTime;
        if (pingTimer >= PingInterval) {
            pingTimer = 0f;
            ServerReportPing((int)TimeManager.RoundTripTime, AntiCheat.ClientClock);
        }
    }

    [ServerRpc]
    private void ServerReportPing(int ms, double clientTime) {
        Ping.Value = Mathf.Clamp(ms, 0, 9999);
        // Regular samples of the client's clock, so the baseline that shot timestamps are held to exists early.
        AntiCheat.CheckClock(Owner, clientTime);
    }

    /// <summary>Owner: carry this on the next deployment.</summary>
    public void ChooseLoadout(WeaponId gun, OpticId sight) {
        if (IsOwner) ServerChooseLoadout(gun, sight);
    }

    [ServerRpc]
    private void ServerChooseLoadout(WeaponId gun, OpticId sight) {
        SetLoadout(gun, sight);
    }

    /// <summary>Server (also before spawning): the next avatar's guns, corrected to something legal.</summary>
    public void SetLoadout(WeaponId gun, OpticId sight) {
        if (!Weapons.IsPrimary(gun)) gun = Weapons.Primaries[0];
        WeaponSpec spec = Weapons.Get(gun);
        if (!spec.Fits(sight)) sight = spec.Optics[0];
        primary.Value = (byte)gun;
        optic.Value = (byte)sight;
    }

    [Server]
    public void AddKill(bool headshot) {
        Kills.Value++;
        Score.Value += KillScore + (headshot ? HeadshotBonus : 0);
    }

    [Server]
    public void AddDeath() {
        Deaths.Value++;
    }

    [Server]
    public void ResetStats() {
        Kills.Value = 0;
        Deaths.Value = 0;
        Score.Value = 0;
    }

}
