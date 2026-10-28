using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 通常出現の「調整値」と「短い配置パターン」。
///
/// 通常のランダム出現（レーン別タイマー）と配置パターンが二重に出ないよう、次の順で整理している。
///  ・パターンを出したら、その配置が画面右端を抜けきるまで（＋余裕）ランダム出現を止める（_patternBlockUntil）。
///  ・パターンの配置前に、全スロットを既存アイテムとの重なり判定にかける。1つでも重なれば何も出さずに後で再試行する。
///  ・休憩区間は「アイテムを出さず、ランダム出現を薄くする」だけ。パターンとは同時に走らない。
///  ・フィーバー中はフィーバー用パターンだけを選び、フィーバー開始時に1回出す。
/// </summary>
public partial class ItemSpawner
{
    // ── 調整値（C13：速度・出現量・悪い割合） ────────────────────────────────
    [Header("Tuning (速度・出現量・悪い割合)")]
    [Tooltip("通常出現とパターンのアイテムの移動速度の倍率（1=Prefab設定のまま）。得点段階の速度倍率とは別に掛かる。")]
    [SerializeField, Min(0.1f)] private float itemSpeedMultiplier = 1f;

    [Tooltip("通常出現の量の倍率（1=現状、2=出現間隔が半分、0.5=2倍に間引く）。休憩区間ではさらに薄くなる。")]
    [SerializeField, Min(0.05f)] private float spawnAmountMultiplier = 1f;

    [Tooltip("悪いアイテム（Clock・サイレン）の出やすさの倍率。Normal Spawn Rules の個数(重み)に掛ける。" +
             "1=現状、0=悪いアイテムを出さない、2=2倍出やすい。悪い割合 = 悪い重み合計 ÷ 全体の重み合計。")]
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

    [Tooltip("配置パターンの一覧。追加・削除・並べ替え・数値変更ができる。lane は 0=上 / 1=中 / 2=下、column は先頭からの列番号。")]
    [SerializeField] private List<SpawnPattern> patterns = ItemSpawnPatternDefaults.Create();

    private const int PatternKindCount = 5;

    private float _nextPatternTime;        // 次にパターンを試す時刻（Time.unscaledTime）
    private float _patternBlockUntil;      // この時刻までランダム出現を止める（パターンの配置が抜けきるまで）
    private float _restFrom;               // 休憩区間の開始
    private float _restUntil;              // 休憩区間の終了
    private float _restDensity = 1f;       // 休憩区間中の通常出現の濃さ
    private bool _patternWasFever;
    private int _lastPatternIndex = -1;
    private int _debugPatternIndex = -1;
    private readonly GameObject[] _patternPrefabs = new GameObject[PatternKindCount];

    /// <summary>パターンの管理状態を初期化する（ResetLaneSpawnTimers から呼ぶ＝本プレイ開始・停止明け）。</summary>
    private void ResetPatternDirector()
    {
        float now = Time.unscaledTime;
        _nextPatternTime = now + Mathf.Max(0f, firstPatternDelaySeconds);
        _patternBlockUntil = 0f;
        _restFrom = 0f;
        _restUntil = 0f;
        _restDensity = 1f;
        _patternWasFever = IsFeverActiveForSpawn();
        _lastPatternIndex = -1;
    }

    /// <summary>通常出現の濃さ（出現量の倍率 × 休憩区間の薄さ）。待ち時間はこの値で割る。</summary>
    private float GetSpawnDensityFactor(float now)
    {
        float factor = Mathf.Max(0.05f, spawnAmountMultiplier);
        if (now >= _restFrom && now < _restUntil)
            factor *= Mathf.Clamp(_restDensity, 0.05f, 1f);
        return factor;
    }

    /// <summary>パターンの配置が抜けきるまでランダム出現を止めるべきか。</summary>
    private bool IsRandomSpawnBlockedByPattern(float now)
    {
        return now < _patternBlockUntil;
    }

