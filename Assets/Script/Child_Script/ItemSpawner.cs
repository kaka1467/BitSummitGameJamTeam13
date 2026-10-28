using UnityEngine;
using System;
using System.Collections.Generic;
using UnityEngine.InputSystem;
using Unity.Profiling;

public partial class ItemSpawner : MonoBehaviour
{
    // ── 計測用マーカー（Profilerで処理時間と呼び出し回数を見る。動作は変更しない）──
    private static readonly ProfilerMarker s_buildScheduleMarker =
        new ProfilerMarker("ItemSpawner.BuildNormalSpawnSchedule");
    private static readonly ProfilerMarker s_handleScheduledMarker =
        new ProfilerMarker("ItemSpawner.HandleScheduledNormalSpawns");
    private static readonly ProfilerMarker s_tryPlaceMarker =
        new ProfilerMarker("ItemSpawner.TryPlaceItemWithoutOverlap");
    private static readonly ProfilerMarker s_isOccupiedMarker =
        new ProfilerMarker("ItemSpawner.IsSpawnPositionOccupied");
    private static readonly ProfilerMarker s_collectActiveItemsMarker =
        new ProfilerMarker("ItemSpawner.CollectActiveItems");

    [Serializable]
    public class SpawnIntervalRange
    {
        [Min(0f)] public float minInterval = 10f;
        [Min(0f)] public float maxInterval = 30f;
    }

    [Serializable]
    public class PrefabSpawnCount
    {
        public GameObject prefab;
        [Min(0)] public int count = 1;
    }

    [Serializable]
    public class NormalSpawnRule
    {
        [Tooltip("このルールの有効時間帯（ゲーム開始からの秒数）")]
        public SpawnIntervalRange interval = new SpawnIntervalRange();

        [Tooltip("ループを有効にするか")]
        public bool loopEnabled = false;

        [Tooltip("ループの周期（秒）。0 以下の場合は interval の幅（maxInterval - minInterval）を使用する）")]
        [Min(0f)] public float loopCycleDuration = 0f;

        public List<PrefabSpawnCount> prefabSpawnCounts = new List<PrefabSpawnCount>();
    }

    [Header("Normal Spawn Rules")]
    [SerializeField]
    private List<NormalSpawnRule> normalSpawnRules = new List<NormalSpawnRule>
    {
        new NormalSpawnRule()
    };

    [Header("Spawn Control")]
    [SerializeField] private bool spawnEnabled = true;

    [Header("Debug")]
    [Tooltip("スポーン関連の詳細ログを出力するかどうか")]
    [SerializeField] private bool showDebugLogs = false;

    // ── 通常出現の成功間隔（同一フレームに複数成功して3レーン横並びになる状態を緩和する） ──
    //  ・「実際に配置できた」ときだけ次の成功可能時刻を更新する（見送り・配置失敗・破棄では更新しない）。
    //  ・休止中は通常予定の配置試行そのものを休む（失敗として retryCount を増やさない）。
    //  ・休止は通常スケジュールの配置だけに適用する。チュートリアルの直接出現・デバッグキー・
    //    巨大障害物の別枠出現には適用しない。
    [Header("Normal Spawn Success Interval")]
    [Tooltip("通常アイテムの配置に成功した後、次の通常配置を休止する基準時間（秒）。0 でこの追加制御を無効化する。" +
             "実際の休止時間は 基準時間 × Random.Range(1-jitter, 1+jitter) になるため、最小値ではない。")]
    [SerializeField, Min(0f)] private float normalSpawnSuccessInterval = 0.2f;

    [Tooltip("基準待機時間のゆらぎ率（0〜1）。0 で固定、0.4 なら 0.6〜1.4 倍の範囲になる。")]
    [SerializeField, Range(0f, 1f)] private float normalSpawnSuccessIntervalJitter = 0.4f;

    [Tooltip("通常出現の予定をあきらめる猶予（秒）。元の予定時刻（再試行で更新しない時刻）からこの時間を過ぎた予定は破棄する。" +
             "0 以下や不正値は既定の 3 秒として扱う（無期限に持ち越さない）。以前のリトライ予算（maxRetryCount × retryInterval ≒ 3 秒）を目安にしている。")]
    [SerializeField] private float normalSpawnGiveUpSeconds = 3f;

    // ── 調査用の集計（既定OFF。showDebugLogs とは独立で、ONにしない限り出力しない） ──
    [Header("Spawn Stats (investigation)")]
    [Tooltip("通常出現の配置成功数などを実時間10秒ごとに1行だけ出力する。showDebugLogs とは独立。既定は OFF。")]
    [SerializeField] private bool enableSpawnStats = false;

    [Tooltip("集計ログを出す間隔（実時間の秒）。")]
    [SerializeField, Min(1f)] private float spawnStatsLogIntervalSeconds = 10f;

    // ── 良いアイテム同士のX方向近接配置（今回の試作。まとまり制御・レーン優先・中央レーン対策は別工程） ──
    //  ・有効時：良い同士（ItemType.Score＝Score/MoreScore、ItemType.Fever）のペアだけ
    //    「実サイズの半幅合計＋余白」まで詰められる。
    //  ・無効時：従来どおり Max(minSpawnDistanceX, 半幅合計) を使う。
    //  ・余白0は「接するまで詰める」設定であり、無効化ではない（無効化はフラグで行う）。
    //  ・片方が対象外（Clock/BGM/HugeObstacle(QTE)/Boost）または分類不明なら従来の判定を使う。
    //  ・Y方向の判定・固定記録の保持時間・速度・Collider・予定個数は変更しない。
    [Header("Good Item Close Placement (trial)")]
    [Tooltip("良いアイテム同士（Score/MoreScore/Fever）のX方向の必要間隔を「実サイズ＋余白」にする。OFFで従来の判定。")]
    [SerializeField] private bool enableGoodItemClosePlacement = true;

    [Tooltip("良いアイテム同士で詰められる追加の余白（ワールド単位）。0 は「接するまで詰める」設定で、無効化ではない。" +
             "負や NaN は 0 として扱う（サイズ合計より狭くはしない）。")]
    [SerializeField, Min(0f)] private float goodItemCloseMarginX = 0.15f;

    // ── レーン別の通常出現タイマー（今回の方式。旧・時刻付き予定方式の置き換え） ──
    //  ・上/中央/下の各レーンが独立に「最小〜最大」の待ち時間を抽選し、その時刻に自分のレーンへ1個だけ配置を試す。
    //  ・成功でも失敗でも、次回は「今回の現在時刻＋抽選値」で再抽選する（遅れの取り戻しはしない）。
    //  ・0秒を抽選しても、同じレーンの次回試行は次フレーム以降（1フレームにつき各レーン最大1回）。
    //  ・全体では同一フレームに最大3レーン分の試行を許可する（既存の全体成功間隔では直列化しない）。
    [Header("Normal Spawn (per-lane timer)")]
    [Tooltip("レーン別の通常出現：次の試行までの最小間隔（秒）。")]
    [SerializeField, Min(0f)] private float laneSpawnIntervalMinSeconds = 0.15f;

    [Tooltip("レーン別の通常出現：次の試行までの最大間隔（秒）。最小より小さい値・NaNは最小として扱う。")]
    [SerializeField, Min(0f)] private float laneSpawnIntervalMaxSeconds = 0.85f;

    [Header("HugeObstacle")]
    [SerializeField, Min(0)] private int hugeObstacleSpawnCount = 1;
    [SerializeField] private float hugeObstacleSpawnY = 1.929803e-08f;

    // ── BGMの通常出現条件 ─────────────────────────────────────────────────────
    //   子機の時間制限は廃止したため、BGMは「本編開始からの経過時間がこの値(秒)以上」になってから通常出現を許可する。
    //   （従来の『残り120秒未満』＝制限240秒での経過120秒以降と同じ既定値）
    [Header("BGM Spawn Condition")]
    [Tooltip("BGMアイテムを通常出現させてよい、本編開始からの経過時間のしきい値（秒）。経過時間がこの値以上になってから通常出現を許可する。")]
    [UnityEngine.Serialization.FormerlySerializedAs("bgmNormalSpawnRemainingSeconds")]
    [SerializeField, Min(0f)] private float bgmNormalSpawnElapsedSeconds = 120f;

    // ── デバッグ出現キー（数字1〜6） ──────────────────────────────────────────
    //   子機のゲーム中のみ有効。タイトル・ロード中・ゲーム終了後は出現させない。
    //   1:Score / 2:Boost / 3:Fever / 4:Clock / 5:BGM / 6:HugeObstacle / 7:配置パターン（順番に1つ）
    [Header("Debug Spawn Keys")]
    [Tooltip("数字キー(1〜7)によるデバッグ出現を有効にする。7は配置パターンを一覧の順に1つずつ出す。")]
    [SerializeField] private bool enableDebugSpawnKeys = true;
    [Tooltip("デバッグ出現に関するログを出力する。")]
    [SerializeField] private bool showDebugSpawnKeyLogs = true;

    [Header("Overlap Avoidance")]
    [SerializeField, Min(0f)] private float minSpawnDistanceX = 1.2f;
    [SerializeField, Min(0f)] private float minSpawnDistanceY = 0.6f;
    [SerializeField, Min(0f)] private float recentSpawnWindow = 2.0f;   // ★ 0.6→2.0: レコード保持時間を延長
    [SerializeField, Min(0f)] private float overlapAvoidanceXStep = 0.8f;
    [SerializeField, Min(1)] private int maxPlacementAttempts = 8;

    //  追加: 配置失敗時のリトライ間隔と最大リトライ回数
    [SerializeField, Min(0f)] private float retryInterval = 0.15f;
    [SerializeField, Min(1)] private int maxRetryCount = 20;

    [Serializable]
    private class ScheduledNormalSpawn
    {
        public GameObject prefab;
        public float spawnTime;        // 出現を実行する絶対時刻（Time.unscaledTime と直接比較する）
        public float visibleByTime;    // 画面に入っているべき絶対時刻（締め切り計算に使う）
        public int retryCount;      // リトライ回数
        public float plannedSpawnTime;   // 元の予定時刻（再試行で更新しない。待機期限と処理順の基準）
        public float tieBreak;           // 同じ予定時刻の順序を構築時に固定する乱数（登録順の偏りを避ける）
        public float bandEndSeconds;     // この予定が属する時間帯の終了（本プレイ開始からの相対秒。時間帯をまたいだ成功の判定に使う）

        // ループ用フィールド
        public bool loopEnabled;
        public float loopCycleDuration;   // 次サイクルまでの周期
        public float cycleStartTime;      // このサイクルが始まった絶対時刻（本プレイ開始時刻＋ルール内の相対秒）
        public float localVisibleOffset;  // サイクル内でのランダムオフセット（再スケジュール時に再利用）
    }

