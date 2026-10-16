using UnityEngine;

/// <summary>
/// GameOverPresentationController：
/// 捕獲確定から結果画面へ切り替わるまでの「音」と「UI表示」を1か所で受け持つ。
///
/// 役割（この範囲の書き込み元をこのスクリプトへ一本化する）：
///  ・捕獲確定（怪しさメーターMAX到達／OnPlayerCaught の共通到達点）：
///      MAX到達音を1回だけ再生し、怪しさメーター等を隠し、通常の心拍制御を止める。
///  ・ドア開け完了（Door_Openの正常完了＋ドア本体の全開、またはドアなしルートの最初の停止到達）：
///      赤段階と同じテンポの心拍を開始する。
///  ・Jumpscare実開始（ステート到達確認）：
///      専用効果音を1回だけ再生する。
///  ・中断／シーン遷移／再プレイ：
///      心拍と再生済みフラグを初期化する。
///
/// 設計方針：
///  ・UIのアルファ値／オブジェクト表示は SuspicionVisualFeedback が唯一の書き込み元。
///    ここはその公開APIを呼ぶだけで、UIの値を直接書かない（競合しない）。
///  ・心拍のテンポは SuspicionHeartbeat の redBeatInterval をそのまま使う（テンポ値はここに持たない）。
///  ・効果音は 2D（spatialBlend=0）で再生し、カメラ移動や母親との距離で小さくならないようにする。
///    既存の足音などの3D音設定には触れない。
///  ・Clip／参照が未設定でも、警告を出すだけで母親・カメラ演出は止めない。
/// </summary>
public class GameOverPresentationController : MonoBehaviour
{
    [Header("参照（未設定ならStart時に自動検索）")]
    [Tooltip("怪しさUIの表示担当。捕獲時のメーター非表示とDangerVignette保持をこのコンポーネントへ依頼します。")]
    [SerializeField] private SuspicionVisualFeedback visualFeedback;

    [Tooltip("心拍音の担当。捕獲からドア開け完了までは通常制御を止め、以降は赤段階と同じテンポで鳴らします。")]
    [SerializeField] private SuspicionHeartbeat heartbeat;

    // ゲームオーバー演出の効果音専用のAudioSource（Inspectorでは割り当てない＝内部で用意する）。
    //  ・2D（spatialBlend=0）で再生する：カメラ移動や母親との距離で音量が変わらないようにするため。
    //  ・他用途（足音など）のAudioSourceを流用しない：Stopや2D設定の変更で別の音へ影響させないため。
    private AudioSource gameOverAudioSource;

    [Header("怪しさメーターMAX到達音（捕獲確定で1回）")]
    [Tooltip("怪しさメーターが最大へ到達して捕獲が確定したときに1回だけ再生するClip。\n" +
             "未設定なら警告のみで、母親・カメラ演出はそのまま続きます。")]
    [SerializeField] private AudioClip maxGaugeReachedClip;

    [Tooltip("MAX到達音の音量。")]
    [SerializeField, Range(0f, 1f)] private float maxGaugeReachedVolume = 1f;

    [Header("ジャンプスケア音（Jumpscareステート到達で1回）")]
    [Tooltip("JumpScarePointでJumpscareステートへの到達を確認した時に1回だけ再生するClip。\n" +
             "未設定なら警告のみで、母親・カメラ演出はそのまま続きます。")]
    [SerializeField] private AudioClip jumpScareClip;

    [Tooltip("ジャンプスケア音の音量。")]
    [SerializeField, Range(0f, 1f)] private float jumpScareVolume = 1f;

    [Header("デバッグ")]
    [SerializeField] private bool showDebugLogs = true;

    // 捕獲1回ぶんの状態（Start / ResetForReplay で必ず戻す）。
    private bool _captured;
    private bool _maxSoundPlayed;
    private bool _jumpScareSoundPlayed;
    private bool _heartbeatStarted;

    /// <summary>この捕獲で既に捕獲確定処理を行ったか。</summary>
    public bool IsCaptured => _captured;

