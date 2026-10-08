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
    // 終了要求は Bool。視線中に要求が来たら true を保持し、視線終了後に Chore_End へ進む。
    private const string ChoreExitRequestedParameter = "Chore_ExitRequested";
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
    ///  ・Chore_End / Door_Open は Animator の Transition（Exit Time）で Idle へ自動遷移するため、
    ///    ここでは Walk=true を立てるだけでよい（Play("Idle") による強制復帰は行わない）。
    ///  ・Idle 到達後は既存の Idle→Walk 遷移で歩きへ入る。
    /// </summary>
    public void RestoreWalking()
    {
        Animator a = ResolveAnimator();
        if (a == null) return;

        // 歩行中として扱う。演出ステートからの Idle 復帰は Animator の Transition が担当する。
        SetWalking(true);

        if (showDebugLogs)
            Debug.Log("[MotherAnimationPlayer] 歩き要求（Walk=true）— Idle 復帰は Animator 遷移に委ねます");
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

    /// <summary>Chore_End（立つ・1回）へ進む終了要求を立てる（Bool=true）。</summary>
    public void PlayChoreEnd() => SetChoreExitRequested(true);

    /// <summary>Door_Open（ドアを開ける・1回）へ遷移要求を出す。</summary>
    public void PlayDoorOpen() => FireTrigger(DoorOpenParameter);

    /// <summary>Chore_Peek の再生完了を待つ（タイムアウト付き）。</summary>
    public IEnumerator WaitForChorePeek() => WaitForOneShot(choreLookStateName);

    /// <summary>Chore_End の再生完了を待つ（タイムアウト付き）。</summary>
    public IEnumerator WaitForChoreEnd() => WaitForOneShot(choreEndStateName);

    /// <summary>Door_Open の再生完了を待つ（タイムアウト付き）。</summary>
    public IEnumerator WaitForDoorOpen() => WaitForOneShot(doorOpenStateName);

    /// <summary>
    /// 終了要求（Chore_ExitRequested）を設定する。
    ///  ・true … Chore / Chore_Peek から Chore_End へ進む要求（視線中でも保持される）。
    ///  ・false … Chore_Peek → Chore に戻る条件（視線終了後）。
    /// </summary>
    public void SetChoreExitRequested(bool requested)
    {
        Animator a = ResolveAnimator();
        if (a == null) return;

        if (!HasParameter(ChoreExitRequestedParameter, AnimatorControllerParameterType.Bool))
        {
            Debug.LogWarning($"[MotherAnimationPlayer] Animator に Bool '{ChoreExitRequestedParameter}' がありません。" +
                             "Animator Controller へ登録してください", this);
            return;
        }

        a.SetBool(ChoreExitRequestedParameter, requested);

        if (showDebugLogs)
            Debug.Log($"[MotherAnimationPlayer] {ChoreExitRequestedParameter} = {requested}");
    }

    /// <summary>
    /// 片付け用の未消費パラメーターをリセットする。
    /// 開始・完了・中断・再プレイ時に呼び、Chore_ExitRequested と Trigger の残留を防ぐ。
    /// </summary>
    public void ResetChoreParameters()
    {
        Animator a = ResolveAnimator();
        if (a == null) return;

        ResetBoolIfExists(a, ChoreExitRequestedParameter);
        ResetTriggerIfExists(a, ChoreStartParameter);
        ResetTriggerIfExists(a, ChorePeekParameter);
        ResetTriggerIfExists(a, DoorOpenParameter);
    }

    private static void ResetBoolIfExists(Animator a, string name)
    {
        if (HasParameterOn(a, name, AnimatorControllerParameterType.Bool))
            a.SetBool(name, false);
    }

    private static void ResetTriggerIfExists(Animator a, string name)
    {
        if (HasParameterOn(a, name, AnimatorControllerParameterType.Trigger))
            a.ResetTrigger(name);
    }

    /// <summary>
    /// 演出（Chore / Chore_Peek / Chore_End / Door_Open）の再生を中断し、
    /// 通常歩きへ戻す。中断・ゲームオーバー・シーン変更で使う。
    /// </summary>
    public void AbortPerformance()
    {
        Animator a = ResolveAnimator();
        if (a == null) return;

        // 未消費の片付けパラメーターを残さない。
        ResetChoreParameters();

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
    /// 1回再生ステートの完了を待つ。結果を success で返す（未開始・中断・タイムアウトと正常終了を混同しない）。
    ///  成功条件：ステートへ到達し、その再生が最後まで進んだ（normalizedTime>=1 もしくは正式な退出）。
    ///  失敗：Animator未解決／ステート未登録／到達タイムアウト／完了タイムアウト／中断。
    /// </summary>
    public IEnumerator WaitForOneShot(string stateName, System.Action<bool> onResult) =>
        WaitForOneShotState(stateName, onResult);

    /// <summary>
    /// 1回再生ステートの完了を待つ（結果なしの互換オーバーロード）。
    /// </summary>
    public IEnumerator WaitForOneShot(string stateName)
    {
        yield return WaitForOneShotState(stateName, null);
    }

    /// <summary>
    /// 1回再生ステートの完了を待つ本体。
    ///  ・開始を確認できた場合のみ「正常終了」を判定する（未開始を成功扱いにしない）。
    ///  ・中断フラグ（AbortPerformance 経由で ResetChoreParameters が呼ばれた場合は呼び出し側が停止）では失敗を返す。
    ///  ・ループステートは呼び出し側の時間管理に委ね、開始確認のみで成功とする。
    /// </summary>
    private IEnumerator WaitForOneShotState(string stateName, System.Action<bool> onResult)
    {
        Animator a = ResolveAnimator();

        if (a == null || string.IsNullOrEmpty(stateName) || !HasState(stateName))
        {
            Debug.LogWarning($"[MotherAnimationPlayer] ステート '{stateName}' を解決できないため再生完了を判定できません（失敗扱い）", this);
            yield return new WaitForSeconds(fallbackWaitSeconds);
            onResult?.Invoke(false);
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
            // 未開始：正常終了と混同しない（失敗を返す）。
            Debug.LogWarning($"[MotherAnimationPlayer] ステート '{stateName}' へ到達できませんでした（{reachTimeoutSeconds:F1}s）— 失敗", this);
            yield return new WaitForSeconds(fallbackWaitSeconds);
            onResult?.Invoke(false);
            yield break;
        }

        // 2) 開始を確認できた。1回再生の終了まで待つ。
        //    ・現在もそのステートなら、normalizedTime>=1 で「正常終了」。
        //    ・別のステートへ正式に退出した場合も「正常終了」（遷移による退出。Exit Time=1 の遷移で起こる）。
        //    ・同じステートへ戻ってきた場合（ループ）や再入は正常終了とみなさない。
        float elapsed = 0f;
        bool completed = false;
        bool leftToOtherState = false;

        while (elapsed < oneShotTimeoutSeconds)
        {
            AnimatorStateInfo info = a.GetCurrentAnimatorStateInfo(Layer);

            if (info.IsName(stateName))
            {
                if (info.loop) { completed = true; break; }        // ループは時間管理に委ねる
                if (info.normalizedTime >= 1f) { completed = true; break; }
                // 一度別ステートへ出た後に同じステートへ戻ってきた場合は正常終了としない
                leftToOtherState = false;
            }
            else
            {
                // 別ステートへ移動した。遷移中（IsInTransition）でなければ正式な退出とみなす。
                if (!a.IsInTransition(Layer))
                {
                    leftToOtherState = true;
                    break;
                }
            }

            elapsed += Time.deltaTime;
            yield return null;
        }

        if (!completed && !leftToOtherState)
        {
            Debug.LogWarning($"[MotherAnimationPlayer] ステート '{stateName}' の再生完了を" +
                             $"{oneShotTimeoutSeconds:F1}s で確認できませんでした — 失敗", this);
            onResult?.Invoke(false);
            yield break;
        }

        onResult?.Invoke(true);
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
        return HasParameterOn(ResolveAnimator(), parameterName, type);
    }

    /// <summary>指定 Animator がパラメーターを持つか（static。リセット処理から使う）。</summary>
    private static bool HasParameterOn(Animator a, string parameterName, AnimatorControllerParameterType type)
    {
        if (a == null || string.IsNullOrEmpty(parameterName)) return false;

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
