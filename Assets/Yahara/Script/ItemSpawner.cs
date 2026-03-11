using UnityEngine;

public class ItemSpawner : MonoBehaviour
{
    public float spawnInterval = 10.1f;

    public float spawnX = 12f;

    public float[] lanesY;

    void Start()
    {
        InvokeRepeating(nameof(SpawnItem), 1f, spawnInterval);
    }

    void SpawnItem()
    {
        GameObject item = ItemPool.Instance.GetFromPool();

        if (item == null) return;

        float y = lanesY[Random.Range(0, lanesY.Length)];

        item.transform.position = new Vector3(spawnX, y, 980.56f);
    }
}