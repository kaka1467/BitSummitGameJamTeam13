using System.Collections;
using UnityEngine;

/// <summary>
/// MotherAnimationPlayer：
/// 母親モデルの Animator への書き込みを1か所に集約する小さなプレイヤー。
///
/// 担当（この範囲だけを行う）：
///   ・Walk / Idle / Door_Open / Chore / Chore_Peek / Chore_End の再生
///   ・ループ再生と1回再生の区別
///   ・1回再生の完了待ち（タイムアウト付き）
///   ・再生の中断
///   ・通常歩きへの復帰（Idle 経由 → 既存の Idle→Walk 遷移）
///   ・通常の覗き（Peek_Door / Peek_Windows）の Trigger 発火
///
/// 担当しない（他クラスの責務）：
///   ・経路・Waypoint 移動（MotherApproachController）
///   ・怪しさゲージ（MotherSuspicionSystem / MotherGauge）
///   ・ゲーム進行・片付けの進行（MotherChoreController）
///
/// 設計方針：
///   ・Animator.Play / SetBool / SetTrigger の呼び出しは本クラスに集約する。
///     移動・発見判定側から別々に Walk=false を書いて上書きしない。
///   ・Animator Controller の遷移は次の2本のみを使う（新規 Transition は追加しない）。
///       Idle --(Walk == true)--> Walk
///       Idle --(Peek_Door / Peek_Windows)--> Peek_Door / Peek_Window
///     Chore / Chore_Peek / Chore_End / Door_Open は遷移を持たないため、
///     終了後は本クラスの RestoreWalking()（Play("Idle") + Walk=true）で必ず歩きへ戻す。
/// </summary>
public class MotherAnimationPlayer : MonoBehaviour
{
    // ── Animator パラメーター／ステート名 ─────────────────────────────────────
    private const string WalkParameter = "Walk";
    private const int Layer = 0;

    // 片付け演出用の Animator パラメーター（Animator Controller 側で Transition の条件に使う）。
    private const string ChoreStartParameter = "Chore_Start";
    private const string ChorePeekParameter = "Chore_Peek_Trigger";
    private const string ChoreEndParameter = "Chore_EndTrigger";
    private const string DoorOpenParameter = "Door_OpenTrigger";

    // ── 参照 ──────────────────────────────────────────────────────────────────
    [Header("参照")]
    [Tooltip("母親モデルのAnimator。未設定の場合はMotherApproachControllerから解決する。")]
    [SerializeField] private Animator animator;

    [Tooltip("Animator解決のフォールバック元（未設定時にMotherAnimatorを参照する）。")]
    [SerializeField] private MotherApproachController approachController;

    // ── ステート名 ────────────────────────────────────────────────────────────
    [Header("ステート名")]
    [SerializeField] private string idleStateName = "Idle";
    [SerializeField] private string walkStateName = "Walk";
    [SerializeField] private string doorOpenStateName = "Door_Open";
    [SerializeField] private string choreLoopStateName = "Chore";
    [SerializeField] private string choreLookStateName = "Chore_Peek";
    [SerializeField] private string choreEndStateName = "Chore_End";

    // ── タイミング ────────────────────────────────────────────────────────────
    [Header("タイミング")]
    [Tooltip("1回再生ステートへ到達するまでの待ち上限（秒）。")]
    [SerializeField, Min(0.1f)] private float reachTimeoutSeconds = 1f;
    [Tooltip("1回再生ステートの完了を待つ上限（秒）。想定尺より十分大きい必要がある。")]
    [SerializeField, Min(1f)] private float oneShotTimeoutSeconds = 30f;
    [Tooltip("ステート未登録などで再生できない場合のフォールバック待機（秒）。")]
    [SerializeField, Min(0.1f)] private float fallbackWaitSeconds = 1f;

    [Header("デバッグ")]
    [SerializeField] private bool showDebugLogs = true;

    // ── 公開状態 ──────────────────────────────────────────────────────────────
    /// <summary>解決済みの Animator（未解決なら null）。</summary>
    public Animator Animator => ResolveAnimator();

    /// <summary>現在 Walk パラメーターが true か（Animator 未解決なら false）。</summary>
    public bool IsWalking
    {
        get
        {
            Animator a = ResolveAnimator();
            return a != null && HasParameter(WalkParameter, AnimatorControllerParameterType.Bool) &&
                   a.GetBool(WalkParameter);
        }
    }

