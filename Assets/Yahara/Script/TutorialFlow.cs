using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

public class TutorialFlow : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private PlayerMove playerMove;
    [SerializeField] private ItemSpawner itemSpawner;
    [SerializeField] private GameManager gameManager;
    [SerializeField] private TextMeshProUGUI countdownText;

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

    private bool tutorialRunning;

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
            gameManager.enabled = false;
        }

        itemSpawner.SpawnEnabled = false;
        playerMove.SetInputEnabled(false);
        playerMove.SetAutoDrive(true);
        playerMove.SetAutoTargetX(null);

        ClearActiveItems();

        if (countdownText != null)
        {
            countdownText.gameObject.SetActive(false);
            countdownText.text = string.Empty;
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
            gameManager.enabled = true;
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

            yield return WaitForItemToDeactivate(item, collectTimeout);
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

            yield return WaitForItemToDeactivate(obstacle, avoidTimeout);
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
        while (!qteDone && timer < qteWaitTimeout)
        {
            timer += Time.unscaledDeltaTime;
            yield return null;
        }

        QTEManager.HugeQteFinished -= handler;
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

            yield return new WaitForSecondsRealtime(goDisplaySeconds);
            countdownText.gameObject.SetActive(false);
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
