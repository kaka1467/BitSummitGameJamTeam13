using UnityEngine;

public class ItemMove : MonoBehaviour
{
    public float speed = 5f;

    void Update()
    {
        transform.position += Vector3.left * speed * Time.deltaTime;

        if (transform.position.x < -15f)
        {
            ItemPool.Instance.ReturnToPool(gameObject);
        }
    }
}
