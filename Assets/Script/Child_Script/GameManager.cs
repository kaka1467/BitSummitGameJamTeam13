using System.Collections;
using UnityEngine;
using TMPro;
using UnityEngine.SceneManagement;

public class GameManager : MonoBehaviour
{
    [Header("Game Over")]
    public string gameOverSceneName = "ChildGameOver"; // GAME_OVER (caught) result scene
    public string timeUpSceneName = "ChildGameClear";   // TIME_UP result scene
    public float gameOverDelay = 0f; // 遷移までの待機（実時間）

    // ── PlayerPrefs Keys（結果・ランキング保存用） ─────────────────────────────
    // 親機・子機で同じPlayerPrefs保存形式（キー文字列・降順TOP5）を使用します。
    // 既存セーブデータおよびランキングとの互換性を保つため、キー文字列の値は絶対に変更しないでください。
    private const string KeyGameOverScore  = "LastGameOverScore";
    private const string KeyTimeUpScore    = "LastTimeUpScore";
    private const string KeyGameOverRank   = "GameOverRank_";
    private const string KeyTimeUpRank     = "TimeUpRank_";
    private const int    RankingSize       = 5;

    // 子機側専用: シーン遷移中に確定した結果種別（"GAME_OVER" または "TIME_UP"）を保持するキー
    private const string KeyResultTypePending = "ResultTypePending";

    // 互換性保持用の公開定数
    public const string PlayerPrefsGameOverScore = KeyGameOverScore;
    public const string PlayerPrefsTimeUpScore = KeyTimeUpScore;
    public const string PlayerPrefsResultTypePending = KeyResultTypePending;
    [SerializeField] private CanvasGroup fadeCanvasGroup;
    [SerializeField, Min(0f)] private float fadeSeconds = 0.5f;

    public static GameManager instance;

    /// <summary>
    /// 結果データ（LastGameOverScore / LastTimeUpScore / ResultTypePending / ランキング）への書き込みが完了したときに発火する静的イベント。
    /// 結果シーンの表示（ResultScoreUI など）はシーン読み込み時の表示リフレッシュに加え、このイベントでも表示を再読み込みして同期する。
    /// （データ書き込みがシーン読み込み後の表示リフレッシュより後に行われるケースに対応するため）
    /// </summary>
    public static event System.Action ResultDataCommitted;

    public int score = 0;
    public float time = 240f;
    public float maxTime = 240f;

    // ── 得点段階による速度倍率 ─────────────────────────────────────────────────
    //  ・段階は「そのプレイの最高到達スコア」で決める（現在スコアではない。減点で段階は下がらない）。
    //  ・倍率はこの表から絶対値を選ぶ（毎回掛け重ねない）。
    //  ・表はここ1か所だけに持ち、ItemMove / BackgroundScroll / ItemSpawner は
    //    読み取り専用プロパティ（ScoreSpeedMultiplier / CurrentScoreSpeedMultiplier）経由で参照する。
    [System.Serializable]
    public struct ScoreSpeedStep
    {
        [Tooltip("この倍率になる最低スコア（最高到達スコアで判定）。")]
        [Min(0)] public int score;
        [Tooltip("速度倍率（アイテム・背景の元の速度に掛ける絶対値）。")]
        [Min(0f)] public float multiplier;
    }

    [Header("得点段階による速度倍率（最高到達スコア → 倍率）")]
    [Tooltip("段数・しきい値・倍率をここで変更する。並び順は問わない（実行時にソートしない）。" +
             "負のしきい値・0以下の倍率・NaN/Infinity の倍率は無効として無視し、" +
             "該当が無ければ1.0倍にフォールバックする。")]
    [SerializeField] private ScoreSpeedStep[] scoreSpeedSteps = new ScoreSpeedStep[]
    {
        new ScoreSpeedStep { score = 0,      multiplier = 1.00f },
        new ScoreSpeedStep { score = 500,    multiplier = 1.10f },
        new ScoreSpeedStep { score = 1000,   multiplier = 1.20f },
        new ScoreSpeedStep { score = 1500,   multiplier = 1.30f },
        new ScoreSpeedStep { score = 2000,   multiplier = 1.40f },
        new ScoreSpeedStep { score = 3000,   multiplier = 1.50f },
        new ScoreSpeedStep { score = 4000,   multiplier = 1.60f },
        new ScoreSpeedStep { score = 5000,   multiplier = 1.70f },
        new ScoreSpeedStep { score = 6000,   multiplier = 1.80f },
        new ScoreSpeedStep { score = 7000,   multiplier = 1.90f },
        new ScoreSpeedStep { score = 8000,   multiplier = 2.00f },
        new ScoreSpeedStep { score = 9000,   multiplier = 2.10f },
        new ScoreSpeedStep { score = 10000,  multiplier = 2.20f },
        new ScoreSpeedStep { score = 11000,  multiplier = 2.30f },
        new ScoreSpeedStep { score = 12000,  multiplier = 2.40f },
        new ScoreSpeedStep { score = 13000,  multiplier = 2.50f },
        new ScoreSpeedStep { score = 14000,  multiplier = 2.60f },
        new ScoreSpeedStep { score = 15000,  multiplier = 2.70f },
        new ScoreSpeedStep { score = 16000,  multiplier = 2.80f },
        new ScoreSpeedStep { score = 18000,  multiplier = 2.90f },
        new ScoreSpeedStep { score = 20000,  multiplier = 3.00f },
        new ScoreSpeedStep { score = 25000,  multiplier = 3.50f },
        new ScoreSpeedStep { score = 30000,  multiplier = 4.00f },
        new ScoreSpeedStep { score = 35000,  multiplier = 4.50f },
        new ScoreSpeedStep { score = 40000,  multiplier = 5.00f },
        new ScoreSpeedStep { score = 45000,  multiplier = 5.50f },
        new ScoreSpeedStep { score = 50000,  multiplier = 6.00f },
        new ScoreSpeedStep { score = 55000,  multiplier = 6.50f },
        new ScoreSpeedStep { score = 60000,  multiplier = 7.00f },
        new ScoreSpeedStep { score = 65000,  multiplier = 7.50f },
        new ScoreSpeedStep { score = 70000,  multiplier = 8.00f },
        new ScoreSpeedStep { score = 75000,  multiplier = 8.50f },
        new ScoreSpeedStep { score = 80000,  multiplier = 9.00f },
        new ScoreSpeedStep { score = 85000,  multiplier = 9.50f },
        new ScoreSpeedStep { score = 90000,  multiplier = 10.00f },
    };

