using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Main menu: callsign, clickable room list, quick join, create room (with its mode and map), connection state.
/// </summary>
public class LobbyScreen : MonoBehaviour {

    public struct RoomEntry {
        /// <summary>Directory id (GameLift game session id); falls back to the name when absent.</summary>
        public string Id;
        public string Name;
        public int Players;
        public int MaxPlayers;
        public bool IsOpen;
        public string Map;

        public string Key => string.IsNullOrEmpty(Id) ? Name : Id;
    }

    public enum NetState { Connecting, Online, Offline, Busy }

    /// <summary>Fired with the room's <see cref="RoomEntry.Key"/>.</summary>
    public event Action<string> JoinRequested;
    public event Action QuickJoinRequested;
    public event Action<string> CreateRequested;
    public event Action ReconnectRequested;

    /// <summary>Mode and map a new room is created with (lobby keys, e.g. "elim" / "outpost").</summary>
    public string NewRoomMode => Modes.Key(Modes.All[modeIndex]);
    public string NewRoomMap => MapChoices[mapIndex];

    private static readonly string[] MapChoices = { MapLoader.SceneMapId, "outpost" };
    private int modeIndex, mapIndex;
    private TextMeshProUGUI modeText, mapText;

    public string Callsign {
        get => callsign.text.Trim();
        set => callsign.text = value;
    }

    private TMP_InputField callsign, newRoom;
    private TextMeshProUGUI statusText, regionText, notice, countText, emptyText;
    private Image statusDot;
    private Button joinButton, quickButton, createButton, reconnectButton;
    private RectTransform listContent;
    private readonly List<RoomRow> rows = new List<RoomRow>();
    private string selected;
    private bool online;

    public static LobbyScreen Create(Transform parent) {
        RectTransform rt = UIBuilder.Fill(UIBuilder.Rect("Lobby", parent));
        LobbyScreen s = rt.gameObject.AddComponent<LobbyScreen>();
        s.Build(rt);
        return s;
    }

    public void SetVisible(bool v) {
        gameObject.SetActive(v);
    }

