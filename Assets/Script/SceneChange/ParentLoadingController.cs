using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// ParentLoadingController（親機ロード専用）：
/// ロード画面の表示から ParentGameScene への遷移までと、子機の準備完了を待つ判断を持つ。
///
/// 責務の境界（第1段階：待機判断の分離）：
///   本クラス        — 最小表示時間・待機判断（ローカル状態 _childLoadingComplete）・遷移（_sceneLoaded で1回）
///   ParentUdpSender — ソケット・受信スレッド・LOADING_COMPLETE の受信とメインスレッドへの受け渡し・
///                     現在プレイの受信記録 ChildLoadingComplete（購読前受信の初期同期用に暫定保持）
///
///  ・UDP側の受信記録（ChildLoadingComplete）は「通信層が受け取った事実」であり、
///    待機するかどうかの判断は本クラスのローカル状態で行う（本クラスから受信記録は書き換えない）。
///  ・接続先は ParentUdpSender.Instance（DDOLの生存個体）を基準にする。
///    シーンの重複UDP個体は Awake で自壊するため、重複個体を検索して掴まない（Inspector参照も持たない）。
///  ・LOADING_COMPLETE にプレイ識別子が無いため、前プレイの遅延通知は完全には識別できない。
///    受信記録の初期化を「開始受理時」と「タイトル復帰時」に行うことで影響を抑える（識別子の追加は別タスク）。
/// </summary>
public class ParentLoadingController : MonoBehaviour
{
    [Header("Scene Settings")]
    [Tooltip("Name of the parent game scene to load after loading is done.")]
    public string gameSceneName = "ParentGameScene";

    [Header("Timing")]
    [Tooltip("Minimum seconds to show the loading screen before transitioning, even if child is already ready.")]
    public float minimumDisplaySeconds = 1.0f;

    // ── 待機判断（ロード側のローカル状態）────────────────────────────────────
    // UDP側の受信記録とは別に、このロード画面が待機判断に使う状態をローカルに持つ。
    // イベント受信、または購読前分の初回読み取りで true になり、以降は再送が来ても変わらない。
    private bool _childLoadingComplete = false;

    private ParentUdpSender _udpSender;
    private bool _subscribed = false;
    private bool _sceneLoaded = false;

    private void OnEnable()
    {
        // 有効化し直したときも購読を張り直せるようにする（未生成なら Start/Update で再試行）。
        TryBindUdpSender();
    }

    private void Start()
    {
        // 生存UDPの取得 → 購読 → 現在値の読み取り は TryBindUdpSender 内で行う。
        if (!TryBindUdpSender())
        {
            Debug.LogWarning("[ParentLoadingController] ParentUdpSender.Instance が未生成です — 待機中に再試行します（購読できるまで待機を続けます）。");
        }

        StartCoroutine(LoadingRoutine());
    }

    private void Update()
    {
        // UDPの取得が遅れた場合の再試行（購読と初回同期は TryBindUdpSender 内で行う）。
        if (_sceneLoaded || _subscribed) return;
        TryBindUdpSender();
    }

    private void OnDisable()
    {
        UnbindUdpSender();
    }

    private void OnDestroy()
    {
        UnbindUdpSender();
    }

    // ── UDPへの接続（生存個体＝DDOLを基準にする）──────────────────────────────

    /// <summary>
    /// 生存UDP（ParentUdpSender.Instance）を 取得 → 完了通知を購読 → 現在プレイの受信記録を読み取り、
    /// の順で結び付ける。先に購読してから読むため、購読の前後どちらで届いた通知も取りこぼさない。
    /// </summary>
    /// <returns>購読済み（または今回購読できた）なら true。</returns>
    private bool TryBindUdpSender()
    {
        if (_subscribed) return true;

        ParentUdpSender alive = ParentUdpSender.Instance;
        if (alive == null) return false;

        _udpSender = alive;

        // 1) 購読（以降に届く通知はイベントで受け取る）
        _udpSender.LoadingCompleteReceived += OnChildLoadingCompleteReceived;
        _subscribed = true;

        // 2) 初回同期（購読前に届いていた完了通知を、UDP側の受信記録から反映する）
        if (_udpSender.ChildLoadingComplete && !_childLoadingComplete)
        {
            _childLoadingComplete = true;
            Debug.Log("[ParentLoadingController] 購読前に届いていた完了通知を、UDP側の受信記録から反映しました（ローカル完了=true）。");
        }

        Debug.Log($"[ParentLoadingController] 生存UDPへ接続 '{alive.gameObject.name}' (id={alive.GetInstanceID()}, UDP受信記録={_udpSender.ChildLoadingComplete}, ローカル完了={_childLoadingComplete})", this);
        return true;
    }

