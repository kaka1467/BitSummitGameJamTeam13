using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// ChildUdpReceiver:
/// Handles all UDP communication on the child side.
///
/// Outbound messages (child → parent, port 8002):
///   TEAM13_START_GAME        — child requests game start
///   TEAM13_TIME_UP           — child timer expired
///   TEAM13_CHILD_DEAD        — child died
///   TEAM13_RANK_SYNC:<version>:<sourceId>:<updateNo>:<GOlast>:<GO0..GO4>:<TUlast>:<TU0..TU4>:<latestPlayId>:<latestType>
///                            — 子機を唯一の基準とするランキング／スコアのスナップショット（親機は表示用コピーとして保持）
///   TEAM13_LOUD_ITEM         — child picked up loud item
///   TEAM13_PING              — heartbeat
///   TEAM13_RETURN_TO_TITLE   — child returned to title → parent also returns
///
/// Inbound messages (parent → child, port 8000):
///   TEAM13_START_GAME   — load game scene
///   TEAM13_CAUGHT       — parent game-over → child game-over
///   TEAM13_SLEEP_LOCK   — disable child player input
///   TEAM13_SLEEP_UNLOCK — re-enable child player input
///   TEAM13_PING         — heartbeat from parent
///   TEAM13_RETURN_TO_TITLE — parent returned to title → child also returns
///   TEAM13_RANK_SYNC_ACK:<version>:<sourceId>:<updateNo> — parent applied the snapshot → child stops resending
/// </summary>
public class ChildUdpReceiver : MonoBehaviour
{
    private const string MAGIC_NUMBER = "TEAM13_";
    private const string CMD_START = "START_GAME";

    public enum ConnectionState { Disconnected, Connecting, Connected }

    // ── Inspector ─────────────────────────────────────────────────────────────
    public int normalPort = 8000;
    public int broadcastPort = 8001;
    public int parentReceivePort = 8002;
    public string targetIP = "127.0.0.1";
    public ConnectionState currentState = ConnectionState.Disconnected;
    public string lastMessage = "";
    public string gameSceneName = "ChildLoading";
    public string titleSceneName = "ChildeTitle";

    public SleepingManager sleepingManager;

    [Header("Game References")]
    [Tooltip("診断ログ用のPlayerMove参照（Inspector割当のみ。睡眠ロックの適用は ChildSleepLockApplier が担当）。")]
    public PlayerMove playerMove;

    [Header("Debug")]
    [Tooltip("通信ログなどの詳細出力を有効にする")]
    [SerializeField] private bool showDebugLogs = true;

    // 子機が現在適用している睡眠ロック状態（重複パケット処理の抑制用）
    private bool isSleepInputLocked = false;
    private bool caughtHandled = false;
    // 購読者（ゲーム側の受信担当＝GameManager）がまだ準備できていない間に受理したCAUGHTを1件だけ保留する。
    //  ・caughtHandled（=現在プレイで受理済み）とは別に管理する。
    //    受理済みでも保留が残っていれば、購読成立後にその1件を消費して通知する。
    //  ・保留には「受理時のプレイ識別子」を記録し、同一プレイでのみ消費する（次プレイへ持ち越さない）。
    //  ・履歴は持たない（再送で増やさない／上書きして別イベントにしない）。
    private bool _hasPendingCaught = false;
    private string _pendingCaughtPlayId = "";

    // ── RETURN_TO_TITLE のプレイ識別（古い再送・遅延パケットの無視用） ─────────────────────
    // 連番だけでは「前回プレイの、まだ届いていない番号の通知」を区別できない。
    // また片端末の再起動で連番が1に戻ると大小比較が破綻する。
    // そのため「プレイ識別子（両端末で共有）」＋「そのプレイ内の連番」の2段で判定する。
    // 子機はゲーム開始時に新しい識別子を発行し、START_GAME:<sid> で親機へ伝える。
    private string _playSessionId = "";

    // 子機（このプロセス）が RETURN_TO_TITLE を送るたびに単調増加する連番。アプリ実行中は決してリセットしない。
    // 同じプレイ内での重複・再送を弾くために使う（プレイ識別子が一致する場合のみ意味を持つ）。
    private int _returnToTitleSendSeq = 0;
    private int _lastPeerReturnToTitleSeq = 0;

    // ── ランキング同期（スナップショット）────────────────────────────────────
    // 子機が唯一の基準。最新スナップショットをACKまで保持し、回数・間隔を決めて再送する。
    // 消さない設計にすることで、復帰や切断、アプリ終了をまたいでも同期を取りこぼさない。
    private const float RankSyncResendInterval = 0.5f;
    private const int   RankSyncMaxAttempts = 6;   // 0.5s x 6 = 約3秒で一旦停止（保持は継続）

    private const string PrefRankSyncSourceId  = "RankSync_SourceId";   // 同期元ID（この子機データの識別）
    private const string PrefRankSyncUpdateNo  = "RankSync_UpdateNo";   // 更新番号（単調増加・永続）
    private const string PrefRankSyncConfirmed = "RankSync_Confirmed";  // 現在の更新番号がACK済みか
    private const string PrefRankSyncPending   = "RankSync_PendingMessage"; // 未確認スナップショット全文（そのまま再送するため）
    private const string PrefRankSyncSessionSeq = "RankSync_SessionSeq";    // 接続確認の世代（起動ごとに+1）

    private string _rankSyncSourceId = "";
    private int    _rankSyncUpdateNo = 0;
    private int    _rankSyncSessionSeq = 0;
    private bool   _rankSyncConfirmed = true;
    private string _pendingRankSyncMessage = "";
    // 親機が今の接続確認用に発行した ID。これを受信してから接続確認の応答（HELLO）を返す。
    private string _rankSyncChallengeId = "";
    private Coroutine _rankSyncResendCoroutine;

    // ── Private networking ────────────────────────────────────────────────────
    // 通信基盤（UdpChannel）へ委譲する：ソケット生成・bind・送信・受信スレッド・停止／解放は基盤側が持つ。
    //  ChildUdpReceiver 自身は同じソケットを別途生成しない（受信内容の解釈とゲーム処理だけを行う）。
    private UdpChannel _udpChannel;
    private volatile bool isRunning = false;

    // ログ用のエントリ構造体
    private struct LogEntry
    {
        public LogType type;
        public string message;

        public LogEntry(LogType type, string message)
        {
            this.type = type;
            this.message = message;
        }
    }

    private readonly ConcurrentQueue<string> messageQueue = new ConcurrentQueue<string>();
    private readonly ConcurrentQueue<Action> actionQueue = new ConcurrentQueue<Action>();
    private readonly ConcurrentQueue<LogEntry> logQueue = new ConcurrentQueue<LogEntry>();

    private Coroutine discoveryCoroutine;
    private Coroutine heartbeatCoroutine;
    private float lastReceiveTime;
    private float pingInterval = 1.0f;
    private float timeoutLimit = 3.0f;
    private bool gameSceneLoaded = false;

    public static ChildUdpReceiver instance { get; private set; }

    // ── 接続状態の通知（タイトルUIなどへ状態変化だけを伝える）─────────────────
    /// <summary>
    /// 接続状態が変化したときにメインスレッドで発火する（同値では発火しない）。
    /// 受信スレッドからUIイベントを直接呼ばないよう、状態変更は必ず SetConnectionState 経由で行う。
    /// </summary>
    public event Action<ConnectionState> ConnectionStateChanged;

    /// <summary>現在の接続状態（タイトルUIの初回同期用の読み取りAPI）。</summary>
    public ConnectionState CurrentConnectionState => currentState;

    // ── 睡眠ロックの通知（SLEEP_LOCK / SLEEP_UNLOCK）─────────────────────────
    /// <summary>
    /// 親機の睡眠ロック状態（SLEEP_LOCK=true / SLEEP_UNLOCK=false）が変化したときに
    /// メインスレッドで発火する。同値（冗長なLOCK/UNLOCK・同値の再送）では発火しない。
    /// 適用（入力停止・PlayerMove・睡眠中画像）は購読者が行う。
    /// </summary>
    public event Action<bool> SleepLockChanged;

    /// <summary>現在の睡眠ロック状態（購読前の初期同期用の読み取りAPI）。true=親機が睡眠中でロック中。</summary>
    public bool IsSleepInputLocked => isSleepInputLocked;

    // ── CAUGHT（親機の捕獲）の通知 ────────────────────────────────────────────
    /// <summary>
    /// 親機の捕獲（CAUGHT）を受理したときにメインスレッドで発火する。
    /// ゲーム側の受信担当（GameManager）が購読し、結果処理（TriggerResult）を行う。
    ///  ・受理（caughtHandled）はUDP側が持つ（シーンをまたぐ「現在プレイで一度だけ」の状態）。
    ///  ・購読者が居ない間の受理は1件だけ保留し、購読成立後に通知する（取りこぼさない）。
    ///  ・再送は caughtHandled で無視する（保留を増やさない・別イベントにしない）。
    /// </summary>
    public event Action CaughtReceived;

    /// <summary>ゲーム側の受信担当（購読者）が準備済みか。</summary>
    public bool HasCaughtSubscriber => CaughtReceived != null;

