using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Tab scoreboard; also the results screen while a round is over. In team deathmatch the players are listed per side
/// (the local player's side first, in blue) under a header with the team's score.
/// </summary>
public class ScoreboardScreen : MonoBehaviour {

    public struct Row {
        public string Name;
        public int Kills, Deaths, Score, Ping;
        public bool IsMe, IsDead;
        public Team Team;
    }

    private static readonly float[] ColumnWidths = { 70, -1, 110, 110, 130, 150, 110 };
    private static readonly string[] Columns = { "#", "콜사인", "K", "D", "K/D", "점수", "핑" };

    public bool Visible => gameObject.activeSelf;

    /// <summary>Dev screenshot mode supplies its own rows instead of the room's players.</summary>
    public List<Row> OverrideRows { get; set; }

    private TextMeshProUGUI title, subtitle, banner;
    private RectTransform body;
    private readonly List<GameObject> rowObjects = new List<GameObject>();
    private float refreshTimer;
    private bool matchOver;

    public static ScoreboardScreen Create(Transform parent) {
        RectTransform rt = UIBuilder.Fill(UIBuilder.Rect("Scoreboard", parent));
        ScoreboardScreen s = rt.gameObject.AddComponent<ScoreboardScreen>();
        s.Build(rt);
        rt.gameObject.SetActive(false);
        return s;
    }

    public void SetVisible(bool v) {
        gameObject.SetActive(v);
        if (v) Refresh();
    }

    public void SetMatchOver(bool over) {
        if (over == matchOver) return;
        matchOver = over;
        banner.gameObject.SetActive(over);
    }

    private void Build(RectTransform root) {
        Image dim = UIBuilder.Image(root, "Dim", new Color(0.024f, 0.03f, 0.024f, 0.55f));
        UIBuilder.Fill(dim.rectTransform);

        banner = UIBuilder.Text(root, "Banner", "작전 종료", UITheme.BodyBold, 64, UITheme.Amber, TextAlignmentOptions.Center);
        UIBuilder.Place(banner.rectTransform, new Vector2(0.5f, 1), new Vector2(0, -90), new Vector2(1200, 80));
        banner.characterSpacing = 20;
        banner.gameObject.SetActive(false);

        Image panel = UIBuilder.Panel(root, "Board", UITheme.PanelSolid);
        UIBuilder.Place(panel.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1180, 0));
        panel.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        UIBuilder.VLayout(panel.gameObject, 0);
        panel.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        UIBuilder.Brackets(panel.rectTransform, 22, 3);

        RectTransform head = UIBuilder.Rect("Head", panel.transform);
        UIBuilder.Size(head, height: 86);
        title = UIBuilder.Text(head, "Title", "", UITheme.BodyBold, 36, UITheme.Ink, TextAlignmentOptions.MidlineLeft);
        UIBuilder.Fill(title.rectTransform, 36, 0, 36, 0);
        subtitle = UIBuilder.Label(head, "", 18, TextAlignmentOptions.MidlineRight);
        UIBuilder.Fill(subtitle.rectTransform, 36, 0, 36, 0);
        Line(panel.transform, UITheme.Line);

        RectTransform cols = MakeRow(panel.transform, 50, null);
        for (int i = 0; i < Columns.Length; i++) {
            TextMeshProUGUI l = UIBuilder.Label(cols, Columns[i], 16, i < 2 ? TextAlignmentOptions.MidlineLeft : TextAlignmentOptions.MidlineRight);
            l.color = UITheme.Dim;
            Cell(l, i);
        }
        Line(panel.transform, UITheme.Line);

