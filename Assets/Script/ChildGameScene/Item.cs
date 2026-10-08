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
    // onFinished: HugeObstacle の場合、QTE 完了（成功/失敗問わず）後に呼ばれるコールバック。
    public void ApplyEffect(Collider2D other, System.Action onFinished = null)
    {
        Debug.Log($"[Item] ApplyEffect type={itemType} on other={other.gameObject.name}");
        var gm = GameManager.instance;
        PlayerBoost boost = other.GetComponent<PlayerBoost>() ?? other.GetComponentInParent<PlayerBoost>();

        TryPlayItemSE();

        bool applyTimeOnCollect = itemType != ItemType.HugeObstacle;

        // 悪いアイテム判定：既存の分類は使ったまま、新しい分類は作らない。
        // 「時間減少効果を持つアイテム」＝ timeAmount が負の既存アイテム（DecrementClock など）。
        // HugeObstacle は timeAmount が 0 のためここで検出されない（QTE成功時は減少しない仕様のため）。
        NotifyBadItemIfNeeded();

        switch (itemType)
        {
            case ItemType.Score:
                if (gm != null) gm.AddScore(scoreAmount);
                break;

            // case ItemType.Enemy:
            //     if (gm != null) gm.AddTime(-Mathf.Abs(timeAmount));
            //     break;

            case ItemType.Clock:
                // タイマー取得時にダメージアニメーション再生
                TriggerPlayerDamage(other);
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

                bool started = QTEManager.Instance != null && QTEManager.Instance.StartHugeObstacleQte(success =>
                {
                    restoreObstacleAnimators?.Invoke();
                    if (!success)
                    {
                        // QTE失敗時にダメージアニメーション再生
                        TriggerPlayerDamage(other);
                        
                        // 時間を減らす
                        if (gm != null)
                        {
                            gm.AddTime(-Mathf.Abs(timeAmount));
                        }
                    }
                    // QTE 完了（成功・失敗どちらでも）を通知
                    onFinished?.Invoke();
                });

                if (!started)
                {
                    restoreObstacleAnimators?.Invoke();
                    // QTEが開始できなかった場合もダメージアニメーション再生
                    TriggerPlayerDamage(other);
                    if (gm != null)
                    {
                        gm.AddTime(-Mathf.Abs(timeAmount));
                    }
                    onFinished?.Invoke();
                }
                applyTimeOnCollect = false;
                break;

            case ItemType.Fever:
                if (gm != null) gm.AddFeverCount();
                break;

            case ItemType.BGM:
                // メガホン取得時にダメージアニメーション再生
                TriggerPlayerDamage(other);

                // BGMアイテム（ラウドアイテム）を拾った時に親機へUDP通知を送る
                {
                    ChildUdpReceiver udpReceiver = FindObjectOfType<ChildUdpReceiver>();
                    if (udpReceiver != null)
                    {
                        Debug.Log("[Item] BGM Item matched! Calling SendLoudItem().");
                        udpReceiver.SendLoudItem();
                    }
                    else
                    {
                        Debug.LogError("[Item] ChildUdpReceiver could not be found in the child game scene!");
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

        if (applyTimeOnCollect && gm != null && !Mathf.Approximately(timeAmount, 0f))
        {
            gm.AddTime(timeAmount);
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

    /// <summary>
    /// 悪いアイテム取得の通知を親機へ送る（片付け演出の視線抽選に使用）。
    ///   ・既存の ItemType 分類は変更しない（新しい分類を作らない）。
    ///   ・判定は「timeAmount &lt; 0」＝時間減少効果を持つ既存アイテムのみ。
    ///   ・アイテムの出現率・得点には一切影響しない（通知のみ）。
    ///   ・子機 UDP（ChildUdpReceiver）が見つからない場合は何もしない。
    /// </summary>
    private void NotifyBadItemIfNeeded()
    {
        if (timeAmount >= 0f) return;

        ChildUdpReceiver udpReceiver = FindFirstObjectByType<ChildUdpReceiver>();
        if (udpReceiver == null)
        {
            Debug.LogWarning("[Item] ChildUdpReceiver could not be found — BAD_ITEM notification skipped.");
            return;
        }

        Debug.Log($"[Item] 悪いアイテム取得（timeAmount={timeAmount}）— BAD_ITEM を親機へ送信");
        udpReceiver.SendBadItem();
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