    // ── 捕獲演出の待機（親機の演出完了まで結果画面へ進ませない）────────────────
    /// <summary>捕獲を受理し、演出待機状態へ入ったときに発火する（入力ロック・画像表示の適用側が購読）。</summary>
    public event Action CaptureWaitStarted;

    /// <summary>演出完了（またはフォールバック）で待機を解除したときに、理由付きで発火する。</summary>
    public event Action<string> CaptureSequenceReleased;

    /// <summary>
    /// 捕獲演出の状態。「未捕獲」と「完了済み」を区別するため（「待機中ではない」だけで完了扱いにしない）。
    ///  ・None      ：このプレイでは捕獲を受理しておらず、完了通知も受けていない
    ///  ・Waiting   ：捕獲を受理し、親機の演出完了通知を待っている
    ///  ・Completed ：演出完了（または子機側の上限）で待機を解除済み
    /// </summary>
    public enum CaptureWaitState
    {
        None = 0,
        Waiting = 1,
        Completed = 2,
    }

    private bool _waitingGameOverSequence;
    private CaptureWaitState _captureWaitState = CaptureWaitState.None;
    private string _captureWaitPlayId = "";           // 状態が属するプレイID（初期同期の判定に使う）
    private string _sequenceDoneHandledPlayId = "";   // 完了通知を処理済みのプレイID（重複・再送を無視）
    private string _sequenceDonePendingPlayId = "";   // 捕獲通知より先に届いた完了通知（順序入れ替わり対応）

    /// <summary>捕獲演出の待機中か（親機の演出完了待ち）。</summary>
    public bool IsWaitingGameOverSequence => _waitingGameOverSequence;

    /// <summary>現在プレイの捕獲演出の状態（未捕獲／待機中／完了済み）。</summary>
    public CaptureWaitState CurrentCaptureWaitState => _captureWaitState;

    /// <summary>
    /// 【初期同期】GameManager が購読成立時に確認する「現在プレイの演出完了が確定済みか」。
    ///  ・Completed のときだけ true（未捕獲 None を完了扱いにしない）。
    ///  ・購読より先に完了通知が処理されていても、ここで true になるため取りこぼさない。
    ///  ・プレイIDが分かる場合は現在プレイと一致するときだけ完了済みとみなす（前回プレイを混ぜない）。
    /// </summary>
    public bool IsCaptureWaitCompletedForCurrentPlay
    {
        get
        {
            if (_captureWaitState != CaptureWaitState.Completed) return false;

            if (!string.IsNullOrEmpty(_captureWaitPlayId) &&
                !string.IsNullOrEmpty(_playSessionId) &&
                _captureWaitPlayId != _playSessionId)
            {
                return false;
            }

            return true;
        }
    }

    /// <summary>捕獲を受理したので演出待機に入る（通常の寝たふりとは別の状態として保持する）。</summary>
    private void EnterCaptureWait(string reason)
    {
        if (_waitingGameOverSequence) return;

        _waitingGameOverSequence = true;
        _captureWaitState = CaptureWaitState.Waiting;
        _captureWaitPlayId = _playSessionId ?? "";
        CaptureWaitStarted?.Invoke();

        Debug.Log($"[ChildUdpReceiver] 捕獲演出の待機を開始しました（理由={reason}, playSessionId='{_playSessionId}'）。" +
                  "結果画面へは演出完了通知を受けてから進みます。");

        // 順序入れ替わり：完了通知が先に届いていた場合は、待機へ入った時点で即解除する。
        if (!string.IsNullOrEmpty(_sequenceDonePendingPlayId) &&
            (string.IsNullOrEmpty(_playSessionId) || _sequenceDonePendingPlayId == _playSessionId))
        {
            ReleaseCaptureWait($"演出完了通知が捕獲通知より先に届いていた（playId='{_sequenceDonePendingPlayId}'）");
        }
    }

    /// <summary>演出待機を解除する（結果画面へ進める）。二重に呼んでも1回だけ通知する。</summary>
    private void ReleaseCaptureWait(string reason)
    {
        // 完了扱いは「待機中だったとき」だけ立てる（未捕獲を完了扱いにしない）。
        if (!_waitingGameOverSequence)
        {
            return;
        }

        _waitingGameOverSequence = false;
        _captureWaitState = CaptureWaitState.Completed;
        if (string.IsNullOrEmpty(_captureWaitPlayId)) _captureWaitPlayId = _playSessionId ?? "";

        Debug.Log($"[ChildUdpReceiver] 捕獲演出の待機を解除しました（理由={reason}）。");
        CaptureSequenceReleased?.Invoke(reason);
    }

    /// <summary>
    /// 【子機側の上限】GameManager の待機タイムアウトから呼ぶ。
    ///  ・待機を解除して「完了済み」にそろえ、入力適用側（ChildSleepLockApplier）に古いロックを残さない。
    ///  ・結果遷移は呼び出し側（GameManager）が行う。この通知だけで遷移しない
    ///    （購読者がいない／タイムアウト経路でも二重に結果処理しない）。
    /// </summary>
    public void ReleaseCaptureWaitByLocalTimeout(string reason)
    {
        if (!_waitingGameOverSequence)
        {
            _captureWaitState = CaptureWaitState.Completed;
            if (string.IsNullOrEmpty(_captureWaitPlayId)) _captureWaitPlayId = _playSessionId ?? "";
            Debug.Log($"[ChildUdpReceiver] 子機側の上限で待機終了を記録しました（既に解除済み。理由={reason}）");
            return;
        }

        ReleaseCaptureWait($"子機側の上限（{reason}）");
    }

    /// <summary>
    /// 待機・完了通知の状態を初期化する（再プレイで残さない）。
    ///  ・リセットではイベントを発火しない（誤って結果遷移を起こさない）。
    /// </summary>
    private void ResetCaptureWaitState(string reason)
    {
        _waitingGameOverSequence = false;
        _captureWaitState = CaptureWaitState.None;
        _captureWaitPlayId = "";
        _sequenceDoneHandledPlayId = "";
        _sequenceDonePendingPlayId = "";
        Debug.Log($"[ChildUdpReceiver] 捕獲演出の待機・完了通知の状態を初期化しました（理由={reason}）。");
    }

    /// <summary>
    /// 演出完了通知（SEQUENCE_DONE:&lt;playSessionId&gt;:&lt;reason&gt;）を処理する。
    ///  ・前回プレイの遅延通知は無視する（今回の待機を解除しない）。
    ///  ・重複・再送は無視するが、親機の再送を止めるため ACK は必ず返す。
    ///  ・捕獲通知より先に届いた場合は保留し、待機開始時に即解除する。
    /// </summary>
    private void HandleSequenceDoneMessage(string playId, string reason)
    {
        // 前回プレイの遅延通知（別プレイID）は無視する。ACKも返さない（親機の再送を今回のプレイで止めない）。
        if (!string.IsNullOrEmpty(playId) && !string.IsNullOrEmpty(_playSessionId) && playId != _playSessionId)
        {
            Debug.Log($"[ChildUdpReceiver] SEQUENCE_DONE ignored（前回プレイの遅延通知）: playId='{playId}' / current='{_playSessionId}'");
            return;
        }

        string effectivePlayId = string.IsNullOrEmpty(playId) ? _playSessionId : playId;

        // 受領確認を返す（再送を止める）。
        SendState($"SEQUENCE_DONE_ACK:{effectivePlayId}");

        if (!string.IsNullOrEmpty(effectivePlayId) && _sequenceDoneHandledPlayId == effectivePlayId)
        {
            Debug.Log($"[ChildUdpReceiver] SEQUENCE_DONE は処理済み（重複・再送）: playId='{effectivePlayId}'（ACKのみ返しました）");
            return;
        }

        if (string.IsNullOrEmpty(effectivePlayId))
        {
            Debug.LogWarning("[ChildUdpReceiver] SEQUENCE_DONE のプレイIDが空です。現在プレイとして扱います");
        }

        _sequenceDoneHandledPlayId = effectivePlayId;

        if (_waitingGameOverSequence)
        {
            ReleaseCaptureWait($"演出完了通知を受信（playId='{effectivePlayId}', reason='{reason}'）");
            return;
        }

        // 捕獲通知（CAUGHT）より先に届いた場合は保留する（待機開始時に即解除する）。
        _sequenceDonePendingPlayId = effectivePlayId;
        Debug.Log($"[ChildUdpReceiver] SEQUENCE_DONE を保留しました（捕獲通知より先に受信。待機開始時に解除します）: playId='{effectivePlayId}', reason='{reason}'");
    }

    /// <summary>購読前に受理したCAUGHTの保留が残っているか（ゲーム側の初期同期の問い合わせ用）。</summary>
    public bool HasPendingCaught => _hasPendingCaught;

    /// <summary>
    /// 睡眠ロック状態を更新し、変化があった場合だけ購読者へ通知する（同値では何もしない・通知しない）。
    /// 購読者が居ない場合も状態は正しく更新する（購読成立時の初期同期は IsSleepInputLocked を使う）。
    /// </summary>
    private void SetSleepInputLocked(bool next)
    {
        if (isSleepInputLocked == next) return;

        isSleepInputLocked = next;
        SleepLockChanged?.Invoke(next);
    }

