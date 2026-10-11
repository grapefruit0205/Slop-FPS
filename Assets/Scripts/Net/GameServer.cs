using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FishNet;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Object;
using FishNet.Transporting;
using FishNet.Transporting.Bayou;
using UnityEngine;

/// <summary>
/// Dedicated-server side of a game session: starts the transport (with TLS on GameLift), lets authenticated players
/// in, spawns and respawns their avatars, restarts rounds, and ends the process when the session is over so GameLift
/// can recycle it. The session's game mode and map come from the GameLift game properties `mode` / `map` (or -mode /
/// -map without GameLift); the map is loaded before anyone may join, and players are put on teams as they arrive.
/// In elimination the dead stay down until the next round (<see cref="MatchManager"/> says when one starts) and late
/// joiners watch until then.
/// </summary>
public class GameServer : MonoBehaviour {

    /// <summary>A session nobody joins within this time is given back to GameLift.</summary>
    private const float JoinTimeout = 180f;
    /// <summary>Once everyone has left, end the session after this long.</summary>
    private const float EmptyTimeout = 60f;

    [SerializeField]
    private NetworkObject netPlayerPrefab;
    [SerializeField]
    private NetworkObject matchPrefab;

    private NetworkManager net;
    private GameNetwork game;
    private MatchManager match;
    private readonly Dictionary<int, NetworkObject> avatars = new Dictionary<int, NetworkObject>();
    private readonly Dictionary<int, NetPlayer> players = new Dictionary<int, NetPlayer>();
    private readonly HashSet<int> joinedOnce = new HashSet<int>();
    private readonly HashSet<int> respawning = new HashSet<int>();
    private bool sessionActive;
    private GameMode mode = Modes.Default;
    private string mapId = MapLoader.DefaultMap;
    private bool hadPlayers;
    private float idleSince;

    public static GameServer Instance { get; private set; }

