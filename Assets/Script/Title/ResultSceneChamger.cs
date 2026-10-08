using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TMPro;
using UnityEngine.InputSystem;

public class ResultSceneChamger : MonoBehaviour
{
    [Header("UI要素")]
    public Image fadeImage;          // 黒い画像
    public TextMeshProUGUI fadeText; // 表示するテキスト

    [Header("設定時間")]
    public float fadeDuration = 2.0f; // 暗くなる時間（秒）
    public float textDuration = 1.5f; // テキストが見えている時間（秒）

    [Header("入力と自動遷移")]
    public float inputIgnoreDuration = 5.0f; // 最初の入力無視時間（秒）
    public float autoChangeDelay = 30.0f; // 自動遷移までの時間（秒）
    
    [Header("効果音")]
    [Tooltip("タイトルへ戻る演出（フェード）が始まった瞬間に1回鳴らすAudioSource。未設定なら鳴らさない。シーンごとに設定できる。")]
    public AudioSource returnToTitleSe;

    [Header("遷移先シーン名")]
    public string titleSceneName = "MiniTitle"; 

    private bool isFading = false;
    private float elapsed = 0f;

    void Update()
    {
        if (isFading)
        {
            return;
        }

        elapsed += Time.unscaledDeltaTime;

        if (elapsed >= autoChangeDelay)
        {
            StartFadeToTitle();
            return;
        }

        if (elapsed < inputIgnoreDuration)
        {
            return;
        }

        bool isButtonPressed = false;
        var kb = Keyboard.current;
        var gp = Gamepad.current;

        // キーボード入力
        if (kb != null)
        {
            if (kb.aKey.wasPressedThisFrame) isButtonPressed = true;
            if (kb.bKey.wasPressedThisFrame) isButtonPressed = true;
            if (kb.xKey.wasPressedThisFrame) isButtonPressed = true;
            if (kb.yKey.wasPressedThisFrame) isButtonPressed = true;
        }

        // ゲームパッド入力
        if (gp != null)
        {
            // buttonSouth=A, buttonEast=B, buttonWest=X, buttonNorth=Y (一般的な配置)
            if (gp.buttonSouth.wasPressedThisFrame) isButtonPressed = true;
            if (gp.buttonEast.wasPressedThisFrame) isButtonPressed = true;
            if (gp.buttonWest.wasPressedThisFrame) isButtonPressed = true;
            if (gp.buttonNorth.wasPressedThisFrame) isButtonPressed = true;
        }

        // どっちかでボタンが押されていたら、フェード処理を開始
        if (isButtonPressed)
        {
            StartFadeToTitle();
        }
    }

    /// <summary>
    /// 「タイトルへ戻る」演出の開始を相手側へ通知する（親機・子機の画面同期用）。
    /// 子機 → 親機: TEAM13_RETURN_TO_TITLE（再送付き）。親機 → 子機: 同メッセージ（再送付き）。
    /// 子機・親機のどちらのリザルト画面でもこのスクリプトを共用しているため、
    /// シーン内に存在する側の UDP コンポーネントへ処理を委譲する。
    /// </summary>
    private void NotifyReturnToTitle()
    {
        if (ChildUdpReceiver.instance != null)
        {
            ChildUdpReceiver.instance.notifyReturnToTitle();
        }
        else if (ParentUdpSender.Instance != null)
        {
            ParentUdpSender.Instance.NotifyReturnToTitleToChild();
        }
    }

    /// <summary>
    /// 現在「タイトルへ戻る」フェード演出中かどうか（UDP の戻り通知の重複実行防止に使用）。
    /// </summary>
    public bool IsReturningToTitle => isFading;

    /// <summary>
    /// 「タイトルへ戻る」演出を開始する。
    /// notifyPeer=true（ユーザー操作・自動遷移など、この端末が起点の場合）は相手端末へ RETURN_TO_TITLE を通知する。
    /// notifyPeer=false（相手端末からの RETURN_TO_TITLE 受信が起点の場合）は通知を送り返さない。
    /// 復帰要求（RETURN_TO_TITLE）と確認応答を区別するため、相手起起源の復帰では同じ要求を再送しない。
    /// </summary>
    public void StartFadeToTitle(bool notifyPeer = true)
    {
        if (!isFading)
        {
            // 「タイトルへ戻る」演出の開始を相手側へ通知する（親機・子機の画面同期用）。
            // 相手からの受信が起点の場合は送り返さない（無限往復の防止）。
            if (notifyPeer)
            {
                NotifyReturnToTitle();
            }

            if (returnToTitleSe != null)
            {
                returnToTitleSe.Play();
            }

            StartCoroutine(FadeSequence());
        }
    }

    private IEnumerator FadeSequence()
    {
        isFading = true;

        // 1. 画面を徐々に暗くする（フェードアウト）
        float elapsedTime = 0f;
        Color imgColor = fadeImage.color;

        while (elapsedTime < fadeDuration)
        {
            elapsedTime += Time.unscaledDeltaTime;
            imgColor.a = Mathf.Clamp01(elapsedTime / fadeDuration);
            fadeImage.color = imgColor;
            yield return null;
        }
        
        // 確実に真っ黒にする
        imgColor.a = 1f;
        fadeImage.color = imgColor;

        // 2. テキストを徐々に表示する（フェードイン）
        elapsedTime = 0f;
        Color textColor = fadeText.color;

        while (elapsedTime < 1.0f) // 1秒かけて文字を表示
        {
            elapsedTime += Time.unscaledDeltaTime;
            textColor.a = Mathf.Clamp01(elapsedTime / 1.0f);
            fadeText.color = textColor;
            yield return null;
        }
        textColor.a = 1f;
        fadeText.color = textColor;

        // 3. テキストが表示された状態で少し待つ
        yield return new WaitForSecondsRealtime(textDuration);

        // 4. タイトルシーンへ遷移
        SceneManager.LoadScene(SceneNameResolver.Resolve(titleSceneName));
    }
}
