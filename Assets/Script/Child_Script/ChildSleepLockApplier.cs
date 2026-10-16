using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// ChildSleepLockApplier：
/// 親機から届く睡眠ロック状態（SLEEP_LOCK / SLEEP_UNLOCK）を、子機のシーン側へ適用する担当。
///
/// 役割の境界：
///   ChildUdpRuntimeHost／ChildNotificationHub（通信側） — 受信・重複判定・状態保持（睡眠ロック／捕獲待機）・購読者への通知のみ。
///                               ゲーム処理（入力停止・画像表示・PlayerMove）は行わない。
///   本クラス（シーン側）      — 通知と「現在の状態」を購読し、シーン内のオブジェクトへ適用する。
///
/// ・購読は稼働中の ChildUdpRuntimeHost（DDOLの単一実体）が保持する Hub を基準にする。
/// ・購読成立時に「現在の IsSleepInputLocked」を取得して初期適用する
///   （親機が寝たまま子機がシーン遷移しても、同値LOCKの再送なしで正しい状態になる）。
/// ・無効化・破棄では購読を解除するだけ。UDP側のロック状態はシーン終了を理由に解除しない。
/// ・Host取得が遅れた場合は購読できるまで再試行する（実行順設定で取得順を固定しない）。
/// ・シーン内参照はInspector割当を優先する（未割当の機能は何もしない）。
/// </summary>
public class ChildSleepLockApplier : MonoBehaviour
{
    [Header("参照（Inspector割当を優先）")]
    [Tooltip("入力の有効／無効を切り替えるPlayerMove（ゲームシーンで割り当てる。未割当なら PlayerInputLock のみで停止）。")]
    [SerializeField] private PlayerMove playerMove;

    [Tooltip("睡眠中に表示する画像UI（未割当なら画像表示は行わない）。")]
    [SerializeField] private GameObject sleepLockImage;

    // ── 寝たふり中の画像の震え ────────────────────────────────────────────────
    //  ・寝たふり（SLEEP_LOCK）中に表示している画像だけを小さく震わせる（Canvas全体や他のUIは動かさない）。
    //  ・捕獲後の演出待機で画像が出ている場合は震わせない（寝たふりと重なっても待機中は止める）。
    //  ・画像の表示条件と入力停止の条件は、この設定では変えない。
    [Header("寝たふり中の画像の震え")]
    [Tooltip("寝たふり中に表示している画像だけを小さく震わせるか。OFFにすると震えません。")]
    [SerializeField] private bool sleepImageTremorEnabled = true;

    [Tooltip("左右の揺れの幅（UI座標）。0にすると震えません。初期値：18。X方向だけに動きます（Yは動きません）。")]
    [SerializeField, Min(0f)] private float sleepImageTremorAmplitude = 18f;

    [Tooltip("震えの速さ（1秒あたりの振動数）。大きいほど細かく速く震えます。初期値：16。")]
    [SerializeField, Min(0.1f)] private float sleepImageTremorFrequency = 16f;

    [Tooltip("「ブルブル」と「停止」のうち、左右に震えている時間（秒）。初期値：0.25。0にすると震えません。")]
    [SerializeField, Min(0f)] private float sleepImageTremorBurstSeconds = 0.25f;

    [Tooltip("「ブルブル」と「停止」のうち、元の位置で止まっている時間（秒）。初期値：0.5。0にすると連続で震えます。\n" +
             "停止中も画像の表示と入力ロックは維持されます（止まるのは画像の動きだけです）。")]
    [SerializeField, Min(0f)] private float sleepImageTremorPauseSeconds = 0.5f;

    [Header("Debug")]
    [Tooltip("適用状態が変化したときだけログを出す。")]
    [SerializeField] private bool showDebugLogs = false;