    private int maxScore;                        // そのプレイの最高到達スコア
    private float scoreSpeedMultiplier = 1f;     // 現在の段階倍率（表から選んだ絶対値）

    /// <summary>このプレイの最高到達スコア（読み取り専用）。</summary>
    public int MaxScore => maxScore;

    /// <summary>
    /// 現在の得点段階倍率（読み取り専用）。チュートリアル中は 1.0。
    /// </summary>
    public float ScoreSpeedMultiplier => isTutorialMode ? 1f : scoreSpeedMultiplier;

    /// <summary>
    /// 速度倍率の参照用アクセサ（GameManager 不在時は 1.0）。
    /// ItemMove / BackgroundScroll / ItemSpawner が毎フレーム参照するため、
    /// オブジェクト検索・ソート・配列の割り当ては行わない。
    /// </summary>
    public static float CurrentScoreSpeedMultiplier
    {
        get
        {
            GameManager gm = instance;
            return gm != null ? gm.ScoreSpeedMultiplier : 1f;
        }
    }

    [Header("Fever")]
    public int feverCount = 0;
    public int feverNeeded = 5;
    [Tooltip("フィーバーの継続時間（秒）。名前は旧仕様の名残（現在は速度上昇には使わない）。")]
    public float feverBoostDuration = 8f;
    public FeverLoopEffect feverLoopEffect;
    public FeverLoopEffect feverLoopEffect2;
    // フィーバー実行中（得点2倍・継続時間・キラキラ・QTE新規出現禁止が参照する唯一の状態）
    bool isFeverActive = false;
    public bool IsFeverActive => isFeverActive;

    private Coroutine feverRoutine;
    private float feverEndTime = -1f;

    // --- Score: 1桁ずつ別のTextに表示 ---
    // scoreText は削除し、6桁分の配列に置き換え。
    // インスペクタで scoreDigitTexts[0] = 最上位桁、scoreDigitTexts[5] = 最下位桁 の順に設定してください。
    [Header("Score Digits (最上位桁[0] → 最下位桁[5])")]
    public TextMeshProUGUI[] scoreDigitTexts = new TextMeshProUGUI[6];

    private const int SCORE_DIGITS = 6;

    public TextMeshProUGUI timeText;
    public TextMeshProUGUI feverText;
    // 既存UDP GameObject（ルート）への参照。実行時にそのルート上のHostを解決する。
    public GameObject udpReceiver;

    [Header("Time Change Display")]
    [SerializeField] private TextMeshProUGUI timeChangeText;
    [SerializeField] private float timeChangeDisplaySeconds = 1.5f;

    private Coroutine timeChangeRoutine;

    // ── 片付け演出用：ゲーム進行率の通知 ─────────────────────────────────────
    //   親機の片付け開始条件（後半に入室するか）に使う。
    //   送信頻度を抑えるため、更新は間隔制御して行う（毎フレームは送らない）。
    [Header("Game Progress Notification")]
    [Tooltip("親機へ進行率（0〜1）を送信する間隔（秒）。片付け演出の開始判定に使用。")]
    [SerializeField, Min(0.1f)] private float gameProgressSendInterval = 1f;
    [Tooltip("進行率通知を有効にする。OFFにすると片付け演出の開始は親機側の経過時間で判定される。")]
    [SerializeField] private bool sendGameProgress = true;

    private float _nextGameProgressSendTime;

    bool isGameOver = false;
    public bool IsGameOver => isGameOver;
    private bool isTutorialMode = false;

