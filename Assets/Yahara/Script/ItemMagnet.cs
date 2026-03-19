using UnityEngine;

public class ItemMagnet : MonoBehaviour
{
    public float magnetSpeed = 12f;
    public float magnetRange = 10f;
    public float collectDistance = 1.2f;

    Transform player;
    ItemEffect effect;

    void Awake()
    {
        effect = GetComponent<ItemEffect>();
    }

    void Update()
    {
        if (GameManager.instance == null) return;
        if (!GameManager.instance.IsFeverMagnetActive) return;

        if (player == null)
        {
            GameObject p = GameObject.FindWithTag("Player");
            if (p == null) return;
            player = p.transform;
        }

        float dist = Vector3.Distance(transform.position, player.position);
        if (dist > magnetRange) return;

        transform.position = Vector3.MoveTowards(transform.position, player.position, magnetSpeed * Time.deltaTime);

        if (dist <= collectDistance)
        {
            if (effect != null)
            {
                effect.Collect(player.gameObject);
            }
            else
            {
                ItemPool.Instance.ReturnToPool(gameObject);
            }
        }
    }
}
