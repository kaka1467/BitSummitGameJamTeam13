using UnityEngine;

public class Item : MonoBehaviour
{
    public ItemType itemType;

    public int scoreAmount = 10;
    // 正なら加算、負なら減算。Enemy/HugeObstacle では減少量として扱われる。
    public float timeAmount = 10f;

    public float boostDuration = 0f;
    public float boostMultiplier = 0f;

    // このアイテムがフィーバー時の影響を受けるか
    public bool isMagnetable = true;

    // 効果音用設定
    public AudioClip seClip;
    public float seVolume = 1f;

    // BGM用設定
    public AudioClip bgmClip;
    public bool loopBgm = true;
    public float bgmVolume = 1f;

    // アイテムの効果を適用するメソッド。ItemEffect はトリガー専任となり、このメソッドに処理を委譲する。
    public void ApplyEffect(Collider2D other)
    {
        Debug.Log($"[Item] ApplyEffect type={itemType} on other={other.gameObject.name}");
        var gm = GameManager.instance;
        PlayerBoost boost = other.GetComponent<PlayerBoost>() ?? other.GetComponentInParent<PlayerBoost>();

        TryPlayItemSE();

        switch (itemType)
        {
            case ItemType.Score:
                if (gm != null) gm.AddScore(scoreAmount);
                break;

            // case ItemType.Enemy:
            //     if (gm != null) gm.AddTime(-Mathf.Abs(timeAmount));
            //     break;

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
                    // QTE をスキップした場合も、成功扱いとしてクールタイムを進める
                    QTEManager.RegisterHugeQteSuccess();
                    return;
                }

                if (QTEManager.Instance == null)
                {
                    new GameObject("QTEManager").AddComponent<QTEManager>();
                }

                bool started = QTEManager.Instance != null && QTEManager.Instance.StartHugeObstacleQte(success =>
                {
                    if (!success && gm != null)
                    {
                        gm.AddTime(-Mathf.Abs(timeAmount));
                    }
                });

                if (!started && gm != null)
                {
                    gm.AddTime(-Mathf.Abs(timeAmount));
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

    private void TryPlayItemSE()
    {
        if (seClip == null)
        {
            return;
        }

        if (AudioManager.Instance == null)
        {
            new GameObject("AudioManager").AddComponent<AudioManager>();
        }

        if (AudioManager.Instance == null)
        {
            return;
        }

        AudioManager.Instance.PlaySE(seClip, seVolume);
    }
}