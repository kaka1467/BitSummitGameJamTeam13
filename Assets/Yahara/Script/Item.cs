using UnityEngine;

public class Item : MonoBehaviour
{
    public ItemType itemType;

    public int scoreAmount = 10;
    public int healAmount = 10;

    public int damageAmount = 10;
    public float timeAmount = 10f;

    public float boostDuration = 0f;
    public float boostMultiplier = 0f;

    // このアイテムがフィーバー時の影響を受けるか
    public bool isMagnetable = true;

    // BGM用設定
    public AudioClip bgmClip;
    public bool loopBgm = true;
    public float bgmVolume = 1f;

    // アイテムの効果を適用するメソッド。ItemEffect はトリガー専任となり、このメソッドに処理を委譲する。
    public void ApplyEffect(Collider other)
    {
        var gm = GameManager.instance;
        PlayerHealth health = other.GetComponent<PlayerHealth>() ?? other.GetComponentInParent<PlayerHealth>();
        PlayerBoost boost = other.GetComponent<PlayerBoost>() ?? other.GetComponentInParent<PlayerBoost>();

        switch (itemType)
        {
            case ItemType.Carrot:
            case ItemType.Clover:
                if (health != null) health.Heal(healAmount);
                if (gm != null) gm.AddScore(scoreAmount);
                break;

            case ItemType.Enemy:
                if (health != null) health.TakeDamage(damageAmount);
                break;

            case ItemType.Clock:
                if (gm != null) gm.AddTime(timeAmount);
                break;

            case ItemType.Boost:
                if (boost == null)
                {
                    boost = other.gameObject.AddComponent<PlayerBoost>();
                }

                if (boost != null) boost.StartBoost(boostDuration, boostMultiplier);
                break;

            case ItemType.HugeObstacle:
                // ブースト中は障害物判定を無視
                if (boost != null && boost.IsBoosting)
                {
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
                        health.TakeDamage(damageAmount);
                    }
                });

                if (!started && health != null)
                {
                    health.TakeDamage(damageAmount);
                }
                break;

            case ItemType.Fever:
                if (gm != null) gm.AddFeverCount();
                break;

            case ItemType.BGM:
                // AudioManager を使って BGM を再生。存在しなければ生成する。
                if (AudioManager.Instance == null)
                {
                    new GameObject("AudioManager").AddComponent<AudioManager>();
                }

                if (AudioManager.Instance != null && bgmClip != null)
                {
                    AudioManager.Instance.PlayBGM(bgmClip, loopBgm, Mathf.Clamp01(bgmVolume));
                }
                break;
        }
    }
}