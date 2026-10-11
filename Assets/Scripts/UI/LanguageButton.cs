using TMPro;
using UnityEngine;

public class LanguageButton : MonoBehaviour {
    private TMP_Text label;
    void Awake() { label = GetComponentInChildren<TMP_Text>(); }
    void OnEnable() { Refresh(); L10n.Changed += Refresh; }
    void OnDisable() { L10n.Changed -= Refresh; }
    private void Refresh() { if (label != null) { label.fontSize = 20; label.text = L10n.T("언어") + " · " + L10n.LanguageName + "  >"; } }
}
