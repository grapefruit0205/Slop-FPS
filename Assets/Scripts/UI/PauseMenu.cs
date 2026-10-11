using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Esc menu: resume, settings (sensitivity, invert Y, FOV, crosshair colour, volume),
/// graphics (quality preset, render scale, FPS counter), controls reference, and leaving the room.
/// </summary>
public class PauseMenu : MonoBehaviour {

    public event Action LeaveRequested;

    public bool Visible => gameObject.activeSelf;

    private const int SettingsPage = 0, GraphicsPage = 1, ControlsPage = 2;

    private GameObject[] pages;
    private TextMeshProUGUI[] navs;
    private Image[] swatches;
    private TextMeshProUGUI[] presetLabels;
    private Button[] presetButtons;
    private int shownPreset = -1;
    private Slider renderScale;
    private TextMeshProUGUI fpsState;

    public static PauseMenu Create(Transform parent) {
        RectTransform rt = UIBuilder.Fill(UIBuilder.Rect("Pause", parent));
        PauseMenu s = rt.gameObject.AddComponent<PauseMenu>();
        s.Build(rt);
        rt.gameObject.SetActive(false);
        return s;
    }

    public void SetVisible(bool v) {
        gameObject.SetActive(v);
        if (v) ShowPage(SettingsPage);
    }

    private void Build(RectTransform root) {
        Image bg = UIBuilder.Image(root, "Backdrop", new Color(0.024f, 0.03f, 0.024f, 0.82f), true);
        UIBuilder.Fill(bg.rectTransform);

        // Left navigation
        RectTransform nav = UIBuilder.Rect("Nav", root);
        nav.anchorMin = new Vector2(0, 0);
        nav.anchorMax = new Vector2(0, 1);
        nav.pivot = new Vector2(0, 1);
        nav.offsetMin = new Vector2(110, 120);
        nav.offsetMax = new Vector2(110 + 420, -110);
        UIBuilder.VLayout(nav.gameObject, 6);
        TextMeshProUGUI title = UIBuilder.Text(nav, "Title", "일시정지", UITheme.BodyBold, 60, UITheme.Ink);
        title.characterSpacing = 12;
        UIBuilder.Size(title, height: 110);
        NavItem(nav, "계속하기", () => SetVisible(false), UITheme.Mute);
        navs = new[] {
            NavItem(nav, "설정", () => ShowPage(SettingsPage), UITheme.Ink),
            NavItem(nav, "그래픽", () => ShowPage(GraphicsPage), UITheme.Mute),
            NavItem(nav, "조작키", () => ShowPage(ControlsPage), UITheme.Mute),
        };
        UIBuilder.Size(UIBuilder.Rect("Gap", nav), height: 40);
        NavItem(nav, "작전 이탈 (로비로)", () => { SetVisible(false); LeaveRequested?.Invoke(); }, new Color(0.79f, 0.42f, 0.36f));

        // Right content
        RectTransform content = UIBuilder.Rect("Content", root);
        content.anchorMin = new Vector2(0, 0);
        content.anchorMax = new Vector2(1, 1);
        content.offsetMin = new Vector2(110 + 420 + 60, 120);
        content.offsetMax = new Vector2(-160, -130);

        // The HUD's controls reminder, for whoever missed it after spawning (above the HUD panels showing through).
        TextMeshProUGUI hint = UIBuilder.Text(root, "ControlsHint", ControlsHint.Line(), UITheme.Body, 20, UITheme.Ink, TextAlignmentOptions.Left);
        LocalizedLabel.BindDynamic(hint, ControlsHint.Line);
        hint.rectTransform.anchorMin = new Vector2(0, 0);
        hint.rectTransform.anchorMax = new Vector2(1, 0);
        hint.rectTransform.pivot = new Vector2(0.5f, 0);
        hint.rectTransform.offsetMin = new Vector2(110 + 420 + 60, 232);
        hint.rectTransform.offsetMax = new Vector2(-160, 268);
        hint.enableAutoSizing = true;
        hint.fontSizeMin = 14;
        hint.fontSizeMax = 20;

        pages = new GameObject[3];
        RectTransform list = Page(content, SettingsPage, "Settings");

        LoadoutPicker.Create(list, true);
        Button language = UIBuilder.SmallButton(list, "", L10n.Cycle);
        UIBuilder.Size(language, height: 36);
        language.gameObject.AddComponent<LanguageButton>();
        Section(list, "조작");
        SliderRow(list, "마우스 감도", 0.2f, 10f, GameSettings.Sensitivity, false, "0.0", GameSettings.SetSensitivity);
        ToggleRow(list, "Y축 반전", GameSettings.InvertY, GameSettings.SetInvertY);
        Section(list, "화면");
        SliderRow(list, "시야각 (FOV)", 60f, 100f, GameSettings.FieldOfView, true, "0", GameSettings.SetFieldOfView);
        SwatchRow(list, "조준점 색상");
        Section(list, "사운드");
        SliderRow(list, "마스터 볼륨", 0f, 100f, GameSettings.MasterVolume * 100f, true, "0",
                  v => GameSettings.SetMasterVolume(v / 100f));

        BuildGraphics(Page(content, GraphicsPage, "Graphics"));

        RectTransform clist = Page(content, ControlsPage, "Controls");
        Section(clist, "조작키");
        // Two columns, so the whole list fits without scrolling.
        string[,] keys = {
            { "W A S D", "이동" }, { "Shift (누르고 있기)", "달리기" }, { "Space", "점프 · 일어서기" }, { "C", "앉기" },
            { "Z", "엎드리기" }, { "Q / E", "왼쪽 · 오른쪽 기울이기" }, { "마우스 왼쪽", "사격" }, { "마우스 오른쪽", "조준" },
            { "R", "재장전" }, { "1 / 2", "주무기 · 보조무기" },
            { "4 / 5 / 6 · G", "수류탄 · 섬광탄 · 연막탄" }, { "좌 / 우클릭", "멀리 던지기 · 가까이 굴리기" }, { "Tab", "스코어보드" }, { "F3", "FPS 표시" }, { "Esc / P", "일시정지" },
        };
        for (int i = 0; i < keys.GetLength(0); i += 2) {
            RectTransform row = Row(clist);
            for (int j = i; j < Mathf.Min(i + 2, keys.GetLength(0)); j++) {
                TextMeshProUGUI a = UIBuilder.Text(row, "Key", keys[j, 0], UITheme.CondBold, 26, UITheme.Amber);
                UIBuilder.Size(a, 250);
                a.characterSpacing = 4;
                TextMeshProUGUI b = UIBuilder.Text(row, "Action", keys[j, 1], UITheme.Body, 24, UITheme.Ink);
                UIBuilder.Size(b, 0, flex: 1);
            }
            if (i + 1 >= keys.GetLength(0)) UIBuilder.Size(UIBuilder.Rect("Flex", row), 0, flex: 1.6f);
        }

        ShowPage(SettingsPage);
    }

