using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>Primary and optic selection, available before joining and throughout the match.</summary>
public class LoadoutPicker : MonoBehaviour {
    private Button[] buttons;
    private TextMeshProUGUI optic, note;

    public static void Create(Transform parent, bool inGame) {
        RectTransform root = UIBuilder.Rect("LoadoutPicker", parent);
        UIBuilder.Size(root, height: inGame ? 122 : 105);
        UIBuilder.VLayout(root.gameObject, 8);
        LoadoutPicker picker = root.gameObject.AddComponent<LoadoutPicker>();
        RectTransform guns = UIBuilder.Rect("Guns", root);
        UIBuilder.Size(guns, height: 36);
        UIBuilder.HLayout(guns.gameObject, 8);
        picker.buttons = new Button[Weapons.Primaries.Length];
        for (int i = 0; i < picker.buttons.Length; i++) {
            WeaponId id = Weapons.Primaries[i];
            Button button = UIBuilder.SmallButton(guns, Weapons.Get(id).Name,
                () => LoadoutChoice.Set(id, LoadoutChoice.Optic));
            UIBuilder.Size(button, 0, 36, 1);
            button.GetComponentInChildren<TextMeshProUGUI>().fontSize = 20;
            picker.buttons[i] = button;
        }
        Button sight = UIBuilder.SmallButton(root, "", LoadoutChoice.NextOptic);
        UIBuilder.Size(sight, height: 32);
        picker.optic = sight.GetComponentInChildren<TextMeshProUGUI>();
        picker.optic.fontSize = 18;
        if (inGame) {
            picker.note = UIBuilder.Text(root, "Note", "", UITheme.Body, 18, UITheme.Mute);
            UIBuilder.Size(picker.note, height: 38);
            picker.note.enableWordWrapping = true;
        }
    }

    void Update() {
        for (int i = 0; i < buttons.Length; i++) {
            buttons[i].GetComponentInChildren<TextMeshProUGUI>().color =
                Weapons.Primaries[i] == LoadoutChoice.Primary ? UITheme.Ink : UITheme.Amber;
        }
        optic.text = L10n.T("조준경") + " · " + Weapons.OpticName(LoadoutChoice.Optic);
        if (note != null) note.text = L10n.T("선택한 무기는 다음 부활 또는 다음 라운드에 적용됩니다.");
    }
}
