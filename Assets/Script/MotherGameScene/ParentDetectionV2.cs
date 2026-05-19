using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// ParentDetectionV2:
/// Controls mother-related danger progression, final branching,
/// door behavior, and reset flow.
/// </summary>
public class ParentDetectionV2 : MonoBehaviour
{
    private enum DoorOpenType
    {
        None,
        Peek,
        Full
    }

    [Header("System & UI References")]
    public ParentWarningSystem warningSystem;
    public SleepingController sleepingController;
    public MotherGauge motherGauge;

    [Header("Approach Presentation")]
    [SerializeField] private ParentApproachController approachController;

    [Header("Stage Effects Objects")]
    [SerializeField] private GameObject firstFloorLight;
    [SerializeField] private GameObject secondFloorLight1;
    [SerializeField] private GameObject secondFloorLight2;
    [SerializeField] private GameObject secondFloorLight3;
    [SerializeField] private AudioSource stairsAudioSource;
    [SerializeField] private AudioSource dummyDoorAudioSource;
    [SerializeField] private GameObject realMotherObject;
    [SerializeField] private GameObject dummyMotherObject;

    [Header("Audio Sources (SE)")]
    [SerializeField] private AudioSource lightSwitchAudioSource;
    [SerializeField] private AudioSource mainDoorOpenAudioSource;
    [SerializeField] private AudioSource mainDoorCloseAudioSource;
    [SerializeField] private AudioSource rushInAudioSource;

    [Header("Door Control")]
    [SerializeField] private DoorController targetDoorController;

    [Header("Event Branching")]
    [SerializeField, Range(0f, 1f)] private float dummyProbability = 0.3f;

    [Header("Gauge Speed Settings")]
    [Tooltip("Gauge increase per second when mother is looking")]
    public float riseSpeed = 45f;
    [Tooltip("Gauge decrease per second when safe")]
    public float dropSpeed = 15f;

    [Header("Random Rise Speed Ranges")]
    [SerializeField] private float minRiseSpeed = 20f;
    [SerializeField] private float maxRiseSpeed = 40f;
    [SerializeField] private float minDebugAutoRiseSpeed = 8f;
    [SerializeField] private float maxDebugAutoRiseSpeed = 15f;

    [Header("Loud Item Feature")]
    [SerializeField] private bool enableLoudItemFeature = true;

    [Header("Debug")]
    [SerializeField] private bool autoProgressInDebug = false;

    public bool isCaught = false;
    public bool isMotherLookingNow = false;

    private bool stage1Triggered = false;
    private bool stage2Triggered = false;
    private bool stage3Triggered = false;
    private bool stage4Triggered = false;

    private DoorOpenType currentDoorState = DoorOpenType.None;

    private Coroutine dummyResetCoroutine = null;
    private Coroutine primaryResetCoroutine = null;

    private float decimalGauge = 0f;
    private bool hasPermanentGameOver = false;

    private float currentRandomRiseSpeed;
    private float currentRandomDebugRiseSpeed;

    private void Start()
    {
        isCaught = false;
        isMotherLookingNow = false;

        if (motherGauge == null)
        {
            motherGauge = Object.FindFirstObjectByType<MotherGauge>();
        }

        if (motherGauge != null)
        {
            decimalGauge = motherGauge.currentGauge;
            motherGauge.enableAutoDecrease = false;
        }

        if (sleepingController == null)
        {
            sleepingController = Object.FindFirstObjectByType<SleepingController>();
        }

        if (targetDoorController == null)
        {
            targetDoorController = Object.FindFirstObjectByType<DoorController>();
        }

        if (warningSystem == null)
        {
            warningSystem = Object.FindFirstObjectByType<ParentWarningSystem>();
        }

        if (approachController == null)
        {
            approachController = Object.FindFirstObjectByType<ParentApproachController>();
        }

        RandomizeMotherSpeeds();
        riseSpeed = Random.Range(minRiseSpeed, maxRiseSpeed);
    }

