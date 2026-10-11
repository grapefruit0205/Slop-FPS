using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Elimination's part of the HUD, around <see cref="MatchHud"/>'s round score: how many of each side are still standing
/// (a box beside each score box, its top bar one segment per player), the freeze countdown, the round result banner,
/// "waiting for the other side", and who you are watching while out of the round. Hidden in the other modes.
/// </summary>
public class RoundHud : MonoBehaviour {

    private const float BoxWidth = 96f;
    private const float Outside = 132f + 128f + 8f;     // MatchHud's clock gap + score box + a little air

    private RectTransform root;
    private Side ours, theirs;
    private CanvasGroup freeze, banner, spectate;
    private TextMeshProUGUI freezeRound, freezeCount, bannerTitle, bannerDetail, waiting, spectateName, spectateHint;

    private class Side {
        public GameObject Box;
        public TextMeshProUGUI Value, Caption;
        public RectTransform Bar;
        public readonly List<Image> Segments = new List<Image>();
    }

    public static RoundHud Create(Transform hud) {
        RectTransform rt = UIBuilder.Fill(UIBuilder.Rect("Rounds", hud));
        RoundHud h = rt.gameObject.AddComponent<RoundHud>();
        h.Build(rt);
        return h;
    }

    private void Build(RectTransform rt) {
        root = rt;
        ours = AliveBox(-1);
        theirs = AliveBox(1);

        // Freeze: round number, a big countdown, what is going on.
        RectTransform f = UIBuilder.Rect("Freeze", root);
        UIBuilder.Place(f, new Vector2(0.5f, 0.5f), new Vector2(0, 230), new Vector2(700, 200));
        freeze = UIBuilder.Group(f.gameObject);
        freezeRound = UIBuilder.Text(f, "Round", "", UITheme.CondMedium, 28, UITheme.Amber, TextAlignmentOptions.Center);
        UIBuilder.Place(freezeRound.rectTransform, new Vector2(0.5f, 1), Vector2.zero, new Vector2(700, 34));
        freezeRound.characterSpacing = 12;
        freezeCount = UIBuilder.Text(f, "Count", "", UITheme.CondBold, 110, UITheme.Ink, TextAlignmentOptions.Center);
        UIBuilder.Place(freezeCount.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0, 0), new Vector2(300, 120));
        TextMeshProUGUI hint = UIBuilder.Text(f, "Hint", "배치 완료  ·  시작 전까지 이동 · 사격 불가", UITheme.Body, 22, UITheme.Mute, TextAlignmentOptions.Center);
        UIBuilder.Place(hint.rectTransform, new Vector2(0.5f, 0), Vector2.zero, new Vector2(700, 30));
        Shadow(freezeRound);
        Shadow(freezeCount);
        Shadow(hint);

