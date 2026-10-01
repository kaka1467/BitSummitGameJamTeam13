using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

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
///   EndWarningSequence()               — サイクル完了後にParentDetectionV2が呼ぶ正常終了
///
/// 責務の境界：
///   ParentWarningSystem     — 灯り、遅延、速度、ルート確率、突入設定
///   ParentApproachController— 経路移動と向き
///   ParentDetectionV2       — ドア分岐、疑惑、部屋チェックの結果、捕獲
/// </summary>
public class ParentWarningSystem : MonoBehaviour
{
    // ── 主要な参照 ────────────────────────────────────────────────────────────
    [Header("参照")]
    [SerializeField] public ParentApproachController approachController;
    [SerializeField] public ParentDetectionV2        parentDetection;
    [SerializeField] public MotherGauge              motherGauge;

    // ── 予告灯 ────────────────────────────────────────────────────────────────
    [Header("予告灯")]
    [SerializeField] private GameObject firstFloorLight;
    [SerializeField] private GameObject secondFloorLight1;
    [SerializeField] private GameObject secondFloorLight2;
    [SerializeField] private GameObject secondFloorLight3;

    // 新部屋（Lights_Base配下）の予告灯。
    // 旧部屋の参照（firstFloorLight / secondFloorLight1〜3）がNoneでも例外を出さないよう、
    // 点灯・消灯はSetLightActive()経由で行う。
    [SerializeField] private GameObject hallwayLight1;   // Lights_Hallway
    [SerializeField] private GameObject hallwayLight2;   // Lights_Hallway 2
    [SerializeField] private GameObject hallwayLight3;   // Lights_Hallway 3
    [SerializeField] private GameObject frontLight;      // Lights_Front
    [SerializeField] private GameObject outsideLight;    // Lights_Outside

    [SerializeField] private AudioSource lightSwitchAudioSource;

    // ── 予告遅延のスケーリング ────────────────────────────────────────────────
    [Header("予告遅延（低疑惑）")]
    [Tooltip("低疑惑時の1階の灯りと2階の灯りの間隔の最小秒数。")]
    public float secondFloorDelayMin = 1f;
    [Tooltip("低疑惑時の1階の灯りと2階の灯りの間隔の最大秒数。")]
    public float secondFloorDelayMax = 10f;
    [Tooltip("低疑惑時の2階の灯りから接近開始までの最小秒数。")]
    public float approachDelayMin = 1f;
    [Tooltip("低疑惑時の2階の灯りから接近開始までの最大秒数。")]
    public float approachDelayMax = 3f;

    [Header("予告遅延（高疑惑）")]
    [Tooltip("現在のゲージがこの閾値を超えた場合、以下の高疑惑用遅延範囲を使用する。")]
    public int highSuspicionDelayGaugeThreshold = 5;
    [Tooltip("ゲージが閾値を超えたときの1階の灯りと2階の灯りの間隔の最小秒数。")]
    public float highSuspicionSecondFloorDelayMin = 1f;
    [Tooltip("ゲージが閾値を超えたときの1階の灯りと2階の灯りの間隔の最大秒数。")]
    public float highSuspicionSecondFloorDelayMax = 3f;
    [Tooltip("ゲージが閾値を超えたときの2階の灯りから接近開始までの最小秒数。")]
    public float highSuspicionApproachDelayMin;
    [Tooltip("ゲージが閾値を超えたときの2階の灯りから接近開始までの最大秒数。")]
    public float highSuspicionApproachDelayMax = 1f;

    // ── 移動速度 ──────────────────────────────────────────────────────────────
    [Header("接近速度")]
    [Tooltip("自動ルートに設定するmoveSpeedの最小値。")]
    public float approachMoveSpeedMin = 5f;
    [Tooltip("自動ルートに設定するmoveSpeedの最大値。")]
    public float approachMoveSpeedMax = 15f;
    [Tooltip("自動ルートで最大疑惑時に線形加算するmoveSpeed。")]
    public float approachSpeedSuspicionBonus;
    [Tooltip("現在のゲージがこの閾値を超えた場合、以下の高疑惑速度範囲を使用する。")]
    public int highSuspicionSpeedGaugeThreshold = 5;
    [Tooltip("ゲージが閾値を超えたときに設定するmoveSpeedの最小値。")]
    public float highSuspicionApproachMoveSpeedMin = 25f;
    [Tooltip("ゲージが閾値を超えたときに設定するmoveSpeedの最大値。")]
    public float highSuspicionApproachMoveSpeedMax = 30f;

    // ── 突入速度 ──────────────────────────────────────────────────────────────
    [Header("大きな音による突入")]
    [Tooltip("大きな音による突入時に接近コントローラーへ設定するmoveSpeed。通常の高疑惑速度より明らかに速くする。")]
    public float loudItemRushInMoveSpeed = 40f;