    private void Update()
    {
        if (Keyboard.current != null)
        {
            if (Keyboard.current.pKey.wasPressedThisFrame)
            {
                TriggerFinalEvent(primary: true);
            }

            if (Keyboard.current.oKey.wasPressedThisFrame)
            {
                TriggerFinalEvent(primary: false);
            }

            if (Keyboard.current.lKey.wasPressedThisFrame)
            {
                OnLoudItemTriggered();
            }
        }

        if (isCaught) return;
        if (hasPermanentGameOver) return;
        if (motherGauge == null) return;

        // Suspicion rises if:
        //   a) The full-open event set isMotherLookingNow=true, OR
        //   b) The mother is actively peeking (IsPeekingNow) AND she already reached
        //      the hallway phase (IsInHallwayPhase). This covers the dummy/peek branch
        //      where isMotherLookingNow stays false but the door is still open.
        bool isPeeking = (warningSystem != null) && warningSystem.IsPeekingNow
                         && (approachController != null) && approachController.IsInHallwayPhase;
        bool parentIsLooking = isMotherLookingNow || isPeeking;
        bool playerIsSleeping = false;

        if (sleepingController != null)
        {
            try
            {
                playerIsSleeping = sleepingController.IsSleeping;
            }
            catch
            {
                playerIsSleeping = false;
            }
        }
        else
        {
            if (Keyboard.current != null && Keyboard.current.spaceKey.isPressed)
            {
                playerIsSleeping = true;
            }
        }

        bool useDebugAutoProgress = false;

        if (useDebugAutoProgress)
        {
            if (!playerIsSleeping)
            {
                decimalGauge += currentRandomDebugRiseSpeed * Time.deltaTime;
            }
            else
            {
                decimalGauge -= dropSpeed * Time.deltaTime;
            }
        }
        else
        {
            Debug.Log($"[F:{Time.frameCount}][PDV2] parentIsLooking={parentIsLooking}, playerIsSleeping={playerIsSleeping}, isMotherLookingNow={isMotherLookingNow}, hasPermanentGameOver={hasPermanentGameOver}, isCaught={isCaught}, gauge={decimalGauge}");
            if (parentIsLooking && !playerIsSleeping)
            {
                float riseAmount = riseSpeed * Time.deltaTime;
                Debug.Log($"[F:{Time.frameCount}][PDV2-Gauge] PATH=RISE | decimalGauge BEFORE={decimalGauge:F4} | amount=+{riseAmount:F4} | riseSpeed={riseSpeed}");
                decimalGauge += riseAmount;
            }
            else
            {
                float dropAmount = dropSpeed * Time.deltaTime;
                Debug.Log($"[F:{Time.frameCount}][PDV2-Gauge] PATH=DROP | decimalGauge BEFORE={decimalGauge:F4} | amount=-{dropAmount:F4} | dropSpeed={dropSpeed}");
                decimalGauge -= dropAmount;
            }
        }

        decimalGauge = Mathf.Clamp(decimalGauge, 0f, motherGauge.maxGauge);

        motherGauge.SetGaugeDirect(Mathf.RoundToInt(decimalGauge));
        Debug.Log($"[F:{Time.frameCount}][PDV2-Gauge] AFTER ASSIGN | decimalGauge={decimalGauge:F4} | motherGauge.currentGauge={motherGauge.currentGauge}");

        if (motherGauge.currentGauge >= motherGauge.maxGauge)
        {
            OnPlayerCaught();
        }

        UpdateStages();
    }

    private void RandomizeMotherSpeeds()
    {
        float minRise = Mathf.Min(minRiseSpeed, maxRiseSpeed);
        float maxRise = Mathf.Max(minRiseSpeed, maxRiseSpeed);
        float minDebug = Mathf.Min(minDebugAutoRiseSpeed, maxDebugAutoRiseSpeed);
        float maxDebug = Mathf.Max(minDebugAutoRiseSpeed, maxDebugAutoRiseSpeed);

        currentRandomRiseSpeed = Random.Range(minRise, maxRise);
        currentRandomDebugRiseSpeed = Random.Range(minDebug, maxDebug);
    }