    /// <param name="portOverride">Dev screenshot mode hosts on its own port; otherwise -port / 7770.</param>
    public void Run(GameNetwork game, NetworkManager net, ushort portOverride = 0) {
        Instance = this;
        this.game = game;
        this.net = net;
        Application.targetFrameRate = 60;

        ushort port = portOverride != 0 ? portOverride : NetConfig.ServerPort;
        Bayou bayou = net.TransportManager.GetTransport<Bayou>();
        bayou.SetPort(port);

        if (NetConfig.UseGameLift) {
            string logPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "..", "logs"));
            if (!GameLiftHost.Init(port, logPath)) {
                Debug.LogError("[GameServer] GameLift init failed; quitting so the fleet restarts this process.");
                Application.Quit(1);
                return;
            }
            GameLiftHost.SessionStarted += OnSessionStarted;
            GameLiftHost.TerminationRequested += Shutdown;
            EnableTls(bayou, null);
        } else {
            // Local/dev server: always "in session", with the mode and map from the command line.
            ApplySessionSettings(NetConfig.ModeArg, NetConfig.MapArg);
            sessionActive = true;
            // -tlsdir <dir with certificate.pem/privateKey.pem>: test wss:// without GameLift.
            string tlsDir = NetConfig.TlsDir;
            if (tlsDir != null) EnableTls(bayou, tlsDir);
        }

        FpsAuthenticator auth = net.ServerManager.GetAuthenticator() as FpsAuthenticator;
        if (auth != null) auth.Validate = ValidateJoin;
        ShareSessionSettings();
        net.ServerManager.OnRemoteConnectionState += OnRemoteConnectionState;
        net.SceneManager.OnClientLoadedStartScenes += OnClientLoadedStartScenes;
        net.ServerManager.OnServerConnectionState += OnServerState;
        PlayerHealth.ServerDied += OnAvatarDied;
        idleSince = Time.time;

        net.ServerManager.StartConnection();
        Debug.Log("[GameServer] Listening on " + port + (NetConfig.UseGameLift ? " (GameLift)" : " (standalone)"));
    }

    /// <param name="pemDir">Local PEM files to use; null asks GameLift for the fleet's generated certificate.</param>
    private void EnableTls(Bayou bayou, string pemDir) {
        string password = System.Guid.NewGuid().ToString("N");
        string pfx = pemDir != null ? GameLiftHost.PemToPfx(pemDir, password) : GameLiftHost.PrepareCertificate(password);
        if (pfx == null) {
            Debug.LogWarning("[GameServer] No TLS certificate; browsers on https pages will not be able to connect.");
            return;
        }
        // Bayou keeps its SSL settings private and serialized; set them before the server starts.
        FieldInfo field = typeof(Bayou).GetField("_sslConfiguration", BindingFlags.Instance | BindingFlags.NonPublic);
        field.SetValue(bayou, new SslConfiguration(true, pfx, password, System.Security.Authentication.SslProtocols.Tls12));
        Debug.Log("[GameServer] TLS enabled (wss)");
    }

    private void OnServerState(ServerConnectionStateArgs args) {
        if (args.ConnectionState == LocalConnectionState.Started) {
            match = Instantiate(matchPrefab).GetComponent<MatchManager>();
            match.Configure(mode, mapId, NetConfig.ScoreLimitOverride, NetConfig.RoundTimeOverride);
            net.ServerManager.Spawn(match.gameObject);
            match.ServerRoundRestart += RestartRound;
            match.ServerRoundBegin += BeginRound;
        }
    }

    private void OnSessionStarted(string sessionName, Dictionary<string, string> properties) {
        properties.TryGetValue("mode", out string wantMode);
        properties.TryGetValue("map", out string wantMap);
        // The map is in place before the session opens, so nobody can join into an empty world.
        ApplySessionSettings(wantMode, wantMap);
        sessionActive = true;
        hadPlayers = false;
        idleSince = Time.time;
        if (match != null) {
            match.RoomName.Value = sessionName ?? "";
            match.Configure(mode, mapId, NetConfig.ScoreLimitOverride, NetConfig.RoundTimeOverride);
            match.StartNewMatch();
        }
    }

    /// <summary>Decide this session's mode and map (unknown values fall back to TDM / warehouse) and load the map.</summary>
    private void ApplySessionSettings(string wantMode, string wantMap) {
        if (!string.IsNullOrEmpty(wantMode) && !Modes.IsKnown(wantMode)) {
            Debug.LogWarning("[GameServer] Unknown mode '" + wantMode + "', using " + Modes.Key(Modes.Default));
        }
        mode = Modes.Parse(wantMode);
        mapId = MapLoader.Load(wantMap).id;
        ShareSessionSettings();
        Debug.Log("[GameServer] Session settings: mode " + Modes.Key(mode) + " (" + Modes.DisplayName(mode) + "), map " + mapId);
    }

    /// <summary>Every accepted join reply carries the mode and map, so clients load the level before their avatar arrives.</summary>
    private void ShareSessionSettings() {
        if (net == null) return;
        FpsAuthenticator auth = net.ServerManager.GetAuthenticator() as FpsAuthenticator;
        if (auth == null) return;
        auth.SessionMode = Modes.Key(mode);
        auth.SessionMap = mapId;
    }

    private string ValidateJoin(NetworkConnection conn, JoinRequest request) {
        string reason = null;
        if (!sessionActive) reason = "서버가 아직 준비되지 않았습니다. 잠시 후 다시 시도해 주세요.";
        else if (players.Count >= NetConfig.MaxPlayersPerRoom) reason = "방이 가득 찼습니다.";
        else if (!GameLiftHost.AcceptPlayer(request.PlayerSessionId)) reason = "입장권이 만료되었습니다. 로비에서 다시 참가해 주세요.";
        if (reason != null) Debug.Log("[GameServer] Turned away client " + conn.ClientId + " (" + request.Name + "): " + reason);
        return reason;
    }

    private void OnClientLoadedStartScenes(NetworkConnection conn, bool asServer) {
        if (!asServer || players.ContainsKey(conn.ClientId)) return;
        FpsAuthenticator.TryGetJoin(conn, out JoinRequest join);
        NetworkObject np = Instantiate(netPlayerPrefab);
        NetPlayer player = np.GetComponent<NetPlayer>();
        player.Callsign.Value = UniqueCallsign(string.IsNullOrEmpty(join.Name) ? "Player" + conn.ClientId : join.Name);
        player.SetLoadout((WeaponId)join.Primary, (OpticId)join.Optic);
        player.Team = Teams.Pick(players.Values, mode);
        net.ServerManager.Spawn(np, conn);
        players[conn.ClientId] = player;
        hadPlayers = true;
        Debug.Log("[GameServer] Client " + conn.ClientId + " joined as " + player.Callsign.Value + (player.Team != Team.None ? ", team " + player.Team : "") +
                  " (" + players.Count + "/" + NetConfig.MaxPlayersPerRoom + "; " + TeamCounts() + ")");
        if (match == null || match.AllowsJoinSpawn) {
            SpawnAvatar(conn);
            return;
        }
        // Elimination round under way: watch the team until the next one.
        joinedOnce.Add(conn.ClientId);
        Debug.Log("[GameServer] Client " + conn.ClientId + " joined during round " + match.Round + ", spectating until the next round");
        match.MessageLocalized("event.joinlate", UITheme.Safe(player.Callsign.Value));
    }

    private string TeamCounts() {
        if (!Modes.IsTeamMode(mode)) return Modes.Key(mode);
        int a = players.Values.Count(p => p.Team == Team.A), b = players.Values.Count(p => p.Team == Team.B);
        return "team A " + a + " / B " + b;
    }

    /// <summary>Two "Ghost"s on one scoreboard are confusing; the second becomes "Ghost2".</summary>
    private string UniqueCallsign(string wanted) {
        string name = wanted;
        for (int n = 2; players.Values.Any(p => p.Callsign.Value == name); n++) {
            string suffix = n.ToString();
            name = (wanted.Length + suffix.Length > 16 ? wanted.Substring(0, 16 - suffix.Length) : wanted) + suffix;
        }
        return name;
    }

    private void OnRemoteConnectionState(NetworkConnection conn, RemoteConnectionStateArgs args) {
        if (args.ConnectionState != RemoteConnectionState.Stopped) return;
        if (FpsAuthenticator.TryGetJoin(conn, out JoinRequest join)) GameLiftHost.RemovePlayer(join.PlayerSessionId);
        FpsAuthenticator.Forget(conn);
        AntiCheat.Forget(conn);
        joinedOnce.Remove(conn.ClientId);
        if (avatars.TryGetValue(conn.ClientId, out NetworkObject avatar)) {
            avatars.Remove(conn.ClientId);
            if (avatar != null && avatar.IsSpawned) net.ServerManager.Despawn(avatar);
        }
        if (players.TryGetValue(conn.ClientId, out NetPlayer player)) {
            players.Remove(conn.ClientId);
            Debug.Log("[GameServer] Client " + conn.ClientId + " left (" + players.Count + "/" + NetConfig.MaxPlayersPerRoom + ")");
            if (match != null) match.MessageLocalized("event.leave", UITheme.Safe(player.Callsign.Value));
            // FishNet despawns objects owned by a leaving connection itself.
        }
        if (players.Count == 0) idleSince = Time.time;
    }

    /// <param name="announce">Say so in the event log ("joined" / "redeployed"); round starts don't, for everyone at once.</param>
    private void SpawnAvatar(NetworkConnection conn, bool announce = true) {
        if (!conn.IsActive || !players.TryGetValue(conn.ClientId, out NetPlayer player)) return;
        MapDefinition map = MapLoader.Current;
        Transform[] spawns = map != null ? map.SpawnsFor(player.Team) : game.SpawnPoints;
        Transform spot = PickSpawn(spawns, player);
        Vector3 pos = spot.position;
        Quaternion rot = spot.rotation;
        // Tests want everyone in one place whatever their side: around the map's first free-for-all point.
        if (NetConfig.DevSpawn) DevSpawnSpot(map != null ? map.spawnPoints[0] : spawns[0], out pos, out rot);
        GameObject[] models = game.PlayerModels;
        GameObject avatar = Instantiate(models[Random.Range(0, models.Length)], pos, rot);
        Loadout loadout = avatar.GetComponent<Loadout>();
        if (loadout != null) loadout.Equip(player.Primary, player.Optic);
        net.ServerManager.Spawn(avatar, conn);
        avatars[conn.ClientId] = avatar.GetComponent<NetworkObject>();
        player.Alive.Value = true;
        Debug.Log("[GameServer] Client " + conn.ClientId + (player.Team != Team.None ? " (team " + player.Team + ")" : "") + " deploys at " +
                  (NetConfig.DevSpawn ? "dev spot" : spot.name) + " " + pos.ToString("F1") + ", primary " + player.Primary + ", optic " + player.Optic);
        bool first = joinedOnce.Add(conn.ClientId);
        if (match != null && (announce || first)) {
            match.MessageLocalized(first ? "event.join" : "event.spawn", UITheme.Safe(player.Callsign.Value));
        }
    }

    /// <summary>
    /// The spawn point farthest from every living enemy (random among the best few), so nobody respawns in front of a
    /// gun. Teammates only count when they stand on the spot, so two players never appear inside each other.
    /// </summary>
    private Transform PickSpawn(Transform[] spawns, NetPlayer who) {
        List<KeyValuePair<float, Transform>> scored = new List<KeyValuePair<float, Transform>>();
        foreach (Transform s in spawns) {
            float nearest = float.MaxValue;
            foreach (KeyValuePair<int, NetworkObject> kv in avatars) {
                NetworkObject a = kv.Value;
                if (a == null || !a.IsSpawned) continue;
                PlayerHealth h = a.GetComponent<PlayerHealth>();
                if (h != null && h.IsDead) continue;
                float d = Vector3.Distance(a.transform.position, s.position);
                bool ally = who.Team != Team.None && players.TryGetValue(kv.Key, out NetPlayer other) && other.Team == who.Team;
                if (ally) d = d < 2.5f ? 0f : float.MaxValue;
                nearest = Mathf.Min(nearest, d);
            }
            scored.Add(new KeyValuePair<float, Transform>(nearest, s));
        }
        scored.Sort((x, y) => y.Key.CompareTo(x.Key));
        return scored[Random.Range(0, Mathf.Min(3, scored.Count))].Value;
    }

    /// <summary>
    /// Automated tests (-devspawn): put everyone within sight of the first spawn point, each new avatar on a free spot
    /// 8 m from it, all facing that point.
    /// </summary>
    private void DevSpawnSpot(Transform center, out Vector3 pos, out Quaternion rot) {
        pos = center.position;
        rot = center.rotation;
        bool taken = false;
        foreach (NetworkObject a in avatars.Values) {
            if (a == null) continue;
            Vector3 d = a.transform.position - center.position;
            d.y = 0f;
            if (d.magnitude < 1.5f) taken = true;
        }
        if (!taken) return;
        int mask = LayerMask.GetMask("Shootable", "Default");
        // Spawn points float a little above the floor; measure from the floor under the first one.
        Vector3 basePos = Physics.Raycast(center.position + Vector3.up * 0.5f, Vector3.down, out RaycastHit under, 5f, mask,
                                          QueryTriggerInteraction.Ignore) ? under.point : center.position;
        for (int i = 0; i < 16; i++) {
            Vector3 dir = Quaternion.Euler(0f, i * 22.5f + Random.Range(0f, 10f), 0f) * center.forward;
            Vector3 candidate = basePos + dir * 8f;
            // Start outside the first player's body so its own hitboxes don't block the view.
            Vector3 from = basePos + Vector3.up * 1.4f + dir * 0.8f;
            bool clear = !Physics.Linecast(from, candidate + Vector3.up * 1.4f, mask, QueryTriggerInteraction.Ignore);
            bool floor = Physics.Raycast(candidate + Vector3.up * 1.5f, Vector3.down, out RaycastHit ground, 4f, mask,
                                         QueryTriggerInteraction.Ignore);
            // Not where someone already stands (a round start deploys everyone at once).
            bool free = true;
            foreach (NetworkObject a in avatars.Values) {
                if (a == null) continue;
                Vector3 d = a.transform.position - ground.point;
                d.y = 0f;
                if (d.magnitude < 1.5f) free = false;
            }
            if (clear && floor && free) {
                pos = ground.point + Vector3.up * 0.05f;
                rot = Quaternion.LookRotation(-dir);
                return;
            }
        }
    }

    private void OnAvatarDied(PlayerHealth victim) {
        if (match != null && !match.AllowsRespawn) {
            // Elimination: out until the next round, which replaces the body.
            Debug.Log("[GameServer] Client " + victim.OwnerId + " is out until the next round (round " + match.Round + ")");
            return;
        }
        StartCoroutine(Respawn(victim.Owner, victim.NetworkObject, victim.RespawnTime));
    }

    private IEnumerator Respawn(NetworkConnection conn, NetworkObject oldAvatar, float delay) {
        respawning.Add(conn.ClientId);
        yield return new WaitForSeconds(delay);
        respawning.Remove(conn.ClientId);
        // The round may have restarted (and respawned everyone) while we waited, or an elimination round begun.
        if (oldAvatar == null || !oldAvatar.IsSpawned) yield break;
        if (match != null && !match.AllowsRespawn) yield break;
        if (avatars.TryGetValue(conn.ClientId, out NetworkObject current) && current == oldAvatar) avatars.Remove(conn.ClientId);
        net.ServerManager.Despawn(oldAvatar);
        Debug.Log("[GameServer] Respawning client " + conn.ClientId);
        SpawnAvatar(conn);
    }

    /// <summary>
    /// Elimination, start of a round: even the sides out, then every player gets a fresh avatar (full health, full
    /// magazines, their chosen loadout) at their side's spawns, the dead and late joiners included.
    /// </summary>
    private void BeginRound(bool firstOfMatch) {
        foreach (NetPlayer p in Teams.Rebalance(players.Values, mode)) {
            if (match != null) match.MessageLocalized("event.balance", UITheme.Safe(p.Callsign.Value));
            Debug.Log("[GameServer] Rebalanced " + p.Callsign.Value + " to team " + p.Team);
        }
        // Kills made while warming up don't count towards the match.
        if (firstOfMatch) {
            foreach (NetPlayer p in players.Values) p.ResetStats();
        }
        DespawnAvatars();
        foreach (NetPlayer p in players.Values) {
            p.Alive.Value = false;
            if (p.Owner != null && p.Owner.IsActive) SpawnAvatar(p.Owner, false);
        }
        Debug.Log("[GameServer] Round " + match.Round + ": deployed " + avatars.Count + " players (" + TeamCounts() + ")");
    }

    private void DespawnAvatars() {
        foreach (KeyValuePair<int, NetworkObject> kv in avatars) {
            if (kv.Value != null && kv.Value.IsSpawned) net.ServerManager.Despawn(kv.Value);
        }
        avatars.Clear();
    }

    /// <summary>New round: wipe the scoreboard and put everyone back at full health on a fresh spawn.</summary>
    private void RestartRound() {
        // Even the sides out first (people may have left), while the old scores still say who to move.
        foreach (NetPlayer p in Teams.Rebalance(players.Values, mode)) {
            if (match != null) match.MessageLocalized("event.balance", UITheme.Safe(p.Callsign.Value));
            Debug.Log("[GameServer] Rebalanced " + p.Callsign.Value + " to team " + p.Team);
        }
        foreach (NetPlayer p in players.Values) p.ResetStats();
        if (match != null && match.IsRoundBased) {
            // Round 1 of the new match deploys everyone (BeginRound).
            match.MessageLocalized("event.start");
            return;
        }
        List<NetworkConnection> conns = new List<NetworkConnection>();
        foreach (KeyValuePair<int, NetworkObject> kv in avatars) {
            if (kv.Value != null && kv.Value.IsSpawned) {
                conns.Add(kv.Value.Owner);
                net.ServerManager.Despawn(kv.Value);
            }
        }
        avatars.Clear();
        foreach (NetworkConnection c in conns) SpawnAvatar(c);
        if (match != null) {
            match.MessageLocalized("event.start");
        }
    }

    void Update() {
        GameLiftHost.Pump();
        WarmUp();
        if (!GameLiftHost.Active || !sessionActive || players.Count > 0) return;
        float limit = hadPlayers ? EmptyTimeout : JoinTimeout;
        if (Time.time - idleSince > limit) {
            Debug.Log("[GameServer] Session idle for " + limit + "s, ending process.");
            Shutdown();
        }
    }

    /// <summary>
    /// Elimination waiting for players: anyone without a living avatar (dead in a round that could not be followed
    /// by another, or joined mid-round) deploys, as in the other modes.
    /// </summary>
    private void WarmUp() {
        if (match == null || !match.IsRoundBased || match.Phase != RoundPhase.Waiting) return;
        foreach (NetPlayer p in players.Values) {
            if (p.Alive.Value || p.Owner == null || !p.Owner.IsActive || respawning.Contains(p.Owner.ClientId)) continue;
            if (avatars.TryGetValue(p.Owner.ClientId, out NetworkObject old)) {
                avatars.Remove(p.Owner.ClientId);
                if (old != null && old.IsSpawned) net.ServerManager.Despawn(old);
            }
            Debug.Log("[GameServer] Warm-up: deploying client " + p.Owner.ClientId);
            SpawnAvatar(p.Owner, false);
        }
    }

    private void Shutdown() {
        sessionActive = false;
        if (net != null && net.ServerManager.Started) net.ServerManager.StopConnection(true);
        GameLiftHost.EndProcess();
        Application.Quit(0);
    }

    void OnDestroy() {
        PlayerHealth.ServerDied -= OnAvatarDied;
    }

}
