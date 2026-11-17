using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// SleepVisionEffectController：
/// SleepingController.IsSleepingに応じて、2つのUIまぶたRectTransformを動かす。
/// 睡眠中：上まぶたを下へ、下まぶたを上へ動かして閉じた位置に近づける。
/// 起きているとき：両方のまぶたを開いた位置へ戻す。
/// 表示専用で、ゲームプレイのロジックは持たない。
/// </summary>
public class SleepVisionEffectController : MonoBehaviour
{
    // ── 参照 ────────────────────────────────────────────────────────────────
    [Header("参照")]
    [Tooltip("IsSleeping状態の参照元。未設定の場合はStart時に自動検索します。")]
    public SleepingController sleepingController;

    [Tooltip("上まぶた画像のRectTransform。")]
    public RectTransform upperEyelid;

    [Tooltip("下まぶた画像のRectTransform。")]
    public RectTransform lowerEyelid;

    // ── 上まぶたの位置 ────────────────────────────────────────────────────────
    [Header("上まぶたの位置")]
    [Tooltip("目が完全に開いているときの上まぶたのanchoredPosition。")]
    public Vector2 upperOpenAnchoredPos = new Vector2(0f, 100f);

    [Tooltip("睡眠／閉じた状態のときの上まぶたのanchoredPosition。")]
    public Vector2 upperClosedAnchoredPos = new Vector2(0f, 0f);

    // ── 下まぶたの位置 ────────────────────────────────────────────────────────
    [Header("下まぶたの位置")]
    [Tooltip("目が完全に開いているときの下まぶたのanchoredPosition。")]
    public Vector2 lowerOpenAnchoredPos = new Vector2(0f, -100f);

    [Tooltip("睡眠／閉じた状態のときの下まぶたのanchoredPosition。")]
    public Vector2 lowerClosedAnchoredPos = new Vector2(0f, 0f);

    // ── 遷移 ────────────────────────────────────────────────────────────────
    [Header("遷移")]
    [Tooltip("MoveTowards補間の1秒あたりの移動量。大きいほど速くスライドします。")]
    public float transitionSpeed = 300f;

    // ── 寝たふり中のカメラの震え（調整元は SleepingController に一本化）─────────
    //  ・有効／強さ／速さの設定は SleepingController が保持する（ここには置かない＝二重管理にしない）。
    //  ・このコンポーネントは、実際にカメラを動かす処理だけを担当する。
    //  ・SleepingController.IsSleeping が true の間だけ震わせ、捕獲／ゲームオーバーのカメラ演出中は止める。
    //  ・基本は位置の小さな揺れだけで、画面に対して「左右」だけに震わせる（上下・前後は動かさない。回転も加えない）。
    [Tooltip("震わせるカメラ。未設定なら起動時に Camera.main から1回だけ解決します。")]
    public Camera tremorCamera;

    [Tooltip("捕獲・ゲームオーバーのカメラ演出。未設定なら起動時に1回だけ自動検索します。\n" +
             "この演出が動いている間は、演出を優先して震えを停止します。")]
    public GameOverCameraSequenceController gameOverCameraSequence;

    // 震えの位相（止めても初期化しない。LOCK通知の再送で最初から再生しないため）
    private float _tremorPhase;

    // 現在このスクリプトが加えている震えのオフセット（0なら加えていない）
    private Vector3 _appliedTremorOffset;

    // 震えを適用した直後の「自分が書いたカメラ位置」（外部上書きの検出用）
    private Vector3 _lastWrittenTremorPosition;

    // 一度でも震えで位置を書いたか（書いていない状態では位置に触れない）
    private bool _hasWrittenTremorPosition;

    // ── 外部の絶対位置上書きを検出するための比較許容値（位置差の二乗）──────────
    // ・別処理（例：ゲームオーバーのカメラ演出）がカメラ位置を「絶対値」で書き戻すと、
    //   自分が加えた震えのオフセットはその時点で消えている。
    //   それでも前回オフセットを引くと、カメラが逆向きに余分へずれてしまうため、
    //   「最後に自分が書いた位置のままか」を比較してから取り除く。
    // ・値は浮動小数点誤差と Vector3 の近似比較に合わせたごく小さい二乗距離
    //   （約0.1mm＝1e-4単位の差までを「同じ位置」とみなす）。
    // ・この比較は「別処理による絶対位置の上書き」への対策であり、
    //   あらゆる外部移動を判別できる仕組みではない。
    private const float TremorWritePositionToleranceSqr = 1e-8f;

