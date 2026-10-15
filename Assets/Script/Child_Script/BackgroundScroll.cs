using UnityEngine;

public class BackgroundScroll : MonoBehaviour
{
    // 元の背景速度（Inspector値）。得点段階倍率はこの値へ掛け戻さない。
    public float speed = 0.05f;
    // Boost中の背景倍率（従来どおり別枠で維持する）
    [SerializeField] private float boostedSpeedMultiplier = 2f;

    private Renderer rend;
    private PlayerBoost playerBoost;
    private float currentOffset;

    void Start()
    {
        rend = GetComponent<Renderer>();

        GameObject player = GameObject.FindWithTag("Player");
        if (player != null)
        {
            playerBoost = player.GetComponent<PlayerBoost>() ?? player.GetComponentInParent<PlayerBoost>();
        }
    }

    void Update()
    {
        if (rend == null) return;

        if (playerBoost == null)
        {
            GameObject player = GameObject.FindWithTag("Player");
            if (player != null)
            {
                playerBoost = player.GetComponent<PlayerBoost>() ?? player.GetComponentInParent<PlayerBoost>();
            }
        }

        // 背景速度＝元の速度 × 得点段階倍率 × Boost倍率。
        //  ・得点段階倍率は GameManager の表から選ばれた絶対値（GameManager不在時・チュートリアル中は1.0）。
        //  ・Boost倍率は従来どおり別枠（得点倍率と同時なら両方を掛ける）。
        //  ・フィーバー由来の加速は行わない（フィーバーは得点2倍のみ）。
        //  ・「残り60秒以下で背景2倍」は廃止した（時間を理由に背景が加速しない）。
        float scoreMultiplier = GameManager.CurrentScoreSpeedMultiplier;
        float boostMultiplier = (playerBoost != null && playerBoost.IsBoosting) ? boostedSpeedMultiplier : 1f;
        float multiplier = scoreMultiplier * boostMultiplier;
        currentOffset += Time.deltaTime * speed * multiplier;
        rend.material.mainTextureOffset = new Vector2(currentOffset, 0f);
    }
}