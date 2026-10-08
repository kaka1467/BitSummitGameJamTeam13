using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;

/// <summary>
/// ParentWarningSystem：
/// 接近前の演出（予告灯、遅延、速度スケーリング、ルート選択）をすべて管理する。
/// 大きな音による突入の入口も管理する（灯りと遅延を省略し、高速でDoorPeekを強制）。
///
/// 公開エントリーポイント：
///   StartWarningSequence()             — 通常の自動フロー（スケジューラーから呼び出し）
///   StartManualDoorWarningSequence()   — 1キーのデバッグ：完全な予告、DoorPeekを強制
///   StartManualHallwayPassByWarningSequence() — 2キーのデバッグ：完全な予告、フェイントA（ドア前を停止せず通過）を強制
///   StartManualGardenPassByWarningSequence() — 5キーのデバッグ：廊下灯1/2の予告後、庭側を通過
///   StartManualGardenPeekWarningSequence()   — 4キーのデバッグ：廊下灯1/2と庭灯の予告後、GardenPeekPointで庭を覗き、所定時間後に帰還
///   StartManualCatFeintWarningSequence()     — 3キーのデバッグ：ドア側の既存予告の後、猫だけが母親と同じ経路で歩きドアから覗く
///   TriggerInstantPassBy()             — 即時デバッグ通過、灯りと遅延なし
///   TriggerInstantDoor()               — 即時デバッグDoorPeek、灯りと遅延なし
///   StartLoudItemRushInSequence()      — 大きな音による突入：2階の灯りのみ、速度=loudItemRushInMoveSpeed
///   StopWarningSequence()              — 強制停止してリセット（ゲームオーバー、シーンアンロードなど）
///   EndWarningSequence()               — サイクル完了後にMotherSuspicionSystemが呼ぶ正常終了
///
/// 責務の境界：
///   ParentWarningSystem     — 灯り、遅延、速度、ルート確率、突入設定
///   ParentApproachController— 経路移動と向き
///   MotherSuspicionSystem       — ドア分岐、疑惑、部屋チェックの結果、捕獲
/// </summary>
public class ParentWarningSystem : MonoBehaviour
{
    // ── 主要な参照 ────────────────────────────────────────────────────────────
    [Header("参照")]
    [SerializeField] public ParentApproachController approachController;
    [SerializeField] public MotherSuspicionSystem        parentDetection;
    [SerializeField] public MotherGauge              motherGauge;

    // ── 予告灯 ────────────────────────────────────────────────────────────────
    [Header("予告灯")]
    [SerializeField] private GameObject hallwayLight1;   // Lights_Hallway
    [SerializeField] private GameObject hallwayLight2;   // Lights_Hallway 2
    [SerializeField] private GameObject hallwayLight3;   // Lights_Hallway 3
    [SerializeField] private GameObject frontLight;      // Lights_Front
    [SerializeField] private GameObject outsideLight;    // Lights_Outside

    [SerializeField] private AudioSource lightSwitchAudioSource;

    // ── 予兆の段階間の待機（旧: secondFloorDelay / approachDelay） ─────────────
    //   実際の待機は次の2つ。旧名は「2階」「接近」という場所名に依存していたため、
    //   現在の役割が分かる名前に変更した（秒数・待機順は変更していない）。
    //     1) 第1段階の灯りON → 第2段階の灯りON まで
    //     2) 第2段階の灯りON → 移動開始（接近開始）まで
    [Header("予兆の段階間の待機（低疑惑）")]
    [Tooltip("第1段階の灯りを点けてから、第2段階の灯りを点けるまでの最小秒数。")]
    [FormerlySerializedAs("secondFloorDelayMin")]
    public float foreshadowStageIntervalMin = 1f;

    [Tooltip("第1段階の灯りを点けてから、第2段階の灯りを点けるまでの最大秒数。")]
    [FormerlySerializedAs("secondFloorDelayMax")]
    public float foreshadowStageIntervalMax = 10f;

    [Tooltip("第2段階の灯りを点けてから、移動（接近）を開始するまでの最小秒数。")]
    [FormerlySerializedAs("approachDelayMin")]
    public float moveStartDelayMin = 1f;

    [Tooltip("第2段階の灯りを点けてから、移動（接近）を開始するまでの最大秒数。")]
    [FormerlySerializedAs("approachDelayMax")]
    public float moveStartDelayMax = 3f;

    [Header("予兆の段階間の待機（高疑惑）")]
    [Tooltip("現在のゲージがこの閾値を超えた場合、以下の高疑惑用の待機範囲を使用する。")]
    public int highSuspicionDelayGaugeThreshold = 5;

    [Tooltip("ゲージが閾値を超えたときの、第1段階の灯りから第2段階の灯りまでの最小秒数。")]
    [FormerlySerializedAs("highSuspicionSecondFloorDelayMin")]
    public float highSuspicionForeshadowStageIntervalMin = 1f;

    [Tooltip("ゲージが閾値を超えたときの、第1段階の灯りから第2段階の灯りまでの最大秒数。")]
    [FormerlySerializedAs("highSuspicionSecondFloorDelayMax")]
    public float highSuspicionForeshadowStageIntervalMax = 3f;

