using UnityEngine;
using System.Collections.Generic;

public class ItemPool : MonoBehaviour
{
    public static ItemPool Instance;

    public GameObject[] itemPrefabs;

    public int poolSize = 30;

    // プレハブごとにプールを管理
    private Dictionary<GameObject, List<GameObject>> pools = new Dictionary<GameObject, List<GameObject>>();

    void Awake()
    {
        Instance = this;

        // プレハブごとに poolSize 分プールを作成
        foreach (GameObject prefab in itemPrefabs)
        {
            pools[prefab] = new List<GameObject>();

            for (int i = 0; i < poolSize; i++)
            {
                GameObject obj = Instantiate(prefab);
                obj.SetActive(false);
                pools[prefab].Add(obj);
            }
        }
    }

    // スポーン時にランダムでプレハブを選んで返す
    public GameObject GetFromPool()
    {
        // ランダムなプレハブを選択
        GameObject prefab = itemPrefabs[Random.Range(0, itemPrefabs.Length)];
        List<GameObject> pool = pools[prefab];

        foreach (GameObject obj in pool)
        {
            if (!obj.activeInHierarchy)
            {
                obj.SetActive(true);
                return obj;
            }
        }

        // プールが枯渇したら新たに生成
        GameObject newObj = Instantiate(prefab);
        pool.Add(newObj);
        return newObj;
    }

    public void ReturnToPool(GameObject obj)
    {
        obj.SetActive(false);
    }
}