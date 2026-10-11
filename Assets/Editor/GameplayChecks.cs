using System;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

/// <summary>Headless UI regression checks: Unity -batchmode -executeMethod GameplayChecks.Run.</summary>
[InitializeOnLoad]
public static class GameplayChecks {
    [Serializable] private class Entry { public string key, ko, en, zh; }
    [Serializable] private class Catalog { public Entry[] entries; }

    static GameplayChecks() {
        if (SessionState.GetBool("FPS.GameplayChecks", false))
            EditorApplication.playModeStateChanged += OnPlayState;
    }

    public static void Run() {
        SessionState.SetBool("FPS.GameplayChecks", true);
        EditorApplication.playModeStateChanged -= OnPlayState;
        EditorApplication.playModeStateChanged += OnPlayState;
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        EditorApplication.EnterPlaymode();
    }

    private static void OnPlayState(PlayModeStateChange state) {
        if (state == PlayModeStateChange.EnteredPlayMode) EditorApplication.update += CheckOnce;
    }

    private static void CheckOnce() {
        EditorApplication.update -= CheckOnce;
        SessionState.SetBool("FPS.GameplayChecks", false);
        try { RunChecks(); EditorApplication.Exit(0); }
        catch (Exception ex) { Debug.LogException(ex); EditorApplication.Exit(1); }
    }

    private static void RunChecks() {
        GameSettings.Load();
        string language = L10n.Current;
        WeaponId primary = LoadoutChoice.Primary;
        OpticId optic = LoadoutChoice.Optic;
        bool invert = GameSettings.InvertY;
        GameUI ui = null;
        try {
            Catalog catalog = JsonUtility.FromJson<Catalog>(Resources.Load<TextAsset>("localization").text);
            Require(catalog.entries.All(e => !string.IsNullOrEmpty(e.ko) && !string.IsNullOrEmpty(e.en) && !string.IsNullOrEmpty(e.zh)), "Complete translations");
            Require(catalog.entries.Select(e => string.IsNullOrEmpty(e.key) ? e.ko : e.key).Distinct().Count() == catalog.entries.Length, "Unique translation keys");
            string glyphs = new string(string.Concat(catalog.entries.Select(e => e.zh)).Where(c => c > 127).Distinct().ToArray());
            Require(UITheme.ChineseFont != null && UITheme.ChineseFont.TryAddCharacters(glyphs, out string missing), "Chinese glyph coverage");
            Require(UITheme.Body.atlasTexture != AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/UI/Resources/Fonts & Materials/Body-Regular.asset").atlasTexture,
                "Runtime font atlas is independent of the saved font");

            L10n.Set("ko");
            ui = GameUI.Create();
            ui.Lobby.Callsign = "English玩家";
            ui.Lobby.SetRooms(new[] { new LobbyScreen.RoomEntry { Id = "test", Name = "설정", IsOpen = true, MaxPlayers = 8, Map = "warehouse" } });
            TMP_Text title = ui.Lobby.GetComponentsInChildren<TMP_Text>(true).First(t => t.text == "작전 목록");
            foreach (string locale in new[] { "en", "zh", "ko" }) {
                L10n.Set(locale);
                Require(title.text == L10n.T("작전 목록"), "Live lobby translation: " + locale);
                Require(ui.Lobby.Callsign == "English玩家", "Player input preserved: " + locale);
                Require(ui.Lobby.GetComponentsInChildren<TMP_Text>(true).Any(t => t.gameObject.name == "Name" && t.text == "설정"),
                    "Room names matching translation keys preserved: " + locale);
                Require(ui.Pause.GetComponentsInChildren<TMP_Text>(true).Any(t => t.text == L10n.T("마우스 감도")), "Inactive settings translated: " + locale);
            }

            Button pick = ui.Lobby.GetComponentsInChildren<LoadoutPicker>(true).Single().GetComponentsInChildren<Button>()
                .First(b => b.GetComponentInChildren<TMP_Text>().text == "M700");
            pick.onClick.Invoke();
            Require(LoadoutChoice.Primary == WeaponId.Sniper && Weapons.Get(WeaponId.Sniper).Fits(LoadoutChoice.Optic), "Lobby choice and legal optic");
            ui.EnterGame();
            ui.Pause.SetVisible(true);
            typeof(GameUI).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(ui, null);
            Require(Cursor.lockState == CursorLockMode.None && Cursor.visible && SlopFPS.Input.FpsInputGate.Blocked,
                "Pause opens cursor and blocks gameplay");
            Require(ui.Pause.GetComponentsInChildren<TMP_Text>(true).Any(t => t.text == "Esc / P"), "Fullscreen-safe pause shortcut is visible");
            ui.Pause.SetVisible(false);
            typeof(GameUI).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(ui, null);
            Require(!SlopFPS.Input.FpsInputGate.Blocked, "Resuming restores gameplay input");
            ui.Death.Show("English玩家", "AK-47", false, 10, 50, 8);
            typeof(GameUI).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(ui, null);
            Require(Cursor.lockState == CursorLockMode.None && Cursor.visible && SlopFPS.Input.FpsInputGate.Blocked,
                "Death screen unlocks cursor and blocks firing");
            Button deathPick = ui.Death.GetComponentsInChildren<Button>(true)
                .First(b => b.GetComponentsInChildren<TMP_Text>().Any(t => t.text == "MP5"));
            deathPick.onClick.Invoke();
            Require(LoadoutChoice.Primary == WeaponId.SMG, "Death card changes next loadout");
            Debug.Log("[GameplayChecks] ALL PASSED");
        } finally {
            if (ui != null) UnityEngine.Object.DestroyImmediate(ui.gameObject);
            L10n.Set(language);
            LoadoutChoice.Set(primary, optic, false);
            GameSettings.SetInvertY(invert);
        }
    }

    private static void Require(bool ok, string what) {
        if (!ok) throw new Exception("[GameplayChecks] FAILED: " + what);
        Debug.Log("[GameplayChecks] PASS: " + what);
    }
}