    [Tooltip("ゲージが閾値を超えたときの、第2段階の灯りから移動（接近）開始までの最小秒数。")]
    [FormerlySerializedAs("highSuspicionApproachDelayMin")]
    public float highSuspicionMoveStartDelayMin;

    [Tooltip("ゲージが閾値を超えたときの、第2段階の灯りから移動（接近）開始までの最大秒数。")]
    [FormerlySerializedAs("highSuspicionApproachDelayMax")]
    public float highSuspicionMoveStartDelayMax = 1f;

    // ── ルート確率 ────────────────────────────────────────────────────────────
    [Header("ルート確率")]
    // ── 状態 ──────────────────────────────────────────────────────────────────
    [Header("状態")]
    [Tooltip("警告／接近シーケンス中はtrue。")]
    public bool isWarningActive;

    // ── 片付け演出との連携 ───────────────────────────────────────────────────
    /// <summary>
    /// 片付け演出（MotherChoreController）中は true。通常の警告開始をすべて抑止する。
    /// 設定は MotherChoreController.SetBlockedByChore() 経由で行う。
    /// </summary>
    private bool _choreBlocked;

    /// <summary>片付け演出中で通常の警告を抑止しているか。</summary>
    public bool IsChoreBlocked => _choreBlocked;

    /// <summary>
    /// ゲーム進行率（0〜1）。ParentWarningSystem の経過時間を監視時間（180秒）で割った値。
    /// 片付け開始の判定に使う（子機からの通知がない環境でも機能する）。
    /// </summary>
    public float GameplayProgressRate =>
        Mathf.Clamp01(_gameplayElapsedSeconds / Mathf.Max(1f, gameplaySecondsForProgressRate));

    [Header("ゲーム進行率")]
    [Tooltip("ゲーム進行率の計算に使う基準秒数（子機の制限時間と同じ180秒を想定）。")]
    [SerializeField, Min(1f)] private float gameplaySecondsForProgressRate = 180f;

    // ── 片付け演出の設定（ここを唯一の設定元にする） ─────────────────────────
    //   MotherChoreController / ParentApproachController はここを参照する
    //   （同じ設定を重複して持たない）。
    [Header("片付け演出")]
    [Tooltip("片付けの滞在時間（秒）。chorePoint到着・Chore開始から計測し、この時間が経過すると Chore_End へ進む。" +
             "Chore_Peek の再生中も時間は進む。")]
    [SerializeField, Min(1f)] private float choreStayDuration = 10f;

    [Tooltip("自然に覗く間隔の最小秒数。Chore中だけ有効。この範囲から次の覗きまでの間隔を抽選する。")]
    [SerializeField, Min(0f)] private float choreNaturalLookIntervalMin = 4f;

    [Tooltip("自然に覗く間隔の最大秒数。Chore中だけ有効。この範囲から次の覗きまでの間隔を抽選する。")]
    [SerializeField, Min(0f)] private float choreNaturalLookIntervalMax = 8f;

    [Tooltip("悪いアイテム取得時に Chore_Peek（こっちを見る）になる確率。0〜1。")]
    [SerializeField, Range(0f, 1f)] private float choreLookProbability = 0.5f;

    [Tooltip("庭覗き（GardenPeek）でGardenPeekPointに留まる基本秒数。" +
             "実際の覗き時間 = この値 + GardenPeekPoint到着時のゲージ値（到着時に一度だけ決定）。")]
    [SerializeField, Min(0f)] private float gardenPeekDurationBase = 3f;

    /// <summary>片付けの滞在時間（秒）。MotherChoreController が参照する。</summary>
    public float ChoreStayDuration => choreStayDuration;

    /// <summary>自然に覗く間隔の最小秒数。</summary>
    public float ChoreNaturalLookIntervalMin => choreNaturalLookIntervalMin;

    /// <summary>自然に覗く間隔の最大秒数。</summary>
    public float ChoreNaturalLookIntervalMax => choreNaturalLookIntervalMax;

    /// <summary>悪いアイテム取得時の視線発生率（0〜1）。</summary>
    public float ChoreLookProbability => choreLookProbability;

    /// <summary>庭覗きの基本時間（秒）。ParentApproachController が参照する。</summary>
    public float GardenPeekDurationBase => gardenPeekDurationBase;

    /// <summary>
    /// 自然に覗く次の間隔を抽選する（最小〜最大。順序が逆でも安全）。
    /// 不正値（両方0以下）の場合は0を返し、呼び出し側が「自然な覗きなし」として扱う。
    /// </summary>
    public float RollNaturalLookInterval()
    {
        float min = Mathf.Min(choreNaturalLookIntervalMin, choreNaturalLookIntervalMax);
        float max = Mathf.Max(choreNaturalLookIntervalMin, choreNaturalLookIntervalMax);

        if (max <= 0f)
        {
            Debug.LogWarning($"[ParentWarningSystem] 自然な覗きの間隔が0以下のため無効化します " +
                             $"(min={choreNaturalLookIntervalMin:F2}, max={choreNaturalLookIntervalMax:F2})", this);
            return 0f;
        }

        return Random.Range(min, max);
    }

