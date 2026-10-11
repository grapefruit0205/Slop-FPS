using System.Collections;
using System.Collections.Generic;
using FishNet;
using FishNet.Managing;
using FishNet.Transporting;
using FishNet.Transporting.Bayou;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Scene entry point. On a dedicated server it hands over to <see cref="GameServer"/>; on a client it runs the lobby
/// (room list from the lobby API), connects to the chosen game server and drives the in-game UI states.
/// </summary>
public class GameNetwork : MonoBehaviour {

    public const string MapName = "Warehouse";
    private const float RoomRefreshInterval = 5f;
    private const string CallsignPrefKey = "PlayerName";

    // Legacy scene UI. The game now builds its own UI (GameUI); these references are
    // kept so the scene still deserializes, and are only used to hide the old widgets.
    [SerializeField]
    private Text connectionText;
    [SerializeField]
    private Transform[] spawnPoints;
    [SerializeField]
    private Camera sceneCamera;
    [SerializeField]
    private GameObject[] playerModel;
    [SerializeField]
    private GameObject serverWindow;
    [SerializeField]
    private GameObject messageWindow;
    [SerializeField]
    private GameObject sightImage;
    [SerializeField]
    private InputField username;
    [SerializeField]
    private InputField roomName;
    [SerializeField]
    private InputField roomList;
    [SerializeField]
    private InputField messagesLog;

    public static GameNetwork Instance { get; private set; }

    public Transform[] SpawnPoints => spawnPoints;
    public Camera SceneCamera => sceneCamera;
    public GameObject[] PlayerModels => playerModel;
    public string CurrentRoomName { get; private set; } = "";
    public GameUI UI => ui;

    private NetworkManager net;
    private GameUI ui;
    private RoomDirectory directory;
    private bool busy;
    private bool inGame;
    private float refreshTimer;

    private static bool quitting;

    void Awake() {
        Instance = this;
        Application.quitting += () => quitting = true;
    }

    void Start() {
        HideLegacyUI();
        net = InstanceFinder.NetworkManager;
        if (net == null) {
            Debug.LogError("[GameNetwork] No FishNet NetworkManager in the scene.");
            return;
        }
        if (NetConfig.IsServer) {
            sceneCamera.enabled = false;
            GameServer server = net.GetComponent<GameServer>();
            server.Run(this, net);
            return;
        }

        AntiCheat.Register(net);
        ui = GameUI.Create();
        directory = gameObject.AddComponent<RoomDirectory>();
        ui.Lobby.Callsign = PlayerPrefs.GetString(CallsignPrefKey, "");
        ui.Lobby.JoinRequested += JoinRoom;
        ui.Lobby.QuickJoinRequested += QuickJoin;
        ui.Lobby.CreateRequested += CreateRoom;
        ui.Lobby.ReconnectRequested += RefreshNow;
        ui.Pause.LeaveRequested += LeaveRoom;
        L10n.Changed += OnLanguageChanged;
        net.ClientManager.OnClientConnectionState += OnClientConnectionState;
        net.ClientManager.OnAuthenticated += OnAuthenticated;

        if (DevBot.Enabled) gameObject.AddComponent<DevBot>();
        if (NetConfig.DebugInput) gameObject.AddComponent<InputProbe>();
        if (PerfProbe.Enabled) gameObject.AddComponent<PerfProbe>();
        if (DevShots.Enabled) gameObject.AddComponent<DevShots>().Begin(this, ui);
        // The UI tour hosts its own server; everything else finds one through the lobby.
        if (!DevShots.Enabled || DevShots.UsesLobby) RefreshNow();
    }

    private void HideLegacyUI() {
        GameObject screen = GameObject.FindGameObjectWithTag("Screen");
        if (screen != null) screen.SetActive(false);
        foreach (GameObject go in new[] { serverWindow, messageWindow, sightImage }) {
            if (go != null) go.SetActive(false);
        }
        if (connectionText != null) connectionText.gameObject.SetActive(false);
    }

    void Update() {
        if (ui == null || inGame || busy || directory == null || directory.IsLocal) return;
        refreshTimer += Time.unscaledDeltaTime;
        if (refreshTimer >= RoomRefreshInterval) RefreshNow();
    }

    // ------------------------------------------------------------------ lobby

    private void RefreshNow() {
        refreshTimer = 0f;
        if (ui.Lobby.RoomCount == 0) ui.Lobby.SetStatus(LobbyScreen.NetState.Connecting, L10n.T("작전 목록을 불러오는 중…"));
        StartCoroutine(directory.List(OnRooms, error => {
            ui.Lobby.SetStatus(LobbyScreen.NetState.Offline, error);
        }));
    }

    private void OnLanguageChanged() {
        if (!inGame && !busy) RefreshNow();
    }