    // ── Spaceで寝たふりデバッグ（親機と接続していなくても子機単体で確認するための機能）──────
    //  ・押している間だけローカルで寝たふりを有効にする（トグルではない）。
    //  ・親機から受信した寝たふり状態は上書きしない（別枠のデバッグ状態としてORで合成する）。
    //  ・Editor と Development Build でのみ有効。展示用の通常ビルドでは動作しない。
    [Header("Spaceで寝たふりデバッグ（Editor／Development Buildのみ）")]
    [Tooltip("Spaceキーを押している間だけ、親機と接続していなくても子機ローカルで寝たふりを確認できます。\n" +
             "・画像表示・入力停止・画像の震えが、通常の寝たふりと同じように適用されます。\n" +
             "・押している間だけ有効です（トグルではありません）。離すとデバッグぶんだけ解除されます。\n" +
             "・親機から受信した寝たふり状態は上書きしません（親機が寝たふり中なら、Spaceを離してもロックは続きます）。\n" +
             "・捕獲待機中はSpaceを押しても画像は震えません。\n" +
             "・Editor または Development Build でのみ有効です。展示用ビルドでは、この値がONでも動作しません。\n" +
             "・アプリのフォーカスを失うと、デバッグ状態は解除されます。\n" +
             "・親機Hostが無い／未接続／購読前でも動作します（UDPには何も送りません）。")]
    [SerializeField] private bool enableSleepDebugKey = true;

    private bool _subscribed;
    private ChildUdpRuntimeHost _host;               // 実行時解決（Inspector割当なし）
    private ChildNotificationHub _subscribedHub;     // 実際に購読したHub（解除も同じ個体から行う）
    private bool _hasApplied;                       // 同値での不要な再適用を避ける
    private bool _appliedLocked;
    private bool _sleepLocked;                      // 寝たふり（SLEEP_LOCK）によるロック
    private bool _captureWaitLocked;                // 捕獲後の演出待機によるロック（寝たふりとは別枠）
    private bool _missingReceiverWarned;            // 警告は1回だけ

    // ── 寝たふり中の画像の震え用 ────────────────────────────────────────────────
    private RectTransform _sleepImageRect;          // sleepLockImage の RectTransform（1回だけ解決）
    private bool _sleepImageRectResolved;           // 解決を試みたか（毎フレーム GetComponent しない）
    private bool _sleepImageTremorRequested;        // 寝たふり中だけ震わせる（捕獲待機中は止める）
    private float _sleepImageTremorPhase;           // 震えの位相（止めても初期化しない）
    private Vector2 _appliedSleepImageOffset;       // 最後に加えたオフセット（差分適用でずれを残さない）

    // ── Spaceデバッグ用の状態 ──────────────────────────────────────────────────
    private bool _debugSleepHeld;                   // Spaceデバッグによる寝たふり（親機からの状態とは別枠）
    private bool _sleepEffective;                   // 寝たふり有効＝親機からの寝たふり OR Spaceデバッグ

    // ── 画像の震えパターン（左右だけ）────────────────────────────────────────
    private bool _sleepImageTremorInBurst = true;   // 現在ブルブル中か（震え開始時はブルブルから始める）
    private float _sleepImageTremorCycleTime;       // 現在のフェーズの経過時間（実時間で管理＝フレーム数に依存しない）

    private void OnEnable()
    {
        // 無効化をまたいでデバッグ由来の寝たふりを持ち越さない（再開時は未押下から始める）。
        // 親機から受信した寝たふり状態（_sleepLocked）と捕獲待機（_captureWaitLocked）には触れない。
        _debugSleepHeld = false;

        // 画像の震えパターンも初期化する（古いタイマーや位置ずれを残さない。再開時はブルブルから）
        ResetSleepImageTremorPattern();

        // 購読を試みる。購読が成立した時点で現在状態を「強制適用」する（初期同期）。
        // ここで別途 Apply を呼ぶと購読成立時と二重になるため呼ばない。
        // Receiver がまだ居ない場合は Update で再試行し、成立時に適用される。
        TrySubscribe();
    }