    /// <summary>片付け演出の抑止を設定する（MotherChoreController から呼ばれる）。</summary>
    public void SetChoreBlocked(bool blocked)
    {
        if (_choreBlocked == blocked) return;
        _choreBlocked = blocked;
        Debug.Log($"[ParentWarningSystem] 片付け演出による警告抑止: {blocked}");

        // 抑止解除時は、進行中の予告があれば安全に停止して状態を残さない。
        if (!blocked)
        {
            // 何もしない（通常サイクル側が管理する）。
        }
    }

    // ── 現在のルート状態 ──────────────────────────────────────────────────────
    /// <summary>現在の実行で選択されたルート。移動開始前に設定され、シーケンス終了時に解除される。</summary>
    public enum RouteState { None, DoorPeek, HallwayPassBy, GardenPassBy, GardenPeek, CatFeint }
    public RouteState ActiveRoute { get; private set; } = RouteState.None;

    // ── 非公開 ───────────────────────────────────────────────────────────────
    private bool      _eventsSubscribed;
    private Coroutine _foreshadowCoroutine;
    private float     _gameplayElapsedSeconds;
    private bool      _gameplayClockActive;
    private int       _consecutiveAutomaticFeints;

    private void Start()
    {
        _gameplayElapsedSeconds = 0f;
        _gameplayClockActive = true;
        _consecutiveAutomaticFeints = 0;
        // 再プレイ・シーン再読み込みで片付け抑止が残らないようにする。
        _choreBlocked = false;

        if (approachController == null)
            approachController = Object.FindFirstObjectByType<ParentApproachController>();

        if (parentDetection == null)
            parentDetection = Object.FindFirstObjectByType<MotherSuspicionSystem>();

        if (motherGauge == null)
            motherGauge = Object.FindFirstObjectByType<MotherGauge>();

        Debug.Log(
            $"[ParentWarningSystem] Start | approachController={(approachController != null ? approachController.name : "NULL")} | parentDetection={(parentDetection != null ? parentDetection.name : "NULL")} | motherGauge={(motherGauge != null ? motherGauge.name : "NULL")}"
        );

        SubscribeApproachEvents();
    }

    private void OnEnable()
    {
        SubscribeApproachEvents();
    }

    private void OnDisable()
    {
        UnsubscribeApproachEvents();
    }

    private void Update()
    {
        if (_gameplayClockActive)
            _gameplayElapsedSeconds += Time.deltaTime;

        // デバッグ：2キーでフェイントA（HallwayPassBy）を手動起動する。
        // 親機デバッグキーは 1（母親ドア確認：ParentWarningScheduler）／2（本キー）／
        // 0（RushIn：MotherSuspicionSystem）／P（ドア開閉：DoorController）／I・O（疑惑±1：MotherGauge）で構成する。
        // 6キーは片付け演出の手動テスト開始（MotherChoreController.RequestManualStart）。
        if (Keyboard.current == null) return;

        if (Keyboard.current.digit6Key.wasPressedThisFrame)
        {
            Debug.Log("[ParentWarningSystem] 6キー押下 — 片付け演出の手動テスト開始を要求");
            if (parentDetection != null && parentDetection.RequestManualChoreStart())
                return;

            if (parentDetection == null)
                Debug.LogWarning("[ParentWarningSystem] 6キー：parentDetection が未設定のため片付けを開始できません");
        }

        if (Keyboard.current.digit2Key.wasPressedThisFrame)
        {
            Debug.Log("[ParentWarningSystem] 2キー押下 — フェイントA（HallwayPassBy）を手動起動");
            StartManualHallwayPassByWarningSequence();
        }

        if (Keyboard.current.digit3Key.wasPressedThisFrame)
        {
            Debug.Log("[ParentWarningSystem] 3キー押下 — 猫フェイント（CatFeint）を手動起動");
            StartManualCatFeintWarningSequence();
        }

        if (Keyboard.current.digit4Key.wasPressedThisFrame)
        {
            Debug.Log("[ParentWarningSystem] 4キー押下 — 庭側覗き（GardenPeek）を手動起動");
            StartManualGardenPeekWarningSequence();
        }

        if (Keyboard.current.digit5Key.wasPressedThisFrame)
        {
            Debug.Log("[ParentWarningSystem] 5キー押下 — 庭側素通り（GardenPassBy）を手動起動");
            StartManualGardenPassByWarningSequence();
        }
    }

    public void StartWarningSequence()
    {
        if (isWarningActive)
        {
            Debug.Log("[ParentWarningSystem] StartWarningSequence: BLOCKED — sequence already active");
            return;
        }

        // 片付け演出中は通常の警告を開始しない（重複防止）。
        if (_choreBlocked)
        {
            Debug.Log("[ParentWarningSystem] StartWarningSequence: BLOCKED — 片付け演出中");
            return;
        }

        if (!ValidateController()) return;

        isWarningActive = true;
        Debug.Log("[ParentWarningSystem] WARNING START — beginning foreshadowing sequence");

        if (_foreshadowCoroutine != null) StopCoroutine(_foreshadowCoroutine);
        _foreshadowCoroutine = StartCoroutine(ForeshadowAndApproachCoroutine(RouteOverride.None));
    }