    /// <summary>
    /// 購読を解除して参照を手放す（OnDisable/OnDestroy から呼ぶ）。
    /// 無効化・破棄の後に通知を受けないようにする。二重に呼ばれても安全。
    /// </summary>
    private void UnbindUdpSender()
    {
        if (_subscribed && _udpSender != null)
        {
            _udpSender.LoadingCompleteReceived -= OnChildLoadingCompleteReceived;
        }

        _subscribed = false;
        _udpSender = null;
    }

    /// <summary>
    /// 子機の完了通知（UDP側がメインスレッドで発火する）。
    /// 再送で複数回呼ばれてもローカル状態は1回しか更新しない（遷移も _sceneLoaded ガードで1回）。
    /// 無効化・破棄の後は何もしない（通知を受けて遷移しない）。
    /// </summary>
    private void OnChildLoadingCompleteReceived()
    {
        if (!isActiveAndEnabled) return;
        if (_sceneLoaded) return;
        if (_childLoadingComplete) return;

        _childLoadingComplete = true;
        Debug.Log("[ParentLoadingController] 子機の完了通知を受信 — ローカル完了=true。");
    }

    private IEnumerator LoadingRoutine()
    {
        float elapsed = 0f;

        // Phase 1: 最小表示時間の待機
        string senderId = _udpSender != null ? _udpSender.GetInstanceID().ToString() : "null";
        Debug.Log($"[StartTrace][親機] ParentLoadingController.LoadingRoutine 開始 — 最小表示待ち開始 (minimumDisplaySeconds={minimumDisplaySeconds}, senderId={senderId}, scene='{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}', id={GetInstanceID()})", this);
        while (elapsed < minimumDisplaySeconds)
        {
            elapsed += Time.unscaledDeltaTime;
            yield return null;
        }
        Debug.Log($"[StartTrace][親機] ParentLoadingController.LoadingRoutine 最小表示待ち解除 (elapsed={elapsed:F2}s)", this);

        // ─── 修正箇所 ───
        if (_udpSender != null)
        {
            Debug.Log("[ParentLoadingController] Sending START_LOADING to child.");
            
            // 内部でNullReferenceExceptionが出ないよう、念のためSender側が
            // クライアントの初期化を終えているタイミング（Startが走った後など）を見計らって送信します
            // もしこれでも即座にNullエラーが出る場合は、1フレーム待ってから送るようにします
            yield return null; 

            _udpSender.SendState("START_LOADING");
        }
        // ────────────────

        Debug.Log("[ParentLoadingController] Minimum display time reached — waiting for child LOADING_COMPLETE.");

        // Phase 2: 子機からの LOADING_COMPLETE を待つ。
        // LOADING_COMPLETE は「子機のチュートリアル（カウントダウンまで）が完了した」ことを意味するため、
        // これを待たずに親機だけゲームを開始すると子機と進行がずれる。よって自動タイムアウトでの
        // 「親機だけ開始」は行わない（ゲーム開始条件は変更しない）。従来どおり無期限に待つが、
        // 原因切り分けのため一定間隔で警告ログを出す（固まっているのか遅いだけかを判別できるようにする）。
        const float stallWarningIntervalSeconds = 10f;
        float waitElapsed = 0f;
        float nextWarningAt = stallWarningIntervalSeconds;
        while (true)
        {
            // 待機判断はロード側のローカル状態で行う（UDP側の受信記録は購読前分の初期同期にのみ使う）。
            if (_childLoadingComplete)
            {
                Debug.Log("[ParentLoadingController] Child LOADING_COMPLETE received — transitioning (ローカル完了).");
                break;
            }

            waitElapsed += Time.unscaledDeltaTime;
            if (waitElapsed >= nextWarningAt)
            {
                Debug.LogWarning($"[ParentLoadingController] Still waiting for child LOADING_COMPLETE ({waitElapsed:F1}s). " +
                                 "子機がチュートリアル中の可能性があります。親機だけでは開始しません（進行同期のため）。");
                nextWarningAt += stallWarningIntervalSeconds;
            }

            yield return null;
        }

        Transition();
    }

    private void Transition()
    {
        if (_sceneLoaded) return;
        _sceneLoaded = true;
        Debug.Log($"[ParentLoadingController] Loading {gameSceneName}.");
        string udpRecord = _udpSender != null ? _udpSender.ChildLoadingComplete.ToString() : "null";
        Debug.Log($"[StartTrace][親機] ParentLoadingController → SceneManager.LoadScene('{gameSceneName}') 直前 (現在scene='{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}', ローカル完了={_childLoadingComplete}, UDP受信記録={udpRecord})", this);
        SceneManager.LoadScene(gameSceneName);
    }
}