        body = UIBuilder.Rect("Rows", panel.transform);
        UIBuilder.VLayout(body.gameObject, 0);
        body.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        TextMeshProUGUI hint = UIBuilder.Text(panel.transform, "Hint", "", UITheme.Body, 18, UITheme.Dim, TextAlignmentOptions.Center);
        UIBuilder.Size(hint, height: 54);
        hintText = hint;
    }

    private TextMeshProUGUI hintText;

    void Update() {
        refreshTimer -= Time.unscaledDeltaTime;
        if (refreshTimer <= 0f) {
            refreshTimer = 0.5f;
            Refresh();
        }
    }

    private void Refresh() {
        List<Row> rows = OverrideRows ?? CollectRows();
        rows = rows.OrderByDescending(r => r.Score).ThenByDescending(r => r.Kills).ThenBy(r => r.Deaths).ToList();

        foreach (GameObject g in rowObjects) Destroy(g);
        rowObjects.Clear();
        MatchManager mm = MatchManager.Instance;
        if (mm != null && mm.IsTeamMode && OverrideRows == null) {
            Team mine = NetPlayer.Local != null && NetPlayer.Local.Team != Team.None ? NetPlayer.Local.Team : Team.A;
            foreach (Team side in new[] { mine, Teams.Other(mine) }) {
                List<Row> members = rows.Where(r => r.Team == side).ToList();
                AddTeamHeader(side, side == mine && NetPlayer.Local != null, mm.ScoreOf(side), members.Count,
                              mm.IsRoundBased ? L10n.T("  ·  생존 ") + mm.AliveOf(side) : "");
                for (int i = 0; i < members.Count; i++) AddRow(i + 1, members[i]);
            }
        } else {
            for (int i = 0; i < rows.Count; i++) {
                AddRow(i + 1, rows[i]);
            }
        }

        string roomName = mm != null && !string.IsNullOrEmpty(mm.RoomName.Value) ? mm.RoomName.Value
                        : GameNetwork.Instance != null ? GameNetwork.Instance.CurrentRoomName : "";
        title.text = string.IsNullOrEmpty(roomName) ? "Alpha Squad" : roomName;
        float left = mm != null ? mm.TimeLeft : 0f;
        string clock = string.Format("{0:00}:{1:00}", Mathf.FloorToInt(left / 60f), Mathf.FloorToInt(left % 60f));
        int max = NetConfig.MaxPlayersPerRoom;
        string where = mm != null ? MapLoader.DisplayName(mm.MapId) + "  ·  " + Modes.DisplayName(mm.Mode) + " " + Modes.GoalText(mm.Mode, mm.ScoreLimit) +
                                            (mm.IsRoundBased && mm.Round > 0 ? L10n.T("  ·  라운드 ") + mm.Round : "")
                                  : MapLoader.DisplayName(MapLoader.CurrentId);
        subtitle.text = where + "  ·  " + rows.Count + " / " + max + "  ·  " + clock;
        if (matchOver && mm != null) {
            banner.text = mm.ResultHeadline();
            string detail = mm.ResultDetail();
            if (string.IsNullOrEmpty(detail) && rows.Count > 0) detail = "<color=#E3A72F>" + UITheme.Safe(rows[0].Name) + L10n.T("</color> 님이 이번 작전 1위입니다.");
            hintText.text = detail + "  " + Mathf.CeilToInt(mm.IntermissionLeft) + L10n.T("초 후 다음 작전이 시작됩니다.");
        } else {
            hintText.text = L10n.T("Tab 키를 떼면 닫힙니다");
        }
    }

    private static List<Row> CollectRows() {
        List<Row> rows = new List<Row>();
        foreach (NetPlayer p in NetPlayer.All) {
            rows.Add(new Row {
                Name = p.Callsign.Value,
                Kills = p.Kills.Value,
                Deaths = p.Deaths.Value,
                Score = p.Score.Value,
                Ping = p.Ping.Value,
                IsMe = p == NetPlayer.Local,
                IsDead = !p.Alive.Value,
                Team = p.Team,
            });
        }
        return rows;
    }

    /// <summary>"알파 팀  ·  4명 ......... 32" in the side's colour (blue for the local player's own side).</summary>
    /// <param name="extra">After the head count, e.g. elimination's "  ·  생존 3".</param>
    private void AddTeamHeader(Team side, bool ally, int score, int members, string extra = "") {
        Color c = ally ? Teams.AllyColor : Teams.EnemyColor;
        Image bg = UIBuilder.Image(body, "Team" + side, UITheme.WithAlpha(c, 0.16f));
        UIBuilder.Size(bg, height: 50);
        Image edge = UIBuilder.Image(bg.transform, "Edge", c);
        edge.rectTransform.anchorMin = Vector2.zero;
        edge.rectTransform.anchorMax = new Vector2(0, 1);
        edge.rectTransform.pivot = new Vector2(0, 0.5f);
        edge.rectTransform.sizeDelta = new Vector2(5, 0);
        TextMeshProUGUI name = UIBuilder.Text(bg.transform, "Name", Teams.Name(side) + L10n.T(" 팀") + (ally ? L10n.T("  <size=70%>(아군)</size>") : "") +
                                              "  <size=70%><color=#8E918A>" + members + L10n.T("명") + extra + "</color></size>",
                                              UITheme.BodyBold, 24, c, TextAlignmentOptions.MidlineLeft);
        UIBuilder.Fill(name.rectTransform, 36, 0, 36, 0);
        TextMeshProUGUI value = UIBuilder.Text(bg.transform, "Score", score.ToString(), UITheme.CondBold, 34, c, TextAlignmentOptions.MidlineRight);
        UIBuilder.Fill(value.rectTransform, 36, 0, 36, 0);
        rowObjects.Add(bg.gameObject);
    }

    private void AddRow(int rank, Row r) {
        Image bg = UIBuilder.Image(body, "Row", r.IsMe ? UITheme.AmberWash : new Color(0, 0, 0, 0));
        UIBuilder.Size(bg, height: 58);
        HorizontalLayoutGroup h = UIBuilder.HLayout(bg.gameObject, 0, new RectOffset(36, 36, 0, 0));
        h.childForceExpandWidth = false;
        if (r.IsMe) {
            Image edge = UIBuilder.Image(bg.transform, "Edge", UITheme.Amber);
            edge.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            edge.rectTransform.anchorMin = Vector2.zero;
            edge.rectTransform.anchorMax = new Vector2(0, 1);
            edge.rectTransform.pivot = new Vector2(0, 0.5f);
            edge.rectTransform.sizeDelta = new Vector2(5, 0);
        }
        Color ink = r.IsDead ? UITheme.Dim : UITheme.Ink;
        float kd = r.Deaths == 0 ? r.Kills : r.Kills / (float)r.Deaths;
        string name = UITheme.Safe(r.Name) + (r.IsDead ? "  <font=\"Cond-Medium\"><size=70%><color=#D8432E>KIA</color></size></font>" : "");
        string[] values = { rank.ToString(), name, r.Kills.ToString(), r.Deaths.ToString(), kd.ToString("0.00"),
                            r.Score.ToString("N0"), r.Ping.ToString() };
        for (int i = 0; i < values.Length; i++) {
            bool nameCol = i == 1;
            TextMeshProUGUI t = UIBuilder.Text(bg.transform, "C" + i, values[i], nameCol ? UITheme.BodyBold : UITheme.Cond,
                nameCol ? 24 : 26, i == 0 ? (r.IsMe ? UITheme.Amber : UITheme.Mute) : ink,
                i < 2 ? TextAlignmentOptions.MidlineLeft : TextAlignmentOptions.MidlineRight, localize: !nameCol);
            if (i == 6) t.color = r.Ping > 120 ? UITheme.Red : (r.Ping > 70 ? UITheme.Amber : (r.IsDead ? UITheme.Dim : UITheme.Mute));
            Cell(t, i);
        }
        Line(bg.transform, new Color(0.925f, 0.918f, 0.89f, 0.06f), true);
        rowObjects.Add(bg.gameObject);
    }

    private static RectTransform MakeRow(Transform parent, float height, Color? color) {
        RectTransform rt = UIBuilder.Rect("Columns", parent);
        UIBuilder.Size(rt, height: height);
        HorizontalLayoutGroup h = UIBuilder.HLayout(rt.gameObject, 0, new RectOffset(36, 36, 0, 0));
        h.childForceExpandWidth = false;
        return rt;
    }

    private static void Cell(Component c, int column) {
        LayoutElement le = UIBuilder.Size(c, ColumnWidths[column] < 0 ? 0 : ColumnWidths[column]);
        if (ColumnWidths[column] < 0) le.flexibleWidth = 1;
    }

    private static void Line(Transform parent, Color c, bool overlay = false) {
        Image l = UIBuilder.Image(parent, "Line", c);
        if (overlay) {
            l.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            l.rectTransform.anchorMin = Vector2.zero;
            l.rectTransform.anchorMax = new Vector2(1, 0);
            l.rectTransform.pivot = new Vector2(0.5f, 0);
            l.rectTransform.sizeDelta = new Vector2(0, 1);
        } else {
            UIBuilder.Size(l, height: 1);
        }
    }

}