    public void StartManualDoorWarningSequence()
    {
        if (isWarningActive)
        {
            Debug.Log("[ParentWarningSystem] StartManualDoorWarningSequence: BLOCKED — sequence already active");
            return;
        }

        // 片付け演出中は通常の警告を開始しない（デバッグ操作も同様に抑止）。
        if (_choreBlocked)
        {
            Debug.Log("[ParentWarningSystem] StartManualDoorWarningSequence: BLOCKED — 片付け演出中");
            return;
        }

        if (!ValidateController()) return;

        isWarningActive = true;
        Debug.Log("[ParentWarningSystem] MANUAL ROUTE: M — DOOR/PEEK");

        if (_foreshadowCoroutine != null) StopCoroutine(_foreshadowCoroutine);
        _foreshadowCoroutine = StartCoroutine(ForeshadowAndApproachCoroutine(RouteOverride.Door, true));
    }

    /// <summary>
    /// フェイントA（HallwayPassBy）の手動テスト起動：完全な予告（灯り2段階）の後に、
    /// ドアで停止せず通り過ぎるルートを強制する。2キーからも呼び出される。
    /// 通常抽選でも使用するHallwayPassByの手動確認。
    /// hallwayPassByPointが未設定の場合は警告を1回出して中止する。
    /// </summary>
    public void StartManualHallwayPassByWarningSequence()
    {
        if (isWarningActive)
        {
            Debug.Log("[ParentWarningSystem] StartManualHallwayPassByWarningSequence: BLOCKED — sequence already active");
            return;
        }

        if (_choreBlocked)
        {
            Debug.Log("[ParentWarningSystem] StartManualHallwayPassByWarningSequence: BLOCKED — 片付け演出中");
            return;
        }

        if (!ValidateController()) return;

        isWarningActive = true;
        Debug.Log("[ParentWarningSystem] MANUAL ROUTE: FEINT-A — HALLWAY PASS-BY");

        if (_foreshadowCoroutine != null) StopCoroutine(_foreshadowCoroutine);
        _foreshadowCoroutine = StartCoroutine(ForeshadowAndApproachCoroutine(RouteOverride.HallwayPassBy, true));
    }

    /// <summary>
    /// 猫フェイントの手動テスト起動：ドア側の既存予告（灯り2段階）の後、猫だけが母親と同じ経路で
    /// DoorPointまで歩き、ドアが隙間だけ開いて猫が覗く。母親は移動せず、庭灯・庭waypointは使わない。
    /// 疑惑加算・捕獲判定・本チェックは発生しない。自動抽選には含まれない。
    /// </summary>
    public void StartManualCatFeintWarningSequence()
    {
        if (isWarningActive)
        {
            Debug.Log("[ParentWarningSystem] StartManualCatFeintWarningSequence: BLOCKED — sequence already active");
            return;
        }

        if (_choreBlocked)
        {
            Debug.Log("[ParentWarningSystem] StartManualCatFeintWarningSequence: BLOCKED — 片付け演出中");
            return;
        }

        if (!ValidateController()) return;

        isWarningActive = true;
        Debug.Log("[ParentWarningSystem] MANUAL ROUTE: CAT FEINT");

        if (_foreshadowCoroutine != null) StopCoroutine(_foreshadowCoroutine);
        _foreshadowCoroutine = StartCoroutine(CatFeintForeshadowCoroutine());
    }

    private IEnumerator CatFeintForeshadowCoroutine()
    {
        // ドア側と同じ既存予告：灯り1段階 → 遅延 → 灯り2段階 → 遅延。庭灯・庭waypointは使わない。
        TurnOnFirstStageLights();
        if (lightSwitchAudioSource != null) lightSwitchAudioSource.Play();
        Debug.Log("[ParentWarningSystem] CAT FEINT: HALLWAY LIGHTS 1/2 ON");

        float stageIntervalDelay = Random.Range(foreshadowStageIntervalMin, foreshadowStageIntervalMax);
        yield return new WaitForSeconds(stageIntervalDelay);

        TurnOnSecondStageLights();
        if (lightSwitchAudioSource != null) lightSwitchAudioSource.Play();
        Debug.Log("[ParentWarningSystem] CAT FEINT: SECOND FLOOR LIGHTS ON");

        float moveStartDelay = Random.Range(moveStartDelayMin, moveStartDelayMax);
        yield return new WaitForSeconds(moveStartDelay);

        // 母親は移動させない。ドアの猫覗きイベントへ引き渡す（ドア開閉はPDが既存APIで行う）。
        ActiveRoute = RouteState.CatFeint;
        if (parentDetection != null)
            parentDetection.TriggerCatFeintEvent();
        else
            Debug.LogWarning("[ParentWarningSystem] parentDetection is NULL — TriggerCatFeintEventを呼べません", this);

        _foreshadowCoroutine = null;
    }