    /// <summary>
    /// 再プレイ／再初期化。捕獲・再生済みフラグを戻し、心拍を通常制御へ、UIを通常表示へ戻す。
    /// （結果画面から親機シーンへ戻ってきた場合に、音とUIの状態が残らないようにする）
    /// </summary>
    public void ResetForReplay()
    {
        _captured = false;
        _maxSoundPlayed = false;
        _jumpScareSoundPlayed = false;
        _heartbeatStarted = false;

        if (heartbeat != null)
            heartbeat.EndGameOverBeat();

        if (gameOverAudioSource != null)
            gameOverAudioSource.Stop();

        if (visualFeedback != null)
            visualFeedback.SetCapturedForGameOver(false);

        if (showDebugLogs)
            Debug.Log("[GameOverPresentation] 状態を初期化しました（捕獲フラグ・再生済みフラグ・心拍・UI表示）", this);
    }

    /// <summary>
    /// 【捕獲確定】MAX到達音／UI切り替え／通常心拍の停止を、同じ捕獲で1回だけ行う。
    ///  ・無敵などで捕獲が抑止された場合は呼ばれない（呼び出し元が抑止済み）。
    ///  ・毎フレームの「MAXである」ことを根拠に呼ばない（捕獲確定時のみ）。
    /// </summary>
    public void NotifyCaptured()
    {
        if (_captured)
        {
            if (showDebugLogs)
                Debug.Log("[GameOverPresentation] 既に捕獲確定済みのため、MAX到達音とUI切り替えは再実行しません（1捕獲につき1回）", this);
            return;
        }

        _captured = true;

        // UIの参照は「隠す前」に解決する（隠した後に検索させない）。
        ResolveReferences();

        // 1) 怪しさメーターMAX到達音（この捕獲で1回だけ）
        if (!_maxSoundPlayed)
        {
            _maxSoundPlayed = true;
            PlayOneShot(maxGaugeReachedClip, maxGaugeReachedVolume, "怪しさメーターMAX到達音");
        }

        // 2) UI：メーター／「怪しさ」表示を隠し、DangerVignette は結果画面まで保持する。
        if (visualFeedback != null)
        {
            visualFeedback.SetCapturedForGameOver(true);
        }
        else
        {
            Debug.LogWarning("[GameOverPresentation] 【設定不足】SuspicionVisualFeedback が見つからないため、" +
                             "怪しさメーターの非表示を行いません（DangerVignetteの表示はそのまま。演出は継続します）", this);
        }

        // 3) 心拍：捕獲からドア開け完了までは通常の心拍制御を止める（鳴らさない）。
        if (heartbeat != null)
        {
            heartbeat.SuppressNormalBeatForGameOver();
        }
        else
        {
            Debug.LogWarning("[GameOverPresentation] 【設定不足】SuspicionHeartbeat が見つからないため、" +
                             "捕獲後の心拍制御（停止／再開）を行いません（演出は継続します）", this);
        }

        if (showDebugLogs)
            Debug.Log("[GameOverPresentation] 捕獲確定：MAX到達音・UI非表示（Danger保持）・通常心拍停止を適用しました", this);
    }

    private void Start()
    {
        ResolveReferences();
        ResetForReplay();
    }

    /// <summary>
    /// 【ドア開け完了】Door_Openの正常完了とドア本体の全開が確認できた時点で、
    /// 赤段階と同じテンポの心拍を開始する（ドアなしルートでは最初の停止到達で呼ばれる）。
    ///  ・ドアを押す瞬間やカメラ開始通知では呼ばない。
    ///  ・二重開始しない（この捕獲で1回だけ）。
    /// </summary>
    public void NotifyDoorOpenCompleted()
    {
        if (_heartbeatStarted)
        {
            if (showDebugLogs)
                Debug.Log("[GameOverPresentation] 心拍は既に開始済みのため、再開始しません（二重再生防止）", this);
            return;
        }

        _heartbeatStarted = true;

        if (heartbeat == null)
        {
            Debug.LogWarning("[GameOverPresentation] 【設定不足】SuspicionHeartbeat が見つからないため、" +
                             "捕獲後の心拍を開始できません（演出は継続します）", this);
            return;
        }

        heartbeat.BeginGameOverBeat();

        if (showDebugLogs)
            Debug.Log("[GameOverPresentation] ドア開け完了：赤段階と同じテンポで心拍を開始しました", this);
    }

