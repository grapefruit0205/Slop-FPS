using System.Collections.Generic;
using FishNet.Broadcast;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Transporting;
using UnityEngine;

/// <summary>Server to client, just before the server drops a connection on purpose.</summary>
public struct KickNotice : IBroadcast {
    public string Reason;
}

/// <summary>
/// Server side bookkeeping for rejected actions. Every check that throws out an impossible move or shot records a
/// strike with a weight; a connection that piles up <see cref="KickThreshold"/> within <see cref="Window"/> seconds is
/// disconnected. Single strikes are expected now and then from lag spikes, so they only log.
/// </summary>
public static class AntiCheat {

    public const int KickThreshold = 12;
    public const float Window = 60f;

    private const string KickMessage = "비정상적인 플레이가 감지되어 서버에서 연결이 끊어졌습니다.";

    private struct Mark {
        public float Time;
        public int Weight;
    }

    private static readonly Dictionary<int, List<Mark>> strikes = new Dictionary<int, List<Mark>>();

    /// <summary>
    /// Wall-clock seconds for every server-side check. Time.time advances at most maximumDeltaTime per frame, so after
    /// a server hitch it falls behind the client's clock and legitimate moves or shots look too fast.
    /// </summary>
    public static float Now => (float)Time.realtimeSinceStartupAsDouble;

    /// <summary>Client side: the reason given by the last kick, until the next connection.</summary>
    public static string LastKick { get; private set; }

    public static void Register(NetworkManager net) {
        net.ClientManager.RegisterBroadcast<KickNotice>(OnKickNotice);
        net.ClientManager.OnClientConnectionState += args => {
            if (args.ConnectionState == LocalConnectionState.Starting) LastKick = null;
        };
    }

    private static void OnKickNotice(KickNotice notice, Channel channel) {
        LastKick = notice.Reason;
    }

    /// <summary>Record a rejected action by <paramref name="conn"/>; kicks once the recent total is too high.</summary>
    public static void Strike(NetworkConnection conn, string reason, int weight = 1) {
        if (conn == null || !conn.IsActive) return;
        if (!strikes.TryGetValue(conn.ClientId, out List<Mark> list)) {
            list = new List<Mark>();
            strikes[conn.ClientId] = list;
        }
        float now = Now;
        list.RemoveAll(m => now - m.Time > Window);
        list.Add(new Mark { Time = now, Weight = weight });
        int total = 0;
        foreach (Mark m in list) total += m.Weight;
        Debug.LogWarning("[AntiCheat] Client " + conn.ClientId + ": " + reason + " (" + total + "/" + KickThreshold + ")" + (NetConfig.TraceMovement ? " @" + System.DateTime.Now.ToString("HH:mm:ss.fff") : ""));
        if (total >= KickThreshold) Kick(conn, "repeated violations");
    }

    public static void Kick(NetworkConnection conn, string why) {
        if (conn == null || !conn.IsActive) return;
        Debug.LogWarning("[AntiCheat] Kicking client " + conn.ClientId + ": " + why);
        conn.Broadcast(new KickNotice { Reason = KickMessage }, false);
        // Not immediate, so the notice above goes out first.
        conn.Disconnect(false);
        strikes.Remove(conn.ClientId);
    }

    public static void Forget(NetworkConnection conn) {
        strikes.Remove(conn.ClientId);
        clocks.Remove(conn.ClientId);
    }

    // ------------------------------------------------------------------ client clocks

    private const double MaxAhead = 2.0;       // seconds a client's timestamps may run ahead of the baseline
    private const double SettleTime = 5.0;     // the baseline forms during this long after the first sample...
    private const double SettleRange = 1.0;    // ...rising at most this far above the first sample
    private const double Drift = 0.02;         // afterwards it may only creep up this fast (s per s)

    /// <summary>Client side: the clock shots and reloads are stamped with.</summary>
    public static double ClientClock => Time.realtimeSinceStartupAsDouble;

    private class Clock {
        public double First;
        public double FirstAt;
        public double Base;
        public double LastAt;
    }

    private static readonly Dictionary<int, Clock> clocks = new Dictionary<int, Clock>();

    /// <summary>
    /// Server: a timestamp from <paramref name="conn"/>'s clock. Clients time their shots and reloads themselves (see
    /// <see cref="ServerWeapon"/>); this makes sure that clock keeps pace with real time instead of running ahead to
    /// fake a faster fire rate or a shorter reload. The baseline is the largest offset seen (the least delayed
    /// message); it settles in the first seconds and then may only creep, so a client cannot talk it up.
    /// Returns null when the timestamp is plausible, otherwise why not.
    /// </summary>
    public static string CheckClock(NetworkConnection conn, double clientTime) {
        if (conn == null) return null;
        double now = Time.realtimeSinceStartupAsDouble;
        double offset = clientTime - now;
        if (!clocks.TryGetValue(conn.ClientId, out Clock c)) {
            clocks[conn.ClientId] = new Clock { First = offset, FirstAt = now, Base = offset, LastAt = now };
            return null;
        }
        if (offset > c.Base) {
            c.Base = now - c.FirstAt < SettleTime
                ? System.Math.Min(offset, c.First + SettleRange)
                : System.Math.Min(offset, c.Base + Drift * (now - c.LastAt));
        }
        c.LastAt = now;
        double ahead = offset - c.Base;
        return ahead > MaxAhead ? "clock running " + ahead.ToString("F1") + " s ahead" : null;
    }

}
