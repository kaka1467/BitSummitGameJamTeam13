using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 通常出現の「調整値」と「短い配置パターン」。
/// </summary>
public partial class ItemSpawner
{
    // ── 調整値（C13：速度・出現量・悪い割合） ────────────────────────────────
    [Header("Tuning (速度・出現量・悪い割合)")]
    [Tooltip("通常出現とパターンのアイテムの移動速度の倍率（1=Prefab設定のまま）。得点段階の速度倍率とは別に掛かる。")]
    [SerializeField, Min(0.1f)] private float itemSpeedMultiplier = 1f;

    [Tooltip("通常出現の量の倍率（1=現状、2=出現間隔が半分、0.5=2倍に間引く）。休憩区間ではさらに薄くなる。")]
    [SerializeField, Min(0.05f)] private float spawnAmountMultiplier = 1f;

    [Tooltip("悪いアイテム（Clock・サイレン）の出やすさの倍率。Normal Spawn Rules の個数(重み)に掛ける。")]
    [SerializeField, Min(0f)] private float badItemWeightMultiplier = 1f;

    // ── 配置パターン（C11・C12・C14） ───────────────────────────────────────
    [Header("Placement Patterns")]
    [Tooltip("短い配置パターンを使うか。OFFなら従来のランダム出現だけ。")]
    [SerializeField] private bool patternsEnabled = true;

    [Tooltip("本編開始から最初のパターンを出すまでの秒数。")]
    [SerializeField, Min(0f)] private float firstPatternDelaySeconds = 8f;

    [Tooltip("パターン（と休憩）が終わってから、次のパターンまでのランダム出現期間の最小秒数。")]
    [SerializeField, Min(0f)] private float patternIntervalMinSeconds = 6f;

    [Tooltip("同・最大秒数。最小より小さい値は最小として扱う。")]
    [SerializeField, Min(0f)] private float patternIntervalMaxSeconds = 14f;

    [Tooltip("パターンの最後のアイテムが出現位置を抜けた後、ランダム出現を再開するまでの余裕（秒）。")]
    [SerializeField, Min(0f)] private float patternClearanceSeconds = 0.8f;

    [Tooltip("フィーバーが始まったとき、フィーバー用パターン（use=FeverOnly）を1回すぐに出す。")]
    [SerializeField] private bool spawnFeverPatternOnFeverStart = true;

    [Tooltip("配置パターンの一覧。")]
    [SerializeField] private List<SpawnPattern> patterns = ItemSpawnPatternDefaults.Create();

    private const int PatternKindCount = 5;

    private float _patternVisibleUntil;
    private float _nextPatternTime;
    private float _patternBlockUntil;
    private float _restFrom;
    private float _restUntil;
    private float _restDensity = 1f;
    private bool _patternWasFever;
    private int _lastPatternIndex = -1;
    private int _debugPatternIndex = -1;
    private readonly GameObject[] _patternPrefabs = new GameObject[PatternKindCount];

    public bool IsPatternVisible => Time.unscaledTime < _patternVisibleUntil;

    /// <summary>通常出現・パターンのアイテムに掛けている速度倍率（難易度倍率込み）。</summary>
    public float ItemSpeedMultiplier => Mathf.Max(0.1f, itemSpeedMultiplier * GameDifficulty.ChildItemSpeedMultiplier);

    public float ItemPlaneZ => SpawnZ;

    private void ResetPatternDirector()
    {
        float now = Time.unscaledTime;
        _nextPatternTime = now + Mathf.Max(0f, firstPatternDelaySeconds);
        _patternBlockUntil = 0f;
        _patternVisibleUntil = 0f;
        _restFrom = 0f;
        _restUntil = 0f;
        _restDensity = 1f;
        _patternWasFever = IsFeverActiveForSpawn();
        _lastPatternIndex = -1;
    }

    private float GetSpawnDensityFactor(float now)
    {
        float factor = Mathf.Max(0.05f, spawnAmountMultiplier);
        if (now >= _restFrom && now < _restUntil)
            factor *= Mathf.Clamp(_restDensity, 0.05f, 1f);
        return factor;
    }

    private bool IsRandomSpawnBlockedByPattern(float now)
    {
        return now < _patternBlockUntil;
    }

