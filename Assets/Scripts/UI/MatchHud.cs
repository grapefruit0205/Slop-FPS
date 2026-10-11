using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The round's score next to the HUD clock: own side (blue) left and the other side (red) right in team deathmatch;
/// own kills and the leader's in free-for-all; rounds won in elimination, whose round HUD (<see cref="RoundHud"/>)
/// it also sets up. A line under the K/D stats names the mode, map and score limit.
/// Lives under the HUD root, so it shows and hides with it.
/// </summary>
public class MatchHud : MonoBehaviour {

    private const float BoxWidth = 128f;
    private const float ClockGap = 132f;     // half the clock's width plus a little air

    private TextMeshProUGUI leftValue, rightValue, leftLabel, rightLabel, modeLine;
    private Image leftBar, rightBar;
    private float timer;

    public static MatchHud Create(Transform hud) {
        RectTransform rt = UIBuilder.Fill(UIBuilder.Rect("MatchScore", hud));
        MatchHud m = rt.gameObject.AddComponent<MatchHud>();
        m.Build(rt);
        RoundHud.Create(hud);
        return m;
    }

    private void Build(RectTransform root) {
        Box(root, -1, out leftValue, out leftLabel, out leftBar);
        Box(root, 1, out rightValue, out rightLabel, out rightBar);
        modeLine = UIBuilder.Text(root, "Mode", "", UITheme.CondMedium, 18, UITheme.Mute);
        modeLine.characterSpacing = 4;
        UIBuilder.Place(modeLine.rectTransform, new Vector2(0, 1), new Vector2(48, -78), new Vector2(700, 26));
        Shadow(modeLine);
        Refresh();
    }

    /// <summary>One score box beside the clock: a coloured bar on top, the number, a small caption at the bottom.</summary>
    private static void Box(RectTransform root, int side, out TextMeshProUGUI value, out TextMeshProUGUI label, out Image bar) {
        Image bg = UIBuilder.Image(root, side < 0 ? "Ours" : "Theirs", UITheme.Panel);
        RectTransform rt = bg.rectTransform;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1);
        rt.pivot = new Vector2(side < 0 ? 1f : 0f, 1f);
        rt.anchoredPosition = new Vector2(side * ClockGap, -16);
        rt.sizeDelta = new Vector2(BoxWidth, 56);       // ends just above the compass strip
        bar = UIBuilder.Image(rt, "Bar", Teams.AllyColor);
        bar.rectTransform.anchorMin = new Vector2(0, 1);
        bar.rectTransform.anchorMax = new Vector2(1, 1);
        bar.rectTransform.pivot = new Vector2(0.5f, 1);
        bar.rectTransform.sizeDelta = new Vector2(0, 3);
        value = UIBuilder.Text(rt, "Value", "0", UITheme.CondBold, 32, UITheme.Ink, TextAlignmentOptions.Center);
        UIBuilder.Fill(value.rectTransform, 0, 18, 0, 3);
        label = UIBuilder.Text(rt, "Caption", "", UITheme.Body, 14, UITheme.Mute, TextAlignmentOptions.Center);
        UIBuilder.Fill(label.rectTransform, 4, 1, 4, 35);
        label.characterSpacing = 2;
    }

    void Update() {
        timer -= Time.unscaledDeltaTime;
        if (timer > 0f) return;
        timer = 0.25f;
        Refresh();
    }

    private void Refresh() {
        MatchManager mm = MatchManager.Instance;
        NetPlayer me = NetPlayer.Local;
        bool show = mm != null && me != null;
        leftValue.transform.parent.gameObject.SetActive(show);
        rightValue.transform.parent.gameObject.SetActive(show);
        if (!show) {
            modeLine.text = "";
            return;
        }
        modeLine.text = Modes.DisplayName(mm.Mode) + "  ·  " + MapLoader.DisplayName(mm.MapId) +
                        (mm.IsRoundBased && mm.Round > 0 ? L10n.T("  ·  라운드 <color=#ECEAE3>") + mm.Round + "</color>" : "") +
                        L10n.T("  ·  목표 <color=#ECEAE3>") + mm.ScoreLimit + "</color>" + (mm.IsRoundBased ? L10n.T("승") : L10n.T("킬"));

        if (mm.IsTeamMode) {
            Team mine = me.Team != Team.None ? me.Team : Team.A;
            Set(leftValue, leftLabel, leftBar, mm.ScoreOf(mine), L10n.T("아군 ") + Teams.Name(mine), Teams.AllyColor);
            Set(rightValue, rightLabel, rightBar, mm.ScoreOf(Teams.Other(mine)), L10n.T("적군 ") + Teams.Name(Teams.Other(mine)), Teams.EnemyColor);
            return;
        }

        // Free-for-all: me against whoever leads (or the runner-up, when I am the leader).
        NetPlayer best = null;
        foreach (NetPlayer p in NetPlayer.All) {
            if (p == me) continue;
            if (best == null || p.Kills.Value > best.Kills.Value) best = p;
        }
        bool leading = best == null || me.Kills.Value >= best.Kills.Value;
        Set(leftValue, leftLabel, leftBar, me.Kills.Value, leading ? L10n.T("나  ·  1위") : L10n.T("나"), UITheme.Amber);
        string other = best != null ? best.Callsign.Value : "-";
        if (other.Length > 9) other = other.Substring(0, 9);
        Set(rightValue, rightLabel, rightBar, best != null ? best.Kills.Value : 0, (leading ? L10n.T("2위 ") : L10n.T("1위 ")) + UITheme.Safe(other), Teams.EnemyColor);
    }

    private static void Set(TextMeshProUGUI value, TextMeshProUGUI label, Image bar, int score, string caption, Color color) {
        value.text = score.ToString();
        label.text = caption;
        label.color = color;
        bar.color = color;
    }

    private static void Shadow(Graphic g) {
        Shadow s = g.gameObject.AddComponent<Shadow>();
        s.effectColor = new Color(0, 0, 0, 0.85f);
        s.effectDistance = new Vector2(1.5f, -1.5f);
    }

}