    private void Build(RectTransform root) {
        // Left-heavy dark gradient so text stays readable over the live scene camera.
        Image shade = UIBuilder.Image(root, "Shade", Color.white, true);
        shade.sprite = GradientSprite(new Color(0.03f, 0.04f, 0.03f, 0.94f), new Color(0.03f, 0.04f, 0.03f, 0.5f));
        UIBuilder.Fill(shade.rectTransform);

        // Logo
        TextMeshProUGUI logo = UIBuilder.Text(root, "Logo", "SLOP FPS", UITheme.CondBold, 88, UITheme.Ink);
        UIBuilder.Place(logo.rectTransform, new Vector2(0, 1), new Vector2(96, -64), new Vector2(900, 90));
        logo.characterSpacing = 3;
        TextMeshProUGUI sub = UIBuilder.Text(root, "Sub", "TACTICAL DEATHMATCH", UITheme.CondMedium, 24, UITheme.Amber);
        UIBuilder.Place(sub.rectTransform, new Vector2(0, 1), new Vector2(100, -160), new Vector2(900, 30));
        sub.characterSpacing = 34;

        // Connection status, top right
        RectTransform net = UIBuilder.Rect("Net", root);
        UIBuilder.Place(net, new Vector2(1, 1), new Vector2(-96, -76), new Vector2(760, 40));
        HorizontalLayoutGroup nh = UIBuilder.HLayout(net.gameObject, 14);
        nh.childAlignment = TextAnchor.MiddleRight;
        nh.childForceExpandHeight = false;
        nh.childControlHeight = false;
        statusDot = UIBuilder.Image(net, "Dot", UITheme.Amber);
        UIBuilder.Size(statusDot, 14, 14);
        statusDot.rectTransform.sizeDelta = new Vector2(14, 14);
        statusText = UIBuilder.Text(net, "Status", "서버 연결 중", UITheme.BodyBold, 22, UITheme.Ink);
        statusText.rectTransform.sizeDelta = new Vector2(0, 40);
        UIBuilder.Size(statusText).flexibleWidth = 0;
        statusText.gameObject.AddComponent<ContentSizeFitter>().horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        regionText = UIBuilder.Text(net, "Region", "", UITheme.CondMedium, 22, UITheme.Mute);
        regionText.characterSpacing = 6;
        regionText.rectTransform.sizeDelta = new Vector2(0, 40);
        regionText.gameObject.AddComponent<ContentSizeFitter>().horizontalFit = ContentSizeFitter.FitMode.PreferredSize;

        // Left column
        RectTransform side = UIBuilder.Rect("Side", root);
        side.anchorMin = new Vector2(0, 0);
        side.anchorMax = new Vector2(0, 1);
        side.pivot = new Vector2(0, 1);
        side.offsetMin = new Vector2(96, 72);
        side.offsetMax = new Vector2(96 + 500, -236);
        UIBuilder.VLayout(side.gameObject, 8);

        UIBuilder.Size(UIBuilder.Label(side, "콜사인"), height: 24);
        callsign = UIBuilder.Input(side, "이름을 입력하세요", 16);
        UIBuilder.Size(callsign, height: 64);
        Spacer(side, 10);
        joinButton = UIBuilder.Button(side, "선택한 방 참가", "ENTER", true, () => TryJoin(selected));
        quickButton = UIBuilder.Button(side, "빠른 참가", "자동 매칭", false, () => {
            if (ValidateCallsign()) QuickJoinRequested?.Invoke();
        });
        LoadoutPicker.Create(side, false);
        Spacer(side, 4);
        UIBuilder.Size(UIBuilder.Label(side, "새 작전 이름"), height: 24);
        newRoom = UIBuilder.Input(side, "비워두면 자동 생성", 24, 24);
        UIBuilder.Size(newRoom, height: 58);
        // Mode and map of the new room: click to cycle through them.
        RectTransform pick = UIBuilder.Rect("Pick", side);
        UIBuilder.Size(pick, height: 48);
        UIBuilder.HLayout(pick.gameObject, 12);
        modeText = Picker(pick, "모드 변경", () => {
            modeIndex = (modeIndex + 1) % Modes.All.Length;
            ShowPicks();
        });
        mapText = Picker(pick, "맵 변경", () => {
            mapIndex = (mapIndex + 1) % MapChoices.Length;
            ShowPicks();
        });
        ShowPicks();
        createButton = UIBuilder.Button(side, "방 만들기", "2명부터 시작 · 최대 8명", false, () => {
            if (ValidateCallsign()) CreateRequested?.Invoke(newRoom.text.Trim());
        });
        Spacer(side, 8);
        notice = UIBuilder.Text(side, "Notice", "", UITheme.Body, 20, UITheme.Red);
        notice.enableWordWrapping = true;
        UIBuilder.Size(notice, height: 60);
        reconnectButton = UIBuilder.Button(side, "다시 연결", "", false, () => ReconnectRequested?.Invoke(), 56);
        reconnectButton.gameObject.SetActive(false);

        // Room list panel
        Image panel = UIBuilder.Panel(root, "Rooms");
        RectTransform prt = panel.rectTransform;
        prt.anchorMin = new Vector2(0, 0);
        prt.anchorMax = new Vector2(1, 1);
        prt.offsetMin = new Vector2(96 + 500 + 48, 130);
        prt.offsetMax = new Vector2(-96, -260);
        UIBuilder.Brackets(prt, 22, 3);

        RectTransform head = UIBuilder.Rect("Head", prt);
        head.anchorMin = new Vector2(0, 1);
        head.anchorMax = new Vector2(1, 1);
        head.pivot = new Vector2(0.5f, 1);
        head.sizeDelta = new Vector2(0, 76);
        TextMeshProUGUI title = UIBuilder.Text(head, "Title", "작전 목록", UITheme.BodyBold, 30, UITheme.Ink,
                                               TextAlignmentOptions.MidlineLeft);
        UIBuilder.Fill(title.rectTransform, 30, 0, 30, 0);
        countText = UIBuilder.Text(head, "Count", "", UITheme.CondMedium, 20, UITheme.Mute,
                                   TextAlignmentOptions.MidlineRight);
        countText.characterSpacing = 8;
        UIBuilder.Fill(countText.rectTransform, 30, 0, 30, 0);
        Divider(prt, -76);

        RectTransform cols = UIBuilder.Rect("Columns", prt);
        cols.anchorMin = new Vector2(0, 1);
        cols.anchorMax = new Vector2(1, 1);
        cols.pivot = new Vector2(0.5f, 1);
        cols.anchoredPosition = new Vector2(0, -77);
        cols.sizeDelta = new Vector2(0, 46);
        HorizontalLayoutGroup ch = UIBuilder.HLayout(cols.gameObject, 0, new RectOffset(30, 30, 0, 0));
        ch.childForceExpandWidth = false;
        ColumnLabel(cols, "방 이름", -1, 1);
        ColumnLabel(cols, "맵 · 모드", 300, 0);
        ColumnLabel(cols, "인원", 200, 0);
        ColumnLabel(cols, "", 120, 0);
        Divider(prt, -123);

        // Scrollable rows
        RectTransform viewport = UIBuilder.Fill(UIBuilder.Rect("Viewport", prt), 0, 0, 0, 124);
        viewport.gameObject.AddComponent<RectMask2D>();
        UIBuilder.Fill(UIBuilder.Image(viewport, "Hit", new Color(0, 0, 0, 0), true).rectTransform);
        listContent = UIBuilder.Rect("Content", viewport);
        listContent.anchorMin = new Vector2(0, 1);
        listContent.anchorMax = new Vector2(1, 1);
        listContent.pivot = new Vector2(0.5f, 1);
        listContent.sizeDelta = Vector2.zero;
        UIBuilder.VLayout(listContent.gameObject, 0);
        listContent.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        ScrollRect sr = viewport.gameObject.AddComponent<ScrollRect>();
        sr.content = listContent;
        sr.viewport = viewport;
        sr.horizontal = false;
        sr.movementType = ScrollRect.MovementType.Clamped;
        sr.scrollSensitivity = 30;

        emptyText = UIBuilder.Text(viewport, "Empty", "열린 작전이 없습니다.\n왼쪽에서 방을 만들거나 빠른 참가를 눌러보세요.",
                                   UITheme.Body, 22, UITheme.Mute, TextAlignmentOptions.Center);
        UIBuilder.Fill(emptyText.rectTransform, 0, 0, 0, 0);
        emptyText.enableWordWrapping = true;

        Button language = UIBuilder.SmallButton(root, "", L10n.Cycle);
        UIBuilder.Place((RectTransform)language.transform, new Vector2(1, 1), new Vector2(-96, -48), new Vector2(300, 38));
        language.gameObject.AddComponent<LanguageButton>();

        // Footer
        // A browser tab can't quit itself, so WebGL only advertises joining.
        TextMeshProUGUI foot = UIBuilder.Text(root, "Foot",
            Application.platform == RuntimePlatform.WebGLPlayer
                ? L10n.T("<color=#ECEAE3>ENTER</color>  참가")
                : L10n.T("<color=#ECEAE3>ENTER</color>  참가      <color=#ECEAE3>ESC</color>  종료"),
            UITheme.Body, 18, UITheme.Dim);
        UIBuilder.Place(foot.rectTransform, new Vector2(0, 0), new Vector2(96, 56), new Vector2(800, 30));
        TextMeshProUGUI ver = UIBuilder.Text(root, "Version", "v0.3.0  ·  SEOUL", UITheme.CondMedium, 18, UITheme.Dim,
                                             TextAlignmentOptions.Right);
        ver.characterSpacing = 6;
        UIBuilder.Place(ver.rectTransform, new Vector2(1, 0), new Vector2(-96, 56), new Vector2(400, 30));

        SetRooms(new List<RoomEntry>());
        SetStatus(NetState.Connecting, null);
    }

