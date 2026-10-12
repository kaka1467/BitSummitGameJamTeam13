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

    /// <summary>
    /// 1回再生の「完了」とみなす normalizedTime の下限。
    /// Exit Time 遷移（1.0）で退出した場合に、退出直前の観測値がわずかに1未満でも
    /// 正常終了として扱うための許容値。
    /// </summary>
    private const float CompletionNormalizedTolerance = 0.99f;

    // 片付け演出用の Animator パラメーター（Animator Controller 側で Transition の条件に使う）。
    private const string ChoreStartParameter = "Chore_Start";
    private const string ChorePeekParameter = "Chore_Peek_Trigger";
    // 終了要求は Bool。視線中に要求が来たら true を保持し、視線終了後に Chore_End へ進む。
    private const string ChoreExitRequestedParameter = "Chore_ExitRequested";
    private const string DoorOpenParameter = "Door_OpenTrigger";
    // Door_Open ステート専用の速度パラメーター（Animator全体の speed は変更しない）。
    private const string DoorOpenSpeedParameter = "Door_OpenSpeed";
    // 歩行ステート（Walk / Walk_Fast）専用の速度パラメーター（Animator全体の speed は変更しない）。
    //  ・Animator Controller 側で Walk / Walk_Fast の Speed Multiplier に割り当てる。
    //  ・移動（waypoints）側の速度は変更しない（アニメーション再生だけを倍率で調整する）。
    private const string WalkSpeedParameter = "WalkSpeed";

    // ── ゲームオーバー演出用パラメーター（Animator.Play ではなく Transition で入る）──────
    //   ・GameOverActive：通常遷移とゲームオーバー遷移を区別する Bool。
    //   ・WalkFast     ：早歩き（Walk_Fast）へ入る Bool。
    //   ・JumpscareTrigger：ジャンプスケアへ入る Trigger。
    //   ※ 既存の Walk / Door_OpenTrigger / WalkSpeed / Door_OpenSpeed は再利用する。
    private const string GameOverActiveParameter = "GameOverActive";
    private const string WalkFastParameter = "WalkFast";
    private const string JumpscareParameter = "JumpscareTrigger";
    //   ・GameOverStopTrigger：捕獲開始状態（Peek/Chore など）からも「停止状態（Idle）」へ入るための Trigger。
    //     早歩きを一度再生しなくても停止状態へ入れるようにする（Bool ではなく Trigger にして
    //     一度発火したら消費されるようにし、Door_Open／Jumpscare を継続的に上書きしない）。
    private const string GameOverStopParameter = "GameOverStopTrigger";

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

    // ── 片付けのドア開け速度 ──────────────────────────────────────────────────
    [Tooltip("【母親】Door_Open アニメの再生速度（倍率）。1=等倍、2=2倍の速さ、0.5=半分の速さ。\n" +
             "※大きいほど動作が速くなります。Animator全体の speed は変更せず、Door_Open 専用の" +
             "速度パラメーター（Door_OpenSpeed）を設定します。行き／帰りの両方に適用。\n" +
             "※ドア本体が開く「時間」（MotherGameOverSequence の gameOverDoorOpenDuration）とは別の設定です。")]
    [SerializeField, Min(0.01f)] private float doorOpenSpeedMultiplier = 1.5f;

    // ── 歩行アニメーション速度 ────────────────────────────────────────────────
    //   歩行ステート（Walk / Walk_Fast）だけに効く倍率。Animator全体の speed は変更しない。
    //   移動（waypoints）の移動速度・旋回速度・ルートには影響しない（アニメーション再生だけ）。
    [Tooltip("歩行アニメーションだけの再生速度倍率。\n" +
             "1で通常速度。waypoints間の移動速度は変更しない。")]
    [SerializeField, Min(0.01f)] private float walkAnimationSpeedMultiplier = 1.15f;

    // ── 片付けのドア開け「開始タイミング」 ────────────────────────────────────
    //   Door_Open の「手がドアを押す」位置でドア本体を開き始めるための判定を本クラスで持つ。
    //   ※推測の遅延秒数は入れていない。Unity の Animation ウィンドウで押すフレームを確認して設定すること。
    [Tooltip("押すタイミングの判定方式。NormalizedTime=正規化タイムで判定／AnimationEvent=Animation Event通知で判定。" +
             "方式を明示的に選ぶ（両方を混ぜない）。")]
    [SerializeField] private DoorOpenPushTiming doorOpenPushTiming = DoorOpenPushTiming.NormalizedTime;

    [Tooltip("【NormalizedTime方式】Door_Open で「手がドアを押す」正規化タイム（0〜1）。\n" +
             "値は「押す時刻[秒] ÷ クリップ長[秒]」で求める。0=モデル開始と同時。\n" +
             "※これは『ドア本体の開き始めタイミング』であり、開く速さ／時間ではありません。\n" +
             "※AnimationEvent方式ではこの値は使わない。")]
    [SerializeField, Range(0f, 1f)] private float doorOpenPushNormalizedTime = 0f;

    [Tooltip("Door_Open の押すタイミング待ちの上限（秒）。超えたら失敗として扱う（無限待機しない）。")]
    [SerializeField, Min(0.1f)] private float doorOpenPushTimeoutSeconds = 5f;

    /// <summary>押すタイミングの判定方式（明示的に選択する）。</summary>
    public enum DoorOpenPushTiming
    {
        /// <summary>正規化タイム（押す時刻[秒] ÷ クリップ長[秒]）で判定する。</summary>
        NormalizedTime,
        /// <summary>Animation Event（NotifyDoorOpenPushPoint）の通知だけで判定する。</summary>
        AnimationEvent
    }

    /// <summary>押すタイミングが通知されたか（Animation Event 用の任意フック）。</summary>
    private bool _doorOpenPushSignaled;

    // ── ゲームオーバー演出（捕獲時）────────────────────────────────────────────
    //  ・早歩き（Walk_Fast）とジャンプスケア（Jumpscare）は Animator Controller に既存のステートを使う。
    //    どちらも遷移を持たないため、Animator.Play で直接入る（新規ステートや遷移は追加しない）。
    //  ・Animator 全体の speed は変更しない。歩行は WalkSpeed、ドアは Door_OpenSpeed のまま使う。
    //  ・「通常の歩行復帰（RestoreWalking）」が捕獲演出を上書きしないよう、演出中フラグで止める。
    [Header("ゲームオーバー演出")]

    [Tooltip("早歩きステート名。Animator Controller の既存 Walk_Fast を使う（重複作成しない）。")]
    [SerializeField] private string walkFastStateName = "Walk_Fast";

    [Tooltip("ジャンプスケアステート名。Animator Controller の既存 Jumpscare を使う（重複作成しない）。")]
    [SerializeField] private string jumpScareStateName = "Jumpscare";

    [Tooltip("早歩き（Walk_Fast）のときだけ使う歩行アニメの再生速度倍率。" +
             "移動速度（waypoints）とは別。Animator 全体の speed は変更しない。")]
    [SerializeField, Min(0.01f)] private float gameOverWalkFastAnimationSpeedMultiplier = 1.6f;

    /// <summary>
    /// 歩行アニメの速度倍率の上書き（0以下で無効＝walkAnimationSpeedMultiplier を使う）。
    ///  ・早歩き中に FireTrigger→SetWalking(false)→ApplyWalkAnimationSpeed が走っても
    ///    速度が通常値へ戻らないようにするための保持値。
    /// </summary>
    private float _walkSpeedOverride = -1f;

    /// <summary>ゲームオーバー演出中か（通常の歩行復帰で演出を上書きしないためのフラグ）。</summary>
    private bool _gameOverPerformanceActive;

    /// <summary>ゲームオーバー演出中か（読み取り専用）。</summary>
    public bool IsGameOverPerformanceActive => _gameOverPerformanceActive;

    /// <summary>ジャンプスケアのステート名（演出側がログ・診断に使う。Animator 操作は本クラスに閉じる）。</summary>
    public string JumpScareStateName => jumpScareStateName;

    // ── Unity ライフサイクル ──────────────────────────────────────────────────

    /// <summary>
    /// 起動時（再プレイのシーン読み込みを含む）に、歩行アニメーションの倍率を一度適用する。
    ///  ・歩行ステート専用の Float パラメーター（WalkSpeed）だけを設定する。
    ///  ・Animator 全体の speed と、歩行以外のステート（覗き／片付け／ドア開け／捕獲／Idle）の
    ///    再生速度は変更しない。
    /// </summary>
    private void Start()
    {
        ApplyWalkAnimationSpeed();

        // 【重要】母親の移動はすべてコード側（MotherApproachController の transform.position 更新）で行うため、
        //   Animator の Root Motion を無効化する。
        //   ・Root Motion が ON だと、クリップに焼かれた平行移動が毎フレーム位置へ加算され、
        //     コード側の移動速度（Step.moveSpeed / defaultMoveSpeed）が実質無視される
        //     （＝速度を上げても見た目が変わらない）。
        //   ・提供モデルの README も「Apply Root Motion OFF のまま」と指定している。
        //   ・Prefab/Scene 側の設定も OFF にするのが本来だが、実行時に保険として無効化する。
        Animator a = ResolveAnimator();
        if (a != null && a.applyRootMotion)
        {
            a.applyRootMotion = false;
            Debug.LogWarning("[MotherAnimationPlayer] Animator の Apply Root Motion が ON だったため OFF にしました" +
                             "（コード移動と競合し、移動速度が反映されない原因になります）。" +
                             "Prefab/Scene 側の設定も OFF にしてください", this);
        }
    }

    /// <summary>
    /// Inspector で 0 以下が入った場合に下限（0.01）へ補正する。
    ///  ・再生速度 0 で歩行が止まらないようにするための検証。
    ///  ・実行時も ApplyWalkAnimationSpeed 内で Mathf.Max により再検証する。
    /// </summary>
    private void OnValidate()
    {
        walkAnimationSpeedMultiplier = Mathf.Max(0.01f, walkAnimationSpeedMultiplier);
    }

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

    /// <summary>
    /// 【片付け】Chore（片付けループ）へ戻りつつあるか。
    ///  ・現在ステートが Chore、または遷移中で遷移先が Chore のとき true。
    ///  ・MotherChoreController の復帰待ちが使う既存の判定（挙動は変えない）。
    ///  ・ステート名は本クラスに閉じる（呼び出し側は名前を渡さない）。
    /// </summary>
    public bool IsPlayingChoreLoop => IsCurrentStateChoreLoop || IsTransitionDestinationChoreLoop;

    /// <summary>
    /// 【診断】現在のステート（遷移中は遷移元）が Chore ループか。
    ///  ・IsPlayingChoreLoop は「遷移先が Chore」でも true になるため、
    ///    ログではこのプロパティで「現在状態が Chore」と「遷移先が Chore のみ」を区別する。
    ///  ・状態は変更しない（読み取り専用）。
    /// </summary>
    public bool IsCurrentStateChoreLoop
    {
        get
        {
            Animator a = ResolveAnimator();
            return a != null && a.GetCurrentAnimatorStateInfo(Layer).IsName(choreLoopStateName);
        }
    }

    /// <summary>
    /// 【診断】遷移先（GetNextAnimatorStateInfo）が Chore ループか。
    ///  ・遷移中のみ意味を持つ。true でも「Chore へ戻り終えた」ことではない（遷移中である）。
    ///  ・状態は変更しない（読み取り専用）。
    /// </summary>
    public bool IsTransitionDestinationChoreLoop
    {
        get
        {
            Animator a = ResolveAnimator();
            return a != null && a.IsInTransition(Layer) && a.GetNextAnimatorStateInfo(Layer).IsName(choreLoopStateName);
        }
    }

    /// <summary>
    /// 【診断】Animator 状態の変化検出用シグネチャ（ログの間引きに使う）。
    ///  ・「現在ステート・遷移先ステート・遷移中か・enabled・activeInHierarchy」のみ。
    ///  ・normalizedTime は含めない（毎フレーム変化させない＝同じログを出さないため）。
    ///  ・状態は変更しない（読み取り専用）。
    /// </summary>
    public bool TryGetChoreAnimatorSignature(out int currentStateHash, out int nextStateHash,
        out bool isInTransition, out bool enabled, out bool activeInHierarchy)
    {
        currentStateHash = 0;
        nextStateHash = 0;
        isInTransition = false;
        enabled = false;
        activeInHierarchy = false;

        Animator a = ResolveAnimator();
        if (a == null) return false;

        isInTransition = a.IsInTransition(Layer);
        currentStateHash = a.GetCurrentAnimatorStateInfo(Layer).fullPathHash;
        nextStateHash = isInTransition ? a.GetNextAnimatorStateInfo(Layer).fullPathHash : 0;
        enabled = a.enabled;
        activeInHierarchy = (a.gameObject != null) && a.gameObject.activeInHierarchy;
        return true;
    }

    /// <summary>
    /// 【診断】Animator の実行状態を1行で返す（ログ専用。状態は変更しない）。
    /// 参照している Animator の実体・コントローラー・層・現在／次状態・遷移・クリップ・関連パラメータを含む。
    /// 文字列生成と GetCurrent/GetNextAnimatorClipInfo の確保が発生するため、
    /// 段階が変わったときだけ呼ぶ（毎フレーム呼ばない）。
    /// </summary>
    public string DescribeChoreAnimatorState()
    {
        Animator a = ResolveAnimator();
        if (a == null)
            return "[AnimDiag] Animator未解決（animator も approachController.MotherAnimator も null）";

        AnimatorStateInfo cur = a.GetCurrentAnimatorStateInfo(Layer);
        bool inTransition = a.IsInTransition(Layer);
        AnimatorStateInfo next = inTransition ? a.GetNextAnimatorStateInfo(Layer) : default;
        string controllerName = (a.runtimeAnimatorController != null) ? a.runtimeAnimatorController.name : "NULL";
        bool activeInHierarchy = (a.gameObject != null) && a.gameObject.activeInHierarchy;

        return "[AnimDiag]" +
               $" Animator='{a.name}'(InstanceID={a.GetInstanceID()}) enabled={a.enabled} activeInHierarchy={activeInHierarchy} speed={a.speed:F2} layer={Layer}" +
               $" controller='{controllerName}' choreLoopStateName='{choreLoopStateName}'" +
               $" | current: fullPathHash={cur.fullPathHash} shortNameHash={cur.shortNameHash} normalizedTime={cur.normalizedTime:F3} length={cur.length:F2} loop={cur.loop} isChoreLoop={cur.IsName(choreLoopStateName)}" +
               $" clips(cur)={DescribeClipInfos(a.GetCurrentAnimatorClipInfo(Layer))}" +
               $" | isInTransition={inTransition}" +
               $" next: fullPathHash={(inTransition ? next.fullPathHash.ToString() : "-")} shortNameHash={(inTransition ? next.shortNameHash.ToString() : "-")}" +
               $" normalizedTime={(inTransition ? next.normalizedTime.ToString("F3") : "-")} isChoreLoop={(inTransition && next.IsName(choreLoopStateName))}" +
               $" clips(next)={(inTransition ? DescribeClipInfos(a.GetNextAnimatorClipInfo(Layer)) : "(遷移なし)")}" +
               $" | params: {ChoreExitRequestedParameter}={DescribeBoolParameter(a, ChoreExitRequestedParameter)}" +
               $" {WalkParameter}={DescribeBoolParameter(a, WalkParameter)}" +
               $" {ChorePeekParameter}(Trigger)={(HasParameterOn(a, ChorePeekParameter, AnimatorControllerParameterType.Trigger) ? "登録あり" : "未登録")}" +
               $" {ChoreStartParameter}(Trigger)={(HasParameterOn(a, ChoreStartParameter, AnimatorControllerParameterType.Trigger) ? "登録あり" : "未登録")}" +
               $" {DoorOpenParameter}(Trigger)={(HasParameterOn(a, DoorOpenParameter, AnimatorControllerParameterType.Trigger) ? "登録あり" : "未登録")}";
    }

    /// <summary>【診断】Bool パラメータの値を文字列化する（未登録なら N/A）。読み取り専用。</summary>
    private static string DescribeBoolParameter(Animator a, string name)
    {
        if (!HasParameterOn(a, name, AnimatorControllerParameterType.Bool)) return "N/A";
        return a.GetBool(name).ToString();
    }

    /// <summary>【診断】再生中のクリップ名と重み（現在／次状態のどちらにも使う）。読み取り専用。</summary>
    private static string DescribeClipInfos(AnimatorClipInfo[] infos)
    {
        if (infos == null || infos.Length == 0) return "(クリップなし)";

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < infos.Length; i++)
        {
            if (i > 0) sb.Append(" + ");
            sb.Append(infos[i].clip != null ? infos[i].clip.name : "(clipなし)");
            sb.Append(" w=").Append(infos[i].weight.ToString("F2"));
        }
        return sb.ToString();
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  基本操作
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 歩行状態を設定する（通常イベント・片付けの歩行区間で使う唯一の入口）。
    /// Walk=true なら既存遷移 Idle→Walk、false なら Walk→Idle が働く。
    ///  ・ゲームオーバー演出中は、通常処理からの書き込みで演出（Walk_Fast／Jumpscare）を
    ///    上書きしないよう、ここで拒否する。
    ///  ・演出側の要求は SetWalkingInternal を直接使うため、ここで拒否されない。
    /// </summary>
    public void SetWalking(bool walking)
    {
        if (_gameOverPerformanceActive)
        {
            if (showDebugLogs)
                Debug.Log($"[MotherAnimationPlayer] ゲームオーバー演出中のため、通常の歩行設定（Walk={walking}）をスキップします");
            return;
        }

        SetWalkingInternal(walking);
    }

    /// <summary>
    /// 歩行状態を設定する内部処理（ゲームオーバー演出側の要求もここを使う）。
    ///  ・通常処理の入口は public SetWalking（演出中は拒否）。
    ///  ・演出用API（RequestWalk / FireTrigger）はこの内部処理を呼ぶ（ロックで演出を止めない）。
    /// </summary>
    private void SetWalkingInternal(bool walking)
    {
        Animator a = ResolveAnimator();
        if (a == null) return;

        if (!HasParameter(WalkParameter, AnimatorControllerParameterType.Bool))
        {
            Debug.LogWarning($"[MotherAnimationPlayer] Animator に {WalkParameter}(bool) が無いため歩行状態を設定できません", this);
            return;
        }

        // 歩行アニメーションの倍率を併せて適用する（歩行ステート専用。移動速度は変更しない）。
        ApplyWalkAnimationSpeed();

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
        // ゲームオーバー演出中は通常の歩行復帰要求で演出（Walk_Fast／Jumpscare）を上書きしない。
        if (_gameOverPerformanceActive)
        {
            if (showDebugLogs)
                Debug.Log("[MotherAnimationPlayer] ゲームオーバー演出中のため、通常の歩行復帰をスキップします");
            return;
        }

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
        if (_gameOverPerformanceActive)
        {
            if (showDebugLogs)
                Debug.Log($"[MotherAnimationPlayer] ゲームオーバー演出中のため、通常のPeek要求('{triggerName}')をスキップします");
            return;
        }

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

    /// <summary>【片付け】Chore（片付け中・ループ）へ遷移要求を出す（Transition方式。Animator.Playは使わない）。</summary>
    public void RequestChoreStart() => FireTrigger(ChoreStartParameter);

    /// <summary>【片付け】Chore_Peek（こちらを見る・1回）へ遷移要求を出す。</summary>
    public void RequestChorePeek() => FireTrigger(ChorePeekParameter);

    /// <summary>【片付け】Chore_End（立つ・1回）へ進む終了要求を立てる（Bool=true、Transition方式維持）。</summary>
    public void RequestChoreEnd() => SetChoreExitRequested(true);

    /// <summary>
    /// 【片付け】Door_Open（ドアを開ける・1回）へ遷移要求を出す。
    /// 先に Door_Open 専用の速度パラメーターを設定する（Animator全体の speed は変更しない）。
    /// </summary>
    public bool RequestDoorOpen()
    {
        Animator a = ResolveAnimator();
        if (a == null ||
            !HasParameterOn(a, DoorOpenParameter, AnimatorControllerParameterType.Trigger) ||
            !HasState(doorOpenStateName))
        {
            Debug.LogWarning($"[MotherAnimationPlayer] Door_Open（Trigger '{DoorOpenParameter}' / ステート '{doorOpenStateName}'）を" +
                             "解決できないため、ドア開けを要求できません（失敗）", this);
            return false;
        }

        // WalkFast=true のままだと Any State→Walk_Fast が Door_Open を上書きしうるため、
        // 移動要求と古い停止Trigger を先に解除する。
        ClearGameOverMovementRequests();

        _doorOpenPushSignaled = false;   // 新しい Door_Open のためにクリア
        ApplyDoorOpenSpeed();
        FireTrigger(DoorOpenParameter);
        return true;
    }

    /// <summary>
    /// 【任意】Door_Open の「手がドアを押す」瞬間を知らせる（Animation Event から呼べるフック）。
    ///  ・doorOpenPushTiming = AnimationEvent のときに使う。
    ///  ・押すフレームに Animation Event を置き、このメソッドを呼ぶ。
    ///  ・ステート名・タイミングの管理は本クラスに閉じる（呼び出し側へ戻さない）。
    ///  ・待機開始前に通知が来ても取りこぼさないよう、この通知は WaitForDoorOpenPush では消さない
    ///    （消すのは新しい Door_Open を出す RequestDoorOpen の時点だけ）。
    /// </summary>
    public void NotifyDoorOpenPushPoint()
    {
        _doorOpenPushSignaled = true;
    }

    /// <summary>
    /// Door_Open の「手がドアを押す」タイミング（＝ドア本体を開き始める瞬間）まで待つ。
    ///  ・モデルの開始（Door_Open へ到達）が確認できるまで成立しない（開始前にドアを開けない）。
    ///  ・判定方式は doorOpenPushTiming で明示的に選ぶ：
    ///      NormalizedTime … normalizedTime が doorOpenPushNormalizedTime 以上で成立
    ///                       （閾値の意味は「押す時刻[秒] ÷ クリップ長[秒]」）
    ///      AnimationEvent … NotifyDoorOpenPushPoint() の通知だけで成立
    ///  ・タイムアウトしたら success=false（無限待機しない）。
    /// ステート名・正規化タイムは本クラスが保持する（呼び出し側は何も渡さない）。
    /// </summary>
    public IEnumerator WaitForDoorOpenPush(System.Action<bool> onResult)
    {
        Animator a = ResolveAnimator();
        if (a == null || !HasState(doorOpenStateName))
        {
            Debug.LogWarning("[MotherAnimationPlayer] Door_Open ステートを解決できないため、押すタイミングを判定できません（失敗）", this);
            yield return new WaitForSeconds(fallbackWaitSeconds);
            onResult?.Invoke(false);
            yield break;
        }

        // ※ ここで _doorOpenPushSignaled を消さない。
        //   消すのは RequestDoorOpen() の時点だけにして、待機開始前に Animation Event が
        //   来ていてもその通知を取りこぼさないようにする。
        float elapsed = 0f;

        // 1) モデルの開始（Door_Open 到達）を確認する。開始前にドアを開けない。
        bool started = false;
        while (elapsed < reachTimeoutSeconds)
        {
            if (a.GetCurrentAnimatorStateInfo(Layer).IsName(doorOpenStateName))
            {
                started = true;
                break;
            }
            elapsed += Time.deltaTime;
            yield return null;
        }

        if (!started)
        {
            Debug.LogWarning("[MotherAnimationPlayer] Door_Open の開始を確認できなかったため、ドアを開きません（失敗）", this);
            onResult?.Invoke(false);
            yield break;
        }

        // 2) 押すタイミングを、選択した方式だけで待つ（両方式を混ぜない）。
        if (doorOpenPushTiming == DoorOpenPushTiming.AnimationEvent)
        {
            // AnimationEvent方式：イベント通知だけを待つ（正規化タイムは見ない）。
            while (!_doorOpenPushSignaled)
            {
                if (elapsed >= doorOpenPushTimeoutSeconds)
                {
                    Debug.LogWarning($"[MotherAnimationPlayer] Door_Open の押すタイミング（Animation Event）を " +
                                     $"{doorOpenPushTimeoutSeconds:F1}s で確認できませんでした（失敗）", this);
                    onResult?.Invoke(false);
                    yield break;
                }

                elapsed += Time.deltaTime;
                yield return null;
            }

            if (showDebugLogs)
                Debug.Log("[MotherAnimationPlayer] Door_Open 押すタイミング成立（Animation Event）— ドア本体を開き始めます");
        }
        else
        {
            // NormalizedTime方式：normalizedTime が閾値以上になったら成立（閾値0なら到達直後に成立）。
            while (true)
            {
                AnimatorStateInfo info = a.GetCurrentAnimatorStateInfo(Layer);
                if (info.IsName(doorOpenStateName) && info.normalizedTime >= doorOpenPushNormalizedTime)
                    break;

                if (elapsed >= doorOpenPushTimeoutSeconds)
                {
                    Debug.LogWarning($"[MotherAnimationPlayer] Door_Open の押すタイミング（normalizedTime=" +
                                     $"{doorOpenPushNormalizedTime:F2}）を {doorOpenPushTimeoutSeconds:F1}s で確認できませんでした（失敗）", this);
                    onResult?.Invoke(false);
                    yield break;
                }

                elapsed += Time.deltaTime;
                yield return null;
            }

            if (showDebugLogs)
                Debug.Log($"[MotherAnimationPlayer] Door_Open 押すタイミング成立（normalizedTime={doorOpenPushNormalizedTime:F2}）— ドア本体を開き始めます");
        }

        onResult?.Invoke(true);
    }

    /// <summary>
    /// Door_Open ステート専用の速度パラメーター（Door_OpenSpeed）を設定する。
    ///  ・Animator全体の speed は変更しない（このパラメーターは Door_Open ステートだけが見る）。
    ///  ・パラメーターが未登録の場合は警告して速度調整をスキップする（通常速度で再生される）。
    /// </summary>
    private void ApplyDoorOpenSpeed()
    {
        Animator a = ResolveAnimator();
        if (a == null) return;

        if (!HasParameterOn(a, DoorOpenSpeedParameter, AnimatorControllerParameterType.Float))
        {
            Debug.LogWarning($"[MotherAnimationPlayer] Animator に Float '{DoorOpenSpeedParameter}' がありません。" +
                             "Door_Open の速度調整をスキップします（Animator Controller へ登録してください）", this);
            return;
        }

        float speed = Mathf.Max(0.01f, doorOpenSpeedMultiplier);
        a.SetFloat(DoorOpenSpeedParameter, speed);

        if (showDebugLogs)
            Debug.Log($"[MotherAnimationPlayer] Door_Open 速度パラメーター {DoorOpenSpeedParameter} = {speed:F2}");
    }

    /// <summary>
    /// 歩行アニメーション（Walk / Walk_Fast）の再生速度倍率を Animator へ適用する。
    ///  ・Animator 全体の speed は変更しない（歩行ステート専用の Float パラメーターだけを設定する）。
    ///  ・移動（waypoints）の移動速度・旋回速度・ルート、および歩行以外のステートの再生速度は変更しない。
    ///  ・1.0 を設定すれば従来と同じ再生速度（パラメーターの既定値も 1）。
    ///  ・0 以下は 0.01 に補正する（歩行が完全に止まらないようにする）。
    /// </summary>
    private void ApplyWalkAnimationSpeed()
    {
        Animator a = ResolveAnimator();
        if (a == null) return;

        if (!HasParameterOn(a, WalkSpeedParameter, AnimatorControllerParameterType.Float))
        {
            Debug.LogWarning($"[MotherAnimationPlayer] Animator に Float '{WalkSpeedParameter}' がありません。" +
                             "歩行アニメーションの速度調整をスキップします（Animator Controller の" +
                             "Walk / Walk_Fast へ Speed Multiplier として登録してください）", this);
            return;
        }

        float speed = Mathf.Max(0.01f, _walkSpeedOverride > 0f ? _walkSpeedOverride : walkAnimationSpeedMultiplier);
        a.SetFloat(WalkSpeedParameter, speed);

        if (showDebugLogs)
            Debug.Log($"[MotherAnimationPlayer] 歩行アニメーション速度パラメーター {WalkSpeedParameter} = {speed:F2}（移動速度は変更しない）");
    }

    /// <summary>【片付け】Chore_Peek の再生完了を待つ（成功/失敗を onResult で返す）。</summary>
    public IEnumerator WaitForChorePeek(System.Action<bool> onResult = null) =>
        WaitForOneShot(choreLookStateName, onResult);

    /// <summary>【片付け】Chore_End の再生完了を待つ（成功/失敗を onResult で返す）。</summary>
    public IEnumerator WaitForChoreEnd(System.Action<bool> onResult = null) =>
        WaitForOneShot(choreEndStateName, onResult);

    /// <summary>【片付け】Door_Open の再生完了を待つ（成功/失敗を onResult で返す）。</summary>
    public IEnumerator WaitForDoorOpen(System.Action<bool> onResult = null) =>
        WaitForOneShot(doorOpenStateName, onResult);

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
        // 押すタイミング通知もクリアする（中断・再プレイで持ち越さない）。
        _doorOpenPushSignaled = false;

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
    //  ゲームオーバー演出（捕獲時）用 API
    //  ・Animator への書き込みは本クラスに集約する。呼び出し側はステート名を渡さない。
    //  ・Walk_Fast / Jumpscare は遷移を持たないため Animator.Play で入る（Play は本クラス内だけ）。
    //  ・Animator 全体の speed は変更しない（歩行は WalkSpeed、ドアは Door_OpenSpeed）。
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>ゲームオーバー演出の開始を宣言する（GameOverActive=true。通常の歩行復帰で演出を上書きしない）。</summary>
    public void BeginGameOverPerformance()
    {
        _gameOverPerformanceActive = true;
        SetBoolParameter(GameOverActiveParameter, true);
    }

    /// <summary>
    /// ゲームオーバー演出の終了を宣言する。
    ///  ・GameOverActive=false／WalkFast=false／未消費 JumpscareTrigger を消す（再プレイへ持ち越さない）。
    ///  ・歩行速度の上書きも通常値へ戻す。
    /// </summary>
    public void EndGameOverPerformance()
    {
        _gameOverPerformanceActive = false;
        _walkSpeedOverride = -1f;
        ResetGameOverParameters();
        ApplyWalkAnimationSpeed();
    }

    /// <summary>
    /// ゲームオーバー用パラメーター（GameOverActive / WalkFast / JumpscareTrigger）を初期状態へ戻す。
    /// 中断・終了・再プレイで未消費の値・Trigger を残さない。
    /// </summary>
    public void ResetGameOverParameters()
    {
        SetBoolParameter(GameOverActiveParameter, false);
        SetBoolParameter(WalkFastParameter, false);
        ResetTriggerIfExists(ResolveAnimator(), JumpscareParameter);
        ResetTriggerIfExists(ResolveAnimator(), GameOverStopParameter);
    }

    /// <summary>
    /// ゲームオーバー演出で「移動要求を解除し、割り込み用 Trigger を消す」共通処理。
    ///  ・Walk=false／WalkFast=false を確実にする（Any State→Walk_Fast の再発火を防ぐ）。
    ///  ・古い GameOverStopTrigger を消す（停止要求後に別ステートへ移っても、
    ///    残った Trigger が後から Idle へ引き戻さないようにする）。
    /// </summary>
    private void ClearGameOverMovementRequests()
    {
        Animator a = ResolveAnimator();
        SetWalkingInternal(false);
        SetBoolParameter(WalkFastParameter, false);
        ResetTriggerIfExists(a, GameOverStopParameter);
    }

    /// <summary>
    /// 早歩き（Walk_Fast）を要求する。
    ///  ・Animator.Play は使わない。Controller の遷移
    ///    「Any State → Walk_Fast（GameOverActive==true かつ WalkFast==true）」で入る。
    ///  ・歩行速度は WalkSpeed を早歩き用の倍率に上書きする（waypoints の移動速度は変更しない）。
    ///  ・必要な Bool/ステートが無い場合は false を返す（直接 Play へ黙ってフォールバックしない）。
    /// </summary>
    public bool RequestWalkFast()
    {
        Animator a = ResolveAnimator();
        if (a == null)
        {
            Debug.LogWarning("[MotherAnimationPlayer] Animatorを解決できないため、早歩きを要求できません（失敗）", this);
            return false;
        }

        if (!HasParameterOn(a, WalkFastParameter, AnimatorControllerParameterType.Bool))
        {
            Debug.LogWarning($"[MotherAnimationPlayer] Bool '{WalkFastParameter}' が無いため早歩きに入れません（失敗）。" +
                             "Animator Controller へ登録してください", this);
            return false;
        }

        if (!HasState(walkFastStateName))
        {
            Debug.LogWarning($"[MotherAnimationPlayer] ステート '{walkFastStateName}' が無いため早歩きに入れません（失敗）", this);
            return false;
        }

        // 早歩き中は WalkSpeed を早歩き用の倍率に保つ。
        _walkSpeedOverride = gameOverWalkFastAnimationSpeedMultiplier;
        ApplyWalkAnimationSpeed();

        // Walk=false／WalkFast=false／古い停止Trigger を消してから、WalkFast=true を立てる。
        ClearGameOverMovementRequests();
        SetBoolParameter(WalkFastParameter, true);

        if (showDebugLogs)
            Debug.Log($"[MotherAnimationPlayer] 早歩き要求: {WalkFastParameter}=true（遷移で {walkFastStateName} へ。速度倍率={gameOverWalkFastAnimationSpeedMultiplier:F2}）");
        return true;
    }

    /// <summary>
    /// 停止要求（ゲームオーバー演出）：早歩きを経由していなくても「停止状態（Idle）」へ入れる。
    ///  ・Walk=false／WalkFast=false を確実にし、GameOverStopTrigger を発火する。
    ///  ・遷移は Controller 側の「Any State → Idle（GameOverActive==true かつ GameOverStopTrigger）」で行う。
    ///  ・停止状態への到達待ちは WaitForStopState を使う（呼び出し側は到達前に次の段階へ進まない）。
    ///  ・必要な Bool/Trigger が無い場合は false を返す（直接 Play へはフォールバックしない）。
    /// </summary>
    public bool RequestStop()
    {
        Animator a = ResolveAnimator();
        if (a == null)
        {
            Debug.LogWarning("[MotherAnimationPlayer] Animatorを解決できないため、停止要求を出せません（失敗）", this);
            return false;
        }

        if (!HasParameterOn(a, GameOverStopParameter, AnimatorControllerParameterType.Trigger))
        {
            Debug.LogWarning($"[MotherAnimationPlayer] Trigger '{GameOverStopParameter}' が無いため、停止状態へ入れません（失敗）。" +
                             "Animator Controller へ登録してください", this);
            return false;
        }

        if (!HasState(idleStateName))
        {
            Debug.LogWarning($"[MotherAnimationPlayer] 停止状態の '{idleStateName}' が無いため、停止状態へ入れません（失敗）", this);
            return false;
        }

        // 移動要求（Walk/WalkFast）を解除し、古い停止Trigger を消してから停止Trigger を発火する。
        ClearGameOverMovementRequests();
        a.SetTrigger(GameOverStopParameter);

        if (showDebugLogs)
            Debug.Log($"[MotherAnimationPlayer] 停止要求: {GameOverStopParameter}（遷移で {idleStateName} へ）");
        return true;
    }

    /// <summary>
    /// 停止状態（Idle）への到達をタイムアウト付きで待つ。到達できなければ false（打ち切り判断用）。
    ///  ・遷移中は到達とみなさない。
    /// </summary>
    public IEnumerator WaitForStopState(float timeoutSeconds, System.Action<bool> onResult = null)
    {
        Animator a = ResolveAnimator();
        if (a == null || !HasState(idleStateName))
        {
            Debug.LogWarning("[MotherAnimationPlayer] 停止状態の到達を確認できません（Animator/ステート未解決）", this);
            yield return new WaitForSeconds(fallbackWaitSeconds);
            onResult?.Invoke(false);
            yield break;
        }

        float timeout = Mathf.Max(0.1f, timeoutSeconds);
        float elapsed = 0f;

        while (elapsed < timeout)
        {
            AnimatorStateInfo info = a.GetCurrentAnimatorStateInfo(Layer);
            if (!a.IsInTransition(Layer) && info.IsName(idleStateName))
            {
                if (showDebugLogs)
                    Debug.Log($"[MotherAnimationPlayer] 停止状態（{idleStateName}）へ到達しました（{elapsed:F2}s）");
                onResult?.Invoke(true);
                yield break;
            }

            elapsed += Time.deltaTime;
            yield return null;
        }

        Debug.LogWarning($"[MotherAnimationPlayer] 停止状態（{idleStateName}）へ {timeout:F1}s で到達できませんでした（失敗）", this);
        onResult?.Invoke(false);
    }

    /// <summary>
    /// 指定ステートへの到達（遷移完了）をタイムアウト付きで待つ。
    ///  ・遷移中は到達とみなさない。
    ///  ・到達できなければ false（呼び出し側は打ち切り判断に使う）。
    /// </summary>
    public IEnumerator WaitForStateArrival(string stateName, float timeoutSeconds, System.Action<bool> onResult = null)
    {
        Animator a = ResolveAnimator();
        if (a == null || string.IsNullOrEmpty(stateName) || !HasState(stateName))
        {
            Debug.LogWarning($"[MotherAnimationPlayer] ステート '{stateName}' の到達を確認できません（Animator/ステート未解決）", this);
            yield return new WaitForSeconds(fallbackWaitSeconds);
            onResult?.Invoke(false);
            yield break;
        }

        float timeout = Mathf.Max(0.1f, timeoutSeconds);
        float elapsed = 0f;

        while (elapsed < timeout)
        {
            AnimatorStateInfo info = a.GetCurrentAnimatorStateInfo(Layer);
            if (!a.IsInTransition(Layer) && info.IsName(stateName))
            {
                if (showDebugLogs)
                    Debug.Log($"[MotherAnimationPlayer] ステート '{stateName}' へ到達しました（{elapsed:F2}s）");
                onResult?.Invoke(true);
                yield break;
            }

            elapsed += Time.deltaTime;
            yield return null;
        }

        Debug.LogWarning($"[MotherAnimationPlayer] ステート '{stateName}' へ {timeout:F1}s で到達できませんでした（失敗）", this);
        onResult?.Invoke(false);
    }

    /// <summary>早歩き（Walk_Fast）ステートへの到達を待つ。</summary>
    public IEnumerator WaitForWalkFastState(float timeoutSeconds, System.Action<bool> onResult = null) =>
        WaitForStateArrival(walkFastStateName, timeoutSeconds, onResult);

    /// <summary>通常歩き（Walk）ステートへの到達を待つ。</summary>
    public IEnumerator WaitForWalkState(float timeoutSeconds, System.Action<bool> onResult = null) =>
        WaitForStateArrival(walkStateName, timeoutSeconds, onResult);

    /// <summary>
    /// 通常歩き（Walk）へ戻す（ゲームオーバー演出の移動区間に使う）。
    ///  ・既存の Idle→Walk 遷移（Walk=true）を使う。歩行速度の上書きは解除する。
    /// </summary>
    public void RequestWalk()
    {
        _walkSpeedOverride = -1f;
        // WalkFast=false／古い停止Trigger を消してから、通常歩き（Walk=true）へ。
        ClearGameOverMovementRequests();
        // 演出用の歩行要求は SetWalking（演出中は拒否）ではなく内部処理を使う（ロックで演出を止めない）。
        SetWalkingInternal(true);

        if (showDebugLogs)
            Debug.Log("[MotherAnimationPlayer] 歩行要求（通常歩き）");
    }

    /// <summary>
    /// ジャンプスケア（Jumpscare）を1回再生する要求を出す。
    ///  ・Animator.Play は使わない。Controller の遷移
    ///    「Any State → Jumpscare（GameOverActive==true かつ JumpscareTrigger）」で入る。
    ///  ・開始できたら true、パラメーター/ステートが無い場合は false（直接 Play へ黙ってフォールバックしない）。
    /// </summary>
    public bool RequestJumpScare()
    {
        Animator a = ResolveAnimator();
        if (a == null)
        {
            Debug.LogWarning("[MotherAnimationPlayer] Animatorを解決できないため、ジャンプスケアを要求できません（失敗）", this);
            return false;
        }

        if (!HasParameterOn(a, JumpscareParameter, AnimatorControllerParameterType.Trigger))
        {
            Debug.LogWarning($"[MotherAnimationPlayer] Trigger '{JumpscareParameter}' が無いためジャンプスケアに入れません（失敗）。" +
                             "Animator Controller へ登録してください", this);
            return false;
        }

        if (!HasState(jumpScareStateName))
        {
            Debug.LogWarning($"[MotherAnimationPlayer] ステート '{jumpScareStateName}' が無いためジャンプスケアに入れません（失敗）", this);
            return false;
        }

        // 歩行の取り違えと古い停止Trigger を残さない（呼び出し側の順番にも依存しない）。
        ClearGameOverMovementRequests();

        // 未消費の Trigger を消してから発火する。
        a.ResetTrigger(JumpscareParameter);
        a.SetTrigger(JumpscareParameter);

        if (showDebugLogs)
            Debug.Log($"[MotherAnimationPlayer] ジャンプスケア要求: {JumpscareParameter}（遷移で {jumpScareStateName} へ）");
        return true;
    }

    /// <summary>
    /// ジャンプスケアの再生完了を待つ（成功/失敗を onResult で返す）。
    ///  ・未開始・ステート未登録・タイムアウトは失敗（正常完了と混同しない）。
    /// </summary>
    public IEnumerator WaitForJumpScare(System.Action<bool> onResult = null) =>
        WaitForOneShot(jumpScareStateName, onResult);

    /// <summary>Bool パラメーターがあれば設定する（無ければ警告のみ。直接 Play へはフォールバックしない）。</summary>
    private void SetBoolParameter(string name, bool value)
    {
        Animator a = ResolveAnimator();
        if (a == null) return;

        if (!HasParameterOn(a, name, AnimatorControllerParameterType.Bool))
        {
            Debug.LogWarning($"[MotherAnimationPlayer] Animator に Bool '{name}' がありません。Animator Controller へ登録してください", this);
            return;
        }

        a.SetBool(name, value);
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
        // 演出中は public SetWalking が拒否されるため、内部処理を使う
        // （演出のドア開け・立ち上がり要求までロックで止めない）。
        SetWalkingInternal(false);
        a.ResetTrigger(triggerName);
        a.SetTrigger(triggerName);

        if (showDebugLogs)
            Debug.Log($"[MotherAnimationPlayer] 遷移要求: {triggerName}");
    }

    /// <summary>
    /// 【内部実装】1回再生ステートの完了を待つ。結果を success で返す（未開始・中断・タイムアウトと正常終了を混同しない）。
    /// ステート名を外部へ渡さないよう private にする（外部は用途別API
    /// WaitForChorePeek / WaitForChoreEnd / WaitForDoorOpen を使う）。
    ///  成功条件：ステートへ到達し、その再生が最後まで進んだ（normalizedTime>=1 もしくは正式な退出）。
    ///  失敗：Animator未解決／ステート未登録／到達タイムアウト／完了タイムアウト／中断。
    /// </summary>
    private IEnumerator WaitForOneShot(string stateName, System.Action<bool> onResult) =>
        WaitForOneShotState(stateName, onResult);

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
        //    ・normalizedTime>=1 を観測 → 正常終了。
        //    ・完了前に別ステートへ退出 → 想定外の退出（失敗）。
        //    ・ループ設定 → 設定ミス（警告して失敗）。
        //    ・Animator無効化／タイムアウト → 失敗。
        float elapsed = 0f;
        float maxNormalized = 0f;   // 対象ステートで観測した最大 normalizedTime
        bool completed = false;
        bool failed = false;
        string failReason = null;

        while (elapsed < oneShotTimeoutSeconds)
        {
            // Animator が無効化された（シーン変更・中断など）→ 失敗。
            if (a == null || !a.isActiveAndEnabled)
            {
                failReason = "Animatorが無効化されました";
                failed = true;
                break;
            }

            AnimatorStateInfo info = a.GetCurrentAnimatorStateInfo(Layer);

            if (info.IsName(stateName))
            {
                // 3) ワンショット対象のループ設定は誤り。即成功にせず警告して失敗とする。
                if (info.loop)
                {
                    failReason = $"ステート '{stateName}' がループ設定です（ワンショット対象にループは不正）";
                    failed = true;
                    break;
                }

                if (info.normalizedTime >= 1f) { completed = true; break; }
                if (info.normalizedTime > maxNormalized) maxNormalized = info.normalizedTime;
            }
            else if (!a.IsInTransition(Layer))
            {
                // 2) 別ステートへ退出した。完了（normalizedTime>=1）を観測済みなら正常終了、
                //    そうでなければ想定外の退出として失敗にする。
                if (maxNormalized >= CompletionNormalizedTolerance) completed = true;
                else
                {
                    failReason = $"ステート '{stateName}' が完了前に退出しました" +
                                 $"（normalizedTime={maxNormalized:F2}）";
                    failed = true;
                }
                break;
            }

            elapsed += Time.deltaTime;
            yield return null;
        }

        if (failed)
        {
            Debug.LogWarning($"[MotherAnimationPlayer] {failReason} — 失敗", this);
            onResult?.Invoke(false);
            yield break;
        }

        if (!completed)
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