    private void Update()
    {
        // UDP取得が遅れている場合のみ再試行する（購読後は何もしない。Find等は使わない）。
        if (!_subscribed)
            TrySubscribe();

        // Spaceデバッグ：押している間だけローカル寝たふりを有効にする（トグルではない）。
        //  ・Host未取得／未接続／購読前でも動かす（デバッグ入力はUDPに依存させない）。
        //  ・変化したときだけ、UDP通知と同じ状態計算を通して適用する。
        bool held = IsSleepDebugKeyHeld();

        if (held != _debugSleepHeld)
        {
            _debugSleepHeld = held;
            ApplyEffectiveLock(force: false);
        }
    }

    private void OnDisable()
    {
        // 画像に加えた震えのオフセットを戻してから、購読を解除するだけ。
        // （UDP側のロック状態はシーン終了を理由に解除しない。SetActive(false)は画像側だけで、
        //   この制御スクリプト自身は止まらない構成のまま。）

        // デバッグ由来の寝たふりは残さない（通信由来の寝たふり・捕獲待機は勝手に解除しない）。
        if (_debugSleepHeld)
        {
            _debugSleepHeld = false;
            ApplyEffectiveLock(force: true);
        }

        ClearSleepImageTremor();
        Unsubscribe();
    }

    private void OnDestroy()
    {
        Unsubscribe();
    }

    /// <summary>
    /// 稼働中の子機Hostが保持するHubを購読する（重複購読しない）。
    ///  ・Host は Running を優先し、無ければ初期化済みHostのみ採用する（未初期化Hostは購読先にしない）。
    ///  ・取得できない場合は購読せず false を返す（OnEnable/Update で再試行する）。
    /// </summary>
    private bool TrySubscribe()
    {
        if (_subscribed) return true;
        if (!isActiveAndEnabled) return false;

        ChildUdpRuntimeHost host = ResolveHost();
        if (host == null || host.Notifications == null)
        {
            if (!_missingReceiverWarned)
            {
                _missingReceiverWarned = true;
                Debug.LogWarning("[ChildSleepLockApplier] 子機Host（未稼働／未初期化）が見つからないため、購読成立まで再試行します。", this);
            }
            return false;
        }

        _host = host;
        _subscribedHub = host.Notifications;
        _subscribedHub.SleepLockChanged += HandleSleepLockChanged;
        // 捕獲後の演出待機（親機の演出完了待ち）でも、寝たふりと同じ操作不可ロック・画像を使う。
        _subscribedHub.CaptureWaitStarted += HandleCaptureWaitStarted;
        _subscribedHub.CaptureSequenceReleased += HandleCaptureSequenceReleased;
        _subscribed = true;

        // 購読成立の時点で現在状態を「強制適用」する（初期同期）。
        // ・同値スキップは通常の通知だけに使う（購読成立・再有効化では確実に適用する）。
        // ・Host未取得だった時間帯に状態が変わっていても、ここで必ず現在値にそろう。
        _captureWaitLocked = _subscribedHub.WaitingGameOverSequence;
        Apply(_subscribedHub.IsSleepLocked, force: true);

        if (showDebugLogs)
            Debug.Log($"[ChildSleepLockApplier] 購読を開始しました（Host='{host.gameObject.name}', id={host.GetInstanceID()}）。", this);
        return true;
    }

    /// <summary>稼働中のHostを優先し、無ければ初期化済みHostを探す（未初期化Hostは返さない）。</summary>
    private static ChildUdpRuntimeHost ResolveHost()
    {
        ChildUdpRuntimeHost host = ChildUdpRuntimeHost.Running;
        if (host != null) return host;

        ChildUdpRuntimeHost found = FindFirstObjectByType<ChildUdpRuntimeHost>();
        return (found != null && found.IsConfigured) ? found : null;
    }

