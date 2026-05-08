using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

public class DoorController : MonoBehaviour
{
    // Public fields for Inspector assignment
    public Transform door; // The door transform that rotates
    public Transform parentModel; // The parent model transform to show/hide
    public KeyCode doorToggleKey = KeyCode.E; // Key to manually toggle door open/closed
    public KeyCode parentToggleKey = KeyCode.P; // Key to toggle parent visit mode
    public float closedAngle = 0f; // Door angle when closed (degrees)
    public float openAngle = 90f; // Door angle when open (degrees)
    public float openSpeed = 5f; // Speed of door opening/closing

    [Header("Parent Arrival Effects")]
    public AudioSource seSource;
    public AudioClip parentFootstepsClip;
    public AudioClip doorKnobClip;
    public GameObject doorLightLeak;
    public float footstepIntervalMin = 6f;
    public float footstepIntervalMax = 12f;
    public float knobIntervalMin = 8f;
    public float knobIntervalMax = 16f;
    public float lightLeakIntervalMin = 10f;
    public float lightLeakIntervalMax = 18f;
    public float gimmickMinSeparation = 0.75f;
    public float lightLeakDuration = 5f;

    // Auto parent visit settings
    public bool useAutoParent = true; // When true, parent arrival/departure is driven automatically
    public float parentIntervalMin = 5f; // Minimum seconds between parent visits
    public float parentIntervalMax = 10f; // Maximum seconds between parent visits

    // Internal state variables
    private bool isDoorOpen = false; // Current door state
    private bool isParentHere = false; // Whether parent is at the door
    private float parentTimer = 0f; // Internal timer for auto parent visits
    private float nextParentTime = 0f; // When the next parent visit should happen
    private float footstepTimer = 0f;
    private float nextFootstepTime = 0f;
    private float knobTimer = 0f;
    private float nextKnobTime = 0f;
    private float lightLeakTimer = 0f;
    private float nextLightLeakTime = 0f;
    private Coroutine lightLeakRoutine;
    private float lastGimmickTime = -999f;

    // Public read-only property so other scripts can know if the parent is here
    public bool IsParentHere => isParentHere;

    void Start()
    {
        if (seSource == null)
            seSource = GetComponent<AudioSource>();
        if (seSource == null)
            seSource = gameObject.AddComponent<AudioSource>();

        // Initialize parent model visibility
        if (parentModel != null)
            parentModel.gameObject.SetActive(isParentHere);

        SetLightLeak(false);

        // Schedule the first automatic parent visit
        ScheduleNextParentVisit();
        ScheduleNextFootstep();
        ScheduleNextKnob();
        ScheduleNextLightLeak();
    }

    void ScheduleNextParentVisit()
    {
        nextParentTime = Random.Range(parentIntervalMin, parentIntervalMax);
        parentTimer = 0f;
    }

    void ScheduleNextFootstep()
    {
        nextFootstepTime = Random.Range(footstepIntervalMin, footstepIntervalMax);
        footstepTimer = 0f;
    }

    void ScheduleNextKnob()
    {
        nextKnobTime = Random.Range(knobIntervalMin, knobIntervalMax);
        knobTimer = 0f;
    }

    void ScheduleNextLightLeak()
    {
        nextLightLeakTime = Random.Range(lightLeakIntervalMin, lightLeakIntervalMax);
        lightLeakTimer = 0f;
    }

    void ToggleParentPresence()
    {
        isParentHere = !isParentHere;
        isDoorOpen = isParentHere;
        if (parentModel != null)
            parentModel.gameObject.SetActive(isParentHere);
    }

    void OpenParentDoor()
    {
        isParentHere = true;
        isDoorOpen = true;
        if (parentModel != null)
            parentModel.gameObject.SetActive(true);
    }

    void SetLightLeak(bool isActive)
    {
        if (doorLightLeak != null)
            doorLightLeak.SetActive(isActive);
    }

    void PlayOneShot(AudioClip clip)
    {
        if (seSource != null && clip != null)
            seSource.PlayOneShot(clip);
    }

    bool CanTriggerGimmick()
    {
        return Time.time - lastGimmickTime >= gimmickMinSeparation;
    }

    void RegisterGimmickTriggered()
    {
        lastGimmickTime = Time.time;
    }

