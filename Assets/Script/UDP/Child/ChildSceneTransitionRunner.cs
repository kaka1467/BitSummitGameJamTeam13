using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// ChildSceneTransitionRunner：
/// 子機の「シーン遷移の実行」だけを担う担当（通常C#クラス。MonoBehaviourではない）。
///
/// 子機に実在する遷移入口は3つで、いずれも「即時遷移」（フェード・待機を伴う遷移は子機には存在しない）：
///  ・LoadGameScene（開始ボタン／START_GAME受理）＝シーン名をそのまま読み込む（Resolverは使わない＝既存どおり）
///  ・CAUGHTフォールバックの結果遷移＝"ChildGameOver" をそのまま読み込む（Resolverは使わない＝既存どおり）
///  ・タイトル復帰＝SceneNameResolver で解決してから読み込む（既存どおり）
///
/// 親機のフェード方式（BGM/画面フェード付き遷移）は子機の既存挙動に無いため、本担当は持ち込まない。
///
/// 境界（この担当が触らないもの）：
///  ・通信解析・受理判断／セッション・通知ハブ・ランキングの状態更新／スコア保存・ランキング通知は旧側。
///  ・Coroutine の起動・停止とライフサイクル、sceneLoaded の購読／解除と境界処理本体は旧側。
///  ・二重ロード防止などの既存ガードは旧側（本担当はガードを持たない）。
///  ・フェード付きのリザルト→タイトル遷移は既存 ResultSceneChamger が実行する（ここへ複製しない）。
///  ・遷移先のシーン名は呼び出し時点の設定値（Inspector値）を引数で受け取り、本担当は保持しない。
/// </summary>
public static class ChildSceneTransitionRunner
{
    /// <summary>
    /// 即時遷移（シーン名の解決は行わない＝既存どおり）。
    /// ゲームシーン遷移（LoadGameScene）と CAUGHT フォールバックの結果シーン遷移で使う。
    /// </summary>
    public static void LoadSceneNow(string sceneName)
    {
        SceneManager.LoadScene(sceneName);
    }

    /// <summary>
    /// 即時遷移（既存どおり SceneNameResolver で解決してから読み込む）。タイトル復帰で使う
    /// （Inspector の値と Build Settings の名前が揺れていても解決する既存仕様を維持）。
    /// </summary>
    public static void LoadSceneNowResolved(string sceneName)
    {
        SceneManager.LoadScene(SceneNameResolver.Resolve(sceneName));
    }
}
