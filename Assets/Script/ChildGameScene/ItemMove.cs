using UnityEngine;

public class ItemMove : MonoBehaviour
{
    public float speed = 0.5f;
    [SerializeField]
    private float deleteOffsetFromLeft = 1f; // 画面左端からどれだけ外に出たら消すか

    void Update()
    {
        // QTE 処理中は移動・削除を止める
        ItemEffect effect = GetComponent<ItemEffect>();
        if (effect != null && effect.IsQteLocked) return;

        transform.position += Vector3.left * speed * Time.deltaTime;

        Camera cam = Camera.main;
        if (cam != null)
        {
            float halfHeight = cam.orthographicSize;
            float halfWidth = halfHeight * cam.aspect;
            float leftLimit = cam.transform.position.x - halfWidth - deleteOffsetFromLeft;

            if (transform.position.x < leftLimit)
            {
                ItemPool.Instance.ReturnToPool(gameObject);
            }
        }
        else
        {
            if (transform.position.x < -15f)
            {
                ItemPool.Instance.ReturnToPool(gameObject);
            }
        }
    }
}