    /// <summary>
    /// 接続状態を更新し、変化があった場合だけ通知する。
    ///  ・同値なら何もしない（通知もログも出さない）。
    ///  ・受信スレッドから変更する場合は actionQueue 経由（メインスレッド）で呼ぶこと。
    ///  ・既存の public フィールド currentState は従来どおりの名前・型のまま残す。
    /// </summary>
    private void SetConnectionState(ConnectionState next)
    {
        if (currentState == next) return;

        currentState = next;
        ConnectionStateChanged?.Invoke(next);
    }

    // ── Button callbacks ──────────────────────────────────────────────────────
    public void OnConnectButtonClicked()
    {
        if (showDebugLogs)
            Debug.Log($"[StartTrace][子機] ChildUdpReceiver.OnConnectButtonClicked ENTER — scene='{SceneManager.GetActiveScene().name}', currentState={currentState}, gameSceneLoaded={gameSceneLoaded}, id={GetInstanceID()}", this);

        if (currentState == ConnectionState.Connected)
        {
            if (showDebugLogs)
                Debug.Log($"[StartTrace][子機] OnConnectButtonClicked → 分岐=START (Connected なので OnStartButtonClicked を呼ぶ)", this);
            OnStartButtonClicked();
        }
        else
        {
            SetConnectionState(ConnectionState.Connecting);
            if (showDebugLogs)
                Debug.Log($"[StartTrace][子機] OnConnectButtonClicked → 分岐=Connect (Connected ではないため Connecting にして接続操作へ。currentState={currentState})", this);
        }
    }

    public void OnCancelButtonClicked() { SetConnectionState(ConnectionState.Disconnected); }

    // クレジット／設定パネルの開閉（タイトルUI）は ChildTitleConnectionUI へ移した。
    // 本クラスには接続・START・Cancel（通信と開始）の入口だけを残す。

    public void OnStartButtonClicked()
    {
        // 中止条件は無し（このメソッドに return は存在しない）。入口の状態を記録する。
        if (showDebugLogs)
            Debug.Log($"[StartTrace][子機] ChildUdpReceiver.OnStartButtonClicked ENTER — scene='{SceneManager.GetActiveScene().name}', currentState={currentState}, gameSceneLoaded={gameSceneLoaded}, titleSceneName='{titleSceneName}', gameSceneName='{gameSceneName}', id={GetInstanceID()}", this);

        // このプレイの識別子を新しく発行し、開始通知に載せて親機と共有する。
        // 以降の RETURN_TO_TITLE はこの識別子で「どのプレイの通知か」を判定する。
        _playSessionId = CreatePlaySessionId();
        _lastPeerReturnToTitleSeq = 0;

        // 子機起点の新しいプレイ開始でも、前プレイの受理済み状態と保留を持ち越さない。
        caughtHandled = false;
        ClearPendingCaught("新プレイ開始（子機起点）");
        ResetCaptureWaitState("新プレイ開始（子機起点）");

        if (showDebugLogs)
            Debug.Log($"[StartTrace][子機] OnStartButtonClicked → START_GAME 送信へ (sessionId={_playSessionId}, 送信先={targetIP}:{parentReceivePort})", this);

        SendState($"{CMD_START}:{_playSessionId}");
        Debug.Log($"[ChildUdpReceiver] Sent START_GAME (playSessionId={_playSessionId}) to parent at {targetIP}:{parentReceivePort}");
        LoadGameScene();
    }

    /// <summary>
    /// 新しいプレイ識別子を発行する（再起動・再接続をまたいでも衝突しない一意トークン）。
    /// </summary>
    private static string CreatePlaySessionId()
    {
        return $"{System.Guid.NewGuid():N}";
    }

    /// <summary>
    /// <summary>
    /// ランキング／今回スコアが変化したときに、最新スナップショットを作成して親機へ送る。
    /// GameManager.TriggerResult（結果確定後）と RankingResetButton（リセット後）から呼ぶ。
    /// latestResultType は "GAME_OVER" / "TIME_UP" / "NONE"（リセット時）。
    /// 子機側のランキング保存は親機の ACK を待たない（ここでは送信だけを管理する）。
    /// </summary>
    public void NotifyRankingChanged(string latestResultType)
    {
        string type = string.IsNullOrEmpty(latestResultType) ? RankingSnapshot.TypeNone : latestResultType;
        string latestPlayId = (type == RankingSnapshot.TypeNone) ? "" : (_playSessionId ?? "");

        if (type != RankingSnapshot.TypeNone && string.IsNullOrEmpty(latestPlayId))
        {
            Debug.LogWarning("[ChildUdpReceiver] NotifyRankingChanged: playId is empty — sending snapshot with latestType=NONE.");
            type = RankingSnapshot.TypeNone;
        }

        _rankSyncUpdateNo++;
        _rankSyncConfirmed = false;
        _pendingRankSyncMessage = BuildRankSyncMessage(latestPlayId, type);
        PersistRankSyncState();

        if (showDebugLogs)
            Debug.Log($"[ChildUdpReceiver] RankSync update={_rankSyncUpdateNo} latest={type}:{latestPlayId} queued.");

        StartRankSyncResend("changed");
    }

    /// <summary>
    /// 接続完了・再接続時に最新スナップショットを送る（親機の再起動後もこれで表示が復元される）。
    /// 未確認のスナップショットがあれば「同じ更新番号・同じ内容」を再送する。
    /// </summary>
    public void SendRankSyncOnConnect()
    {
        if (!_rankSyncConfirmed && !string.IsNullOrEmpty(_pendingRankSyncMessage))
        {
            StartRankSyncResend("reconnect");
            return;
        }

        // 接続時は表示用の同期のみ（latestType=NONE）。画面遷移は結果確定時の同期でのみ行う。
        _rankSyncUpdateNo++;
        _rankSyncConfirmed = false;
        _pendingRankSyncMessage = BuildRankSyncMessage("", RankingSnapshot.TypeNone);
        PersistRankSyncState();

        if (showDebugLogs)
            Debug.Log($"[ChildUdpReceiver] RankSync update={_rankSyncUpdateNo} sent on connect.");

        StartRankSyncResend("connect");
    }

    /// <summary>子機の既存キー（不変）から、送信用スナップショットを組み立てる。</summary>
    private string BuildRankSyncMessage(string latestPlayId, string latestResultType)
    {
        int goLast = PlayerPrefs.GetInt("LastGameOverScore", 0);
        int[] goTop = ReadRankingKeys("GameOverRank_");
        int tuLast = PlayerPrefs.GetInt("LastTimeUpScore", 0);
        int[] tuTop = ReadRankingKeys("TimeUpRank_");

        return RankingSnapshot.Format(_rankSyncSourceId, _rankSyncUpdateNo, goLast, goTop, tuLast, tuTop, latestPlayId, latestResultType);
    }

    private static int[] ReadRankingKeys(string keyPrefix)
    {
        int[] r = new int[RankingSnapshot.TopSize];
        for (int i = 0; i < r.Length; i++)
        {
            r[i] = PlayerPrefs.GetInt(keyPrefix + i, 0);
        }
        return r;
    }

    /// <summary>ACKが来るまで、同じスナップショットを回数・間隔を決めて再送する（毎フレーム送らない）。</summary>
    private void StartRankSyncResend(string reason)
    {
        if (string.IsNullOrEmpty(_pendingRankSyncMessage)) return;

        // 接続確認の要求が未受信なら、もう一度要求する（親機は同じ challengeId を再送する）
        RequestChallengeIfNeeded();

        // 接続確認の応答（HELLO）→ スナップショットの順で送る。再送でも同じ処理を繰り返す。
        SendRankSyncHello();
        SendState(_pendingRankSyncMessage);

        if (_rankSyncResendCoroutine != null)
        {
            StopCoroutine(_rankSyncResendCoroutine);
        }
        _rankSyncResendCoroutine = StartCoroutine(RankSyncResendRoutine(reason));
    }

    /// <summary>
    /// 接続確認要求（RANK_SYNC_CHALLENGE）がまだ届いていないときだけ、接続元識別つきの
    /// DISCOVERY_REQUEST を1回送って要求し直す（親機は同じ接続確認なら同じ ID を再送する）。
    /// </summary>
    private void RequestChallengeIfNeeded()
    {
        if (!string.IsNullOrEmpty(_rankSyncChallengeId)) return;

        try
        {
            byte[] data = Encoding.UTF8.GetBytes(MAGIC_NUMBER + BuildDiscoveryRequest());
            SendToUdp(data, "255.255.255.255", broadcastPort);
            if (showDebugLogs)
                Debug.Log("[ChildUdpReceiver] RANK_SYNC_CHALLENGE 未受信のため DISCOVERY_REQUEST を再送（同じ challengeId が再送されます）。");
        }
        catch (Exception e) { Debug.LogError($"[ChildUdpReceiver] Challenge request error: {e.Message}"); }
    }

