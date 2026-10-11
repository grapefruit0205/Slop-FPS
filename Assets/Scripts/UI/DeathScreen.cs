using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// "Killed in action" overlay: who killed you, how, and the respawn countdown. In elimination there is no respawn
/// this round: it says so and gives way to the spectator view after a few seconds (<see cref="Spectator"/>).
/// </summary>
public class DeathScreen : MonoBehaviour {

    public bool Visible => gameObject.activeSelf;

    private TextMeshProUGUI killerName, killerMeta, killerHp, countdown, opticHint;
    private Image[] cards;
    private TextMeshProUGUI[] cardNames;
    private RectTransform barFill;
    private CanvasGroup group;
    private float total, remaining;
    private bool outOfRound;
    private float shownFor;

    public static DeathScreen Create(Transform parent) {
        RectTransform rt = UIBuilder.Fill(UIBuilder.Rect("Death", parent));
        DeathScreen s = rt.gameObject.AddComponent<DeathScreen>();
        s.Build(rt);
        rt.gameObject.SetActive(false);
        return s;
    }

    /// <param name="respawnTime">Seconds to the respawn; negative when the player is out until the next round.</param>
    public void Show(string killer, string weapon, bool headshot, float distance, int killerHealth, float respawnTime) {
        killerName.text = killer;
        killerMeta.text = L10n.T(weapon) + (headshot ? L10n.T("  ·  헤드샷") : "") + "  ·  " + Mathf.RoundToInt(distance) + "m";
        killerHp.text = killerHealth > 0 ? killerHealth.ToString() : "-";
        outOfRound = respawnTime < 0f;
        total = remaining = Mathf.Max(0f, respawnTime);
        shownFor = 0f;
        group.alpha = 0f;
        gameObject.SetActive(true);
        RefreshLoadout();
    }

    public void Hide() {
        gameObject.SetActive(false);
    }