        // Round result.
        Image b = UIBuilder.Panel(root, "Banner", UITheme.PanelSolid);
        UIBuilder.Place(b.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0, 200), new Vector2(760, 150));
        b.raycastTarget = false;
        UIBuilder.Brackets(b.rectTransform, 22, 3);
        banner = UIBuilder.Group(b.gameObject);
        bannerTitle = UIBuilder.Text(b.transform, "Title", "", UITheme.BodyBold, 64, UITheme.Ink, TextAlignmentOptions.Center);
        UIBuilder.Place(bannerTitle.rectTransform, new Vector2(0.5f, 1), new Vector2(0, -14), new Vector2(740, 76));
        bannerTitle.characterSpacing = 6;
        bannerDetail = UIBuilder.Text(b.transform, "Detail", "", UITheme.Body, 26, UITheme.Ink, TextAlignmentOptions.Center);
        UIBuilder.Place(bannerDetail.rectTransform, new Vector2(0.5f, 0), new Vector2(0, 18), new Vector2(740, 36));

        // Waiting for the other side.
        waiting = UIBuilder.Text(root, "Waiting", "", UITheme.Body, 22, UITheme.Amber, TextAlignmentOptions.Center);
        UIBuilder.Place(waiting.rectTransform, new Vector2(0.5f, 1), new Vector2(0, -122), new Vector2(900, 30));
        Shadow(waiting);

        // Spectating.
        Image s = UIBuilder.Panel(root, "Spectate");
        UIBuilder.Place(s.rectTransform, new Vector2(0.5f, 0), new Vector2(0, 150), new Vector2(560, 104));
        s.raycastTarget = false;
        spectate = UIBuilder.Group(s.gameObject);
        TextMeshProUGUI label = UIBuilder.Label(s.transform, "관전 중", 16, TextAlignmentOptions.Center);
        UIBuilder.Place(label.rectTransform, new Vector2(0.5f, 1), new Vector2(0, -8), new Vector2(540, 22));
        spectateName = UIBuilder.Text(s.transform, "Name", "", UITheme.BodyBold, 30, Teams.AllyColor, TextAlignmentOptions.Center);
        // Taller than the line: Ellipsis drops a line that doesn't fit vertically.
        UIBuilder.Place(spectateName.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0, 0), new Vector2(540, 56));
        spectateName.overflowMode = TextOverflowModes.Ellipsis;
        spectateHint = UIBuilder.Text(s.transform, "Hint", "<color=#E3A72F>[클릭 / Space]</color>  다음 아군", UITheme.Body, 16, UITheme.Mute, TextAlignmentOptions.Center);
        UIBuilder.Place(spectateHint.rectTransform, new Vector2(0.5f, 0), new Vector2(0, 6), new Vector2(540, 22));

        freeze.alpha = banner.alpha = spectate.alpha = 0f;
    }

    /// <summary>"생존" box beside a score box: a segmented bar (one per player), the count, a caption.</summary>
    private Side AliveBox(int side) {
        Side s = new Side();
        Image bg = UIBuilder.Image(root, side < 0 ? "OursAlive" : "TheirsAlive", UITheme.Panel);
        RectTransform rt = bg.rectTransform;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1);
        rt.pivot = new Vector2(side < 0 ? 1f : 0f, 1f);
        rt.anchoredPosition = new Vector2(side * Outside, -16);
        rt.sizeDelta = new Vector2(BoxWidth, 56);
        s.Box = bg.gameObject;
        s.Bar = UIBuilder.Rect("Bar", rt);
        s.Bar.anchorMin = new Vector2(0, 1);
        s.Bar.anchorMax = new Vector2(1, 1);
        s.Bar.pivot = new Vector2(0.5f, 1);
        s.Bar.sizeDelta = new Vector2(0, 3);
        HorizontalLayoutGroup h = UIBuilder.HLayout(s.Bar.gameObject, 3);
        h.childForceExpandWidth = true;
        h.childForceExpandHeight = true;
        h.childControlWidth = true;
        h.childControlHeight = true;
        s.Value = UIBuilder.Text(rt, "Value", "0", UITheme.CondBold, 30, UITheme.Ink, TextAlignmentOptions.Center);
        UIBuilder.Fill(s.Value.rectTransform, 0, 18, 0, 4);
        s.Caption = UIBuilder.Text(rt, "Caption", "생존", UITheme.Body, 14, UITheme.Mute, TextAlignmentOptions.Center);
        UIBuilder.Fill(s.Caption.rectTransform, 4, 1, 4, 35);
        s.Caption.characterSpacing = 2;
        return s;
    }

    private float timer;

    void Update() {
        timer -= Time.unscaledDeltaTime;
        if (timer > 0f) return;
        timer = 0.1f;
        Refresh();
    }

    private void Refresh() {
        MatchManager mm = MatchManager.Instance;
        NetPlayer me = NetPlayer.Local;
        bool show = mm != null && me != null && mm.IsRoundBased;
        ours.Box.SetActive(show);
        theirs.Box.SetActive(show);
        if (!show) {
            freeze.alpha = banner.alpha = spectate.alpha = 0f;
            waiting.text = mm != null && mm.WaitingForPlayers ? L10n.T("플레이어를 기다리는 중 · 2명이 접속하면 시작합니다") : "";
            return;
        }
        Team mine = me.Team != Team.None ? me.Team : Team.A;
        Team other = Teams.Other(mine);
        SetSide(ours, mm.AliveOf(mine), Members(mine), Teams.AllyColor);
        SetSide(theirs, mm.AliveOf(other), Members(other), Teams.EnemyColor);

        RoundPhase phase = mm.IsOver ? RoundPhase.None : mm.Phase;
        freeze.alpha = phase == RoundPhase.Freeze ? 1f : 0f;
        if (phase == RoundPhase.Freeze) {
            freezeRound.text = L10n.T("라운드 ") + mm.Round;
            freezeCount.text = Mathf.Max(1, Mathf.CeilToInt(mm.PhaseLeft)).ToString();
        }

        banner.alpha = phase == RoundPhase.RoundEnd ? 1f : 0f;
        if (phase == RoundPhase.RoundEnd) {
            Team w = mm.RoundWinner;
            bannerTitle.text = MatchManager.RoundResultText(w);
            bannerTitle.color = w == Team.None ? UITheme.Amber : w == mine ? Teams.AllyColor : Teams.EnemyColor;
            string why = w == Team.None ? (mm.RoundByTime ? L10n.T("시간 종료 · 생존 인원 동일") : L10n.T("양 팀 전멸"))
                       : mm.RoundByTime ? L10n.T("시간 종료 · 생존 인원 우세") : Teams.Name(Teams.Other(w)) + L10n.T(" 전멸");
            bool last = mm.ScoreOf(Team.A) >= mm.ScoreLimit || mm.ScoreOf(Team.B) >= mm.ScoreLimit;
            bannerDetail.text = "<color=" + MatchManager.SideHex(Team.A) + ">" + Teams.Name(Team.A) + " " + mm.ScoreOf(Team.A) + "</color>  :  <color=" +
                                MatchManager.SideHex(Team.B) + ">" + mm.ScoreOf(Team.B) + " " + Teams.Name(Team.B) + "</color>" +
                                "   <color=#8E918A>·  " + why + (last ? L10n.T("  ·  작전 종료") : "") + "</color>";
        }

        waiting.text = phase == RoundPhase.Waiting ? L10n.T("상대 팀 인원을 기다리는 중  ·  양 팀에 한 명 이상 있으면 라운드가 시작됩니다") : "";

        Spectator spec = Spectator.Instance;
        bool watching = spec != null && spec.Active && spec.Target != null;
        spectate.alpha = watching ? 1f : 0f;
        if (watching) {
            NetPlayer t = spec.Target;
            bool ally = t.Team == mine;
            spectateName.text = UITheme.Safe(t.Callsign.Value);
            spectateName.color = ally ? Teams.AllyColor : Teams.EnemyColor;
            spectateHint.text = L10n.T("<color=#E3A72F>[클릭 / Space]</color>  ") + (ally ? L10n.T("다음 아군") : L10n.T("아군 전멸  ·  다음 생존자"));
        }
    }

    private static int Members(Team team) {
        int n = 0;
        foreach (NetPlayer p in NetPlayer.All) {
            if (p.Team == team) n++;
        }
        return n;
    }

    private static void SetSide(Side s, int alive, int members, Color color) {
        int slots = Mathf.Max(Modes.TeamSize, members);
        while (s.Segments.Count < slots) s.Segments.Add(UIBuilder.Image(s.Bar, "Seg", color));
        for (int i = 0; i < s.Segments.Count; i++) {
            Image seg = s.Segments[i];
            seg.gameObject.SetActive(i < slots);
            seg.color = i < alive ? color : i < members ? UITheme.WithAlpha(UITheme.Ink, 0.18f) : UITheme.WithAlpha(UITheme.Ink, 0.06f);
        }
        s.Value.text = alive.ToString();
        s.Value.color = alive > 0 ? UITheme.Ink : UITheme.Dim;
        s.Caption.text = L10n.T("생존");
        s.Caption.color = color;
    }

    private static void Shadow(Graphic g) {
        Shadow s = g.gameObject.AddComponent<Shadow>();
        s.effectColor = new Color(0, 0, 0, 0.85f);
        s.effectDistance = new Vector2(1.5f, -1.5f);
    }

}