    [Serializable]
    private class RecentSpawnRecord
    {
        public Vector3 position;
        public float time;
        public float halfSizeX;
        public float halfSizeY;
        // 記録した時点の分類（良いアイテム＝Score/MoreScore/Fever か）。
        // ・X方向の必要間隔を3つの判定経路で同じルールにするために保持する。
        // ・アイテムがプールへ戻って別の用途に再利用されても、この記録の分類は作成時のまま固定される。
        public bool isGoodItem;
    }

    // 画面右端からどれだけ外側に出現させるか
    [SerializeField]
    private float spawnOffsetFromRight = 0.75f;

    [SerializeField]
    [Tooltip("各レーンのY座標 [上レーン, 中レーン, 下レーン]")]
    public float[] lanesY;
    [UnityEngine.SerializeField] private float hugeInitialDelay = 30f;
    [UnityEngine.SerializeField] private float hugeCooldownAfterQte = 20f;
    // フィーバー中に通常のHugeObstacle出現タイミングが来たとき、次の再判定まで先延ばしする秒数。
    // 予定は消費せず（出現機会を恒久的に失わず）、フィーバー終了後に再開する（毎フレーム再試行はしない）。
    [UnityEngine.SerializeField, Min(0.5f)] private float hugeFeverDeferralSeconds = 5f;

    private float nextHugeSpawnTime = -1f;
    private bool isHugeSpawnScheduled = false;

    // ── 通常出現の成功間隔の状態（BuildNormalSpawnSchedule で初期化する） ──
    //  ・「配置に成功した時刻＋休止時間」を絶対時刻で保持する。Time.unscaledTime と比較する。
    private float nextNormalSpawnAllowedTime;

    // ── 調査用の集計カウンタ（ログ出力だけを enableSpawnStats で制御する） ──
    private int _statNormalSuccessTotal;            // 通常配置成功の総数
    private int _statSameFrameMultiSuccessFrames;   // 同一フレームに2件以上成功した「回数」（フレーム数）
    private int _statRetryCount;                    // 配置失敗による再試行の回数
    private int _statDiscardByRetryLimit;           // リトライ上限による破棄数
    private int _statDiscardByDeadline;             // 待機期限（元の予定時刻＋猶予）による破棄数
    private int _statBgmSkipped;                    // Loud Item の条件による見送り数
    private int _statBandCrossingSuccess;           // 時間帯をまたいで配置成功した数（非ループ予定のみ）
    private int[] _statLaneSuccess;                 // レーン別の配置成功数（lanesY の並び順）
    private int[] _statPrefabSuccess;               // 種類別の配置成功数（_statPrefabs と同順）
    private GameObject[] _statPrefabs;              // 集計対象のPrefab（通常出現ルールの並び）
    private int _statSuccessInFrame;                // このフレームの成功数
    private int _statSuccessFrameNumber = -1;       // _statSuccessInFrame を数えているフレーム番号
    private float _statsNextLogTime;                // 次に集計ログを出す実時刻

    // ── レーン別通常出現タイマーの状態（ResetLaneSpawnTimers で初期化する） ──
    private float[] _laneNextSpawnTime;             // レーンごとの次回試行時刻（Time.unscaledTime 基準）
    private int _laneRoundRobinStart;               // 同時刻に複数レーンが来たときの処理開始レーン（固定優先を避ける）
    private bool _wasSpawnEnabled;                  // spawnEnabled の前フレーム値（停止明けの再抽選に使う）
    private int[] _statLaneAttempts;                // レーン別の配置試行数
    private int[] _statLaneFailures;                // レーン別の配置失敗数

    // 通常出現予定の基準時刻（＝本プレイ開始時刻。Start / RestartSchedule が呼ぶ
    // BuildNormalSpawnSchedule の中で更新する）。
    //  ・ScheduledNormalSpawn の spawnTime / visibleByTime / cycleStartTime は、
    //    すべてこの基準時刻を含む絶対時刻で保持する（相対時刻と絶対時刻を混在させない）。
    //  ・これによりタイトル待機・ロード・チュートリアルの長さに関係なく、
    //    ルールの0〜30秒などが「本プレイ開始からの経過秒」として解釈される。
    //  ・巨大障害物の予定（nextHugeSpawnTime）は元から Time.unscaledTime ＋待機秒数で
    //    管理しているため、この基準時刻は使わない（二重加算しない）。
    private float normalScheduleStartTime;
    private readonly List<ScheduledNormalSpawn> scheduledNormalSpawns = new List<ScheduledNormalSpawn>();
    private readonly List<RecentSpawnRecord> recentSpawnRecords = new List<RecentSpawnRecord>();

    // ★ 追加: 同一Update内で仮登録した配置済み位置（フレーム終わりにクリア）
    private readonly List<RecentSpawnRecord> pendingFrameRecords = new List<RecentSpawnRecord>();

    // ── アクティブアイテム一覧のフレーム内キャッシュ ────────────────────────────
    //  ・IsSpawnPositionOccupied は1回の配置試行で最大 (レーン数+1) 回呼ばれ、出現が集中する
    //    フレームでは全体探索が数百回に達するため、同じフレーム内は一覧を再利用する。
    //  ・取得元は従来と同じ FindObjectsByType<Item>（ItemPool 管理外のアイテムも対象に含める）。
    //  ・キャッシュはフレーム番号で無効化し、未構築／フレームが変わればその場で構築する
    //    （Update 以外の出現経路＝巨大障害物・デバッグキー・TrySpawnByPrefab・チュートリアルでも古い一覧を使わない）。
    //  ・同じフレームで新しく出現したアイテムは、配置成功時に pendingFrameRecords へ登録されるため
    //    後続の配置判定からも見える（重なり防止に穴を作らない）。
    //  ・同じフレームで返却・無効化されたアイテムは、判定ループ側の activeInHierarchy / null チェックで除外される。
    private Item[] _activeItemsForFrame;
    private int _activeItemsForFrameNumber = -1;

    /// <summary>
    /// 重なり判定に使う「現在アクティブな Item コンポーネント一覧」を、同一フレーム内で再利用して返す。
    /// 取得元・並び順・重複の扱いは従来の FindObjectsByType<Item>(FindObjectsSortMode.None) と同じ。
    /// </summary>
    private Item[] GetActiveItemsForFrame()
    {
        int frame = Time.frameCount;
        if (_activeItemsForFrame == null || _activeItemsForFrameNumber != frame)
        {
            using var collectScope = s_collectActiveItemsMarker.Auto();
            _activeItemsForFrame = FindObjectsByType<Item>(FindObjectsSortMode.None);
            _activeItemsForFrameNumber = frame;
        }

        return _activeItemsForFrame;
    }

    // デバッグ出現キーの診断ログ用（初回のみ／警告は1回のみ出力して毎フレームのログ氾濫を防ぐ）
    private bool _debugInitLogged;
    private bool _debugKeyWarningLogged;

    private const float SpawnZ = 609.47f;

    public bool SpawnEnabled
    {
        get => spawnEnabled;
        set => spawnEnabled = value;
    }

    void Start()
    {
        ValidateAndNormalizeSettings();
        // 通常出現はレーン別タイマー方式（旧・時刻付き予定方式 BuildNormalSpawnSchedule は使わない）
        ResetLaneSpawnTimers();

        nextHugeSpawnTime = Time.unscaledTime + hugeInitialDelay;
        isHugeSpawnScheduled = true;
    }

    private void OnValidate()
    {
        ValidateAndNormalizeSettings();
    }

    private void OnEnable()
    {
        QTEManager.HugeQteFinished += OnHugeQteFinished;
    }

    private void OnDisable()
    {
        QTEManager.HugeQteFinished -= OnHugeQteFinished;
    }

    private void OnHugeQteFinished(bool success)
    {
        if (success)
        {
            nextHugeSpawnTime = Time.unscaledTime + hugeCooldownAfterQte;
            isHugeSpawnScheduled = true;
        }
    }

    private void Update()
    {
        // ★ フレーム先頭で仮レコードをクリア
        pendingFrameRecords.Clear();

        // デバッグ出現キー（数字1〜6）。通常スポーンの有効/無効(spawnEnabled)とは独立に動作する。
        HandleDebugSpawnKeys();

        if (!spawnEnabled)
        {
            // 通常出現が止まっている間はタイマーを進めない（再開時にまとめて出さない）
            _wasSpawnEnabled = false;
            return;
        }

        // 停止 → 再開の切り替わりでは、停止していた分をまとめて出さないよう全レーンを再抽選する
        if (!_wasSpawnEnabled)
        {
            ResetLaneSpawnTimers();
            _wasSpawnEnabled = true;
        }

        // 配置パターン（短い並び・休憩区間）。出したらその間はランダム出現を止める（二重に出さない）
        UpdatePatternDirector();

        // 通常出現はレーン別タイマー方式のみ（旧・時刻付き予定方式は呼ばない＝二重実行しない）
        UpdateNormalSpawnByLaneTimers();

        // 調査用の集計ログ（enableSpawnStats が ON のときだけ、実時間の間隔で1行出力する）
        LogSpawnStatsIfNeeded(Time.unscaledTime);

        if (!isHugeSpawnScheduled)
            return;

        if (Time.unscaledTime >= nextHugeSpawnTime)
        {
            if (IsFeverActiveForSpawn())
            {
                // フィーバー中は通常のHugeObstacleを出現させない。
                // 予定は消費せず、次の再判定まで先延ばしする（毎フレーム再試行はしない）。
                // これによりフィーバー終了後に通常出現が再開できる（出現機会を恒久的に失わない）。
                float deferral = Mathf.Max(0.5f, hugeFeverDeferralSeconds);
                nextHugeSpawnTime = Time.unscaledTime + deferral;
                if (showDebugLogs)
                    Debug.Log($"[ItemSpawner] フィーバー中のため通常のHugeObstacle出現を {deferral:F1}s 先へ延期します（予定は保持）。");
            }
            else
            {
                SpawnHugeObstacle();
                isHugeSpawnScheduled = false;
            }
        }
    }

    public void RestartSchedule()
    {
        ValidateAndNormalizeSettings();
        // 本プレイ開始：レーン別タイマーを全レーン再抽選する（旧方式の予定は作らない）
        ResetLaneSpawnTimers();
        nextHugeSpawnTime = Time.unscaledTime + hugeInitialDelay;
        isHugeSpawnScheduled = true;
    }

    public bool TrySpawnByPrefab(GameObject prefab, out GameObject spawnedItem, bool preferHugeLane = false)
    {
        spawnedItem = null;

        if (ItemPool.Instance == null || prefab == null) return false;

        if (!spawnEnabled)
        {
            pendingFrameRecords.Clear();
        }

        // フィーバー中は HugeObstacle を出現させない（通常予定・6キー・他APIと同じ。抜け道を作らない）。
        Item prefabItem = prefab.GetComponent<Item>();
        if (prefabItem != null && prefabItem.itemType == ItemType.HugeObstacle && IsFeverActiveForSpawn())
        {
            if (showDebugLogs)
                Debug.Log($"[ItemSpawner] フィーバー中のため HugeObstacle を出現させません（TrySpawnByPrefab: {prefab.name}）。");
            return false;
        }

        GameObject item = ItemPool.Instance.GetFromPoolByPrefab(prefab);
        if (item == null) return false;

        if (!TryPlaceItemWithoutOverlap(item, preferHugeLane, float.PositiveInfinity, out _))
        {
            ItemPool.Instance.ReturnToPool(item);
            return false;
        }

        spawnedItem = item;
        return true;
    }