    private void Build(RectTransform root) {
        group = UIBuilder.Group(root.gameObject);
        group.blocksRaycasts = false;

        Image vignette = UIBuilder.Image(root, "Vignette", new Color(0.47f, 0.06f, 0.03f, 0.55f));
        vignette.sprite = DeathVignette();
        UIBuilder.Fill(vignette.rectTransform);
        Image dim = UIBuilder.Image(root, "Dim", new Color(0, 0, 0, 0.35f));
        UIBuilder.Fill(dim.rectTransform);

        TextMeshProUGUI kia = UIBuilder.Text(root, "KIA", "전사", UITheme.BodyBold, 120, UITheme.Red, TextAlignmentOptions.Center);
        UIBuilder.Place(kia.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0, 190), new Vector2(900, 140));
        kia.characterSpacing = 30;
        TextMeshProUGUI sub = UIBuilder.Label(root, "KILLED IN ACTION", 24, TextAlignmentOptions.Center);
        sub.characterSpacing = 40;
        UIBuilder.Place(sub.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0, 105), new Vector2(900, 30));

        Image card = UIBuilder.Panel(root, "Killer");
        UIBuilder.Place(card.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0, -20), new Vector2(640, 150));
        card.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        UIBuilder.Brackets(card.rectTransform);
        TextMeshProUGUI l1 = UIBuilder.Label(card.transform, "처치한 적");
        UIBuilder.Place(l1.rectTransform, new Vector2(0, 1), new Vector2(28, -20), new Vector2(300, 24));
        TextMeshProUGUI l2 = UIBuilder.Label(card.transform, "남은 체력", 16, TextAlignmentOptions.Right);
        UIBuilder.Place(l2.rectTransform, new Vector2(1, 1), new Vector2(-28, -20), new Vector2(200, 24));
        killerName = UIBuilder.Text(card.transform, "Name", "", UITheme.BodyBold, 40, UITheme.Ink);
        // Taller than the font's line height: Ellipsis drops a line entirely when it doesn't fit vertically.
        UIBuilder.Place(killerName.rectTransform, new Vector2(0, 1), new Vector2(26, -42), new Vector2(440, 64));
        killerName.enableWordWrapping = false;
        killerName.overflowMode = TextOverflowModes.Ellipsis;
        killerHp = UIBuilder.Text(card.transform, "Hp", "", UITheme.CondBold, 48, UITheme.Amber, TextAlignmentOptions.Right);
        UIBuilder.Place(killerHp.rectTransform, new Vector2(1, 1), new Vector2(-28, -46), new Vector2(160, 54));
        killerMeta = UIBuilder.Text(card.transform, "Meta", "", UITheme.Body, 20, UITheme.Mute);
        UIBuilder.Place(killerMeta.rectTransform, new Vector2(0, 0), new Vector2(28, 18), new Vector2(560, 28));

        BuildLoadout(root);

        countdown = UIBuilder.Text(root, "Countdown", "", UITheme.Body, 26, UITheme.Ink, TextAlignmentOptions.Center);
        UIBuilder.Place(countdown.rectTransform, new Vector2(0.5f, 0), new Vector2(0, 190), new Vector2(640, 40));
        countdown.rectTransform.pivot = new Vector2(0.5f, 0);
        Image bar = UIBuilder.Image(root, "Bar", new Color(0.925f, 0.918f, 0.89f, 0.14f));
        UIBuilder.Place(bar.rectTransform, new Vector2(0.5f, 0), new Vector2(0, 170), new Vector2(640, 8));
        bar.rectTransform.pivot = new Vector2(0.5f, 0);
        Image fill = UIBuilder.Image(bar.transform, "Fill", UITheme.Amber);
        barFill = fill.rectTransform;
        barFill.anchorMin = Vector2.zero;
        barFill.anchorMax = new Vector2(0, 1);
        barFill.pivot = new Vector2(0, 0.5f);
        barFill.sizeDelta = Vector2.zero;
    }

    /// <summary>The next deployment's primary: [1]–[4] (or a click) picks the gun, [F] cycles its optic.</summary>
    private void BuildLoadout(RectTransform root) {
        const float w = 210f, h = 92f, gap = 14f;
        int n = Weapons.Primaries.Length;
        float width = n * w + (n - 1) * gap;
        RectTransform box = UIBuilder.Rect("Loadout", root);
        UIBuilder.Place(box, new Vector2(0.5f, 0), new Vector2(0, 250), new Vector2(width, h + 34));
        box.pivot = new Vector2(0.5f, 0);
        TextMeshProUGUI title = UIBuilder.Label(box, "다음 배치 무장");
        UIBuilder.Place(title.rectTransform, new Vector2(0, 1), new Vector2(2, 0), new Vector2(300, 24));
        opticHint = UIBuilder.Text(box, "Optic", "", UITheme.Body, 17, UITheme.Mute, TextAlignmentOptions.Right);
        UIBuilder.Place(opticHint.rectTransform, new Vector2(1, 1), new Vector2(-2, 0), new Vector2(600, 24));
        cards = new Image[n];
        cardNames = new TextMeshProUGUI[n];
        for (int i = 0; i < n; i++) {
            WeaponId id = Weapons.Primaries[i];
            WeaponSpec spec = Weapons.Get(id);
            Image card = UIBuilder.Panel(box, "Card" + i);
            UIBuilder.Place(card.rectTransform, new Vector2(0, 0), new Vector2(i * (w + gap), 0), new Vector2(w, h));
            Button button = card.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(() => Pick(id));
            TextMeshProUGUI key = UIBuilder.Text(card.transform, "Key", "[" + (i + 1) + "]", UITheme.CondMedium, 16, UITheme.Mute);
            UIBuilder.Place(key.rectTransform, new Vector2(0, 1), new Vector2(14, -8), new Vector2(40, 20));
            TextMeshProUGUI role = UIBuilder.Text(card.transform, "Role", spec.Role, UITheme.Body, 16, UITheme.Mute, TextAlignmentOptions.Right);
            UIBuilder.Place(role.rectTransform, new Vector2(1, 1), new Vector2(-14, -8), new Vector2(140, 20));
            cardNames[i] = UIBuilder.Text(card.transform, "Name", spec.Name, UITheme.CondBold, 34, UITheme.Ink);
            UIBuilder.Place(cardNames[i].rectTransform, new Vector2(0, 0), new Vector2(14, 10), new Vector2(w - 28, 44));
            cards[i] = card;
        }
        group.blocksRaycasts = true;
        KeepClicksForCards(root);
    }

    // The overlay's full-screen images must not swallow clicks meant for the cards.
    private static void KeepClicksForCards(RectTransform root) {
        foreach (Image img in root.GetComponentsInChildren<Image>(true)) {
            if (img.GetComponent<Button>() == null) img.raycastTarget = false;
        }
    }

    private void Pick(WeaponId id) {
        LoadoutChoice.Set(id, LoadoutChoice.Optic);
        UISounds.Click();
        RefreshLoadout();
    }

    private void RefreshLoadout() {
        for (int i = 0; i < cards.Length; i++) {
            bool on = Weapons.Primaries[i] == LoadoutChoice.Primary;
            cards[i].color = on ? UITheme.AmberWash : UITheme.Panel;
            cardNames[i].color = on ? UITheme.Amber : UITheme.Ink;
        }
        bool choice = Weapons.Get(LoadoutChoice.Primary).Optics.Length > 1;
        opticHint.text = L10n.T("조준경  <color=#ECEAE3>") + Weapons.OpticName(LoadoutChoice.Optic) + "</color>" +
                         (choice ? L10n.T("   <color=#E3A72F>[F]</color> 변경") : "");
    }

    void Update() {
        RefreshLoadout();
        bool canPick = GameUI.Instance == null || !GameUI.Instance.Pause.Visible;
        for (int i = 0; canPick && i < Weapons.Primaries.Length && i < 9; i++) {
            if (Input.GetKeyDown(KeyCode.Alpha1 + i)) Pick(Weapons.Primaries[i]);
        }
        if (canPick && Input.GetKeyDown(KeyCode.F)) {
            LoadoutChoice.NextOptic();
            UISounds.Click();
            RefreshLoadout();
        }
        shownFor += Time.deltaTime;
        if (outOfRound) {
            // The loadout cards stay usable for the next round until the view moves on to a teammate.
            float fade = Spectator.DeathCamTime - shownFor;
            group.alpha = Mathf.Clamp01(Mathf.Min(shownFor * 2.5f, fade * 2f));
            barFill.anchorMax = new Vector2(Mathf.Clamp01(shownFor / Spectator.DeathCamTime), 1);
            countdown.text = L10n.T("다음 라운드에 재배치  ·  <color=#E3A72F>아군 관전</color>으로 전환합니다");
            if (fade <= 0f) Hide();
            return;
        }
        remaining = Mathf.Max(0f, remaining - Time.deltaTime);
        group.alpha = Mathf.MoveTowards(group.alpha, 1f, Time.deltaTime * 2.5f);
        float progress = total > 0f ? 1f - remaining / total : 1f;
        barFill.anchorMax = new Vector2(progress, 1);
        countdown.text = "<font=\"Cond-Bold\"><size=130%><color=#E3A72F>" + Mathf.CeilToInt(remaining) +
                         L10n.T("</color></size></font>  초 후 재배치");
    }

    private static Sprite DeathVignette() {
        const int n = 128;
        Texture2D tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        for (int y = 0; y < n; y++) {
            for (int x = 0; x < n; x++) {
                float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                float d = Mathf.Sqrt(dx * dx + dy * dy) / 1.41421f;
                tex.SetPixel(x, y, new Color(1, 1, 1, Mathf.SmoothStep(0.15f, 1f, d)));
            }
        }
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f));
    }

}
