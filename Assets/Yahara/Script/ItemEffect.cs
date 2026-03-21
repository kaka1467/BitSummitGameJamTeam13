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
        // フィーバー中は、プレハブでマグネット無効に設定されているアイテムの効果を無視する
        if (GameManager.instance != null && GameManager.instance.IsFeverMagnetActive)
        {
            ItemMagnet im = GetComponent<ItemMagnet>();
            if ((item != null && !item.isMagnetable) || (im != null && !im.enabled))
            {
                // 効果を与えずにプールへ戻す
                if (ItemPool.Instance != null) ItemPool.Instance.ReturnToPool(gameObject);
                return;
            }
        }
        PlayerHealth health = other.GetComponent<PlayerHealth>()
                           ?? other.GetComponentInParent<PlayerHealth>();

        PlayerBoost boost = other.GetComponent<PlayerBoost>() ?? other.GetComponentInParent<PlayerBoost>();

        switch (item.itemType)
        {
            case ItemType.Carrot:
                GameManager.instance.AddScore(item.scoreAmount);
                break;

            case ItemType.Clover:
                if (health != null) health.Heal(item.healAmount);
                break;

            case ItemType.Enemy:
                if (health != null) health.TakeDamage(item.damageAmount);
                break;

            case ItemType.Clock:
                GameManager.instance.AddTime(item.timeAmount);
                break;

            case ItemType.Boost:
                if (boost == null)
                {
                    boost = other.gameObject.AddComponent<PlayerBoost>();
                }

                boost.StartBoost(item.boostDuration, item.boostMultiplier);
                break;

            case ItemType.HugeObstacle:
                if (boost != null && boost.IsBoosting)
                {
                    ItemPool.Instance.ReturnToPool(gameObject);
                    return;
                }

                if (QTEManager.Instance == null)
                {
                    new GameObject("QTEManager").AddComponent<QTEManager>();
                }

                bool started = QTEManager.Instance != null && QTEManager.Instance.StartHugeObstacleQte(success =>
                {
                    if (!success && health != null)
                    {
                        health.TakeDamage(item.damageAmount);
                    }
                });

                if (!started && health != null)
                {
                    health.TakeDamage(item.damageAmount);
                }
                break;

            case ItemType.Fever:
                GameManager.instance.AddFeverCount();
                break;
        }

        ItemPool.Instance.ReturnToPool(gameObject);
    }
}