using UnityEngine;

/// <summary>
/// ChildSleepLockApplier：
/// 親機から届く睡眠ロック状態（SLEEP_LOCK / SLEEP_UNLOCK）を、子機のシーン側へ適用する担当。
///
/// 役割の境界：
///   ChildUdpReceiver（UDP側） — 受信・重複判定・状態保持（isSleepInputLocked）・購読者への通知のみ。
///                               ゲーム処理（入力停止・画像表示・PlayerMove）は行わない。
///   本クラス（シーン側）      — 通知と「現在の状態」を購読し、シーン内のオブジェクトへ適用する。
///
/// ・購読は生存している ChildUdpReceiver（instance、DDOLの単一実体）を基準にする。
/// ・購読成立時に「現在の IsSleepInputLocked」を取得して初期適用する
///   （親機が寝たまま子機がシーン遷移しても、同値LOCKの再送なしで正しい状態になる）。
/// ・無効化・破棄では購読を解除するだけ。UDP側のロック状態はシーン終了を理由に解除しない。
/// ・UDP取得が遅れた場合は購読できるまで再試行する（Findや名前検索は行わない）。
/// ・シーン内参照はInspector割当を優先する（未割当の機能は何もしない）。
/// </summary>
public class ChildSleepLockApplier : MonoBehaviour
{
    [Header("参照（Inspector割当を優先）")]
    [Tooltip("未設定の場合は ChildUdpReceiver.instance（DDOLの生存個体）を使う。")]
    [SerializeField] private ChildUdpReceiver udpReceiver;

    [Tooltip("入力の有効／無効を切り替えるPlayerMove（ゲームシーンで割り当てる。未割当なら PlayerInputLock のみで停止）。")]
    [SerializeField] private PlayerMove playerMove;

    [Tooltip("睡眠中に表示する画像UI（未割当なら画像表示は行わない）。")]
    [SerializeField] private GameObject sleepLockImage;

    [Header("Debug")]
    [Tooltip("適用状態が変化したときだけログを出す。")]
    [SerializeField] private bool showDebugLogs = false;

    private bool _subscribed;
    private ChildUdpReceiver _subscribedReceiver;   // 購読解除を確実にするため保持する
    private bool _hasApplied;                       // 同値での不要な再適用を避ける
    private bool _appliedLocked;
    private bool _missingReceiverWarned;            // 警告は1回だけ

    private void OnEnable()
    {
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
    }

    private void OnDisable()
    {
        // 購読を解除するだけ。UDP側のロック状態はシーン終了を理由に解除しない。
        Unsubscribe();
    }

    private void OnDestroy()
    {
        Unsubscribe();
    }

    /// <summary>生存ChildUdpReceiverを購読する（重複購読しない）。</summary>
    private bool TrySubscribe()
    {
        if (_subscribed) return true;
        if (!isActiveAndEnabled) return false;

        ChildUdpReceiver receiver = udpReceiver != null ? udpReceiver : ChildUdpReceiver.instance;
        if (receiver == null)
        {
            if (!_missingReceiverWarned)
            {
                _missingReceiverWarned = true;
                Debug.LogWarning("[ChildSleepLockApplier] ChildUdpReceiver が見つからないため、購読成立まで再試行します。", this);
            }
            return false;
        }

        _subscribedReceiver = receiver;
        _subscribedReceiver.SleepLockChanged += HandleSleepLockChanged;
        _subscribed = true;

        // 購読成立の時点で現在状態を「強制適用」する（初期同期）。
        // ・同値スキップは通常の通知だけに使う（購読成立・再有効化では確実に適用する）。
        // ・Receiver が未取得だった時間帯に状態が変わっていても、ここで必ず現在値にそろう。
        Apply(receiver.IsSleepInputLocked, force: true);

        if (showDebugLogs)
            Debug.Log($"[ChildSleepLockApplier] 購読を開始しました（receiver='{receiver.gameObject.name}', id={receiver.GetInstanceID()}）。", this);
        return true;
    }

    /// <summary>保持している購読先から解除する（二重に呼んでも安全）。</summary>
    private void Unsubscribe()
    {
        if (_subscribed)
        {
            if (_subscribedReceiver != null)
                _subscribedReceiver.SleepLockChanged -= HandleSleepLockChanged;

            _subscribed = false;
            if (showDebugLogs)
                Debug.Log("[ChildSleepLockApplier] 購読を解除しました。", this);
        }

        _subscribedReceiver = null;
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
        if (!force && _hasApplied && _appliedLocked == locked) return;
        _hasApplied = true;
        _appliedLocked = locked;

        PlayerInputLock.SetLocked(locked);

        if (playerMove != null)
            playerMove.SetInputEnabled(!locked);

        if (sleepLockImage != null)
            sleepLockImage.SetActive(locked);

        if (showDebugLogs)
            Debug.Log($"[ChildSleepLockApplier] 睡眠ロックを適用しました | locked={locked} | force={force} | " +
                      $"playerMove={(playerMove != null)} | image={(sleepLockImage != null)}", this);
    }
}