    private void UpdateStages()
    {
        if (motherGauge == null) return;

        float progress = motherGauge.maxGauge <= 0f ? 0f : decimalGauge / motherGauge.maxGauge;

        if (!stage1Triggered && progress >= 0.25f)
        {
            stage1Triggered = true;

            if (firstFloorLight != null) firstFloorLight.SetActive(true);
            if (lightSwitchAudioSource != null) lightSwitchAudioSource.Play();
        }

        if (!stage2Triggered && progress >= 0.5f)
        {
            stage2Triggered = true;

            if (secondFloorLight1 != null) secondFloorLight1.SetActive(true);
            if (secondFloorLight2 != null) secondFloorLight2.SetActive(true);
            if (secondFloorLight3 != null) secondFloorLight3.SetActive(true);
            if (lightSwitchAudioSource != null) lightSwitchAudioSource.Play();
        }

        if (!stage3Triggered && progress >= 0.75f)
        {
            stage3Triggered = true;

            if (stairsAudioSource != null) stairsAudioSource.Play();
        }

        if (!stage4Triggered && progress >= 0.90f)
        {
            stage4Triggered = true;
        }
    }

    /// <summary>
    /// Called by ParentWarningSystem when the mother actually stops at the door.
    /// Final branch happens here.
    /// </summary>
    public void OnApproachReachedDoor()
    {
        Debug.Log($"[PDV2] OnApproachReachedDoor called. isCaught={isCaught}, hasPermanentGameOver={hasPermanentGameOver}");
        if (isCaught || hasPermanentGameOver) return;

        stage4Triggered = true;

        bool isDummy = Random.value < dummyProbability;
        TriggerFinalEvent(primary: !isDummy);
    }

    /// <summary>
    /// Called by ParentWarningSystem when the mother passes by the door.
    /// </summary>
    public void OnApproachPassedBy()
    {
        Debug.Log($"[PDV2] OnApproachPassedBy called. isCaught={isCaught}, hasPermanentGameOver={hasPermanentGameOver}");
        if (isCaught || hasPermanentGameOver) return;

        ResetCycle();

        if (warningSystem != null)
        {
            warningSystem.EndWarningSequence();
        }
    }

    private void TriggerFinalEvent(bool primary)
    {
        if (primary)
        {
            TriggerPrimaryEvent();
        }
        else
        {
            TriggerDummyEvent();
        }
    }

    private void TriggerPrimaryEvent()
    {
        Debug.Log("[PDV2] TriggerPrimaryEvent called. isMotherLookingNow will be set to true.");
        currentDoorState = DoorOpenType.Full;
        isMotherLookingNow = true;

        if (realMotherObject != null)
        {
            realMotherObject.SetActive(true);
        }

        if (targetDoorController != null)
        {
            targetDoorController.SetDoorState(DoorController.DoorState.Full);
        }

        if (mainDoorOpenAudioSource != null)
        {
            mainDoorOpenAudioSource.Play();
        }

        if (motherGauge != null && motherGauge.currentGauge >= motherGauge.maxGauge)
        {
            OnPlayerCaught();
        }
        else if (!hasPermanentGameOver)
        {
            if (primaryResetCoroutine != null)
            {
                StopCoroutine(primaryResetCoroutine);
            }

            primaryResetCoroutine = StartCoroutine(HandlePrimaryResetSequence());
        }
    }

    private IEnumerator HandlePrimaryResetSequence()
    {
        float peekDuration = (warningSystem != null) ? warningSystem.GetScaledPeekDuration() : 2.5f;
        Debug.Log($"[PDV2] HandlePrimaryResetSequence: peek duration={peekDuration:F1}s");
        yield return new WaitForSeconds(peekDuration);

        if (hasPermanentGameOver || isCaught)
        {
            yield break;
        }

        if (mainDoorCloseAudioSource != null)
        {
            mainDoorCloseAudioSource.Play();
        }

        if (targetDoorController != null)
        {
            targetDoorController.SetDoorState(DoorController.DoorState.Closed);
            targetDoorController.SetParentVisible(false);
        }

        ResetCycle();

        if (warningSystem != null)
        {
            warningSystem.EndWarningSequence();
        }

        primaryResetCoroutine = null;
    }

