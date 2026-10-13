using UnityEngine;

public class BackgroundScroll : MonoBehaviour
{
    public float speed = 0.05f;
    [SerializeField] private float boostedSpeedMultiplier = 2f;
    [SerializeField, Min(0f)] private float doubleSpeedRemainingTime = 60f;
    [SerializeField, Min(0f)] private float doubleSpeedMultiplier = 2f;

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

        float boostMultiplier = (playerBoost != null && playerBoost.IsBoosting) ? boostedSpeedMultiplier : 1f;
        float timerMultiplier = (GameManager.instance != null && GameManager.instance.time <= doubleSpeedRemainingTime) ? doubleSpeedMultiplier : 1f;
        float multiplier = boostMultiplier * timerMultiplier;
        currentOffset += Time.deltaTime * speed * multiplier;
        rend.material.mainTextureOffset = new Vector2(currentOffset, 0f);
    }
}