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
    [Tooltip("タイトルへ戻る先のシーン名。親機／子機の各結果シーンで Inspector から上書きしてください" +
             "（既定は子機タイトル。親機の結果シーンでは ParentTitle を指定）。")]
    public string titleSceneName = "ChildTitle";

    [Header("復帰ボタン")]
    [Tooltip("「タイトルへ戻る」UIボタン／FAST復帰通知で使う短いフェード時間（秒）。0 で即座に遷移。")]
    public float returnButtonFadeDuration = 0.5f;

    private bool isFading = false;
    private bool _skipWaits = false;
    // 入力の持ち越し対策: 結果画面入場後に決定入力を一度離したか（離す前の押しっぱなしでは復帰しない）
    private bool _confirmInputReleased = false;
    private float elapsed = 0f;

    void Start()
    {
        // 入場時点で決定入力が押されていなければ「離した」状態とみなす。
        // （マウス/タッチの直接クリックを1フレーム目から妨げないため。
        //   押しっぱなしのまま入場した場合は false のままで、離すまで復帰しない）
        UpdateConfirmInputReleased();
    }

    void Update()
    {
        if (isFading)
        {
            return;
        }

        // 押しっぱなし検出は入力無視時間より前に行う
        // （UIのSubmit経由＝InputSystemUIInputModule → 選択中ButtonのOnClick でも同じ判定を使うため）
        UpdateConfirmInputReleased();

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

        // キーボード入力（既存の決定キー。新規キーは追加しない）
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
        // （キーボード／ゲームパッドの既存入力経路。UIボタンと同じ復帰メソッドへ集約する。新規キーは追加しない）
        if (isButtonPressed && _confirmInputReleased)
        {
            BeginReturnToTitle(notifyPeer: true, skipWaits: false);
        }
    }

    /// <summary>
    /// 決定入力（既存のA/B/X/Y・パッド4ボタン）が「いま離されている」フレームを観測したら
    /// _confirmInputReleased を立てる。結果画面入場時から押しっぱなしだった入力では復帰しないための判定。
    /// Update だけでなく、UIボタンの OnClick（InputSystemUIInputModule の Submit 経由）でも同じ判定を使う。
    /// </summary>
    private void UpdateConfirmInputReleased()
    {
        bool held = false;

        var kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.aKey.isPressed || kb.bKey.isPressed || kb.xKey.isPressed || kb.yKey.isPressed)
                held = true;
        }

        var gp = Gamepad.current;
        if (gp != null)
        {
            if (gp.buttonSouth.isPressed || gp.buttonEast.isPressed || gp.buttonWest.isPressed || gp.buttonNorth.isPressed)
                held = true;
        }

        if (!held)
        {
            _confirmInputReleased = true;
        }
    }

    /// <summary>
    /// 「タイトルへ戻る」演出の開始を相手側へ通知する（親機・子機の画面同期用）。
    /// 子機 → 親機: TEAM13_RETURN_TO_TITLE（再送付き）。親機 → 子機: 同メッセージ（再送付き）。
    /// 子機・親機のどちらのリザルト画面でもこのスクリプトを共用しているため、
    /// シーン内に存在する側の UDP コンポーネントへ処理を委譲する。
    /// </summary>
    private void NotifyReturnToTitle(bool fast)
    {
        // 選択順は従来どおり「子機が居れば子機、居なければ親機」。notifyPeer／fast の意味も変えない。
        ChildUdpRuntimeHost childHost = ResolveChildHost();
        if (childHost != null)
        {
            childHost.notifyReturnToTitle(fast);
            return;
        }

        ParentUdpRuntimeHost parentHost = ResolveParentHost();
        if (parentHost != null)
        {
            // Host側の対応入口（旧窓口 NotifyReturnToTitleToChild の委譲先と同じメソッド）。
            parentHost.NotifyReturnToTitleToChildPrepared(fast);
        }
    }

    /// <summary>稼働中の子機Hostを優先し、無ければ初期化済みHostを探す（未初期化Hostは使わない）。</summary>
    private static ChildUdpRuntimeHost ResolveChildHost()
    {
        ChildUdpRuntimeHost host = ChildUdpRuntimeHost.Running;
        if (host != null) return host;

        ChildUdpRuntimeHost found = FindFirstObjectByType<ChildUdpRuntimeHost>();
        return (found != null && found.IsConfigured) ? found : null;
    }

    /// <summary>稼働中の親機Hostを優先し、無ければ初期化済みHostを探す（未初期化Hostは使わない）。</summary>
    private static ParentUdpRuntimeHost ResolveParentHost()
    {
        ParentUdpRuntimeHost host = ParentUdpRuntimeHost.Running;
        if (host != null) return host;

        ParentUdpRuntimeHost found = FindFirstObjectByType<ParentUdpRuntimeHost>();
        return (found != null && found.IsConfigured) ? found : null;
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
    /// （後方互換の入口。実体は BeginReturnToTitle に集約している）
    /// </summary>
    public void StartFadeToTitle(bool notifyPeer = true)
    {
        BeginReturnToTitle(notifyPeer, skipWaits: false);
    }

    /// <summary>
    /// 相手端末からの RETURN_TO_TITLE 受信時に使う入口。
    /// 相手が &lt;sid&gt;:&lt;seq&gt;:FAST（ボタン起点）で復帰した場合のみ skipWaits=true となり、
    /// こちらも短いフェード（returnButtonFadeDuration）で合わせる。
    /// 旧形式（FASTなし）は skipWaits=false で、従来の演出を維持する。
    /// </summary>
    public void StartFadeToTitle(bool notifyPeer, bool skipWaits)
    {
        BeginReturnToTitle(notifyPeer, skipWaits);
    }

    /// <summary>
    /// 「タイトルへ戻る」UIボタンの OnClick から呼ぶ入口（子機の結果画面用）。
    /// 既存の待ち時間（テキスト表示待ちなど）をスキップし、短いフェードだけ（returnButtonFadeDuration）で
    /// タイトルへ戻る。相手側への RETURN_TO_TITLE 通知は既存処理（NotifyReturnToTitle）を再利用する。
    /// 切断中でも遷移は行い、相手の応答は待たない。
    /// </summary>
    public void OnReturnToTitleButtonClicked()
    {
        BeginReturnToTitle(notifyPeer: true, skipWaits: true);
    }

    /// <summary>
    /// 「タイトルへ戻る」の単一の入口。自動遷移・キーボード／ゲームパッド・UIボタン・
    /// 相手端末からの RETURN_TO_TITLE 受信のすべてがこのメソッドを通る。
    /// isFading ガードにより、長押し・連打・再送・受信が重なっても
    /// フェード／シーン遷移は1回だけ実行される。
    /// </summary>
    private void BeginReturnToTitle(bool notifyPeer, bool skipWaits)
    {
        if (isFading)
        {
            // すでに復帰演出中／遷移済み。重複実行しない。
            return;
        }

        isFading = true;
        _skipWaits = skipWaits;

        // 「タイトルへ戻る」演出の開始を相手側へ通知する（親機・子機の画面同期用）。
        // 相手からの受信が起点の場合は送り返さない（無限往復の防止）。
        // ボタン起点（skipWaits=true）は "FAST" を付けて送り、受信側も短いフェードにする。
        if (notifyPeer)
        {
            NotifyReturnToTitle(fast: skipWaits);
        }

        if (returnToTitleSe != null)
        {
            returnToTitleSe.Play();
        }

        StartCoroutine(FadeSequence());
    }

    private IEnumerator FadeSequence()
    {
        // skipWaits（UIボタン）のときは短いフェードのみ。それ以外は既存の待ち時間を維持する。
        float fadeOutSeconds = _skipWaits ? Mathf.Max(0f, returnButtonFadeDuration) : Mathf.Max(0f, fadeDuration);

        // 1. 画面を徐々に暗くする（フェードアウト）
        float elapsedTime = 0f;
        if (fadeImage != null)
        {
            Color imgColor = fadeImage.color;
            if (fadeOutSeconds > 0f)
            {
                while (elapsedTime < fadeOutSeconds)
                {
                    elapsedTime += Time.unscaledDeltaTime;
                    imgColor.a = Mathf.Clamp01(elapsedTime / fadeOutSeconds);
                    fadeImage.color = imgColor;
                    yield return null;
                }
            }
            // 確実に真っ黒にする
            imgColor.a = 1f;
            fadeImage.color = imgColor;
        }

        if (!_skipWaits)
        {
            // 2. テキストを徐々に表示する（フェードイン）
            elapsedTime = 0f;
            if (fadeText != null)
            {
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
            }

            // 3. テキストが表示された状態で少し待つ
            yield return new WaitForSecondsRealtime(textDuration);
        }

        // 4. タイトルシーンへ遷移
        SceneManager.LoadScene(SceneNameResolver.Resolve(titleSceneName));
    }
}