    void Update() {
        ShowPicks();
        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) {
            if (online) {
                if (selected != null) TryJoin(selected);
                else if (ValidateCallsign()) QuickJoinRequested?.Invoke();
            }
        }
        if (Input.GetKeyDown(KeyCode.Escape) && !Application.isEditor && Application.platform != RuntimePlatform.WebGLPlayer) {
            Application.Quit();
        }
    }

    public void SetStatus(NetState state, string detail) {
        online = state == NetState.Online;
        switch (state) {
            case NetState.Connecting:
                statusDot.color = UITheme.Amber;
                statusText.text = L10n.T("서버 연결 중");
                break;
            case NetState.Online:
                statusDot.color = UITheme.Olive;
                statusText.text = L10n.T("연결됨");
                break;
            case NetState.Busy:
                statusDot.color = UITheme.Amber;
                statusText.text = L10n.T("작전 합류 중");
                break;
            default:
                statusDot.color = UITheme.Red;
                statusText.text = L10n.T("연결 끊김");
                break;
        }
        regionText.text = detail ?? "";
        joinButton.interactable = online && selected != null;
        quickButton.interactable = online;
        createButton.interactable = online;
        reconnectButton.gameObject.SetActive(state == NetState.Offline);
        if (state != NetState.Offline) {
            ShowNotice(null);
        }
    }

    public int RoomCount => rows.Count;

    public void ShowNotice(string message) {
        notice.text = message ?? "";
    }

    public void SetRooms(IList<RoomEntry> rooms) {
        foreach (RoomRow r in rows) Destroy(r.gameObject);
        rows.Clear();
        bool selectedStillThere = false;
        int players = 0;
        foreach (RoomEntry e in rooms) {
            RoomRow row = RoomRow.Create(listContent, e, OnRowClicked, TryJoin);
            rows.Add(row);
            players += e.Players;
            if (e.Key == selected) selectedStillThere = true;
        }
        if (!selectedStillThere) selected = null;
        foreach (RoomRow r in rows) r.SetSelected(r.Entry.Key == selected);
        emptyText.gameObject.SetActive(rooms.Count == 0);
        countText.text = L10n.Format("room.count", rooms.Count, players);
        joinButton.interactable = online && selected != null;
    }

    private void OnRowClicked(RoomEntry e) {
        selected = e.Key;
        foreach (RoomRow r in rows) r.SetSelected(r.Entry.Key == selected);
        joinButton.interactable = online;
    }

    private void TryJoin(string room) {
        if (string.IsNullOrEmpty(room) || !ValidateCallsign()) return;
        JoinRequested?.Invoke(room);
    }

    private bool ValidateCallsign() {
        if (Callsign.Length == 0) {
            ShowNotice(L10n.T("콜사인을 먼저 입력하세요."));
            callsign.Select();
            return false;
        }
        ShowNotice(null);
        return true;
    }

    private static TextMeshProUGUI Picker(Transform parent, string hint, UnityEngine.Events.UnityAction onClick) {
        Button b = UIBuilder.Button(parent, "", hint, false, onClick, 48);
        UIBuilder.Size(b, flex: 1);     // the row splits evenly between the two
        TextMeshProUGUI t = b.transform.Find("Text").GetComponent<TextMeshProUGUI>();
        t.font = UITheme.BodyBold;
        t.fontSize = 22;
        t.characterSpacing = 0;
        t.enableAutoSizing = true;
        t.fontSizeMin = 14;
        t.fontSizeMax = 22;
        t.rectTransform.anchorMin = new Vector2(0, 0.4f);
        t.rectTransform.anchorMax = Vector2.one;
        t.rectTransform.offsetMin = new Vector2(16, 0);
        t.rectTransform.offsetMax = new Vector2(-16, -2);
        TextMeshProUGUI help = b.transform.Find("Hint").GetComponent<TextMeshProUGUI>();
        help.fontSize = 14;
        help.characterSpacing = 2;
        help.alignment = TextAlignmentOptions.MidlineLeft;
        help.rectTransform.anchorMin = Vector2.zero;
        help.rectTransform.anchorMax = new Vector2(1, 0.4f);
        help.rectTransform.offsetMin = new Vector2(16, 2);
        help.rectTransform.offsetMax = new Vector2(-16, 0);
        return t;
    }

    private void ShowPicks() {
        modeText.text = Modes.DisplayName(Modes.All[modeIndex]);
        mapText.text = MapLoader.DisplayName(MapChoices[mapIndex]);
    }

    private static void Spacer(Transform parent, float h) {
        UIBuilder.Size(UIBuilder.Rect("Spacer", parent), height: h);
    }

    private static void Divider(RectTransform parent, float y) {
        Image d = UIBuilder.Image(parent, "Divider", UITheme.Line);
        d.rectTransform.anchorMin = new Vector2(0, 1);
        d.rectTransform.anchorMax = new Vector2(1, 1);
        d.rectTransform.pivot = new Vector2(0.5f, 1);
        d.rectTransform.anchoredPosition = new Vector2(0, y);
        d.rectTransform.sizeDelta = new Vector2(0, 1);
    }

    private static void ColumnLabel(Transform parent, string text, float width, float flex) {
        TextMeshProUGUI l = UIBuilder.Label(parent, text, 16);
        l.alignment = TextAlignmentOptions.MidlineLeft;
        l.color = UITheme.Dim;
        LayoutElement le = UIBuilder.Size(l, width);
        if (flex > 0) le.flexibleWidth = flex;
    }

    public static Sprite GradientSprite(Color left, Color right) {
        Texture2D tex = new Texture2D(256, 1, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        for (int x = 0; x < 256; x++) {
            float t = x / 255f;
            // Ease so the dark part holds over the left column, then falls off.
            tex.SetPixel(x, 0, Color.Lerp(left, right, Mathf.SmoothStep(0.15f, 1f, t)));
        }
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, 256, 1), new Vector2(0.5f, 0.5f));
    }

    /// <summary>One clickable line of the room list.</summary>
    private class RoomRow : MonoBehaviour {

        public RoomEntry Entry;
        private Image bg, accent;

        public static RoomRow Create(Transform parent, RoomEntry e, Action<RoomEntry> onClick, Action<string> onJoin) {
            Image bg = UIBuilder.Image(parent, "Row " + e.Name, new Color(0, 0, 0, 0), true);
            UIBuilder.Size(bg, height: 62);
            RoomRow row = bg.gameObject.AddComponent<RoomRow>();
            row.Entry = e;
            row.bg = bg;
            Button click = bg.gameObject.AddComponent<Button>();
            click.transition = Selectable.Transition.None;
            click.onClick.AddListener(() => onClick(e));

            row.accent = UIBuilder.Image(bg.transform, "Accent", UITheme.Amber);
            UIBuilder.Place(row.accent.rectTransform, new Vector2(0, 0.5f), Vector2.zero, new Vector2(5, 62));
            row.accent.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;

            HorizontalLayoutGroup h = UIBuilder.HLayout(bg.gameObject, 0, new RectOffset(30, 30, 0, 0));
            h.childForceExpandWidth = false;
            h.childAlignment = TextAnchor.MiddleLeft;

            TextMeshProUGUI name = UIBuilder.Text(bg.transform, "Name", e.Name, UITheme.BodyBold, 24, UITheme.Ink,
                                                  TextAlignmentOptions.MidlineLeft, localize: false);
            name.overflowMode = TextOverflowModes.Ellipsis;
            UIBuilder.Size(name, 0, flex: 1);
            TextMeshProUGUI map = UIBuilder.Text(bg.transform, "Map", e.Map, UITheme.Body, 22, UITheme.Mute,
                                                 TextAlignmentOptions.MidlineLeft);
            UIBuilder.Size(map, 300);
            map.enableAutoSizing = true;
            map.fontSizeMin = 14;
            map.fontSizeMax = 22;
            map.overflowMode = TextOverflowModes.Ellipsis;

            RectTransform cap = UIBuilder.Rect("Capacity", bg.transform);
            UIBuilder.Size(cap, 200);
            HorizontalLayoutGroup ch = UIBuilder.HLayout(cap.gameObject, 4);
            ch.childAlignment = TextAnchor.MiddleLeft;
            ch.childControlHeight = false;
            ch.childForceExpandHeight = false;
            bool full = e.Players >= e.MaxPlayers;
            for (int i = 0; i < e.MaxPlayers; i++) {
                Image pip = UIBuilder.Image(cap, "Pip", i < e.Players ? UITheme.Ink : UITheme.Dim);
                UIBuilder.Size(pip, 9, 20);
                pip.rectTransform.sizeDelta = new Vector2(9, 20);
            }
            TextMeshProUGUI count = UIBuilder.Text(cap, "Count", full ? "FULL" : e.Players + "/" + e.MaxPlayers,
                                                   UITheme.CondBold, 22, full ? UITheme.Red : UITheme.Ink);
            count.characterSpacing = full ? 8 : 2;
            count.rectTransform.sizeDelta = new Vector2(80, 30);
            UIBuilder.Size(count, 80, 30).minWidth = 80;
            count.margin = new Vector4(10, 0, 0, 0);

            RectTransform btnCell = UIBuilder.Rect("JoinCell", bg.transform);
            UIBuilder.Size(btnCell, 120);
            Button join = UIBuilder.SmallButton(btnCell, "참가", () => onJoin(e.Name));
            UIBuilder.Place((RectTransform)join.transform, new Vector2(1, 0.5f), Vector2.zero, new Vector2(100, 38));
            join.interactable = e.IsOpen && !full;
            if (!join.interactable) {
                join.GetComponentInChildren<TextMeshProUGUI>().color = UITheme.Dim;
                join.GetComponent<UIBorder>().Color = UITheme.Line;
            }

            Image line = UIBuilder.Image(bg.transform, "Line", new Color(0.925f, 0.918f, 0.89f, 0.06f));
            line.rectTransform.anchorMin = Vector2.zero;
            line.rectTransform.anchorMax = new Vector2(1, 0);
            line.rectTransform.pivot = new Vector2(0.5f, 0);
            line.rectTransform.sizeDelta = new Vector2(0, 1);
            line.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            return row;
        }

        public void SetSelected(bool on) {
            bg.color = on ? UITheme.AmberWash : new Color(0, 0, 0, 0);
            accent.enabled = on;
        }

    }

}