    private RectTransform Page(RectTransform content, int index, string name) {
        pages[index] = UIBuilder.Fill(UIBuilder.Rect(name, content)).gameObject;
        RectTransform list = UIBuilder.Rect("List", pages[index].transform);
        list.anchorMin = new Vector2(0, 1);
        list.anchorMax = new Vector2(1, 1);
        list.pivot = new Vector2(0.5f, 1);
        UIBuilder.VLayout(list.gameObject, 0);
        list.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        return list;
    }

    private void BuildGraphics(RectTransform list) {
        Section(list, "그래픽");
        RectTransform row = Row(list);
        TextMeshProUGUI l = UIBuilder.Text(row, "Label", "그래픽 품질", UITheme.Body, 26, UITheme.Ink);
        UIBuilder.Size(l, 360);
        UIBuilder.Size(UIBuilder.Rect("Flex", row), 0, flex: 1);
        presetLabels = new TextMeshProUGUI[3];
        presetButtons = new Button[3];
        for (int i = 0; i < 3; i++) {
            GraphicsPreset p = (GraphicsPreset)i;
            Button b = UIBuilder.SmallButton(row, GraphicsQuality.PresetLabel(p), () => {
                GraphicsQuality.SetPreset(p);
                if (renderScale != null) renderScale.SetValueWithoutNotify(GraphicsQuality.RenderScale * 100f);
                if (renderScale != null) renderScale.onValueChanged.Invoke(renderScale.value);
            });
            presetButtons[i] = b;
            presetLabels[i] = b.GetComponentInChildren<TextMeshProUGUI>();
            LocalizedLabel.BindDynamic(presetLabels[i], () => GraphicsQuality.PresetLabel(p));
            presetLabels[i].font = UITheme.BodyBold;
            UIBuilder.Size(b, 110, 42);
        }
        if (GraphicsQuality.SupportsRenderScale) {
            renderScale = SliderRow(list, "해상도 배율 (%)", GraphicsQuality.MinRenderScale * 100f, GraphicsQuality.MaxRenderScale * 100f,
                                    GraphicsQuality.RenderScale * 100f, true, "0", v => GraphicsQuality.SetRenderScale(v / 100f));
        }
        fpsState = ToggleRow(list, "FPS 표시 (F3)", GraphicsQuality.ShowFps, GraphicsQuality.SetShowFps);

        TextMeshProUGUI note = UIBuilder.Text(list, "Note", "graphics.note", UITheme.Body, 20, UITheme.Mute);
        note.enableWordWrapping = true;
        note.lineSpacing = 8;
        note.margin = new Vector4(0, 18, 0, 0);
        UIBuilder.Size(note, height: 110);

        string software = GraphicsQuality.SoftwareRenderer;
        if (software != null) {
            TextMeshProUGUI warn = UIBuilder.Text(list, "SoftwareWarning",
                L10n.T("<color=#E3A72F>그래픽 가속이 꺼져 있습니다.</color> 브라우저 설정에서 '가능한 경우 그래픽 가속 사용'(하드웨어 가속)을 켜고 ") +
                L10n.T("브라우저를 다시 시작하세요. 지금은 GPU 대신 CPU로 그리고 있어 매우 느립니다.\n<size=16>렌더러: ") + UITheme.Safe(software) + "</size>",
                UITheme.Body, 21, UITheme.Ink);
            LocalizedLabel.BindDynamic(warn, () => L10n.Format("graphics.software", UITheme.Safe(software)));
            warn.enableWordWrapping = true;
            warn.lineSpacing = 6;
            UIBuilder.Size(warn, height: 110);
        }
    }

