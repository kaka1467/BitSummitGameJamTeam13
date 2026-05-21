using System.Collections;
using UnityEngine;

/// <summary>
/// プレイヤーのアニメーションを制御する。
/// - デフォルト：Runアニメーションをループ再生
/// - デバフアイテム取得時：Damageアニメーションを再生し、終了後にRunへ戻る
/// </summary>
[RequireComponent(typeof(Animator))]
public class PlayerAnimator : MonoBehaviour
{
    [Header("アニメーション設定")]
    [Tooltip("ダメージアニメーションの再生時間（秒）。Animatorクリップの長さに合わせて調整してください。")]
    public float damageDuration = 0.8f;

    [Header("ダメージSE設定")]
    [SerializeField] private AudioClip damageSeClip;
    [Range(0f, 1f)]
    [SerializeField] private float damageSeVolume = 1f;

    // Animator パラメータ名（Unityエディタ側のパラメータ名と一致させること）
    private static readonly int ParamIsDamage = Animator.StringToHash("isDamage");

    private Animator anim;
    private Coroutine damageRoutine;
    private AnimatorUpdateMode defaultUpdateMode;
    private bool pendingUpdateModeRestore;

    /// <summary>ダメージアニメーション再生中かどうか</summary>
    public bool IsDamaging => damageRoutine != null;

    // QTE中はダメージポーズを維持し、Run に戻らないようにするフラグ
    private bool _lockDamage;

    private void Awake()
    {
        anim = GetComponent<Animator>();
        defaultUpdateMode = anim.updateMode;
        anim.SetBool(ParamIsDamage, false);
    }

    private void OnEnable()
    {
        if (anim == null) return;
        anim.updateMode = defaultUpdateMode;
        pendingUpdateModeRestore = false;

        // ダメージ再生中であれば isDamage を維持する。
        // レーン変更などで Animator が再有効化されても Run へ戻らないようにする。
        anim.SetBool(ParamIsDamage, IsDamaging);
    }

    private void Update()
    {
        if (pendingUpdateModeRestore && Time.timeScale > 0f)
        {
            anim.updateMode = defaultUpdateMode;
            pendingUpdateModeRestore = false;
        }
    }

    /// <summary>
    /// QTE中にダメージポーズをロックする。
    /// lock=true の間は DamageRoutine が終わっても isDamage が false に戻らない。
    /// QTEクリア時に lock=false を渡してポーズを解除する。
    /// </summary>
    public void SetDamageLock(bool lockDamage)
    {
        _lockDamage = lockDamage;

        // ロック解除時、かつダメージコルーチンが終了済みであれば Run へ戻す
        if (!_lockDamage && !IsDamaging)
        {
            anim.SetBool(ParamIsDamage, false);
        }
    }

    private void OnDisable()
    {
        _lockDamage = false;

        if (damageRoutine != null)
        {
            StopCoroutine(damageRoutine);
            damageRoutine = null;
        }

        if (anim != null)
        {
            anim.updateMode = defaultUpdateMode;
            anim.SetBool(ParamIsDamage, false);
        }
    }

    public void PlayDamage()
    {
        // 既にダメージ演出中であればキャンセルして再開するなどの制御も可能です
        if (damageRoutine != null)
        {
            StopCoroutine(damageRoutine);
            damageRoutine = null;
        }

        TryPlayDamageSe();

        damageRoutine = StartCoroutine(DamageRoutine());
    }

    private void TryPlayDamageSe()
    {
        if (damageSeClip == null)
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

        AudioManager.Instance.PlaySE(damageSeClip, damageSeVolume);
    }

    private IEnumerator DamageRoutine()
    {
        bool useUnscaled = Mathf.Approximately(Time.timeScale, 0f);
        if (useUnscaled)
        {
            anim.updateMode = AnimatorUpdateMode.UnscaledTime;
        }

        // アニメーション開始
        anim.SetBool(ParamIsDamage, true);

        // ダメージアニメーションの長さに合わせて待機
        // アニメーションクリップの長さに応じて調整してください
        if (useUnscaled)
        {
            yield return new WaitForSecondsRealtime(damageDuration);
        }
        else
        {
            yield return new WaitForSeconds(damageDuration);
        }

        // アニメーション終了
        // QTE中ロックが掛かっている場合はダメージポーズを維持する
        if (!_lockDamage)
        {
            anim.SetBool(ParamIsDamage, false);
        }
        if (useUnscaled && Mathf.Approximately(Time.timeScale, 0f))
        {
            pendingUpdateModeRestore = true;
        }
        else
        {
            anim.updateMode = defaultUpdateMode;
        }
        damageRoutine = null;
    }
}