    /// <summary>
    /// チュートリアル中かどうか（読み取り専用）。
    /// QTE成功報酬の「本プレイ中か」の判定に使う。GameManager が存在するだけでは本プレイと判断しない
    /// （GameManager が無い場合・チュートリアル中は報酬を出さない）。
    /// </summary>
    public bool IsTutorialMode => isTutorialMode;

    // ── 本編の開始（チュートリアル終了後）─────────────────────────────────────
    //   音の親機通知など「本編中か」の判定に使う。
    //   ChildGameScene にいるだけでは本編と見なさない（開始前カウントダウンも本編ではない）。
    private bool isMainPlayActive;

    /// <summary>本編が実際に始まっているか（読み取り専用）。</summary>
    public bool IsMainPlayActive => isMainPlayActive;

    /// <summary>
    /// 本編が始まったことを記録する（TutorialFlow の本編開始処理から1回だけ呼ぶ）。
    /// 二重呼び出しは無視する。
    /// </summary>
    public void MarkMainPlayStarted()
    {
        if (isMainPlayActive) return;

        isMainPlayActive = true;
        Debug.Log("[GameManager] 本編開始を記録しました（チュートリアル終了）。", this);
    }

    // ── CAUGHT（親機の捕獲）の購読 ────────────────────────────────────────────
    //   受信後の結果処理（TriggerResult(GameOver)）は本クラスが担当する。
    //   Host側（ChildNotificationHub）は「受理した」ことを通知するだけ（保留・受理済みは通信側が保持）。
    //   ・購読は指定UDPルート→Running→初期化済みHostの順で解決したHostが保持するHubを基準にする。
    //   ・結果処理に必要な初期化（Awake）が終わった「Start完了後」に購読を開始し、
    //     初期化途中に TriggerResult を実行して Start に上書きされる構造を避ける。
    //   ・Instance取得遅延には Update で再試行する。
    //   ・GameManager.instance に対応する正当な担当だけが購読する（二重購読しない）。
    private ChildUdpRuntimeHost _host;                     // 実行時解決したHost（Inspector割当なし）
    private ChildUdpRuntimeHost _caughtHost;               // CAUGHT を購読したHost
    private ChildNotificationHub _caughtHub;               // 実際に購読したHub（解除も同じ個体から行う）
    private bool _caughtSubscribed;

    // ── 捕獲演出の待機（親機の演出完了まで結果画面へ進ませない）────────────────
    [Header("捕獲演出の待機")]
    [Tooltip("親機の演出完了通知（SEQUENCE_DONE）を受け取るまでの上限（秒）。\n" +
             "超えたら通信断とみなし、理由をログに出して確定得点のまま結果画面へ進みます（永久待機しない）。")]
    [SerializeField, Min(1f)] private float sequenceWaitTimeoutSeconds = 45f;

    private bool _scoreFrozen;                 // 捕獲通知で確定したか（以後の加算を停止）
    private int _frozenScore;                  // 確定得点（結果・ランキングはこの値）
    private bool _capturePending;              // 捕獲済みで演出完了待ち（タイマー停止・捕獲優先の判定）
    private bool _waitingSequenceDone;         // CaptureSequenceReleased を購読済みか
    private bool _sequenceDoneReceived;        // 演出完了（または上限）で遷移済みか
    private ChildUdpRuntimeHost _releaseSubscribedHost;    // 演出完了通知を購読したHost
    private ChildNotificationHub _releaseSubscribedHub;    // 実際に購読したHub（解除も同じ個体から行う）
    private Coroutine _sequenceWaitRoutine;
    private bool _started;   // Start完了フラグ（初回の購読をStart後へ遅らせるため）

    public enum ResultType { GameOver, TimeUp }

    private void OnEnable()
    {
        // 初回は Start 完了まで購読しない（結果処理に必要な初期化が終わってから受け取る）。
        // 再有効化（disable→enable）では Start が再実行されないため、ここで購読を再開する。
        if (_started) TrySubscribeCaught();
    }

    private void Start()
    {
        ResolveHost();

        // 初期化完了を記録してから CAUGHT の購読を開始する。
        _started = true;
        TrySubscribeCaught();
    }

    private void OnDisable()
    {
        UnsubscribeCaught();
    }

    private void OnDestroy()
    {
        UnsubscribeCaught();
    }

    /// <summary>
    /// 子機Hostを解決する：指定UDPルート（保存参照）→ 稼働個体（Running）→ 初期化済みHost。
    ///  ・ルートが未割当・破棄済みの場合は既存の生存個体取得へ進む。
    ///  ・未初期化Hostは使わない（既存の再試行を維持する）。
    /// </summary>
    private ChildUdpRuntimeHost ResolveHost()
    {
        if (_host != null) return _host;

        if (udpReceiver != null)
        {
            ChildUdpRuntimeHost onRoot = udpReceiver.GetComponent<ChildUdpRuntimeHost>();
            if (onRoot != null && onRoot.IsConfigured) { _host = onRoot; return _host; }
        }

        ChildUdpRuntimeHost running = ChildUdpRuntimeHost.Running;
        if (running != null) { _host = running; return _host; }

        ChildUdpRuntimeHost found = UnityEngine.Object.FindFirstObjectByType<ChildUdpRuntimeHost>();
        if (found != null && found.IsConfigured) _host = found;
        return _host;
    }

