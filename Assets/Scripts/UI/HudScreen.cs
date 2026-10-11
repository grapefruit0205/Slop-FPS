using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// In-game HUD: vitals, ammo, crosshair, hitmarker, damage direction, kill feed,
/// event log, compass, match clock and K/D line, and the grenades (<see cref="GrenadeHud"/>).
/// </summary>
public class HudScreen : MonoBehaviour {

    private const int SegmentCount = 10;
    private const int MagPipCount = 30;
    private const float FeedLifetime = 7f;
    private const float LogLifetime = 9f;
    private const float CompassWidth = 620f;
    private const float CompassSpan = 150f;     // degrees visible across the strip

    private CanvasGroup combat;                 // elements that only make sense while alive
    private Image damageFlash, lowHealth;
    private TextMeshProUGUI statsLine, clock, bearing, hpText, callsignText, ammoText, reserveText, reloadText, weaponText, modeText, slotText, stanceText;
    private CanvasGroup crosshairGroup, scopeGroup;
    private RectTransform scope4x, scope8x;
    private Image redDot;
    private Image redDotGlow;
    private float baseSpread;
    private RectTransform compassContent, feedRoot, logRoot, crosshairRoot, hitRoot;
    private Image[] segments, magPips, crosshair, hitBars;
    private readonly List<DirIndicator> dirIndicators = new List<DirIndicator>();
    private readonly List<FadeLine> feed = new List<FadeLine>();
    private readonly List<FadeLine> log = new List<FadeLine>();
    private float spread, hitTimer, hitScale = 1f, statsTimer;
    private int health = 100, maxHealth = 100;
    private Sprite arcSprite;

    /// <summary>Grenade slots and the flashbang / smoke overlays.</summary>
    public GrenadeHud Grenades { get; private set; }

    public static HudScreen Create(Transform parent) {
        RectTransform rt = UIBuilder.Fill(UIBuilder.Rect("HUD", parent));
        HudScreen s = rt.gameObject.AddComponent<HudScreen>();
        s.Build(rt);
        return s;
    }

    public void SetVisible(bool v) {
        gameObject.SetActive(v);
    }

    /// <summary>Alive shows crosshair/vitals/weapon; dead keeps only feed, log and clock.</summary>
    public void SetAlive(bool alive) {
        combat.alpha = alive ? 1f : 0f;
        if (!alive) {
            lowHealth.color = UITheme.WithAlpha(UITheme.Red, 0f);
            foreach (DirIndicator d in dirIndicators) d.Life = 0f;
            if (Grenades != null) Grenades.ClearOverlays();
        }
    }

    public void ResetState() {
        foreach (FadeLine f in feed) Destroy(f.Text.transform.parent.gameObject);
        foreach (FadeLine f in log) Destroy(f.Text.gameObject);
        feed.Clear();
        log.Clear();
        damageFlash.color = UITheme.WithAlpha(UITheme.Red, 0f);
        SetHealth(100, 100);
        SetAlive(false);
    }

    private void Build(RectTransform root) {
        // Full-screen effects first so everything else draws over them.
        damageFlash = UIBuilder.Image(root, "DamageFlash", UITheme.WithAlpha(UITheme.Red, 0f));
        UIBuilder.Fill(damageFlash.rectTransform);
        lowHealth = UIBuilder.Image(root, "LowHealth", UITheme.WithAlpha(UITheme.Red, 0f));
        lowHealth.sprite = VignetteSprite();
        UIBuilder.Fill(lowHealth.rectTransform);
        arcSprite = ArcSprite();

        // Top-left K/D/rank
        statsLine = UIBuilder.Text(root, "Stats", "", UITheme.CondMedium, 22, UITheme.Ink);
        statsLine.characterSpacing = 6;
        UIBuilder.Place(statsLine.rectTransform, new Vector2(0, 1), new Vector2(48, -36), new Vector2(700, 40));
        Shadow(statsLine);

        BuildCompass(root);
        BuildFeed(root);

        // Everything below is hidden while dead.
        RectTransform combatRoot = UIBuilder.Fill(UIBuilder.Rect("Combat", root));
        combat = UIBuilder.Group(combatRoot.gameObject);
        combat.blocksRaycasts = false;
        combat.interactable = false;

        BuildScope(combatRoot);
        BuildCrosshair(combatRoot);
        BuildVitals(combatRoot);
        BuildWeapon(combatRoot);
        Grenades = GrenadeHud.Create(root, combatRoot);

        logRoot = UIBuilder.Rect("Log", root);
        UIBuilder.Place(logRoot, new Vector2(0, 0), new Vector2(48, 214), new Vector2(760, 200));
        VerticalLayoutGroup lv = UIBuilder.VLayout(logRoot.gameObject, 4);
        lv.childAlignment = TextAnchor.LowerLeft;

        GameSettings.Changed += ApplyCrosshairColor;
        ApplyCrosshairColor();
    }