    /// <summary>実際に購読した同じHub個体から解除する（二重に呼んでも安全）。</summary>
    private void Unsubscribe()
    {
        if (_subscribed)
        {
            if (_subscribedHub != null)
            {
                _subscribedHub.SleepLockChanged -= HandleSleepLockChanged;
                _subscribedHub.CaptureWaitStarted -= HandleCaptureWaitStarted;
                _subscribedHub.CaptureSequenceReleased -= HandleCaptureSequenceReleased;
            }

            _subscribed = false;
            if (showDebugLogs)
                Debug.Log("[ChildSleepLockApplier] 購読を解除しました。", this);
        }

        _subscribedHub = null;
        _host = null;
    }

    private void HandleSleepLockChanged(bool locked)
    {
        if (!isActiveAndEnabled) return;   // 無効化中は反応しない（購読解除済みだが念のため）
        Apply(locked);                     // 通常の通知は同値スキップ（変化時だけ適用）
    }

    /// <summary>
    /// ロック状態をシーン側へ適用する。
    /// ・PlayerInputLock（静的）… 常に適用（PlayerMove／QTEManager が参照する唯一のゲート）。
    /// ・PlayerMove.SetInputEnabled … 割当があるときだけ（従来と同じ入力停止）。
    /// ・睡眠中画像 … 割当があるときだけ。
    /// force=false（通常の通知）では同値の再適用をしない。
    /// force=true（購読成立・再有効化時の初期同期）では現在状態を確実に適用する。
    /// </summary>
    private void Apply(bool locked, bool force = false)
    {
        _sleepLocked = locked;
        ApplyEffectiveLock(force);
    }

    /// <summary>
    /// 捕獲後の演出待機によるロック（寝たふりロックとは別枠。どちらかがONなら操作不可）。
    ///  ・通常の寝たふりをONにして代用しない（寝たふり解除の入力で演出待機が解けない）。
    ///  ・画像は既存の寝たふり用UI（sleepLockImage）を再利用する。
    /// </summary>
    private void SetCaptureWaitLock(bool locked)
    {
        _captureWaitLocked = locked;
        ApplyEffectiveLock(force: true);
    }

    private void HandleCaptureWaitStarted()
    {
        if (!isActiveAndEnabled) return;

        Debug.Log("[ChildSleepLockApplier] 捕獲演出の待機に入りました（寝たふりとは別状態で、操作不可ロックと画像を適用します）。");
        SetCaptureWaitLock(true);
    }

    private void HandleCaptureSequenceReleased(string reason)
    {
        if (!isActiveAndEnabled) return;

        Debug.Log($"[ChildSleepLockApplier] 捕獲演出の待機を解除しました（理由={reason}）。");
        SetCaptureWaitLock(false);
    }