    /// <summary>
    /// CAUGHT（親機の捕獲）を購読する。Start完了後の正当な GameManager だけが購読する。
    /// Instance が未取得の場合は購読せず false を返す（Update で再試行する）。
    /// </summary>
    private bool TrySubscribeCaught()
    {
        if (_caughtSubscribed) return true;
        if (!isActiveAndEnabled) return false;
        if (instance != this) return false;   // GameManager.instance に対応する正当な担当だけ

        ChildUdpRuntimeHost host = ResolveHost();
        if (host == null || host.Notifications == null) return false;   // 取得できたら Update で再試行する

        _caughtHost = host;
        _caughtHub = host.Notifications;
        _caughtHub.CaughtReceived += HandleCaughtReceived;
        _caughtSubscribed = true;
        Debug.Log($"[GameManager] CAUGHT の購読を開始しました（Host='{host.gameObject.name}'）。");
        return true;
    }

    /// <summary>CAUGHT の購読を解除する（OnDisable/OnDestroy から呼ぶ。二重に呼んでも安全）。</summary>
    private void UnsubscribeCaught()
    {
        if (_caughtSubscribed)
        {
            if (_caughtHub != null)
                _caughtHub.CaughtReceived -= HandleCaughtReceived;

            _caughtSubscribed = false;
        }

        _caughtHub = null;
        _caughtHost = null;

        // 演出完了通知の購読も解除する（破棄後に呼ばれないようにする）。
        //  ・生存個体を再取得せず、実際に購読した同じ個体から解除する。
        if (_waitingSequenceDone)
        {
            if (_releaseSubscribedHub != null)
                _releaseSubscribedHub.CaptureSequenceReleased -= HandleCaptureSequenceReleased;

            _waitingSequenceDone = false;
        }

        _releaseSubscribedHub = null;
        _releaseSubscribedHost = null;

        if (_sequenceWaitRoutine != null)
        {
            StopCoroutine(_sequenceWaitRoutine);
            _sequenceWaitRoutine = null;
        }

        _caughtHost = null;
    }

    /// <summary>
    /// 親機の捕獲（CAUGHT）を受理したときに、メインスレッドで1回だけ呼ばれる。
    /// 既存の TriggerResult(GameOver) をそのまま呼ぶ（本体・保存・ランキング・遷移は変更しない）。
    /// </summary>
    private void HandleCaughtReceived()
    {
        if (!isActiveAndEnabled) return;
        if (isGameOver) return;   // TriggerResult 側のガードと同じ（無駄な再入を避けるだけ）

        // 1) 捕獲時点の得点を確定し、以後の加算を停止する（ランキング・最終得点はこの値を使う）。
        FreezeScoreForCapture("捕獲通知（CAUGHT）");

        // 2) 親機の演出完了通知を待ってから結果画面へ進む（入力・得点は既に停止済み）。
        //    通知が来ない（通信断）場合は上限で解除するため、永久には待たない。
        if (WaitForSequenceDone())
            return;

        TriggerResult(ResultType.GameOver);
    }

    /// <summary>捕獲時点の得点を確定得点として保存し、以後の加算を止める（二重実行しても値は変わらない）。</summary>
    private void FreezeScoreForCapture(string reason)
    {
        if (_scoreFrozen) return;

        _scoreFrozen = true;
        _capturePending = true;   // 演出完了待ちの間はタイマーを止め、TIME_UPより捕獲を優先する
        _frozenScore = score;   // score 自体は以後変更されないため、この値が最終得点になる

        Debug.Log($"[GameManager] 得点を確定しました（理由={reason}）: {_frozenScore}点。結果画面まで加算しません");

        // 捕獲でQTEが残らないように中断する（先に得点固定・捕獲待ちの状態を確定してから行う）。
        //  ・QTEが実際に動作していたときだけ、QTE由来の時間停止（Time.timeScale = 0）を解除する。
        //  ・捕獲待ちは結果確定前のため、結果処理の停止状態はまだ存在しない。
        //  ・得点固定・演出完了待ち・入力ロック（ChildSleepLockApplier 管理）には触れない。
        QTEManager qte = QTEManager.Instance;
        if (qte != null && qte.CancelQteForResult())
        {
            Time.timeScale = 1f;
        }
    }