    private void TriggerDummyEvent()
    {
        Debug.Log("[PDV2] TriggerDummyEvent called. isMotherLookingNow will be set to false.");
        currentDoorState = DoorOpenType.Peek;
        isMotherLookingNow = false;

        if (dummyMotherObject != null)
        {
            dummyMotherObject.SetActive(true);
        }

        if (targetDoorController != null)
        {
            targetDoorController.SetDoorState(DoorController.DoorState.Peek);
        }

        if (dummyDoorAudioSource != null)
        {
            dummyDoorAudioSource.Play();
        }

        if (dummyResetCoroutine != null)
        {
            StopCoroutine(dummyResetCoroutine);
        }

        dummyResetCoroutine = StartCoroutine(HandleDummySequence());
    }

    private IEnumerator HandleDummySequence()
    {
        float peekDuration = (warningSystem != null) ? warningSystem.GetScaledPeekDuration() : 2.5f;
        Debug.Log($"[PDV2] HandleDummySequence: peek duration={peekDuration:F1}s");
        yield return new WaitForSeconds(peekDuration);

        if (dummyMotherObject != null)
        {
            dummyMotherObject.SetActive(false);
        }

        if (mainDoorCloseAudioSource != null)
        {
            mainDoorCloseAudioSource.Play();
        }

        if (targetDoorController != null)
        {
            targetDoorController.SetDoorState(DoorController.DoorState.Closed);
            targetDoorController.SetParentVisible(false);
        }

        ResetCycle();

        if (warningSystem != null)
        {
            warningSystem.EndWarningSequence();
        }

        dummyResetCoroutine = null;
    }

    private void ResetCycle()
    {
        Debug.Log("[PDV2] ResetCycle called. Resetting gauge and state.");
        if (dummyResetCoroutine != null)
        {
            StopCoroutine(dummyResetCoroutine);
            dummyResetCoroutine = null;
        }

        if (primaryResetCoroutine != null)
        {
            StopCoroutine(primaryResetCoroutine);
            primaryResetCoroutine = null;
        }

        decimalGauge = 0f;

        if (motherGauge != null)
        {
            motherGauge.SetGaugeDirect(0);
        }

        if (firstFloorLight != null) firstFloorLight.SetActive(false);
        if (secondFloorLight1 != null) secondFloorLight1.SetActive(false);
        if (secondFloorLight2 != null) secondFloorLight2.SetActive(false);
        if (secondFloorLight3 != null) secondFloorLight3.SetActive(false);

        if (stairsAudioSource != null)
        {
            stairsAudioSource.Stop();
        }

        if (realMotherObject != null) realMotherObject.SetActive(false);
        if (dummyMotherObject != null) dummyMotherObject.SetActive(false);

        stage1Triggered = false;
        stage2Triggered = false;
        stage3Triggered = false;
        stage4Triggered = false;

        isMotherLookingNow = false;
        currentDoorState = DoorOpenType.None;

        if (targetDoorController != null)
        {
            targetDoorController.SetDoorState(DoorController.DoorState.Closed);
            targetDoorController.SetParentVisible(false);
        }

        if (approachController != null)
        {
            approachController.ResetApproach();
        }

        riseSpeed = Random.Range(minRiseSpeed, maxRiseSpeed);
        RandomizeMotherSpeeds();
    }

    private void OnPlayerCaught()
    {
        Debug.Log($"[PDV2] OnPlayerCaught called. gauge={decimalGauge}, parentIsLooking={isMotherLookingNow}");
        isCaught = true;
        isMotherLookingNow = true;
        Debug.LogError("GAME OVER: Caught by Mother!");
    }

    public void NotifyGameOver()
    {
        hasPermanentGameOver = true;
    }

    public void OnLoudItemTriggered()
    {
        if (!enableLoudItemFeature)
        {
            Debug.Log("Loud Item Feature is DISABLED");
            return;
        }

        stage4Triggered = true;

        if (rushInAudioSource != null)
        {
            rushInAudioSource.Play();
        }

        TriggerPrimaryEvent();
    }
}