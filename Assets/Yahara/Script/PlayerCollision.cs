using System.Buffers;
using UnityEngine;

public class PlayerCollision : MonoBehaviour
{
    PlayerHealth health;

    void Start()
    {
        health = GetComponent<PlayerHealth>();
    }

    void OnTriggerEnter2D(Collider2D other)
    {
        if (other.CompareTag("Carrot"))
        {
            GameManager.instance.AddScore(10);
            Destroy(other.gameObject);
        }

        if (other.CompareTag("Clover"))
        {
            health.Heal(10);
            Destroy(other.gameObject);
        }

        if (other.CompareTag("Enemy"))
        {
            health.TakeDamage(20);
            Destroy(other.gameObject);
        }
    }
}