    /// <summary>
    /// 通常出現の候補の重み。
    /// ・EASY ではサイレン（ItemType.BGM）を出さず、減点時計（Clock）のみにする（D-R05）。
    /// ・悪いアイテム（Clock・サイレン）に badItemWeightMultiplier × 難易度倍率を掛ける。
    /// </summary>
    private float GetNormalCandidateWeight(PrefabSpawnCount entry)
    {
        float weight = entry.count;

        Item item = entry.prefab.GetComponent<Item>();
        if (item != null)
        {
            if (item.itemType == ItemType.BGM && !GameDifficulty.ChildAllowSirenItem)
                return 0f;

            if (item.itemType == ItemType.Clock || item.itemType == ItemType.BGM)
                weight *= Mathf.Max(0f, badItemWeightMultiplier * GameDifficulty.ChildBadItemWeightMultiplier);
        }

        return weight;
    }

    private void ApplySpawnSpeedScale(GameObject item)
    {
        if (item == null) return;

        ItemMove move = item.GetComponent<ItemMove>();
        if (move != null) move.SpeedScale = ItemSpeedMultiplier;
    }

    private void UpdatePatternDirector()
    {
        if (!patternsEnabled || patterns == null || patterns.Count == 0) return;

        GameManager gm = GameManager.instance;
        if (gm != null && gm.IsGameOver) return;

        float now = Time.unscaledTime;
        bool fever = IsFeverActiveForSpawn();

        if (fever != _patternWasFever)
        {
            _patternWasFever = fever;
            if (fever && spawnFeverPatternOnFeverStart)
            {
                _restUntil = 0f;
                _nextPatternTime = now;
            }
        }

        if (!fever && LaneRestriction.IsRunning)
        {
            _nextPatternTime = Mathf.Max(_nextPatternTime, now + 0.5f);
            return;
        }

        if (now < _nextPatternTime) return;

        int index = SelectPatternIndex(fever, now - normalScheduleStartTime);
        if (index < 0)
        {
            _nextPatternTime = now + Mathf.Max(1f, patternIntervalMinSeconds);
            return;
        }

        if (!TrySpawnPattern(patterns[index], now, out float endTime))
        {
            _nextPatternTime = now + 0.4f;
            return;
        }

        _lastPatternIndex = index;
        _nextPatternTime = endTime + DrawPatternInterval();
    }

    private float DrawPatternInterval()
    {
        float min = Mathf.Max(0f, patternIntervalMinSeconds);
        float max = Mathf.Max(min, patternIntervalMaxSeconds);
        return (max <= min) ? min : Random.Range(min, max);
    }

    private int SelectPatternIndex(bool fever, float elapsed)
    {
        BuildPatternPrefabCache();

        float total = 0f;
        int candidates = 0;
        for (int i = 0; i < patterns.Count; i++)
        {
            float w = GetPatternSelectionWeight(patterns[i], fever, elapsed);
            if (w <= 0f) continue;
            total += w;
            candidates++;
        }

        if (total <= 0f) return -1;

        bool avoidLast = candidates > 1 && _lastPatternIndex >= 0;
        if (avoidLast)
        {
            total = 0f;
            for (int i = 0; i < patterns.Count; i++)
            {
                if (i == _lastPatternIndex) continue;
                total += GetPatternSelectionWeight(patterns[i], fever, elapsed);
            }
        }

        float pick = Random.value * total;
        int fallback = -1;
        for (int i = 0; i < patterns.Count; i++)
        {
            if (avoidLast && i == _lastPatternIndex) continue;

            float w = GetPatternSelectionWeight(patterns[i], fever, elapsed);
            if (w <= 0f) continue;

            fallback = i;
            pick -= w;
            if (pick <= 0f) return i;
        }

        return fallback;
    }

    private float GetPatternSelectionWeight(SpawnPattern p, bool fever, float elapsed)
    {
        if (p == null || !p.enabled || p.weight <= 0f) return 0f;
        if (fever != (p.use == PatternUse.FeverOnly)) return 0f;
        if (elapsed < p.minElapsedSeconds) return 0f;
        if (!IsPatternResolvable(p)) return 0f;
        return p.weight;
    }

    private bool IsPatternResolvable(SpawnPattern p)
    {
        bool hasSlots = p.slots != null && p.slots.Count > 0;
        if (!hasSlots) return p.restSeconds > 0f;

        for (int i = 0; i < p.slots.Count; i++)
        {
            PatternSlot slot = p.slots[i];
            if (slot == null || _patternPrefabs[(int)slot.kind] == null) return false;
        }

        return true;
    }

