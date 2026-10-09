using UnityEngine;

public class ItemEffect : MonoBehaviour
{
    /// <summary>
    /// QTE 処理中は true。この間は ItemMove による移動・削除を止める。
    /// </summary>
    public bool IsQteLocked { get; private set; } = false;

    private void OnTriggerEnter2D(Collider2D other)
    {
        // プレイヤー本体だけでなく、子オブジェクトのコライダーに当たっても反応できるようにする
        GameObject hitObject = other.gameObject;

        bool isPlayer = hitObject.CompareTag("Player");
        if (!isPlayer)
        {
            Transform root = hitObject.transform.root;
            if (root != null && root.CompareTag("Player"))
            {
                isPlayer = true;
            }
        }

        if (!isPlayer) return;

        HandleCollect(other);
    }

    public void Collect(GameObject player)
    {
        if (player == null) return;
        Collider2D c = player.GetComponent<Collider2D>() ?? player.GetComponentInChildren<Collider2D>();
        if (c == null) return;
        HandleCollect(c);
    }

    private void HandleCollect(Collider2D other)
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

        // HugeObstacle は QTE 完了コールバック後にプールへ戻す
        if (item.itemType == ItemType.HugeObstacle)
        {
            IsQteLocked = true;
            item.ApplyEffect(other, onFinished: () =>
            {
                IsQteLocked = false;
                if (pool != null) pool.ReturnToPool(gameObject);
            });
            return;
        }

        // 効果ロジックは Item 側に委譲
        item.ApplyEffect(other);

        if (pool != null) pool.ReturnToPool(gameObject);
    }
}