    // ── デバッグ速度の上書き ──────────────────────────────────────────────────
    [Header("デバッグ速度上書き（N／M手動ルート）")]
    [Tooltip("trueの場合、N／M手動ルートはランダム範囲の代わりにfixedDebugApproachSpeedを使用する。")]
    public bool useFixedDebugApproachSpeed;
    [Tooltip("useFixedDebugApproachSpeedがtrueのときにN／M手動ルートで使う固定moveSpeed。")]
    public float fixedDebugApproachSpeed = 4f;

    // ── ルート確率 ────────────────────────────────────────────────────────────
    [Header("ルート確率")]
    // ── 通過後ドア音 ──────────────────────────────────────────────────────────
    [Header("廊下通過後のドア音")]
    [Tooltip("通過完了から遠くのドア音が再生されるまでの秒数。")]
    [SerializeField] private float passByThenDoorSoundDelay = 1f;

    // ── 状態 ──────────────────────────────────────────────────────────────────
    [Header("状態")]
    [Tooltip("警告／接近シーケンス中はtrue。")]
    public bool isWarningActive;

    // ── 現在のルート状態 ──────────────────────────────────────────────────────
    /// <summary>現在の実行で選択されたルート。移動開始前に設定され、シーケンス終了時に解除される。</summary>
    public enum RouteState { None, DoorPeek, HallwayPassBy, GardenPassBy, GardenPeek, CatFeint }
    public RouteState ActiveRoute { get; private set; } = RouteState.None;

    // ── 非公開 ───────────────────────────────────────────────────────────────
    private bool      _eventsSubscribed;
    private Coroutine _foreshadowCoroutine;
    private Coroutine _hallwayPassBySoundCoroutine;
    private float     _gameplayElapsedSeconds;
    private bool      _gameplayClockActive;
    private int       _consecutiveAutomaticFeints;

    private void Start()
    {
        _gameplayElapsedSeconds = 0f;
        _gameplayClockActive = true;
        _consecutiveAutomaticFeints = 0;

        if (approachController == null)
            approachController = Object.FindFirstObjectByType<ParentApproachController>();

        if (parentDetection == null)
            parentDetection = Object.FindFirstObjectByType<ParentDetectionV2>();

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
        // 0（RushIn：ParentDetectionV2）／P（ドア開閉：DoorController）／I・O（疑惑±1：MotherGauge）で構成する。
        if (Keyboard.current == null) return;

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

        float secondFloorDelay = Random.Range(secondFloorDelayMin, secondFloorDelayMax);
        yield return new WaitForSeconds(secondFloorDelay);

        TurnOnSecondStageLights();
        if (lightSwitchAudioSource != null) lightSwitchAudioSource.Play();
        Debug.Log("[ParentWarningSystem] CAT FEINT: SECOND FLOOR LIGHTS ON");

        float approachDelay = Random.Range(approachDelayMin, approachDelayMax);
        yield return new WaitForSeconds(approachDelay);

        // 母親は移動させない。ドアの猫覗きイベントへ引き渡す（ドア開閉はPDV2が既存APIで行う）。
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

        if (!ValidateController()) return;

        isWarningActive = true;
        Debug.Log("[ParentWarningSystem] MANUAL ROUTE: GARDEN PASS-BY");

        if (_foreshadowCoroutine != null) StopCoroutine(_foreshadowCoroutine);
        _foreshadowCoroutine = StartCoroutine(GardenPassByForeshadowCoroutine());
    }

