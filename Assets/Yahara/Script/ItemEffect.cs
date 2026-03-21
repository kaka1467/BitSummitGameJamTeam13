using UnityEngine;

public class ItemEffect : MonoBehaviour
{
    private void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag("Player")) return;
        HandleCollect(other);
    }

    public void Collect(GameObject player)
    {
        if (player == null) return;
        Collider c = player.GetComponent<Collider>() ?? player.GetComponentInChildren<Collider>();
        if (c == null) return;
        HandleCollect(c);
    }

    private void HandleCollect(Collider other)
    {
        Item item = GetComponent<Item>();
        // キャッシュと安全確認
        ItemMagnet im = GetComponent<ItemMagnet>();
        var gm = GameManager.instance;
        var pool = ItemPool.Instance;

        // item がない場合は効果を与えずにプールへ戻す
        if (item == null)
        {
            if (pool != null) pool.ReturnToPool(gameObject);
            return;
        }

        // フィーバー中は、プレハブでマグネット無効に設定されているアイテムの効果を無視する
        if (gm != null && gm.IsFeverMagnetActive)
        {
            if (!item.isMagnetable || (im != null && !im.enabled))
            {
                if (pool != null) pool.ReturnToPool(gameObject);
                return;
            }
        }

        // 効果ロジックは Item 側に委譲
        item.ApplyEffect(other);

        if (pool != null) pool.ReturnToPool(gameObject);
    }
}