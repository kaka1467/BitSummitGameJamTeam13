using System.Collections;
using System.Collections.Generic;
using DG.Tweening;
using TMPro;
using UnityEngine;

public class TutorialFlow : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private PlayerMove playerMove;
    [SerializeField] private ItemSpawner itemSpawner;
    [SerializeField] private GameManager gameManager;
    [SerializeField] private TextMeshProUGUI countdownText;
    [SerializeField] private TextMeshProUGUI startText;

    [Header("Settings")]
    [SerializeField] private bool runOnStart = true;
    [SerializeField] private bool disableGameManagerDuringTutorial = true;
    [SerializeField] private float initialDelay = 0.5f;

    [Header("Auto Collect")]
    [SerializeField] private float collectTimeout = 5f;
    [SerializeField] private List<GameObject> tutorialItems = new List<GameObject>();

    [Header("Auto Avoid")]
    [SerializeField] private float avoidTimeout = 5f;
    [SerializeField] private List<GameObject> tutorialObstacles = new List<GameObject>();

    [Header("QTE")]
    [SerializeField] private float qteWaitTimeout = 12f;
    [SerializeField] private float afterQteDelay = 0.5f;

    [Header("Countdown")]
    [SerializeField] private int countdownFrom = 3;
    [SerializeField] private float countdownStepSeconds = 1f;
    [SerializeField] private float goDisplaySeconds = 0.5f;

    [Header("Start Text Animation")]
    [SerializeField] private Vector3 startScaleFrom = new Vector3(0.2f, 0.2f, 1f);
    [SerializeField] private Vector3 startScaleTo = new Vector3(1.2f, 1.2f, 1f);
    [SerializeField] private float startTweenSeconds = 0.35f;
    [SerializeField] private Ease startEase = Ease.OutBack;

    [Header("Auto Horizontal Move")]
    [SerializeField] private bool autoHorizontalEnabled = true;
    [SerializeField, Min(0f)] private float autoHorizontalRange = 1.5f;
    [SerializeField, Min(0.02f)] private float autoTargetUpdateSeconds = 0.05f;
    [SerializeField, Min(0f)] private float autoCollectCenterRange = 0.8f;

    private bool tutorialRunning;
    private Tween startTween;
    private Coroutine autoTargetRoutine;

    private enum AutoTargetMode
    {
        Collect,
        Avoid,
        Qte
    }

    private void Start()
    {
        if (runOnStart)
        {
            StartTutorial();
        }
    }

    public void StartTutorial()
    {
        if (tutorialRunning) return;
        StartCoroutine(TutorialRoutine());
    }

    private IEnumerator TutorialRoutine()
    {
        tutorialRunning = true;

        if (playerMove == null)
        {
            playerMove = FindFirstObjectByType<PlayerMove>();
        }

        if (itemSpawner == null)
        {
            itemSpawner = FindFirstObjectByType<ItemSpawner>();
        }

        if (gameManager == null)
        {
            gameManager = GameManager.instance;
        }

        if (playerMove == null || itemSpawner == null)
        {
            Debug.LogError("TutorialFlow: Missing PlayerMove or ItemSpawner.");
            tutorialRunning = false;
            yield break;
        }

        if (disableGameManagerDuringTutorial && gameManager != null)
        {
            gameManager.SetTutorialMode(true);
        }

        itemSpawner.SpawnEnabled = false;
        playerMove.SetInputEnabled(false);
        playerMove.SetAutoDrive(true);
        playerMove.SetAutoTargetX(null);
        playerMove.SetAutoHorizontalSpeed(playerMove.horizontalMoveSpeed);

        ClearActiveItems();

        if (countdownText != null)
        {
            countdownText.gameObject.SetActive(false);
            countdownText.text = string.Empty;
        }

        if (startText != null)
        {
            startText.gameObject.SetActive(false);
            startText.text = string.Empty;
        }

        if (initialDelay > 0f)
        {
            yield return new WaitForSecondsRealtime(initialDelay);
        }

        yield return RunAutoCollect();
        yield return RunAutoAvoid();
        ClearActiveItems();
        yield return RunQteStep();

        if (afterQteDelay > 0f)
        {
            yield return new WaitForSecondsRealtime(afterQteDelay);
        }

        yield return RunCountdown();

        playerMove.SetInputEnabled(true);
        playerMove.SetAutoDrive(false);
        playerMove.SetAutoTargetX(null);

        itemSpawner.SpawnEnabled = true;
        itemSpawner.RestartSchedule();

        if (disableGameManagerDuringTutorial && gameManager != null)
        {
            gameManager.SetTutorialMode(false);
            gameManager.ResetScoreAndFever();
        }

        tutorialRunning = false;
    }

    private IEnumerator RunAutoCollect()
    {
        ClearActiveItems();
        List<GameObject> items = tutorialItems != null && tutorialItems.Count > 0
            ? tutorialItems
            : new List<GameObject>();

        for (int i = 0; i < items.Count; i++)
        {
            GameObject prefab = items[i];
            if (prefab == null) continue;

            GameObject item = null;
            bool spawned = itemSpawner.TrySpawnByPrefab(prefab, out item);
            if (!spawned || item == null)
            {
                yield return new WaitForSecondsRealtime(0.2f);
                continue;
            }

            ClearActiveItemsExcept(item);

            yield return WaitForItemToEnterScreen(item, collectTimeout);

            int laneIndex = GetNearestLaneIndex(item.transform.position.y);
            playerMove.SetAutoLane(laneIndex);

            yield return TrackAutoTargetToItem(item, collectTimeout, AutoTargetMode.Collect);
        }
    }

    private IEnumerator RunAutoAvoid()
    {
        if (tutorialObstacles == null || tutorialObstacles.Count == 0) yield break;

        ClearActiveItems();

        for (int i = 0; i < tutorialObstacles.Count; i++)
        {
            GameObject prefab = tutorialObstacles[i];
            if (prefab == null) continue;

            GameObject obstacle = null;
            bool spawned = itemSpawner.TrySpawnByPrefab(prefab, out obstacle);
            if (!spawned || obstacle == null)
            {
                yield return new WaitForSecondsRealtime(0.2f);
                continue;
            }

            ClearActiveItemsExcept(obstacle);

            yield return WaitForItemToEnterScreen(obstacle, avoidTimeout);

            int obstacleLane = GetNearestLaneIndex(obstacle.transform.position.y);
            int safeLane = PickSafeLane(obstacleLane);
            playerMove.SetAutoLane(safeLane);

            yield return TrackAutoTargetToItem(obstacle, avoidTimeout, AutoTargetMode.Avoid);
        }
    }

    private IEnumerator RunQteStep()
    {
        ClearActiveItems();
        GameObject huge = null;
        if (!itemSpawner.TrySpawnHugeObstacle(out huge) || huge == null)
        {
            yield break;
        }

        ClearActiveItemsExcept(huge);

        yield return WaitForItemToEnterScreen(huge, qteWaitTimeout);

        int laneIndex = GetNearestLaneIndex(huge.transform.position.y);
        playerMove.SetAutoLane(laneIndex);

        bool qteDone = false;
        System.Action<bool> handler = _ => qteDone = true;
        QTEManager.HugeQteFinished += handler;

        float timer = 0f;
        float nextUpdate = 0f;
        float interval = Mathf.Max(0.02f, autoTargetUpdateSeconds);
        while (!qteDone && timer < qteWaitTimeout)
        {
            timer += Time.unscaledDeltaTime;
            if (autoHorizontalEnabled && huge != null && huge.activeInHierarchy && timer >= nextUpdate)
            {
                nextUpdate = timer + interval;
                UpdateAutoTargetForItem(huge, AutoTargetMode.Qte);
            }
            yield return null;
        }

        if (!qteDone && QTEManager.Instance != null && QTEManager.Instance.IsQteActive)
        {
            while (!qteDone)
            {
                yield return null;
            }
        }

        QTEManager.HugeQteFinished -= handler;
        playerMove.SetAutoTargetX(null);
    }

    private IEnumerator RunCountdown()
    {
        int start = Mathf.Max(1, countdownFrom);
        if (countdownText != null)
        {
            countdownText.gameObject.SetActive(true);
            for (int i = start; i > 0; i--)
            {
                countdownText.text = i.ToString();
                yield return new WaitForSecondsRealtime(countdownStepSeconds);
            }

            countdownText.gameObject.SetActive(false);

            if (startText != null)
            {
                startText.gameObject.SetActive(true);
                startText.text = "Start";
                RectTransform rect = startText.rectTransform;
                rect.localScale = startScaleFrom;
                startTween?.Kill();
                startTween = rect.DOScale(startScaleTo, startTweenSeconds)
                    .SetEase(startEase)
                    .SetUpdate(true);
                yield return new WaitForSecondsRealtime(goDisplaySeconds);
                startText.gameObject.SetActive(false);
                yield break;
            }

            yield return new WaitForSecondsRealtime(goDisplaySeconds);
            yield break;
        }

        float total = start * Mathf.Max(0f, countdownStepSeconds) + Mathf.Max(0f, goDisplaySeconds);
        if (total > 0f)
        {
            yield return new WaitForSecondsRealtime(total);
        }
    }

    private int GetNearestLaneIndex(float y)
    {
        if (itemSpawner == null || itemSpawner.lanesY == null || itemSpawner.lanesY.Length == 0) return 0;

        int bestIndex = 0;
        float bestDist = Mathf.Abs(itemSpawner.lanesY[0] - y);
        for (int i = 1; i < itemSpawner.lanesY.Length; i++)
        {
            float dist = Mathf.Abs(itemSpawner.lanesY[i] - y);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    private int PickSafeLane(int obstacleLane)
    {
        if (itemSpawner == null || itemSpawner.lanesY == null || itemSpawner.lanesY.Length == 0) return obstacleLane;

        int laneCount = itemSpawner.lanesY.Length;
        if (laneCount == 1) return obstacleLane;

        for (int i = 0; i < laneCount; i++)
        {
            int index = (obstacleLane + 1 + i) % laneCount;
            if (index != obstacleLane) return index;
        }

        return obstacleLane;
    }

    private void ClearActiveItems()
    {
        ClearActiveItemsExcept(null);
    }

    private void ClearActiveItemsExcept(GameObject keep)
    {
        ItemPool pool = ItemPool.Instance;
        Item[] items = FindObjectsByType<Item>(FindObjectsSortMode.None);
        foreach (Item item in items)
        {
            if (item == null) continue;
            GameObject obj = item.gameObject;
            if (obj == null || !obj.activeInHierarchy) continue;
            if (keep != null && obj == keep) continue;

            if (pool != null)
            {
                pool.ReturnToPool(obj);
            }
            else
            {
                obj.SetActive(false);
            }
        }
    }

    private IEnumerator WaitForItemToDeactivate(GameObject item, float timeout)
    {
        float timer = 0f;
        while (item != null && item.activeInHierarchy && timer < timeout)
        {
            timer += Time.unscaledDeltaTime;
            yield return null;
        }
    }

    private IEnumerator TrackAutoTargetToItem(GameObject item, float timeout, AutoTargetMode mode)
    {
        if (!autoHorizontalEnabled || playerMove == null || item == null)
        {
            yield return WaitForItemToDeactivate(item, timeout);
            yield break;
        }

        if (autoTargetRoutine != null)
        {
            StopCoroutine(autoTargetRoutine);
        }

        autoTargetRoutine = StartCoroutine(UpdateAutoTargetRoutine(item, timeout, mode));
        yield return autoTargetRoutine;
        autoTargetRoutine = null;
        playerMove.SetAutoTargetX(null);
    }

    private IEnumerator UpdateAutoTargetRoutine(GameObject item, float timeout, AutoTargetMode mode)
    {
        float timer = 0f;
        float interval = Mathf.Max(0.02f, autoTargetUpdateSeconds);
        float nextUpdate = 0f;

        while (item != null && item.activeInHierarchy && timer < timeout)
        {
            timer += Time.unscaledDeltaTime;
            if (timer >= nextUpdate)
            {
                nextUpdate = timer + interval;
                UpdateAutoTargetForItem(item, mode);
            }

            yield return null;
        }
    }

    private void UpdateAutoTargetForItem(GameObject item, AutoTargetMode mode)
    {
        if (playerMove == null || item == null)
        {
            return;
        }

        float targetX = item.transform.position.x;
        if (mode == AutoTargetMode.Avoid)
        {
            float playerX = playerMove.transform.position.x;
            float dir = playerX <= targetX ? -1f : 1f;
            targetX = playerX + dir * autoHorizontalRange;
            playerMove.SetAutoTargetX(targetX);
            return;
        }

        Camera cam = Camera.main;
        float centerX = cam != null ? cam.transform.position.x : 0f;
        if (Mathf.Abs(targetX - centerX) <= autoCollectCenterRange)
        {
            playerMove.SetAutoTargetX(targetX);
        }
        else
        {
            playerMove.SetAutoTargetX(null);
        }
    }

    private IEnumerator WaitForItemToEnterScreen(GameObject item, float timeout)
    {
        float timer = 0f;
        while (item != null && item.activeInHierarchy && timer < timeout)
        {
            if (IsItemInsideScreen(item))
            {
                yield break;
            }

            timer += Time.unscaledDeltaTime;
            yield return null;
        }
    }

    private bool IsItemInsideScreen(GameObject item)
    {
        if (item == null) return false;

        Camera cam = Camera.main;
        if (cam == null) return true;

        float halfHeight = cam.orthographicSize;
        float halfWidth = halfHeight * cam.aspect;
        float rightEdgeX = cam.transform.position.x + halfWidth;
        float leftEdgeX = cam.transform.position.x - halfWidth;

        float x = item.transform.position.x;
        return x <= rightEdgeX && x >= leftEdgeX;
    }
}
