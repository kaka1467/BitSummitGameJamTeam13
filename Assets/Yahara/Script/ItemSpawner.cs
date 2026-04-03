using UnityEngine;

public class ItemSpawner : MonoBehaviour
{
    public float spawnInterval = 10.1f;

    // 画面右端からどれだけ外側に出現させるか
    [SerializeField]
    private float spawnOffsetFromRight = 1f;

    public float[] lanesY;
    [UnityEngine.SerializeField] private float hugeInitialDelay = 30f; // ゲーム開始後、この秒数まではHugeは出さない
    [UnityEngine.SerializeField] private float hugeCooldownAfterQte = 20f; // QTEクリア後のHugeスポーン猶予
    [UnityEngine.SerializeField] private int maxAttemptsToAvoidHuge = 5;

    // HugeObstacle 用の次回スポーン時刻（実時間ベース：QTE中も進む）
    private float nextHugeSpawnTime = -1f;
    private bool isHugeSpawnScheduled = false;

    void Start()
    {
        // 初回のHugeObstacleをゲーム開始から hugeInitialDelay 秒後にスポーンするよう予約（実時間）
        nextHugeSpawnTime = Time.unscaledTime + hugeInitialDelay;
        isHugeSpawnScheduled = true;
        InvokeRepeating(nameof(SpawnItem), 1f, spawnInterval);
    }

    private void OnEnable()
    {
        QTEManager.HugeQteFinished += OnHugeQteFinished;
    }

    private void OnDisable()
    {
        QTEManager.HugeQteFinished -= OnHugeQteFinished;
    }

    private void OnHugeQteFinished(bool success)
    {
        if (success)
        {
            // QTE成功時から hugeCooldownAfterQte 秒後に次のHugeObstacleをスポーンするよう予約（実時間）
            nextHugeSpawnTime = Time.unscaledTime + hugeCooldownAfterQte;
            isHugeSpawnScheduled = true;
        }
    }

    private void Update()
    {
        // HugeObstacle 専用のスポーンタイマー処理（ゲーム時間ベース）
        if (!isHugeSpawnScheduled)
            return;

        if (Time.unscaledTime >= nextHugeSpawnTime)
        {
            SpawnHugeObstacle();
            isHugeSpawnScheduled = false;
        }
    }

    void SpawnItem()
    {
        if (ItemPool.Instance == null) return;

        GameObject item = ItemPool.Instance.GetFromPool();
        if (item == null) return;

        float y = 0f;
        Item itemComp = item.GetComponent<Item>();
        var gm = GameManager.instance;
        bool feverActive = gm != null && gm.IsFeverMagnetActive;

        bool ShouldReject(Item comp)
        {
            if (comp == null) return false;
            if (comp.itemType == ItemType.HugeObstacle) return true;
            if (feverActive) return !comp.isMagnetable;

            return false;
        }

        // 通常スポーンでは HugeObstacle は出さず、別のアイテムを試行する
        int attempts = 0;
        while (ShouldReject(itemComp) && attempts < maxAttemptsToAvoidHuge)
        {
            ItemPool.Instance.ReturnToPool(item);
            item = ItemPool.Instance.GetFromPool();
            if (item == null) return;
            itemComp = item.GetComponent<Item>();
            attempts++;
        }

        // まだ条件に合わないなら今回はスポーンしない
        if (ShouldReject(itemComp))
        {
            ItemPool.Instance.ReturnToPool(item);
            return;
        }

        if (itemComp != null && itemComp.itemType == ItemType.HugeObstacle)
        {
            if (lanesY != null && lanesY.Length > 1)
            {
                y = lanesY[1];
            }
            else if (lanesY != null && lanesY.Length > 0)
            {
                y = lanesY[0];
            }
            else
            {
                y = 0f;
            }
        }
        else
        {
            if (lanesY != null && lanesY.Length > 0)
                y = lanesY[Random.Range(0, lanesY.Length)];
            else
                y = 0f;
        }

        // カメラの画面右端＋オフセットに出現させる
        float spawnXWorld = 0f;
        Camera cam = Camera.main;
        if (cam != null)
        {
            float halfHeight = cam.orthographicSize;
            float halfWidth = halfHeight * cam.aspect;
            spawnXWorld = cam.transform.position.x + halfWidth + spawnOffsetFromRight;
        }
        else
        {
            // カメラが取得できない場合のフォールバック
            spawnXWorld = 12f;
        }

        item.transform.position = new Vector3(spawnXWorld, y, 621.66f);
    }

    private void SpawnHugeObstacle()
    {
        if (ItemPool.Instance == null) return;

        // HugeObstacle 専用に、対応するアイテムだけを確実に取得
        GameObject item = ItemPool.Instance.GetFromPoolByItemType(ItemType.HugeObstacle);
        if (item == null) return;

        Item itemComp = item.GetComponent<Item>();
        if (itemComp == null || itemComp.itemType != ItemType.HugeObstacle)
        {
            ItemPool.Instance.ReturnToPool(item);
            return;
        }

        float y = 0f;
        if (lanesY != null && lanesY.Length > 1)
        {
            y = lanesY[1];
        }
        else if (lanesY != null && lanesY.Length > 0)
        {
            y = lanesY[0];
        }
        else
        {
            y = 0f;
        }

        float spawnXWorld = 0f;
        Camera cam = Camera.main;
        if (cam != null)
        {
            float halfHeight = cam.orthographicSize;
            float halfWidth = halfHeight * cam.aspect;
            spawnXWorld = cam.transform.position.x + halfWidth + spawnOffsetFromRight;
        }
        else
        {
            spawnXWorld = 12f;
        }

        item.transform.position = new Vector3(spawnXWorld, y, 621.66f);
    }
}