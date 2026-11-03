using System;
using UnityEngine;

/// <summary>
/// 子機の表示言語（日本語／英語）。現在はチュートリアルの文言の切り替えに使う。
/// 保存先は PlayerPrefs の "GameLanguage"（スコアリセットでは消えない）。未設定は日本語。
/// </summary>
public static class GameLanguage
{
    public enum Language
    {
        Japanese = 0,
        English = 1,
    }

    private const string PrefKey = "GameLanguage";

    private static bool _loaded;
    private static Language _current = Language.Japanese;

    /// <summary>言語が切り替わったとき（表示中の文言を更新したい画面が購読する）。</summary>
    public static event Action<Language> Changed;

    public static Language Current
    {
        get
        {
            if (!_loaded)
            {
                _current = (PlayerPrefs.GetInt(PrefKey, (int)Language.Japanese) == (int)Language.English)
                    ? Language.English
                    : Language.Japanese;
                _loaded = true;
            }

            return _current;
        }
    }

    public static bool IsEnglish => Current == Language.English;

    public static void Set(Language language)
    {
        if (_loaded && _current == language) return;

        _current = language;
        _loaded = true;
        PlayerPrefs.SetInt(PrefKey, (int)language);
        PlayerPrefs.Save();
        Changed?.Invoke(language);
    }

    public static void Toggle()
    {
        Set(IsEnglish ? Language.Japanese : Language.English);
    }

    /// <summary>現在の言語に合う文言を返す。英語が空なら日本語にフォールバックする。</summary>
    public static string Pick(string japanese, string english)
    {
        return (IsEnglish && !string.IsNullOrEmpty(english)) ? english : japanese;
    }
}
