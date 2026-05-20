using UnityEngine;
using System;
using System.Collections.Generic;

public class ItemSpawner : MonoBehaviour
{
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

    [Header("HugeObstacle")]
    [SerializeField, Min(0)] private int hugeObstacleSpawnCount = 1;
    [SerializeField] private float hugeObstacleSpawnY = 1.929803e-08f;

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
        public float spawnTime;
        public float visibleByTime;
        public int retryCount;      // リトライ回数

        // ループ用フィールド
        public bool loopEnabled;
        public float loopCycleDuration;   // 次サイクルまでの周期
        public float cycleStartTime;      // このサイクルが始まった実時刻（ゲーム開始からの経過）
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

    private float nextHugeSpawnTime = -1f;
    private bool isHugeSpawnScheduled = false;
    private readonly List<ScheduledNormalSpawn> scheduledNormalSpawns = new List<ScheduledNormalSpawn>();
    private readonly List<RecentSpawnRecord> recentSpawnRecords = new List<RecentSpawnRecord>();

    // ★ 追加: 同一Update内で仮登録した配置済み位置（フレーム終わりにクリア）
    private readonly List<RecentSpawnRecord> pendingFrameRecords = new List<RecentSpawnRecord>();

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
        if (!spawnEnabled) return;

        // ★ フレーム先頭で仮レコードをクリア
        pendingFrameRecords.Clear();

        HandleScheduledNormalSpawns();

        if (!isHugeSpawnScheduled)
            return;

        if (Time.unscaledTime >= nextHugeSpawnTime)
        {
            SpawnHugeObstacle();
            isHugeSpawnScheduled = false;
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

                    scheduled.spawnTime    = Mathf.Max(0f, nextVisible - leadTime);
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

                        scheduled.spawnTime    = Mathf.Max(0f, nextVisible - leadTime);
                        scheduled.visibleByTime = nextVisible;
                        scheduled.cycleStartTime = nextCycleStart;
                        scheduled.retryCount   = 0;

                        Debug.LogWarning($"[ItemSpawner] '{scheduled.prefab?.name}' を {maxRetryCount} 回試みたが配置できなかったため次サイクルへ持ち越します。");
                    }
                    else
                    {
                        Debug.LogWarning($"[ItemSpawner] '{scheduled.prefab?.name}' を {maxRetryCount} 回試みたが配置できなかったため破棄します。");
                        scheduledNormalSpawns.RemoveAt(i);
                    }
                }
                else
                {
                    // 少し後に再試行
                    scheduled.spawnTime = now + retryInterval;
                }
            }
        }
    }

    private void BuildNormalSpawnSchedule()
    {
        scheduledNormalSpawns.Clear();

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
                float visibleTime = startTime + localOffset;
                scheduledNormalSpawns.Add(new ScheduledNormalSpawn
                {
                    prefab           = entry.prefab,
                    spawnTime        = Mathf.Max(0f, visibleTime - leadTime),
                    visibleByTime    = visibleTime,
                    retryCount       = 0,
                    loopEnabled      = rule.loopEnabled,
                    loopCycleDuration = rule.loopEnabled ? cycleDuration : 0f,
                    cycleStartTime   = startTime,          // 第1サイクルの基準時刻
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
        if (move == null || move.speed <= 0f) return 0f;

        return distance / move.speed;
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

        var gm = GameManager.instance;
        bool feverActive = gm != null && gm.IsFeverMagnetActive;

        if (feverActive && !itemComp.isMagnetable)
        {
            ItemPool.Instance.ReturnToPool(item);
            // フィーバー中のスキップは「失敗」ではなく正常終了 → true で除去
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
        if (move == null || move.speed <= 0f)
        {
            return float.PositiveInfinity;
        }

        float remainingTime = Mathf.Max(0f, visibleByTime - now);
        float rightEdgeX = GetScreenRightEdgeX();
        float maxSpawnX = rightEdgeX + (move.speed * remainingTime);
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
        //    ★ FindObjectsByType は重いので、ItemPool 管理外の既存アイテムだけを対象にする
        Item[] activeItems = FindObjectsByType<Item>(FindObjectsSortMode.None);
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
}