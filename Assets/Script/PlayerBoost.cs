using System.Collections;
using UnityEngine;

public class PlayerBoost : MonoBehaviour
{
    public bool IsBoosting { get; private set; }

    // Current multiplier applied to speeds while boosting. Defaults to 1.
    public float CurrentMultiplier { get; private set; } = 1f;

    private Coroutine boostRoutine;

    public void StartBoost(float duration, float speedMultiplier)
    {
        if (boostRoutine != null)
        {
            StopCoroutine(boostRoutine);
            EndBoost();
        }

        boostRoutine = StartCoroutine(DoBoost(duration, speedMultiplier));
    }

    private IEnumerator DoBoost(float duration, float speedMultiplier)
    {
        IsBoosting = true;
        CurrentMultiplier = speedMultiplier;

        float endTime = Time.time + duration;
        while (Time.time < endTime)
        {
            yield return null;
        }

        EndBoost();
    }

    private void EndBoost()
    {
        IsBoosting = false;
        CurrentMultiplier = 1f;
        boostRoutine = null;
    }
}
