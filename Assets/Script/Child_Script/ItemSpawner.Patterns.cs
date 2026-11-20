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

    [Tooltip("ONならシーンに古いパターンが残っていてもコード定義のクッキーラン風レーンパターンと間隔を優先する。")]
    [SerializeField] private bool useCodeDefinedRunPatterns = true;

    [Tooltip("本編開始から最初のパターンを出すまでの秒数。")]
    [SerializeField, Min(0f)] private float firstPatternDelaySeconds = 0.25f;

    [Tooltip("パターン（と休憩）が終わってから、次のパターンまでのランダム出現期間の最小秒数。")]
    [SerializeField, Min(0f)] private float patternIntervalMinSeconds = 0.25f;

    [Tooltip("同・最大秒数。最小より小さい値は最小として扱う。")]
    [SerializeField, Min(0f)] private float patternIntervalMaxSeconds = 0.65f;

    [Tooltip("パターンの最後のアイテムが出現位置を抜けた後、ランダム出現を再開するまでの余裕（秒）。")]
    [SerializeField, Min(0f)] private float patternClearanceSeconds = 0.20f;

    [Tooltip("フィーバーが始まったとき、フィーバー用パターン（use=FeverOnly）を1回すぐに出す。")]
    [SerializeField] private bool spawnFeverPatternOnFeverStart = true;

    [Tooltip("配置パターンの一覧。")]
    [SerializeField] private List<SpawnPattern> patterns = ItemSpawnPatternDefaults.Create();

    private const int PatternKindCount = 6;

    private List<SpawnPattern> _runtimePatterns;
    private float _patternVisibleUntil;
    private float _nextPatternTime;
    private float _patternBlockUntil;
    private float _restFrom;
    private float _restUntil;
    private float _restDensity = 1f;
    private bool _patternWasFever;
    private int _lastPatternIndex = -1;
    private int _debugPatternIndex = -1;
    private float _nextSirenPatternAllowedTime;
    private readonly GameObject[] _patternPrefabs = new GameObject[PatternKindCount];

    public bool IsPatternVisible => Time.unscaledTime < _patternVisibleUntil;

    /// <summary>通常出現・パターンのアイテムに掛けている速度倍率（難易度倍率込み）。</summary>
    public float ItemSpeedMultiplier => Mathf.Max(0.1f, itemSpeedMultiplier * GameDifficulty.ChildItemSpeedMultiplier);

    public float ItemPlaneZ => SpawnZ;

    private List<SpawnPattern> GetActivePatterns()
    {
        if (useCodeDefinedRunPatterns)
        {
            if (_runtimePatterns == null || _runtimePatterns.Count == 0)
                _runtimePatterns = ItemSpawnPatternDefaults.Create();
            return _runtimePatterns;
        }

        return patterns;
    }

    private float GetEffectiveFirstPatternDelay()
    {
        return useCodeDefinedRunPatterns
            ? Mathf.Min(Mathf.Max(0f, firstPatternDelaySeconds), 0.25f)
            : Mathf.Max(0f, firstPatternDelaySeconds);
    }

    private float GetEffectivePatternClearance()
    {
        float raw = useCodeDefinedRunPatterns ? 0.20f : Mathf.Max(0f, patternClearanceSeconds);
        float speedFactor = Mathf.Max(1f, GameManager.CurrentScoreSpeedMultiplier);
        return raw / speedFactor;
    }

    private void ResetPatternDirector()
    {
        float now = Time.unscaledTime;
        _nextPatternTime = now + GetEffectiveFirstPatternDelay();
        _patternBlockUntil = 0f;
        _patternVisibleUntil = 0f;
        _restFrom = 0f;
        _restUntil = 0f;
        _restDensity = 1f;
        _patternWasFever = IsFeverActiveForSpawn();
        _lastPatternIndex = -1;
        _nextSirenPatternAllowedTime = 0f;
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
    /// ・パターン間のランダム出現では後半に悪いアイテムだけにならないよう重みを抑える。
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
            {
                float elapsed = Time.unscaledTime - normalScheduleStartTime;
                float badScale = Mathf.Max(0f, badItemWeightMultiplier * GameDifficulty.ChildBadItemWeightMultiplier);
                if (elapsed < 30f) badScale *= 0.30f;
                else if (elapsed < 90f) badScale *= 0.55f;
                else badScale *= 0.80f;
                weight *= badScale;
            }
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
        List<SpawnPattern> activePatterns = GetActivePatterns();
        if (!patternsEnabled || activePatterns == null || activePatterns.Count == 0) return;

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

        // 障害レーン（LaneRestriction）中もパターンを止めず、空いている2レーンに流す
        // （制限レーン上のスロットは TryPlacePatternSlots 側で除外・回避する）

        if (now < _nextPatternTime) return;

        int index = SelectPatternIndex(activePatterns, fever, now - normalScheduleStartTime);
        if (index < 0)
        {
            _nextPatternTime = now + 0.4f;
            return;
        }

        if (!TrySpawnPattern(activePatterns[index], now, out float endTime))
        {
            _nextPatternTime = now + 0.25f;
            return;
        }

        _lastPatternIndex = index;
        _nextPatternTime = endTime + DrawPatternInterval();
    }

    private float DrawPatternInterval()
    {
        float min = useCodeDefinedRunPatterns ? 0.25f : Mathf.Max(0f, patternIntervalMinSeconds);
        float max = useCodeDefinedRunPatterns ? 0.65f : Mathf.Max(min, patternIntervalMaxSeconds);
        float raw = (max <= min) ? min : Random.Range(min, max);
        float speedFactor = Mathf.Max(1f, GameManager.CurrentScoreSpeedMultiplier);
        return raw / speedFactor;
    }

    private int SelectPatternIndex(List<SpawnPattern> activePatterns, bool fever, float elapsed)
    {
        BuildPatternPrefabCache();

        float total = 0f;
        int candidates = 0;
        for (int i = 0; i < activePatterns.Count; i++)
        {
            float w = GetPatternSelectionWeight(activePatterns[i], fever, elapsed);
            if (w <= 0f) continue;
            total += w;
            candidates++;
        }

        if (total <= 0f) return -1;

        bool avoidLast = candidates > 1 && _lastPatternIndex >= 0;
        if (avoidLast)
        {
            total = 0f;
            for (int i = 0; i < activePatterns.Count; i++)
            {
                if (i == _lastPatternIndex) continue;
                total += GetPatternSelectionWeight(activePatterns[i], fever, elapsed);
            }
        }

        float pick = Random.value * total;
        int fallback = -1;
        for (int i = 0; i < activePatterns.Count; i++)
        {
            if (avoidLast && i == _lastPatternIndex) continue;

            float w = GetPatternSelectionWeight(activePatterns[i], fever, elapsed);
            if (w <= 0f) continue;

            fallback = i;
            pick -= w;
            if (pick <= 0f) return i;
        }

        return fallback;
    }

    private static bool PatternContainsSiren(SpawnPattern p)
    {
        if (p == null || p.slots == null) return false;
        for (int i = 0; i < p.slots.Count; i++)
        {
            if (p.slots[i] != null && p.slots[i].kind == PatternItemKind.Siren)
                return true;
        }
        return false;
    }

    private float GetPatternSelectionWeight(SpawnPattern p, bool fever, float elapsed)
    {
        if (p == null || !p.enabled || p.weight <= 0f) return 0f;
        if (fever != (p.use == PatternUse.FeverOnly)) return 0f;
        if (elapsed < p.minElapsedSeconds) return 0f;
        if (!IsPatternResolvable(p)) return 0f;

        bool hasSiren = PatternContainsSiren(p);
        if (hasSiren)
        {
            if (fever || !IsBgmNormalSpawnAllowed()) return 0f;
            if (Time.unscaledTime < _nextSirenPatternAllowedTime) return 0f;
        }

        float weight = p.weight;
        if (!fever && p.restSeconds <= 0f && !hasSiren)
        {
            if (elapsed >= 60f && p.minElapsedSeconds >= 55f)
                weight *= 1.6f;
            else if (elapsed >= 60f && p.minElapsedSeconds == 0f)
                weight *= 0.65f;
        }
        return weight;
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

            blockUntil = now + extentSeconds + GetEffectivePatternClearance();
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

        if (PatternContainsSiren(p))
        {
            _nextSirenPatternAllowedTime = now + Random.Range(14f, 20f);
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
        float spawnEdgeX = GetSpawnXWorld();

        var items = new List<GameObject>(p.slots.Count);
        var lanes = new List<int>(p.slots.Count);
        var columns = new List<float>(p.slots.Count);
        float maxWidth = 0f;
        float maxGoodWidth = 0f;
        int blockedLane = LaneRestriction.IsRunning ? LaneRestriction.BlockedLane : -1;

        for (int i = 0; i < p.slots.Count; i++)
        {
            PatternSlot slot = p.slots[i];
            if (slot == null) continue;

            int lane = Mathf.Clamp(slot.lane, 0, laneCount - 1);
            if (flip) lane = laneCount - 1 - lane;

            // 障害レーンが出ているレーンにはアイテムを重ねて配置しない
            if (blockedLane >= 0 && lane == blockedLane) continue;

            GameObject prefab = _patternPrefabs[(int)slot.kind];
            GameObject item = (prefab != null) ? ItemPool.Instance.GetFromPoolByPrefab(prefab) : null;
            if (item == null)
            {
                ReturnPatternItems(items);
                return false;
            }

            item.transform.position = new Vector3(spawnEdgeX + 10000f, lanesY[lane], SpawnZ);

            items.Add(item);
            lanes.Add(lane);
            columns.Add(slot.column);
            float itemWidth = GetApproxHalfSize(item).x * 2f;
            maxWidth = Mathf.Max(maxWidth, itemWidth);
            if (slot.kind == PatternItemKind.LowScore || slot.kind == PatternItemKind.HighScore)
                maxGoodWidth = Mathf.Max(maxGoodWidth, itemWidth);
        }

        if (items.Count == 0) return false;

        // クッキーラン風にアイテム幅ベースでぴったり並ぶ間隔を算出する（重なりすぎず、広がりすぎない）
        float baseWidth = maxGoodWidth > 0.01f ? maxGoodWidth : maxWidth;
        float unitSpacing = Mathf.Clamp(baseWidth * 1.06f, 0.20f, 0.36f);
        float spacingMultiplier = (p.columnSpacing >= 0.75f && p.columnSpacing <= 1.5f) ? p.columnSpacing : 1.0f;
        float spacing = unitSpacing * spacingMultiplier;

        // 既存アイテムがスポーン直近にいる場合、パターンを破棄せず少し右へずらして確実に配置する
        float baseX = spawnEdgeX;
        float rightmostNearSpawnX = float.NegativeInfinity;
        Item[] activeItems = GetActiveItemsForFrame();
        for (int i = 0; i < activeItems.Length; i++)
        {
            Item active = activeItems[i];
            if (active == null || !active.gameObject.activeInHierarchy) continue;
            if (items.Contains(active.gameObject)) continue;

            float x = active.transform.position.x;
            if (x >= spawnEdgeX - unitSpacing * 1.15f && x < spawnEdgeX + 500f)
            {
                if (x > rightmostNearSpawnX) rightmostNearSpawnX = x;
            }
        }

        if (rightmostNearSpawnX > float.NegativeInfinity)
        {
            baseX = Mathf.Clamp(rightmostNearSpawnX + unitSpacing * 1.35f, spawnEdgeX, spawnEdgeX + 1.4f);
        }

        float maxColumn = 0f;
        for (int i = 0; i < columns.Count; i++)
            maxColumn = Mathf.Max(maxColumn, columns[i]);

        for (int i = 0; i < items.Count; i++)
        {
            Vector3 position = new Vector3(baseX + columns[i] * spacing, lanesY[lanes[i]], SpawnZ);
            items[i].transform.position = position;
            ApplySpawnSpeedScale(items[i]);
            RegisterRecentSpawn(items[i], position, now);
            RegisterPendingFrameRecord(items[i], position, now);
        }

        ItemMove move = items[0].GetComponent<ItemMove>();
        float speed = (move != null) ? move.EffectiveSpeed : 0f;
        if (speed < 0.05f) speed = 0.8f;
        float extraRightOffset = Mathf.Max(0f, baseX - spawnEdgeX);
        extentSeconds = (extraRightOffset + maxColumn * spacing + baseWidth) / speed;

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
                case ItemType.BGM:
                    if (_patternPrefabs[(int)PatternItemKind.Siren] == null)
                        _patternPrefabs[(int)PatternItemKind.Siren] = prefab;
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
        if (_nextSirenPatternAllowedTime > 0f) _nextSirenPatternAllowedTime += pausedSeconds;

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
        List<SpawnPattern> activePatterns = GetActivePatterns();
        if (activePatterns == null || activePatterns.Count == 0)
        {
            Debug.LogWarning("[ItemSpawner] デバッグ出現キー7: 配置パターンが登録されていません。");
            return;
        }

        BuildPatternPrefabCache();

        for (int step = 0; step < activePatterns.Count; step++)
        {
            _debugPatternIndex = (_debugPatternIndex + 1) % activePatterns.Count;
            SpawnPattern p = activePatterns[_debugPatternIndex];
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
