using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Frame rate and frame time in the top-right corner, toggled with F3 or the graphics settings page
/// (<see cref="GraphicsQuality.ShowFps"/>). Averaged over half a second so it can be read.
/// </summary>
public class FpsCounter : MonoBehaviour {

    private TextMeshProUGUI text;
    private float elapsed;
    private int frames;

    public static FpsCounter Create(Transform parent) {
        TextMeshProUGUI t = UIBuilder.Text(parent, "FpsCounter", "", UITheme.CondMedium, 20, UITheme.Ink, TextAlignmentOptions.TopRight);
        UIBuilder.Place(t.rectTransform, new Vector2(1, 1), new Vector2(-14, -8), new Vector2(260, 24));
        t.characterSpacing = 4;
        t.gameObject.AddComponent<Shadow>().effectColor = new Color(0, 0, 0, 0.8f);
        FpsCounter c = t.gameObject.AddComponent<FpsCounter>();
        c.text = t;
        return c;
    }

    void Update() {
        GameUI ui = GameUI.Instance;
        bool typing = ui != null && ui.IgnoreKeys;
        if (!typing && Input.GetKeyDown(KeyCode.F3)) GraphicsQuality.SetShowFps(!GraphicsQuality.ShowFps);
        text.enabled = GraphicsQuality.ShowFps;
        frames++;
        elapsed += Time.unscaledDeltaTime;
        if (elapsed < 0.5f) return;
        float fps = frames / elapsed;
        float ms = 1000f * elapsed / frames;
        string color = fps >= 50f ? "#8DB255" : fps >= 30f ? "#E3A72F" : "#D8432E";
        text.text = "<color=" + color + ">" + fps.ToString("0") + "</color> FPS  " + ms.ToString("0.0") + " MS";
        frames = 0;
        elapsed = 0f;
    }

}