    /// <summary>指定ステートを現在再生中か（Animator 未解決なら false）。</summary>
    public bool IsPlayingState(string stateName)
    {
        Animator a = ResolveAnimator();
        if (a == null || string.IsNullOrEmpty(stateName)) return false;
        return a.GetCurrentAnimatorStateInfo(Layer).IsName(stateName);
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  基本操作
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 歩行状態を設定する（通常イベント・片付けの歩行区間で使う唯一の入口）。
    /// Walk=true なら既存遷移 Idle→Walk、false なら Walk→Idle が働く。
    /// </summary>
    public void SetWalking(bool walking)
    {
        Animator a = ResolveAnimator();
        if (a == null) return;

        if (!HasParameter(WalkParameter, AnimatorControllerParameterType.Bool))
        {
            Debug.LogWarning($"[MotherAnimationPlayer] Animator に {WalkParameter}(bool) が無いため歩行状態を設定できません", this);
            return;
        }

        a.SetBool(WalkParameter, walking);
    }

    /// <summary>
    /// 通常歩きへ復帰する。
    ///  ・Chore / Chore_Peek / Chore_End / Door_Open は遷移を持たないため、
    ///    Idle へ明示的に切り替えてから Walk=true で既存の Idle→Walk に乗せる。
    ///  ・既に Idle 到達済みなら Idle の Play を省略して無駄な再開をしない。
    /// </summary>
    public void RestoreWalking()
    {
        Animator a = ResolveAnimator();
        if (a == null) return;

        // 1) Idle へ切り替える（演出ステートから出る唯一の有効な切り替え）。
        if (HasState(idleStateName))
        {
            if (!IsPlayingState(idleStateName))
                a.Play(idleStateName, Layer, 0f);
        }
        else
        {
            Debug.LogWarning($"[MotherAnimationPlayer] Animator に '{idleStateName}' が無いため歩きへ戻せません", this);
            return;
        }

        // 2) 歩行中として扱い、既存の Idle→Walk 遷移で歩きへ入る。
        SetWalking(true);

        if (showDebugLogs)
            Debug.Log($"[MotherAnimationPlayer] {idleStateName} 経由で通常歩きへ復帰");
    }

    /// <summary>
    /// 通常の覗きアニメーションの Trigger を発火する（既存の Peek_Door / Peek_Windows 用）。
    /// 片付けの Chore_Peek とは別物で、Step 発火のみを担当する。
    /// </summary>
    public void FirePeekTrigger(string triggerName)
    {
        Animator a = ResolveAnimator();
        if (a == null || string.IsNullOrEmpty(triggerName)) return;

        if (!HasParameter(triggerName, AnimatorControllerParameterType.Trigger))
        {
            Debug.LogWarning($"[MotherAnimationPlayer] Animator に Trigger '{triggerName}' が無いため再生をスキップします", this);
            return;
        }

        a.ResetTrigger(triggerName);
        a.SetTrigger(triggerName);
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  片付け用ステートの再生（Animator のパラメーターと Transition で遷移させる）
    //   ・MotherChoreController は「要求」だけを出し、実際の遷移は Animator Controller が行う。
    //   ・直接 Play しないため、通常の歩き要求が演出を途中で上書きしない。
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>Chore（片付け中・ループ）へ遷移要求を出す。</summary>
    public void PlayChore() => FireTrigger(ChoreStartParameter);

    /// <summary>Chore は ループステートのため、再トリガーは不要（互換用）。</summary>
    public void PlayChoreLoop() { /* Chore は遷移後ループする。再要求しない。 */ }

    /// <summary>Chore_Peek（こちらを見る・1回）へ遷移要求を出す。</summary>
    public void PlayChorePeek() => FireTrigger(ChorePeekParameter);

    /// <summary>Chore_End（立つ・1回）へ遷移要求を出す。</summary>
    public void PlayChoreEnd() => FireTrigger(ChoreEndParameter);

    /// <summary>Door_Open（ドアを開ける・1回）へ遷移要求を出す。</summary>
    public void PlayDoorOpen() => FireTrigger(DoorOpenParameter);

    /// <summary>Chore_Peek の再生完了を待つ（タイムアウト付き）。</summary>
    public IEnumerator WaitForChorePeek() => WaitForOneShot(choreLookStateName);

    /// <summary>Chore_End の再生完了を待つ（タイムアウト付き）。</summary>
    public IEnumerator WaitForChoreEnd() => WaitForOneShot(choreEndStateName);

    /// <summary>Door_Open の再生完了を待つ（タイムアウト付き）。</summary>
    public IEnumerator WaitForDoorOpen() => WaitForOneShot(doorOpenStateName);

    /// <summary>ステート名を指定して1回再生の完了を待つ（汎用）。</summary>
    public IEnumerator WaitForOneShot(string stateName) => WaitForOneShotState(stateName);

    /// <summary>
    /// 演出（Chore / Chore_Peek / Chore_End / Door_Open）の再生を中断し、
    /// 通常歩きへ戻す。中断・ゲームオーバー・シーン変更で使う。
    /// </summary>
    public void AbortPerformance()
    {
        Animator a = ResolveAnimator();
        if (a == null) return;

        // 再生待ちコルーチンは呼び出し側が停止する。ここでは状態だけを歩きへ戻す。
        RestoreWalking();
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  内部
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 指定ステート名へ Trigger を発火して遷移させる（Animator の Transition が実際の遷移を行う）。
    /// ステート名は「遷移先ステートの存在確認」にのみ使い、Play はしない。
    /// </summary>
    private void FireTrigger(string triggerName)
    {
        Animator a = ResolveAnimator();
        if (a == null) return;

        if (string.IsNullOrEmpty(triggerName))
        {
            Debug.LogWarning("[MotherAnimationPlayer] Trigger名が空のため要求できません", this);
            return;
        }

        if (!HasParameter(triggerName, AnimatorControllerParameterType.Trigger))
        {
            Debug.LogWarning($"[MotherAnimationPlayer] Animator に Trigger '{triggerName}' がありません。" +
                             "Animator Controller へ登録してください", this);
            return;
        }

        // 歩行で上書きされないよう先に Walk を解除してから遷移を要求する。
        SetWalking(false);
        a.ResetTrigger(triggerName);
        a.SetTrigger(triggerName);

        if (showDebugLogs)
            Debug.Log($"[MotherAnimationPlayer] 遷移要求: {triggerName}");
    }

    /// <summary>
    /// 1回再生ステートの完了を待つ。ループステートが渡された場合は即終了（呼び出し側が時間管理）。
    /// ステート未登録・到達不能・尺超過の場合はタイムアウトで抜け、警告を出す。
    /// </summary>
    private IEnumerator WaitForOneShotState(string stateName)
    {
        Animator a = ResolveAnimator();
        if (a == null || string.IsNullOrEmpty(stateName) || !HasState(stateName))
        {
            yield return new WaitForSeconds(fallbackWaitSeconds);
            yield break;
        }

        // 1) ステート到達を待つ（Play 直後は遷移中・前ステートのことがある）。
        float reachElapsed = 0f;
        while (reachElapsed < reachTimeoutSeconds && !IsPlayingState(stateName))
        {
            reachElapsed += Time.deltaTime;
            yield return null;
        }

        if (!IsPlayingState(stateName))
        {
            Debug.LogWarning($"[MotherAnimationPlayer] ステート '{stateName}' へ到達できませんでした（{reachTimeoutSeconds:F1}s）", this);
            yield return new WaitForSeconds(fallbackWaitSeconds);
            yield break;
        }

        // 2) 1回再生の終了（normalizedTime >= 1）まで待つ。ループは時間管理に委ねる。
        float elapsed = 0f;
        while (elapsed < oneShotTimeoutSeconds)
        {
            AnimatorStateInfo info = a.GetCurrentAnimatorStateInfo(Layer);
            if (!info.IsName(stateName)) yield break;          // 別ステート＝終了扱い
            if (info.loop) yield break;                        // ループは呼び出し側で時間管理
            if (info.normalizedTime >= 1f) yield break;        // 1回再生の終了

            elapsed += Time.deltaTime;
            yield return null;
        }

        Debug.LogWarning($"[MotherAnimationPlayer] ステート '{stateName}' の再生完了を" +
                         $"{oneShotTimeoutSeconds:F1}s で確認できませんでした", this);
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Animator 解決・パラメーター判定
    // ──────────────────────────────────────────────────────────────────────────

    private Animator ResolveAnimator()
    {
        if (animator != null) return animator;

        if (approachController == null)
            approachController = Object.FindFirstObjectByType<MotherApproachController>();

        if (approachController != null)
            animator = approachController.MotherAnimator;

        return animator;
    }

    private bool HasState(string stateName)
    {
        Animator a = ResolveAnimator();
        if (a == null || string.IsNullOrEmpty(stateName)) return false;
        return a.HasState(Layer, Animator.StringToHash(stateName));
    }

    private bool HasParameter(string parameterName, AnimatorControllerParameterType type)
    {
        Animator a = ResolveAnimator();
        if (a == null) return false;

        AnimatorControllerParameter[] parameters = a.parameters;
        for (int i = 0; i < parameters.Length; i++)
        {
            if (parameters[i].name == parameterName && parameters[i].type == type)
                return true;
        }

        return false;
    }

    public string IdleStateName => idleStateName;
    public string WalkStateName => walkStateName;
    public string DoorOpenStateName => doorOpenStateName;
    public string ChoreLoopStateName => choreLoopStateName;
    public string ChoreLookStateName => choreLookStateName;
    public string ChoreEndStateName => choreEndStateName;

    /// <summary>Walk(bool) パラメーターと Idle ステートが揃っているか（再生可否の事前判定用）。</summary>
    public bool CanPlayWalk => HasParameter(WalkParameter, AnimatorControllerParameterType.Bool);
}