    /// <summary>接続元識別つき DISCOVERY_REQUEST（親機が再送と新規接続を区別するために使う）。</summary>
    private string BuildDiscoveryRequest()
        => "DISCOVERY_REQUEST:" + _rankSyncSourceId + ":" + _rankSyncSessionSeq;

    /// <summary>接続確認の応答（親機が今の接続で発行した challengeId をそのまま返す）。</summary>
    private void SendRankSyncHello()
    {
        if (string.IsNullOrEmpty(_rankSyncChallengeId)) return;   // 要求受信前は応答しない
        SendState(RankSyncHello.Format(_rankSyncChallengeId, _rankSyncSourceId, _rankSyncSessionSeq));
    }

    private IEnumerator RankSyncResendRoutine(string reason)
    {
        for (int attempt = 0; attempt < RankSyncMaxAttempts; attempt++)
        {
            yield return new WaitForSecondsRealtime(RankSyncResendInterval);

            if (_rankSyncConfirmed || string.IsNullOrEmpty(_pendingRankSyncMessage))
            {
                _rankSyncResendCoroutine = null;
                yield break;
            }
            RequestChallengeIfNeeded();
            SendRankSyncHello();
            SendState(_pendingRankSyncMessage);
        }

        // 上限到達。毎フレーム送らない。未確認のまま保持し、再接続時に再送する。
        if (showDebugLogs)
            Debug.LogWarning($"[ChildUdpReceiver] RankSync update={_rankSyncUpdateNo} still unconfirmed after {RankSyncMaxAttempts} resends ({reason}) — kept for reconnect resend.");
        _rankSyncResendCoroutine = null;
    }

    /// <summary>親機からの同期確認（RANK_SYNC_ACK）。自分の同期元・自分の更新番号のときだけ確定する。</summary>
    private void HandleRankSyncAck(string sourceId, int updateNo)
    {
        // 自分の同期元・今保持している更新番号・未確認のときだけ解除（古いACKで新しい未確認更新を消さない）
        if (!ChildRankSyncPolicy.ShouldAcceptAck(sourceId, updateNo, _rankSyncSourceId, _rankSyncUpdateNo, _rankSyncConfirmed))
            return;

        _rankSyncConfirmed = true;
        _pendingRankSyncMessage = "";
        PersistRankSyncState();

        if (_rankSyncResendCoroutine != null)
        {
            StopCoroutine(_rankSyncResendCoroutine);
            _rankSyncResendCoroutine = null;
        }

        if (showDebugLogs)
            Debug.Log($"[ChildUdpReceiver] RankSync confirmed: update={updateNo}.");
    }

    private void PersistRankSyncState()
    {
        PlayerPrefs.SetString(PrefRankSyncSourceId, _rankSyncSourceId);
        PlayerPrefs.SetInt(PrefRankSyncUpdateNo, _rankSyncUpdateNo);
        PlayerPrefs.SetInt(PrefRankSyncConfirmed, _rankSyncConfirmed ? 1 : 0);
        PlayerPrefs.SetString(PrefRankSyncPending, _pendingRankSyncMessage ?? "");
        PlayerPrefs.Save();
    }

    /// <summary>
    /// 同期元ID・更新番号・確認状態を復元する。
    /// 未確認のまま終了していた場合は、同じ更新番号で再送対象に戻す（次の接続時に送る）。
    /// </summary>
    /// <summary>
    /// 同期元ID・更新番号・確認状態・未確認全文を復元する。
    /// ・未確認の全文が保存されている場合は、検証したうえで「そのまま（同じ sourceId・updateNo・内容）」再送する
    /// ・全文保存が無い旧状態からの起動時は、現在の子機データから「新しい更新番号」で作り直す
    ///   （古い updateNo を使って別内容を送らない）
    /// </summary>
    private void LoadRankSyncState()
    {
        _rankSyncSourceId = PlayerPrefs.GetString(PrefRankSyncSourceId, "");
        if (string.IsNullOrEmpty(_rankSyncSourceId))
        {
            _rankSyncSourceId = System.Guid.NewGuid().ToString("N");
            Debug.LogWarning($"[ChildUdpReceiver] RankSync sourceId generated: {_rankSyncSourceId}");
        }

        // 接続確認の世代を進める（起動ごとに +1）。古い接続確認が遅延しても親機が巻き戻らないようにする。
        _rankSyncSessionSeq = PlayerPrefs.GetInt(PrefRankSyncSessionSeq, 0) + 1;
        PlayerPrefs.SetInt(PrefRankSyncSessionSeq, _rankSyncSessionSeq);

        _rankSyncUpdateNo = PlayerPrefs.GetInt(PrefRankSyncUpdateNo, 0);
        _rankSyncConfirmed = PlayerPrefs.GetInt(PrefRankSyncConfirmed, 1) != 0;

        string pending = PlayerPrefs.GetString(PrefRankSyncPending, "");
        _pendingRankSyncMessage = "";

        if (!_rankSyncConfirmed)
        {
            if (ChildRankSyncPolicy.CanResendVerbatim(pending, _rankSyncSourceId, _rankSyncUpdateNo))
            {
                // 同じ全文をそのまま再送する（sourceId・updateNo・内容を変えない）
                _pendingRankSyncMessage = pending;
                Debug.LogWarning($"[ChildUdpReceiver] RankSync update={_rankSyncUpdateNo} pending restored verbatim — will resend on connect.");
            }
            else
            {
                // 旧状態（全文保存なし／不一致）からの移行: 現在のデータから新しい更新番号で作り直す
                _rankSyncUpdateNo++;
                _rankSyncConfirmed = false;
                _pendingRankSyncMessage = BuildRankSyncMessage("", RankingSnapshot.TypeNone);
                Debug.LogWarning($"[ChildUdpReceiver] RankSync migrated to a new update={_rankSyncUpdateNo} (no valid persisted full text). 古いupdateNoでは送りません。");
            }
        }
        else
        {
            PlayerPrefs.SetString(PrefRankSyncPending, "");
        }

        PersistRankSyncState();
    }

