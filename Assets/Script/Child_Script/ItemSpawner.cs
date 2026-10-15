using UnityEngine;
using System;
using System.Collections.Generic;
using UnityEngine.InputSystem;
using Unity.Profiling;

public class ItemSpawner : MonoBehaviour
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

    [Header("HugeObstacle")]
    [SerializeField, Min(0)] private int hugeObstacleSpawnCount = 1;
    [SerializeField] private float hugeObstacleSpawnY = 1.929803e-08f;

    // ── BGMの通常出現条件 ─────────────────────────────────────────────────────
    //   BGMは「子機のゲームタイマーの残り時間がこの値(秒)未満」のときだけ通常出現を許可する。
    //   ゲーム開始からの経過時間ではなく、GameManager が管理する実際の残り時間を参照する
    //   （時間の加算・減算があれば、その現在値に従う）。
    [Header("BGM Spawn Condition")]
    [Tooltip("BGMアイテムを通常出現させてよい残り時間のしきい値（秒）。残り時間がこの値未満のときだけ通常出現を許可する。")]
    [SerializeField, Min(0f)] private float bgmNormalSpawnRemainingSeconds = 120f;

    // ── デバッグ出現キー（数字1〜6） ──────────────────────────────────────────
    //   子機のゲーム中のみ有効。タイトル・ロード中・ゲーム終了後は出現させない。
    //   1:Score / 2:Boost / 3:Fever / 4:Clock / 5:BGM / 6:HugeObstacle
    [Header("Debug Spawn Keys")]
    [Tooltip("数字キー(1〜6)によるデバッグ出現を有効にする。")]
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
        BuildNormalSpawnSchedule();

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

        if (!spawnEnabled) return;

        HandleScheduledNormalSpawns();

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
        BuildNormalSpawnSchedule();
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

        if (!TryPlaceItemWithoutOverlap(item, preferHugeLane))
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

        if (!TryPlaceItemWithoutOverlap(item, preferHugeLane: true))
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

    private void HandleScheduledNormalSpawns()
    {
        if (scheduledNormalSpawns.Count == 0) return;

        // 処理対象があるときだけ計測する（毎フレームの空呼び出しをマーカーに出さない）
        using var handleScope = s_handleScheduledMarker.Auto();

        // 予定時刻（spawnTime）は基準時刻を含む絶対時刻なので、現在時刻とそのまま比較できる。
        float now = Time.unscaledTime;

        // ★ 修正: リトライも含めて処理するため、逆順ループで安全に削除
        for (int i = scheduledNormalSpawns.Count - 1; i >= 0; i--)
        {
            var scheduled = scheduledNormalSpawns[i];
            if (scheduled == null || now < scheduled.spawnTime) continue;

            bool placed = SpawnItemByPrefab(scheduled.prefab, scheduled.visibleByTime);

            if (placed)
            {
                if (scheduled.loopEnabled && scheduled.loopCycleDuration > 0f)
                {
                    // ループ: 次サイクルの開始時刻へ再スケジュール
                    float nextCycleStart = scheduled.cycleStartTime + scheduled.loopCycleDuration;
                    float leadTime = GetLeadTimeToScreenEnter(scheduled.prefab);
                    float nextVisible = nextCycleStart + scheduled.localVisibleOffset;

                    // 再予約も同じ絶対時刻系で行う（基準時刻より前には生成しない）
                    scheduled.spawnTime    = Mathf.Max(normalScheduleStartTime, nextVisible - leadTime);
                    scheduled.visibleByTime = nextVisible;
                    scheduled.cycleStartTime = nextCycleStart;
                    scheduled.retryCount   = 0;
                }
                else
                {
                    // 非ループ: 配置成功 → キューから削除
                    scheduledNormalSpawns.RemoveAt(i);
                }
            }
            else
            {
                // ★ 配置失敗 → リトライ上限未満なら再スケジュール、超えたら諦めて削除
                scheduled.retryCount++;
                if (scheduled.retryCount >= maxRetryCount)
                {
                    if (scheduled.loopEnabled && scheduled.loopCycleDuration > 0f)
                    {
                        // ループ中でも上限リトライ失敗時は次サイクルへ持ち越す
                        float nextCycleStart = scheduled.cycleStartTime + scheduled.loopCycleDuration;
                        float leadTime = GetLeadTimeToScreenEnter(scheduled.prefab);
                        float nextVisible = nextCycleStart + scheduled.localVisibleOffset;

                        // 上限リトライ失敗時の次サイクル持ち越しも同じ絶対時刻系で行う
                        scheduled.spawnTime    = Mathf.Max(normalScheduleStartTime, nextVisible - leadTime);
                        scheduled.visibleByTime = nextVisible;
                        scheduled.cycleStartTime = nextCycleStart;
                        scheduled.retryCount   = 0;

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
                    }
                }
                else
                {
                    // 少し後に再試行（now も予定時刻も同じ絶対時刻系なので、そのまま加算できる）
                    scheduled.spawnTime = now + retryInterval;
                }
            }
        }
    }

    private void BuildNormalSpawnSchedule()
    {
        using var buildScope = s_buildScheduleMarker.Auto();

        scheduledNormalSpawns.Clear();

        // この回の基準時刻（＝本プレイ開始時刻）をここで確定する。
        // Start（シーン読み込み時）と RestartSchedule（本プレイ開始時）の両方がここを通るため、
        // 後から呼ばれた方が基準時刻として有効になる（本プレイ開始時の RestartSchedule が最後に来る）。
        normalScheduleStartTime = Time.unscaledTime;

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

                scheduledNormalSpawns.Add(new ScheduledNormalSpawn
                {
                    prefab           = entry.prefab,
                    spawnTime        = Mathf.Max(normalScheduleStartTime, visibleTime - leadTime),
                    visibleByTime    = visibleTime,
                    retryCount       = 0,
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

    // ★ 戻り値を bool に変更: 配置成功なら true
    private bool SpawnItemByPrefab(GameObject prefab, float visibleByTime)
    {
        if (ItemPool.Instance == null) return false;
        if (prefab == null) return false;

        GameObject item = ItemPool.Instance.GetFromPoolByPrefab(prefab);
        if (item == null) return false;

        Item itemComp = item.GetComponent<Item>();
        if (itemComp == null)
        {
            ItemPool.Instance.ReturnToPool(item);
            return false;
        }

        // BGM は「子機のタイマーの残り時間がしきい値(既定120秒)未満」のときだけ通常出現を許可する。
        // 解禁前の予定はここで消費する（保留・毎フレーム再試行・解禁時のまとめ出現はしない）。
        if (itemComp.itemType == ItemType.BGM && !IsBgmNormalSpawnAllowed())
        {
            ItemPool.Instance.ReturnToPool(item);
            if (showDebugLogs)
                Debug.Log($"[ItemSpawner] BGMは残り時間がしきい値({bgmNormalSpawnRemainingSeconds:F0}秒)未満でのみ通常出現します — 今回の予定は見送ります。");
            return true;
        }

        float maxExtraX = GetAllowedExtraXForVisibleDeadline(prefab, visibleByTime, Time.unscaledTime);
        if (TryPlaceItemWithoutOverlap(item, preferHugeLane: false, maxExtraX))
        {
            return true;
        }

        // ★ 配置失敗時はプールに戻してリトライを促す
        ItemPool.Instance.ReturnToPool(item);
        return false;
    }

    private bool TryPlaceItemWithoutOverlap(GameObject item, bool preferHugeLane, float maxExtraX = float.PositiveInfinity)
    {
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

        // ① 最近スポーン記録との重なり判定
        foreach (var record in recentSpawnRecords)
        {
            if (record == null) continue;

            float requiredX = Mathf.Max(minSpawnDistanceX, spawningHalf.x + record.halfSizeX);
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

            float requiredX = Mathf.Max(minSpawnDistanceX, spawningHalf.x + record.halfSizeX);
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
            float requiredX = Mathf.Max(minSpawnDistanceX, spawningHalf.x + targetHalf.x);
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

    private void RegisterRecentSpawn(GameObject spawnedItem, Vector3 position, float now)
    {
        Vector2 half = GetApproxHalfSize(spawnedItem);
        recentSpawnRecords.Add(new RecentSpawnRecord
        {
            position = position,
            time = now,
            halfSizeX = half.x,
            halfSizeY = half.y
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
            halfSizeY = half.y
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

        if (!TryPlaceItemWithoutOverlap(item, preferHugeLane: true))
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
    /// BGMアイテムが通常出現できるか。子機のゲームタイマーの残り時間(GameManager.time)が
    /// しきい値(bgmNormalSpawnRemainingSeconds, 既定120秒)未満のときだけ許可する。
    /// ゲーム開始からの経過時間ではなく、実際の残り時間（時間の加算・減算の現在値）を参照する。
    /// </summary>
    private bool IsBgmNormalSpawnAllowed()
    {
        GameManager gm = GameManager.instance;
        if (gm == null) return false; // 残り時間を取得できない場合は許可しない
        return gm.time < bgmNormalSpawnRemainingSeconds;
    }
}