    /// <summary>
    /// 庭側素通りの手動テスト起動：廊下灯1/2の予告後に庭側を通過する。
    /// ドア停止イベントを発生させないため、疑惑・捕獲・ドア分岐は実行されない。
    /// </summary>
    public void StartManualGardenPassByWarningSequence()
    {
        if (isWarningActive)
        {
            Debug.Log("[ParentWarningSystem] StartManualGardenPassByWarningSequence: BLOCKED — sequence already active");
            return;
        }

        if (_choreBlocked)
        {
            Debug.Log("[ParentWarningSystem] StartManualGardenPassByWarningSequence: BLOCKED — 片付け演出中");
            return;
        }

        if (!ValidateController()) return;

        isWarningActive = true;
        Debug.Log("[ParentWarningSystem] MANUAL ROUTE: GARDEN PASS-BY");

        if (_foreshadowCoroutine != null) StopCoroutine(_foreshadowCoroutine);
        _foreshadowCoroutine = StartCoroutine(GardenPassByForeshadowCoroutine());
    }

    /// <summary>
    /// 庭側覗きの手動テスト起動：廊下灯1/2と庭灯の予告後に、GardenPeekPointで停止して庭を覗く。
    /// ドア停止イベント・ドア開閉は発生させず、覗き中の疑惑加算・捕獲判定はPD側で行う。
    /// 覗き時間は gardenPeekDurationBase+GardenPeekPoint到着時のゲージ値（controller側で到着時に一度だけ決定）。
    /// </summary>
    public void StartManualGardenPeekWarningSequence()
    {
        if (isWarningActive)
        {
            Debug.Log("[ParentWarningSystem] StartManualGardenPeekWarningSequence: BLOCKED — sequence already active");
            return;
        }

        if (_choreBlocked)
        {
            Debug.Log("[ParentWarningSystem] StartManualGardenPeekWarningSequence: BLOCKED — 片付け演出中");
            return;
        }

        if (!ValidateController()) return;

        isWarningActive = true;
        Debug.Log("[ParentWarningSystem] MANUAL ROUTE: GARDEN PEEK");

        if (_foreshadowCoroutine != null) StopCoroutine(_foreshadowCoroutine);
        _foreshadowCoroutine = StartCoroutine(GardenPeekForeshadowCoroutine());
    }

    public void TriggerInstantPassBy()
    {
        if (isWarningActive) return;
        if (_choreBlocked) return;
        if (!ValidateController()) return;

        isWarningActive = true;
        ActiveRoute = RouteState.HallwayPassBy;
        Debug.Log("[ParentWarningSystem] INSTANT DEBUG: HALLWAY PASS-BY");

        parentDetection?.ApplyApproachSpeed(true);
        approachController.StartApproachHallwayPassBy();
    }

    public void TriggerInstantDoor()
    {
        if (isWarningActive) return;
        if (_choreBlocked) return;
        if (!ValidateController()) return;

        isWarningActive = true;
        ActiveRoute = RouteState.DoorPeek;
        Debug.Log("[ParentWarningSystem] INSTANT DEBUG: DOOR/PEEK");

        parentDetection?.ApplyApproachSpeed(true);
        approachController.StartApproachDoorOnly();
    }

    /// <summary>
    /// 大きな音による突入：1階の灯りと予告遅延を省略する。
    /// 2階の灯りだけを点灯し、速度をloudItemRushInMoveSpeedに設定してDoorPeekルートを強制する。
    /// 音声とゲージの処理後にMotherSuspicionSystem.OnLoudItemTriggered()から呼び出される。
    /// 警告シーケンスがすでに進行中の場合は何もしない。
    /// </summary>
    public void StartLoudItemRushInSequence()
    {
        if (isWarningActive)
        {
            Debug.Log("[ParentWarningSystem] StartLoudItemRushInSequence: BLOCKED — sequence already active");
            return;
        }

        // 片付け演出中は突入も開始しない（重複防止。通常の親イベントと重複させない）。
        if (_choreBlocked)
        {
            Debug.Log("[ParentWarningSystem] StartLoudItemRushInSequence: BLOCKED — 片付け演出中");
            return;
        }

        if (!ValidateController()) return;

        isWarningActive = true;
        ActiveRoute     = RouteState.DoorPeek;

        if (approachController != null)
            approachController.IsRushIn = true;

        // 2階の灯りのみ — 突入時は1階の灯りを省略する。
        TurnOnSecondStageLights();
        if (lightSwitchAudioSource != null) lightSwitchAudioSource.Play();

        if (parentDetection != null)
            parentDetection.SetLoudItemRushInSpeed(parentDetection.loudItemRushInMoveSpeed);

        Debug.Log($"[ParentWarningSystem] LOUD-ITEM RUSH-IN | speed={parentDetection.loudItemRushInMoveSpeed} | route=DoorPeek");
        approachController.StartApproachDoorOnly();
    }

    /// <summary>実行中の予告を強制停止し、EndWarningSequence()を呼び出す。</summary>
    public void StopWarningSequence()
    {
        if (_foreshadowCoroutine != null)
        {
            StopCoroutine(_foreshadowCoroutine);
            _foreshadowCoroutine = null;
        }
        parentDetection?.CancelPassByDoorSound();

        Debug.Log("[ParentWarningSystem] WARNING STOPPED");
        TurnOffAllLights();
        EndWarningSequence();
    }

