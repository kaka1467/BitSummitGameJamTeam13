using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;
using UnityEngine.SceneManagement;
using UnityEngine.InputSystem;

/// <summary>
/// タイトル画面で一定時間放置すると動画を再生するコンポーネント
/// 動画開始・終了時にシームレスなフェードイン/アウトを行います。
/// - 旧レガシー UnityEngine.Input から完全に移行し、新しい Input System を利用
/// </summary>
public class TitleScreenVideoPlayer : MonoBehaviour
{
    [Header("待機時間設定")]
    [Tooltip("動画再生までの待機時間(秒)")]
    [SerializeField] private float idleTime = 30f;

    [Header("動画設定")]
    [Tooltip("VideoPlayerコンポーネント")]
    [SerializeField] private VideoPlayer videoPlayer;

    [Tooltip("動画再生時に非表示にするUI(タイトルロゴなど)")]
    [SerializeField] private GameObject[] uiToHide;

    [Header("動画終了時の設定")]
    [Tooltip("動画終了時にタイトル画面に戻るか")]
    [SerializeField] private bool returnToTitleAfterVideo = true;

    [Tooltip("動画をループ再生するか")]
    [SerializeField] private bool loopVideo = false;

    [Header("フェード設定")]
    [Tooltip("フェードに使用する全面黒UIのImageコンポーネント\n(Canvas配下の最前面RawImageまたはImageをアサイン)")]
    [SerializeField] private Image fadeImage;

    [Tooltip("フェードの時間(秒)")]
    [SerializeField] private float fadeDuration = 0.4f;

    private float idleTimer = 0f;
    private bool isVideoPlaying = false;
    private Vector2 lastMousePosition;
    private bool hasStartedOnce = false;
    private bool isPrepared = false;
    private bool isStopping = false;

    void Start()
    {
        // VideoPlayerの初期設定
        if (videoPlayer != null)
        {
            videoPlayer.isLooping = loopVideo;
            videoPlayer.Stop();
            videoPlayer.gameObject.SetActive(false);

            // 動画終了時のイベント登録
            videoPlayer.loopPointReached += OnVideoFinished;
            // 準備完了イベント登録
            videoPlayer.prepareCompleted += OnVideoPrepared;
        }
        else
        {
            Debug.LogError("VideoPlayerが設定されていません!");
        }

        // フェード画像初期化（完全に透明）
        if (fadeImage != null)
        {
            fadeImage.color = new Color(0f, 0f, 0f, 0f);
            fadeImage.gameObject.SetActive(true);
            // 描画順を最前面に
            fadeImage.transform.SetAsLastSibling();
        }
        else
        {
            Debug.LogWarning("fadeImageが設定されていません。フェードなしで再生します。");
        }

        // 新Input Systemを使って現在のマウス位置を取得
        lastMousePosition = GetMousePosition();
    }

    void Update()
    {
        if (isVideoPlaying)
        {
            // 動画再生中に何か入力があったら動画をスキップ
            if (AnyInputDown() && !isStopping)
            {
                StartCoroutine(StopVideoWithFade());
            }

            // 動画の終端直前で先にフェードアウトを開始して空背景が見えるのを防ぐ
            if (!loopVideo && !isStopping && videoPlayer != null && videoPlayer.isPrepared)
            {
                double length = videoPlayer.length;
                if (length > 0.0)
                {
                    double remaining = length - videoPlayer.time;
                    double leadTime = fadeDuration + 0.05f;
                    if (remaining <= leadTime)
                    {
                        StartCoroutine(StopVideoWithFade());
                    }
                }
            }
            return;
        }

        // 入力検知(キーボード、マスクリック、マウス移動、タッチ)
        bool hasInput = AnyInputPressed() || IsMouseMoved() || AnyTouchPressed();

        // マウス位置更新（移動検知用）
        lastMousePosition = GetMousePosition();

        if (hasInput)
        {
            // 入力があったらタイマーをリセット
            idleTimer = 0f;

            // Prepareしていた場合はキャンセル
            if (isPrepared)
            {
                videoPlayer.Stop();
                videoPlayer.gameObject.SetActive(false);
                isPrepared = false;
            }
        }
        else
        {
            // 放置時間をカウント
            idleTimer += Time.deltaTime;

            // 指定時間の少し前（フェード時間分＋α）にPrepare（デコード）を開始
            float prepareStartTime = idleTime - fadeDuration - 0.5f;
            if (idleTimer >= prepareStartTime && !isPrepared && !hasStartedOnce)
            {
                PrepareVideo();
            }

            // 指定時間経過したら動画再生
            if (idleTimer >= idleTime && !hasStartedOnce)
            {
                hasStartedOnce = true;
                StartCoroutine(PlayVideoWithFade());
            }
        }
    }