    /// <summary>
    /// 親機の演出完了通知（Hostが保持するHubの CaptureSequenceReleased）を待つ。
    ///  ・既に受信済み／購読できる相手が居ない場合は false（呼び出し側が即座に遷移する）。
    ///  ・二重購読しない。
    /// </summary>
    private bool WaitForSequenceDone()
    {
        ChildUdpRuntimeHost host = ResolveHost();
        if (host == null || host.Notifications == null)
        {
            Debug.LogWarning("[GameManager] 子機Hostが見つからないため、演出完了を待てません（即座に結果画面へ進みます）", this);
            return false;
        }

        if (_sequenceDoneReceived)
        {
            Debug.Log("[GameManager] 演出完了通知は既に受信済みのため、待たずに結果画面へ進みます");
            return false;
        }

        // 1) 先に購読する。この後に状態を確認するため、購読前に完了していても取りこぼさない。
        if (!_waitingSequenceDone)
        {
            _waitingSequenceDone = true;
            _releaseSubscribedHost = host;           // 解除は「実際に購読した相手」から行う
            _releaseSubscribedHub = host.Notifications;
            _releaseSubscribedHub.CaptureSequenceReleased += HandleCaptureSequenceReleased;
            Debug.Log($"[GameManager] 親機の演出完了通知を購読しました（Host='{host.gameObject.name}'）");
        }

        // 2) 購読成立時に現在プレイの状態を初期同期する。
        //    SEQUENCE_DONE先着 → CAUGHT の順でも、Receiverが完了済みを保持しているためここで確定できる。
        //    「待機中ではない」だけでは完了扱いにしない（未捕獲は false）。
        if (host.IsCaptureWaitCompletedForCurrentPlay)
        {
            _sequenceDoneReceived = true;
            Debug.Log("[GameManager] 演出完了は購読成立時に既に確定済みでした（初期同期）。待たずに結果画面へ進みます");
            return false;
        }

        // 3) まだ待機中：上限付きで待つ（通知が来なくても永久には待たない）。
        if (_sequenceWaitRoutine == null)
            _sequenceWaitRoutine = StartCoroutine(SequenceWaitTimeoutRoutine());

        return true;
    }

    /// <summary>演出完了通知の受信（または子機側の上限到達）で、確定得点のまま結果画面へ進む。</summary>
    private void HandleCaptureSequenceReleased(string reason)
    {
        if (_sequenceDoneReceived) return;
        _sequenceDoneReceived = true;

        Debug.Log($"[GameManager] 親機の演出完了通知を受信しました（理由={reason}）。確定得点 {_frozenScore}点 で結果画面へ進みます");

        if (_sequenceWaitRoutine != null)
        {
            StopCoroutine(_sequenceWaitRoutine);
            _sequenceWaitRoutine = null;
        }

        TriggerResult(ResultType.GameOver);
    }

    /// <summary>
    /// 【異常時フォールバック】親機の演出完了通知が来ない場合（通信断など）の上限。
    /// 理由をログに出して結果画面へ進む（永久待機しない）。
    /// </summary>
    private IEnumerator SequenceWaitTimeoutRoutine()
    {
        yield return new WaitForSecondsRealtime(Mathf.Max(1f, sequenceWaitTimeoutSeconds));

        _sequenceWaitRoutine = null;

        if (_sequenceDoneReceived) yield break;

        _sequenceDoneReceived = true;
        Debug.LogWarning($"[GameManager] 【フォールバック】親機の演出完了通知を {sequenceWaitTimeoutSeconds:F1}s で受信できませんでした" +
                         "（通信断の可能性）。確定得点のまま結果画面へ進みます", this);

        // Receiver側の待機状態と、入力適用側（ロック・画像）に古い状態を残さない。
        //  ・結果遷移はこのメソッドが行う（Receiverの解除通知では遷移しない＝二重結果処理を防ぐ）。
        if (_releaseSubscribedHost != null)
            _releaseSubscribedHost.ReleaseCaptureWaitByLocalTimeout("GameManagerの待機タイムアウト");

        TriggerResult(ResultType.GameOver);
    }

    void Awake()
    {
        instance = this;

        if (fadeCanvasGroup != null)
        {
            fadeCanvasGroup.alpha = 0f;
            fadeCanvasGroup.interactable = false;
            fadeCanvasGroup.blocksRaycasts = false;
        }

        if (timeChangeText != null)
        {
            timeChangeText.gameObject.SetActive(false);
        }

        if (feverLoopEffect != null)
        {
            feverLoopEffect.StopEffect();
        }

        if (feverLoopEffect2 != null)
        {
            feverLoopEffect2.StopEffect();
        }
    }

    void Update()
    {
        // CAUGHT の購読がまだ張れていない場合は再試行する（子機Hostの取得遅延）。
        if (_started && !_caughtSubscribed)
            TrySubscribeCaught();

        if (isGameOver) return;

        // 捕獲演出の待機中はタイマーを進めない（TIME_UPが先に確定して GameClear へ進まないようにする）。
        if (_capturePending) return;

        if (!isTutorialMode)
        {
            // QTE中は QTEManager が Time.timeScale = 0 にするため、ゲーム本体の残り時間は
            // 実時間（unscaled）で進める。QTE中以外は従来どおり Time.deltaTime を使う
            // （Time.deltaTime の一括置換はしない）。
            bool qteActive = QTEManager.Instance != null && QTEManager.Instance.IsQteActive;
            time -= qteActive ? Time.unscaledDeltaTime : Time.deltaTime;

            if (time <= 0f)
            {
                time = 0f;
                GameOver();

                // 結果確定後はこのフレームの通常処理（得点表示・進行率通知など）を続けない。
                // 時間表示だけは00:00へ更新する。
                UpdateTimeText();
                return;
            }
        }

        UpdateScoreDigits();
        UpdateTimeText();

        if (feverText != null)
        {
            feverText.text = string.Format("{0}/{1}", feverCount, feverNeeded);
        }

        // 片付け演出用：進行率を親機へ定期通知する（出現率・得点には影響しない）。
        SendGameProgressIfDue();
    }