    void DelayGimmick(ref float nextTime, float timer)
    {
        float wait = gimmickMinSeparation - (Time.time - lastGimmickTime);
        wait = Mathf.Max(0f, wait);
        nextTime = timer + wait + Random.Range(0f, gimmickMinSeparation);
    }

    void Update()
    {
        // Check for manual door toggle (E key)
        if (Keyboard.current != null && Keyboard.current.eKey.wasPressedThisFrame)
        {
            isDoorOpen = !isDoorOpen; // Toggle door state
        }

        // Manual gimmick triggers (1/2/3 keys)
        if (Keyboard.current != null && Keyboard.current.digit1Key.wasPressedThisFrame)
        {
            PlayOneShot(parentFootstepsClip);
            ScheduleNextFootstep();
            RegisterGimmickTriggered();
        }
        if (Keyboard.current != null && Keyboard.current.digit2Key.wasPressedThisFrame)
        {
            PlayOneShot(doorKnobClip);
            ScheduleNextKnob();
            RegisterGimmickTriggered();
        }
        if (Keyboard.current != null && Keyboard.current.digit3Key.wasPressedThisFrame)
        {
            if (lightLeakRoutine != null)
                StopCoroutine(lightLeakRoutine);
            lightLeakRoutine = StartCoroutine(LightLeakRoutine());
            ScheduleNextLightLeak();
            RegisterGimmickTriggered();
        }

        // Parent visit handling
        if (useAutoParent)
        {
            parentTimer += Time.deltaTime;
            if (parentTimer >= nextParentTime)
            {
                ToggleParentPresence();
                ScheduleNextParentVisit();
            }
        }
        else if (Keyboard.current != null && Keyboard.current.pKey.wasPressedThisFrame)
        {
            ToggleParentPresence();
        }

        footstepTimer += Time.deltaTime;
        if (footstepTimer >= nextFootstepTime)
        {
            if (CanTriggerGimmick())
            {
                PlayOneShot(parentFootstepsClip);
                ScheduleNextFootstep();
                RegisterGimmickTriggered();
            }
            else
            {
                DelayGimmick(ref nextFootstepTime, footstepTimer);
            }
        }

        knobTimer += Time.deltaTime;
        if (knobTimer >= nextKnobTime)
        {
            if (CanTriggerGimmick())
            {
                PlayOneShot(doorKnobClip);
                ScheduleNextKnob();
                RegisterGimmickTriggered();
            }
            else
            {
                DelayGimmick(ref nextKnobTime, knobTimer);
            }
        }

        lightLeakTimer += Time.deltaTime;
        if (lightLeakTimer >= nextLightLeakTime)
        {
            if (CanTriggerGimmick())
            {
                if (lightLeakRoutine != null)
                    StopCoroutine(lightLeakRoutine);
                lightLeakRoutine = StartCoroutine(LightLeakRoutine());
                ScheduleNextLightLeak();
                RegisterGimmickTriggered();
            }
            else
            {
                DelayGimmick(ref nextLightLeakTime, lightLeakTimer);
            }
        }

        // Smoothly rotate the door towards the target angle
        if (door != null)
        {
            Quaternion targetRotation = Quaternion.Euler(0f, isDoorOpen ? openAngle : closedAngle, 0f);
            door.localRotation = Quaternion.Lerp(door.localRotation, targetRotation, Time.deltaTime * openSpeed);
        }
    }

    IEnumerator LightLeakRoutine()
    {
        SetLightLeak(true);
        yield return new WaitForSeconds(Mathf.Max(0f, lightLeakDuration));
        SetLightLeak(false);
        OpenParentDoor();
        lightLeakRoutine = null;
    }
}

/*
Setup Instructions:
1. Attach this script to an empty GameObject in your scene (e.g., name it "DoorManager").
2. In the Inspector, drag the door GameObject (the one that rotates) into the "Door" field.
3. Drag the parent model GameObject into the "Parent Model" field.
4. Adjust the angles and speed as needed (closedAngle=0, openAngle=90 for a 90-degree swing).
5. Test: Press E to manually open/close the door. When useAutoParent is enabled, the parent will automatically appear/disappear over time; otherwise, press P to toggle parent presence.
*/
