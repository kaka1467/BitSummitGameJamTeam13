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

    private float allowedHugeSpawnTime;

    void Start()
    {
        allowedHugeSpawnTime = Time.time + hugeInitialDelay;
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
            allowedHugeSpawnTime = Time.time + hugeCooldownAfterQte;
        }
    }

    void SpawnItem()
    {
        if (ItemPool.Instance == null) return;

        GameObject item = ItemPool.Instance.GetFromPool();
        if (item == null) return;

        float y = 0f;
        Item itemComp = item.GetComponent<Item>();

        // Huge の出現が許可されているかを確認し、許可されていなければ別アイテムを試行する
        int attempts = 0;
        while (itemComp != null && itemComp.itemType == ItemType.HugeObstacle && Time.time < allowedHugeSpawnTime && attempts < maxAttemptsToAvoidHuge)
        {
            ItemPool.Instance.ReturnToPool(item);
            item = ItemPool.Instance.GetFromPool();
            if (item == null) return;
            itemComp = item.GetComponent<Item>();
            attempts++;
        }

        // まだHugeで許可されていない場合は戻す
        if (itemComp != null && itemComp.itemType == ItemType.HugeObstacle && Time.time < allowedHugeSpawnTime)
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
}