    /// <summary>
    /// 残り時間の表示を更新する。
    /// 表示は「残り時間を切り上げた総秒数」から算出する（減算直後に切り捨てると、
    /// 開始直後が 04:00 ではなく 03:59 になってしまうため）。
    /// 判定（時間切れ・片付け解禁・Loud Item解禁）には使わず、time の生値のみを使う。
    /// </summary>
    private void UpdateTimeText()
    {
        int displayTotalSeconds = Mathf.CeilToInt(Mathf.Max(0f, time));
        int minutes = displayTotalSeconds / 60;
        int seconds = displayTotalSeconds % 60;
        timeText.text = string.Format("{0:00}:{1:00}", minutes, seconds);
    }

    /// <summary>
    /// 現在のゲーム進行率（0〜1）を算出し、間隔どおりに親機へ送信する。
    /// 片付け演出（母親の後半入室）の開始条件に使用する。
    /// </summary>
    private void SendGameProgressIfDue()
    {
        if (!sendGameProgress) return;
        if (isGameOver || isTutorialMode) return;
        if (Time.unscaledTime < _nextGameProgressSendTime) return;

        ChildUdpRuntimeHost host = ResolveHost();
        if (host == null) return;

        float maxTimeSafe = Mathf.Max(1f, maxTime);
        float progressRate = Mathf.Clamp01(1f - (time / maxTimeSafe));
        host.SendGameProgress(progressRate);

        _nextGameProgressSendTime = Time.unscaledTime + Mathf.Max(0.1f, gameProgressSendInterval);
    }

    /// <summary>
    /// score を1桁ずつ分解して scoreDigitTexts の各要素に書き込む。
    /// 上限は 999999。それを超えた場合は 999999 として表示する。
    /// </summary>
    private void UpdateScoreDigits()
    {
        if (scoreDigitTexts == null || scoreDigitTexts.Length == 0) return;

        // 表示上限クランプ（6桁 = 999999）
        int displayScore = Mathf.Clamp(score, 0, 999999);

        // 最下位桁から順に取り出し、配列の末尾から埋める
        int remaining = displayScore;
        for (int i = SCORE_DIGITS - 1; i >= 0; i--)
        {
            int digit = remaining % 10;
            remaining /= 10;

            if (i < scoreDigitTexts.Length && scoreDigitTexts[i] != null)
            {
                scoreDigitTexts[i].text = digit.ToString();
            }
        }
    }

    public void AddScore(int amount)
    {
        // 捕獲通知後は、結果画面まで一切ポイントを増やさない（毎フレーム加算・アイテム・コンボ・
        // ボーナス・遅延処理などの全加算入口はここを通る）。
        if (_scoreFrozen)
        {
            Debug.Log($"[GameManager] 捕獲後のためポイント加算を停止中（amount={amount} を無視）。確定得点={_frozenScore}");
            return;
        }

        // 結果確定後（捕獲以外の時間切れなど）に遅延して届いた取得でも最終得点を変えない。
        if (isGameOver) return;

        // フィーバー中は「正の得点」だけ2倍にする（減点は倍にしない）。
        if (isFeverActive && amount > 0)
        {
            amount *= 2;
        }

        // 減点アイテムで合計スコアが負にならないよう、内部値も0以上に保つ。
        score = Mathf.Max(0, score + amount);

        // 最高到達スコアが更新されたときだけ速度段階を選び直す（毎フレームの再計算はしない）。
        //  ・フィーバー2倍を適用した後の実スコアで判定する（上の amount *= 2 の後）。
        //  ・減点でスコアが下がっても maxScore は下げないため、段階は下がらない。
        //  ・チュートリアル中の取得は本プレイの最高到達スコアへ持ち込まない。
        if (!isTutorialMode && score > maxScore)
        {
            maxScore = score;
            UpdateScoreSpeedStage();
        }
    }

    /// <summary>
    /// 最高到達スコアから速度段階倍率を選び直す（表から絶対値を選ぶ。掛け重ねない）。
    ///  ・表が空／該当なし／不正値のみの場合は 1.0 倍にフォールバックする。
    ///  ・並び順に依存しない（全件走査して「該当する最大しきい値」を選ぶ。実行時ソートはしない）。
    ///  ・同じしきい値が重複している場合は、その中で最大の倍率を使う（順序に依存しない）。
    ///  ・負のしきい値、0以下の倍率、NaN/Infinity の倍率は無効として無視する。
    ///  ・呼び出しは最高到達スコアが更新されたときだけ（毎フレームのソート・割り当てはしない）。
    /// </summary>
    private void UpdateScoreSpeedStage()
    {
        float selected = 1f;
        int bestThreshold = -1;
        bool found = false;

        if (scoreSpeedSteps != null)
        {
            for (int i = 0; i < scoreSpeedSteps.Length; i++)
            {
                ScoreSpeedStep step = scoreSpeedSteps[i];

                if (step.score < 0) continue;
                if (step.multiplier <= 0f || float.IsNaN(step.multiplier) || float.IsInfinity(step.multiplier)) continue;
                if (step.score > maxScore) continue;

                bool isBetter = !found
                                || step.score > bestThreshold
                                || (step.score == bestThreshold && step.multiplier > selected);
                if (!isBetter) continue;

                bestThreshold = step.score;
                selected = step.multiplier;
                found = true;
            }
        }

        scoreSpeedMultiplier = selected;
    }