    /// <summary>
    /// 【Jumpscare実開始】Jumpscareステートへの到達を確認した時点で、専用効果音を1回だけ再生する。
    ///  ・Trigger発火だけでは鳴らさない（呼び出し元がステート到達を確認してから呼ぶ）。
    ///  ・心拍は止めない（結果画面へ切り替わるまで維持する）。
    /// </summary>
    public void NotifyJumpScareStarted()
    {
        if (_jumpScareSoundPlayed)
        {
            if (showDebugLogs)
                Debug.Log("[GameOverPresentation] ジャンプスケア音は既に再生済みのため、再再生しません（1捕獲につき1回）", this);
            return;
        }

        _jumpScareSoundPlayed = true;
        PlayOneShot(jumpScareClip, jumpScareVolume, "ジャンプスケア音");
    }

    /// <summary>
    /// 【中断／演出終了】心拍を停止して通常制御へ戻す。
    ///  ・正常完了では呼ばない（心拍は結果画面へ切り替わるまで維持する）。
    ///  ・UIは戻さない（捕獲後は結果画面まで非表示のまま）。
    /// </summary>
    public void NotifySequenceEnded(string reason)
    {
        if (heartbeat != null)
            heartbeat.EndGameOverBeat();

        if (showDebugLogs)
            Debug.Log($"[GameOverPresentation] 演出終了（理由={reason}）：心拍を停止し通常制御へ戻しました", this);
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  内部
    // ──────────────────────────────────────────────────────────────────────────

    private void ResolveReferences()
    {
        if (visualFeedback == null)
            visualFeedback = Object.FindFirstObjectByType<SuspicionVisualFeedback>();

        if (heartbeat == null)
            heartbeat = Object.FindFirstObjectByType<SuspicionHeartbeat>();

        if (gameOverAudioSource == null)
            gameOverAudioSource = Create2DAudioSource();
    }

    /// <summary>
    /// ゲームオーバー効果音専用の 2D AudioSource を新しく用意する（このGameObjectへ追加）。
    ///  ・2D（spatialBlend=0）／Play On Awake OFF／Loop OFF。距離で音量が変わらない。
    ///  ・既存のAudioSource（足音など）を流用しない：設定変更やStopで別の音へ影響させないため。
    /// </summary>
    private AudioSource Create2DAudioSource()
    {
        if (gameOverAudioSource != null) return gameOverAudioSource;

        // 既存コンポーネントを取得せず、必ず専用のAudioSourceを追加する。
        AudioSource source = gameObject.AddComponent<AudioSource>();

        source.playOnAwake = false;
        source.loop = false;
        source.spatialBlend = 0f;   // 2D：位置による減衰なし
        source.dopplerLevel = 0f;
        source.volume = 1f;         // 音量は PlayOneShot の volumeScale で指定する

        if (showDebugLogs)
            Debug.Log($"[GameOverPresentation] 効果音専用の2D AudioSourceを用意しました（{gameObject.name}）。" +
                      "Clipと音量はInspectorで設定してください", this);

        return source;
    }

    private void PlayOneShot(AudioClip clip, float volume, string label)
    {
        if (clip == null)
        {
            Debug.LogWarning($"[GameOverPresentation] 【設定不足】{label} のAudioClipが未設定のため再生しません（演出は継続します）", this);
            return;
        }

        if (gameOverAudioSource == null)
        {
            Debug.LogWarning($"[GameOverPresentation] 【設定不足】AudioSourceが無いため {label} を再生できません（演出は継続します）", this);
            return;
        }

        gameOverAudioSource.PlayOneShot(clip, Mathf.Clamp01(volume));
        Debug.Log($"[GameOverPresentation] {label} を再生: clip='{clip.name}' / 音量={Mathf.Clamp01(volume):F2}", this);
    }

    private void OnDisable()
    {
        // シーン遷移・無効化で音と心拍モードを残さない（UIは結果画面まで非表示のままにする）。
        if (gameOverAudioSource != null)
            gameOverAudioSource.Stop();

        if (heartbeat != null)
            heartbeat.EndGameOverBeat();
    }
}