    void Update() {
        if (swatches != null) {
            for (int i = 0; i < swatches.Length; i++) {
                swatches[i].GetComponent<UIBorder>().Color = i == GameSettings.CrosshairColorIndex ? UITheme.Amber : UITheme.Line2;
            }
        }
        if (presetLabels != null && shownPreset != (int)GraphicsQuality.Current) {
            shownPreset = (int)GraphicsQuality.Current;
            for (int i = 0; i < presetLabels.Length; i++) {
                bool on = i == shownPreset;
                presetLabels[i].color = on ? UITheme.Ink : UITheme.Amber;
                // The button tint multiplies the image colour, so the selected fill goes in the colour block.
                ColorBlock cb = presetButtons[i].colors;
                cb.normalColor = cb.selectedColor = on ? UITheme.WithAlpha(UITheme.Amber, 0.45f) : new Color(0f, 0f, 0f, 0.01f);
                presetButtons[i].colors = cb;
            }
        }
        // F3 can flip the counter while the menu is open.
        if (fpsState != null) fpsState.text = GraphicsQuality.ShowFps ? L10n.T("켬") : L10n.T("끔");
    }

    private void ShowPage(int page) {
        for (int i = 0; i < pages.Length; i++) {
            bool on = i == page;
            pages[i].SetActive(on);
            navs[i].color = on ? UITheme.Ink : UITheme.Mute;
            navs[i].transform.parent.GetChild(0).gameObject.SetActive(on);
        }
    }

    private static TextMeshProUGUI NavItem(Transform parent, string text, Action onClick, Color color) {
        Image bg = UIBuilder.Image(parent, "Nav " + text, new Color(0, 0, 0, 0.001f), true);
        UIBuilder.Size(bg, height: 58);
        Image edge = UIBuilder.Image(bg.transform, "Edge", UITheme.Amber);
        edge.rectTransform.anchorMin = Vector2.zero;
        edge.rectTransform.anchorMax = new Vector2(0, 1);
        edge.rectTransform.pivot = new Vector2(0, 0.5f);
        edge.rectTransform.sizeDelta = new Vector2(3, 0);
        edge.transform.SetAsFirstSibling();
        edge.gameObject.SetActive(false);
        TextMeshProUGUI t = UIBuilder.Text(bg.transform, "Text", text, UITheme.BodyBold, 32, color, TextAlignmentOptions.MidlineLeft);
        UIBuilder.Fill(t.rectTransform, 24, 0, 0, 0);
        Button b = bg.gameObject.AddComponent<Button>();
        b.targetGraphic = t;
        ColorBlock cb = b.colors;
        cb.highlightedColor = new Color(1.25f, 1.15f, 0.9f);
        cb.colorMultiplier = 1.3f;
        b.colors = cb;
        b.onClick.AddListener(() => { UISounds.Click(); onClick(); });
        return t;
    }