    // ── Public send API ───────────────────────────────────────────────────────
    public void SendState(string message)
    {
        if (showDebugLogs)
            Debug.Log($"[ChildUdpReceiver] → '{message}' to {targetIP}:{parentReceivePort}");

        // 開始経路の診断: 子機の SendState は接続状態ガードを持たず常に送信する（中断条件は無し）。
        bool isStartTrace = message.StartsWith(CMD_START, StringComparison.Ordinal);
        if (isStartTrace && showDebugLogs)
            Debug.Log($"[StartTrace][子機] ChildUdpReceiver.SendState 送信直前 — '{message}' → {targetIP}:{parentReceivePort}, currentState={currentState}, id={GetInstanceID()}", this);

        try
        {
            byte[] data = Encoding.UTF8.GetBytes(MAGIC_NUMBER + message);
            SendToUdp(data, targetIP, parentReceivePort);

            if (isStartTrace && showDebugLogs)
                Debug.Log($"[StartTrace][子機] ChildUdpReceiver.SendState 送信成功 — '{message}' → {targetIP}:{parentReceivePort} ({data.Length} bytes)", this);

            if (message == "LOADING_COMPLETE")
            {
                // Fallback: broadcast so parent can receive even if targetIP is stale.
                SendToUdp(data, "255.255.255.255", parentReceivePort);
                if (showDebugLogs)
                    Debug.Log($"[ChildUdpReceiver] → '{message}' broadcast to 255.255.255.255:{parentReceivePort}");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[ChildUdpReceiver] SendState error: {e.Message}");
            if (isStartTrace)
                Debug.LogError($"[StartTrace][子機] ChildUdpReceiver.SendState 例外 — '{message}' → {targetIP}:{parentReceivePort}, error='{e.Message}'", this);
        }
    }

    /// <summary>
    /// リザルト通知など、取りこぼすと困る重要メッセージ用。UDP のパケットロス対策として、
    /// 即時に1回送ったあと extraSends 回を interval 秒間隔で再送する。
    /// このオブジェクトは DontDestroyOnLoad なので、シーン遷移後も再送が続く。
    /// 受信側（親機）は重複メッセージを無視する前提。
    /// </summary>
    public void SendStateRepeated(string message, int extraSends = 3, float interval = 0.15f)
    {
        SendState(message);

        if (extraSends > 0)
        {
            StartCoroutine(ResendRoutine(message, extraSends, interval));
        }
    }

    private IEnumerator ResendRoutine(string message, int count, float interval)
    {
        for (int i = 0; i < count; i++)
        {
            yield return new WaitForSecondsRealtime(interval);
            SendState(message);
        }
    }

    /// <summary>
    /// ラウドアイテムを取得したことを親機に送信する
    /// </summary>
    public void SendLoudItem()
    {
        // MAGIC_NUMBER ("TEAM13_") + "LOUD_ITEM" で送信
        SendState("LOUD_ITEM");
        Debug.Log("[ChildUdpReceiver] Sent LOUD_ITEM packet to Parent.");
    }

    /// <summary>
    /// ゲーム進行率（0〜1）を親機に送信する。片付け演出の開始条件に使う。
    /// 送信コストを抑えるため、呼び出し側で間隔を制御する（毎フレームは送らない）。
    /// </summary>
    public void SendGameProgress(float progressRate)
    {
        int progressMilli = Mathf.RoundToInt(Mathf.Clamp01(progressRate) * 1000f);
        SendState($"GAME_PROGRESS:{progressMilli}");
        if (showDebugLogs)
            Debug.Log($"[ChildUdpReceiver] Sent GAME_PROGRESS:{progressMilli} packet to Parent.");
    }

    /// <summary>
    /// 子機側で「タイトルへ戻る」処理を開始したことを親機へ通知する。
    /// 完全なメッセージは "TEAM13_RETURN_TO_TITLE:&lt;プレイ識別子&gt;:&lt;連番&gt;"（SendState が MAGIC_NUMBER を前置する）。
    /// 取りこぼすと親機がリザルト画面に取り残されるため、再送付きで送る。識別子・連番は再送でも同じ値を送る。
    /// </summary>
    public void notifyReturnToTitle(bool fast = false)
    {
        _returnToTitleSendSeq++;

        // ボタン起点は ":FAST" を付けて送る（受信側も短いフェードにする）。
        // 再送は SendStateRepeated が「同じ文字列＝同じ sid・seq・復帰モード」を送る。
        string message = fast
            ? $"RETURN_TO_TITLE:{_playSessionId}:{_returnToTitleSendSeq}:FAST"
            : $"RETURN_TO_TITLE:{_playSessionId}:{_returnToTitleSendSeq}";

        SendStateRepeated(message);
        Debug.Log($"[ChildUdpReceiver] Sent RETURN_TO_TITLE (playSessionId={_playSessionId}, seq={_returnToTitleSendSeq}, fast={fast}) with retries to parent.");
    }

    // ── Unity lifecycle ───────────────────────────────────────────────────────
    void Awake()
    {
        if (instance != null && instance != this)
        {
            Debug.Log("[ChildUdpReceiver] Duplicate detected — destroying self.");
            Destroy(gameObject);
            return;
        }
        instance = this;
        DontDestroyOnLoad(gameObject);
    }

    void Start()
    {
        Debug.Log("[ChildUdpReceiver] Start");

        // 同期元ID・更新番号・確認状態を復元（未確認なら次の接続で再送する）
        LoadRankSyncState();

        SceneManager.sceneLoaded += OnSceneLoaded;
        RefreshSceneReferences();

        if (IsTitleScene(SceneManager.GetActiveScene().name))
        {
            // タイトルUIの初期化は ChildTitleConnectionUI が行う（本クラスはセッション状態のリセットのみ）。
            ResetForNewSession();
        }

        isRunning = true;

        // 通信基盤を開く：受信は normalPort の1経路（従来の new UdpClient(normalPort) と同じbind先）、
        // 送信は broadcast 許可（Discovery が 255.255.255.255 へ送るため。従来の EnableBroadcast=true と同じ）。
        //  ・Discovery用の受信経路は作らない（子機は normalPort の1経路で受信する＝従来と同じ）。
        //  ・途中で失敗した場合、基盤側が生成済みソケットを全て破棄する（部分初期化を残さない）。
        _udpChannel = new UdpChannel();
        if (!_udpChannel.TryOpen(0, normalPort, true, out string openError))
        {
            Debug.LogError($"[ChildUdpReceiver] UDP通信基盤を開けませんでした（port={normalPort}）: {openError}");
            _udpChannel.Dispose();
            _udpChannel = null;
            isRunning = false;
            return;
        }
    }

    void Update()
    {
        // 1) 基盤（UdpChannel）から受信データ・通信エラーを取り込み、既存のキューへ積む。
        //    取り込み→処理を同一フレームで行うため、反映フレームは従来と同じ。
        PumpChannelQueues();

        // 2) 以降は従来どおりの処理順（messageQueue → actionQueue → logQueue → 保留CAUGHT消費）。
        while (messageQueue.TryDequeue(out string msg))
            HandleIncoming(msg);

        while (actionQueue.TryDequeue(out Action action))
            action();

        ProcessLogQueue();

        // ── CAUGHT：購読前受信の保留を、ゲーム側の受信担当が準備できたら1回だけ通知する ────────
        //   消費条件：子機ゲームシーン ／ 保留したプレイIDが現在プレイIDと一致 ／ 購読者が準備済み。
        //   消費の前に保留フラグと保留IDをクリアし、その後に通知する。
        if (_hasPendingCaught)
        {
            string pendingScene = SceneManager.GetActiveScene().name;
            if (IsTitleScene(pendingScene) || pendingScene == "ChildGameOver" || pendingScene == "ChildGameClear")
            {
                // タイトル・結果シーンへ移行したら破棄する（次プレイへ持ち越さない）。
                ClearPendingCaught("非ゲームシーンへ移行");
            }
            else if (IsGameplayScene(pendingScene) &&
                     CaughtReceived != null &&
                     _pendingCaughtPlayId == (_playSessionId ?? ""))
            {
                // クリアしてから通知する（通知側で状態を消す構造にしない）。
                _hasPendingCaught = false;
                _pendingCaughtPlayId = "";
                if (showDebugLogs)
                    Debug.Log("[ChildUdpReceiver] 保留していた CAUGHT を購読者へ通知します（メインスレッド・同一プレイ）。");
                CaughtReceived?.Invoke();
            }
            // ロード中・プレイID不一致・購読者未準備の場合は保留を維持する（無制限リトライではなく1件のみ）。
        }

        // Timeout
        if (currentState == ConnectionState.Connected &&
            Time.time - lastReceiveTime > timeoutLimit)
        {
            SetConnectionState(ConnectionState.Disconnected);
            Debug.LogWarning("[ChildUdpReceiver] Connection timed out — parent heartbeat lost.");
        }

        // Discovery coroutine lifecycle
        if (currentState == ConnectionState.Connecting && discoveryCoroutine == null)
            discoveryCoroutine = StartCoroutine(DiscoveryCoroutine());
        else if (currentState != ConnectionState.Connecting && discoveryCoroutine != null)
        {
            StopCoroutine(discoveryCoroutine);
            discoveryCoroutine = null;
        }

        // Heartbeat coroutine lifecycle
        if (currentState == ConnectionState.Connected && heartbeatCoroutine == null)
            heartbeatCoroutine = StartCoroutine(HeartbeatCoroutine());
        else if (currentState != ConnectionState.Connected && heartbeatCoroutine != null)
        {
            StopCoroutine(heartbeatCoroutine);
            heartbeatCoroutine = null;
        }

        // タイトルUIの更新は ChildTitleConnectionUI が ConnectionStateChanged を受けて行う。
    }

    void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        if (instance == this) instance = null;
        isRunning = false;

        if (discoveryCoroutine != null) { StopCoroutine(discoveryCoroutine); discoveryCoroutine = null; }
        if (heartbeatCoroutine != null) { StopCoroutine(heartbeatCoroutine); heartbeatCoroutine = null; }

        // 通信基盤を停止する：停止要求 → 受信ソケットを閉じてブロッキング受信を解除 → 受信スレッドの終了を有限時間だけ待つ。
        //  ・基盤側が冪等なので複数回呼んでも安全（無期限Joinはしない）。
        //  ・終了した個体の受信結果は基盤側で破棄される（新しい個体へ流用しない）。
        UdpChannel channel = _udpChannel;
        _udpChannel = null;
        channel?.Close();
    }

    // ── Scene reference refresh ────────────────────────────────────────────────
    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        Debug.Log($"[ChildUdpReceiver] Scene loaded: '{scene.name}' — refreshing scene references.");
        if (showDebugLogs)
            Debug.Log($"[StartTrace][子機] ChildUdpReceiver.OnSceneLoaded 到着 — scene='{scene.name}', mode={mode}, currentState={currentState}, gameSceneLoaded={gameSceneLoaded}, isTitle={IsTitleScene(scene.name)}, sessionId={_playSessionId}, id={GetInstanceID()}", this);
        RefreshSceneReferences();

