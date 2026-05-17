using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;
using UnityEngine.SceneManagement;
using UnityEngine.InputSystem;

/// <summary>
/// タイトル画面で一定時間放置すると動画を再生するコンポーネント
/// 動画開始時にシームレスなフェードイン/アウトを行う
/// </summary>
public class TitleScreenVideoPlayer : MonoBehaviour
{
    [Header("待機時間設定")]
    [Tooltip("動画再生までの待機時間(秒)")]
    [SerializeField] private float idleTime = 30f;

    [Header("動画設定")]
    [Tooltip("VideoPlayerコンポーネント")]
    [SerializeField] private VideoPlayer videoPlayer;
    
    [Tooltip("動画再生中に非表示にするUI(タイトルロゴなど)")]
    [SerializeField] private GameObject[] uiToHide;

    [Header("動画終了後の設定")]
    [Tooltip("動画終了後にタイトル画面に戻るか")]
    [SerializeField] private bool returnToTitleAfterVideo = true;

    [Tooltip("動画をループ再生するか")]
    [SerializeField] private bool loopVideo = false;

    [Header("フェード設定")]
    [Tooltip("フェードに使用する全画面黒UIのImageコンポーネント\n(Canvas配下の黒いRawImageまたはImageをアサイン)")]
    [SerializeField] private Image fadeImage;

    [Tooltip("フェードの時間(秒)")]
    [SerializeField] private float fadeDuration = 0.4f;

    private float idleTimer = 0f;
    private bool isVideoPlaying = false;
    private Vector3 lastMousePosition;
    private bool hasStartedOnce = false;
    private bool isPrepared = false;

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

        // フェード画像の初期化(完全透明)
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

        // initialize lastMousePosition using new Input System
        lastMousePosition = GetMousePosition();
    }

    void Update()
    {
        if (isVideoPlaying)
        {
            // 動画再生中に何か入力があれば動画をスキップ
            if (AnyInputDown())
            {
                StartCoroutine(StopVideoWithFade());
            }
            return;
        }

        // 入力検知(キー入力、マウスクリック、マウス移動)
        bool hasInput = AnyInputPressed() ||
                       IsMouseMoved() ||
                       AnyTouchPressed();

        // update last mouse position for comparison
        lastMousePosition = GetMousePosition();

        if (hasInput)
        {
            // 入力があったらタイマーをリセット
            idleTimer = 0f;

            // Prepareしていたらキャンセル
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

            // 指定時間の少し前(フェード時間分)にPrepare開始
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
    /// 動画を事前にPrepare(デコード準備)する
    /// </summary>
    private void PrepareVideo()
    {
        if (videoPlayer == null || isPrepared) return;
        isPrepared = true;

        // SetActive(true)してPrepareだけ実行(まだ再生しない)
        videoPlayer.gameObject.SetActive(true);
        videoPlayer.Prepare();
    }

    /// <summary>
    /// Prepare完了コールバック
    /// </summary>
    private void OnVideoPrepared(VideoPlayer vp)
    {
        // Prepare完了。PlayVideoWithFadeのPlay()呼び出しを待つだけ。
        Debug.Log("VideoPlayer: 準備完了");
    }

    /// <summary>
    /// フェードアウト → 動画再生 → フェードイン のシーケンス
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

        // フェードアウト(画面を黒く)
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

        // フェードイン(黒を消す)
        yield return StartCoroutine(Fade(1f, 0f));
    }

    /// <summary>
    /// フェードアウト → UI復元 → 動画停止 → フェードイン のシーケンス
    /// </summary>
    private IEnumerator StopVideoWithFade()
    {
        if (videoPlayer == null) yield break;

        // 二重実行防止
        isVideoPlaying = false;

        // フェードアウト(画面を黒く)
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

        // フェードイン(黒を消す)
        yield return StartCoroutine(Fade(1f, 0f));
    }

    /// <summary>
    /// フェード処理(alphaFrom → alphaTo)
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
        if (returnToTitleAfterVideo && isVideoPlaying)
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

    // Helper: get current mouse position using new Input System
    private Vector3 GetMousePosition()
    {
        if (Mouse.current != null)
        {
            Vector2 pos = Mouse.current.position.ReadValue();
            return new Vector3(pos.x, pos.y, 0f);
        }
        return Vector3.zero;
    }

    // Helper: check if mouse moved since last frame
    private bool IsMouseMoved()
    {
        Vector3 current = GetMousePosition();
        return current != lastMousePosition;
    }

    // Helper: check any input pressed (continuous)
    private bool AnyInputPressed()
    {
        bool keyPressed = Keyboard.current != null && Keyboard.current.anyKey != null && Keyboard.current.anyKey.isPressed;
        bool mousePressed = Mouse.current != null && (Mouse.current.leftButton.isPressed || Mouse.current.rightButton.isPressed);
        bool gamepadPressed = Gamepad.current != null && (
            Gamepad.current.buttonSouth.isPressed ||
            Gamepad.current.buttonNorth.isPressed ||
            Gamepad.current.buttonEast.isPressed ||
            Gamepad.current.buttonWest.isPressed);
        return keyPressed || mousePressed || gamepadPressed;
    }

    // Helper: check any input down (triggered this frame)
    private bool AnyInputDown()
    {
        bool keyDown = Keyboard.current != null && Keyboard.current.anyKey != null && Keyboard.current.anyKey.wasPressedThisFrame;
        bool mouseDown = Mouse.current != null && (Mouse.current.leftButton.wasPressedThisFrame || Mouse.current.rightButton.wasPressedThisFrame);
        bool gamepadDown = Gamepad.current != null && (
            Gamepad.current.buttonSouth.wasPressedThisFrame ||
            Gamepad.current.buttonNorth.wasPressedThisFrame ||
            Gamepad.current.buttonEast.wasPressedThisFrame ||
            Gamepad.current.buttonWest.wasPressedThisFrame);
        bool touchDown = Touchscreen.current != null && Touchscreen.current.primaryTouch != null && Touchscreen.current.primaryTouch.press.wasPressedThisFrame;
        return keyDown || mouseDown || gamepadDown || touchDown;
    }

    // Helper: check any touch currently pressed
    private bool AnyTouchPressed()
    {
        return Touchscreen.current != null && Touchscreen.current.primaryTouch != null && Touchscreen.current.primaryTouch.press.isPressed;
    }
}