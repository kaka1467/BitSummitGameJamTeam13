using UnityEngine;

public class ItemEffect : MonoBehaviour
{
    private void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag("Player")) return;

        Item item = GetComponent<Item>();
        PlayerHealth health = other.GetComponent<PlayerHealth>()
                           ?? other.GetComponentInParent<PlayerHealth>();

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
        }

        ItemPool.Instance.ReturnToPool(gameObject);
    }
}