    public void NotifyGameOver()
    {
        _gameplayClockActive = false;
        // ゲームオーバー時は片付け抑止も解除しておく（停止状態を残さない）。
        _choreBlocked = false;
    }

    public void EndWarningSequence()
    {
        if (!isWarningActive) return;

        isWarningActive = false;
        ActiveRoute = RouteState.None;
        Debug.Log("[ParentWarningSystem] WARNING ENDED — resetting approach controller");

        TurnOffAllLights();

        if (approachController != null)
            approachController.ResetApproach();
    }

    private enum RouteOverride { None, Door, HallwayPassBy }

    private IEnumerator GardenPassByForeshadowCoroutine()
    {
        // 灯りを点ける前に参照を検証する。未設定なら灯り・移動を一切始めず警告のみで終了する。
        if (approachController == null || approachController.gardenPassByPoint == null ||
            approachController.gardenRoutePoints == null || approachController.gardenRoutePoints.Length == 0)
        {
            Debug.LogWarning("[ParentWarningSystem] gardenPassByPoint／gardenRoutePointsが未設定のためGardenPassByを開始しません。SceneでTurnPoint→GardenPeekPoint間の中間ウェイポイントを順番に割り当ててください。", this);
            _foreshadowCoroutine = null;
            EndWarningSequence();
            yield break;
        }

        TurnOnFirstStageLights();
        if (lightSwitchAudioSource != null) lightSwitchAudioSource.Play();
        Debug.Log("[ParentWarningSystem] GARDEN PASS-BY: HALLWAY LIGHTS 1/2 ON");

        float stageIntervalDelay = Random.Range(foreshadowStageIntervalMin, foreshadowStageIntervalMax);
        yield return new WaitForSeconds(stageIntervalDelay);

        SetLightActive(outsideLight, true);
        if (lightSwitchAudioSource != null) lightSwitchAudioSource.Play();
        Debug.Log("[ParentWarningSystem] GARDEN PASS-BY: OUTSIDE LIGHT ON");

        float moveStartDelay = Random.Range(moveStartDelayMin, moveStartDelayMax);
        yield return new WaitForSeconds(moveStartDelay);

        parentDetection?.ApplyApproachSpeed(true);
        ActiveRoute = RouteState.GardenPassBy;
        approachController.StartApproachGardenPassBy();
        _foreshadowCoroutine = null;
    }

    private IEnumerator GardenPeekForeshadowCoroutine()
    {
        // 灯りを点ける前に参照を検証する。未設定なら灯り・移動を一切始めず警告のみで終了する。
        if (approachController == null || approachController.gardenPeekPoint == null ||
            approachController.gardenRoutePoints == null || approachController.gardenRoutePoints.Length == 0)
        {
            Debug.LogWarning("[ParentWarningSystem] gardenPeekPoint／gardenRoutePointsが未設定のためGardenPeekを開始しません。SceneでTurnPoint→GardenPeekPoint間の中間ウェイポイントを順番に割り当ててください。", this);
            _foreshadowCoroutine = null;
            EndWarningSequence();
            yield break;
        }

        TurnOnFirstStageLights();
        if (lightSwitchAudioSource != null) lightSwitchAudioSource.Play();
        Debug.Log("[ParentWarningSystem] GARDEN PEEK: HALLWAY LIGHTS 1/2 ON");

        float stageIntervalDelay = Random.Range(foreshadowStageIntervalMin, foreshadowStageIntervalMax);
        yield return new WaitForSeconds(stageIntervalDelay);

        SetLightActive(outsideLight, true);
        if (lightSwitchAudioSource != null) lightSwitchAudioSource.Play();
        Debug.Log("[ParentWarningSystem] GARDEN PEEK: OUTSIDE LIGHT ON");

        float moveStartDelay = Random.Range(moveStartDelayMin, moveStartDelayMax);
        yield return new WaitForSeconds(moveStartDelay);

        parentDetection?.ApplyApproachSpeed(true);

        // 覗き時間（gardenPeekDurationBase+GardenPeekPoint到着時のゲージ値）は
        // ParentApproachController側で覗き開始時に一度だけ決定するため、ここでは移動開始のみを行う。
        ActiveRoute = RouteState.GardenPeek;
        approachController.StartApproachGardenPeek();
        _foreshadowCoroutine = null;
    }

