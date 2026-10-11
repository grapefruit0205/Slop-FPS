using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using SlopFPS.Input;

/// <summary>
/// Root of the runtime-built UI. Owns every screen, decides which are visible,
/// and tells the first-person scripts when a menu has taken the mouse.
/// </summary>
public class GameUI : MonoBehaviour {

    public static GameUI Instance { get; private set; }

    public LobbyScreen Lobby { get; private set; }
    public HudScreen Hud { get; private set; }
    public ScoreboardScreen Scoreboard { get; private set; }
    public DeathScreen Death { get; private set; }
    public PauseMenu Pause { get; private set; }
    public ControlsHint Hint { get; private set; }

    public bool InGame { get; private set; }

    /// <summary>Set by dev screenshot mode to keep the scoreboard up regardless of Tab.</summary>
    public bool ForceScoreboard { get; set; }

    /// <summary>Set by dev screenshot mode so stray key presses don't change screens.</summary>
    public bool IgnoreKeys { get; set; }

    public static GameUI Create() {
        GameSettings.Load();
        GraphicsQuality.Load();
        GameObject go = new GameObject("GameUI", typeof(RectTransform));
        go.layer = LayerMask.NameToLayer("UI");
        Canvas canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;
        CanvasScaler scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;
        go.AddComponent<GraphicRaycaster>();

        if (FindObjectOfType<EventSystem>() == null) {
            GameObject es = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            es.transform.SetParent(go.transform, false);
        }

        GameUI ui = go.AddComponent<GameUI>();
        ui.Build();
        return ui;
    }

    private void Build() {
        Instance = this;
        Transform root = transform;
        // Sibling order is draw order: later screens draw on top.
        Hud = HudScreen.Create(root);
        MatchHud.Create(Hud.transform);
        Hint = ControlsHint.Create(Hud.transform);
        Death = DeathScreen.Create(root);
        Scoreboard = ScoreboardScreen.Create(root);
        Lobby = LobbyScreen.Create(root);
        Pause = PauseMenu.Create(root);
        FpsCounter.Create(root);
        ShowLobby();
    }

    public void ShowLobby() {
        InGame = false;
        Hud.SetVisible(false);
        Death.Hide();
        Scoreboard.SetVisible(false);
        Pause.SetVisible(false);
        Lobby.SetVisible(true);
    }

    public void EnterGame() {
        InGame = true;
        Lobby.SetVisible(false);
        Hud.ResetState();
        Hint.ResetForMatch();
    }

    void Update() {
        if (InGame) {
            if (!IgnoreKeys && Input.GetKeyDown(KeyCode.Escape)) {
                Pause.SetVisible(!Pause.Visible);
            }
            bool matchOver = MatchManager.Instance != null && MatchManager.Instance.IsOver;
            bool showBoard = ForceScoreboard || matchOver || (!IgnoreKeys && !Pause.Visible && Input.GetKey(KeyCode.Tab));
            if (showBoard != Scoreboard.Visible) {
                Scoreboard.SetVisible(showBoard);
            }
            Scoreboard.SetMatchOver(matchOver);
        }

        bool wantsCursor = !InGame || Pause.Visible || Death.Visible;
        bool blockGameplay = wantsCursor || (MatchManager.Instance != null && MatchManager.Instance.IsOver);
        FpsInputGate.Blocked = blockGameplay;
        if (wantsCursor) {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }

    void OnDestroy() {
        if (Instance == this) {
            Instance = null;
            FpsInputGate.Blocked = false;
        }
    }

}
