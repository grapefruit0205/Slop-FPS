using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Local UI language. Network mode/map/weapon IDs and player text are never translated.</summary>
public static class L10n {
    [Serializable] private class Entry { public string key, ko, en, zh; }
    [Serializable] private class Catalog { public Entry[] entries; }
    private static readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
    public static event Action Changed;
    public static string Current { get; private set; }
    public static string LanguageName => Current == "en" ? "English" : Current == "zh" ? "简体中文" : "한국어";

    static L10n() {
        TextAsset file = Resources.Load<TextAsset>("localization");
        if (file != null) {
            foreach (Entry entry in JsonUtility.FromJson<Catalog>(file.text).entries)
                entries[string.IsNullOrEmpty(entry.key) ? entry.ko : entry.key] = entry;
        }
        string saved = NetConfig.LanguageArg ?? PlayerPrefs.GetString("ui.language", "");
        Current = saved == "en" || saved == "zh" || saved == "ko" ? saved
            : Application.systemLanguage == SystemLanguage.Korean ? "ko"
            : Application.systemLanguage == SystemLanguage.ChineseSimplified || Application.systemLanguage == SystemLanguage.ChineseTraditional
                || Application.systemLanguage == SystemLanguage.Chinese ? "zh" : "en";
    }

    public static string T(string key) {
        if (key == null) return "";
        if (!entries.TryGetValue(key, out Entry entry)) return key;
        return Current == "en" ? entry.en : Current == "zh" ? entry.zh : entry.ko;
    }

    public static string Format(string key, params object[] args) => string.Format(T(key), args);
    public static bool Has(string key) => key != null && entries.ContainsKey(key);

    public static void Set(string language) {
        if (language != "ko" && language != "en" && language != "zh") return;
        if (Current == language) return;
        Current = language;
        PlayerPrefs.SetString("ui.language", language);
        PlayerPrefs.Save();
        Changed?.Invoke();
    }

    public static void Cycle() => Set(Current == "ko" ? "en" : Current == "en" ? "zh" : "ko");
}