    // ── クリア時の閉眼・暗転演出（PA07）──────────────────────────────────────
    //  ・02:00（TIME_UP）到達時に ParentGameTimeManager から開始される。
    //  ・開始後は通常の SleepingController.IsSleeping による開閉を上書きし、
    //    プレイヤーがキー／枕を離しても目が再び開かないように固定する。
    //  ・既に寝たふり中でまぶたが閉じている（または閉じかけている）場合は、
    //    現在位置から開かずにそのまま閉じ切る。
    private bool _isClearSleepSequenceActive;
    private Image _clearBlackoutOverlay;

    /// <summary>クリア時の閉眼・暗転演出が進行中か。</summary>
    public bool IsClearSleepSequenceActive => _isClearSleepSequenceActive;

    // ──────────────────────────────────────────────────────────────────────────
    //  Unityライフサイクル
    // ──────────────────────────────────────────────────────────────────────────

    private void Start()
    {
        if (sleepingController == null)
            sleepingController = Object.FindFirstObjectByType<SleepingController>();

        if (upperEyelid != null)
            upperEyelid.anchoredPosition = upperOpenAnchoredPos;

        if (lowerEyelid != null)
            lowerEyelid.anchoredPosition = lowerOpenAnchoredPos;

        _isClearSleepSequenceActive = false;

        // 震え用の参照は起動時に1回だけ解決する（毎フレーム Find/GetComponent を呼ばない）。
        if (tremorCamera == null)
            tremorCamera = Camera.main;

        if (gameOverCameraSequence == null)
            gameOverCameraSequence = Object.FindFirstObjectByType<GameOverCameraSequenceController>();
    }

    private void Update()
    {
        // クリア演出（PA07）進行中はコルーチン側でまぶたと暗転を制御する（通常開閉で上書きしない）。
        if (_isClearSleepSequenceActive)
            return;

        bool sleeping = sleepingController != null && sleepingController.IsSleeping;

        Vector2 upperTarget = sleeping ? upperClosedAnchoredPos : upperOpenAnchoredPos;
        Vector2 lowerTarget = sleeping ? lowerClosedAnchoredPos : lowerOpenAnchoredPos;

        float step = transitionSpeed * Time.deltaTime;

        if (upperEyelid != null)
            upperEyelid.anchoredPosition = Vector2.MoveTowards(upperEyelid.anchoredPosition, upperTarget, step);

        if (lowerEyelid != null)
            lowerEyelid.anchoredPosition = Vector2.MoveTowards(lowerEyelid.anchoredPosition, lowerTarget, step);
    }

    /// <summary>
    /// 【PA07】02:00到達時の「眠るように画面が閉じる」閉眼・暗転演出を実行する。
    ///  ・通常の寝たふり入力（IsSleeping）を無視して強制的に閉眼方向へのみ進める。
    ///  ・既にまぶたが閉じている場合は一度開いたりせず、そのまま暗転保持へ進む。
    ///  ・まぶたの隙間や別UI（怪しさメーター・ビネット）が透けないよう、
    ///    SleepingUI を最前面へ移動しつつ完全黒オーバーレイを合わせてフェードインさせる。
    /// </summary>
    public System.Collections.IEnumerator PlayClearSleepSequence(float closeDurationSeconds, float blackoutHoldSeconds)
    {
        _isClearSleepSequenceActive = true;
        ClearTremorOffset();

        // 他のUI（DangerVignette / Suspicion UI）より手前にまぶた・暗転が出るよう最前面へ移動する。
        transform.SetAsLastSibling();

        Image blackout = EnsureClearBlackoutOverlay();

        float fullUpperDist = Vector2.Distance(upperOpenAnchoredPos, upperClosedAnchoredPos);
        float fullLowerDist = Vector2.Distance(lowerOpenAnchoredPos, lowerClosedAnchoredPos);

        Vector2 startUpper = upperEyelid != null ? upperEyelid.anchoredPosition : upperClosedAnchoredPos;
        Vector2 startLower = lowerEyelid != null ? lowerEyelid.anchoredPosition : lowerClosedAnchoredPos;

        float remainUpper = Vector2.Distance(startUpper, upperClosedAnchoredPos);
        float remainLower = Vector2.Distance(startLower, lowerClosedAnchoredPos);

        float upperRatio = fullUpperDist > 0.001f ? Mathf.Clamp01(remainUpper / fullUpperDist) : 0f;
        float lowerRatio = fullLowerDist > 0.001f ? Mathf.Clamp01(remainLower / fullLowerDist) : 0f;
        float remainRatio = Mathf.Max(upperRatio, lowerRatio);

        // 既に閉じかけ／閉じている場合は残り距離に応じて時間を短縮し、一度開く動きを出さない。
        float baseCloseDuration = Mathf.Max(0.1f, closeDurationSeconds);
        float effectiveCloseDuration = baseCloseDuration * remainRatio;

        if (effectiveCloseDuration > 0.01f)
        {
            float elapsed = 0f;
            while (elapsed < effectiveCloseDuration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / effectiveCloseDuration);
                // 眠りに落ちるような柔らかいイージング（SmoothStep）
                float eased = Mathf.SmoothStep(0f, 1f, t);

                if (upperEyelid != null)
                    upperEyelid.anchoredPosition = Vector2.Lerp(startUpper, upperClosedAnchoredPos, eased);

                if (lowerEyelid != null)
                    lowerEyelid.anchoredPosition = Vector2.Lerp(startLower, lowerClosedAnchoredPos, eased);

                if (blackout != null)
                {
                    // まぶたが閉じ切る後半にかけて完全暗転へ馴染ませる
                    float overallProgress = Mathf.Lerp(1f - remainRatio, 1f, eased);
                    float alpha = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.35f, 1f, overallProgress));
                    blackout.color = new Color(0f, 0f, 0f, alpha);
                }