    private bool TrySpawnPattern(SpawnPattern p, float now, out float endTime)
    {
        endTime = now;
        if (p == null) return false;

        int slotCount = (p.slots != null) ? p.slots.Count : 0;
        float blockUntil = now;

        if (slotCount > 0)
        {
            if (ItemPool.Instance == null || lanesY == null || lanesY.Length == 0) return false;
            if (!TryPlacePatternSlots(p, now, out float extentSeconds, out float lifetimeSeconds)) return false;
            _patternVisibleUntil = Mathf.Max(_patternVisibleUntil, now + lifetimeSeconds);

            blockUntil = now + extentSeconds + Mathf.Max(0f, patternClearanceSeconds);
            _patternBlockUntil = Mathf.Max(_patternBlockUntil, blockUntil);
        }

        endTime = blockUntil;

        if (p.restSeconds > 0f)
        {
            _restFrom = blockUntil;
            _restUntil = blockUntil + p.restSeconds;
            _restDensity = Mathf.Clamp(p.restDensity, 0.05f, 1f);
            endTime = _restUntil;
        }

        if (_laneNextSpawnTime != null)
        {
            for (int i = 0; i < _laneNextSpawnTime.Length; i++)
            {
                _laneNextSpawnTime[i] = Mathf.Max(_laneNextSpawnTime[i], blockUntil + DrawLaneSpawnInterval());
            }
        }

        if (showDebugLogs)
            Debug.Log($"[ItemSpawner] パターン『{p.name}』を出しました（スロット{slotCount}個 / ランダム停止={blockUntil - now:F1}s / 休憩={p.restSeconds:F1}s）。");

        return true;
    }

    private bool TryPlacePatternSlots(SpawnPattern p, float now, out float extentSeconds, out float lifetimeSeconds)
    {
        extentSeconds = 0f;
        lifetimeSeconds = 0f;

        int laneCount = lanesY.Length;
        bool flip = p.allowVerticalFlip && laneCount >= 2 && Random.value < 0.5f;
        float baseX = GetSpawnXWorld();

        var items = new List<GameObject>(p.slots.Count);
        var lanes = new List<int>(p.slots.Count);
        float maxWidth = 0f;

        for (int i = 0; i < p.slots.Count; i++)
        {
            PatternSlot slot = p.slots[i];
            GameObject prefab = (slot != null) ? _patternPrefabs[(int)slot.kind] : null;
            GameObject item = (prefab != null) ? ItemPool.Instance.GetFromPoolByPrefab(prefab) : null;
            if (item == null)
            {
                ReturnPatternItems(items);
                return false;
            }

            int lane = Mathf.Clamp(slot.lane, 0, laneCount - 1);
            if (flip) lane = laneCount - 1 - lane;

            item.transform.position = new Vector3(baseX + 10000f, lanesY[lane], SpawnZ);

            items.Add(item);
            lanes.Add(lane);
            maxWidth = Mathf.Max(maxWidth, GetApproxHalfSize(item).x * 2f);
        }

        float spacing = Mathf.Max(p.columnSpacing, maxWidth + GetGoodItemCloseMarginX());
        float maxColumn = 0f;
        for (int i = 0; i < p.slots.Count; i++)
            maxColumn = Mathf.Max(maxColumn, p.slots[i].column);

        for (int i = 0; i < items.Count; i++)
        {
            Vector3 candidate = new Vector3(baseX + p.slots[i].column * spacing, lanesY[lanes[i]], SpawnZ);
            if (IsSpawnPositionOccupied(candidate, now, items[i]))
            {
                ReturnPatternItems(items);
                return false;
            }
        }

        for (int i = 0; i < items.Count; i++)
        {
            Vector3 position = new Vector3(baseX + p.slots[i].column * spacing, lanesY[lanes[i]], SpawnZ);
            items[i].transform.position = position;
            ApplySpawnSpeedScale(items[i]);
            RegisterRecentSpawn(items[i], position, now);
            RegisterPendingFrameRecord(items[i], position, now);
        }

        ItemMove move = items[0].GetComponent<ItemMove>();
        float speed = (move != null) ? move.EffectiveSpeed : 0f;
        if (speed < 0.05f) speed = 0.8f;
        extentSeconds = (maxColumn * spacing + maxWidth) / speed;

        float leftEdgeX = GetScreenRightEdgeX() - GetScreenWidthWorld() - 1f;
        lifetimeSeconds = extentSeconds + Mathf.Max(0f, baseX - leftEdgeX) / speed;

        return true;
    }