        if (IsTitleScene(scene.name))
        {
            // タイトルUI（再取得・初期化・リスナー再登録）は ChildTitleConnectionUI が担当する。
            ResetForNewSession();
            Debug.Log($"[ChildUdpReceiver] Title scene loaded ('{scene.name}') — reset state for next round.");
        }
        else if (scene.name == "ChildGameOver" || scene.name == "ChildGameClear")
        {
            // 結果シーンでは遅延CAUGHTを二重処理しない。
            // 受理済み状態（caughtHandled）は維持したまま、未消費の保留だけを破棄する。
            ClearPendingCaught("結果シーン読込");
        }
        // ChildLoading→ゲームシーンの読込では保留を維持する（購読成立後に消費する）。
        // ゲームシーンでの睡眠ロックの適用（入力停止・画像表示）は ChildSleepLockApplier（シーン側）が
        // 購読成立時の初期同期（IsSleepInputLocked の取得）で行う。ここではUDP側の状態を勝手に解除しない
        // （親機が寝たままでも維持する）。
    }

    // タイトルUIの再取得・初期化・リスナー再登録は ChildTitleConnectionUI へ移した。
    // 本クラスはタイトルシーン判定だけを通信ロジック用に保持する。

    private bool IsTitleScene(string sceneName)
    {
        return sceneName == titleSceneName ||
               sceneName == "TitleScene" ||
               sceneName == "Title" ||
               sceneName.Contains("Title");
    }

    // タイトルのボタンへのリスナー登録は ChildTitleConnectionUI が担当する（本クラスでは行わない）。

    public void ResetForNewSession()
    {
        SetConnectionState(ConnectionState.Disconnected);
        lastReceiveTime = 0f;
        lastMessage = "";
        targetIP = "127.0.0.1";
        gameSceneLoaded = false;
        caughtHandled = false;
        // 購読前に受理したCAUGHTの保留もリセットする（次プレイへ持ち越さない）。
        ClearPendingCaught("ResetForNewSession（タイトル復帰）");
        // 捕獲演出の待機・完了状態もリセットする（イベントは発火しない＝結果遷移を起こさない）。
        ResetCaptureWaitState("ResetForNewSession（タイトル復帰）");

        // 睡眠ロックを解除し、購読者がいれば変更を通知する（シーン側が解除を適用できるように）。
        // 購読者が居ない場合もUDP側の保持状態は正しく解除される。
        SetSleepInputLocked(false);

        // プレイ識別子をクリアする。タイトルに戻った時点で前のプレイは終了しているため、
        // 以降に届く前回プレイの RETURN_TO_TITLE は識別子不一致で無視される
        // （次プレイ開始時に OnStartButtonClicked で新しい識別子を必ず発行する）。
        _playSessionId = "";
        _lastPeerReturnToTitleSeq = 0;

        if (discoveryCoroutine != null)
        {
            StopCoroutine(discoveryCoroutine);
            discoveryCoroutine = null;
        }

        if (heartbeatCoroutine != null)
        {
            StopCoroutine(heartbeatCoroutine);
            heartbeatCoroutine = null;
        }

        if (sleepingManager != null)
            Debug.Log("[ChildUdpReceiver] ResetForNewSession: sleepingManager remains as reference for next round.");
    }

    private void RefreshSceneReferences()
    {
        // 睡眠ロックの適用対象（PlayerMove・睡眠中画像）の取得は ChildSleepLockApplier（シーン側）が行う。
        // ここではUDP側が別用途で参照するものだけを取得する（playerMove は診断ログ用のInspector割当のみ）。

        sleepingManager = UnityEngine.Object.FindFirstObjectByType<SleepingManager>();
        if (sleepingManager != null)
            Debug.Log($"[ChildUdpReceiver] sleepingManager found: '{sleepingManager.gameObject.name}'.");
        else
            Debug.Log("[ChildUdpReceiver] sleepingManager not found in current scene (OK on game/loading scenes).");
    }

    // タイトルUIの参照取得は ChildTitleConnectionUI が担当する（Inspector参照のみ・名前検索なし）。

    // ── Incoming message dispatch (main thread) ───────────────────────────────
    private void HandleIncoming(string raw)
    {
        if (!raw.StartsWith(MAGIC_NUMBER)) return;
        string msg = raw.Substring(MAGIC_NUMBER.Length);
        lastMessage = msg;
        if (msg != "PING" && showDebugLogs)
            Debug.Log($"[ChildUdpReceiver] HandleIncoming: '{msg}' | scene='{SceneManager.GetActiveScene().name}' | playerMove={(playerMove != null ? playerMove.gameObject.name : "NULL")} | GameManager={(GameManager.instance != null ? "present" : "NULL")}");

        if (msg == "PING")
        {
            lastReceiveTime = Time.time;
            return;
        }

        if (msg == CMD_START || msg.StartsWith(CMD_START + ":", StringComparison.Ordinal))
        {
            // 親機が起点で開始した場合、親機が発行したプレイ識別子を採用する
            // （子機起点の場合は OnStartButtonClicked で既に発行済み）。
            // 「新しいプレイの開始」が確定した場合だけ、前プレイの受理済み／保留をリセットする。
            // 同じプレイIDのSTART_GAME再受信ではリセットしない（再送で受理済み状態を消さない）。
            bool isNewPlay = false;

            if (msg.StartsWith(CMD_START + ":", StringComparison.Ordinal))
            {
                string sid = msg.Substring(CMD_START.Length + 1);
                if (!string.IsNullOrEmpty(sid))
                {
                    isNewPlay = (sid != _playSessionId);
                    _playSessionId = sid;
                    _lastPeerReturnToTitleSeq = 0;
                }
                else
                {
                    // IDが空の付き形式はレガシー扱い（ゲーム未開始なら新プレイ）。
                    isNewPlay = !gameSceneLoaded;
                }
            }
            else
            {
                // 旧形式（IDなし）はゲーム未開始なら新プレイとして扱う。
                isNewPlay = !gameSceneLoaded;
            }

            if (isNewPlay)
            {
                caughtHandled = false;
                ClearPendingCaught("新プレイ開始（START_GAME）");
                ResetCaptureWaitState("新プレイ開始（START_GAME）");
            }

            if (showDebugLogs)
                Debug.Log($"[ChildUdpReceiver] Received START_GAME from parent — loading game scene. playSessionId={_playSessionId}, isNewPlay={isNewPlay}");
            LoadGameScene();
            return;
        }

        if (msg == "CAUGHT")
        {
            string activeScene = SceneManager.GetActiveScene().name;
            if (showDebugLogs)
                Debug.Log($"[ChildUdpReceiver] Received TEAM13_CAUGHT — scene='{activeScene}', GameManager.instance={(GameManager.instance != null ? "present" : "NULL")}, playerMove={(playerMove != null ? playerMove.gameObject.name : "NULL")}, sleepingManager={(sleepingManager != null ? sleepingManager.gameObject.name : "NULL")}.");

            // タイトル・結果シーンではCAUGHTを破棄する（受理済みにしない＝次プレイの捕獲を無効化しない）。
            if (IsTitleScene(activeScene) || activeScene == "ChildGameOver" || activeScene == "ChildGameClear")
            {
                if (showDebugLogs)
                    Debug.Log($"[ChildUdpReceiver] TEAM13_CAUGHT discarded — scene='{activeScene}'（タイトル／結果のため破棄）");
                return;
            }

            // ロード中は「現在プレイが開始済みで有効なプレイIDを持つ場合」だけ受理する。
            if (activeScene == gameSceneName && string.IsNullOrEmpty(_playSessionId))
            {
                if (showDebugLogs)
                    Debug.Log($"[ChildUdpReceiver] TEAM13_CAUGHT discarded — scene='{activeScene}'（ロード中かつ有効なプレイIDが無い）");
                return;
            }

            if (caughtHandled)
            {
                if (showDebugLogs)
                    Debug.Log($"[ChildUdpReceiver] TEAM13_CAUGHT ignored — scene='{activeScene}', 既に受理済み（再送を無視）");
                return;
            }

            // 受理済みにしてから通知／フォールバックへ進む（再送はここで弾かれる）。
            HandleCaughtAccepted(activeScene);

            return;
        }

        if (msg.StartsWith("SEQUENCE_DONE:", StringComparison.Ordinal))
        {
            // SEQUENCE_DONE:<playSessionId>:<reason>
            string rest = msg.Substring("SEQUENCE_DONE:".Length);
            string[] parts = rest.Split(':');
            string playId = parts.Length > 0 ? parts[0].Trim() : "";
            string reason = parts.Length > 1 ? parts[1].Trim() : "unspecified";

            HandleSequenceDoneMessage(playId, reason);
            return;
        }

        if (msg.StartsWith("RETURN_TO_TITLE:", StringComparison.Ordinal))
        {
            // RETURN_TO_TITLE:<playSessionId>:<seq>[:FAST]（旧: RETURN_TO_TITLE:<seq>）
            // 3つ目の値は "FAST" のみ許可し、それ以外の追加値は推測で処理せず不正として拒否する。
            string rest = msg.Substring("RETURN_TO_TITLE:".Length);
            string[] parts = rest.Split(':');

            string sid = "";
            string seqText;
            bool fast = false;

            if (parts.Length == 1)
            {
                // 旧形式: <seq>（識別子なし）
                seqText = parts[0];
            }
            else if (parts.Length == 2)
            {
                // <playSessionId>:<seq>
                sid = parts[0];
                seqText = parts[1];
            }
            else if (parts.Length == 3 && parts[2] == "FAST")
            {
                // <playSessionId>:<seq>:FAST（ボタン起点）
                sid = parts[0];
                seqText = parts[1];
                fast = true;
            }
            else
            {
                if (showDebugLogs)
                    Debug.LogWarning($"[ChildUdpReceiver] RETURN_TO_TITLE ignored — invalid format (only 'FAST' is allowed as 3rd field): '{msg}'");
                return;
            }

            if (!int.TryParse(seqText, out int seq))
            {
                if (showDebugLogs)
                    Debug.LogWarning($"[ChildUdpReceiver] RETURN_TO_TITLE ignored — could not parse seq: '{seqText}'");
                return;
            }

            HandleTeamReturnToTitle(sid, seq, fast);
            return;
        }

        if (msg.StartsWith("RANK_SYNC_CHALLENGE:", StringComparison.Ordinal))
        {
            // RANK_SYNC_CHALLENGE:<version>:<challengeId>
            if (RankSyncChallenge.TryParse(msg, out RankSyncChallenge challenge, out string challengeError))
            {
                _rankSyncChallengeId = challenge.ChallengeId;
                if (showDebugLogs)
                    Debug.Log($"[ChildUdpReceiver] RANK_SYNC_CHALLENGE received — challenge='{challenge.ChallengeId}'. 接続確認の応答を返します。");

                // 応答（HELLO）を返す。未確認スナップショットがあれば同じ更新番号・同じ内容で再送も行う。
                // 本文・更新番号は接続確認では書き換えない（接続情報は HELLO 側で運ぶ）。
                if (!string.IsNullOrEmpty(_pendingRankSyncMessage)) StartRankSyncResend("challenge");
                else SendRankSyncHello();
            }
            else if (showDebugLogs)
            {
                Debug.LogWarning($"[ChildUdpReceiver] RANK_SYNC_CHALLENGE ignored — {challengeError}: '{msg}'");
            }
            return;
        }

        if (msg.StartsWith("RANK_SYNC_ACK:", StringComparison.Ordinal))
        {
            // RANK_SYNC_ACK:<version>:<sourceId>:<updateNo>
            string[] ackParts = msg.Substring("RANK_SYNC_ACK:".Length).Split(':');

            if (ackParts.Length != 3)
            {
                if (showDebugLogs)
                    Debug.LogWarning($"[ChildUdpReceiver] RANK_SYNC_ACK ignored — invalid format: '{msg}'");
                return;
            }

            if (ackParts[0] != RankingSnapshot.VersionName)
            {
                if (showDebugLogs)
                    Debug.LogWarning($"[ChildUdpReceiver] RANK_SYNC_ACK ignored — unsupported version: '{ackParts[0]}'");
                return;
            }

            if (!int.TryParse(ackParts[2], out int ackUpdate))
            {
                if (showDebugLogs)
                    Debug.LogWarning($"[ChildUdpReceiver] RANK_SYNC_ACK ignored — invalid updateNo: '{ackParts[2]}'");
                return;
            }

            HandleRankSyncAck(ackParts[1], ackUpdate);
            return;
        }

        if (msg == "SLEEP_LOCK")
        {
            if (isSleepInputLocked)
            {
                if (showDebugLogs)
                    Debug.Log("[ChildUdpReceiver] Received redundant SLEEP_LOCK (already locked) — skipped.");
                return;
            }

            // 状態を更新し、購読者（シーン側の適用担当）へメインスレッドで通知する。
            // 入力停止・PlayerMove・睡眠中画像の適用は購読者が行う（UDP側では行わない）。
            SetSleepInputLocked(true);

            if (showDebugLogs)
                Debug.Log("[ChildUdpReceiver] Received SLEEP_LOCK — locking input.");

            return;
        }

        if (msg == "SLEEP_UNLOCK")
        {
            if (!isSleepInputLocked)
            {
                if (showDebugLogs)
                    Debug.Log("[ChildUdpReceiver] Received redundant SLEEP_UNLOCK (already unlocked) — skipped.");
                return;
            }

            // 状態を更新し、購読者（シーン側の適用担当）へメインスレッドで通知する。
            SetSleepInputLocked(false);

            if (showDebugLogs)
                Debug.Log("[ChildUdpReceiver] Received SLEEP_UNLOCK — unlocking input.");

            return;
        }

        if (showDebugLogs)
            Debug.Log($"[ChildUdpReceiver] Unhandled message: '{msg}'");
    }

    // ── CAUGHT（親機の捕獲）の受理・保留 ─────────────────────────────────────

    /// <summary>
    /// 子機のゲームシーン（ロード・タイトル・結果以外）かどうか。
    /// 従来フォールバックが使っていた判定と同じ（特定のシーン名に依存しない）。
    /// </summary>
    private bool IsGameplayScene(string sceneName)
    {
        return sceneName != "ChildGameOver" &&
               sceneName != "ChildGameClear" &&
               sceneName != gameSceneName &&
               !IsTitleScene(sceneName);
    }

    /// <summary>
    /// CAUGHTを受理した（caughtHandled=true）うえで、
    /// 「購読者へ通知」「GameManager不在時のフォールバック」「1件保留」のいずれか1つだけを行う。
    /// 同じCAUGHTを通知とフォールバックの両方で処理しない。
    /// </summary>
    private void HandleCaughtAccepted(string activeScene)
    {
        // 受理済みは「通知やフォールバックの前」に立てる（再送を弾く。保留の消費は妨げない）。
        caughtHandled = true;

        // 捕獲を受理したので、親機の演出完了まで「演出待機」に入る（通常の寝たふりとは別状態）。
        EnterCaptureWait("CAUGHT受理");

        // 1) ゲーム側の受信担当（GameManager）が準備済みなら通知する。
        if (CaughtReceived != null)
        {
            if (showDebugLogs)
                Debug.Log($"[ChildUdpReceiver] CAUGHTを購読者へ通知します（メインスレッド, scene='{activeScene}'）。");
            CaughtReceived?.Invoke();
            return;
        }

        // 2) GameManagerが存在しないゲームシーンでは、従来のフォールバックを実行する。
        //    （ロード・タイトル・結果では実行しない＝従来どおり）
        if (IsGameplayScene(activeScene) && GameManager.instance == null)
        {
            RunCaughtFallback(activeScene);
            return;
        }

        // 3) それ以外（ロード中／GameManagerが存在するが購読準備中）は1件保留する。
        EnqueuePendingCaught();
    }

    /// <summary>
    /// 【従来フォールバック】GameManager不在のゲームシーンでの保存・ランキング通知・結果シーン遷移。
    /// 対象シーンと内容は従来どおり維持する（ロード・タイトル・結果では実行しない）。
    /// </summary>
    private void RunCaughtFallback(string activeScene)
    {
        // フォールバック：既に結果画面やタイトル画面にいる場合の二重ロードを防止
        bool shouldRunFallback = activeScene != "ChildGameOver" &&
                                 activeScene != "ChildGameClear" &&
                                 activeScene != "ChildLoading" &&
                                 !IsTitleScene(activeScene);
        if (shouldRunFallback)
        {
            if (showDebugLogs)
                Debug.Log($"[ChildUdpReceiver] CAUGHT fallback executed — scene='{activeScene}', loading 'ChildGameOver'.");
            // GameManager.instance が未設定でも、シーン内に実体があればそこから実スコアを取得する。
            // それも見つからない場合のみ 0 点として扱う（最終手段）。
            int finalScore = 0;
            GameManager gm = GameManager.instance != null ? GameManager.instance : FindFirstObjectByType<GameManager>();
            if (gm != null)
            {
                finalScore = gm.score;
            }
            PlayerPrefs.SetInt("LastGameOverScore", finalScore);
            PlayerPrefs.Save();
            NotifyRankingChanged(RankingSnapshot.TypeGameOver);
            SceneManager.LoadScene("ChildGameOver");
        }
        else if (showDebugLogs)
        {
            Debug.Log($"[ChildUdpReceiver] CAUGHT fallback skipped — scene='{activeScene}'.");
        }
    }

    /// <summary>
    /// 購読者が居ない間に受理したCAUGHTを1件だけ保留する（履歴は持たない・再送で増やさない）。
    /// 保留には受理時のプレイ識別子を記録し、同一プレイでのみ消費する。
    /// </summary>
    private void EnqueuePendingCaught()
    {
        if (_hasPendingCaught)
        {
            if (showDebugLogs)
                Debug.Log("[ChildUdpReceiver] CAUGHT は既に1件保留済みのため追加しません（再送は無視）。");
            return;
        }

        _hasPendingCaught = true;
        _pendingCaughtPlayId = _playSessionId ?? "";
        if (showDebugLogs)
            Debug.Log($"[ChildUdpReceiver] CAUGHT を1件保留します（playId='{_pendingCaughtPlayId}'、購読成立後に通知）。");
    }

    /// <summary>CAUGHTの保留を破棄する（次プレイ・非ゲームシーンへ持ち越さない）。</summary>
    private void ClearPendingCaught(string reason)
    {
        if (!_hasPendingCaught) return;

        _hasPendingCaught = false;
        _pendingCaughtPlayId = "";
        if (showDebugLogs)
            Debug.Log($"[ChildUdpReceiver] CAUGHT の保留を破棄しました（{reason}）。");
    }

    /// <summary>
    /// 親機からの「タイトルへ戻る」通知（TEAM13_RETURN_TO_TITLE:&lt;識別子&gt;:&lt;連番&gt;）を受信したときの処理。
    /// 親機に合わせて子機もタイトル画面へ戻る。
    /// ・識別子が現在のプレイと異なる → 別プレイ（前回の再送・遅延、再起動前の通知）として無視する。
    /// ・識別子が一致し、連番が「最後に処理した連番」以下 → 同プレイ内の重複として無視する。
    /// リザルト画面にいる場合は ResultSceneChamger のフェード演出を流用し、
    /// ゲームプレイ中などの演出がない場合は即座に遷移する。
    /// </summary>
    private void HandleTeamReturnToTitle(string playSessionId, int seq, bool fast)
    {
        // 旧フォーマット（識別子なし）は、こちらに識別子が無い場合のみ受け付ける（後方互換）。
        bool sessionMatches = !string.IsNullOrEmpty(playSessionId)
            ? playSessionId == _playSessionId
            : string.IsNullOrEmpty(_playSessionId);

        if (!sessionMatches)
        {
            if (showDebugLogs)
                Debug.Log($"[ChildUdpReceiver] RETURN_TO_TITLE ignored — different play session (recv='{playSessionId}', current='{_playSessionId}').");
            return;
        }

        if (seq <= _lastPeerReturnToTitleSeq)
        {
            if (showDebugLogs)
                Debug.Log($"[ChildUdpReceiver] RETURN_TO_TITLE ignored — stale/duplicate seq={seq} (last processed={_lastPeerReturnToTitleSeq}).");
            return;
        }
        _lastPeerReturnToTitleSeq = seq;

        string activeScene = SceneManager.GetActiveScene().name;
        if (IsTitleScene(activeScene))
        {
            if (showDebugLogs)
                Debug.Log("[ChildUdpReceiver] RETURN_TO_TITLE ignored — already on title scene.");
            return;
        }

        ResultSceneChamger changer = FindFirstObjectByType<ResultSceneChamger>();
        if (changer != null)
        {
            if (changer.IsReturningToTitle)
            {
                if (showDebugLogs)
                    Debug.Log("[ChildUdpReceiver] RETURN_TO_TITLE ignored — already returning to title (fade in progress).");
                return;
            }

            if (showDebugLogs)
                Debug.Log($"[ChildUdpReceiver] RETURN_TO_TITLE received — starting result fade to title (fast={fast}).");
            changer.StartFadeToTitle(notifyPeer: false, skipWaits: fast);
            return;
        }

        Debug.Log($"[ChildUdpReceiver] RETURN_TO_TITLE received — returning to title from scene={activeScene}.");
        SceneManager.LoadScene(SceneNameResolver.Resolve(titleSceneName));
    }

    // ── Coroutines ────────────────────────────────────────────────────────────
    private IEnumerator DiscoveryCoroutine()
    {
        while (currentState == ConnectionState.Connecting)
        {
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(MAGIC_NUMBER + BuildDiscoveryRequest());
                SendToUdp(data, "255.255.255.255", broadcastPort);
                Debug.Log($"[ChildUdpReceiver] Sent DISCOVERY_REQUEST (broadcast) source='{_rankSyncSourceId}' sessionSeq={_rankSyncSessionSeq}");
            }
            catch (Exception e) { Debug.LogError($"[ChildUdpReceiver] Discovery send error: {e.Message}"); }
            yield return new WaitForSeconds(0.5f);
        }
    }

    private IEnumerator HeartbeatCoroutine()
    {
        while (currentState == ConnectionState.Connected)
        {
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(MAGIC_NUMBER + "PING");
                SendToUdp(data, targetIP, parentReceivePort);
            }
            catch (Exception e) { Debug.LogError($"[ChildUdpReceiver] Heartbeat error: {e.Message}"); }

            // QTE中などで Time.timeScale が 0 になっても PING を止めないよう、実時間で待つ。
            // （scaled time だと PING が途絶え、親機が接続切れと誤判定して
            //   SLEEP_LOCK / SLEEP_UNLOCK などを以後すべて送らなくなる）
            yield return new WaitForSecondsRealtime(pingInterval);
        }
    }

    // ── Log queue processing ──────────────────────────────────────────────────
    /// <summary>
    /// 受信スレッドなどの別スレッドからログをキューに追加する
    /// </summary>
    private void EnqueueLog(LogType type, string message)
    {
        logQueue.Enqueue(new LogEntry(type, message));
    }

    /// <summary>
    /// 受信スレッドからキューイングされたログをUnityメインスレッドで出力する
    /// </summary>
    private void ProcessLogQueue()
    {
        while (logQueue.TryDequeue(out LogEntry log))
        {
            switch (log.type)
            {
                case LogType.Log:
                    if (showDebugLogs)
                        Debug.Log(log.message);
                    break;
                case LogType.Warning:
                    if (showDebugLogs)
                        Debug.LogWarning(log.message);
                    break;
                case LogType.Error:
                    Debug.LogError(log.message);
                    break;
            }
        }
    }

    /// <summary>
    /// 通信基盤（UdpChannel）から、受信データと通信エラーをメインスレッドで取り込み、
    /// 既存のキュー（messageQueue / actionQueue / logQueue）へ積む。
    ///  ・従来は受信スレッドが直接このキューへ積んでいた処理を、そのままメインスレッドへ移した
    ///    （受信スレッドでは Unity 処理を呼ばない）。処理順・反映フレームは従来と同じ。
    ///  ・受信元エンドポイントは失わずログに残す（本文の解釈は従来どおり）。
    /// </summary>
    private void PumpChannelQueues()
    {
        if (!isRunning || _udpChannel == null) return;

        // 通信エラー（従来: 受信スレッドから logQueue 経由で Error として出していたもの）
        while (_udpChannel.TryDequeueIssue(out UdpChannel.Issue issue))
        {
            EnqueueLog(LogType.Error, $"[ChildUdpReceiver] ReceiveData error: {issue.Message}");
        }

        // 受信データ（従来の ReceiveData の順序をそのまま再現する）
        while (_udpChannel.TryDequeueReceived(out UdpChannel.Datagram datagram))
        {
            string msg = datagram.Text;
            IPEndPoint ep = datagram.RemoteEndPoint;

            // PING 以外のメッセージのみログキューへ積む（毎秒のPINGによる文字列生成・ログ出力を抑制）
            if (msg != MAGIC_NUMBER + "PING")
            {
                EnqueueLog(LogType.Log, $"[ChildUdpReceiver] Received: '{msg}' from {(ep != null ? ep.Address.ToString() : "unknown")}");
            }

            if (msg == MAGIC_NUMBER + "DISCOVERY_ACCEPT")
            {
                string parentIP = ep != null ? ep.Address.ToString() : "";
                EnqueueLog(LogType.Log, $"[ChildUdpReceiver] DISCOVERY_ACCEPT from {parentIP} — now Connected.");
                actionQueue.Enqueue(() =>
                {
                    targetIP = parentIP;
                    SetConnectionState(ConnectionState.Connected);
                    lastReceiveTime = Time.time;
                    gameSceneLoaded = false;

                    // 再接続時: 最新スナップショットを送る（未確認があれば同じ更新番号・同じ内容を再送）
                    SendRankSyncOnConnect();
                });
            }

            messageQueue.Enqueue(msg);
        }
    }

    /// <summary>
    /// 通信基盤（UdpChannel）で1件送信する（メインスレッドからのみ呼ぶ）。
    ///  ・基盤が未オープン（初期化失敗・停止後）の場合は例外を投げる（呼び出し側の既存 try/catch が受け止める）。
    /// </summary>
    private void SendToUdp(byte[] data, string ip, int port)
    {
        UdpChannel channel = _udpChannel;
        if (channel == null)
            throw new InvalidOperationException("UDP channel is not open");

        channel.Send(data, data.Length, ip, port);
    }

    // ── 受信スレッドは通信基盤（UdpChannel）が持つ ─────────────────────────────
    //  従来の受信スレッド（ReceiveData）は撤去した。ブロッキング受信・受信スレッドの生成と終了・
    //  受信データ／通信エラーの受け渡しは UdpChannel が担当し、ChildUdpReceiver は
    //  Update の PumpChannelQueues() でメインスレッドとして取り込むだけにした。
    //  （受信内容の解釈は従来どおり HandleIncoming 以降で行う）

    // タイトルUIの表示更新（ラベル・位置・Cancel表示・クレジット/設定ボタン）は
    // ChildTitleConnectionUI が ChildUdpReceiver.ConnectionStateChanged を受けて行う。

    private void LoadGameScene()
    {
        if (showDebugLogs)
            Debug.Log($"[StartTrace][子機] ChildUdpReceiver.LoadGameScene ENTER — gameSceneLoaded={gameSceneLoaded}, 遷移先='{gameSceneName}', 現在scene='{SceneManager.GetActiveScene().name}', id={GetInstanceID()}", this);

        // 二重ロード防止。ここで return する場合は遷移しない（判定値も記録する）。
        if (gameSceneLoaded)
        {
            if (showDebugLogs)
                Debug.Log($"[StartTrace][子機] LoadGameScene 中断 — gameSceneLoaded が既に true のため LoadScene しません。", this);
            return;
        }
        gameSceneLoaded = true;

        if (showDebugLogs)
            Debug.Log($"[StartTrace][子機] LoadGameScene → SceneManager.LoadScene('{gameSceneName}') 直前", this);
        SceneManager.LoadScene(gameSceneName);
    }

    private static void CloseClient(ref UdpClient client, string label)
    {
        if (client == null) return;
        try { client.Close(); client.Dispose(); }
        catch (Exception e) { Debug.LogWarning($"[ChildUdpReceiver] Error closing {label}: {e.Message}"); }
        client = null;
    }
}