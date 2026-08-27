using System.Collections;
using UnityEngine;

/// <summary>
/// ParentWarningSystem：
/// 接近前の演出（予告灯、遅延、速度スケーリング、ルート選択）をすべて管理する。
/// 大きな音による突入の入口も管理する（灯りと遅延を省略し、高速でDoorPeekを強制）。
///
/// 公開エントリーポイント：
///   StartWarningSequence()             — 通常の自動フロー（スケジューラーから呼び出し）
///   StartManualPassByWarningSequence() — Nキーのデバッグ：完全な予告、PassByを強制
///   StartManualDoorWarningSequence()   — Mキーのデバッグ：完全な予告、DoorPeekを強制
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
    public float highSuspicionApproachDelayMin = 0f;
    [Tooltip("ゲージが閾値を超えたときの2階の灯りから接近開始までの最大秒数。")]
    public float highSuspicionApproachDelayMax = 1f;

    // ── 移動速度 ──────────────────────────────────────────────────────────────
    [Header("接近速度")]
    [Tooltip("自動ルートに設定するmoveSpeedの最小値。")]
    public float approachMoveSpeedMin = 5f;
    [Tooltip("自動ルートに設定するmoveSpeedの最大値。")]
    public float approachMoveSpeedMax = 15f;
    [Tooltip("自動ルートで最大疑惑時に線形加算するmoveSpeed。")]
    public float approachSpeedSuspicionBonus = 0f;
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
    public bool useFixedDebugApproachSpeed = false;
    [Tooltip("useFixedDebugApproachSpeedがtrueのときにN／M手動ルートで使う固定moveSpeed。")]
    public float fixedDebugApproachSpeed = 4f;

    // ── ルート確率 ────────────────────────────────────────────────────────────
    [Header("ルート確率")]
    [Tooltip("疑惑0（gauge=0）でのDoorPeek確率。")]
    [Range(0f, 1f)]
    public float doorChanceAtMinSuspicion = 0.2f;
    [Tooltip("最大疑惑（gauge=maxGauge）でのDoorPeek確率。危険度を高く感じさせるため1に近い値を推奨。")]
    [Range(0f, 1f)]
    public float doorChanceAtMaxSuspicion = 0.95f;
    [Tooltip("ドア以外の結果のうち、PassByではなくPassByThenDoorSoundを選ぶ割合。")]
    [Range(0f, 1f)]
    public float basePassByThenDoorSoundChance = 0.33f;

    // ── 第3ルートのオーディオ ─────────────────────────────────────────────────
    [Header("通過後ドア音ルート")]
    [Tooltip("PassByThenDoorSoundルートで通過完了後に再生するAudioSource。")]
    [SerializeField] private AudioSource passByThenDoorSoundAudioSource;
    [Tooltip("通過完了から遠くのドア音が再生されるまでの秒数。")]
    [SerializeField] private float passByThenDoorSoundDelay = 1f;

    // ── 状態 ──────────────────────────────────────────────────────────────────
    [Header("状態")]
    [Tooltip("警告／接近シーケンス中はtrue。")]
    public bool isWarningActive = false;

    // ── 現在のルート状態 ──────────────────────────────────────────────────────
    /// <summary>現在の実行で選択されたルート。移動開始前に設定され、シーケンス終了時に解除される。</summary>
    public enum RouteState { None, PassBy, DoorPeek, PassByThenDoorSound }
    public RouteState ActiveRoute { get; private set; } = RouteState.None;

    // ── 非公開 ───────────────────────────────────────────────────────────────
    private bool      _eventsSubscribed    = false;
    private Coroutine _foreshadowCoroutine = null;
    private Coroutine _passByThenDoorSoundCoroutine = null;

    private void Start()
    {
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

    public void StartManualPassByWarningSequence()
    {
        if (isWarningActive)
        {
            Debug.Log("[ParentWarningSystem] StartManualPassByWarningSequence: BLOCKED — sequence already active");
            return;
        }

        if (!ValidateController()) return;

        isWarningActive = true;
        Debug.Log("[ParentWarningSystem] MANUAL ROUTE: N — PASS-BY");

        if (_foreshadowCoroutine != null) StopCoroutine(_foreshadowCoroutine);
        _foreshadowCoroutine = StartCoroutine(ForeshadowAndApproachCoroutine(RouteOverride.PassBy, true));
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

    public void TriggerInstantPassBy()
    {
        if (isWarningActive) return;
        if (!ValidateController()) return;

        isWarningActive = true;
        ActiveRoute = RouteState.PassBy;
        Debug.Log("[ParentWarningSystem] INSTANT DEBUG: PASS-BY");

        ApplyApproachSpeed(true, 0f);
        approachController.StartApproachPassByOnly();
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
        if (secondFloorLight1 != null) secondFloorLight1.SetActive(true);
        if (secondFloorLight2 != null) secondFloorLight2.SetActive(true);
        if (secondFloorLight3 != null) secondFloorLight3.SetActive(true);
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

        if (_passByThenDoorSoundCoroutine != null)
        {
            StopCoroutine(_passByThenDoorSoundCoroutine);
            _passByThenDoorSoundCoroutine = null;
        }

        Debug.Log("[ParentWarningSystem] WARNING STOPPED");
        TurnOffAllLights();
        EndWarningSequence();
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

    private enum RouteOverride { None, PassBy, Door }

    private IEnumerator ForeshadowAndApproachCoroutine(RouteOverride routeOverride, bool isManual = false)
    {
        float suspicionFraction = GetSuspicionFraction();
        int gauge = (motherGauge != null) ? motherGauge.currentGauge : 0;
        bool highSuspicionDelays = !isManual && gauge > highSuspicionDelayGaugeThreshold;

        if (firstFloorLight != null) firstFloorLight.SetActive(true);
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

        if (secondFloorLight1 != null) secondFloorLight1.SetActive(true);
        if (secondFloorLight2 != null) secondFloorLight2.SetActive(true);
        if (secondFloorLight3 != null) secondFloorLight3.SetActive(true);
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
        if (routeOverride == RouteOverride.PassBy)
        {
            chosenRoute = RouteState.PassBy;
        }
        else if (routeOverride == RouteOverride.Door)
        {
            chosenRoute = RouteState.DoorPeek;
        }
        else
        {
            chosenRoute = ChooseRoute();
        }

        ActiveRoute = chosenRoute;
        Debug.Log($"[ParentWarningSystem] ROUTE CHOSEN: {ActiveRoute}");

        switch (ActiveRoute)
        {
            case RouteState.DoorPeek:
                approachController.StartApproachDoorOnly();
                break;

            case RouteState.PassBy:
            case RouteState.PassByThenDoorSound:
                approachController.StartApproachPassByOnly();
                break;
        }

        _foreshadowCoroutine = null;
    }

    private RouteState ChooseRoute()
    {
        float suspicionFraction = GetSuspicionFraction();
        float doorChance = Mathf.Lerp(doorChanceAtMinSuspicion, doorChanceAtMaxSuspicion, suspicionFraction);
        float roll = Random.value;

        if (roll < doorChance)
        {
            Debug.Log($"[ParentWarningSystem] ChooseRoute | suspicion={suspicionFraction:F2} | doorChance={doorChance:F2} | result=DoorPeek");
            return RouteState.DoorPeek;
        }

        float nonDoorRoll = Random.value;
        RouteState result = nonDoorRoll < basePassByThenDoorSoundChance
            ? RouteState.PassByThenDoorSound
            : RouteState.PassBy;

        Debug.Log(
            $"[ParentWarningSystem] ChooseRoute | suspicion={suspicionFraction:F2} | doorChance={doorChance:F2} | nonDoorRoll={nonDoorRoll:F2} | result={result}"
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

    private void TurnOffAllLights()
    {
        if (firstFloorLight != null) firstFloorLight.SetActive(false);
        if (secondFloorLight1 != null) secondFloorLight1.SetActive(false);
        if (secondFloorLight2 != null) secondFloorLight2.SetActive(false);
        if (secondFloorLight3 != null) secondFloorLight3.SetActive(false);
    }

    private void SubscribeApproachEvents()
    {
        if (_eventsSubscribed || approachController == null) return;

        approachController.OnApproachStarted.AddListener(HandleApproachStarted);
        approachController.OnReachedDoor.AddListener(HandleReachedDoor);
        approachController.OnStoppedAtDoor.AddListener(HandleStoppedAtDoor);
        approachController.OnPassedByDoor.AddListener(HandlePassedByDoor);

        _eventsSubscribed = true;
        Debug.Log("[ParentWarningSystem] Subscribed to ParentApproachController events");
    }

    private void UnsubscribeApproachEvents()
    {
        if (!_eventsSubscribed || approachController == null) return;

        approachController.OnApproachStarted.RemoveListener(HandleApproachStarted);
        approachController.OnReachedDoor.RemoveListener(HandleReachedDoor);
        approachController.OnStoppedAtDoor.RemoveListener(HandleStoppedAtDoor);
        approachController.OnPassedByDoor.RemoveListener(HandlePassedByDoor);

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

        if (ActiveRoute == RouteState.PassByThenDoorSound)
        {
            if (_passByThenDoorSoundCoroutine != null)
                StopCoroutine(_passByThenDoorSoundCoroutine);

            _passByThenDoorSoundCoroutine = StartCoroutine(PlayPassByThenDoorSoundCoroutine());
        }

        if (parentDetection != null)
            parentDetection.OnApproachPassedBy();
        else
            Debug.LogWarning("[ParentWarningSystem] HandlePassedByDoor: parentDetection is NULL");
    }

    private IEnumerator PlayPassByThenDoorSoundCoroutine()
    {
        float delay = Mathf.Max(0f, passByThenDoorSoundDelay);
        Debug.Log($"[ParentWarningSystem] PassByThenDoorSound: waiting {delay:F1}s");

        yield return new WaitForSeconds(delay);

        if (passByThenDoorSoundAudioSource != null)
        {
            Debug.Log("[ParentWarningSystem] PassByThenDoorSound: PLAY");
            passByThenDoorSoundAudioSource.Play();
        }
        else
        {
            Debug.LogWarning("[ParentWarningSystem] PassByThenDoorSound: AudioSource is NULL");
        }

        _passByThenDoorSoundCoroutine = null;
    }

    private bool ValidateController()
    {
        if (approachController != null) return true;
        Debug.LogWarning("[ParentWarningSystem] approachController is NULL — assign it in the Inspector.", this);
        return false;
    }
}