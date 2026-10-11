using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// A one-line controls reminder at the bottom of the HUD for the first seconds after the first spawn of each match
/// (sprint, stances, lean, aim...). The full list stays on the pause menu's controls page.
/// Lives under the HUD root, so it shows and hides with it.
/// </summary>
public class ControlsHint : MonoBehaviour {

    public const float ShowSeconds = 15f;
    private const float FadeSeconds = 1.2f;

    /// <summary>Key, action: the bindings in FirstPersonController, PlayerStance, FpsGun, GrenadeHands and GameUI.</summary>
    public static readonly string[,] Keys = {
        { "Shift", "달리기" }, { "C", "앉기" }, { "Z", "엎드리기" }, { "Q/E", "기울이기" },
        { "우클릭", "조준" }, { "R", "재장전" }, { "1/2", "무기" }, { "4/5/6", "수류탄" }, { "Tab", "스코어보드" },
    };

    private CanvasGroup group;
    private PlayerNetworkMover seen;
    private bool shownThisMatch;
    private float left;

    public static ControlsHint Create(Transform hud) {
        RectTransform rt = UIBuilder.Rect("ControlsHint", hud);
        // Above the health and ammo panels, which reach the middle on narrow (16:10) canvases.
        UIBuilder.Place(rt, new Vector2(0.5f, 0), new Vector2(0, 222), new Vector2(980, 46));
        ControlsHint h = rt.gameObject.AddComponent<ControlsHint>();
        h.Build(rt);
        return h;
    }

    /// <summary>A new match: show the hint again on the first spawn.</summary>
    public void ResetForMatch() {
        shownThisMatch = false;
        seen = null;
        left = 0f;
        group.alpha = 0f;
    }

    private void Build(RectTransform root) {
        group = UIBuilder.Group(root.gameObject);
        group.blocksRaycasts = false;
        group.interactable = false;
        group.alpha = 0f;
        Image bg = UIBuilder.Image(root, "Panel", UITheme.Panel);
        UIBuilder.Fill(bg.rectTransform);
        UIBorder.Add(bg.rectTransform, UITheme.Line);
        UIBuilder.Brackets(bg.rectTransform, 12, 2);
        TextMeshProUGUI t = UIBuilder.Text(bg.transform, "Text", Line(), UITheme.Body, 21, UITheme.Ink, TextAlignmentOptions.Center);
        LocalizedLabel.BindDynamic(t, Line);
        UIBuilder.Fill(t.rectTransform, 16, 0, 16, 0);
        t.enableAutoSizing = true;
        t.fontSizeMin = 14;
        t.fontSizeMax = 21;
        t.gameObject.AddComponent<Shadow>().effectColor = new Color(0, 0, 0, 0.6f);
    }

    /// <summary>"Shift 달리기 · C 앉기 · ..." with amber keys (TMP rich text); also the pause menu's footer.</summary>
    public static string Line() {
        string amber = "#" + ColorUtility.ToHtmlStringRGB(UITheme.Amber);
        string mute = "#" + ColorUtility.ToHtmlStringRGB(UITheme.Dim);
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        for (int i = 0; i < Keys.GetLength(0); i++) {
            if (i > 0) sb.Append("  <color=").Append(mute).Append(">·</color>  ");
            sb.Append("<color=").Append(amber).Append("><b>").Append(L10n.T(Keys[i, 0])).Append("</b></color> ").Append(L10n.T(Keys[i, 1]));
        }
        return sb.ToString();
    }

    void Update() {
        PlayerNetworkMover local = PlayerNetworkMover.Local;
        if (local != null && local != seen) {
            seen = local;
            if (!shownThisMatch) {
                shownThisMatch = true;
                left = ShowSeconds;
            }
        }
        if (local == null) left = Mathf.Min(left, FadeSeconds);   // died or left: fade out
        if (left > 0f) left -= Time.unscaledDeltaTime;
        // The pause menu has its own copy at the same height.
        bool paused = GameUI.Instance != null && GameUI.Instance.Pause.Visible;
        group.alpha = paused ? 0f : Mathf.Clamp01(left / FadeSeconds);
    }

}