    private IEnumerator ForeshadowAndApproachCoroutine(RouteOverride routeOverride, bool isManual = false)
    {
        // フェイントA（HallwayPassBy）は hallwayPassByPoint の割当が前提。
        // 未設定の場合は警告を1回出し、灯り・移動を一切始めずにサイクルを即終了する。
        if (routeOverride == RouteOverride.HallwayPassBy &&
            (approachController == null || approachController.hallwayPassByPoint == null))
        {
            Debug.LogWarning("[ParentWarningSystem] hallwayPassByPointが未設定のためHallwayPassByを開始しません。SceneでTransformを割り当ててください。", this);
            _foreshadowCoroutine = null;
            EndWarningSequence();
            yield break;
        }

        int gauge = (motherGauge != null) ? motherGauge.currentGauge : 0;
        bool highSuspicionDelays = !isManual && gauge > highSuspicionDelayGaugeThreshold;

        TurnOnFirstStageLights();
        if (lightSwitchAudioSource != null) lightSwitchAudioSource.Play();
        Debug.Log("[ParentWarningSystem] FIRST FLOOR LIGHT ON");

        float stageIntervalDelay;
        if (highSuspicionDelays)
        {
            stageIntervalDelay = Random.Range(highSuspicionForeshadowStageIntervalMin, highSuspicionForeshadowStageIntervalMax);
            Debug.Log($"[ParentWarningSystem] 予兆の段階間の待機: {stageIntervalDelay:F1}s (HIGH SUSPICION range {highSuspicionForeshadowStageIntervalMin}-{highSuspicionForeshadowStageIntervalMax}s | gauge={gauge})");
        }
        else
        {
            stageIntervalDelay = Random.Range(foreshadowStageIntervalMin, foreshadowStageIntervalMax);
            Debug.Log($"[ParentWarningSystem] 予兆の段階間の待機: {stageIntervalDelay:F1}s (LOW SUSPICION range {foreshadowStageIntervalMin}-{foreshadowStageIntervalMax}s | gauge={gauge})");
        }
        yield return new WaitForSeconds(stageIntervalDelay);

        TurnOnSecondStageLights();
        if (lightSwitchAudioSource != null) lightSwitchAudioSource.Play();
        Debug.Log("[ParentWarningSystem] SECOND FLOOR LIGHTS ON");

        float moveStartDelay;
        if (highSuspicionDelays)
        {
            moveStartDelay = Random.Range(highSuspicionMoveStartDelayMin, highSuspicionMoveStartDelayMax);
            Debug.Log($"[ParentWarningSystem] 移動開始までの待機: {moveStartDelay:F1}s (HIGH SUSPICION range {highSuspicionMoveStartDelayMin}-{highSuspicionMoveStartDelayMax}s | gauge={gauge})");
        }
        else
        {
            moveStartDelay = Random.Range(moveStartDelayMin, moveStartDelayMax);
            Debug.Log($"[ParentWarningSystem] 移動開始までの待機: {moveStartDelay:F1}s (LOW SUSPICION range {moveStartDelayMin}-{moveStartDelayMax}s | gauge={gauge})");
        }
        yield return new WaitForSeconds(moveStartDelay);

        parentDetection?.ApplyApproachSpeed(isManual);

        RouteState chosenRoute;
        if (routeOverride == RouteOverride.Door)
        {
            chosenRoute = RouteState.DoorPeek;
        }
        else if (routeOverride == RouteOverride.HallwayPassBy)
        {
            chosenRoute = RouteState.HallwayPassBy;
        }
        else
        {
            chosenRoute = ChooseRoute();
        }

        ActiveRoute = chosenRoute;
        bool isAutomatic = !isManual && routeOverride == RouteOverride.None;
        Debug.Log($"[ParentWarningSystem] ROUTE CHOSEN | mode={(isAutomatic ? "Automatic" : "Manual")} | " +
                  $"route={ActiveRoute} | consecutiveFeints={_consecutiveAutomaticFeints}");

        bool started;
        switch (ActiveRoute)
        {
            case RouteState.DoorPeek:
                started = approachController.StartApproachDoorOnly();
                break;

            case RouteState.HallwayPassBy:
                started = approachController.StartApproachHallwayPassBy();
                break;

            case RouteState.GardenPassBy:
                started = approachController.StartApproachGardenPassBy();
                break;

            case RouteState.GardenPeek:
                started = approachController.StartApproachGardenPeek();
                break;

            case RouteState.CatFeint:
                if (parentDetection != null)
                    started = parentDetection.TriggerCatFeintEvent();
                else
                {
                    started = false;
                    EndWarningSequence();
                }
                break;

            default:
                started = false;
                break;
        }

        if (started && isAutomatic)
        {
            bool isFeint = ActiveRoute == RouteState.HallwayPassBy ||
                           ActiveRoute == RouteState.GardenPassBy ||
                           ActiveRoute == RouteState.CatFeint;
            _consecutiveAutomaticFeints = isFeint
                ? _consecutiveAutomaticFeints + 1
                : 0;
            Debug.Log($"[ParentWarningSystem] AUTOMATIC ROUTE COMMITTED | route={ActiveRoute} | " +
                      $"group={(isFeint ? "Feint" : "Peek")} | consecutiveFeints={_consecutiveAutomaticFeints}");
        }
        else if (!started)
        {
            Debug.LogWarning($"[ParentWarningSystem] {(isAutomatic ? "AUTOMATIC" : "MANUAL")} ROUTE NOT STARTED | " +
                             $"route={ActiveRoute} — counter unchanged");
            EndWarningSequence();
        }

        _foreshadowCoroutine = null;
    }