    // Called when timer runs out — TIME_UP result
    // 【修正済み】ゲームオーバー時の不要なフィーバー加算バグを除去
    public void GameOver()
    {
        TriggerResult(ResultType.TimeUp);
    }

    // Called when parent catches child — GAME_OVER result
    public void GameOver(string udpMessage)
    {
        // Legacy entry point: "TIME_UP" and "CHILD_DEAD" use TIME_UP result;
        // "CAUGHT" and anything else uses GAME_OVER result.
        if (udpMessage == "TIME_UP" || udpMessage == "CHILD_DEAD")
            TriggerResult(ResultType.TimeUp);
        else
            TriggerResult(ResultType.GameOver);
    }

    // 【修正済み】ランキング更新ロジックを追加
    public void TriggerResult(ResultType resultType)
    {
        if (isGameOver) return;

        // 【捕獲優先】捕獲済み（演出待機中）なら、TIME_UP が要求されても GameOver として確定する。
        //  ・捕獲時点の確定得点（score は凍結済み）を使って保存・ランキング・遷移する。
        //  ・GameClear へ進ませない。
        if (_capturePending && resultType == ResultType.TimeUp)
        {
            Debug.LogWarning("[GameManager] 【捕獲優先】TIME_UP要求を GameOver として確定します（捕獲時点の確定得点を使用）", this);
            resultType = ResultType.GameOver;
        }

        isGameOver = true;

        // 結果確定の共通入口でQTEを片付ける（成功・失敗としては扱わない）。
        //  ・時間切れ／捕獲／その他どの経路から確定してもQTEが残らないようにする。
        //  ・Time.timeScale はここでは変更しない（この後の Time.timeScale = 0f が結果確定の停止を担う）。
        QTEManager qte = QTEManager.Instance;
        if (qte != null) qte.CancelQteForResult();

        // Save score under type-specific key
        string scoreKey = resultType == ResultType.GameOver
            ? KeyGameOverScore
            : KeyTimeUpScore;
        string rankKey = resultType == ResultType.GameOver
            ? KeyGameOverRank
            : KeyTimeUpRank;

        PlayerPrefs.SetInt(scoreKey, score);
        PlayerPrefs.SetString(KeyResultTypePending, resultType == ResultType.GameOver ? "GAME_OVER" : "TIME_UP");

        // ランキングの更新
        UpdateRanking(rankKey, score);

        PlayerPrefs.Save();
        Debug.Log($"[GameManager] Result={resultType} score={score} saved to '{scoreKey}'");

        // 書き込み完了を通知（結果シーンの表示がシーン読み込み後でも最新データに同期する）
        ResultDataCommitted?.Invoke();

        // 一時的に時間を止める（UI表示などがある場合）。遷移はRealtimeで行う。
        Time.timeScale = 0f;

        // 最終結果は親機へ「プレイID付き」で送る（親機の保存確認 ACK が来るまで子機側で保持・再送される）
        string resultTypeName = resultType == ResultType.GameOver ? "GAME_OVER" : "TIME_UP";
        string targetScene = resultType == ResultType.GameOver ? gameOverSceneName : timeUpSceneName;
        StartCoroutine(HandleGameOver(resultTypeName, score, targetScene));
    }

    // 【追加】PlayerPrefsを用いたランキング保存メソッド
    // 挿入ロジック本体は RankingUtil に集約（親機と共有・単体テスト可能）。挙動は従来と同一。
    private static void UpdateRanking(string keyPrefix, int newScore)
    {
        // 保存前の全順位（2位がどこで0になるかの追跡用）
        int[] before = RankingUtil.ReadFromPlayerPrefs(keyPrefix, RankingSize);
        RankingUtil.InsertScoreToPlayerPrefs(keyPrefix, newScore, RankingSize);
        int[] after = RankingUtil.ReadFromPlayerPrefs(keyPrefix, RankingSize);

        Debug.Log($"[GameManager] UpdateRanking key='{keyPrefix}', before=[{string.Join(", ", before)}], " +
                  $"newScore={newScore}, after=[{string.Join(", ", after)}]");
    }

    private void ActivateFeverEffects()
    {
        // 成立時の固定加点（旧 feverScoreBonus）は廃止し、フィーバー中の得点2倍に置き換えた。
        // 速度上昇（PlayerBoost）も廃止した（Boostアイテム単体の効果は変更しない）。

        float duration = Mathf.Max(0f, feverBoostDuration);

        isFeverActive = true;
        feverEndTime = Mathf.Max(feverEndTime, Time.time + duration);

        if (feverLoopEffect != null)
        {
            feverLoopEffect.StartEffect();
        }

        if (feverLoopEffect2 != null)
        {
            feverLoopEffect2.StartEffect();
        }

        if (feverRoutine == null)
        {
            feverRoutine = StartCoroutine(FeverEffectRoutine());
        }
    }