    private static void ReturnPatternItems(List<GameObject> items)
    {
        if (ItemPool.Instance == null) return;
        for (int i = 0; i < items.Count; i++)
            ItemPool.Instance.ReturnToPool(items[i]);
    }

    private void BuildPatternPrefabCache()
    {
        for (int i = 0; i < PatternKindCount; i++) _patternPrefabs[i] = null;

        ItemPool pool = ItemPool.Instance;
        if (pool == null || pool.itemPrefabs == null) return;

        int lowScore = int.MaxValue;
        int highScore = int.MinValue;

        for (int i = 0; i < pool.itemPrefabs.Length; i++)
        {
            GameObject prefab = pool.itemPrefabs[i];
            if (prefab == null) continue;

            Item item = prefab.GetComponent<Item>();
            if (item == null) continue;

            switch (item.itemType)
            {
                case ItemType.Score:
                    if (item.scoreAmount < lowScore)
                    {
                        lowScore = item.scoreAmount;
                        _patternPrefabs[(int)PatternItemKind.LowScore] = prefab;
                    }
                    if (item.scoreAmount > highScore)
                    {
                        highScore = item.scoreAmount;
                        _patternPrefabs[(int)PatternItemKind.HighScore] = prefab;
                    }
                    break;
                case ItemType.Clock:
                    if (_patternPrefabs[(int)PatternItemKind.Bad] == null)
                        _patternPrefabs[(int)PatternItemKind.Bad] = prefab;
                    break;
                case ItemType.Fever:
                    if (_patternPrefabs[(int)PatternItemKind.Fever] == null)
                        _patternPrefabs[(int)PatternItemKind.Fever] = prefab;
                    break;
                case ItemType.Boost:
                    if (_patternPrefabs[(int)PatternItemKind.Boost] == null)
                        _patternPrefabs[(int)PatternItemKind.Boost] = prefab;
                    break;
            }
        }
    }

    private void OnGameResumed(float pausedSeconds)
    {
        if (pausedSeconds <= 0f) return;

        float now = Time.unscaledTime;

        normalScheduleStartTime += pausedSeconds;
        nextNormalSpawnAllowedTime += pausedSeconds;
        if (isHugeSpawnScheduled)
            nextHugeSpawnTime = Mathf.Max(nextHugeSpawnTime + pausedSeconds, now + 2.5f);

        if (_nextPatternTime > 0f) _nextPatternTime += pausedSeconds;
        if (_patternBlockUntil > 0f) _patternBlockUntil += pausedSeconds;
        if (_patternVisibleUntil > 0f) _patternVisibleUntil += pausedSeconds;
        if (_restFrom > 0f) _restFrom += pausedSeconds;
        if (_restUntil > 0f) _restUntil += pausedSeconds;

        for (int i = 0; i < recentSpawnRecords.Count; i++)
        {
            if (recentSpawnRecords[i] != null) recentSpawnRecords[i].time += pausedSeconds;
        }

        if (_laneNextSpawnTime != null)
        {
            for (int i = 0; i < _laneNextSpawnTime.Length; i++)
                _laneNextSpawnTime[i] = Mathf.Max(_laneNextSpawnTime[i] + pausedSeconds, now + DrawLaneSpawnInterval());
        }

        if (showDebugLogs)
            Debug.Log($"[ItemSpawner] 停止から再開しました（停止={pausedSeconds:F1}s）。出現の時刻をずらし、各レーンの待ち時間を引き直しました。");
    }

    private void SpawnDebugPattern()
    {
        if (patterns == null || patterns.Count == 0)
        {
            Debug.LogWarning("[ItemSpawner] デバッグ出現キー7: 配置パターンが登録されていません。");
            return;
        }

        BuildPatternPrefabCache();

        for (int step = 0; step < patterns.Count; step++)
        {
            _debugPatternIndex = (_debugPatternIndex + 1) % patterns.Count;
            SpawnPattern p = patterns[_debugPatternIndex];
            if (p == null || !IsPatternResolvable(p)) continue;

            if (TrySpawnPattern(p, Time.unscaledTime, out _))
            {
                Debug.Log($"[ItemSpawner] デバッグ出現キー7: パターン『{p.name}』を出しました。");
            }
            else
            {
                Debug.LogWarning($"[ItemSpawner] デバッグ出現キー7: パターン『{p.name}』は既存アイテムと重なるため出せませんでした。");
            }
            return;
        }

        Debug.LogWarning("[ItemSpawner] デバッグ出現キー7: 出せるパターンがありません（必要なアイテムが ItemPool に未登録の可能性）。");
    }
}
