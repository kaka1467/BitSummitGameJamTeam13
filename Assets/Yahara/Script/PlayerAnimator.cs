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

    // Animator パラメータ名（Unityエディタ側のパラメータ名と一致させること）
    private static readonly int ParamIsDamaged = Animator.StringToHash("isDamaged");

    private Animator anim;
    private Coroutine damageRoutine;

    private void Awake()
    {
        anim = GetComponent<Animator>();
    }

   public void PlayDamage()
    {
        // 既にダメージ演出中であればキャンセルして再開するなどの制御も可能です
        StopAllCoroutines();
        StartCoroutine(DamageRoutine());
    }

    private IEnumerator DamageRoutine()
    {
        // アニメーション開始
        anim.SetBool("isDamage", true);

        // ダメージアニメーションの長さに合わせて待機
        // アニメーションクリップの長さに応じて調整してください
        yield return new WaitForSeconds(0.5f);

        // アニメーション終了
        anim.SetBool("isDamage", false);
    }
}
