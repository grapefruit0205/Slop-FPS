using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Grenades on the HUD: a row of three slots over the weapon panel ("4 수류탄 1 · 5 섬광 1 · 6 연막 1", the one in hand
/// in amber, spent ones dimmed), the flashbang's whiteout, and the grey fog over the screen while standing in smoke.
/// The overlays sit under the rest of the HUD, so the crosshair and numbers stay readable through them.
/// </summary>
public class GrenadeHud : MonoBehaviour {

    private const float SlotWidth = 128f;

    private class Slot {
        public GrenadeId Id;
        public Image Bg, Edge;
        public TextMeshProUGUI Key, Name, Count;
    }

    private readonly Slot[] slots = new Slot[3];
    private Image whiteout, fog;
    private CanvasGroup rowGroup;
    private float flashHold, flashFade, flashFadeTime, flashPeak;

    /// <param name="screen">Root of the HUD: the overlays go here, behind the other HUD elements.</param>
    /// <param name="combat">The part of the HUD shown only while alive: the slots go here.</param>
    public static GrenadeHud Create(RectTransform screen, RectTransform combat) {
        GrenadeHud h = combat.gameObject.AddComponent<GrenadeHud>();
        h.Build(screen, combat);
        return h;
    }

    private void Build(RectTransform screen, RectTransform combat) {
        fog = UIBuilder.Image(screen, "SmokeFog", new Color(0.55f, 0.56f, 0.55f, 0f));
        UIBuilder.Fill(fog.rectTransform);
        fog.transform.SetSiblingIndex(0);
        whiteout = UIBuilder.Image(screen, "Whiteout", new Color(1f, 1f, 1f, 0f));
        UIBuilder.Fill(whiteout.rectTransform);
        whiteout.transform.SetSiblingIndex(1);

        // Over the weapon panel (48 from the corner, 150 tall) and its [1]/[2] line, clear of the controls hint's top
        // edge (268), which reaches this far right on 16:10 screens.
        RectTransform row = UIBuilder.Rect("Grenades", combat);
        UIBuilder.Place(row, new Vector2(1, 0), new Vector2(-48, 274), new Vector2(SlotWidth * 3 + 8, 34));
        rowGroup = UIBuilder.Group(row.gameObject);
        HorizontalLayoutGroup h = UIBuilder.HLayout(row.gameObject, 4);
        h.childAlignment = TextAnchor.MiddleRight;
        h.childForceExpandWidth = true;
        h.childForceExpandHeight = true;
        for (int i = 0; i < slots.Length; i++) slots[i] = BuildSlot(row, Grenades.All[i]);
        Refresh(null, GrenadeId.None);
    }

    private static Slot BuildSlot(RectTransform row, GrenadeId id) {
        GrenadeSpec spec = Grenades.Get(id);
        Slot s = new Slot { Id = id };
        s.Bg = UIBuilder.Image(row, "Slot " + id, UITheme.Panel);
        UIBorder.Add(s.Bg.rectTransform, UITheme.Line);
        s.Edge = UIBuilder.Image(s.Bg.transform, "Edge", UITheme.Amber);
        RectTransform er = s.Edge.rectTransform;
        er.anchorMin = new Vector2(0, 0);
        er.anchorMax = new Vector2(1, 0);
        er.pivot = new Vector2(0.5f, 0);
        er.sizeDelta = new Vector2(0, 2);
        s.Key = UIBuilder.Text(s.Bg.transform, "Key", ((int)spec.Key - (int)KeyCode.Alpha0).ToString(), UITheme.CondBold, 20, UITheme.Mute,
                               TextAlignmentOptions.Center);
        UIBuilder.Place(s.Key.rectTransform, new Vector2(0, 0.5f), new Vector2(6, 0), new Vector2(22, 30));
        s.Key.rectTransform.pivot = new Vector2(0, 0.5f);
        s.Name = UIBuilder.Text(s.Bg.transform, "Name", spec.Short, UITheme.Body, 16, UITheme.Ink, TextAlignmentOptions.Left);
        UIBuilder.Place(s.Name.rectTransform, new Vector2(0, 0.5f), new Vector2(32, 0), new Vector2(70, 30));
        s.Name.rectTransform.pivot = new Vector2(0, 0.5f);
        s.Count = UIBuilder.Text(s.Bg.transform, "Count", "1", UITheme.CondBold, 22, UITheme.Ink, TextAlignmentOptions.Right);
        UIBuilder.Place(s.Count.rectTransform, new Vector2(1, 0.5f), new Vector2(-10, 0), new Vector2(30, 30));
        s.Count.rectTransform.pivot = new Vector2(1, 0.5f);
        return s;
    }

    /// <summary>Counts from <paramref name="counts"/> (null: none carried) and the one in hand.</summary>
    public void Refresh(System.Func<GrenadeId, int> counts, GrenadeId inHand) {
        foreach (Slot s in slots) {
            int n = counts != null ? counts(s.Id) : 0;
            bool selected = s.Id == inHand && inHand != GrenadeId.None;
            bool empty = n <= 0;
            s.Edge.enabled = selected;
            s.Bg.color = selected ? UITheme.AmberWash : UITheme.Panel;
            s.Key.color = selected ? UITheme.Amber : empty ? UITheme.Dim : UITheme.Mute;
            s.Name.color = selected ? UITheme.Amber : empty ? UITheme.Dim : UITheme.Ink;
            s.Count.color = selected ? UITheme.Amber : empty ? UITheme.Dim : UITheme.Ink;
            s.Count.text = n.ToString();
        }
    }

    /// <summary>
    /// Blind the screen by <paramref name="strength"/> (0..1): fully white for a while when it was strong, then fading.
    /// A second flash only ever makes it worse. Returns the seconds of full white.
    /// </summary>
    public float Flash(float strength) {
        float hold = strength >= 0.5f ? Grenades.FlashMaxWhite * strength : 0f;
        float fade = 0.4f + Grenades.FlashFade * strength;
        float peak = Mathf.Clamp01(strength * 1.6f);
        if (peak >= CurrentWhite()) {
            flashPeak = peak;
            flashHold = Mathf.Max(flashHold, hold);
            flashFadeTime = fade;
            flashFade = fade;
        }
        return hold;
    }

    private float CurrentWhite() {
        if (flashHold > 0f) return flashPeak;
        return flashFadeTime > 0f ? flashPeak * Mathf.Clamp01(flashFade / flashFadeTime) : 0f;
    }

    /// <summary>Drop the overlays (death, leaving).</summary>
    public void ClearOverlays() {
        flashHold = flashFade = flashFadeTime = 0f;
        whiteout.color = new Color(1f, 1f, 1f, 0f);
        fog.color = UITheme.WithAlpha(fog.color, 0f);
    }

    void LateUpdate() {
        float dt = Time.deltaTime;
        // The pause menu's footer runs along the bottom where the slots are.
        GameUI ui = GameUI.Instance;
        rowGroup.alpha = ui != null && ui.Pause != null && ui.Pause.Visible ? 0f : 1f;
        if (flashHold > 0f) flashHold -= dt;
        else if (flashFade > 0f) flashFade -= dt;
        float white = CurrentWhite();
        // The tail of the fade lingers like an afterimage.
        whiteout.color = new Color(1f, 1f, 0.97f, Mathf.Pow(white, 0.8f));

        Transform view = GrenadeWorld.ViewOverride != null ? GrenadeWorld.ViewOverride : Camera.main != null ? Camera.main.transform : null;
        float smoke = view != null ? GrenadeWorld.SmokeAt(view.position) : 0f;
        Color f = fog.color;
        f.a = Mathf.MoveTowards(f.a, Mathf.Clamp01(smoke * 1.15f) * 0.97f, dt * 3f);
        fog.color = f;
    }

}
