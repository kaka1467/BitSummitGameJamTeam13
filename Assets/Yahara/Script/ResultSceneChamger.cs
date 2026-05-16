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
    
    [Header("遷移先シーン名")]
    public string titleSceneName = "TitleScene"; 

    private bool isFading = false;

    void Update()
{
    // どちらのシステムで検知したかを判定するフラグ
    bool isButtonPressed = false;

    // 1. 旧 Input Manager での検知 (ジョイスティックのボタン0、またはPCテスト用のスペースキー)
    if (Input.GetKeyDown(KeyCode.JoystickButton0) || Input.GetKeyDown(KeyCode.Space))
    {
        isButtonPressed = true;
    }

    // 2. 新 Input System での検知 (ゲームパッドが接続されていて、南ボタン(A/Bボタン等)が押されたか)
    if (Gamepad.current != null && Gamepad.current.buttonSouth.wasPressedThisFrame)
    {
        isButtonPressed = true;
    }

    // どっちかでボタンが押されていたら、フェード処理を開始
    if (isButtonPressed)
    {
        StartFadeToTitle();
    }
}

    // 外部（ボタン入力など）からこの関数を呼び出す
    public void StartFadeToTitle()
    {
        if (!isFading)
        {
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
            elapsedTime += Time.deltaTime;
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
            elapsedTime += Time.deltaTime;
            textColor.a = Mathf.Clamp01(elapsedTime / 1.0f);
            fadeText.color = textColor;
            yield return null;
        }
        textColor.a = 1f;
        fadeText.color = textColor;

        // 3. テキストが表示された状態で少し待つ
        yield return new WaitForSeconds(textDuration);

        // 4. タイトルシーンへ遷移
        SceneManager.LoadScene(titleSceneName);
    }
}