    public bool TrySpawnRandomNormal(out GameObject spawnedItem)
    {
        spawnedItem = null;
        List<GameObject> prefabs = GetNormalPrefabsFromItemPool();
        if (prefabs == null || prefabs.Count == 0) return false;

        GameObject prefab = prefabs[UnityEngine.Random.Range(0, prefabs.Count)];
        return TrySpawnByPrefab(prefab, out spawnedItem, false);
    }

    public bool TrySpawnHugeObstacle(out GameObject spawnedItem)
    {
        spawnedItem = null;

        if (ItemPool.Instance == null) return false;

        if (!spawnEnabled)
        {
            pendingFrameRecords.Clear();
        }

        // フィーバー中は HugeObstacle を出現させない（通常予定・6キーと同じ禁止条件。別APIからも抜け道を作らない）。
        if (IsFeverActiveForSpawn())
        {
            if (showDebugLogs)
                Debug.Log("[ItemSpawner] フィーバー中のため HugeObstacle を出現させません（TrySpawnHugeObstacle）。");
            return false;
        }

        GameObject item = ItemPool.Instance.GetFromPoolByItemType(ItemType.HugeObstacle);
        if (item == null) return false;

        Item itemComp = item.GetComponent<Item>();
        if (itemComp == null || itemComp.itemType != ItemType.HugeObstacle)
        {
            ItemPool.Instance.ReturnToPool(item);
            return false;
        }

        if (!TryPlaceItemWithoutOverlap(item, true, float.PositiveInfinity, out _))
        {
            ItemPool.Instance.ReturnToPool(item);
            return false;
        }

        spawnedItem = item;
        return true;
    }