    private void OnRooms(List<LobbyScreen.RoomEntry> rooms) {
        if (inGame) return;
        int players = 0;
        foreach (LobbyScreen.RoomEntry r in rooms) players += r.Players;
        string detail = directory.IsLocal
            ? L10n.T("로컬 개발 서버  <color=#ECEAE3>") + NetConfig.DevHost + ":" + NetConfig.DevPort + "</color>"
            : L10n.T("REGION <color=#ECEAE3>SEOUL</color>    작전 <color=#ECEAE3>") + rooms.Count + L10n.T("</color>    접속 <color=#ECEAE3>") + players + "</color>";
        if (!busy) ui.Lobby.SetStatus(LobbyScreen.NetState.Online, detail);
        ui.Lobby.SetRooms(rooms);
        if (NetConfig.AutoJoin && !autoJoined && rooms.Count > 0) {
            autoJoined = true;
            if (!string.IsNullOrEmpty(NetConfig.PresetName)) ui.Lobby.Callsign = NetConfig.PresetName;
            if (string.IsNullOrEmpty(ui.Lobby.Callsign)) ui.Lobby.Callsign = "Test" + Random.Range(100, 1000);
            JoinRoom(rooms[0].Key);
        }
    }

    private bool autoJoined;

    private void JoinRoom(string roomId) {
        if (!BeginRequest(L10n.T("작전에 참가하는 중…"))) return;
        StartCoroutine(directory.Join(roomId, ui.Lobby.Callsign, Connect, RequestFailed));
    }

    private void QuickJoin() {
        if (!BeginRequest(L10n.T("빈 자리를 찾는 중…"))) return;
        StartCoroutine(directory.QuickJoin(ui.Lobby.Callsign, Connect, RequestFailed));
    }

    private void CreateRoom(string name) {
        if (!BeginRequest(L10n.T("새 작전을 여는 중…"))) return;
        StartCoroutine(directory.Create(name, ui.Lobby.Callsign, ui.Lobby.NewRoomMode, ui.Lobby.NewRoomMap, Connect, RequestFailed));
    }

    private bool BeginRequest(string status) {
        if (busy || inGame) return false;
        busy = true;
        PlayerPrefs.SetString(CallsignPrefKey, ui.Lobby.Callsign);
        ui.Lobby.ShowNotice(null);
        ui.Lobby.SetStatus(LobbyScreen.NetState.Busy, status);
        return true;
    }

    private void RequestFailed(string message) {
        busy = false;
        ui.Lobby.ShowNotice(message);
        RefreshNow();
    }

    // ------------------------------------------------------------------ connection

    public void Connect(ConnectInfo info) {
        busy = true;
        CurrentRoomName = info.RoomName ?? "";
        FpsAuthenticator.PendingName = ui.Lobby.Callsign;
        FpsAuthenticator.PendingPlayerSessionId = info.PlayerSessionId ?? "";
        Bayou bayou = net.TransportManager.GetTransport<Bayou>();
        bayou.SetUseWSS(info.Secure);
        bayou.SetClientAddress(info.Host);
        bayou.SetPort(info.Port);
        ui.Lobby.SetStatus(LobbyScreen.NetState.Busy, L10n.T("서버에 접속하는 중…"));
        if (!net.ClientManager.StartConnection()) RequestFailed(L10n.T("서버에 접속하지 못했습니다."));
    }

    private void OnAuthenticated() {
        Debug.Log("[GameNetwork] Joined " + CurrentRoomName + " as " + ui.Lobby.Callsign);
        busy = false;
        inGame = true;
        ui.EnterGame();
        ui.Hud.SetCallsign(ui.Lobby.Callsign);
        ui.Hud.SetVisible(true);
        ui.Hud.SetAlive(false);
    }

    private void OnClientConnectionState(ClientConnectionStateArgs args) {
        // The transport also reports "stopped" while the app is shutting down and the UI is already gone.
        if (args.ConnectionState != LocalConnectionState.Stopped || quitting || this == null || ui == null) return;
        if (inGame) {
            ReturnToLobby(leaving ? null : L10n.T(AntiCheat.LastKick ?? "서버와의 연결이 끊어졌습니다."));
        } else if (busy) {
            RequestFailed(FpsAuthenticator.LastRejection ?? L10n.T("서버에 접속하지 못했습니다. 잠시 후 다시 시도해 주세요."));
        }
        leaving = false;
    }

    private bool leaving;

    public void LeaveRoom() {
        leaving = true;
        net.ClientManager.StopConnection();
    }

    private void ReturnToLobby(string notice) {
        inGame = false;
        busy = false;
        CurrentRoomName = "";
        if (sceneCamera != null) sceneCamera.enabled = true;
        ui.ShowLobby();
        ui.Lobby.ShowNotice(notice);
        RefreshNow();
    }

    void OnDestroy() {
        L10n.Changed -= OnLanguageChanged;
        if (net == null || ui == null) return;
        net.ClientManager.OnClientConnectionState -= OnClientConnectionState;
        net.ClientManager.OnAuthenticated -= OnAuthenticated;
    }

    // ------------------------------------------------------------------ local avatar lifecycle

    public void OnLocalAvatarSpawned(PlayerNetworkMover avatar) {
        sceneCamera.enabled = false;
        if (ui == null) return;
        ui.Death.Hide();
        ui.Hud.SetVisible(true);
        ui.Hud.SetAlive(true);
    }

    public void OnLocalDeath() {
        if (ui != null) ui.Hud.SetAlive(false);
    }

    public void OnLocalAvatarDespawned() {
        // Between the old body disappearing and the respawn, look through the scene camera.
        if (sceneCamera != null) sceneCamera.enabled = true;
    }

}