    /// <summary>
    /// 実効ロック（寝たふりロック OR 捕獲演出待機ロック）をシーン側へ適用する。
    /// ・PlayerInputLock（静的）… 常に適用（PlayerMove／QTEManager が参照する唯一のゲート）。
    /// ・PlayerMove.SetInputEnabled … 割当があるときだけ（従来と同じ入力停止）。
    /// ・睡眠中画像 … 割当があるときだけ。
    /// force=false（通常の通知）では同値の再適用をしない。
    /// force=true（購読成立・再有効化・捕獲待機の切り替え）では現在状態を確実に適用する。
    /// </summary>
    /// <summary>
    /// 現在の状態から「寝たふり有効／操作ロック／画像表示／画像の震え」を計算して適用する。
    ///  ・寝たふり有効＝親機からの寝たふり OR Spaceデバッグ（デバッグ専用の状態を別に持ち、受信値は上書きしない）
    ///  ・操作ロック・画像表示＝寝たふり有効 OR 捕獲待機
    ///  ・画像の震え＝寝たふり有効 AND 捕獲待機ではない
    /// UDP通知・購読成立時の初期同期・Spaceデバッグ入力のどの経路からも、この同じ計算を通す。
    /// force=false（通常の通知）では同値の再適用をしない。
    /// force=true（購読成立・再有効化・捕獲待機の切り替え・デバッグ解除）では現在状態を確実に適用する。
    /// </summary>
    private void ApplyEffectiveLock(bool force)
    {
        // 寝たふり有効：親機からの状態をSpaceデバッグで上書きせず、別枠のORで合成する
        _sleepEffective = _sleepLocked || _debugSleepHeld;

        // 震えの可否は「実効ロック」ではなく内訳で決める（寝たふり中だけ／捕獲待機中は止める）。
        //  ・実効ロックが同じでも内訳が変われば、ここで震えの開始・停止が正しく更新される。
        //  ・この更新は下の「同値スキップ（早期return）」より前で行う。
        bool tremorRequested = _sleepEffective && !_captureWaitLocked;
        if (tremorRequested != _sleepImageTremorRequested)
        {
            // 震えの開始／停止が切り替わったときだけパターンを初期化する。
            //  ・同じ状態の再適用（LOCK再送など）ではリセットしない＝毎回先頭に戻らない。
            //  ・開始時は待機ではなくブルブルから始める。
            //  ・停止（寝たふり解除・捕獲待機への切り替え）ではパターン時間をリセットする。
            ResetSleepImageTremorPattern();
            _sleepImageTremorRequested = tremorRequested;
        }
        ResolveSleepImageRectIfNeeded();

        bool locked = _sleepEffective || _captureWaitLocked;

        if (!force && _hasApplied && _appliedLocked == locked) return;
        _hasApplied = true;
        _appliedLocked = locked;

        PlayerInputLock.SetLocked(locked);

        if (playerMove != null)
            playerMove.SetInputEnabled(!locked);

        if (sleepLockImage != null)
            sleepLockImage.SetActive(locked);

        if (showDebugLogs)
            Debug.Log($"[ChildSleepLockApplier] 操作不可ロックを適用しました | locked={locked}" +
                      $"（sleep={_sleepEffective}: parent={_sleepLocked}, debug={_debugSleepHeld}, captureWait={_captureWaitLocked}） | " +
                      $"force={force} | playerMove={(playerMove != null)} | image={(sleepLockImage != null)}", this);
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  寝たふり中の画像の震え
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// sleepLockImage の RectTransform を必要なときに1回だけ解決する。
    ///  ・毎フレーム GetComponent を呼ばない。
    ///  ・参照が未設定でも例外にしない（次回の適用時に再挑戦する）。
    /// </summary>
    private void ResolveSleepImageRectIfNeeded()
    {
        if (_sleepImageRectResolved) return;
        if (sleepLockImage == null) return;

        _sleepImageRect = sleepLockImage.GetComponent<RectTransform>();
        _sleepImageRectResolved = true;
    }

    /// <summary>
    /// 寝たふり中に表示している画像だけを小さく震わせる。
    ///  ・動かすのは sleepLockImage の RectTransform の位置だけ（Canvas全体や他のUIは動かさない）。
    ///  ・自分が加えた差分だけを毎フレーム入れ替える（毎フレーム加算して位置がずれる方式にしない）。
    ///  ・震えが不要になったら、加えたオフセットを必ず0へ戻す（元の位置に戻す）。
    /// </summary>
    private void LateUpdate()
    {
        if (_sleepImageRect == null)
        {
            ClearSleepImageTremor();
            return;
        }

        Vector2 desiredOffset = Vector2.zero;

        if (sleepImageTremorEnabled && _sleepImageTremorRequested)
        {
            // 「左右に素早くブルブル → 元の位置で停止」を、フレーム数ではなく経過時間で繰り返す。
            float burstSeconds = Mathf.Max(0f, sleepImageTremorBurstSeconds);
            float pauseSeconds = Mathf.Max(0f, sleepImageTremorPauseSeconds);

            _sleepImageTremorCycleTime += Time.deltaTime;

            if (_sleepImageTremorInBurst)
            {
                // ブルブル中：時間が来たら停止へ（時間0なら即停止する）
                if (burstSeconds <= 0f || _sleepImageTremorCycleTime >= burstSeconds)
                {
                    _sleepImageTremorInBurst = false;
                    _sleepImageTremorCycleTime = 0f;
                }
            }
            else
            {
                // 停止中：時間が来たらブルブルへ（位相を0へ戻して必ず0から揺らし始める）
                if (pauseSeconds <= 0f || _sleepImageTremorCycleTime >= pauseSeconds)
                {
                    _sleepImageTremorInBurst = true;
                    _sleepImageTremorCycleTime = 0f;
                    _sleepImageTremorPhase = 0f;
                }
            }

            if (_sleepImageTremorInBurst && burstSeconds > 0f)
            {
                _sleepImageTremorPhase += Time.deltaTime * Mathf.Max(0.1f, sleepImageTremorFrequency);

                float amplitude = Mathf.Max(0f, sleepImageTremorAmplitude);
                if (amplitude > 0f)
                {
                    // 左右（X）だけを揺らす。Yは常に0＝元の縦位置を保つ（上下には動かさない）
                    desiredOffset = new Vector2(Mathf.Sin(_sleepImageTremorPhase) * amplitude, 0f);
                }
            }
            // 停止中は offset 0 のまま＝元の位置で待機する（直前の揺れ位置で止めない）
        }

        ApplySleepImageTremorOffset(desiredOffset);
    }

    /// <summary>
    /// 震えのオフセットを「前回のぶんを戻してから新しいぶんを足す」形で適用する。
    /// 位置を毎フレーム足し続けないため、震えが積み上がってずれることがない。
    /// </summary>
    private void ApplySleepImageTremorOffset(Vector2 desiredOffset)
    {
        if (_sleepImageRect == null)
        {
            _appliedSleepImageOffset = Vector2.zero;
            return;
        }

        if (_appliedSleepImageOffset == desiredOffset) return;

        _sleepImageRect.anchoredPosition += (desiredOffset - _appliedSleepImageOffset);
        _appliedSleepImageOffset = desiredOffset;
    }

    /// <summary>
    /// 画像の震えパターンを初期状態へ戻す（開始時は「ブルブル」から始める）。
    /// ・フレーム数ではなく経過時間で管理しているため、ここではフェーズ時間と位相だけを初期化する。
    /// ・停止（寝たふり解除・捕獲待機）でも呼び、タイマーを残さない。
    /// </summary>
    private void ResetSleepImageTremorPattern()
    {
        _sleepImageTremorInBurst = true;   // 待機からではなくブルブルから始める
        _sleepImageTremorCycleTime = 0f;
        _sleepImageTremorPhase = 0f;
    }

    /// <summary>画像に加えた震えのオフセットを0へ戻す（元の位置へ戻す）。</summary>
    private void ClearSleepImageTremor()
    {
        if (_appliedSleepImageOffset == Vector2.zero) return;
        ApplySleepImageTremorOffset(Vector2.zero);
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Spaceで寝たふりデバッグ
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Spaceデバッグが「押されている」状態か。
    ///  ・デバッグ設定OFF／非対応ビルド／フォーカスなし／Keyboard未接続では false（＝デバッグ解除）。
    ///  ・Keyboard.current が null でも例外にしない。
    ///  ・入力ロック中でも直接デバイスを読むため、離したことを検出できる。
    /// </summary>
    private bool IsSleepDebugKeyHeld()
    {
        if (!enableSleepDebugKey) return false;                 // OFFにしたらデバッグ状態を解除して再計算させる
        if (!IsSleepDebugSupportedInThisBuild()) return false;   // 展示用ビルドでは動作させない
        if (!Application.isFocused) return false;                // フォーカスを失ったらデバッグ状態を解除する

        Keyboard keyboard = Keyboard.current;
        if (keyboard == null) return false;                      // Input System 未初期化でも例外にしない

        return keyboard.spaceKey.isPressed;
    }

    /// <summary>
    /// Spaceデバッグを許可するビルドか。
    ///  ・Editor と Development Build でのみ true。
    ///  ・展示用（通常）ビルドでは、Inspectorの値がONでもSpaceデバッグを使わない。
    /// </summary>
    private static bool IsSleepDebugSupportedInThisBuild()
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        return true;
#else
        return false;
#endif
    }
}