    private IEnumerator FeverEffectRoutine()
    {
        while (Time.time < feverEndTime)
        {
            yield return null;
        }

        StopFeverEffects();
    }

    private IEnumerator HandleGameOver(string resultTypeName, int finalScore, string targetScene)
    {
        ChildUdpRuntimeHost host = ResolveHost();
        if (host != null)
        {
            // 結果確定後、子機を唯一の基準としてランキング／スコアのスナップショットを親機へ同期する。
            // （親機の確認返信は待たず、子機側の保存はここで完了している）
            host.NotifyRankingChanged(resultTypeName);
            Debug.Log($"[GameManager] Notified ranking change (snapshot sync): {resultTypeName}:{finalScore}");
        }
        else
        {
            Debug.LogWarning("[GameManager] 子機Hostが見つからないため、結果を親機へ送信できません。");
        }

        yield return new WaitForSecondsRealtime(0.1f);

        if (fadeCanvasGroup != null)
        {
            if (!fadeCanvasGroup.gameObject.activeSelf)
            {
                fadeCanvasGroup.gameObject.SetActive(true);
            }

            fadeCanvasGroup.interactable = false;
            fadeCanvasGroup.blocksRaycasts = true;
            yield return StartCoroutine(FadeOutRoutine());
        }

        float delay = Mathf.Max(0f, gameOverDelay);
        if (delay > 0f)
        {
            yield return new WaitForSecondsRealtime(delay);
        }

        // シーン遷移の前にタイムスケールを復帰させる
        Time.timeScale = 1f;

        if (!string.IsNullOrEmpty(targetScene))
        {
            SceneManager.LoadScene(targetScene);
        }
    }

    private IEnumerator FadeOutRoutine()
    {
        if (fadeCanvasGroup == null)
        {
            yield break;
        }

        float duration = Mathf.Max(0f, fadeSeconds);
        if (duration <= 0f)
        {
            fadeCanvasGroup.alpha = 1f;
            yield break;
        }

        float startAlpha = fadeCanvasGroup.alpha;
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            fadeCanvasGroup.alpha = Mathf.Lerp(startAlpha, 1f, t);
            yield return null;
        }

        fadeCanvasGroup.alpha = 1f;
    }

    public void AddFeverCount()
    {
        feverCount++;
        if (feverCount >= feverNeeded)
        {
            feverCount = 0; // Reset fever count
            ActivateFeverEffects();
        }
    }

    public void AddTime(float amount)
    {
        if (amount < 0f)
        {
            ShowTimeDecrease(amount);
        }

        time += amount;
        if (time > maxTime) time = maxTime;
    }

    private void ShowTimeDecrease(float amount)
    {
        if (timeChangeText == null) return;

        int totalSeconds = Mathf.Abs(Mathf.RoundToInt(amount));
        int minutes = totalSeconds / 60;
        int seconds = totalSeconds % 60;

        timeChangeText.text = $"-{minutes:00}:{seconds:00}";
        timeChangeText.gameObject.SetActive(true);

        if (timeChangeRoutine != null)
        {
            StopCoroutine(timeChangeRoutine);
        }

        timeChangeRoutine = StartCoroutine(HideTimeChangeAfterDelay());
    }

    private IEnumerator HideTimeChangeAfterDelay()
    {
        float duration = Mathf.Max(0f, timeChangeDisplaySeconds);
        if (duration > 0f)
        {
            yield return new WaitForSecondsRealtime(duration);
        }

        if (timeChangeText != null)
        {
            timeChangeText.gameObject.SetActive(false);
        }

        timeChangeRoutine = null;
    }

    public void SetTutorialMode(bool active)
    {
        isTutorialMode = active;
    }

    public void ResetScoreAndFever()
    {
        // 再プレイ：得点ロックと演出待機の状態も初期化する（前回プレイの待機を残さない）。
        _scoreFrozen = false;
        _frozenScore = 0;
        _capturePending = false;
        _sequenceDoneReceived = false;
        score = 0;
        feverCount = 0;

        // 最高到達スコアと速度段階も初期化する（再プレイに前回の最高段階を残さない）。
        maxScore = 0;
        scoreSpeedMultiplier = 1f;

        StopFeverEffects();
        UpdateScoreDigits();

        if (feverText != null)
        {
            feverText.text = string.Format("{0}/{1}", feverCount, feverNeeded);
        }
    }

    private void StopFeverEffects()
    {
        if (feverRoutine != null)
        {
            StopCoroutine(feverRoutine);
            feverRoutine = null;
        }

        isFeverActive = false;
        feverEndTime = -1f;

        if (feverLoopEffect != null)
        {
            feverLoopEffect.StopEffect();
        }

        if (feverLoopEffect2 != null)
        {
            feverLoopEffect2.StopEffect();
        }
    }
}