    void OnDestroy() {
        GameSettings.Changed -= ApplyCrosshairColor;
    }

    // ---------------------------------------------------------------- compass + clock

    private void BuildCompass(RectTransform root) {
        clock = UIBuilder.Text(root, "Clock", "10:00", UITheme.CondBold, 40, UITheme.Ink, TextAlignmentOptions.Center);
        UIBuilder.Place(clock.rectTransform, new Vector2(0.5f, 1), new Vector2(0, -22), new Vector2(240, 44));
        clock.characterSpacing = 4;
        Shadow(clock);

        RectTransform strip = UIBuilder.Rect("Compass", root);
        UIBuilder.Place(strip, new Vector2(0.5f, 1), new Vector2(0, -74), new Vector2(CompassWidth, 34));
        strip.gameObject.AddComponent<RectMask2D>();
        compassContent = UIBuilder.Rect("Ticks", strip);
        compassContent.anchorMin = compassContent.anchorMax = new Vector2(0.5f, 0.5f);
        compassContent.pivot = new Vector2(0.5f, 0.5f);
        compassContent.sizeDelta = Vector2.zero;
        float pxPerDeg = CompassWidth / CompassSpan;
        string[] names = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
        for (int d = -180; d <= 540; d += 15) {
            int a = ((d % 360) + 360) % 360;
            bool cardinal = a % 45 == 0;
            string label = cardinal ? names[a / 45] : a.ToString();
            bool major = cardinal && label.Length == 1;
            TextMeshProUGUI t = UIBuilder.Text(compassContent, "T" + d, label,
                major ? UITheme.CondBold : UITheme.CondMedium, major ? 26 : (cardinal ? 21 : 17),
                UITheme.WithAlpha(UITheme.Ink, major ? 1f : (cardinal ? 0.85f : 0.4f)), TextAlignmentOptions.Center);
            UIBuilder.Place(t.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(d * pxPerDeg, 0), new Vector2(60, 34));
            t.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            Shadow(t);
        }
        Image needle = UIBuilder.Image(root, "Needle", UITheme.Amber);
        UIBuilder.Place(needle.rectTransform, new Vector2(0.5f, 1), new Vector2(0, -70), new Vector2(2, 10));
        bearing = UIBuilder.Text(root, "Bearing", "000", UITheme.CondBold, 22, UITheme.Amber, TextAlignmentOptions.Center);
        UIBuilder.Place(bearing.rectTransform, new Vector2(0.5f, 1), new Vector2(0, -110), new Vector2(120, 28));
        bearing.characterSpacing = 6;
        Shadow(bearing);
    }

    // ---------------------------------------------------------------- crosshair, hitmarker, damage direction