    /// <summary>
    /// 動画再生前にPrepare（デコード準備）を行う
    /// </summary>
    private void PrepareVideo()
    {
        if (videoPlayer == null || isPrepared) return;
        isPrepared = true;

        // SetActive(true)にしてPrepareを実行（まだ再生はしない）
        videoPlayer.gameObject.SetActive(true);
        videoPlayer.Prepare();
    }

    /// <summary>
    /// Prepare完了のコールバック
    /// </summary>
    private void OnVideoPrepared(VideoPlayer vp)
    {
        // 準備完了。PlayVideoWithFadeがPlay()を呼び出すのを待つ
        Debug.Log("VideoPlayer: 準備完了");
    }

    /// <summary>
    /// フェードアウト ➡ 動画再生 ➡ フェードイン のシーケンス
    /// </summary>
    private IEnumerator PlayVideoWithFade()
    {
        if (videoPlayer == null) yield break;

        // まだPrepareが完了していなければ完了を待つ
        if (!videoPlayer.isPrepared)
        {
            videoPlayer.gameObject.SetActive(true);
            videoPlayer.Prepare();
            yield return new WaitUntil(() => videoPlayer.isPrepared);
        }

        // フェードアウト（画面を黒に）
        yield return StartCoroutine(Fade(0f, 1f));

        // UIを非表示
        foreach (var ui in uiToHide)
        {
            if (ui != null) ui.SetActive(false);
        }

        // 動画再生開始
        isVideoPlaying = true;
        videoPlayer.gameObject.SetActive(true);
        videoPlayer.Play();

        // フェードイン（画面を透明に）
        yield return StartCoroutine(Fade(1f, 0f));
    }

    /// <summary>
    /// フェードアウト ➡ UI復帰 ➡ 動画停止 ➡ フェードイン のシーケンス
    /// </summary>
    private IEnumerator StopVideoWithFade()
    {
        if (videoPlayer == null) yield break;

        // 二重実行防止
        if (isStopping) yield break;
        isStopping = true;
        isVideoPlaying = false;

        // フェードアウト（画面を黒に）
        yield return StartCoroutine(Fade(0f, 1f));

        // 動画停止
        videoPlayer.Stop();
        videoPlayer.gameObject.SetActive(false);
        isPrepared = false;

        // UIを再表示
        foreach (var ui in uiToHide)
        {
            if (ui != null) ui.SetActive(true);
        }

        // タイマーリセット
        idleTimer = 0f;
        hasStartedOnce = false;
        isStopping = false;

        // UIが描画されるまで1フレーム待つ（背景のチラ見え防止）
        yield return null;

        // フェードイン（画面を透明に）
        yield return StartCoroutine(Fade(1f, 0f));
    }

