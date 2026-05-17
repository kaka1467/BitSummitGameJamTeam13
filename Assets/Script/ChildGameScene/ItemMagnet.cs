using UnityEngine;

public class ItemMagnet : MonoBehaviour
{
    public float magnetRange = 7f;           // どのくらい近づいたら引き寄せを維持するか
    public float collectDistance = 0.05f;    // プレイヤーと重なった（回収）とみなす距離。0.02などの小スケールに合わせる[cite: 8]
    public float pixelsPerUnit = 256f;       // 1ユニットあたりのピクセル数。プロジェクトの設定(256)に合わせる[cite: 8]
    public float minMagnetSpeedPixels = 150f; // プレイヤーに極端に近づいた時の最低速度（ピクセル単位）
    public float maxMagnetSpeedPixels = 300f; // 引き寄せ開始時の最高速度（ピクセル単位）
    [SerializeField] private float screenEntryOffset = 0f;// 画面右端からどれだけ内側に入ったら引き寄せ開始するか（0なら画面右端で開始）

    Transform player;
    ItemEffect effect;
    private bool isMagnetizing = false;

    void Awake()
    {
        effect = GetComponent<ItemEffect>();
    }

    void OnEnable()
    {
        isMagnetizing = false;
    }

    void Update()
    {
    // フィーバー中でなければ何もしない
        if (GameManager.instance == null || !GameManager.instance.IsFeverMagnetActive) return;

        if (player == null)
        {
            GameObject p = GameObject.FindWithTag("Player");
            if (p == null) return;
            player = p.transform;
        }

        Camera cam = Camera.main;
        if (cam != null)
        {
            float halfWidth = cam.orthographicSize * cam.aspect;
            float leftEdgeX = cam.transform.position.x - halfWidth;
            if (transform.position.x < leftEdgeX) return;

            if (!isMagnetizing)
            {
                float rightEdgeX = cam.transform.position.x + halfWidth;
                if (transform.position.x < (rightEdgeX - screenEntryOffset))
                {
                    isMagnetizing = true;
                }
                return;
            }
        }

        Vector2 currentPos2D = new Vector2(transform.position.x, transform.position.y);
        Vector2 playerPos2D = new Vector2(player.position.x, player.position.y);
        float dist = Vector2.Distance(currentPos2D, playerPos2D);

        if (dist > magnetRange) return;

        // スピード計算 (PPU 256に基づいた計算)
        float t = Mathf.Clamp01(dist / magnetRange);
        float speedPixels = Mathf.Lerp(maxMagnetSpeedPixels, minMagnetSpeedPixels, t);
        float speedUnits = (speedPixels / pixelsPerUnit) * Time.deltaTime;

        // プレイヤーの方向へ移動（Z軸は現在の値を維持）
        // Vector3.MoveTowards を使い、移動量が距離を超えないようにする
        Vector3 targetPos = new Vector3(player.position.x, player.position.y, transform.position.z);
        transform.position = Vector3.MoveTowards(transform.position, targetPos, speedUnits);

        // 距離が極めて近くなった時だけ回収処理を呼ぶ
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