    private void BuildCrosshair(RectTransform root) {
        crosshairRoot = UIBuilder.Rect("Crosshair", root);
        UIBuilder.Place(crosshairRoot, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        crosshairRoot.pivot = new Vector2(0.5f, 0.5f);
        crosshairGroup = UIBuilder.Group(crosshairRoot.gameObject);
        crosshair = new Image[5];
        for (int i = 0; i < 4; i++) {
            bool horizontal = i < 2;
            crosshair[i] = UIBuilder.Image(crosshairRoot, "Bar", Color.white);
            crosshair[i].rectTransform.sizeDelta = horizontal ? new Vector2(16, 3) : new Vector2(3, 16);
            crosshair[i].gameObject.AddComponent<Shadow>().effectColor = new Color(0, 0, 0, 0.8f);
        }
        crosshair[4] = UIBuilder.Image(crosshairRoot, "Dot", Color.white);
        crosshair[4].rectTransform.sizeDelta = new Vector2(3, 3);

        hitRoot = UIBuilder.Rect("Hitmarker", root);
        UIBuilder.Place(hitRoot, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        hitRoot.pivot = new Vector2(0.5f, 0.5f);
        hitBars = new Image[4];
        for (int i = 0; i < 4; i++) {
            float ang = 45f + 90f * i;
            hitBars[i] = UIBuilder.Image(hitRoot, "Hit", new Color(1, 1, 1, 0));
            RectTransform rt = hitBars[i].rectTransform;
            rt.sizeDelta = new Vector2(16, 3);
            rt.localRotation = Quaternion.Euler(0, 0, ang);
            rt.anchoredPosition = Quaternion.Euler(0, 0, ang) * new Vector3(20, 0, 0);
            hitBars[i].gameObject.AddComponent<Shadow>().effectColor = new Color(0, 0, 0, 0.8f);
        }

        for (int i = 0; i < 4; i++) {
            Image arc = UIBuilder.Image(root, "DamageDir", UITheme.WithAlpha(UITheme.Red, 0f));
            arc.sprite = arcSprite;
            UIBuilder.Place(arc.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(460, 460));
            arc.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            dirIndicators.Add(new DirIndicator { Image = arc });
        }
    }

    private void ApplyCrosshairColor() {
        if (crosshair == null) return;
        foreach (Image i in crosshair) i.color = GameSettings.CrosshairColor;
    }

    /// <summary>Kick the crosshair open a little; called per shot.</summary>
    public void Shot() {
        spread = Mathf.Min(spread + 6f, 22f);
    }

    public void Hitmarker(bool headshot, bool kill) {
        Color c = kill ? UITheme.Red : (headshot ? UITheme.Amber : Color.white);
        foreach (Image b in hitBars) b.color = c;
        hitTimer = kill ? 0.45f : 0.22f;
        hitScale = kill ? 1.6f : (headshot ? 1.3f : 1f);
        if (kill) UISounds.Kill(); else UISounds.Hit(headshot);
    }

    public void DamageFrom(Vector3 worldPosition) {
        DirIndicator slot = dirIndicators[0];
        foreach (DirIndicator d in dirIndicators) {
            if (d.Life < slot.Life) slot = d;
        }
        slot.Source = worldPosition;
        slot.Life = 1.6f;
        Flash();
    }

    public void Flash() {
        damageFlash.color = UITheme.WithAlpha(UITheme.Red, 0.22f);
    }

    // ---------------------------------------------------------------- vitals / weapon panels

    private void BuildVitals(RectTransform root) {
        Image panel = UIBuilder.Panel(root, "Vitals");
        UIBuilder.Place(panel.rectTransform, new Vector2(0, 0), new Vector2(48, 48), new Vector2(420, 150));
        UIBuilder.Brackets(panel.rectTransform);
        TextMeshProUGUI label = UIBuilder.Label(panel.transform, "체력");
        UIBuilder.Place(label.rectTransform, new Vector2(0, 1), new Vector2(24, -16), new Vector2(200, 24));
        callsignText = UIBuilder.Label(panel.transform, "", 16, TextAlignmentOptions.Right);
        UIBuilder.Place(callsignText.rectTransform, new Vector2(1, 1), new Vector2(-24, -16), new Vector2(240, 24));
        callsignText.fontStyle = FontStyles.Normal;
        stanceText = UIBuilder.Text(panel.transform, "Stance", "", UITheme.CondMedium, 17, UITheme.Mute, TextAlignmentOptions.Left);
        UIBuilder.Place(stanceText.rectTransform, new Vector2(0, 1), new Vector2(0, 30), new Vector2(300, 26));
        hpText = UIBuilder.Text(panel.transform, "HP", "100", UITheme.CondBold, 72, UITheme.Ink);
        UIBuilder.Place(hpText.rectTransform, new Vector2(0, 1), new Vector2(22, -38), new Vector2(360, 70));
        RectTransform segRoot = UIBuilder.Rect("Segments", panel.transform);
        UIBuilder.Place(segRoot, new Vector2(0, 0), new Vector2(24, 20), new Vector2(372, 12));
        HorizontalLayoutGroup h = UIBuilder.HLayout(segRoot.gameObject, 5);
        h.childForceExpandWidth = true;
        segments = new Image[SegmentCount];
        for (int i = 0; i < SegmentCount; i++) {
            segments[i] = UIBuilder.Image(segRoot, "Seg", UITheme.Ink);
        }
    }

    private void BuildWeapon(RectTransform root) {
        Image panel = UIBuilder.Panel(root, "Weapon");
        UIBuilder.Place(panel.rectTransform, new Vector2(1, 0), new Vector2(-48, 48), new Vector2(400, 150));
        UIBuilder.Brackets(panel.rectTransform);
        modeText = UIBuilder.Label(panel.transform, "AUTO");
        UIBuilder.Place(modeText.rectTransform, new Vector2(0, 1), new Vector2(24, -16), new Vector2(160, 24));
        slotText = UIBuilder.Text(panel.transform, "Slots", "", UITheme.CondMedium, 17, UITheme.Mute, TextAlignmentOptions.Right);
        UIBuilder.Place(slotText.rectTransform, new Vector2(1, 1), new Vector2(0, 30), new Vector2(400, 26));
        weaponText = UIBuilder.Text(panel.transform, "Name", "AK-47", UITheme.CondBold, 32, UITheme.Ink,
                                    TextAlignmentOptions.Right);
        weaponText.characterSpacing = 6;
        UIBuilder.Place(weaponText.rectTransform, new Vector2(1, 1), new Vector2(-24, -8), new Vector2(240, 36));
        reserveText = UIBuilder.Text(panel.transform, "Reserve", "/ 90", UITheme.Cond, 34, UITheme.Mute,
                                     TextAlignmentOptions.BottomRight);
        UIBuilder.Place(reserveText.rectTransform, new Vector2(1, 1), new Vector2(-24, -62), new Vector2(110, 46));
        ammoText = UIBuilder.Text(panel.transform, "Ammo", "30", UITheme.CondBold, 84, UITheme.Ink,
                                  TextAlignmentOptions.BottomRight);
        UIBuilder.Place(ammoText.rectTransform, new Vector2(1, 1), new Vector2(-138, -40), new Vector2(200, 76));
        reloadText = UIBuilder.Text(panel.transform, "Reload", "", UITheme.CondMedium, 18, UITheme.Amber);
        reloadText.characterSpacing = 8;
        UIBuilder.Place(reloadText.rectTransform, new Vector2(0, 1), new Vector2(24, -52), new Vector2(200, 24));

        RectTransform mag = UIBuilder.Rect("Mag", panel.transform);
        UIBuilder.Place(mag, new Vector2(1, 0), new Vector2(-24, 20), new Vector2(MagPipCount * 9f, 14));
        HorizontalLayoutGroup h = UIBuilder.HLayout(mag.gameObject, 3);
        h.childForceExpandWidth = true;
        magPips = new Image[MagPipCount];
        for (int i = 0; i < MagPipCount; i++) {
            magPips[i] = UIBuilder.Image(mag, "Pip", UITheme.Ink);
        }
    }

    /// <summary>The gun in hand (and the one on the back, for the 1/2 hint).</summary>
    public void SetWeapon(WeaponSpec spec, OpticId optic, WeaponSpec other) {
        weaponText.text = L10n.T(spec.Name);
        modeText.text = L10n.T(spec.Pellets > 1 ? "PUMP" : spec.Automatic ? "AUTO" : spec.Interval >= 1f ? "BOLT" : "SEMI");
        string sight = optic != OpticId.Iron ? "  ·  " + Weapons.OpticName(optic) : "";
        bool primary = Weapons.IsPrimary(spec.Id);
        string one = primary ? spec.Name + sight : (other != null ? other.Name : "");
        string two = primary ? (other != null ? other.Name : "") : spec.Name;
        slotText.text = (primary ? "<color=#ECEAE3>[1] " + one + "</color>" : "[1] " + one) + "     " +
                        (primary ? "[2] " + two : "<color=#ECEAE3>[2] " + two + "</color>");
        baseSpread = spec.HipSpread * 3f + (spec.Pellets > 1 ? 10f : 0f);
    }

    /// <summary>
    /// A grenade in hand: the weapon panel names it and counts what is left; <paramref name="fuse"/> is the cooking
    /// frag's time to the bang (negative: pin still in).
    /// </summary>
    public void SetThrowable(GrenadeSpec spec, int left, float fuse) {
        weaponText.text = L10n.T(spec.Name);
        modeText.text = fuse >= 0f ? L10n.T("핀 제거") : L10n.T("투척");
        ammoText.text = left.ToString();
        ammoText.color = UITheme.Ink;
        reserveText.text = "";
        foreach (Image pip in magPips) pip.gameObject.SetActive(false);
        reloadText.color = UITheme.Amber;
        reloadText.text = fuse >= 0f ? "<size=130%>" + fuse.ToString("F1") + L10n.T("</size>  초") : L10n.T("[좌] 멀리  [우] 가까이");
        baseSpread = 4f;
    }

    /// <summary>Aiming down the sights: hide the hip crosshair, show the red dot or a scope's view.</summary>
    /// <param name="sightModel">The gun has real sights to look through; no helper dot for iron sights then.</param>
    public void SetAim(float aim, OpticId optic, bool scoped, bool sightModel = false) {
        crosshairGroup.alpha = 1f - Mathf.Clamp01(aim * 2.5f);
        bool dot = optic == OpticId.RedDot || (optic == OpticId.Iron && !sightModel);
        redDot.gameObject.SetActive(dot && aim > 0.85f);
        redDot.color = optic == OpticId.RedDot ? new Color(1f, 0.15f, 0.1f, 0.95f) : UITheme.WithAlpha(GameSettings.CrosshairColor, 0.8f);
        redDot.rectTransform.sizeDelta = optic == OpticId.RedDot ? new Vector2(9, 9) : new Vector2(3, 3);
        redDotGlow.enabled = optic == OpticId.RedDot;
        scopeGroup.alpha = scoped ? 1f : 0f;
        scope4x.gameObject.SetActive(optic == OpticId.Scope4x);
        scope8x.gameObject.SetActive(optic == OpticId.Scope8x);
    }

    // ---------------------------------------------------------------- optics

    private void BuildScope(RectTransform root) {
        redDot = UIBuilder.Image(root, "RedDot", Color.red);
        redDot.sprite = DotSprite();
        UIBuilder.Place(redDot.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(7, 7));
        redDot.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        // The emitter's dot blooms a little on the glass.
        redDotGlow = UIBuilder.Image(redDot.rectTransform, "Glow", new Color(1f, 0.2f, 0.1f, 0.22f));
        redDotGlow.sprite = redDot.sprite;
        UIBuilder.Place(redDotGlow.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(26, 26));
        redDotGlow.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        redDotGlow.raycastTarget = false;
        redDot.gameObject.SetActive(false);

        // The eyepiece: a square, as tall as the screen, black outside the lens; black bars fill the sides.
        RectTransform scopeRoot = UIBuilder.Fill(UIBuilder.Rect("Scope", root));
        scopeGroup = UIBuilder.Group(scopeRoot.gameObject);
        scopeGroup.alpha = 0f;
        Image lens = UIBuilder.Image(scopeRoot, "Eyepiece", Color.black);
        lens.sprite = EyepieceSprite();
        RectTransform lr = lens.rectTransform;
        lr.anchorMin = new Vector2(0.5f, 0f);
        lr.anchorMax = new Vector2(0.5f, 1f);
        lr.pivot = new Vector2(0.5f, 0.5f);
        lr.sizeDelta = Vector2.zero;
        AspectRatioFitter fit = lens.gameObject.AddComponent<AspectRatioFitter>();
        fit.aspectMode = AspectRatioFitter.AspectMode.HeightControlsWidth;
        fit.aspectRatio = 1f;
        foreach (float side in new[] { 0f, 1f }) {
            Image bar = UIBuilder.Image(lr, "Side", Color.black);
            RectTransform br = bar.rectTransform;
            br.anchorMin = new Vector2(side, 0f);
            br.anchorMax = new Vector2(side, 1f);
            br.pivot = new Vector2(1f - side, 0.5f);
            br.sizeDelta = new Vector2(4000f, 0f);
            br.anchoredPosition = Vector2.zero;
        }
        Color ink = new Color(0f, 0f, 0f, 0.9f);
        scope4x = UIBuilder.Rect("4x", lr);
        UIBuilder.Fill(scope4x);
        // ACOG-style: posts from the edges, an illuminated chevron with its tip on the point of aim.
        Post(scope4x, new Vector2(0f, 0.5f), new Vector2(0.42f, 0.5f), 3f, ink);
        Post(scope4x, new Vector2(0.58f, 0.5f), new Vector2(1f, 0.5f), 3f, ink);
        Post(scope4x, new Vector2(0.5f, 0f), new Vector2(0.5f, 0.42f), 3f, ink);
        Color glow = new Color(1f, 0.2f, 0.1f, 0.95f);
        foreach (float sign in new[] { -1f, 1f }) {
            Image arm = UIBuilder.Image(scope4x, "Chevron", glow);
            RectTransform ar = arm.rectTransform;
            ar.anchorMin = ar.anchorMax = new Vector2(0.5f, 0.5f);
            ar.pivot = new Vector2(0.5f, 1f);
            ar.sizeDelta = new Vector2(3f, 26f);
            ar.anchoredPosition = Vector2.zero;
            ar.localRotation = Quaternion.Euler(0, 0, sign * 35f);
        }
        scope8x = UIBuilder.Rect("8x", lr);
        UIBuilder.Fill(scope8x);
        // Fine crosshair with thick outer posts and a centre dot.
        Post(scope8x, new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), 1.5f, ink);
        Post(scope8x, new Vector2(0.5f, 0f), new Vector2(0.5f, 1f), 1.5f, ink);
        Post(scope8x, new Vector2(0f, 0.5f), new Vector2(0.3f, 0.5f), 6f, ink);
        Post(scope8x, new Vector2(0.7f, 0.5f), new Vector2(1f, 0.5f), 6f, ink);
        Post(scope8x, new Vector2(0.5f, 0f), new Vector2(0.5f, 0.3f), 6f, ink);
        Image centre = UIBuilder.Image(scope8x, "Dot", glow);
        centre.sprite = DotSprite();
        UIBuilder.Place(centre.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(5, 5));
        centre.rectTransform.pivot = new Vector2(0.5f, 0.5f);
    }

    /// <summary>A reticle line from <paramref name="a"/> to <paramref name="b"/> (anchor space), horizontal or vertical.</summary>
    private static void Post(RectTransform parent, Vector2 a, Vector2 b, float width, Color color) {
        Image img = UIBuilder.Image(parent, "Post", color);
        RectTransform rt = img.rectTransform;
        rt.anchorMin = Vector2.Min(a, b);
        rt.anchorMax = Vector2.Max(a, b);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = a.y == b.y ? new Vector2(0f, width) : new Vector2(width, 0f);
        rt.anchoredPosition = Vector2.zero;
    }

    private static Sprite DotSprite() {
        const int n = 32;
        Texture2D tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        for (int y = 0; y < n; y++) {
            for (int x = 0; x < n; x++) {
                float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                float r = Mathf.Sqrt(dx * dx + dy * dy);
                tex.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01((1f - r) * 6f)));
            }
        }
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f));
    }

    /// <summary>Opaque outside a circle, with a soft dark rim just inside it, like looking into an eyepiece.</summary>
    private static Sprite EyepieceSprite() {
        const int n = 512;
        Texture2D tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        for (int y = 0; y < n; y++) {
            for (int x = 0; x < n; x++) {
                float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                float r = Mathf.Sqrt(dx * dx + dy * dy);
                float a = r >= 0.92f ? 1f : Mathf.SmoothStep(0f, 0.85f, Mathf.InverseLerp(0.72f, 0.92f, r));
                tex.SetPixel(x, y, new Color(1, 1, 1, a));
            }
        }
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f));
    }

    public void SetCallsign(string name) {
        callsignText.text = name;
    }

    /// <summary>Crouched or lying down shows above the health panel; standing shows nothing.</summary>
    public void SetStance(Stance stance) {
        stanceText.text = stance == Stance.Crouch ? L10n.T("앉기  <color=#ECEAE3>[C]</color>")
                        : stance == Stance.Prone ? L10n.T("엎드리기  <color=#ECEAE3>[Z]</color>") : "";
    }

    public void SetHealth(int current, int max) {
        health = Mathf.Max(0, current);
        maxHealth = Mathf.Max(1, max);
        hpText.text = health + "<size=40%><color=#8E918A>  / " + maxHealth + "</color></size>";
        int lit = Mathf.CeilToInt(SegmentCount * health / (float)maxHealth);
        bool low = health <= maxHealth * 0.3f;
        for (int i = 0; i < SegmentCount; i++) {
            segments[i].color = i < lit ? (low ? UITheme.Red : UITheme.Ink) : new Color(0.925f, 0.918f, 0.89f, 0.12f);
        }
        hpText.color = low ? UITheme.Red : UITheme.Ink;
    }

    public void SetAmmo(int inMag, int magSize, int reserve, bool reloading) {
        ammoText.text = inMag.ToString();
        reserveText.text = "/ " + reserve;
        bool low = inMag <= magSize / 4;
        ammoText.color = inMag == 0 ? UITheme.Red : (low ? UITheme.Amber : UITheme.Ink);
        int shown = Mathf.Min(magSize, MagPipCount);
        for (int i = 0; i < MagPipCount; i++) {
            magPips[i].gameObject.SetActive(i < shown);
            magPips[i].color = i < inMag ? UITheme.Ink : new Color(0.925f, 0.918f, 0.89f, 0.16f);
        }
        if (reloading) reloadText.text = L10n.T("재장전 중");
        else if (inMag == 0 && reserve == 0) reloadText.text = L10n.T("탄약 없음");
        else if (low) reloadText.text = L10n.T("[R] 재장전");
        else reloadText.text = "";
        reloadText.color = inMag == 0 && reserve == 0 ? UITheme.Red : UITheme.Amber;
    }

    // ---------------------------------------------------------------- kill feed + log

    private void BuildFeed(RectTransform root) {
        feedRoot = UIBuilder.Rect("Feed", root);
        UIBuilder.Place(feedRoot, new Vector2(1, 1), new Vector2(-48, -36), new Vector2(720, 300));
        VerticalLayoutGroup v = UIBuilder.VLayout(feedRoot.gameObject, 6);
        v.childAlignment = TextAnchor.UpperRight;
        v.childControlWidth = true;
        v.childForceExpandWidth = false;
    }

    public void AddKill(string killer, string victim, string weapon, bool headshot, bool killerIsMe, bool victimIsMe) {
        weapon = L10n.T(weapon);
        bool mine = killerIsMe || victimIsMe;
        Image bg = UIBuilder.Image(feedRoot, "Kill", mine ? UITheme.AmberWash : new Color(0.04f, 0.047f, 0.04f, 0.6f));
        bg.transform.SetAsFirstSibling();
        HorizontalLayoutGroup h = UIBuilder.HLayout(bg.gameObject, 0, new RectOffset(16, 16, 7, 7));
        h.childForceExpandHeight = false;
        if (mine) {
            Image edge = UIBuilder.Image(bg.transform, "Edge", UITheme.Amber);
            edge.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            edge.rectTransform.anchorMin = Vector2.zero;
            edge.rectTransform.anchorMax = new Vector2(0, 1);
            edge.rectTransform.pivot = new Vector2(0, 0.5f);
            edge.rectTransform.sizeDelta = new Vector2(3, 0);
        }
        string k = killerIsMe ? "<color=#E3A72F><b>" + UITheme.Safe(killer) + "</b></color>" : UITheme.Safe(killer);
        string vName = victimIsMe ? "<color=#E3A72F><b>" + UITheme.Safe(victim) + "</b></color>"
                                  : "<color=#E2867A>" + UITheme.Safe(victim) + "</color>";
        // The condensed font has no Hangul ("수류탄"): Korean names stay in the body font.
        bool latin = System.Linq.Enumerable.All(weapon, ch => ch < 0x1100);
        string w = (latin ? "<font=\"Cond-Medium\">" : "") + "<size=78%><color=#8E918A>  " + weapon + (headshot ? " · HS" : "") + "  </color></size>" +
                   (latin ? "</font>" : "");
        TextMeshProUGUI t = UIBuilder.Text(bg.transform, "Text", k + w + vName, UITheme.Body, 22, UITheme.Ink);
        feed.Add(new FadeLine { Text = t, Group = UIBuilder.Group(bg.gameObject), Life = FeedLifetime });
        while (feed.Count > 5) {
            Destroy(feed[0].Text.transform.parent.gameObject);
            feed.RemoveAt(0);
        }
    }

    public void AddMessage(string richText) {
        TextMeshProUGUI t = UIBuilder.Text(logRoot, "Line", richText, UITheme.Body, 20, UITheme.Ink);
        Shadow(t);
        UIBuilder.Size(t, height: 28);
        log.Add(new FadeLine { Text = t, Group = UIBuilder.Group(t.gameObject), Life = LogLifetime });
        while (log.Count > 6) {
            Destroy(log[0].Text.gameObject);
            log.RemoveAt(0);
        }
    }

    // ---------------------------------------------------------------- per-frame

    void Update() {
        float dt = Time.deltaTime;

        damageFlash.color = Color.Lerp(damageFlash.color, UITheme.WithAlpha(UITheme.Red, 0f), 3f * dt);
        float lowTarget = combat.alpha > 0f && health > 0 ? Mathf.Clamp01(1f - health / (maxHealth * 0.4f)) * 0.45f : 0f;
        lowHealth.color = UITheme.WithAlpha(UITheme.Red, Mathf.Lerp(lowHealth.color.a, lowTarget, 4f * dt));

        spread = Mathf.Lerp(spread, 0f, 8f * dt);
        float gap = 7f + baseSpread + spread;
        crosshair[0].rectTransform.anchoredPosition = new Vector2(-gap - 8, 0);
        crosshair[1].rectTransform.anchoredPosition = new Vector2(gap + 8, 0);
        crosshair[2].rectTransform.anchoredPosition = new Vector2(0, gap + 8);
        crosshair[3].rectTransform.anchoredPosition = new Vector2(0, -gap - 8);

        hitTimer -= dt;
        float ha = Mathf.Clamp01(hitTimer / 0.12f);
        hitRoot.localScale = Vector3.one * Mathf.Lerp(1f, hitScale, ha);
        foreach (Image b in hitBars) b.color = UITheme.WithAlpha(b.color, ha);

        Transform cam = Camera.main != null ? Camera.main.transform : null;
        if (cam != null) {
            float yaw = cam.eulerAngles.y;
            compassContent.anchoredPosition = new Vector2(-yaw * CompassWidth / CompassSpan, 0);
            bearing.text = Mathf.RoundToInt(yaw % 360f).ToString("000");
        }
        foreach (DirIndicator d in dirIndicators) {
            d.Life -= dt;
            float a = Mathf.Clamp01(d.Life / 0.6f) * 0.85f;
            d.Image.color = UITheme.WithAlpha(UITheme.Red, a);
            if (a > 0f && cam != null) {
                Vector3 to = d.Source - cam.position;
                float angle = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg - cam.eulerAngles.y;
                d.Image.rectTransform.localRotation = Quaternion.Euler(0, 0, -angle);
            }
        }

        Fade(feed, dt, true);
        Fade(log, dt, false);

        statsTimer -= Time.unscaledDeltaTime;
        if (statsTimer <= 0f) {
            statsTimer = 0.5f;
            RefreshStats();
        }
    }

    private void RefreshStats() {
        MatchManager mm = MatchManager.Instance;
        float left = mm != null ? mm.TimeLeft : MatchManager.MatchLength;
        clock.text = string.Format("{0:00}:{1:00}", Mathf.FloorToInt(left / 60f), Mathf.FloorToInt(left % 60f));
        clock.color = left <= 60f ? UITheme.Amber : UITheme.Ink;
        NetPlayer me = NetPlayer.Local;
        if (me == null) return;
        int rank = 1;
        foreach (NetPlayer p in NetPlayer.All) {
            if (p != me && p.Score.Value > me.Score.Value) rank++;
        }
        statsLine.text = L10n.T("KILLS") + " <font=\"Cond-Bold\"><size=150%>" + me.Kills.Value +
                         "</size></font>    " + L10n.T("DEATHS") + " <font=\"Cond-Bold\"><size=150%>" + me.Deaths.Value +
                         "</size></font>    " + L10n.T("RANK") + " <font=\"Cond-Bold\"><size=150%>" + rank +
                         "</size></font><color=#8E918A> / " + NetPlayer.All.Count + "</color>";
    }

    private static void Fade(List<FadeLine> lines, float dt, bool feedLine) {
        for (int i = lines.Count - 1; i >= 0; i--) {
            FadeLine f = lines[i];
            f.Life -= dt;
            f.Group.alpha = Mathf.Clamp01(f.Life / 1.2f);
            if (f.Life <= 0f) {
                Destroy(feedLine ? f.Text.transform.parent.gameObject : f.Text.gameObject);
                lines.RemoveAt(i);
            }
        }
    }

    private static void Shadow(Graphic g) {
        Shadow s = g.gameObject.AddComponent<Shadow>();
        s.effectColor = new Color(0, 0, 0, 0.85f);
        s.effectDistance = new Vector2(1.5f, -1.5f);
    }

    private static Sprite VignetteSprite() {
        const int n = 128;
        Texture2D tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        for (int y = 0; y < n; y++) {
            for (int x = 0; x < n; x++) {
                float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                float d = Mathf.Sqrt(dx * dx + dy * dy) / 1.41421f;
                tex.SetPixel(x, y, new Color(1, 1, 1, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.6f, 1f, d))));
            }
        }
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f));
    }

    /// <summary>A soft ring segment pointing up; rotated toward the damage source.</summary>
    private static Sprite ArcSprite() {
        const int n = 256;
        Texture2D tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        for (int y = 0; y < n; y++) {
            for (int x = 0; x < n; x++) {
                float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                float r = Mathf.Sqrt(dx * dx + dy * dy);
                float ang = Mathf.Abs(Mathf.Atan2(dx, dy) * Mathf.Rad2Deg);
                float ring = Mathf.Clamp01(1f - Mathf.Abs(r - 0.86f) / 0.09f);
                float sweep = Mathf.Clamp01(1f - ang / 28f);
                tex.SetPixel(x, y, new Color(1, 1, 1, ring * sweep * sweep));
            }
        }
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f));
    }

    private class DirIndicator {
        public Image Image;
        public Vector3 Source;
        public float Life;
    }

    private class FadeLine {
        public TextMeshProUGUI Text;
        public CanvasGroup Group;
        public float Life;
    }

}
