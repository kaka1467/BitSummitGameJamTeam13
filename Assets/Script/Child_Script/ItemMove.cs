using UnityEngine;

public class ItemMove : MonoBehaviour
{
    // 元の速度（Inspector値）。得点段階倍率はこの値へ掛け戻さない（プール再利用で累積させない）。
    public float speed = 0.5f;

    /// <summary>
    /// 実効速度＝元の速度 × 得点段階倍率（GameManager不在時・チュートリアル中は元の速度のまま）。
    /// 参照のたびに計算するため、プール返却と再取得を繰り返しても倍率は累積しない。
    /// すでに画面にいるアイテムにも新規出現アイテムにも同じ倍率が反映される。
    /// </summary>
    public float EffectiveSpeed => speed * GameManager.CurrentScoreSpeedMultiplier;

    [SerializeField]
    private float deleteOffsetFromLeft = 1f; // 画面左端からどれだけ外に出たら消すか

    void Update()
    {
        // QTE 処理中は移動・削除を止める
        ItemEffect effect = GetComponent<ItemEffect>();
        if (effect != null && effect.IsQteLocked) return;

        // 移動は得点段階倍率を反映した実効速度で行う（QTE中は Time.deltaTime が0なので従来どおり止まる）。
        transform.position += Vector3.left * EffectiveSpeed * Time.deltaTime;

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