/// <summary>
/// 親機のシーン境界判断（純粋。UnityEngine非依存）。
///
/// 既存 ParentUdpSender のシーン分類の式・短絡評価をそのまま移設したもの（新しい条件は追加しない）：
///  ・タイトルシーン判定（IsTitleScene）
///  ・実際のゲームプレイ中シーン判定（IsGameplayScene）
///  ・ロード中シーン判定（IsLoadingScene＝接続タイムアウトの例外条件）
///  ・新セッション境界の判定（IsNewSessionScene＝OnSceneLoaded のリセット条件）
///
/// 境界（このクラスが触らないもの）：
///  ・現在シーンの取得（SceneManager）・実シーン遷移・sceneLoaded の購読／解除・
///    各担当（セッション／通知ハブ／接続）へのリセット・通知呼び出し・ログは旧側が行う。
///  ・結果画面への遷移可否は既存 RankSyncNavigation が担当する（同じ条件をここへ複製しない）。
///  ・シーン名は判定時点の呼び出し側の値（Inspector値）を引数で受け取り、本クラスは保持しない。
/// </summary>
public static class ParentSceneBoundaryPolicy
{
    /// <summary>
    /// タイトルシーン判定。親機のタイトル（motherTitleSceneName）と子機のタイトル（titleSceneName）の両方を扱う。
    /// 末尾の Contains("Title") は命名ゆれ吸収用のフォールバック（既存の式のまま）。
    /// </summary>
    public static bool IsTitleScene(string sceneName, string motherTitleSceneName, string titleSceneName)
        => sceneName == motherTitleSceneName ||
           sceneName == titleSceneName ||
           sceneName == "TitleScene" ||
           sceneName == "Title" ||
           sceneName.Contains("Title");

    /// <summary>
    /// 実際のゲームプレイシーンか。gameSceneName は最初に読み込む ParentLoading を指すため、
    /// プレイ中の判定には使えない（既存の理由そのまま。使うと LOUD_ITEM が常に無視される）。
    /// </summary>
    public static bool IsGameplayScene(string sceneName, string gameplaySceneName)
        => sceneName == gameplaySceneName;

    /// <summary>
    /// ロード中シーンか（既存：接続タイムアウトの例外条件）。ロード中は接続を維持する側で使う。
    /// </summary>
    public static bool IsLoadingScene(string sceneName, string gameSceneName)
        => sceneName == gameSceneName;

    /// <summary>
    /// 新しいセッションの境界シーンか（既存：OnSceneLoaded のリセット条件。ロード中／ソロ開始シーン）。
    /// 短絡評価の順序も既存のまま（ロード中判定 → ソロ開始判定）。
    /// </summary>
    public static bool IsNewSessionScene(string sceneName, string gameSceneName, string soloGameSceneName)
        => sceneName == gameSceneName || sceneName == soloGameSceneName;
}
