using System;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

/// <summary>
/// Small helpers for building uGUI hierarchies from code. All sizes are in
/// reference pixels of a 1920x1080 canvas.
/// </summary>
public static class UIBuilder {

    public static RectTransform Rect(string name, Transform parent) {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.layer = LayerMask.NameToLayer("UI");
        RectTransform rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        return rt;
    }

    /// <summary>Stretch to fill the parent, with optional insets (left, bottom, right, top).</summary>
    public static RectTransform Fill(RectTransform rt, float l = 0, float b = 0, float r = 0, float t = 0) {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(l, b);
        rt.offsetMax = new Vector2(-r, -t);
        return rt;
    }

    /// <summary>Anchor to a single point of the parent (and use it as pivot), then offset and size.</summary>
    public static RectTransform Place(RectTransform rt, Vector2 anchor, Vector2 pos, Vector2 size) {
        rt.anchorMin = anchor;
        rt.anchorMax = anchor;
        rt.pivot = anchor;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        return rt;
    }

    public static Image Image(Transform parent, string name, Color color, bool raycast = false) {
        RectTransform rt = Rect(name, parent);
        Image img = rt.gameObject.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = raycast;
        return img;
    }

    /// <summary>A translucent panel with a hairline border.</summary>
    public static Image Panel(Transform parent, string name, Color? color = null, bool border = true) {
        Image img = Image(parent, name, color ?? UITheme.Panel, true);
        if (border) {
            UIBorder.Add(img.rectTransform, UITheme.Line);
        }
        return img;
    }

    /// <summary>Amber corner brackets (top-left and bottom-right), the "tactical" accent.</summary>
    public static void Brackets(RectTransform target, float size = 18, float thickness = 2) {
        Color c = UITheme.Amber;
        Bar(target, new Vector2(0, 1), new Vector2(0, 0), new Vector2(size, thickness), c);
        Bar(target, new Vector2(0, 1), new Vector2(0, 0), new Vector2(thickness, size), c);
        Bar(target, new Vector2(1, 0), new Vector2(0, 0), new Vector2(size, thickness), c);
        Bar(target, new Vector2(1, 0), new Vector2(0, 0), new Vector2(thickness, size), c);
    }

    private static void Bar(RectTransform parent, Vector2 anchor, Vector2 pos, Vector2 size, Color c) {
        Image img = Image(parent, "Bracket", c);
        Place(img.rectTransform, anchor, pos, size);
        img.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
    }

    public static TextMeshProUGUI Text(Transform parent, string name, string text, TMP_FontAsset font, float size,
                                       Color color, TextAlignmentOptions align = TextAlignmentOptions.Left, bool localize = true) {
        RectTransform rt = Rect(name, parent);
        TextMeshProUGUI t = rt.gameObject.AddComponent<TextMeshProUGUI>();
        t.font = font;
        t.fontSize = size;
        t.color = color;
        t.alignment = align;
        t.text = text;
        if (localize) LocalizedLabel.Bind(t, text);
        t.raycastTarget = false;
        t.enableWordWrapping = false;
        t.overflowMode = TextOverflowModes.Overflow;
        return t;
    }

    /// <summary>Small upper-case, letter-spaced label in the muted colour.</summary>
    public static TextMeshProUGUI Label(Transform parent, string text, float size = 16,
                                        TextAlignmentOptions align = TextAlignmentOptions.Left) {
        TextMeshProUGUI t = Text(parent, "Label", text, UITheme.CondMedium, size, UITheme.Mute, align);
        t.characterSpacing = 10;
        t.fontStyle = FontStyles.UpperCase;
        return t;
    }

