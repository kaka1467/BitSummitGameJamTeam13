/// <summary>
/// 子機のCAUGHTフォールバック（GameManager不在のゲームシーンでの結果処理・結果シーン遷移）の判定（純粋。UnityEngine非依存）。
///
/// 既存 ChildUdpReceiver の判定式・短絡評価をそのまま移設したもの（新しい条件は追加しない）：
///  ・タイトルシーン判定（IsTitleScene）
///  ・ゲームシーン判定（IsGameplayScene＝ロード・タイトル・結果以外）
///  ・フォールバック実行の継続／中止（ShouldRunFallback＝二重ロード防止）
///
/// 境界（このクラスが触らないもの）：
///  ・現在シーンの取得（SceneManager）・GameManager の実体確認・スコア保存（PlayerPrefs）・
///    ランキング通知・実シーン遷移・ログは旧側（ChildUdpReceiver）が行う。
///  ・時間待ち・Coroutine・再送・タイムアウトの判断は持たない（既存経路にも無い。新設もしない）。
/// </summary>
public static class ChildCaughtFallbackPolicy
{
    /// <summary>結果シーン（GAME_OVER）。旧側の既存リテラルと同じ値。</summary>
    public const string GameOverSceneName = "ChildGameOver";

    /// <summary>結果シーン（GAME_CLEAR）。旧側の既存リテラルと同じ値。</summary>
    public const string GameClearSceneName = "ChildGameClear";

    /// <summary>
    /// フォールバック実行判定が使うロードシーンのリテラル名。
    /// 既存 RunCaughtFallback のリテラル（"ChildLoading"）をそのまま維持する
    /// （Inspector の gameSceneName とは別。既存コードがそうしていた差をそのまま保つ）。
    /// </summary>
    public const string LoadingSceneName = "ChildLoading";

    /// <summary>
    /// タイトルシーン判定（既存 IsTitleScene と同じ式：設定名と一致、または固定名、"Title" を含む）。
    /// </summary>
    public static bool IsTitleScene(string sceneName, string titleSceneName)
        => sceneName == titleSceneName ||
           sceneName == "TitleScene" ||
           sceneName == "Title" ||
           sceneName.Contains("Title");

    /// <summary>
    /// 子機のゲームシーン（ロード・タイトル・結果以外）かどうか。
    /// 従来フォールバックが使っていた判定と同じ（特定のシーン名に依存しない）。
    /// ロード中の扱いは呼び出し側が渡す設定値（gameSceneName）で判定する＝既存どおり。
    /// </summary>
    public static bool IsGameplayScene(string sceneName, string configuredLoadingSceneName, string titleSceneName)
        => sceneName != GameOverSceneName &&
           sceneName != GameClearSceneName &&
           sceneName != configuredLoadingSceneName &&
           !IsTitleScene(sceneName, titleSceneName);

    /// <summary>
    /// 【従来フォールバック】実行してよいか（継続＝true／中止＝false）。
    /// 既存の二重ロード防止条件そのまま：結果シーン・ロード中・タイトルでは中止する。
    /// </summary>
    public static bool ShouldRunFallback(string activeScene, string titleSceneName)
        => activeScene != GameOverSceneName &&
           activeScene != GameClearSceneName &&
           activeScene != LoadingSceneName &&
           !IsTitleScene(activeScene, titleSceneName);
}
