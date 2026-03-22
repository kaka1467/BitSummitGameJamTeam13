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
                // デバッグログを追加して呼び出し状況を確認
                Debug.Log($"Item.ApplyEffect: BGM triggered on '{other.gameObject.name}', AudioManager.Instance is {(AudioManager.Instance == null ? "null" : "present")}, bgmClip is {(bgmClip == null ? "null" : bgmClip.name)}, loopBgm={loopBgm}, bgmVolume={bgmVolume}");

                // AudioManager を使って BGM を再生。存在しなければ生成する。
                if (AudioManager.Instance == null)
                {
                    Debug.Log("Item.ApplyEffect: Creating AudioManager GameObject");
                    new GameObject("AudioManager").AddComponent<AudioManager>();
                }

                if (AudioManager.Instance == null)
                {
                    Debug.LogError("Item.ApplyEffect: AudioManager.Instance is still null after creation attempt.");
                    break;
                }

                if (bgmClip == null)
                {
                    Debug.LogWarning("Item.ApplyEffect: bgmClip is null — assign an AudioClip in the inspector.");
                    break;
                }

                float vol = Mathf.Clamp01(bgmVolume);
                Debug.Log($"Item.ApplyEffect: Calling PlayBGM for clip '{bgmClip.name}' (loop={loopBgm}, vol={vol})");
                AudioManager.Instance.PlayBGM(bgmClip, loopBgm, vol);
                break;
        }
    }
}