    public static Button Button(Transform parent, string label, string hint, bool primary, UnityAction onClick,
                                float height = 64) {
        Image bg = Image(parent, "Button " + label, Color.white, true);
        if (!primary) {
            UIBorder.Add(bg.rectTransform, UITheme.Line2);
        }
        bg.gameObject.AddComponent<LayoutElement>().preferredHeight = height;
        Button b = bg.gameObject.AddComponent<Button>();
        // Tints multiply a white image, so each state colour is used as-is.
        ColorBlock cb = b.colors;
        if (primary) {
            cb.normalColor = UITheme.Amber;
            cb.highlightedColor = new Color(0.96f, 0.74f, 0.29f);
            cb.pressedColor = new Color(0.78f, 0.56f, 0.15f);
            cb.disabledColor = UITheme.WithAlpha(UITheme.Amber, 0.35f);
        } else {
            cb.normalColor = new Color(0f, 0f, 0f, 0.4f);
            cb.highlightedColor = new Color(0.89f, 0.655f, 0.184f, 0.22f);
            cb.pressedColor = new Color(0.89f, 0.655f, 0.184f, 0.38f);
            cb.disabledColor = new Color(0f, 0f, 0f, 0.25f);
        }
        cb.selectedColor = cb.normalColor;
        cb.colorMultiplier = 1f;
        cb.fadeDuration = 0.08f;
        b.colors = cb;
        b.targetGraphic = bg;
        if (onClick != null) {
            b.onClick.AddListener(onClick);
        }
        b.onClick.AddListener(UISounds.Click);

        Color ink = primary ? new Color(0.086f, 0.075f, 0.043f) : UITheme.Ink;
        TextMeshProUGUI t = Text(bg.transform, "Text", label, UITheme.CondBold, 30, ink);
        Fill(t.rectTransform, 22, 0, 22, 0);
        t.characterSpacing = 4;
        t.alignment = TextAlignmentOptions.MidlineLeft;
        if (!string.IsNullOrEmpty(hint)) {
            TextMeshProUGUI h = Text(bg.transform, "Hint", hint, UITheme.CondMedium, 16,
                                     UITheme.WithAlpha(ink, 0.75f), TextAlignmentOptions.MidlineRight);
            Fill(h.rectTransform, 22, 0, 22, 0);
            h.characterSpacing = 6;
        }
        return b;
    }

    /// <summary>Compact outlined amber button, e.g. "참가" in a table row.</summary>
    public static Button SmallButton(Transform parent, string label, UnityAction onClick) {
        Image bg = Image(parent, "Small " + label, Color.white, true);
        UIBorder.Add(bg.rectTransform, UITheme.WithAlpha(UITheme.Amber, 0.6f));
        Button b = bg.gameObject.AddComponent<Button>();
        b.targetGraphic = bg;
        ColorBlock cb = b.colors;
        cb.normalColor = new Color(0f, 0f, 0f, 0.01f);
        cb.highlightedColor = new Color(0.89f, 0.655f, 0.184f, 0.2f);
        cb.pressedColor = new Color(0.89f, 0.655f, 0.184f, 0.35f);
        cb.selectedColor = cb.normalColor;
        cb.disabledColor = new Color(0f, 0f, 0f, 0.01f);
        b.colors = cb;
        if (onClick != null) {
            b.onClick.AddListener(onClick);
        }
        b.onClick.AddListener(UISounds.Click);
        TextMeshProUGUI t = Text(bg.transform, "Text", label, UITheme.CondBold, 20, UITheme.Amber,
                                 TextAlignmentOptions.Center);
        Fill(t.rectTransform);
        t.characterSpacing = 6;
        return b;
    }

    public static TMP_InputField Input(Transform parent, string placeholder, int charLimit, float fontSize = 26) {
        Image bg = Image(parent, "Input", Color.white, true);
        UIBorder.Add(bg.rectTransform, UITheme.Line2);
        bg.gameObject.SetActive(false);

        RectTransform area = Fill(Rect("Text Area", bg.transform), 18, 6, 18, 6);
        area.gameObject.AddComponent<RectMask2D>();
        TextMeshProUGUI ph = Text(area, "Placeholder", placeholder, UITheme.Body, fontSize, UITheme.Dim);
        Fill(ph.rectTransform);
        ph.alignment = TextAlignmentOptions.MidlineLeft;
        ph.fontStyle = FontStyles.Normal;
        TextMeshProUGUI txt = Text(area, "Text", "", UITheme.Body, fontSize, UITheme.Ink);
        Fill(txt.rectTransform);
        txt.alignment = TextAlignmentOptions.MidlineLeft;

        TMP_InputField f = bg.gameObject.AddComponent<TMP_InputField>();
        f.textViewport = area;
        f.textComponent = txt;
        f.placeholder = ph;
        f.characterLimit = charLimit;
        f.caretColor = UITheme.Amber;
        f.customCaretColor = true;
        f.selectionColor = UITheme.WithAlpha(UITheme.Amber, 0.35f);
        f.targetGraphic = bg;
        f.fontAsset = UITheme.Body;
        f.pointSize = fontSize;
        ColorBlock cb = f.colors;
        cb.normalColor = UITheme.Field;
        cb.highlightedColor = new Color(0.1f, 0.1f, 0.08f, 0.6f);
        cb.selectedColor = new Color(0.12f, 0.1f, 0.05f, 0.7f);
        cb.pressedColor = cb.selectedColor;
        f.colors = cb;
        bg.gameObject.SetActive(true);
        return f;
    }

