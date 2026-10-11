using System;
using System.Collections.Generic;
using FishNet.Authenticating;
using FishNet.Broadcast;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Transporting;

public struct JoinRequest : IBroadcast {
    public string Name;
    public string PlayerSessionId;
    public string Version;
    public byte Primary;
    public byte Optic;
}

public struct JoinResult : IBroadcast {
    public bool Passed;
    public string Reason;
    /// <summary>The session's game mode key (tdm / ffa) and map id, so the client can build the level before its avatar arrives.</summary>
    public string Mode;
    public string Map;
}

/// <summary>
/// First message after connecting: the client sends its callsign and GameLift player session id, the server checks
/// both (and reserves the slot with GameLift) before letting the connection into the game. The reply carries the
/// session's mode and map; the client loads that map right away, ahead of the spawn messages that follow.
/// </summary>
public class FpsAuthenticator : Authenticator {

    public override event Action<NetworkConnection, bool> OnAuthenticationResult;

    /// <summary>Client side: what to send on the next connection.</summary>
    public static string PendingName = "";
    public static string PendingPlayerSessionId = "";

    /// <summary>Client side: why the server turned us away, if it did.</summary>
    public static string LastRejection { get; private set; }

    /// <summary>Client side: mode key and map id of the session we were let into.</summary>
    public static string JoinedMode { get; private set; }
    public static string JoinedMap { get; private set; }

    /// <summary>Server side: returns null to accept, or a player-facing reason to reject. Set by GameServer.</summary>
    public Func<NetworkConnection, JoinRequest, string> Validate;

    /// <summary>Server side: the session's mode key and map id, sent with every accepted join. Set by GameServer.</summary>
    public string SessionMode = "";
    public string SessionMap = "";

    private static readonly Dictionary<int, JoinRequest> accepted = new Dictionary<int, JoinRequest>();

    public static bool TryGetJoin(NetworkConnection conn, out JoinRequest join) {
        return accepted.TryGetValue(conn.ClientId, out join);
    }

    public static void Forget(NetworkConnection conn) {
        accepted.Remove(conn.ClientId);
    }

    public override void InitializeOnce(NetworkManager networkManager) {
        base.InitializeOnce(networkManager);
        networkManager.ClientManager.OnClientConnectionState += OnClientState;
        networkManager.ClientManager.RegisterBroadcast<JoinResult>(OnJoinResult);
        networkManager.ServerManager.RegisterBroadcast<JoinRequest>(OnJoinRequest, false);
    }

    private void OnClientState(ClientConnectionStateArgs args) {
        if (args.ConnectionState != LocalConnectionState.Started) return;
        LastRejection = null;
        NetworkManager.ClientManager.Broadcast(new JoinRequest {
            Name = PendingName, PlayerSessionId = PendingPlayerSessionId, Version = NetConfig.ProtocolVersion,
            Primary = (byte)LoadoutChoice.Primary, Optic = (byte)LoadoutChoice.Optic,
        });
    }

    private void OnJoinResult(JoinResult result, Channel channel) {
        if (!result.Passed) {
            LastRejection = L10n.T(result.Reason);
            return;
        }
        JoinedMode = result.Mode;
        JoinedMap = result.Map;
        MapLoader.Load(result.Map);
    }

    private void OnJoinRequest(NetworkConnection conn, JoinRequest request, Channel channel) {
        if (conn.IsAuthenticated) return;
        request.Name = CleanName(request.Name);
        string reason = null;
        if (request.Version != NetConfig.ProtocolVersion) reason = "게임 버전이 서버와 다릅니다. 페이지를 새로 고쳐 주세요.";
        else if (request.Name.Length == 0) reason = "콜사인이 비어 있습니다.";
        else if (Validate != null) reason = Validate(conn, request);

        bool passed = reason == null;
        if (passed) accepted[conn.ClientId] = request;
        NetworkManager.ServerManager.Broadcast(conn, new JoinResult {
            Passed = passed, Reason = reason, Mode = passed ? SessionMode : null, Map = passed ? SessionMap : null,
        }, false);
        OnAuthenticationResult?.Invoke(conn, passed);
    }

    /// <summary>Same rules as the lobby: 1–16 visible characters, no rich-text brackets.</summary>
    public static string CleanName(string name) {
        if (string.IsNullOrEmpty(name)) return "";
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        foreach (char c in name.Trim()) {
            if (char.IsControl(c) || c == '<' || c == '>') continue;
            sb.Append(c);
            if (sb.Length == 16) break;
        }
        return sb.ToString();
    }

}
