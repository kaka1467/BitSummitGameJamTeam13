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
                // 最初はフィーバー中のアイテムが引き寄せられるのを全て非アクティブ
                Item itemComp = prefab.GetComponent<Item>();
                bool magnetable = (itemComp == null) ? true : itemComp.isMagnetable;

                if (magnetable)
                {
                    if (obj.GetComponent<ItemMagnet>() == null) obj.AddComponent<ItemMagnet>();
                }
                else
                {
                    ItemMagnet m = obj.GetComponent<ItemMagnet>();
                    if (m != null) m.enabled = false;
                }

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
                // prefab の設定に応じてアイテムがプレイヤーに向かうのを有効/無効にする
                Item prefabItem = prefab.GetComponent<Item>();
                bool magnetable = (prefabItem == null) ? true : prefabItem.isMagnetable;

                ItemMagnet magnet = obj.GetComponent<ItemMagnet>();
                if (magnetable)
                {
                    if (magnet == null) obj.AddComponent<ItemMagnet>();
                    else magnet.enabled = true;
                }
                else
                {
                    if (magnet != null) magnet.enabled = false;
                }

                obj.SetActive(true);
                return obj;
            }
        }

        // プールが枯渇したら新たに生成
        GameObject newObj = Instantiate(prefab);
        Item prefabItem2 = prefab.GetComponent<Item>();
        bool magnetable2 = (prefabItem2 == null) ? true : prefabItem2.isMagnetable;
        if (magnetable2)
        {
            if (newObj.GetComponent<ItemMagnet>() == null) newObj.AddComponent<ItemMagnet>();
        }
        else
        {
            ItemMagnet m2 = newObj.GetComponent<ItemMagnet>();
            if (m2 != null) m2.enabled = false;
        }

        pool.Add(newObj);
        newObj.SetActive(true);
        return newObj;
    }

    public void ReturnToPool(GameObject obj)
    {
        obj.SetActive(false);
    }

    // 指定した ItemType を持つアイテムを確実に取得するためのAPI
    public GameObject GetFromPoolByItemType(ItemType itemType)
    {
        // 対応するプレハブを探す
        GameObject targetPrefab = null;
        foreach (GameObject prefab in itemPrefabs)
        {
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

        List<GameObject> pool = pools[targetPrefab];

        // 既存の非アクティブなオブジェクトを探す
        foreach (GameObject obj in pool)
        {
            if (!obj.activeInHierarchy)
            {
                // マグネット設定をプレハブに合わせて反映
                Item prefabItem = targetPrefab.GetComponent<Item>();
                bool magnetable = (prefabItem == null) ? true : prefabItem.isMagnetable;

                ItemMagnet magnet = obj.GetComponent<ItemMagnet>();
                if (magnetable)
                {
                    if (magnet == null) obj.AddComponent<ItemMagnet>();
                    else magnet.enabled = true;
                }
                else
                {
                    if (magnet != null) magnet.enabled = false;
                }

                obj.SetActive(true);
                return obj;
            }
        }

        // 全て使用中なら新しく生成
        GameObject newObj = Instantiate(targetPrefab);
        Item prefabItem2 = targetPrefab.GetComponent<Item>();
        bool magnetable2 = (prefabItem2 == null) ? true : prefabItem2.isMagnetable;
        if (magnetable2)
        {
            if (newObj.GetComponent<ItemMagnet>() == null) newObj.AddComponent<ItemMagnet>();
        }
        else
        {
            ItemMagnet m2 = newObj.GetComponent<ItemMagnet>();
            if (m2 != null) m2.enabled = false;
        }

        pool.Add(newObj);
        newObj.SetActive(true);
        return newObj;
    }
}