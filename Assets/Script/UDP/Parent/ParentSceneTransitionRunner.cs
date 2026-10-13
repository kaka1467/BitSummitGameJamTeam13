using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// ParentSceneTransitionRunner：
/// 親機の「シーン遷移の実行」だけを担う担当（通常C#クラス。MonoBehaviourではない）。
///
/// 移した実行責任（既存 ParentUdpSender の実処理をそのまま移設）：
///  ・LoadSceneAfterBgmFade＝タイトルBGMのフェード開始＋画面の暗転フェード完了待ち＋遷移
///  ・即時遷移（シーン名をそのまま読み込む）
///  ・即時遷移（SceneNameResolver で解決してから読み込む＝タイトル復帰）
///
/// 境界（この担当が触らないもの）：
///  ・通信受信・結果確定の判断／セッション等の状態更新／既存ガード（_resultProcessed 等）は旧側。
///  ・Coroutine の起動・停止（StartCoroutine／StopCoroutine）とライフサイクルは旧側が持つ。
///  ・親機のシーン分類（タイトル／プレイ中／ロード中／新セッション境界）は ParentSceneBoundaryPolicy が担う。
///  ・結果遷移の可否は既存 RankSyncNavigation が担う（ここへ複製しない）。
///  ・フェード付きのリザルト→タイトル遷移は既存 ResultSceneChamger が実行する（ここへ複製しない）。
///  ・遷移先のシーン名は「呼び出し時点の設定値」を引数で受け取る（本担当は保持しない）。
/// </summary>
public static class ParentSceneTransitionRunner
{
    /// <summary>
    /// 【既存 LoadSceneAfterBgmFade の実行そのもの】タイトルBGMのフェードアウトと画面の暗転フェードを走らせ、
    /// 暗転の完了だけを待って遷移する。
    ///
    /// 既存の意図（そのまま維持）：
    ///  ・以前は「BGMフェード完了」と「画面フェード完了」の長い方（＝BGMの3秒）まで待っていたため、
    ///    子機が開始しても親機のロード画面（ParentLoading）が出るまで3秒かかっていた。
    ///    BGMフェードは音の演出でありロード画面の表示を待たせる必要はないため、待機は画面フェードのみを基準にする。
    ///  ・TitleBgmFader の AudioSource はタイトルシーン内のオブジェクトにあり DontDestroyOnLoad ではないため、
    ///    シーン遷移時に AudioSource ごと破棄され、フェード途中でも安全に終了する（音源リーク・例外は無い）。
    ///  ・暗転完了後に遷移するので、フェード途中で切れるBGMは画面が黒い状態で途切れ、耳障りになりにくい。
    ///
    /// 維持する点：yield、待機時間（FadeOutSeconds）、フェード順序、遷移の実行時点、シーン名の解決をしないこと。
    /// Coroutine の起動／停止は呼び出し側（旧側）が行う。
    /// diagnostics は既存 showDebugLogs 相当の値取得（**各診断ログの位置で現在値を取得する**ための最小の Func&lt;bool&gt;。
    /// 旧コードが待機後に showDebugLogs を再読していた評価時点をそのまま維持する。ログ出力の有無のみに作用する）。
    /// logContext は既存 StartTrace ログのコンテキスト（既存と同じ Unity オブジェクトを渡す。本担当は ParentUdpSender を参照しない）。
    /// diagnosticsContext は既存 ENTER ログの文脈（呼び出し側が判定時点の値で組み立てて渡す。例："currentState=Connected, sessionId=..."）。
    /// </summary>
    public static IEnumerator LoadSceneAfterBgmFade(string sceneName, Func<bool> diagnostics, UnityEngine.Object logContext, string diagnosticsContext)
    {
        if (diagnostics())
            Debug.Log($"[StartTrace][親機] ParentUdpSender.LoadSceneAfterBgmFade ENTER — 遷移先='{sceneName}', 現在scene='{SceneManager.GetActiveScene().name}', {diagnosticsContext}, id={(logContext != null ? logContext.GetInstanceID() : 0)}", logContext);

        TitleBgmFader bgmFader = UnityEngine.Object.FindFirstObjectByType<TitleBgmFader>();
        TitleScreenFader screenFader = UnityEngine.Object.FindFirstObjectByType<TitleScreenFader>();

        // BGMフェードは開始するだけ（待たない）。シーン遷移で AudioSource ごと破棄される。
        if (bgmFader == null)
        {
            Debug.LogWarning("[ParentUdpSender] LoadSceneAfterBgmFade: TitleBgmFader が見つかりません（BGMはフェードせず遷移します）。");
        }
        else if (!bgmFader.FadeOut(allowResume: false))
        {
            Debug.LogWarning("[ParentUdpSender] LoadSceneAfterBgmFade: BGM FadeOut() が false（BGM未再生の可能性）。");
        }

        // 待機は「画面の暗転フェード」の完了のみを基準にする。
        float waitSeconds = 0f;
        if (screenFader == null)
        {
            Debug.LogWarning("[ParentUdpSender] LoadSceneAfterBgmFade: TitleScreenFader が見つかりません（画面フェードなしで遷移します）。");
        }
        else if (screenFader.FadeOut())
        {
            waitSeconds = screenFader.FadeOutSeconds;
        }

        if (waitSeconds > 0f)
        {
            Debug.Log($"[ParentUdpSender] LoadSceneAfterBgmFade: 画面フェード {waitSeconds}秒で '{sceneName}' へ遷移します（BGMフェードは待ちません）。");
            if (diagnostics())
                Debug.Log($"[StartTrace][親機] LoadSceneAfterBgmFade フェード待機開始 — waitSeconds={waitSeconds:F2}, 遷移先='{sceneName}'", logContext);
            yield return new WaitForSecondsRealtime(waitSeconds);
            if (diagnostics())
                Debug.Log($"[StartTrace][親機] LoadSceneAfterBgmFade フェード待機解除 — 遷移先='{sceneName}'", logContext);
        }
        else if (diagnostics())
        {
            Debug.Log($"[StartTrace][親機] LoadSceneAfterBgmFade フェード待機なし (waitSeconds=0) — 遷移先='{sceneName}'", logContext);
        }

        if (diagnostics())
            Debug.Log($"[StartTrace][親機] LoadSceneAfterBgmFade → SceneManager.LoadScene('{sceneName}') 直前", logContext);
        SceneManager.LoadScene(sceneName);
    }

    /// <summary>
    /// 即時遷移（シーン名の解決は行わない＝既存どおり）。結果遷移（TIME_UP等）と RANK_SYNC 経由の遷移で使う。
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