    /// <summary>通常出現の候補の重み。悪いアイテム（Clock・サイレン）だけ badItemWeightMultiplier を掛ける。</summary>
    private float GetNormalCandidateWeight(PrefabSpawnCount entry)
    {
        float weight = entry.count;

        Item item = entry.prefab.GetComponent<Item>();
        if (item != null && (item.itemType == ItemType.Clock || item.itemType == ItemType.BGM))
            weight *= Mathf.Max(0f, badItemWeightMultiplier);

        return weight;
    }

    /// <summary>通常出現・パターンで出したアイテムに、速度倍率を設定する（プール返却時は ItemMove 側で1に戻る）。</summary>
    private void ApplySpawnSpeedScale(GameObject item)
    {
        if (item == null) return;

        ItemMove move = item.GetComponent<ItemMove>();
        if (move != null) move.SpeedScale = Mathf.Max(0.1f, itemSpeedMultiplier);
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  ディレクター
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>毎フレーム呼ぶ。パターンを出す時刻になったら1つ選んで配置する。</summary>
    private void UpdatePatternDirector()
    {
        if (!patternsEnabled || patterns == null || patterns.Count == 0) return;

        GameManager gm = GameManager.instance;
        if (gm != null && gm.IsGameOver) return;

        float now = Time.unscaledTime;
        bool fever = IsFeverActiveForSpawn();

        // フィーバー開始の瞬間：休憩を打ち切り、フィーバー用パターンをすぐ試す。
        if (fever != _patternWasFever)
        {
            _patternWasFever = fever;
            if (fever && spawnFeverPatternOnFeverStart)
            {
                _restUntil = 0f;
                _nextPatternTime = now;
            }
        }

        if (now < _nextPatternTime) return;

        int index = SelectPatternIndex(fever, now - normalScheduleStartTime);
        if (index < 0)
        {
            // 使えるパターンが無い（フィーバー用が無い・条件未達など）。少し待って再判定する。
            _nextPatternTime = now + Mathf.Max(1f, patternIntervalMinSeconds);
            return;
        }

        if (!TrySpawnPattern(patterns[index], now, out float endTime))
        {
            // 既存アイテムと重なった。何も出さずに短い間隔で再試行する（毎フレームは試さない）。
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

    /// <summary>条件に合うパターンを重み付きで1つ選ぶ。無ければ -1。直前と同じものは、他に候補があれば避ける。</summary>
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

    /// <summary>必要なアイテムがすべて ItemPool に登録されていて、配置または休憩が成立するか。</summary>
    private bool IsPatternResolvable(SpawnPattern p)
    {
        bool hasSlots = p.slots != null && p.slots.Count > 0;
        if (!hasSlots) return p.restSeconds > 0f;   // 休憩専用パターン

        for (int i = 0; i < p.slots.Count; i++)
        {
            PatternSlot slot = p.slots[i];
            if (slot == null || _patternPrefabs[(int)slot.kind] == null) return false;
        }

        return true;
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  配置
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// パターンを1つ配置する。成功したら endTime（ランダム出現を再開してよい時刻。休憩があればその終わり）を返す。
    ///  ・全スロットぶんのアイテムを先に確保し、既存アイテムとの重なりを全部確認してから動かす（部分的には出さない）。
    ///  ・パターン内のアイテム同士は重なり判定にかけない（意図した並びのため）。
    /// </summary>
    private bool TrySpawnPattern(SpawnPattern p, float now, out float endTime)
    {
        endTime = now;
        if (p == null) return false;

        int slotCount = (p.slots != null) ? p.slots.Count : 0;
        float blockUntil = now;

        if (slotCount > 0)
        {
            if (ItemPool.Instance == null || lanesY == null || lanesY.Length == 0) return false;
            if (!TryPlacePatternSlots(p, now, out float extentSeconds)) return false;

            blockUntil = now + extentSeconds + Mathf.Max(0f, patternClearanceSeconds);
            _patternBlockUntil = Mathf.Max(_patternBlockUntil, blockUntil);
        }

        endTime = blockUntil;

        if (p.restSeconds > 0f)
        {
            // 休憩はパターンの配置が抜けきった後から始める。
            _restFrom = blockUntil;
            _restUntil = blockUntil + p.restSeconds;
            _restDensity = Mathf.Clamp(p.restDensity, 0.05f, 1f);
            endTime = _restUntil;
        }

        // ランダム出現の各レーンは、止めている間の分をまとめて出さないよう、再開後に改めて待ち時間を引く。
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

    /// <summary>全スロットを確保 → 重なり確認 → 配置。失敗時は確保したアイテムをすべてプールへ戻す。</summary>
    private bool TryPlacePatternSlots(SpawnPattern p, float now, out float extentSeconds)
    {
        extentSeconds = 0f;

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

            // 確保した直後は画面外の遠くへ退避する（前回使用時の位置が、後続スロットの重なり判定に影響しないように）。
            item.transform.position = new Vector3(baseX + 10000f, lanesY[lane], SpawnZ);

            items.Add(item);
            lanes.Add(lane);
            maxWidth = Mathf.Max(maxWidth, GetApproxHalfSize(item).x * 2f);
        }

        // 列の間隔：指定値、ただしアイテムの実サイズ＋余白より狭くしない（パターン内で重ならないように）。
        float spacing = Mathf.Max(p.columnSpacing, maxWidth + GetGoodItemCloseMarginX());
        float maxColumn = 0f;
        for (int i = 0; i < p.slots.Count; i++)
            maxColumn = Mathf.Max(maxColumn, p.slots[i].column);

        // 先に全スロットを既存アイテム（直近の出現記録・同フレーム・画面上の実物）と照合する。
        for (int i = 0; i < items.Count; i++)
        {
            Vector3 candidate = new Vector3(baseX + p.slots[i].column * spacing, lanesY[lanes[i]], SpawnZ);
            if (IsSpawnPositionOccupied(candidate, now, items[i]))
            {
                ReturnPatternItems(items);
                return false;
            }
        }

        // 問題なし：配置する。
        for (int i = 0; i < items.Count; i++)
        {
            Vector3 position = new Vector3(baseX + p.slots[i].column * spacing, lanesY[lanes[i]], SpawnZ);
            items[i].transform.position = position;
            ApplySpawnSpeedScale(items[i]);
            RegisterRecentSpawn(items[i], position, now);
            RegisterPendingFrameRecord(items[i], position, now);
        }

        // 配置全体が出現位置を抜けきるまでの時間（先頭アイテムの実効速度で見積もる）。
        ItemMove move = items[0].GetComponent<ItemMove>();
        float speed = (move != null) ? move.EffectiveSpeed : 0f;
        if (speed < 0.05f) speed = 0.8f;
        extentSeconds = (maxColumn * spacing + maxWidth) / speed;

        return true;
    }

    private static void ReturnPatternItems(List<GameObject> items)
    {
        if (ItemPool.Instance == null) return;
        for (int i = 0; i < items.Count; i++)
            ItemPool.Instance.ReturnToPool(items[i]);
    }

    /// <summary>
    /// ItemPool の登録内容から、パターンの種類ごとのPrefabを選ぶ。
    ///  ・LowScore / HighScore：Score型のうち scoreAmount が最小／最大のもの。
    ///  ・Bad：Clock型（サイレンは時間条件があるため使わない）。
    ///  ・無い種類は null（その種類を使うパターンは選ばれない）。
    /// </summary>
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

    // ──────────────────────────────────────────────────────────────────────────
    //  デバッグ：数字キー7で、一覧のパターンを順番に1つずつ出す（時刻・フィーバー条件を無視）
    // ──────────────────────────────────────────────────────────────────────────

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
