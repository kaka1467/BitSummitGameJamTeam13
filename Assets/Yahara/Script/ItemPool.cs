using UnityEngine;
using System.Collections.Generic;
using System;

public class ItemPool : MonoBehaviour
{
    public static ItemPool Instance;

    public GameObject[] itemPrefabs;

    public int poolSize = 30;

    // プレハブごとにプールを管理
    private Dictionary<GameObject, List<GameObject>> pools = new Dictionary<GameObject, List<GameObject>>();
    private readonly List<GameObject> validPrefabs = new List<GameObject>();

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        RebuildPools();
    }

    private void RebuildPools()
    {
        pools.Clear();
        validPrefabs.Clear();

        if (itemPrefabs == null || itemPrefabs.Length == 0)
        {
            Debug.LogError("ItemPool: itemPrefabs is empty. Assign at least one prefab in Inspector.");
            return;
        }

        // プレハブごとに poolSize 分プールを作成
        foreach (GameObject prefab in itemPrefabs)
        {
            if (prefab == null)
            {
                Debug.LogWarning("ItemPool: itemPrefabs contains Missing/Null entry. Skipping.");
                continue;
            }

            if (pools.ContainsKey(prefab))
            {
                Debug.LogWarning($"ItemPool: Duplicate prefab '{prefab.name}' found in itemPrefabs. Skipping duplicate.");
                continue;
            }

            pools[prefab] = new List<GameObject>();
            validPrefabs.Add(prefab);

            for (int i = 0; i < Mathf.Max(0, poolSize); i++)
            {
                try
                {
                    GameObject obj = Instantiate(prefab);
                    ConfigureMagnet(obj, prefab);
                    obj.SetActive(false);
                    pools[prefab].Add(obj);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"ItemPool: Failed to instantiate prefab '{prefab.name}'. {ex.Message}");
                    RemovePrefab(prefab);
                    break;
                }
            }
        }

        if (validPrefabs.Count == 0)
        {
            Debug.LogError("ItemPool: No valid prefabs available after validation.");
        }
    }

    private void ConfigureMagnet(GameObject obj, GameObject prefab)
    {
        // 最初はフィーバー中のアイテムが引き寄せられるのを全て非アクティブ
        Item itemComp = prefab.GetComponent<Item>();
        bool magnetable = (itemComp == null) ? true : itemComp.isMagnetable;

        if (magnetable)
        {
            if (obj.GetComponent<ItemMagnet>() == null) obj.AddComponent<ItemMagnet>();
        }
        else
        {
            ItemMagnet magnet = obj.GetComponent<ItemMagnet>();
            if (magnet != null) magnet.enabled = false;
        }
    }

    private void RemovePrefab(GameObject prefab)
    {
        validPrefabs.Remove(prefab);
        pools.Remove(prefab);
    }

    private bool TryGetPool(GameObject prefab, out List<GameObject> pool)
    {
        pool = null;
        if (prefab == null) return false;

        if (!pools.TryGetValue(prefab, out pool))
        {
            pool = new List<GameObject>();
            pools[prefab] = pool;
            if (!validPrefabs.Contains(prefab)) validPrefabs.Add(prefab);
        }

        return true;
    }

    // スポーン時にランダムでプレハブを選んで返す
    public GameObject GetFromPool()
    {
        if (validPrefabs.Count == 0)
        {
            RebuildPools();
            if (validPrefabs.Count == 0) return null;
        }

        // ランダムなプレハブを選択
        GameObject prefab = validPrefabs[UnityEngine.Random.Range(0, validPrefabs.Count)];
        if (!TryGetPool(prefab, out List<GameObject> pool)) return null;

        foreach (GameObject obj in pool)
        {
            if (obj != null && !obj.activeInHierarchy)
            {
                // prefab の設定に応じてアイテムがプレイヤーに向かうのを有効/無効にする
                ConfigureMagnet(obj, prefab);

                obj.SetActive(true);
                return obj;
            }
        }

        // プールが枯渇したら新たに生成
        try
        {
            GameObject newObj = Instantiate(prefab);
            ConfigureMagnet(newObj, prefab);
            pool.Add(newObj);
            newObj.SetActive(true);
            return newObj;
        }
        catch (Exception ex)
        {
            Debug.LogError($"ItemPool.GetFromPool: failed to instantiate '{prefab.name}'. {ex.Message}");
            RemovePrefab(prefab);
            return null;
        }
    }

    public void ReturnToPool(GameObject obj)
    {
        if (obj == null) return;
        obj.SetActive(false);
    }

    // 指定した ItemType を持つアイテムを確実に取得するためのAPI
    public GameObject GetFromPoolByItemType(ItemType itemType)
    {
        if (validPrefabs.Count == 0)
        {
            RebuildPools();
            if (validPrefabs.Count == 0) return null;
        }

        // 対応するプレハブを探す
        GameObject targetPrefab = null;
        foreach (GameObject prefab in validPrefabs)
        {
            if (prefab == null) continue;
            Item itemComp = prefab.GetComponent<Item>();
            if (itemComp != null && itemComp.itemType == itemType)
            {
                targetPrefab = prefab;
                break;
            }
        }

        if (targetPrefab == null)
        {
            return null;
        }

        if (!TryGetPool(targetPrefab, out List<GameObject> pool)) return null;

        // 既存の非アクティブなオブジェクトを探す
        foreach (GameObject obj in pool)
        {
            if (obj != null && !obj.activeInHierarchy)
            {
                // マグネット設定をプレハブに合わせて反映
                ConfigureMagnet(obj, targetPrefab);

                obj.SetActive(true);
                return obj;
            }
        }

        // 全て使用中なら新しく生成
        try
        {
            GameObject newObj = Instantiate(targetPrefab);
            ConfigureMagnet(newObj, targetPrefab);

            pool.Add(newObj);
            newObj.SetActive(true);
            return newObj;
        }
        catch (Exception ex)
        {
            Debug.LogError($"ItemPool.GetFromPoolByItemType: failed to instantiate '{targetPrefab.name}'. {ex.Message}");
            RemovePrefab(targetPrefab);
            return null;
        }
    }
}