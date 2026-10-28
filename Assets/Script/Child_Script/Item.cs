using UnityEngine;

public class Item : MonoBehaviour
{
    public ItemType itemType;

    public int scoreAmount = 10;
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
    // onFinished: HugeObstacle の場合、QTE の終了（成功・失敗・中断のいずれでも）後に呼ばれるコールバック。
    public void ApplyEffect(Collider2D other, System.Action onFinished = null)
    {
        Debug.Log($"[Item] ApplyEffect type={itemType} on other={other.gameObject.name}");
        var gm = GameManager.instance;
        PlayerBoost boost = other.GetComponent<PlayerBoost>() ?? other.GetComponentInParent<PlayerBoost>();

        // フィーバー中はサイレン（BGM／Loud Item）の効果を無効にする。
        //  ・減点・ダメージ・親機への通知・SE/BGM をすべて行わない（取得したアイテムはそのまま消える）。
        //  ・フィーバー終了後の取得は通常どおり有効。終了直後の保護時間は設けない。
        if (itemType == ItemType.BGM && gm != null && gm.IsFeverActive)
        {
            Debug.Log("[Item] フィーバー中のためサイレンの効果を無効にしました。");
            return;
        }

        TryPlayItemSE();

        switch (itemType)
        {
            case ItemType.Score:
                if (gm != null) gm.AddScore(scoreAmount);
                break;

            case ItemType.Clock:
                // 時計取得時にダメージアニメーション再生
                TriggerPlayerDamage(other);

                // 時計の減点（scoreAmount は Inspector で調整可能。負の値で減点）
                if (gm != null) gm.AddScore(scoreAmount);
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
                    onFinished?.Invoke();
                    return;
                }

                System.Action restoreObstacleAnimators = SetAnimatorsUnscaled(gameObject);

                if (QTEManager.Instance == null)
                {
                    new GameObject("QTEManager").AddComponent<QTEManager>();
                }

                bool started = QTEManager.Instance != null && QTEManager.Instance.StartHugeObstacleQte(outcome =>
                {
                    restoreObstacleAnimators?.Invoke();

                    // 失敗（Failure）のときだけ追加ダメージを与える（時間・得点の増減はしない）。
                    // 成功（Success）と中断（Cancelled：捕獲など）では効果を適用しない。
                    // QTEの報酬（成功時の加点）は QTEManager が付与する。
                    if (outcome == QteOutcome.Failure)
                    {
                        // QTE失敗時にダメージアニメーション再生
                        TriggerPlayerDamage(other);
                    }

                    // アイテムのプール返却はどの終了状態でも行う（中断でもプールへ戻す）
                    onFinished?.Invoke();
                });

                if (!started)
                {
                    restoreObstacleAnimators?.Invoke();
                    // QTEが開始できなかった場合もダメージアニメーション再生
                    TriggerPlayerDamage(other);
                    onFinished?.Invoke();
                }
                break;

            case ItemType.Fever:
                if (gm != null) gm.AddFeverCount();
                break;

            case ItemType.BGM:
                // メガホン取得時にダメージアニメーション再生
                TriggerPlayerDamage(other);

                // Loud Item の減点（scoreAmount は Inspector で調整可能。負の値で減点）
                //  ・音声処理や親機通知の成否にかかわらず、有効な取得1回につき1回だけ適用する
                //    （下の AudioManager／bgmClip 未設定による早期 break より前に置く）。
                if (gm != null) gm.AddScore(scoreAmount);

                // BGMアイテム（ラウドアイテム）を拾った時に親機へUDP通知を送る
                {
                    // 送信元は稼働中の子機Host（旧互換窓口へのフォールバックは作らない）。
                    // 取得できない場合は明示的に記録する（アイテム効果・得点・時間・QTE・SE/BGMの処理順序は変更しない）。
                    ChildUdpRuntimeHost udpHost = ChildUdpRuntimeHost.Running;
                    if (udpHost != null)
                    {
                        Debug.Log("[Item] BGM Item matched! Calling SendLoudItem().");
                        udpHost.SendLoudItem();
                    }
                    else
                    {
                        Debug.LogError("[Item] ChildUdpRuntimeHost is not running — LOUD_ITEM was not sent to the parent.");
                    }
                }
                
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

    private void TriggerPlayerDamage(Component target)
    {
        if (target == null) return;

        PlayerAnimator playerAnimator = target.GetComponent<PlayerAnimator>() ?? target.GetComponentInParent<PlayerAnimator>();
        if (playerAnimator != null)
        {
            playerAnimator.PlayDamage();
        }
    }

    private static System.Action SetAnimatorsUnscaled(GameObject target)
    {
        if (target == null) return null;

        Animator[] animators = target.GetComponentsInChildren<Animator>(true);
        if (animators == null || animators.Length == 0) return null;

        AnimatorUpdateMode[] updateModes = new AnimatorUpdateMode[animators.Length];
        for (int i = 0; i < animators.Length; i++)
        {
            if (animators[i] == null) continue;
            updateModes[i] = animators[i].updateMode;
            animators[i].updateMode = AnimatorUpdateMode.UnscaledTime;
        }

        return () =>
        {
            for (int i = 0; i < animators.Length; i++)
            {
                if (animators[i] == null) continue;
                animators[i].updateMode = updateModes[i];
            }
        };
    }
}