    /// <summary>
    /// 庭側覗きの手動テスト起動：廊下灯1/2と庭灯の予告後に、GardenPeekPointで停止して庭を覗く。
    /// ドア停止イベント・ドア開閉は発生させず、覗き中の疑惑加算・捕獲判定はPDV2側で行う。
    /// 覗き時間は gardenPeekDurationBase+GardenPeekPoint到着時のゲージ値（controller側で到着時に一度だけ決定）。
    /// </summary>
    public void StartManualGardenPeekWarningSequence()
    {
        if (isWarningActive)
        {
            Debug.Log("[ParentWarningSystem] StartManualGardenPeekWarningSequence: BLOCKED — sequence already active");
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
        if (!ValidateController()) return;

        isWarningActive = true;
        ActiveRoute = RouteState.HallwayPassBy;
        Debug.Log("[ParentWarningSystem] INSTANT DEBUG: HALLWAY PASS-BY");

        ApplyApproachSpeed(true, 0f);
        approachController.StartApproachHallwayPassBy();
    }

    public void TriggerInstantDoor()
    {
        if (isWarningActive) return;
        if (!ValidateController()) return;

        isWarningActive = true;
        ActiveRoute = RouteState.DoorPeek;
        Debug.Log("[ParentWarningSystem] INSTANT DEBUG: DOOR/PEEK");

        ApplyApproachSpeed(true, 0f);
        approachController.StartApproachDoorOnly();
    }

    /// <summary>
    /// 大きな音による突入：1階の灯りと予告遅延を省略する。
    /// 2階の灯りだけを点灯し、速度をloudItemRushInMoveSpeedに設定してDoorPeekルートを強制する。
    /// 音声とゲージの処理後にParentDetectionV2.OnLoudItemTriggered()から呼び出される。
    /// 警告シーケンスがすでに進行中の場合は何もしない。
    /// </summary>
    public void StartLoudItemRushInSequence()
    {
        if (isWarningActive)
        {
            Debug.Log("[ParentWarningSystem] StartLoudItemRushInSequence: BLOCKED — sequence already active");
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

        if (approachController != null)
            approachController.moveSpeed = loudItemRushInMoveSpeed;

        Debug.Log($"[ParentWarningSystem] LOUD-ITEM RUSH-IN | speed={loudItemRushInMoveSpeed} | route=DoorPeek");
        approachController.StartApproachDoorOnly();
    }

    /// <summary>実行中の予告または通過後ドア音のコルーチンを強制停止し、EndWarningSequence()を呼び出す。</summary>
    public void StopWarningSequence()
    {
        if (_foreshadowCoroutine != null)
        {
            StopCoroutine(_foreshadowCoroutine);
            _foreshadowCoroutine = null;
        }
        if (_hallwayPassBySoundCoroutine != null)
        {
            StopCoroutine(_hallwayPassBySoundCoroutine);
            _hallwayPassBySoundCoroutine = null;
        }

        Debug.Log("[ParentWarningSystem] WARNING STOPPED");
        TurnOffAllLights();
        EndWarningSequence();
    }

    public void NotifyGameOver()
    {
        _gameplayClockActive = false;
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

        float secondFloorDelay = Random.Range(secondFloorDelayMin, secondFloorDelayMax);
        yield return new WaitForSeconds(secondFloorDelay);

        SetLightActive(outsideLight, true);
        if (lightSwitchAudioSource != null) lightSwitchAudioSource.Play();
        Debug.Log("[ParentWarningSystem] GARDEN PASS-BY: OUTSIDE LIGHT ON");

        float approachDelay = Random.Range(approachDelayMin, approachDelayMax);
        yield return new WaitForSeconds(approachDelay);

        ApplyApproachSpeed(true, 0f);
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

        float secondFloorDelay = Random.Range(secondFloorDelayMin, secondFloorDelayMax);
        yield return new WaitForSeconds(secondFloorDelay);

        SetLightActive(outsideLight, true);
        if (lightSwitchAudioSource != null) lightSwitchAudioSource.Play();
        Debug.Log("[ParentWarningSystem] GARDEN PEEK: OUTSIDE LIGHT ON");

        float approachDelay = Random.Range(approachDelayMin, approachDelayMax);
        yield return new WaitForSeconds(approachDelay);

        ApplyApproachSpeed(true, 0f);

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

        float suspicionFraction = GetSuspicionFraction();
        int gauge = (motherGauge != null) ? motherGauge.currentGauge : 0;
        bool highSuspicionDelays = !isManual && gauge > highSuspicionDelayGaugeThreshold;

        TurnOnFirstStageLights();
        if (lightSwitchAudioSource != null) lightSwitchAudioSource.Play();
        Debug.Log("[ParentWarningSystem] FIRST FLOOR LIGHT ON");

        float secondFloorDelay;
        if (highSuspicionDelays)
        {
            secondFloorDelay = Random.Range(highSuspicionSecondFloorDelayMin, highSuspicionSecondFloorDelayMax);
            Debug.Log($"[ParentWarningSystem] Second-floor delay: {secondFloorDelay:F1}s (HIGH SUSPICION range {highSuspicionSecondFloorDelayMin}-{highSuspicionSecondFloorDelayMax}s | gauge={gauge})");
        }
        else
        {
            secondFloorDelay = Random.Range(secondFloorDelayMin, secondFloorDelayMax);
            Debug.Log($"[ParentWarningSystem] Second-floor delay: {secondFloorDelay:F1}s (LOW SUSPICION range {secondFloorDelayMin}-{secondFloorDelayMax}s | gauge={gauge})");
        }
        yield return new WaitForSeconds(secondFloorDelay);

        TurnOnSecondStageLights();
        if (lightSwitchAudioSource != null) lightSwitchAudioSource.Play();
        Debug.Log("[ParentWarningSystem] SECOND FLOOR LIGHTS ON");

        float approachDelay;
        if (highSuspicionDelays)
        {
            approachDelay = Random.Range(highSuspicionApproachDelayMin, highSuspicionApproachDelayMax);
            Debug.Log($"[ParentWarningSystem] Approach-start delay: {approachDelay:F1}s (HIGH SUSPICION range {highSuspicionApproachDelayMin}-{highSuspicionApproachDelayMax}s | gauge={gauge})");
        }
        else
        {
            approachDelay = Random.Range(approachDelayMin, approachDelayMax);
            Debug.Log($"[ParentWarningSystem] Approach-start delay: {approachDelay:F1}s (LOW SUSPICION range {approachDelayMin}-{approachDelayMax}s | gauge={gauge})");
        }
        yield return new WaitForSeconds(approachDelay);

        ApplyApproachSpeed(isManual, suspicionFraction);

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

    private void ApplyApproachSpeed(bool isManual, float suspicionFraction = 0f)
    {
        if (approachController == null) return;

        float speed;

        if (isManual && useFixedDebugApproachSpeed)
        {
            speed = fixedDebugApproachSpeed;
            Debug.Log($"[ParentWarningSystem] APPROACH SPEED: {speed:F2} units/sec (FIXED DEBUG)");
        }
        else
        {
            int gauge = (motherGauge != null) ? motherGauge.currentGauge : 0;

            if (!isManual && gauge > highSuspicionSpeedGaugeThreshold)
            {
                speed = Random.Range(highSuspicionApproachMoveSpeedMin, highSuspicionApproachMoveSpeedMax);
                Debug.Log($"[ParentWarningSystem] APPROACH SPEED: {speed:F2} units/sec (HIGH SUSPICION RANGE)");
            }
            else
            {
                speed = Random.Range(approachMoveSpeedMin, approachMoveSpeedMax);

                if (!isManual && approachSpeedSuspicionBonus > 0f)
                    speed += approachSpeedSuspicionBonus * suspicionFraction;

                Debug.Log($"[ParentWarningSystem] APPROACH SPEED: {speed:F2} units/sec (RANDOMISED, suspicion={suspicionFraction:F2})");
            }
        }

        approachController.moveSpeed = speed;
    }

    private float GetSuspicionFraction()
    {
        if (motherGauge == null || motherGauge.maxGauge <= 0)
            return 0f;

        return Mathf.Clamp01((float)motherGauge.currentGauge / motherGauge.maxGauge);
    }

    private void SetLightActive(GameObject lightObject, bool active)
    {
        if (lightObject != null) lightObject.SetActive(active);
    }

    // 予告 第1段階：新部屋の Lights_Hallway / Lights_Hallway 2 を点灯する。
    // 旧部屋の1階灯り（firstFloorLight）がNoneの場合は何もしない。
    private void TurnOnFirstStageLights()
    {
        SetLightActive(firstFloorLight, true);
        SetLightActive(hallwayLight1, true);
        SetLightActive(hallwayLight2, true);
    }

    // 予告 第2段階／突入の最終段階：新部屋の Lights_Hallway 3 / Lights_Front を点灯する。
    // 第1段階の灯りは消さない（積み上げ演出）。旧部屋の2階灯りがNoneの場合は何もしない。
    private void TurnOnSecondStageLights()
    {
        SetLightActive(secondFloorLight1, true);
        SetLightActive(secondFloorLight2, true);
        SetLightActive(secondFloorLight3, true);
        SetLightActive(hallwayLight3, true);
        SetLightActive(frontLight, true);
    }

    private void TurnOffAllLights()
    {
        SetLightActive(firstFloorLight, false);
        SetLightActive(secondFloorLight1, false);
        SetLightActive(secondFloorLight2, false);
        SetLightActive(secondFloorLight3, false);
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
        Debug.Log("[ParentWarningSystem] EVENT: Stopped at door — forwarding to ParentDetectionV2.OnApproachReachedDoor()");

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

        if (ActiveRoute == RouteState.HallwayPassBy && approachController != null)
            _hallwayPassBySoundCoroutine = StartCoroutine(PlayHallwayPassBySoundCoroutine());

        if (parentDetection != null)
            parentDetection.OnApproachPassedBy();
        else
            Debug.LogWarning("[ParentWarningSystem] HandlePassedByDoor: parentDetection is NULL");
    }

    private IEnumerator PlayHallwayPassBySoundCoroutine()
    {
        float delay = Mathf.Max(0f, passByThenDoorSoundDelay);
        Debug.Log($"[ParentWarningSystem] HallwayPassBy sound: waiting {delay:F1}s");

        yield return new WaitForSeconds(delay);

        Debug.Log("[ParentWarningSystem] HallwayPassBy sound: PLAY");
        approachController.PlayPassBySound();

        _hallwayPassBySoundCoroutine = null;
    }

    private bool ValidateController()
    {
        if (approachController != null) return true;
        Debug.LogWarning("[ParentWarningSystem] approachController is NULL — assign it in the Inspector.", this);
        return false;
    }
}