    // 通常抽選は、経過時間でフェイント／覗きのグループ比率を変え、
    // グループ内は均等に選ぶ。RushInと手動起動はここを通らない。
    private RouteState ChooseRoute()
    {
        float feintProbability = _gameplayElapsedSeconds < 60f
            ? 0.50f
            : _gameplayElapsedSeconds < 120f
                ? 0.40f
                : 0.25f;
        if (_consecutiveAutomaticFeints >= 2)
        {
            RouteState forcedPeek = Random.Range(0, 2) == 0
                ? RouteState.DoorPeek
                : RouteState.GardenPeek;
            Debug.Log($"[ParentWarningSystem] ChooseRoute | forcedPeek=true | consecutiveFeints={_consecutiveAutomaticFeints} | result={forcedPeek}");
            return forcedPeek;
        }

        bool chooseFeint = Random.value < feintProbability;
        RouteState result;

        if (chooseFeint)
        {
            int index = Random.Range(0, 3);
            result = index == 0
                ? RouteState.HallwayPassBy
                : index == 1
                    ? RouteState.GardenPassBy
                    : RouteState.CatFeint;
        }
        else
        {
            result = Random.Range(0, 2) == 0
                ? RouteState.DoorPeek
                : RouteState.GardenPeek;
        }

        Debug.Log(
            $"[ParentWarningSystem] ChooseRoute | elapsed={_gameplayElapsedSeconds:F2}s | " +
            $"feintProbability={feintProbability:F2} | group={(chooseFeint ? "Feint" : "Peek")} | result={result}"
        );

        return result;
    }

    private void SetLightActive(GameObject lightObject, bool active)
    {
        if (lightObject != null) lightObject.SetActive(active);
    }

    // 予告 第1段階：新部屋の Lights_Hallway / Lights_Hallway 2 を点灯する。
    private void TurnOnFirstStageLights()
    {
        SetLightActive(hallwayLight1, true);
        SetLightActive(hallwayLight2, true);
    }

    // 予告 第2段階／突入の最終段階：新部屋の Lights_Hallway 3 / Lights_Front を点灯する。
    // 第1段階の灯りは消さない（積み上げ演出）。旧部屋の2階灯りがNoneの場合は何もしない。
    private void TurnOnSecondStageLights()
    {
        SetLightActive(hallwayLight3, true);
        SetLightActive(frontLight, true);
    }

    private void TurnOffAllLights()
    {
        SetLightActive(hallwayLight1, false);
        SetLightActive(hallwayLight2, false);
        SetLightActive(hallwayLight3, false);
        SetLightActive(frontLight, false);
        SetLightActive(outsideLight, false);
    }

    private void SubscribeApproachEvents()
    {
        if (_eventsSubscribed || approachController == null) return;

        approachController.onApproachStarted.AddListener(HandleApproachStarted);
        approachController.onReachedDoor.AddListener(HandleReachedDoor);
        approachController.onStoppedAtDoor.AddListener(HandleStoppedAtDoor);
        approachController.onPassedByDoor.AddListener(HandlePassedByDoor);

        _eventsSubscribed = true;
        Debug.Log("[ParentWarningSystem] Subscribed to ParentApproachController events");
    }

    private void UnsubscribeApproachEvents()
    {
        if (!_eventsSubscribed || approachController == null) return;

        approachController.onApproachStarted.RemoveListener(HandleApproachStarted);
        approachController.onReachedDoor.RemoveListener(HandleReachedDoor);
        approachController.onStoppedAtDoor.RemoveListener(HandleStoppedAtDoor);
        approachController.onPassedByDoor.RemoveListener(HandlePassedByDoor);

        _eventsSubscribed = false;
    }

    private void HandleApproachStarted()
    {
        Debug.Log("[ParentWarningSystem] EVENT: Approach started");

        if (parentDetection != null)
            parentDetection.OnApproachStarted();
    }

    private void HandleReachedDoor()
    {
        Debug.Log("[ParentWarningSystem] EVENT: Reached door");
    }

    private void HandleStoppedAtDoor()
    {
        Debug.Log("[ParentWarningSystem] EVENT: Stopped at door — forwarding to MotherSuspicionSystem.OnApproachReachedDoor()");

        if (!isWarningActive)
        {
            Debug.Log("[ParentWarningSystem] HandleStoppedAtDoor: ignoring — warning not active");
            return;
        }

        if (parentDetection != null)
            parentDetection.OnApproachReachedDoor();
        else
            Debug.LogWarning("[ParentWarningSystem] HandleStoppedAtDoor: parentDetection is NULL");
    }

    private void HandlePassedByDoor()
    {
        Debug.Log("[ParentWarningSystem] EVENT: Passed by door");

        if (!isWarningActive)
        {
            Debug.Log("[ParentWarningSystem] HandlePassedByDoor: ignoring — warning not active");
            return;
        }

        bool shouldPlayPassByDoorSound = ActiveRoute == RouteState.HallwayPassBy;

        if (parentDetection != null)
        {
            parentDetection.OnApproachPassedBy();
            if (shouldPlayPassByDoorSound)
                parentDetection.PlayPassByDoorSound();
        }
        else
            Debug.LogWarning("[ParentWarningSystem] HandlePassedByDoor: parentDetection is NULL");
    }

    private bool ValidateController()
    {
        if (approachController != null) return true;
        Debug.LogWarning("[ParentWarningSystem] approachController is NULL — assign it in the Inspector.", this);
        return false;
    }
}