    private static void Section(Transform parent, string text) {
        TextMeshProUGUI t = UIBuilder.Text(parent, "Section", text, UITheme.BodyBold, 26, UITheme.Amber);
        t.characterSpacing = 10;
        t.alignment = TextAlignmentOptions.BottomLeft;
        UIBuilder.Size(t, height: 50);
        t.margin = new Vector4(0, 0, 0, 12);
    }

    private static RectTransform Row(Transform parent) {
        RectTransform row = UIBuilder.Rect("Row", parent);
        UIBuilder.Size(row, height: 58);
        HorizontalLayoutGroup h = UIBuilder.HLayout(row.gameObject, 28);
        h.childAlignment = TextAnchor.MiddleLeft;
        h.childForceExpandWidth = false;
        Image line = UIBuilder.Image(row, "Line", UITheme.Line);
        line.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
        line.rectTransform.anchorMin = Vector2.zero;
        line.rectTransform.anchorMax = new Vector2(1, 0);
        line.rectTransform.pivot = new Vector2(0.5f, 0);
        line.rectTransform.sizeDelta = new Vector2(0, 1);
        return row;
    }

    private static Slider SliderRow(Transform parent, string label, float min, float max, float value, bool whole,
                                    string format, Action<float> onChanged) {
        RectTransform row = Row(parent);
        TextMeshProUGUI l = UIBuilder.Text(row, "Label", label, UITheme.Body, 26, UITheme.Ink);
        UIBuilder.Size(l, 360);
        TextMeshProUGUI readout = null;
        Slider s = UIBuilder.Slider(row, min, max, value, whole, v => {
            readout.text = v.ToString(format);
            onChanged(v);
        });
        UIBuilder.Size(s, 0, 40, 1);
        readout = UIBuilder.Text(row, "Value", value.ToString(format), UITheme.CondBold, 28, UITheme.Ink, TextAlignmentOptions.MidlineRight);
        UIBuilder.Size(readout, 100);
        return s;
    }

    /// <summary>Returns the state text ("켬"/"끔").</summary>
    private static TextMeshProUGUI ToggleRow(Transform parent, string label, bool value, Action<bool> onChanged) {
        RectTransform row = Row(parent);
        TextMeshProUGUI l = UIBuilder.Text(row, "Label", label, UITheme.Body, 26, UITheme.Ink);
        UIBuilder.Size(l, 360);
        UIBuilder.Size(UIBuilder.Rect("Flex", row), 0, flex: 1);
        TextMeshProUGUI state = null;
        bool current = value;
        Button b = UIBuilder.SmallButton(row, value ? L10n.T("켬") : L10n.T("끔"), () => {
            current = !current;
            state.text = current ? L10n.T("켬") : L10n.T("끔");
            onChanged(current);
        });
        state = b.GetComponentInChildren<TextMeshProUGUI>();
        LocalizedLabel.BindDynamic(state, () => L10n.T(current ? "켬" : "끔"));
        UIBuilder.Size(b, 100, 42);
        return state;
    }

    private void SwatchRow(Transform parent, string label) {
        RectTransform row = Row(parent);
        TextMeshProUGUI l = UIBuilder.Text(row, "Label", label, UITheme.Body, 26, UITheme.Ink);
        UIBuilder.Size(l, 360);
        swatches = new Image[GameSettings.CrosshairColors.Length];
        for (int i = 0; i < swatches.Length; i++) {
            int index = i;
            Image sw = UIBuilder.Image(row, "Swatch", GameSettings.CrosshairColors[i], true);
            UIBuilder.Size(sw, 40, 40);
            UIBorder.Add(sw.rectTransform, UITheme.Line2, 3f);
            Button b = sw.gameObject.AddComponent<Button>();
            b.targetGraphic = sw;
            b.onClick.AddListener(() => { UISounds.Click(); GameSettings.SetCrosshairColor(index); });
            swatches[i] = sw;
        }
    }

}