    /// <summary>
    /// フェード処理（alphaFrom から alphaTo へ変形）
    /// </summary>
    private IEnumerator Fade(float alphaFrom, float alphaTo)
    {
        if (fadeImage == null) yield break;

        float elapsed = 0f;
        while (elapsed < fadeDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / fadeDuration);
            fadeImage.color = new Color(0f, 0f, 0f, Mathf.Lerp(alphaFrom, alphaTo, t));
            yield return null;
        }
        fadeImage.color = new Color(0f, 0f, 0f, alphaTo);
    }

    /// <summary>
    /// 動画終了時のコールバック
    /// </summary>
    private void OnVideoFinished(VideoPlayer vp)
    {
        if (returnToTitleAfterVideo && isVideoPlaying && !isStopping)
        {
            StartCoroutine(StopVideoWithFade());
        }
    }

    void OnDestroy()
    {
        // イベント登録解除
        if (videoPlayer != null)
        {
            videoPlayer.loopPointReached -= OnVideoFinished;
            videoPlayer.prepareCompleted -= OnVideoPrepared;
        }
    }

    // ---------- New Input System Helpers ----------

    /// <summary>
    /// 現在のマウス位置を返す（Mouse.current が null の場合は Vector2.zero）
    /// </summary>
    private Vector2 GetMousePosition()
    {
        if (Mouse.current != null)
        {
            return Mouse.current.position.ReadValue();
        }
        return Vector2.zero;
    }

    /// <summary>
    /// マウスが移動したか（前フレームとの差分）
    /// </summary>
    private bool IsMouseMoved()
    {
        Vector2 current = GetMousePosition();
        return current != lastMousePosition;
    }

    /// <summary>
    /// 継続的に押されているか（タイマーリセット用）
    /// </summary>
    private bool AnyInputPressed()
    {
        // キーボードのチェック
        bool keyPressed = Keyboard.current != null && Keyboard.current.anyKey != null && Keyboard.current.anyKey.isPressed;

        // マウスのチェック
        bool mousePressed = false;
        if (Mouse.current != null)
        {
            mousePressed = Mouse.current.leftButton.isPressed ||
                          Mouse.current.rightButton.isPressed ||
                          Mouse.current.middleButton.isPressed;
        }

        // ゲームパッドのチェック
        bool gamepadPressed = false;
        if (Gamepad.current != null)
        {
            gamepadPressed = Gamepad.current.buttonSouth.isPressed ||
                            Gamepad.current.buttonEast.isPressed ||
                            Gamepad.current.buttonWest.isPressed ||
                            Gamepad.current.buttonNorth.isPressed ||
                            Gamepad.current.startButton.isPressed;
        }

        return keyPressed || mousePressed || gamepadPressed;
    }

    /// <summary>
    /// このフレームで押されたか（動画スキップ用）
    /// </summary>
    private bool AnyInputDown()
    {
        // キーボードのチェック
        bool keyDown = Keyboard.current != null && Keyboard.current.anyKey != null && Keyboard.current.anyKey.wasPressedThisFrame;

        // マウスのチェック
        bool mouseDown = false;
        if (Mouse.current != null)
        {
            mouseDown = Mouse.current.leftButton.wasPressedThisFrame ||
                       Mouse.current.rightButton.wasPressedThisFrame ||
                       Mouse.current.middleButton.wasPressedThisFrame;
        }

        // ゲームパッドのチェック
        bool gamepadDown = false;
        if (Gamepad.current != null)
        {
            gamepadDown = Gamepad.current.buttonSouth.wasPressedThisFrame ||
                         Gamepad.current.buttonEast.wasPressedThisFrame ||
                         Gamepad.current.buttonWest.wasPressedThisFrame ||
                         Gamepad.current.buttonNorth.wasPressedThisFrame ||
                         Gamepad.current.startButton.wasPressedThisFrame;
        }

        // タッチのチェック
        bool touchDown = false;
        if (Touchscreen.current != null && Touchscreen.current.primaryTouch != null)
        {
            touchDown = Touchscreen.current.primaryTouch.press.wasPressedThisFrame;
        }

        return keyDown || mouseDown || gamepadDown || touchDown;
    }

    /// <summary>
    /// タッチ入力が継続してされているか
    /// </summary>
    private bool AnyTouchPressed()
    {
        if (Touchscreen.current == null) return false;
        var primary = Touchscreen.current.primaryTouch;
        if (primary == null) return false;
        return primary.press.isPressed;
    }
}