    public static Slider Slider(Transform parent, float min, float max, float value, bool whole,
                                Action<float> onChanged) {
        RectTransform root = Rect("Slider", parent);
        Slider s = root.gameObject.AddComponent<Slider>();

        Image track = Image(root, "Track", new Color(0.925f, 0.918f, 0.89f, 0.18f), true);
        RectTransform tr = track.rectTransform;
        tr.anchorMin = new Vector2(0, 0.5f);
        tr.anchorMax = new Vector2(1, 0.5f);
        tr.sizeDelta = new Vector2(0, 6);

        RectTransform fillArea = Rect("Fill Area", root);
        fillArea.anchorMin = new Vector2(0, 0.5f);
        fillArea.anchorMax = new Vector2(1, 0.5f);
        fillArea.sizeDelta = new Vector2(0, 6);
        Image fill = Image(fillArea, "Fill", UITheme.WithAlpha(UITheme.Amber, 0.75f));
        fill.rectTransform.sizeDelta = Vector2.zero;

        RectTransform handleArea = Fill(Rect("Handle Area", root), 12, 8, 12, 8);
        Image handle = Image(handleArea, "Handle", UITheme.Amber, true);
        handle.rectTransform.sizeDelta = new Vector2(16, 0);

        s.fillRect = fill.rectTransform;
        s.handleRect = handle.rectTransform;
        s.targetGraphic = handle;
        s.direction = UnityEngine.UI.Slider.Direction.LeftToRight;
        s.minValue = min;
        s.maxValue = max;
        s.wholeNumbers = whole;
        s.value = value;
        if (onChanged != null) {
            s.onValueChanged.AddListener(v => onChanged(v));
        }
        return s;
    }

    public static VerticalLayoutGroup VLayout(GameObject go, float spacing, RectOffset padding = null,
                                              bool controlHeight = true) {
        VerticalLayoutGroup v = go.AddComponent<VerticalLayoutGroup>();
        v.spacing = spacing;
        v.padding = padding ?? new RectOffset();
        v.childControlWidth = true;
        v.childControlHeight = controlHeight;
        v.childForceExpandWidth = true;
        v.childForceExpandHeight = false;
        return v;
    }

    public static HorizontalLayoutGroup HLayout(GameObject go, float spacing, RectOffset padding = null) {
        HorizontalLayoutGroup h = go.AddComponent<HorizontalLayoutGroup>();
        h.spacing = spacing;
        h.padding = padding ?? new RectOffset();
        h.childControlWidth = true;
        h.childControlHeight = true;
        h.childForceExpandWidth = false;
        h.childForceExpandHeight = true;
        return h;
    }

    public static LayoutElement Size(Component c, float width = -1, float height = -1, float flex = -1) {
        LayoutElement le = c.GetComponent<LayoutElement>();
        if (le == null) le = c.gameObject.AddComponent<LayoutElement>();
        if (width >= 0) le.preferredWidth = width;
        if (height >= 0) le.preferredHeight = height;
        if (flex >= 0) le.flexibleWidth = flex;
        return le;
    }

    public static CanvasGroup Group(GameObject go) {
        CanvasGroup g = go.GetComponent<CanvasGroup>();
        return g != null ? g : go.AddComponent<CanvasGroup>();
    }

}