    private void ValidateAndNormalizeSettings()
    {
        hugeObstacleSpawnCount = Mathf.Max(0, hugeObstacleSpawnCount);
        minSpawnDistanceX = Mathf.Max(0f, minSpawnDistanceX);
        minSpawnDistanceY = Mathf.Max(0f, minSpawnDistanceY);
        recentSpawnWindow = Mathf.Max(0f, recentSpawnWindow);
        overlapAvoidanceXStep = Mathf.Max(0f, overlapAvoidanceXStep);
        maxPlacementAttempts = Mathf.Max(1, maxPlacementAttempts);
        retryInterval = Mathf.Max(0f, retryInterval);
        maxRetryCount = Mathf.Max(1, maxRetryCount);

        // 追加した設定値も不正値（負・NaN）をそのまま使わない。
        normalSpawnSuccessInterval = SanitizeNonNegative(normalSpawnSuccessInterval);
        normalSpawnSuccessIntervalJitter = SanitizeNonNegative(normalSpawnSuccessIntervalJitter);
        if (normalSpawnSuccessIntervalJitter > 1f) normalSpawnSuccessIntervalJitter = 1f;
        if (float.IsNaN(normalSpawnGiveUpSeconds) || normalSpawnGiveUpSeconds <= 0f) normalSpawnGiveUpSeconds = 3f;
        spawnStatsLogIntervalSeconds = SanitizeNonNegative(spawnStatsLogIntervalSeconds);
        if (spawnStatsLogIntervalSeconds < 1f) spawnStatsLogIntervalSeconds = 1f;

        // 近接配置の余白：負・NaN は 0（＝接するまで詰める。無効化ではない）。
        goodItemCloseMarginX = SanitizeNonNegative(goodItemCloseMarginX);

        // レーン別タイマーの間隔：負・NaNは0、最大が最小より小さければ最小にそろえる。
        laneSpawnIntervalMinSeconds = SanitizeNonNegative(laneSpawnIntervalMinSeconds);
        laneSpawnIntervalMaxSeconds = SanitizeNonNegative(laneSpawnIntervalMaxSeconds);
        if (laneSpawnIntervalMaxSeconds < laneSpawnIntervalMinSeconds)
            laneSpawnIntervalMaxSeconds = laneSpawnIntervalMinSeconds;

        if (normalSpawnRules == null)
        {
            normalSpawnRules = new List<NormalSpawnRule>();
        }

        if (normalSpawnRules.Count == 0)
        {
            normalSpawnRules.Add(new NormalSpawnRule());
        }

        foreach (var rule in normalSpawnRules)
        {
            if (rule == null) continue;

            if (rule.interval == null)
            {
                rule.interval = new SpawnIntervalRange();
            }

            rule.interval.minInterval = Mathf.Max(0f, rule.interval.minInterval);
            rule.interval.maxInterval = Mathf.Max(rule.interval.minInterval, rule.interval.maxInterval);

            if (rule.prefabSpawnCounts == null)
            {
                rule.prefabSpawnCounts = new List<PrefabSpawnCount>();
            }

            List<GameObject> normalPrefabs = GetNormalPrefabsFromItemPool();
            if (normalPrefabs.Count > 0)
            {
                var normalized = new List<PrefabSpawnCount>(normalPrefabs.Count);
                foreach (var prefab in normalPrefabs)
                {
                    PrefabSpawnCount found = rule.prefabSpawnCounts.Find(x => x != null && x.prefab == prefab);
                    if (found == null)
                    {
                        normalized.Add(new PrefabSpawnCount
                        {
                            prefab = prefab,
                            count = 0
                        });
                    }
                    else
                    {
                        found.count = Mathf.Max(0, found.count);
                        normalized.Add(found);
                    }
                }

                rule.prefabSpawnCounts = normalized;
            }
            else
            {
                for (int i = rule.prefabSpawnCounts.Count - 1; i >= 0; i--)
                {
                    var entry = rule.prefabSpawnCounts[i];
                    if (entry == null || entry.prefab == null)
                    {
                        rule.prefabSpawnCounts.RemoveAt(i);
                        continue;
                    }

                    Item item = entry.prefab.GetComponent<Item>();
                    if (item != null && item.itemType == ItemType.HugeObstacle)
                    {
                        rule.prefabSpawnCounts.RemoveAt(i);
                        continue;
                    }

                    entry.count = Mathf.Max(0, entry.count);
                }
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  通常出現：レーン別タイマー方式（今回の唯一の通常出現起点）
    //  ・上/中央/下が独立に「最小〜最大」を抽選し、その時刻に自分のレーンへ1個だけ配置を試す。
    //  ・成功でも失敗でも、次回は「今回の現在時刻＋抽選値」で再抽選する（遅れの取り戻しをしない）。
    //  ・失敗予定の蓄積、別レーンへの振り替え、他レーンの成功によるタイマー初期化は行わない。
    //  ・1フレームにつき各レーン最大1回（0秒抽選でも次フレーム以降）。
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>レーン別タイマーの最小間隔（秒）。負・NaNは0。</summary>
    private float GetLaneSpawnIntervalMin()
    {
        return SanitizeNonNegative(laneSpawnIntervalMinSeconds);
    }

    /// <summary>レーン別タイマーの最大間隔（秒）。最小より小さい値・NaNは最小として扱う。</summary>
    private float GetLaneSpawnIntervalMax()
    {
        float min = GetLaneSpawnIntervalMin();
        float max = SanitizeNonNegative(laneSpawnIntervalMaxSeconds);
        return (max < min) ? min : max;
    }

    /// <summary>次の試行までの待ち時間を抽選する（最小〜最大）。</summary>
    private float DrawLaneSpawnInterval()
    {
        float min = GetLaneSpawnIntervalMin();
        float max = GetLaneSpawnIntervalMax();
        float interval = (max <= min) ? min : UnityEngine.Random.Range(min, max);

        // 出現量の倍率と休憩区間の薄さで割る（量が多いほど間隔が短くなる）
        return interval / GetSpawnDensityFactor(Time.unscaledTime);
    }

    /// <summary>
    /// レーン別タイマーを初期化する（Start / RestartSchedule / 停止明けに呼ぶ）。
    /// 各レーンの初回待ち時間は個別に抽選し、時間帯判定・集計の基準時刻もここで確定する。
    /// </summary>
    private void ResetLaneSpawnTimers()
    {
        normalScheduleStartTime = Time.unscaledTime;

        int laneCount = Mathf.Max(1, (lanesY != null) ? lanesY.Length : 0);
        if (_laneNextSpawnTime == null || _laneNextSpawnTime.Length != laneCount)
        {
            _laneNextSpawnTime = new float[laneCount];
        }

        // 休憩・パターンの状態を先に初期化する（前回の休憩の濃さを最初の抽選に持ち込まない）
        ResetPatternDirector();

        for (int i = 0; i < _laneNextSpawnTime.Length; i++)
        {
            // 初回もレーンごとに独立に抽選する
            _laneNextSpawnTime[i] = normalScheduleStartTime + DrawLaneSpawnInterval();
        }

        _laneRoundRobinStart = 0;

        RebuildSpawnStatsTables();
        ResetSpawnStats();
    }

    /// <summary>
    /// レーン別タイマーを進める。同時刻に複数レーンが来ても処理開始レーンを巡回させ、
    /// 「毎回 上→中央→下」の固定優先にしない（レーンごとのタイマーは独立のまま）。
    /// </summary>
    private void UpdateNormalSpawnByLaneTimers()
    {
        if (_laneNextSpawnTime == null || _laneNextSpawnTime.Length == 0) return;

        // 本プレイ終了後は通常出現しない（デバッグ出現と同じ判定を使う）
        GameManager gm = GameManager.instance;
        if (gm != null && gm.IsGameOver) return;

        float now = Time.unscaledTime;

        // 配置パターンが出現位置を抜けきるまでは、ランダム出現を止める（パターンと重ねて出さない）
        if (IsRandomSpawnBlockedByPattern(now)) return;

        int laneCount = _laneNextSpawnTime.Length;
        int start = Mathf.Abs(_laneRoundRobinStart) % laneCount;

        for (int k = 0; k < laneCount; k++)
        {
            int lane = (start + k) % laneCount;

            if (now < _laneNextSpawnTime[lane]) continue;

            // このレーンに1個だけ試す（失敗しても別レーンへ移さない・蓄積しない）
            TrySpawnNormalItemInLane(lane);

            // 成功でも失敗でも「今回の現在時刻＋抽選値」で次回を決める。
            // 0秒を引いても、このレーンの次回試行は次フレーム以降になる（whileで取り戻さない）。
            _laneNextSpawnTime[lane] = now + DrawLaneSpawnInterval();
        }

        _laneRoundRobinStart = (start + 1) % laneCount;
    }

    /// <summary>
    /// 指定レーンに通常アイテムを1個配置する。種類は「現在有効な時間帯の PrefabSpawnCount」を
    /// 相対重みとして抽選する（個数のノルマではない）。有効な候補が無ければ見送る。
    /// </summary>
    private bool TrySpawnNormalItemInLane(int laneIndex)
    {
        if (ItemPool.Instance == null) return false;
        if (lanesY == null || laneIndex < 0 || laneIndex >= lanesY.Length) return false;

        if (_statLaneAttempts != null && laneIndex < _statLaneAttempts.Length) _statLaneAttempts[laneIndex]++;

        GameObject prefab = SelectNormalPrefabByWeight();
        if (prefab == null) return false;   // 有効な通常候補なし → 見送り

        GameObject item = ItemPool.Instance.GetFromPoolByPrefab(prefab);
        if (item == null) return false;

        Item itemComp = item.GetComponent<Item>();
        if (itemComp == null)
        {
            ItemPool.Instance.ReturnToPool(item);
            return false;
        }

        // Loud(BGM) の残り時間条件など、既存の出現禁止条件はそのまま守る（予定は消費＝見送り）
        if (itemComp.itemType == ItemType.BGM && !IsBgmNormalSpawnAllowed())
        {
            ItemPool.Instance.ReturnToPool(item);
            _statBgmSkipped++;
            if (showDebugLogs)
                Debug.Log($"[ItemSpawner] BGMは本編開始から{bgmNormalSpawnElapsedSeconds:F0}秒経過後にのみ通常出現します — 今回の予定は見送ります。");
            return false;
        }

        if (!TryPlaceItemInSpecifiedLane(item, laneIndex, out int placedLaneIndex))
        {
            ItemPool.Instance.ReturnToPool(item);
            if (_statLaneFailures != null && laneIndex < _statLaneFailures.Length) _statLaneFailures[laneIndex]++;
            return false;
        }

        OnLaneNormalSpawnPlaced(prefab, placedLaneIndex);
        return true;
    }

    /// <summary>
    /// 現在の経過時間で有効な通常出現ルールから、PrefabSpawnCount を相対重みとして1つ抽選する。
    /// ・count はノルマではなく重み（良い／悪いの重みを勝手に変えない）。
    /// ・HugeObstacle／QTE、Itemが無いもの、count 0 は候補にしない。
    /// ・毎回の配列・リスト生成はしない（重み合計と抽選の2回走査のみ）。
    /// </summary>
    private GameObject SelectNormalPrefabByWeight()
    {
        if (normalSpawnRules == null || normalSpawnRules.Count == 0) return null;

        float elapsed = Time.unscaledTime - normalScheduleStartTime;

        float total = 0f;
        for (int r = 0; r < normalSpawnRules.Count; r++)
        {
            NormalSpawnRule rule = normalSpawnRules[r];
            if (!IsNormalRuleActiveAt(rule, elapsed)) continue;

            List<PrefabSpawnCount> entries = rule.prefabSpawnCounts;
            if (entries == null) continue;

            for (int e = 0; e < entries.Count; e++)
            {
                if (!IsValidNormalCandidate(entries[e])) continue;
                total += GetNormalCandidateWeight(entries[e]);
            }
        }

        if (total <= 0f) return null;

        float pick = UnityEngine.Random.value * total;
        for (int r = 0; r < normalSpawnRules.Count; r++)
        {
            NormalSpawnRule rule = normalSpawnRules[r];
            if (!IsNormalRuleActiveAt(rule, elapsed)) continue;

            List<PrefabSpawnCount> entries = rule.prefabSpawnCounts;
            if (entries == null) continue;

            for (int e = 0; e < entries.Count; e++)
            {
                PrefabSpawnCount entry = entries[e];
                if (!IsValidNormalCandidate(entry)) continue;

                pick -= GetNormalCandidateWeight(entry);
                if (pick <= 0f) return entry.prefab;
            }
        }

        return null;
    }

    /// <summary>そのルールが現在の経過時間で有効か（非ループ＝時間帯、ループ＝開始以降ずっと有効）。</summary>
    private static bool IsNormalRuleActiveAt(NormalSpawnRule rule, float elapsedSeconds)
    {
        if (rule == null || rule.interval == null) return false;

        float min = Mathf.Max(0f, rule.interval.minInterval);
        float max = Mathf.Max(min, rule.interval.maxInterval);

        if (rule.loopEnabled) return elapsedSeconds >= min;

        return elapsedSeconds >= min && elapsedSeconds < max;
    }

    /// <summary>通常出現の候補として有効か（count>0・Prefabあり・Itemあり・HugeObstacle以外）。</summary>
    private static bool IsValidNormalCandidate(PrefabSpawnCount entry)
    {
        if (entry == null || entry.prefab == null) return false;
        if (entry.count <= 0) return false;

        Item item = entry.prefab.GetComponent<Item>();
        if (item == null) return false;
        if (item.itemType == ItemType.HugeObstacle) return false;   // QTE／巨大障害物は通常候補に含めない

        return true;
    }

    /// <summary>
    /// 指定レーンへ配置する（通常出現用）。レーンのシャッフル・別レーンへのフォールバックはしない。
    /// Xは画面右側の基準出現位置のみを使う（待ち時間をごまかすために遠いXへ積まない）。
    /// 重なり防止は既存の占有判定（3経路）をそのまま使う。
    /// </summary>
    private bool TryPlaceItemInSpecifiedLane(GameObject item, int laneIndex, out int placedLaneIndex)
    {
        placedLaneIndex = -1;

        if (item == null) return false;
        if (lanesY == null || laneIndex < 0 || laneIndex >= lanesY.Length) return false;

        using var placeScope = s_tryPlaceMarker.Auto();

        float spawnX = GetSpawnXWorld();
        float now = Time.unscaledTime;
        Vector3 candidate = new Vector3(spawnX, lanesY[laneIndex], SpawnZ);

        if (IsSpawnPositionOccupied(candidate, now, item)) return false;

        item.transform.position = candidate;
        ApplySpawnSpeedScale(item);
        RegisterRecentSpawn(item, candidate, now);
        // 同じフレームの他レーンからも見えるよう仮レコードにも登録する
        RegisterPendingFrameRecord(item, candidate, now);

        placedLaneIndex = laneIndex;
        return true;
    }

    /// <summary>レーン別タイマー方式の配置成功を集計する（成功数・レーン別・種類別・同一フレーム同時成功）。</summary>
    private void OnLaneNormalSpawnPlaced(GameObject prefab, int laneIndex)
    {
        _statNormalSuccessTotal++;

        if (_statLaneSuccess != null && laneIndex >= 0 && laneIndex < _statLaneSuccess.Length)
        {
            _statLaneSuccess[laneIndex]++;
        }

        if (prefab != null && _statPrefabs != null && _statPrefabSuccess != null)
        {
            for (int i = 0; i < _statPrefabs.Length; i++)
            {
                if (_statPrefabs[i] == prefab)
                {
                    _statPrefabSuccess[i]++;
                    break;
                }
            }
        }

        // 同一フレームに2件以上成功した「回数」（フレーム数であって個数ではない）
        if (_statSuccessFrameNumber != Time.frameCount)
        {
            _statSuccessFrameNumber = Time.frameCount;
            _statSuccessInFrame = 0;
        }
        _statSuccessInFrame++;
        if (_statSuccessInFrame == 2) _statSameFrameMultiSuccessFrames++;
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  旧・時刻付き予定方式（現在は未使用。通常出現はレーン別タイマー方式が唯一の起点）
    //  ・BuildNormalSpawnSchedule / ScheduleNormalSpawnsForRule / HandleScheduledNormalSpawns /
    //    RescheduleToNextCycle / CompareScheduledNormalSpawns / SpawnItemByPrefab は
    //    Start・RestartSchedule・Update から呼ばれていない（＝旧方式と二重実行しない）。
    //  ・Inspectorの設定値（成功間隔・ゆらぎ・猶予・ルール表）は消さずに残している。
    //    normalSpawnRules はレーン別タイマーの「種類の重み」として引き続き使う。
    // ──────────────────────────────────────────────────────────────────────────

    private void HandleScheduledNormalSpawns()
    {
        if (scheduledNormalSpawns.Count == 0) return;

        // 処理対象があるときだけ計測する（毎フレームの空呼び出しをマーカーに出さない）
        using var handleScope = s_handleScheduledMarker.Auto();

        // 予定時刻（spawnTime）・元の予定時刻（plannedSpawnTime）は基準時刻を含む絶対時刻なので、
        // 現在時刻（Time.unscaledTime）とそのまま比較できる。QTE中も時計は止めない（既存どおり）。
        float now = Time.unscaledTime;

        // 成功間隔の休止中か。休止中は「配置試行」を休むだけで、失敗としては数えない
        // （retryCount も spawnTime も変更しない）。
        bool canAttempt = now >= nextNormalSpawnAllowedTime;

        // ★ 予定は「元の予定時刻 → 同着は構築時に固定した乱数」で並んでいるため、
        //    先頭から順に処理すれば登録順（ルール順・Prefab順）による種類の偏りが出ない。
        for (int i = 0; i < scheduledNormalSpawns.Count; i++)
        {
            var scheduled = scheduledNormalSpawns[i];
            if (scheduled == null)
            {
                scheduledNormalSpawns.RemoveAt(i);
                i--;
                continue;
            }

            // ① 待機期限（元の予定時刻＋猶予）を過ぎた予定は、休止中でもここで破棄する
            //    （休止を理由に無期限へ持ち越さない）。
            if (now > scheduled.plannedSpawnTime + GetNormalSpawnGiveUpSeconds())
            {
                _statDiscardByDeadline++;
                scheduledNormalSpawns.RemoveAt(i);
                i--;
                continue;
            }

            // ② まだ処理時刻に達していない（リトライ待ちも含む）
            if (now < scheduled.spawnTime) continue;

            // ③ 休止中は配置を試みない（この休止は失敗ではないので retryCount を増やさない）
            if (!canAttempt) continue;

            NormalPlacementResult result = SpawnItemByPrefab(
                scheduled.prefab, scheduled.visibleByTime, out int placedLaneIndex, out _);

            switch (result)
            {
                case NormalPlacementResult.Placed:
                    // 実際に配置できたときだけ成功間隔を消費し、集計する
                    OnNormalSpawnPlaced(scheduled, placedLaneIndex, now);
                    canAttempt = now >= nextNormalSpawnAllowedTime;

                    if (scheduled.loopEnabled && scheduled.loopCycleDuration > 0f)
                    {
                        RescheduleToNextCycle(scheduled);
                    }
                    else
                    {
                        scheduledNormalSpawns.RemoveAt(i);
                        i--;
                    }
                    break;

                case NormalPlacementResult.SkippedByCondition:
                    // Loud Item の残り時間条件による見送り。予定は消費するが「配置成功」ではない
                    // （成功間隔は消費しない）。
                    _statBgmSkipped++;
                    if (scheduled.loopEnabled && scheduled.loopCycleDuration > 0f)
                    {
                        RescheduleToNextCycle(scheduled);
                    }
                    else
                    {
                        scheduledNormalSpawns.RemoveAt(i);
                        i--;
                    }
                    break;

                default: // NormalPlacementResult.Failed
                    // ★ 配置失敗 → リトライ上限未満なら再スケジュール、超えたら諦めて削除
                    _statRetryCount++;
                    scheduled.retryCount++;
                    if (scheduled.retryCount >= maxRetryCount)
                    {
                        _statDiscardByRetryLimit++;
                        if (scheduled.loopEnabled && scheduled.loopCycleDuration > 0f)
                        {
                            // ループ中でも上限リトライ失敗時は次サイクルへ持ち越す
                            RescheduleToNextCycle(scheduled);

                            if (showDebugLogs)
                            {
                                Debug.Log($"[ItemSpawner] '{scheduled.prefab?.name}' を {maxRetryCount} 回試みたが配置できなかったため次サイクルへ持ち越します。");
                            }
                        }
                        else
                        {
                            if (showDebugLogs)
                            {
                                Debug.Log($"[ItemSpawner] '{scheduled.prefab?.name}' を {maxRetryCount} 回試みたが配置できなかったため破棄します。");
                            }
                            scheduledNormalSpawns.RemoveAt(i);
                            i--;
                        }
                    }
                    else
                    {
                        // 少し後に再試行（now も予定時刻も同じ絶対時刻系なので、そのまま加算できる）
                        scheduled.spawnTime = now + retryInterval;
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// ループ予定を次サイクルへ再予約する。
    /// 元の予定時刻（plannedSpawnTime）・時間帯・同着順も更新する（待機期限と処理順に使うため）。
    /// </summary>
    private void RescheduleToNextCycle(ScheduledNormalSpawn scheduled)
    {
        float nextCycleStart = scheduled.cycleStartTime + scheduled.loopCycleDuration;
        float leadTime = GetLeadTimeToScreenEnter(scheduled.prefab);
        float nextVisible = nextCycleStart + scheduled.localVisibleOffset;

        // 再予約も同じ絶対時刻系で行う（基準時刻より前には生成しない）
        float planned = Mathf.Max(normalScheduleStartTime, nextVisible - leadTime);

        scheduled.spawnTime        = planned;
        scheduled.plannedSpawnTime = planned;
        scheduled.visibleByTime    = nextVisible;
        scheduled.cycleStartTime   = nextCycleStart;
        scheduled.retryCount       = 0;
        scheduled.tieBreak         = UnityEngine.Random.value;
        scheduled.bandEndSeconds   = nextCycleStart - normalScheduleStartTime + scheduled.loopCycleDuration;
    }

    /// <summary>通常出現の配置結果。bool の兼用（配置成功と予定の正常消費）を分けるための最小構成。</summary>
    private enum NormalPlacementResult
    {
        Placed,             // 実際に配置できた（＝成功間隔を消費し、集計に数える）
        SkippedByCondition, // 条件による見送り（Loud Item の残り時間ゲート）。予定は消費するが成功ではない
        Failed              // 配置に失敗した（リトライ対象）
    }

    /// <summary>
    /// 予定の処理順。元の予定時刻が早い順、同着は構築時に固定した乱数（tieBreak）で決める。
    /// リストを並べ替えるのは BuildNormalSpawnSchedule の1回だけで、毎フレームはソートしない。
    /// </summary>
    private static int CompareScheduledNormalSpawns(ScheduledNormalSpawn a, ScheduledNormalSpawn b)
    {
        if (a == null) return (b == null) ? 0 : 1;
        if (b == null) return -1;

        int byTime = a.plannedSpawnTime.CompareTo(b.plannedSpawnTime);
        if (byTime != 0) return byTime;

        return a.tieBreak.CompareTo(b.tieBreak);
    }

    /// <summary>負・NaN を 0 に丸める（不正な設定値をそのまま使わない）。</summary>
    private static float SanitizeNonNegative(float value)
    {
        if (float.IsNaN(value) || value < 0f) return 0f;
        return value;
    }

    /// <summary>通常出現の予定をあきらめる猶予（秒）。0以下・不正値は既定の3秒として扱う。</summary>
    private float GetNormalSpawnGiveUpSeconds()
    {
        if (float.IsNaN(normalSpawnGiveUpSeconds) || normalSpawnGiveUpSeconds <= 0f) return 3f;
        return normalSpawnGiveUpSeconds;
    }

    /// <summary>
    /// 成功後の休止時間＝基準時間 × Random.Range(1-jitter, 1+jitter)。
    /// 基準時間が0のときは 0（休止なし＝追加制御を無効化）。
    /// </summary>
    private float GetNormalSpawnInterval()
    {
        float baseInterval = SanitizeNonNegative(normalSpawnSuccessInterval);
        if (baseInterval <= 0f) return 0f;

        float jitter = SanitizeNonNegative(normalSpawnSuccessIntervalJitter);
        if (jitter > 1f) jitter = 1f;
        if (jitter <= 0f) return baseInterval;

        return baseInterval * UnityEngine.Random.Range(1f - jitter, 1f + jitter);
    }

    /// <summary>
    /// 通常アイテムの配置に成功したときの処理（成功間隔の更新と集計）。
    /// 「実際に配置できた」ときだけ呼ぶ（Loud の見送り・配置失敗・破棄では呼ばない）。
    /// </summary>
    private void OnNormalSpawnPlaced(ScheduledNormalSpawn scheduled, int laneIndex, float now)
    {
        // 次の成功可能時刻（休止時間）。基準時間が0なら now のまま＝休止なし。
        nextNormalSpawnAllowedTime = now + GetNormalSpawnInterval();

        // ── 集計（カウントは常時。ログ出力だけを enableSpawnStats で制御する）──
        _statNormalSuccessTotal++;

        if (_statLaneSuccess != null && laneIndex >= 0 && laneIndex < _statLaneSuccess.Length)
        {
            _statLaneSuccess[laneIndex]++;
        }

        if (scheduled != null && _statPrefabs != null && _statPrefabSuccess != null)
        {
            for (int i = 0; i < _statPrefabs.Length; i++)
            {
                if (_statPrefabs[i] == scheduled.prefab)
                {
                    _statPrefabSuccess[i]++;
                    break;
                }
            }
        }

        // 時間帯をまたいだ成功（元の予定の時間帯の終端を過ぎてから成功した。ループ予定は対象外）
        if (scheduled != null && !scheduled.loopEnabled &&
            now - normalScheduleStartTime >= scheduled.bandEndSeconds)
        {
            _statBandCrossingSuccess++;
        }

        // 同一フレームに2件以上成功した「回数」（フレーム数であって個数ではない）
        if (_statSuccessFrameNumber != Time.frameCount)
        {
            _statSuccessFrameNumber = Time.frameCount;
            _statSuccessInFrame = 0;
        }
        _statSuccessInFrame++;
        if (_statSuccessInFrame == 2) _statSameFrameMultiSuccessFrames++;
    }

    /// <summary>Y座標から最も近いレーンの番号を返す（集計用。lanesY が無い場合は -1）。</summary>
    private int GetLaneIndexOfY(float y)
    {
        if (lanesY == null || lanesY.Length == 0) return -1;

        int best = 0;
        float bestDist = Mathf.Abs(lanesY[0] - y);
        for (int i = 1; i < lanesY.Length; i++)
        {
            float d = Mathf.Abs(lanesY[i] - y);
            if (d < bestDist)
            {
                bestDist = d;
                best = i;
            }
        }
        return best;
    }

    /// <summary>
    /// 集計対象のPrefab一覧とカウンタ配列を、通常出現ルールの並びから作り直す。
    /// （巨大障害物は通常出現の集計対象外）
    /// </summary>
    private void RebuildSpawnStatsTables()
    {
        var prefabs = new List<GameObject>();
        if (normalSpawnRules != null)
        {
            foreach (var rule in normalSpawnRules)
            {
                if (rule == null || rule.prefabSpawnCounts == null) continue;
                foreach (var entry in rule.prefabSpawnCounts)
                {
                    if (entry == null || entry.prefab == null) continue;

                    Item item = entry.prefab.GetComponent<Item>();
                    if (item != null && item.itemType == ItemType.HugeObstacle) continue;
                    if (!prefabs.Contains(entry.prefab)) prefabs.Add(entry.prefab);
                }
            }
        }

        _statPrefabs = prefabs.ToArray();
        _statPrefabSuccess = new int[_statPrefabs.Length];

        int laneCount = Mathf.Max(1, (lanesY != null) ? lanesY.Length : 0);
        _statLaneSuccess = new int[laneCount];
        _statLaneAttempts = new int[laneCount];
        _statLaneFailures = new int[laneCount];
    }

    /// <summary>成功間隔と集計を初期化する（Start / RestartSchedule から呼ばれる）。</summary>
    private void ResetSpawnStats()
    {
        _statNormalSuccessTotal = 0;
        _statSameFrameMultiSuccessFrames = 0;
        _statRetryCount = 0;
        _statDiscardByRetryLimit = 0;
        _statDiscardByDeadline = 0;
        _statBgmSkipped = 0;
        _statBandCrossingSuccess = 0;
        _statSuccessInFrame = 0;
        _statSuccessFrameNumber = -1;

        if (_statLaneSuccess != null) System.Array.Clear(_statLaneSuccess, 0, _statLaneSuccess.Length);
        if (_statLaneAttempts != null) System.Array.Clear(_statLaneAttempts, 0, _statLaneAttempts.Length);
        if (_statLaneFailures != null) System.Array.Clear(_statLaneFailures, 0, _statLaneFailures.Length);
        if (_statPrefabSuccess != null) System.Array.Clear(_statPrefabSuccess, 0, _statPrefabSuccess.Length);

        nextNormalSpawnAllowedTime = 0f;
        _statsNextLogTime = Time.unscaledTime + Mathf.Max(1f, spawnStatsLogIntervalSeconds);
    }

    /// <summary>
    /// 調査用の集計ログ。enableSpawnStats が ON のときだけ、実時間の間隔で1行出力する。
    /// ここで出す数値は「配置成功数」であり、画面到達数・取得数ではない。
    /// </summary>
    private void LogSpawnStatsIfNeeded(float now)
    {
        if (!enableSpawnStats) return;
        if (now < _statsNextLogTime) return;

        _statsNextLogTime = now + Mathf.Max(1f, spawnStatsLogIntervalSeconds);

        var sb = new System.Text.StringBuilder(384);
        sb.Append("[ItemSpawner] 通常出現（レーン別タイマー）の集計：配置の試行/成功/失敗（画面到達数・取得数ではない）");
        sb.Append(" | 経過=").Append((now - normalScheduleStartTime).ToString("F1")).Append("s");
        sb.Append(" | 成功計=").Append(_statNormalSuccessTotal);

        // レーン別：試行数 / 成功数 / 配置失敗数（中央の偏りはこの3つを分けて見る）
        sb.Append(" | レーン別(試行/成功/失敗)=");
        if (_laneNextSpawnTime != null)
        {
            for (int i = 0; i < _laneNextSpawnTime.Length; i++)
            {
                int att = (_statLaneAttempts != null && i < _statLaneAttempts.Length) ? _statLaneAttempts[i] : 0;
                int suc = (_statLaneSuccess != null && i < _statLaneSuccess.Length) ? _statLaneSuccess[i] : 0;
                int fail = (_statLaneFailures != null && i < _statLaneFailures.Length) ? _statLaneFailures[i] : 0;

                sb.Append("lane").Append(i).Append(':').Append(att).Append('/').Append(suc).Append('/').Append(fail).Append(' ');
            }
        }

        sb.Append("| 種類別=");
        if (_statPrefabs != null && _statPrefabSuccess != null)
        {
            for (int i = 0; i < _statPrefabs.Length; i++)
            {
                sb.Append(_statPrefabs[i] != null ? _statPrefabs[i].name : "null").Append(':').Append(_statPrefabSuccess[i]).Append(' ');
            }
        }

        // 旧・時刻付き予定方式にしか意味のない集計（再試行・破棄・時間帯またぎ）は、新方式で有効であるかのように出さない。
        sb.Append("| 同一フレーム2件以上の回数=").Append(_statSameFrameMultiSuccessFrames);
        sb.Append(" | Loud見送り=").Append(_statBgmSkipped);
        sb.Append(" | レーン間隔設定=").Append(GetLaneSpawnIntervalMin().ToString("F2"))
          .Append("〜").Append(GetLaneSpawnIntervalMax().ToString("F2")).Append("s");

        Debug.Log(sb.ToString());
    }

    private void BuildNormalSpawnSchedule()
    {
        using var buildScope = s_buildScheduleMarker.Auto();

        scheduledNormalSpawns.Clear();

        // この回の基準時刻（＝本プレイ開始時刻）をここで確定する。
        // Start（シーン読み込み時）と RestartSchedule（本プレイ開始時）の両方がここを通るため、
        // 後から呼ばれた方が基準時刻として有効になる（本プレイ開始時の RestartSchedule が最後に来る）。
        normalScheduleStartTime = Time.unscaledTime;

        // 追加状態（成功間隔・集計）もこの基準時刻で初期化する。
        // Start と RestartSchedule の両方がここを通るため、再プレイへ状態を持ち越さない。
        RebuildSpawnStatsTables();
        ResetSpawnStats();

        if (normalSpawnRules == null || normalSpawnRules.Count == 0)
        {
            return;
        }

        for (int i = 0; i < normalSpawnRules.Count; i++)
        {
            var rule = normalSpawnRules[i];
            if (rule == null || rule.interval == null) continue;
            ScheduleNormalSpawnsForRule(rule);
        }

        // 処理順を「元の予定時刻 → 同着は構築時に固定した乱数」で決める。
        //  ・登録順（ルール順・Prefab順）で特定の種類だけが枠を取り続けないようにする。
        //  ・並べ替えは構築時に1回だけ行い、毎フレームのソートや一時リスト生成は行わない。
        scheduledNormalSpawns.Sort(CompareScheduledNormalSpawns);
    }

    private void ScheduleNormalSpawnsForRule(NormalSpawnRule rule)
    {
        if (rule == null || rule.interval == null) return;

        float startTime = Mathf.Max(0f, rule.interval.minInterval);
        float endTime = Mathf.Max(startTime, rule.interval.maxInterval);

        // ループ周期を決定。0 以下の場合はルール幅を使う
        float cycleDuration = (rule.loopEnabled && rule.loopCycleDuration > 0f)
            ? rule.loopCycleDuration
            : Mathf.Max(1f, endTime - startTime);

        if (rule.prefabSpawnCounts == null) return;

        foreach (var entry in rule.prefabSpawnCounts)
        {
            if (entry == null) continue;
            if (entry.prefab == null) continue;

            Item item = entry.prefab.GetComponent<Item>();
            if (item != null && item.itemType == ItemType.HugeObstacle) continue;

            int count = Mathf.Max(0, entry.count);
            float leadTime = GetLeadTimeToScreenEnter(entry.prefab);
            for (int i = 0; i < count; i++)
            {
                // サイクル内でのランダムオフセット（0〜cycleDuration の範囲）
                float localOffset = UnityEngine.Random.Range(0f, cycleDuration);

                // ルール内の相対秒（本プレイ開始＝0秒）に基準時刻を加え、絶対時刻として保持する。
                // 先行時間（leadTime＝画面外から画面に入るまでの移動時間）は従来どおり差し引く。
                // 予定が本プレイ開始時刻より前になる場合は基準時刻にクランプし、開始前には生成しない
                // （その分だけ最初のアイテムが画面に入るのは遅れる。移動時間は残る）。
                float visibleTime = normalScheduleStartTime + startTime + localOffset;
                float plannedSpawnTime = Mathf.Max(normalScheduleStartTime, visibleTime - leadTime);

                scheduledNormalSpawns.Add(new ScheduledNormalSpawn
                {
                    prefab           = entry.prefab,
                    spawnTime        = plannedSpawnTime,
                    visibleByTime    = visibleTime,
                    retryCount       = 0,
                    plannedSpawnTime = plannedSpawnTime,            // 元の予定時刻（再試行で更新しない）
                    tieBreak         = UnityEngine.Random.value,    // 同着の処理順を構築時に固定する
                    bandEndSeconds   = startTime + cycleDuration,   // この予定が属する時間帯の終了（相対秒）
                    loopEnabled      = rule.loopEnabled,
                    loopCycleDuration = rule.loopEnabled ? cycleDuration : 0f,
                    cycleStartTime   = normalScheduleStartTime + startTime,   // 第1サイクルの基準時刻（絶対）
                    localVisibleOffset = localOffset,
                });
            }
        }
    }

    private float GetLeadTimeToScreenEnter(GameObject prefab)
    {
        if (prefab == null) return 0f;

        float distance = Mathf.Max(0f, spawnOffsetFromRight);
        if (distance <= 0f) return 0f;

        ItemMove move = prefab.GetComponent<ItemMove>();
        if (move == null) return 0f;

        // 得点段階倍率を反映した実効速度で先行時間を求める（段階が上がるほど画面へ入るのが早くなる）。
        float effectiveSpeed = move.EffectiveSpeed;
        if (effectiveSpeed <= 0f) return 0f;

        return distance / effectiveSpeed;
    }

    private List<GameObject> GetNormalPrefabsFromItemPool()
    {
        var result = new List<GameObject>();
        ItemPool pool = FindFirstObjectByType<ItemPool>();
        if (pool == null || pool.itemPrefabs == null) return result;

        foreach (var prefab in pool.itemPrefabs)
        {
            if (prefab == null) continue;
            if (result.Contains(prefab)) continue;

            Item item = prefab.GetComponent<Item>();
            if (item != null && item.itemType == ItemType.HugeObstacle) continue;

            result.Add(prefab);
        }

        return result;
    }

    // 戻り値: 実際に配置できた（Placed）／条件で見送った（SkippedByCondition）／配置に失敗した（Failed）。
    // ・以前は bool で「配置成功」と「予定の正常消費（Loud の見送り）」を兼用していた。
    //   成功間隔を消費してよいのは Placed のときだけであることを区別できるようにした。
    private NormalPlacementResult SpawnItemByPrefab(
        GameObject prefab, float visibleByTime, out int placedLaneIndex, out GameObject placedItem)
    {
        placedLaneIndex = -1;
        placedItem = null;

        if (ItemPool.Instance == null) return NormalPlacementResult.Failed;
        if (prefab == null) return NormalPlacementResult.Failed;

        GameObject item = ItemPool.Instance.GetFromPoolByPrefab(prefab);
        if (item == null) return NormalPlacementResult.Failed;

        Item itemComp = item.GetComponent<Item>();
        if (itemComp == null)
        {
            ItemPool.Instance.ReturnToPool(item);
            return NormalPlacementResult.Failed;
        }

        // BGM は「本編開始からの経過時間がしきい値(既定120秒)以上」のときだけ通常出現を許可する。
        // 解禁前の予定はここで消費する（保留・毎フレーム再試行・解禁時のまとめ出現はしない）。
        // これは配置成功ではないため、成功間隔は消費しない（SkippedByCondition で返す）。
        if (itemComp.itemType == ItemType.BGM && !IsBgmNormalSpawnAllowed())
        {
            ItemPool.Instance.ReturnToPool(item);
            if (showDebugLogs)
                Debug.Log($"[ItemSpawner] BGMは本編開始から{bgmNormalSpawnElapsedSeconds:F0}秒経過後にのみ通常出現します — 今回の予定は見送ります。");
            return NormalPlacementResult.SkippedByCondition;
        }

        float maxExtraX = GetAllowedExtraXForVisibleDeadline(prefab, visibleByTime, Time.unscaledTime);
        if (TryPlaceItemWithoutOverlap(item, false, maxExtraX, out placedLaneIndex))
        {
            placedItem = item;
            return NormalPlacementResult.Placed;
        }

        // ★ 配置失敗時はプールに戻してリトライを促す
        ItemPool.Instance.ReturnToPool(item);
        return NormalPlacementResult.Failed;
    }

    private bool TryPlaceItemWithoutOverlap(
        GameObject item, bool preferHugeLane, float maxExtraX, out int placedLaneIndex)
    {
        placedLaneIndex = -1;

        if (item == null) return false;

        using var placeScope = s_tryPlaceMarker.Auto();

        Item itemComp = item.GetComponent<Item>();
        bool forceHugeObstacleY = itemComp != null && itemComp.itemType == ItemType.HugeObstacle;

        float baseSpawnX = GetSpawnXWorld();
        float now = Time.unscaledTime;
        float clampedMaxExtraX = float.IsPositiveInfinity(maxExtraX) ? float.PositiveInfinity : Mathf.Max(0f, maxExtraX);

        for (int attempt = 0; attempt < maxPlacementAttempts; attempt++)
        {
            float extraX = overlapAvoidanceXStep * attempt;
            if (!float.IsPositiveInfinity(clampedMaxExtraX))
            {
                extraX = Mathf.Min(extraX, clampedMaxExtraX);
            }

            float spawnX = baseSpawnX + extraX;
            float y = forceHugeObstacleY ? hugeObstacleSpawnY : SelectLaneYAvoidingOverlap(item, spawnX, preferHugeLane, now);
            Vector3 candidate = new Vector3(spawnX, y, SpawnZ);

            if (!IsSpawnPositionOccupied(candidate, now, item))
            {
                item.transform.position = candidate;
                RegisterRecentSpawn(item, candidate, now);
                // ★ 同フレーム内の他アイテムからも見えるよう仮レコードにも登録
                RegisterPendingFrameRecord(item, candidate, now);
                placedLaneIndex = GetLaneIndexOfY(y);   // 集計用（レーン別の配置成功数）
                return true;
            }
        }

        return false;
    }

    private float GetAllowedExtraXForVisibleDeadline(GameObject prefab, float visibleByTime, float now)
    {
        if (prefab == null) return 0f;

        ItemMove move = prefab.GetComponent<ItemMove>();
        if (move == null)
        {
            return float.PositiveInfinity;
        }

        // 配置時に許可する追加X距離も、得点段階倍率を反映した実効速度で求める
        // （実効速度が上がるほど「画面に入るまでの残り時間」で進める距離が伸びる）。
        float effectiveSpeed = move.EffectiveSpeed;
        if (effectiveSpeed <= 0f)
        {
            return float.PositiveInfinity;
        }

        float remainingTime = Mathf.Max(0f, visibleByTime - now);
        float rightEdgeX = GetScreenRightEdgeX();
        float maxSpawnX = rightEdgeX + (effectiveSpeed * remainingTime);
        float baseSpawnX = GetSpawnXWorld();
        return Mathf.Max(0f, maxSpawnX - baseSpawnX);
    }

    private float SelectLaneYAvoidingOverlap(GameObject spawningItem, float spawnX, bool preferHugeLane, float now)
    {
        if (lanesY == null || lanesY.Length == 0) return 0f;

        List<int> laneIndices = new List<int>(lanesY.Length);
        if (preferHugeLane && lanesY.Length > 1)
        {
            laneIndices.Add(1);
            for (int i = 0; i < lanesY.Length; i++)
            {
                if (i != 1) laneIndices.Add(i);
            }
        }
        else
        {
            for (int i = 0; i < lanesY.Length; i++) laneIndices.Add(i);
            ShuffleLaneIndices(laneIndices);
        }

        foreach (int index in laneIndices)
        {
            Vector3 candidate = new Vector3(spawnX, lanesY[index], SpawnZ);
            if (!IsSpawnPositionOccupied(candidate, now, spawningItem))
            {
                return lanesY[index];
            }
        }

        return lanesY[laneIndices[0]];
    }

    private void ShuffleLaneIndices(List<int> indices)
    {
        for (int i = indices.Count - 1; i > 0; i--)
        {
            int j = UnityEngine.Random.Range(0, i + 1);
            int temp = indices[i];
            indices[i] = indices[j];
            indices[j] = temp;
        }
    }

    private bool IsSpawnPositionOccupied(Vector3 candidate, float now, GameObject spawningItem)
    {
        using var occupiedScope = s_isOccupiedMarker.Auto();

        PruneRecentSpawns(now);
        Vector2 spawningHalf = GetApproxHalfSize(spawningItem);
        // 配置しようとしているアイテムの分類は1回だけ取得する（毎判定で配列・一覧探索を増やさない）
        bool spawningIsGood = IsGoodItem(spawningItem);

        // ① 最近スポーン記録との重なり判定
        foreach (var record in recentSpawnRecords)
        {
            if (record == null) continue;

            float requiredX = GetRequiredXDistance(spawningIsGood, spawningHalf, record.isGoodItem, record.halfSizeX);
            float requiredY = Mathf.Max(minSpawnDistanceY, spawningHalf.y + record.halfSizeY);

            if (Mathf.Abs(record.position.x - candidate.x) < requiredX &&
                Mathf.Abs(record.position.y - candidate.y) < requiredY)
            {
                return true;
            }
        }

        // ★ ② 同フレーム内の仮登録レコードとの重なり判定（FindObjectsByType を廃止）
        foreach (var record in pendingFrameRecords)
        {
            if (record == null) continue;

            float requiredX = GetRequiredXDistance(spawningIsGood, spawningHalf, record.isGoodItem, record.halfSizeX);
            float requiredY = Mathf.Max(minSpawnDistanceY, spawningHalf.y + record.halfSizeY);

            if (Mathf.Abs(record.position.x - candidate.x) < requiredX &&
                Mathf.Abs(record.position.y - candidate.y) < requiredY)
            {
                return true;
            }
        }

        // ③ 実際にアクティブなアイテムとの重なり判定
        //    ★ FindObjectsByType は重いので、同じフレーム内では一覧を再利用する
        //      （取得元は従来どおり ItemPool 管理外のアイテムも含む全体探索のまま）
        Item[] activeItems = GetActiveItemsForFrame();
        foreach (var activeItem in activeItems)
        {
            if (activeItem == null || !activeItem.gameObject.activeInHierarchy) continue;
            if (spawningItem != null && activeItem.gameObject == spawningItem) continue;

            Vector3 pos = activeItem.transform.position;
            Vector2 targetHalf = GetApproxHalfSize(activeItem.gameObject);
            float requiredX = GetRequiredXDistance(spawningIsGood, spawningHalf, IsGoodItemType(activeItem.itemType), targetHalf.x);
            float requiredY = Mathf.Max(minSpawnDistanceY, spawningHalf.y + targetHalf.y);

            if (Mathf.Abs(pos.x - candidate.x) < requiredX &&
                Mathf.Abs(pos.y - candidate.y) < requiredY)
            {
                return true;
            }
        }

        return false;
    }

    private Vector2 GetApproxHalfSize(GameObject obj)
    {
        if (obj == null) return new Vector2(0.2f, 0.2f);

        Renderer r = obj.GetComponentInChildren<Renderer>();
        if (r != null)
        {
            Vector3 e = r.bounds.extents;
            return new Vector2(Mathf.Max(0.01f, e.x), Mathf.Max(0.01f, e.y));
        }

        return new Vector2(0.2f, 0.2f);
    }

    /// <summary>
    /// 良いアイテム（Score/MoreScore/Fever）かどうか。分類が取得できない場合は false
    /// （不明な対象を良い扱いにしない）。
    /// </summary>
    private static bool IsGoodItemType(ItemType itemType)
    {
        return itemType == ItemType.Score || itemType == ItemType.Fever;
    }

    /// <summary>GameObject から良いアイテムかを判定する（Item が無い場合は false）。</summary>
    private static bool IsGoodItem(GameObject obj)
    {
        if (obj == null) return false;

        Item item = obj.GetComponent<Item>();
        return item != null && IsGoodItemType(item.itemType);
    }

    /// <summary>
    /// 近接配置の余白（ワールド単位）。負・NaN は 0（＝接するまで詰める設定。無効化ではない）。
    /// </summary>
    private float GetGoodItemCloseMarginX()
    {
        return SanitizeNonNegative(goodItemCloseMarginX);
    }

    /// <summary>
    /// X方向の必要間隔（3つの判定経路で共通に使う）。
    /// ・良い同士（A・Bとも Score/MoreScore/Fever）かつ機能有効のときだけ「半幅合計＋余白」。
    /// ・それ以外（片方が対象外・分類不明・機能無効）は従来どおり Max(minSpawnDistanceX, 半幅合計)。
    /// </summary>
    private float GetRequiredXDistance(bool isGoodA, Vector2 halfA, bool isGoodB, float otherHalfX)
    {
        float sizeSum = halfA.x + otherHalfX;

        if (enableGoodItemClosePlacement && isGoodA && isGoodB)
        {
            return sizeSum + GetGoodItemCloseMarginX();
        }

        return Mathf.Max(minSpawnDistanceX, sizeSum);
    }

    private void RegisterRecentSpawn(GameObject spawnedItem, Vector3 position, float now)
    {
        Vector2 half = GetApproxHalfSize(spawnedItem);
        recentSpawnRecords.Add(new RecentSpawnRecord
        {
            position = position,
            time = now,
            halfSizeX = half.x,
            halfSizeY = half.y,
            // 分類は記録した時点のアイテム種別で固定する（プール再利用で別の対象と誤認しない）
            isGoodItem = IsGoodItem(spawnedItem)
        });
    }

    // ★ 追加: 同フレーム内の仮配置レコードを登録
    private void RegisterPendingFrameRecord(GameObject spawnedItem, Vector3 position, float now)
    {
        Vector2 half = GetApproxHalfSize(spawnedItem);
        pendingFrameRecords.Add(new RecentSpawnRecord
        {
            position = position,
            time = now,
            halfSizeX = half.x,
            halfSizeY = half.y,
            // ①と同じ分類保持（3経路で同じX判定にする）
            isGoodItem = IsGoodItem(spawnedItem)
        });
    }

    private void PruneRecentSpawns(float now)
    {
        for (int i = recentSpawnRecords.Count - 1; i >= 0; i--)
        {
            var record = recentSpawnRecords[i];
            if (record == null || now - record.time > recentSpawnWindow)
            {
                recentSpawnRecords.RemoveAt(i);
            }
        }
    }

    private float GetHugeLaneY()
    {
        if (lanesY != null && lanesY.Length > 1)
        {
            return lanesY[1];
        }
        if (lanesY != null && lanesY.Length > 0)
        {
            return lanesY[0];
        }

        return 0f;
    }

    private float GetRandomLaneY()
    {
        if (lanesY != null && lanesY.Length > 0)
        {
            return lanesY[UnityEngine.Random.Range(0, lanesY.Length)];
        }

        return 0f;
    }

    private float GetSpawnXWorld()
    {
        return GetScreenRightEdgeX() + spawnOffsetFromRight;
    }

    private float GetScreenRightEdgeX()
    {
        float rightEdgeX = 0f;
        Camera cam = Camera.main;
        if (cam != null)
        {
            float halfHeight = cam.orthographicSize;
            float halfWidth = halfHeight * cam.aspect;
            rightEdgeX = cam.transform.position.x + halfWidth;
        }
        else
        {
            rightEdgeX = 11.25f;
        }

        return rightEdgeX;
    }

    private void SpawnHugeObstacle()
    {
        int spawnCount = Mathf.Max(0, hugeObstacleSpawnCount);
        if (spawnCount <= 0) return;

        for (int i = 0; i < spawnCount; i++)
        {
            SpawnSingleHugeObstacle();
        }
    }

    private void SpawnSingleHugeObstacle()
    {
        if (ItemPool.Instance == null) return;

        GameObject item = ItemPool.Instance.GetFromPoolByItemType(ItemType.HugeObstacle);
        if (item == null) return;

        Item itemComp = item.GetComponent<Item>();
        if (itemComp == null || itemComp.itemType != ItemType.HugeObstacle)
        {
            ItemPool.Instance.ReturnToPool(item);
            return;
        }

        if (!TryPlaceItemWithoutOverlap(item, true, float.PositiveInfinity, out _))
        {
            ItemPool.Instance.ReturnToPool(item);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  デバッグ出現（数字キー1〜6）
    //  ・子機のゲーム中のみ。タイトル・ロード中・ゲーム終了後は出現させない。
    //  ・キーを押した瞬間だけ（wasPressedThisFrame）。長押しの連続出現はしない。
    //  ・通常スポーンと同じ画面右側のX位置に、中央レーン(lanesY[1])のYで1個だけ出す。
    //  ・重なり回避で別レーンへ逃がさない。配置できなければその入力分は失敗として扱う。
    //  ・取得時の効果・回収・移動は既存処理（Item/ItemEffect/ItemMove/ItemPool）を使う。
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 数字キー1〜6（上段・テンキー共通）の押下（その瞬間のみ）を検出し、対応する種類をデバッグ出現させる。
    /// Inspector の enableDebugSpawnKeys が false のときは何もしない。
    /// どの段階で止まったか切り分けられるよう、初回のみ初期化状態をログ出力する（毎フレームは出さない）。
    /// </summary>
    private void HandleDebugSpawnKeys()
    {
        LogDebugInitOnce();

        if (!enableDebugSpawnKeys) return;

        Keyboard keyboard = Keyboard.current;
        if (keyboard == null)
        {
            LogDebugKeyWarningOnce("Keyboard.current が取得できません（Input System 未初期化の可能性）。");
            return;
        }

        // ゲーム終了後はデバッグ出現させない。
        // タイトル・ロード・結果シーンには ItemSpawner 自体が存在しないため、シーン名ではなく状態で判定する。
        // GameManager が取得できない場合でもこの経路は動かす（ChildGameScene 直接Play の初期化差を許容する）。
        GameManager gm = GameManager.instance;
        if (gm != null && gm.IsGameOver)
        {
            LogDebugKeyWarningOnce("ゲーム終了後のためデバッグ出現を停止します。");
            return;
        }

        // 押した瞬間だけ判定する（長押しで毎フレーム出現させない）。
        // 上段数字キーとテンキーのどちらかを押した瞬間に「1回だけ」出現させ、同フレームの二重出現を防ぐ。
        if (IsDebugKeyPressedThisFrame(keyboard, 1)) SpawnDebugItemByKey(1, ItemType.Score);
        if (IsDebugKeyPressedThisFrame(keyboard, 2)) SpawnDebugItemByKey(2, ItemType.Boost);
        if (IsDebugKeyPressedThisFrame(keyboard, 3)) SpawnDebugItemByKey(3, ItemType.Fever);
        if (IsDebugKeyPressedThisFrame(keyboard, 4)) SpawnDebugItemByKey(4, ItemType.Clock);
        if (IsDebugKeyPressedThisFrame(keyboard, 5)) SpawnDebugItemByKey(5, ItemType.BGM);
        if (IsDebugKeyPressedThisFrame(keyboard, 6)) SpawnDebugItemByKey(6, ItemType.HugeObstacle);
        if (IsDebugKeyPressedThisFrame(keyboard, 7)) SpawnDebugPattern();   // 配置パターンを順番に1つ出す
    }

    /// <summary>
    /// 上段数字キー(1〜6)またはテンキー(1〜6)のどちらかを「このフレームで押した」かを返す。
    /// 両方を同時に押しても1回だけ true になり、同じ種類を二重に出現させない。
    /// </summary>
    private static bool IsDebugKeyPressedThisFrame(Keyboard keyboard, int number)
    {
        if (keyboard == null) return false;

        switch (number)
        {
            case 1: return keyboard.digit1Key.wasPressedThisFrame || keyboard.numpad1Key.wasPressedThisFrame;
            case 2: return keyboard.digit2Key.wasPressedThisFrame || keyboard.numpad2Key.wasPressedThisFrame;
            case 3: return keyboard.digit3Key.wasPressedThisFrame || keyboard.numpad3Key.wasPressedThisFrame;
            case 4: return keyboard.digit4Key.wasPressedThisFrame || keyboard.numpad4Key.wasPressedThisFrame;
            case 5: return keyboard.digit5Key.wasPressedThisFrame || keyboard.numpad5Key.wasPressedThisFrame;
            case 6: return keyboard.digit6Key.wasPressedThisFrame || keyboard.numpad6Key.wasPressedThisFrame;
            case 7: return keyboard.digit7Key.wasPressedThisFrame || keyboard.numpad7Key.wasPressedThisFrame;
            default: return false;
        }
    }

    /// <summary>
    /// デバッグ出現の初期化状態を初回だけ出力する（原因切り分け用。毎フレームは出さない）。
    /// </summary>
    private void LogDebugInitOnce()
    {
        if (_debugInitLogged) return;
        _debugInitLogged = true;

        GameManager gm = GameManager.instance;
        Camera cam = Camera.main;
        int laneCount = (lanesY != null) ? lanesY.Length : 0;
        float midLane = (laneCount >= 2) ? lanesY[1] : 0f;

        Debug.Log($"[ItemSpawner] デバッグ出現キー初期化 | enableDebugSpawnKeys={enableDebugSpawnKeys} | " +
                  $"itemPool={(ItemPool.Instance != null)} | gameManager={(gm != null)} | " +
                  $"gameManagerIsGameOver={(gm != null && gm.IsGameOver)} | keyboard={(Keyboard.current != null)} | " +
                  $"lanes={laneCount} | midLaneY={midLane:F3} | cameraMain={(cam != null)} | spawnEnabled={spawnEnabled}");
    }

    /// <summary>デバッグ出現キーの警告を1回だけ出力する（毎フレームのログ氾濫を防ぐ）。</summary>
    private void LogDebugKeyWarningOnce(string message)
    {
        if (_debugKeyWarningLogged) return;
        _debugKeyWarningLogged = true;
        Debug.LogWarning($"[ItemSpawner] デバッグ出現キー: {message}");
    }

    /// <summary>
    /// デバッグキー1つ分の出現処理。
    /// フィーバー中の HugeObstacle は出現させない（通常出現と同じ禁止条件・既存のフィーバー状態を参照）。
    /// </summary>
    private void SpawnDebugItemByKey(int keyNumber, ItemType itemType)
    {
        if (itemType == ItemType.HugeObstacle && IsFeverActiveForSpawn())
        {
            Debug.Log($"[ItemSpawner] デバッグ出現キー{keyNumber}: フィーバー中のため {itemType} は出現させません。");
            return;
        }

        if (!TrySpawnDebugItemAtMiddleLane(itemType, out _))
        {
            Debug.LogWarning($"[ItemSpawner] デバッグ出現キー{keyNumber}: {itemType} の出現に失敗しました（中央レーン固定。別レーンへは逃がしません）。");
        }
    }

    /// <summary>
    /// 指定した ItemType を、通常スポーンと同じ画面右側のX位置を起点に「中央レーン(lanesY[1])」のYで1個だけ出現させる。
    ///  ・レーンは中央固定（別レーンへは逃がさない）。
    ///  ・重なり回避は既存の overlapAvoidanceXStep × maxPlacementAttempts を使い X 方向にのみ行う
    ///    （通常スポーンの TryPlaceItemWithoutOverlap と同じ考え方。無制限のリトライはしない）。
    ///  ・HugeObstacle もこのデバッグ経路では中央レーンを使う（通常出現の hugeObstacleSpawnY は変更しない）。
    ///  ・BGM はこの経路では通常出現の残り時間制限(120秒)を受けない（LOUD_ITEM経路の確認用）。
    /// </summary>
    private bool TrySpawnDebugItemAtMiddleLane(ItemType itemType, out GameObject spawnedItem)
    {
        spawnedItem = null;

        if (ItemPool.Instance == null)
        {
            Debug.LogWarning("[ItemSpawner] デバッグ出現: ItemPool.Instance がありません。");
            return false;
        }

        // 中央レーンの座標は既存レーン設定(lanesY)を参照する。
        if (lanesY == null || lanesY.Length < 2)
        {
            Debug.LogWarning("[ItemSpawner] デバッグ出現: 中央レーン(lanesY[1])が設定されていません（lanesY未設定）。");
            return false;
        }

        GameObject item = ItemPool.Instance.GetFromPoolByItemType(itemType);
        if (item == null)
        {
            Debug.LogWarning($"[ItemSpawner] デバッグ出現: ItemPool から {itemType} を取得できませんでした（プレハブ未登録の可能性）。");
            return false;
        }

        Item itemComp = item.GetComponent<Item>();
        if (itemComp == null || itemComp.itemType != itemType)
        {
            ItemPool.Instance.ReturnToPool(item);
            Debug.LogWarning($"[ItemSpawner] デバッグ出現: 取得したアイテムの種類が {itemType} と一致しません。");
            return false;
        }

        float y = lanesY[1];                // 中央レーン固定（別レーンへは逃がさない）
        float baseX = GetSpawnXWorld();
        float now = Time.unscaledTime;
        float xStep = Mathf.Max(0f, overlapAvoidanceXStep);
        int attempts = Mathf.Max(1, maxPlacementAttempts);

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            // 重なり回避は X 方向のみ（中央レーンのYは固定）。通常スポーンと同じ考え方で右へずらす。
            float x = baseX + xStep * attempt;
            Vector3 candidate = new Vector3(x, y, SpawnZ);

            if (IsSpawnPositionOccupied(candidate, now, item))
                continue;

            item.transform.position = candidate;
            RegisterRecentSpawn(item, candidate, now);
            RegisterPendingFrameRecord(item, candidate, now);

            spawnedItem = item;
            if (showDebugSpawnKeyLogs)
                Debug.Log($"[ItemSpawner] デバッグ出現: {itemType} を中央レーン (x={x:F2}, y={y:F2}, attempt={attempt}) に1個出現させました。");
            return true;
        }

        // 中央レーンに配置できなかった（別レーンへは逃がさない）。
        ItemPool.Instance.ReturnToPool(item);
        Debug.LogWarning($"[ItemSpawner] デバッグ出現: 中央レーン(y={y:F3})に配置できませんでした（{attempts}回試行 / {itemType}）。" +
                         "重なり回避はX方向のみで、別レーンへは逃がしません。");
        return false;
    }

    /// <summary>
    /// 既存のフィーバー状態（GameManager.IsFeverActive）を参照する。
    /// デバッグ専用のフィーバーフラグは作らない。
    /// </summary>
    private static bool IsFeverActiveForSpawn()
    {
        GameManager gm = GameManager.instance;
        return gm != null && gm.IsFeverActive;
    }

    /// <summary>
    /// BGMアイテムが通常出現できるか。本編開始からの経過時間(GameManager.MainPlayElapsedSeconds)が
    /// しきい値(bgmNormalSpawnElapsedSeconds, 既定120秒)以上のときだけ許可する。
    /// チュートリアル中（本編未開始）は経過0のため許可しない。
    /// </summary>
    private bool IsBgmNormalSpawnAllowed()
    {
        GameManager gm = GameManager.instance;
        if (gm == null || !gm.IsMainPlayActive) return false; // 本編が始まっていなければ許可しない
        return gm.MainPlayElapsedSeconds >= bgmNormalSpawnElapsedSeconds;
    }
}