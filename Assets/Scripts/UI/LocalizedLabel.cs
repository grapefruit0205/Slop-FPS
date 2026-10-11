using TMPro;
using UnityEngine;

/// <summary>Updates a static label even on inactive screens when the language changes.</summary>
public class LocalizedLabel : MonoBehaviour {
    private TMP_Text label;
    private string key;
    private string last;
    private System.Func<string> render;

    public static void BindDynamic(TMP_Text label, System.Func<string> render) {
        LocalizedLabel binding = label.gameObject.AddComponent<LocalizedLabel>();
        binding.label = label;
        binding.render = render;
        label.text = render();
        L10n.Changed += binding.Refresh;
    }

    public static void Bind(TMP_Text label, string source) {
        if (!L10n.Has(source)) return;
        LocalizedLabel binding = label.gameObject.AddComponent<LocalizedLabel>();
        binding.label = label;
        binding.key = source;
        binding.last = label.text = L10n.T(source);
        L10n.Changed += binding.Refresh;
    }

    private void Refresh() {
        if (render != null) { if (label != null) label.text = render(); return; }
        // A dynamic owner may have replaced this initial label (e.g. a HUD status).
        if (label != null && label.text == last) last = label.text = L10n.T(key);
    }

    void OnDestroy() { L10n.Changed -= Refresh; }
}
