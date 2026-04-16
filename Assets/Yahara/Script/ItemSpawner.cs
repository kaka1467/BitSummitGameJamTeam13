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
        public SpawnIntervalRange interval = new SpawnIntervalRange();
        public List<PrefabSpawnCount> prefabSpawnCounts = new List<PrefabSpawnCount>();
    }

    [Header("Normal Spawn Rules")]
    [SerializeField] private List<NormalSpawnRule> normalSpawnRules = new List<NormalSpawnRule>
    {
        new NormalSpawnRule()
    };

    [Header("HugeObstacle")]
    [SerializeField, Min(0)] private int hugeObstacleSpawnCount = 1;

    [Header("Overlap Avoidance")]
    [SerializeField, Min(0f)] private float minSpawnDistanceX = 1.2f;
    [SerializeField, Min(0f)] private float minSpawnDistanceY = 0.6f;
    [SerializeField, Min(0f)] private float recentSpawnWindow = 0.6f;
    [SerializeField, Min(0f)] private float overlapAvoidanceXStep = 0.8f;
    [SerializeField, Min(1)] private int maxPlacementAttempts = 8;

    [Serializable]
    private class ScheduledNormalSpawn
    {
        public GameObject prefab;
        public float spawnTime;
        public float visibleByTime;
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

    public float[] lanesY;
    [UnityEngine.SerializeField] private float hugeInitialDelay = 30f; // ゲーム開始後、この秒数まではHugeは出さない
    [UnityEngine.SerializeField] private float hugeCooldownAfterQte = 20f; // QTEクリア後のHugeスポーン猶予

    // HugeObstacle 用の次回スポーン時刻（実時間ベース：QTE中も進む）
    private float nextHugeSpawnTime = -1f;
    private bool isHugeSpawnScheduled = false;
    private readonly List<ScheduledNormalSpawn> scheduledNormalSpawns = new List<ScheduledNormalSpawn>();
    private readonly List<RecentSpawnRecord> recentSpawnRecords = new List<RecentSpawnRecord>();

    private const float SpawnZ = 621.66f;

    void Start()
    {
        ValidateAndNormalizeSettings();
        BuildNormalSpawnSchedule();

        // 初回のHugeObstacleをゲーム開始から hugeInitialDelay 秒後にスポーンするよう予約（実時間）
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
            // QTE成功時から hugeCooldownAfterQte 秒後に次のHugeObstacleをスポーンするよう予約（実時間）
            nextHugeSpawnTime = Time.unscaledTime + hugeCooldownAfterQte;
            isHugeSpawnScheduled = true;
        }
    }

    private void Update()
    {
        HandleScheduledNormalSpawns();

        // HugeObstacle 専用のスポーンタイマー処理（ゲーム時間ベース）
        if (!isHugeSpawnScheduled)
            return;

        if (Time.unscaledTime >= nextHugeSpawnTime)
        {
            SpawnHugeObstacle();
            isHugeSpawnScheduled = false;
        }
    }

    private void ValidateAndNormalizeSettings()
    {
        hugeObstacleSpawnCount = Mathf.Max(0, hugeObstacleSpawnCount);
        minSpawnDistanceX = Mathf.Max(0f, minSpawnDistanceX);
        minSpawnDistanceY = Mathf.Max(0f, minSpawnDistanceY);
        recentSpawnWindow = Mathf.Max(0f, recentSpawnWindow);
        overlapAvoidanceXStep = Mathf.Max(0f, overlapAvoidanceXStep);
        maxPlacementAttempts = Mathf.Max(1, maxPlacementAttempts);

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
                        // HugeObstacle は専用タイマーで管理するため通常ルールには含めない
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
        for (int i = scheduledNormalSpawns.Count - 1; i >= 0; i--)
        {
            var scheduled = scheduledNormalSpawns[i];
            if (scheduled == null || now < scheduled.spawnTime) continue;

            SpawnItemByPrefab(scheduled.prefab, scheduled.visibleByTime);
            scheduledNormalSpawns.RemoveAt(i);
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
                float visibleTime = UnityEngine.Random.Range(startTime, endTime);
                scheduledNormalSpawns.Add(new ScheduledNormalSpawn
                {
                    prefab = entry.prefab,
                    // 指定時間帯は「画面内に入る時刻」として扱う
                    spawnTime = Mathf.Max(0f, visibleTime - leadTime),
                    visibleByTime = visibleTime
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

    private void SpawnItemByPrefab(GameObject prefab, float visibleByTime)
    {
        if (ItemPool.Instance == null) return;
        if (prefab == null) return;

        GameObject item = ItemPool.Instance.GetFromPoolByPrefab(prefab);
        if (item == null) return;

        Item itemComp = item.GetComponent<Item>();
        if (itemComp == null)
        {
            ItemPool.Instance.ReturnToPool(item);
            return;
        }

        var gm = GameManager.instance;
        bool feverActive = gm != null && gm.IsFeverMagnetActive;

        if (feverActive && !itemComp.isMagnetable)
        {
            ItemPool.Instance.ReturnToPool(item);
            return;
        }

        float maxExtraX = GetAllowedExtraXForVisibleDeadline(prefab, visibleByTime, Time.unscaledTime);
        PlaceItemWithoutOverlap(item, preferHugeLane: false, maxExtraX);
    }

    private void PlaceItemWithoutOverlap(GameObject item, bool preferHugeLane, float maxExtraX = float.PositiveInfinity)
    {
        if (item == null) return;

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
            float y = SelectLaneYAvoidingOverlap(item, spawnX, preferHugeLane, now);
            Vector3 candidate = new Vector3(spawnX, y, SpawnZ);

            if (!IsSpawnPositionOccupied(candidate, now, item))
            {
                item.transform.position = candidate;
                RegisterRecentSpawn(item, candidate, now);
                return;
            }
        }

        float fallbackExtraX = overlapAvoidanceXStep * maxPlacementAttempts;
        if (!float.IsPositiveInfinity(clampedMaxExtraX))
        {
            fallbackExtraX = Mathf.Min(fallbackExtraX, clampedMaxExtraX);
        }

        float fallbackX = baseSpawnX + fallbackExtraX;
        float fallbackY = preferHugeLane ? GetHugeLaneY() : GetRandomLaneY();
        Vector3 fallback = new Vector3(fallbackX, fallbackY, SpawnZ);
        item.transform.position = fallback;
        RegisterRecentSpawn(item, fallback, now);
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
            // カメラが取得できない場合のフォールバック
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

        // HugeObstacle 専用に、対応するアイテムだけを確実に取得
        GameObject item = ItemPool.Instance.GetFromPoolByItemType(ItemType.HugeObstacle);
        if (item == null) return;

        Item itemComp = item.GetComponent<Item>();
        if (itemComp == null || itemComp.itemType != ItemType.HugeObstacle)
        {
            ItemPool.Instance.ReturnToPool(item);
            return;
        }

        PlaceItemWithoutOverlap(item, preferHugeLane: true);
    }
}