                yield return null;
            }
        }

        if (upperEyelid != null)
            upperEyelid.anchoredPosition = upperClosedAnchoredPos;

        if (lowerEyelid != null)
            lowerEyelid.anchoredPosition = lowerClosedAnchoredPos;

        if (blackout != null)
            blackout.color = Color.black;

        float hold = Mathf.Max(0f, blackoutHoldSeconds);
        // 既に寝たふり中で閉眼時間が0秒だった場合も、クリアの余韻として最低限の暗転間を確保する。
        if (effectiveCloseDuration <= 0.05f)
            hold = Mathf.Max(hold, 0.8f);

        if (hold > 0f)
            yield return new WaitForSeconds(hold);
    }

    /// <summary>完全暗転を保証するための全画面黒オーバーレイを準備する。</summary>
    private Image EnsureClearBlackoutOverlay()
    {
        if (_clearBlackoutOverlay != null)
        {
            _clearBlackoutOverlay.gameObject.SetActive(true);
            _clearBlackoutOverlay.transform.SetAsLastSibling();
            return _clearBlackoutOverlay;
        }

        GameObject go = new GameObject("ClearBlackoutOverlay", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        go.transform.SetParent(transform, false);
        go.transform.SetAsLastSibling();

        RectTransform rt = go.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        rt.localScale = Vector3.one;

        _clearBlackoutOverlay = go.GetComponent<Image>();
        _clearBlackoutOverlay.color = new Color(0f, 0f, 0f, 0f);
        _clearBlackoutOverlay.raycastTarget = false;
        return _clearBlackoutOverlay;
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  寝たふり中のカメラの震え
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 寝たふり中だけカメラを小さく震わせる。
    ///  ・他のスクリプトが書いた現在位置はそのままに、自分が加えた差分だけを毎フレーム入れ替える
    ///    （毎フレーム加算して位置がずれる方式にしない。基準位置の固定もしない）。
    ///  ・震えが不要になったら、自分が加えたオフセットを必ず0へ戻す（ずれを残さない）。
    /// </summary>
    private void LateUpdate()
    {
        // クリア閉眼演出（PA07）が動いている間は、寝たふりの震えを止める
        if (_isClearSleepSequenceActive)
        {
            ClearTremorOffset();
            return;
        }

        // 震えの有効／強さ／速さは SleepingController 側の設定を毎フレーム参照する
        // （プレイ中にInspectorで変更した内容がそのまま反映される）
        bool tremorEnabled = sleepingController != null && sleepingController.SleepCameraTremorEnabled;

        // 無効化・カメラ未解決のときは、加えたぶんを戻してから何もしない
        if (!tremorEnabled || tremorCamera == null)
        {
            ClearTremorOffset();
            return;
        }

        bool sleeping = sleepingController.IsSleeping;
        // 捕獲／ゲームオーバーのカメラ演出が動いている間は、その演出を優先して震えを止める
        bool suppressedByCameraSequence = IsGameOverCameraSequenceActive();

        Vector3 desiredOffset = Vector3.zero;

        if (sleeping && !suppressedByCameraSequence)
        {
            // 位相は止まっている間も保持する（LOCK通知の再送や再開で最初から再生しないため）
            _tremorPhase += Time.deltaTime * sleepingController.SleepCameraTremorFrequency;

            float amplitude = sleepingController.SleepCameraTremorAmplitude;   // 負値は0として取得される
            if (amplitude > 0f)
            {
                // 画面に対して左右だけの揺れにする。回転は加えない（既存のカメラ演出を壊さない）。
                //  ・カメラのローカルX（＝カメラの右方向）だけを使い、上下(Y)と前後(Z)へは動かさない。
                //  ・ワールド座標のXを直接使うのではなく、カメラの向きを基準に左右方向を作る。
                Vector3 localRightOffset = new Vector3(Mathf.Sin(_tremorPhase), 0f, 0f) * amplitude;

                // カメラの右方向へずらす（元の位置は変更せず、加えるオフセットの方向だけを左右に限定する）
                desiredOffset = tremorCamera.transform.rotation * localRightOffset;
            }
        }

        ApplyTremorOffsetSafely(desiredOffset);
    }

    /// <summary>捕獲・ゲームオーバーのカメラ演出が動いているか（動いている間は震えを止める）。</summary>
    private bool IsGameOverCameraSequenceActive()
    {
        if (gameOverCameraSequence == null) return false;
        return gameOverCameraSequence.IsRunning || gameOverCameraSequence.IsHoldingFinalPose;
    }

    /// <summary>
    /// 震えのオフセットを安全に適用する（「取り除き」と「新しい適用」を常に同じ手順で行う）。
    ///  1) 前回オフセットを取り除く前に、現在位置が「最後に自分が書いた位置」のままかを確認する。
    ///  2) そのままなら、前回オフセットを取り除く（自分のぶんだけを戻す）。
    ///  3) 外部が絶対位置で上書きしていたら、前回オフセットは既に消えているので引かない。
    ///  4) どちらの経路でも、取り除き済み／無効になった記録はクリアする（二重に引かない）。
    ///  5) 新しいオフセットを加える場合は、その後に新しいオフセットと書き込み位置を記録する。
    ///  ※停止時だけガードするのではなく、通常の適用処理でも毎回この手順を通す。
    ///  ※3)は「別処理による絶対位置の上書き」への対策であり、あらゆる外部移動を判別できる仕組みではない。
    /// </summary>
    private void ApplyTremorOffsetSafely(Vector3 desiredOffset)
    {
        Transform target = (tremorCamera != null) ? tremorCamera.transform : null;
        if (target == null)
        {
            // カメラが無い／破棄済み：記録だけクリアする（位置には触れない）
            ClearTremorBookkeeping();
            return;
        }

        // ── 1) 前回オフセットの取り除き（外部上書きの確認つき）──
        if (_hasWrittenTremorPosition || _appliedTremorOffset != Vector3.zero)
        {
            // 現在位置が「最後に自分が書いた位置」のままか（＝外部が上書きしていないか）を確認する
            bool positionIsStillOurs =
                _hasWrittenTremorPosition &&
                (target.position - _lastWrittenTremorPosition).sqrMagnitude <= TremorWritePositionToleranceSqr;

            if (positionIsStillOurs)
            {
                // 自分のオフセットだけを取り除く（元の位置へ戻す）
                target.position -= _appliedTremorOffset;
            }
            // 外部が上書きしていた場合は、前回オフセットは既に消えているので引かない

            // どちらの経路でも、取り除き済み／無効になった記録はクリアする（二重に引かない）
            ClearTremorBookkeeping();
        }

        // ── 2) 新しいオフセットの適用 ──
        if (desiredOffset == Vector3.zero) return;   // 震えなし（何も書かず、記録も持たない）

        target.position += desiredOffset;

        // 適用（書き込み）の後で、新しいオフセットと書き込んだ位置を記録する
        _appliedTremorOffset = desiredOffset;
        _lastWrittenTremorPosition = target.position;
        _hasWrittenTremorPosition = true;
    }

    /// <summary>震えのオフセット記録をクリアする（位置には触れない）。</summary>
    private void ClearTremorBookkeeping()
    {
        _appliedTremorOffset = Vector3.zero;
        _lastWrittenTremorPosition = Vector3.zero;
        _hasWrittenTremorPosition = false;
    }

    /// <summary>
    /// 自分が加えた震えのオフセットを取り除く。
    /// ・通常停止／OnDisable／OnDestroy で同じこの処理を使う。
    /// ・2回呼ばれても、記録がクリア済みなら位置には触れない（二重に引かない）。
    /// </summary>
    private void ClearTremorOffset()
    {
        ApplyTremorOffsetSafely(Vector3.zero);
    }

    private void OnDisable()
    {
        // コンポーネント無効化・シーン切り替え時に、自分が加えた揺れを戻す
        ClearTremorOffset();
    }

    private void OnDestroy()
    {
        ClearTremorOffset();
    }
}
