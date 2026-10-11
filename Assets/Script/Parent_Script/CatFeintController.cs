using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// CatFeintController：猫フェイント（3キー）で、猫だけを母親のドア側と同じwaypoint順で移動させ、
/// DoorPoint到着後にドアから覗く猫として見せるためのコンポーネント。
/// 経路はMotherApproachControllerの公開参照（startPoint／廊下ルートList／turnPoint／doorPoint）をそのまま再利用し、
/// 猫本体（catObject）だけを移動させる（母親モデル・母親のイベントは一切動かさない）。
/// 移動アニメーションは猫の実移動中だけ再生する。鳴き声は専用meowAudioSourceから再生する。
/// </summary>
public class CatFeintController : MonoBehaviour
{
    [Header("猫の参照")]
    [Tooltip("猫フェイントで移動・表示する猫のGameObject。移動開始時にStartPointへスナップされるため、Scene上の配置位置はどこでもよい。")]
    [SerializeField] private GameObject catObject;

    [Tooltip("猫の鳴き声を再生する専用のAudioSource。既存の親の音源とは別に割り当てる。")]
    [SerializeField] private AudioSource meowAudioSource;

    [Header("猫の足音")]
    [Tooltip("猫が移動中に使う専用の足音AudioSource。")]
    [SerializeField] private AudioSource catFootstepAudioSource;

    [Range(0f, 1f)]
    [Tooltip("猫の足音の音量。")]
    [SerializeField] private float footstepVolume = 0.35f;

    [Header("経路（母親と同じwaypoint参照を再利用）")]
    [Tooltip("母親のドア側経路を持つMotherApproachController。startPoint〜doorPointの公開参照をそのまま使う。")]
    [SerializeField] private MotherApproachController routeController;

    [Header("移動")]
    [Tooltip("猫の移動速度（単位／秒）。")]
    [SerializeField] private float catMoveSpeed = 4f;

    [Tooltip("旋回速度（度／秒）。TurnPoint／DoorPoint到着時の向き調整に使う。")]
    [SerializeField] private float catTurnSpeed = 90f;

    [Tooltip("到着と判定するウェイポイントまでの距離（単位）。")]
    [SerializeField] private float stopDistance = 0.05f;

    [Tooltip("猫の足が床に埋まる場合に、ワールドY方向へ持ち上げるオフセット（単位）。StartPointへの出現・各waypoint移動・DoorPoint到着・帰位のすべてに同じ値が1回だけ適用される（累積しない）。Playで猫の足元を見ながら調整する（初期値0=持ち上げなし）。")]
    [SerializeField] private float catHeightOffset;

    [Header("表示タイミング")]
    [Tooltip("ドアが開いてから鳴き声を再生するまでの秒数。")]
    [SerializeField] private float meowDelaySeconds;

    [Tooltip("ドアを閉じる前に猫を非表示にしておく秒数（ドアを閉じるX秒前に消す）。")]
    [SerializeField] private float catHideBeforeCloseSeconds;

    // ── ドア開放時間（猫フェイント） ──────────────────────────────────────────
    [Header("ドア開放時間")]
    [Tooltip("猫フェイントでドアを開けておく時間（秒）。\n" +
             "ドアの開く・閉じる速度は変更しない。\n" +
             "・カウントは「ドアが目標開度（Full）に実際に到達した時点」から始めます。\n" +
             "・怪しさゲージ分（MotherSuspicionSystem の現在ゲージ）が加算されます（従来と同じ扱い）。\n" +
             "・猫は閉める前に必ず非表示になります（catHideBeforeCloseSeconds はこの時間のうちに\n" +
             "  猫を隠すタイミングで、待ち時間は加算されません）。\n" +
             "・猫のジャンプ動作がこの時間より長い場合は、ジャンプを見せ終えるまで\n" +
             "  ドアを開けたままにします（その分、実際の開放時間は長くなります）。\n" +
             "・catHideBeforeCloseSeconds がこの値より大きい場合だけ、その時間までドアが開いたままになります。")]
    [SerializeField, Min(0f)] private float catDoorHoldOpenSeconds = 7f;

    [Tooltip("ドアが目標開度（Full）へ到達するのを待つ上限（秒）。\n" +
             "超えた場合は到達失敗として扱い、開放時間は数えません\n" +
             "（猫を隠す→ドアを閉める→状態解除の安全な失敗終了。通常終了とはログで区別）。")]
    [SerializeField, Min(0.1f)] private float doorReachWaitTimeoutSeconds = 5f;

    // ── ジャンプの完了判定 ────────────────────────────────────────────────────
    [Header("ジャンプの完了判定")]
    [Tooltip("猫のジャンプ状態名（Animator Controller 側の名前）。\n" +
             "ジャンプを見せ終えてから猫を隠す判定に使う（固定秒数では断定しない）。")]
    [SerializeField] private string jumpStateName = "Armature_001|Cat Jump";

    [Tooltip("ジャンプの終了（待機状態へ戻る）を待つ上限（秒）。\n" +
             "超えた場合はアニメ完了と断定せず、警告を出して猫を隠します（ドアは閉じます）。")]
    [SerializeField, Min(0.1f)] private float jumpWaitTimeoutSeconds = 3f;

    private Vector3 _homePosition;
    private Quaternion _homeRotation;
    private Coroutine _meowCoroutine;
    private bool _walkAborted;
    private Animator _animator;
    private bool _hasWalkParameter;
    private bool _hasJumpParameter;
    private bool _hasIdleState;
    private bool _animatorWarningLogged;

    // ジャンプ観測（TriggerJump の時点から開始／終了を区別して記録する）
    private bool _jumpRequested;   // TriggerJump でジャンプを要求した（観測対象）
    private bool _jumpStarted;     // Jump状態への遷移開始、または Jump状態の開始を確認した
    private bool _jumpExited;      // 開始を確認した後に Jump状態と終了遷移から抜けた

    private const string WalkParameter = "Walk";
    private const string JumpParameter = "Jump";
    private const string IdleState = "Idle";

    /// <summary>
    /// Inspectorで0以下が入った場合に下限へ補正する（開放時間0以下・待ち上限0以下を防ぐ）。
    ///  ・実行時も DoorHoldOpenSeconds / DoorReachWaitTimeoutSeconds / JumpWaitTimeoutSeconds で再検証する。
    /// </summary>
    private void OnValidate()
    {
        catDoorHoldOpenSeconds = Mathf.Max(0f, catDoorHoldOpenSeconds);
        doorReachWaitTimeoutSeconds = Mathf.Max(0.1f, doorReachWaitTimeoutSeconds);
        jumpWaitTimeoutSeconds = Mathf.Max(0.1f, jumpWaitTimeoutSeconds);
    }

    private void Start()
    {
        if (catObject != null)
        {
            _homePosition = catObject.transform.position;
            _homeRotation = catObject.transform.rotation;
            _animator = catObject.GetComponentInChildren<Animator>(true);
            CacheAnimatorParameters();
            ResetAnimationState();
            // 通常プレイ中は猫を出しておかない（3キー開始時にStartPointへ出現させる）。
            catObject.SetActive(false);
        }
    }

    /// <summary>ドアを閉じる前に猫を隠しておく秒数（MotherSuspicionSystemのドア閉鎖タイミング計算に使う）。</summary>
    public float HideBeforeCloseSeconds => Mathf.Max(0f, catHideBeforeCloseSeconds);

    /// <summary>
    /// 猫フェイントでドアを開けておく時間（秒）。0以上。
    ///  ・MotherSuspicionSystem が「ドアがFullへ到達した時点」からこの時間を数えてドアを閉める。
    ///  ・怪しさゲージ分の加算は呼び出し側（MotherSuspicionSystem）が行う（従来と同じ扱い）。
    ///  ・ドアの回転速度・開度は変更しない（既存 DoorController の通常速度を使う）。
    /// </summary>
    public float DoorHoldOpenSeconds => Mathf.Max(0f, catDoorHoldOpenSeconds);

    /// <summary>ドアが目標開度（Full）へ到達するのを待つ上限（秒）。</summary>
    public float DoorReachWaitTimeoutSeconds => Mathf.Max(0.1f, doorReachWaitTimeoutSeconds);

    /// <summary>
    /// ジャンプ完了待ちの観測結果。
    ///  ・bool と違い「ジャンプ設定なし（待機対象なし）」と「完了」「タイムアウト失敗」を区別する。
    /// </summary>
    public enum JumpWaitResult
    {
        /// <summary>Jump パラメーターが無い、または今回のフェイントでジャンプ要求が無い＝待機対象なし（「完了」とは区別）。</summary>
        NotConfigured,

        /// <summary>開始（Jump状態への遷移開始を含む）を確認し、その後に Jump状態と終了遷移から抜けた。</summary>
        Completed,

        /// <summary>上限時間内に開始または終了を確認できなかった（開始できない場合を含む）。呼び出し側は失敗として安全終了する。</summary>
        TimedOut,
    }

    /// <summary>ジャンプ状態（jumpStateName）を再生中か。Animator 未解決なら false。</summary>
    public bool IsJumpPlaying
    {
        get
        {
            if (_animator == null || string.IsNullOrEmpty(jumpStateName)) return false;
            return _animator.GetCurrentAnimatorStateInfo(0).IsName(jumpStateName);
        }
    }

    /// <summary>Jump状態へ向かう遷移中か（＝開始の観測に使う。遷移完了を待たずに「開始した」と判断できる）。</summary>
    public bool IsJumpStarting
    {
        get
        {
            if (_animator == null || string.IsNullOrEmpty(jumpStateName)) return false;
            return _animator.IsInTransition(0) && _animator.GetNextAnimatorStateInfo(0).IsName(jumpStateName);
        }
    }

    /// <summary>Jump状態へ向かう遷移中、または Jump状態を再生中か（＝ジャンプが始まっている）。</summary>
    public bool IsJumpEntering => IsJumpPlaying || IsJumpStarting;

    /// <summary>ジャンプの終了を待つ上限（秒）。</summary>
    public float JumpWaitTimeoutSeconds => Mathf.Max(0.1f, jumpWaitTimeoutSeconds);

    /// <summary>
    /// ジャンプの進行を1フレーム分観測する。TriggerJump の時点から、猫フェイントの待機ループと
    /// WaitForJumpFinished が毎フレーム呼ぶ（開放時間内に終わったジャンプも「完了」として記録するため）。
    ///  ・開始＝「Jump状態への遷移開始」または「Jump状態」を確認したこと。
    ///  ・終了＝開始を確認した後に、Jump状態でも Jump状態への遷移中でもなくなったこと
    ///    （＝終了遷移も抜けた。既存 Animator の ExitTime=0.8889 は変更しない／「クリップの最後まで再生」とは断定しない）。
    ///  ・未開始のうちは「Jump状態でない」ことを終了とみなさない（開始前の即完了を防ぐ）。
    /// </summary>
    public void ObserveJumpProgress()
    {
        if (!_jumpRequested || _jumpExited) return;
        if (_animator == null || !_hasJumpParameter) return;

        if (IsJumpEntering)
        {
            _jumpStarted = true;
            return;
        }

        if (_jumpStarted)
            _jumpExited = true;
    }

    /// <summary>
    /// ジャンプ動作を見せ終えるまで待つ。判定は Animator の状態で行い、固定秒数で完了と断定しない。
    ///  ・開始を確認していない状態で「Jump状態でない」ことを完了とみなさない（開放時間0秒でも開始前に即成功しない）。
    ///  ・開始（遷移開始を含む）と、その後の終了遷移からの退出の両方を確認できた場合のみ Completed。
    ///  ・開始できない／終了遷移を抜けない場合は timeoutSeconds 内で TimedOut（待機を無制限に増やさない）。
    ///  ・Jump パラメーターが無い／ジャンプ要求が無い場合は NotConfigured（「ジャンプ完了」とは区別）。
    ///  ・観測は TriggerJump の時点から始まっており、開放時間内に完了したジャンプは即 Completed になる。
    ///  ・中断時は呼び出し側がこの待機を行わない（安全に終了してよい）。
    /// </summary>
    public IEnumerator WaitForJumpFinished(float timeoutSeconds, Action<JumpWaitResult> onResult = null)
    {
        if (_animator == null || !_hasJumpParameter || !_jumpRequested)
        {
            onResult?.Invoke(JumpWaitResult.NotConfigured);   // ジャンプ設定なし・待機対象なし
            yield break;
        }

        ObserveJumpProgress();
        if (_jumpExited)
        {
            onResult?.Invoke(JumpWaitResult.Completed);   // 開放時間の待機中に終了まで観測済み
            yield break;
        }

        float timeout = Mathf.Max(0.1f, timeoutSeconds);
        float elapsed = 0f;

        while (elapsed < timeout)
        {
            ObserveJumpProgress();
            if (_jumpExited)
            {
                onResult?.Invoke(JumpWaitResult.Completed);
                yield break;
            }

            elapsed += Time.deltaTime;
            yield return null;
        }

        onResult?.Invoke(JumpWaitResult.TimedOut);   // 開始できない／終了遷移を抜けない（呼び出し側が警告して安全終了）
    }

    /// <summary>
    /// 猫をStartPointに出現させ、母親のDoorRoutineと同じwaypoint順でDoorPointまで移動させる。
    /// shouldContinueがfalseを返すと中断する（後始末は呼び出し側で行う）。
    /// </summary>
    public IEnumerator MoveAlongDoorRoute(Func<bool> shouldContinue)
    {
        _walkAborted = false;
        ResetAnimationState();

        if (routeController == null || catObject == null)
        {
            Debug.LogWarning("[CatFeint] routeController／catObjectが未設定のため猫を移動できません。Inspectorで割り当ててください。", this);
            yield break;
        }

        // StartPointに出現（母親と同じくStartPointの位置・向きで初期化。高さはcatHeightOffsetを1回だけ加算）。
        Transform startPoint = routeController.startPoint;
        if (startPoint != null)
            catObject.transform.SetPositionAndRotation(CatGoalPosition(startPoint.position), startPoint.rotation);
        catObject.SetActive(true);
        StartCatFootsteps();

        // 母親と同じ廊下ルートを辿る：TurnPointより前 → TurnPoint（旋回）→ TurnPointより後 → DoorPoint（旋回）。
        // 経路は母親側のInspector設定（List）から取得するため、点を増減してもコード変更は不要。
        List<Transform> beforeTurn = routeController.GetHallwayPointsBeforeTurn();
        for (int i = 0; i < beforeTurn.Count; i++)
        {
            yield return MoveCatTo(beforeTurn[i], shouldContinue);
            if (_walkAborted) yield break;
        }

        if (routeController.turnPoint != null)
        {
            yield return MoveCatTo(routeController.turnPoint, shouldContinue);
            if (_walkAborted) yield break;
            yield return RotateCatToYaw(routeController.turnPoint.rotation.eulerAngles.y, shouldContinue);
            if (_walkAborted) yield break;
        }

        List<Transform> afterTurn = routeController.GetHallwayPointsAfterTurn();
        for (int i = 0; i < afterTurn.Count; i++)
        {
            yield return MoveCatTo(afterTurn[i], shouldContinue);
            if (_walkAborted) yield break;
        }

        if (routeController.doorPoint != null)
        {
            yield return MoveCatTo(routeController.doorPoint, shouldContinue);
            if (_walkAborted) yield break;
            yield return RotateCatToYaw(routeController.doorPoint.rotation.eulerAngles.y, shouldContinue);
            if (_walkAborted) yield break;
        }

        StopCatFootsteps();
        SetWalking(false);
    }

    /// <summary>ドアをPeek状態にした直後にJumpを1回だけ再生する。</summary>
    public void TriggerJump()
    {
        if (_animator == null)
        {
            LogAnimatorWarning();
            return;
        }

        if (!_hasJumpParameter)
        {
            LogAnimatorWarning();
            return;
        }

        SetWalking(false);
        _animator.ResetTrigger(JumpParameter);
        _animator.SetTrigger(JumpParameter);

        // この「ジャンプ要求」の時点から開始／終了の観測を始める（開放時間の待機中も観測を続ける）。
        _jumpRequested = true;
        _jumpStarted = false;
        _jumpExited = false;
    }

    /// <summary>ドアが開いたタイミングで鳴き声を再生する（meowDelaySecondsだけ遅らせられる）。</summary>
    public void PlayMeow()
    {
        if (_meowCoroutine != null)
        {
            StopCoroutine(_meowCoroutine);
            _meowCoroutine = null;
        }
        _meowCoroutine = StartCoroutine(PlayMeowCoroutine());
    }

    /// <summary>猫を非表示にする（鳴き声待ちも停止。終了・中断時に必ず呼ぶ）。</summary>
    public void HideCat()
    {
        if (_meowCoroutine != null)
        {
            StopCoroutine(_meowCoroutine);
            _meowCoroutine = null;
        }
        StopCatFootsteps();
        ResetAnimationState();
        if (catObject != null && catObject.activeSelf)
            catObject.SetActive(false);
    }

    /// <summary>猫を次回用の開始状態へ戻す（ホーム位置・向きに戻して非表示。高さはcatHeightOffsetを1回だけ加算）。</summary>
    public void ReturnToStartPosition()
    {
        // ジャンプ観測を次回サイクル用にクリア（前回の「開始／終了」を今回の猫フェイントへ持ち越さない）。
        _jumpRequested = false;
        _jumpStarted = false;
        _jumpExited = false;

        if (catObject == null) return;

        // 鳴き声待ち（meowDelaySeconds）が残っていても、戻した後に遅れて鳴らないように必ず止める。
        if (_meowCoroutine != null)
        {
            StopCoroutine(_meowCoroutine);
            _meowCoroutine = null;
        }

        StopCatFootsteps();
        ResetAnimationState();
        catObject.transform.SetPositionAndRotation(CatGoalPosition(_homePosition), _homeRotation);
        catObject.SetActive(false);
    }

    /// <summary>
    /// 目標位置へcatHeightOffset（ワールドY）を1回だけ加算した到達点を返す。
    /// 常にwaypoint（またはホーム）の生座標から再計算するため、フレーム間・waypoint間でオフセットは累積しない。
    /// </summary>
    private Vector3 CatGoalPosition(Vector3 targetPosition)
    {
        return new Vector3(targetPosition.x, targetPosition.y + catHeightOffset, targetPosition.z);
    }

    private IEnumerator MoveCatTo(Transform target, Func<bool> shouldContinue)
    {
        if (target == null) yield break; // 母親側と同じnull安全スキップ

        SetWalking(true);

        // 到達点は「waypoint位置＋catHeightOffset」を毎回waypointの生座標から再計算する。
        // 現在位置へオフセットを加算しないため、フレーム間・waypoint間で猫が浮き上がり続けたり
        // 加算が累積したりしない（Yは常にwaypoint＋offsetの一定高さを保つ）。
        Vector3 goal = CatGoalPosition(target.position);

        while (Vector3.Distance(catObject.transform.position, goal) > stopDistance)
        {
            if (shouldContinue != null && !shouldContinue())
            {
                _walkAborted = true;
                ResetAnimationState();
                yield break;
            }
            catObject.transform.position = Vector3.MoveTowards(
                catObject.transform.position, goal, catMoveSpeed * Time.deltaTime);
            yield return null;
        }
        catObject.transform.position = goal;
    }

    private IEnumerator RotateCatToYaw(float targetYaw, Func<bool> shouldContinue)
    {
        float target = NormalizeAngle(targetYaw);
        while (Mathf.Abs(NormalizeAngle(catObject.transform.rotation.eulerAngles.y) - target) > 0.5f)
        {
            if (shouldContinue != null && !shouldContinue())
            {
                _walkAborted = true;
                ResetAnimationState();
                yield break;
            }
            float newYaw = Mathf.MoveTowardsAngle(
                catObject.transform.rotation.eulerAngles.y, targetYaw, catTurnSpeed * Time.deltaTime);
            catObject.transform.rotation = Quaternion.Euler(0f, newYaw, 0f);
            yield return null;
        }
    }

    private IEnumerator PlayMeowCoroutine()
    {
        StopCatFootsteps();

        if (meowDelaySeconds > 0f)
            yield return new WaitForSeconds(meowDelaySeconds);

        if (meowAudioSource != null)
            meowAudioSource.Play();

        _meowCoroutine = null;
    }

    private void StartCatFootsteps()
    {
        if (catFootstepAudioSource == null)
            return;

        catFootstepAudioSource.loop = true;
        catFootstepAudioSource.volume = footstepVolume;
        if (!catFootstepAudioSource.isPlaying)
            catFootstepAudioSource.Play();
    }

    private void StopCatFootsteps()
    {
        if (catFootstepAudioSource == null)
            return;

        if (catFootstepAudioSource.isPlaying)
            catFootstepAudioSource.Stop();
    }

    private static float NormalizeAngle(float angle)
    {
        angle %= 360f;
        if (angle > 180f) angle -= 360f;
        if (angle < -180f) angle += 360f;
        return angle;
    }

    private void CacheAnimatorParameters()
    {
        _hasWalkParameter = false;
        _hasJumpParameter = false;
        _hasIdleState = false;

        if (_animator == null)
            return;

        _hasIdleState = _animator.HasState(0, Animator.StringToHash(IdleState));
        foreach (AnimatorControllerParameter parameter in _animator.parameters)
        {
            if (parameter.name == WalkParameter && parameter.type == AnimatorControllerParameterType.Bool)
                _hasWalkParameter = true;
            else if (parameter.name == JumpParameter && parameter.type == AnimatorControllerParameterType.Trigger)
                _hasJumpParameter = true;
        }

        if (!_hasWalkParameter || !_hasJumpParameter || !_hasIdleState)
            LogAnimatorWarning();
    }

    private void SetWalking(bool isWalking)
    {
        if (_animator == null)
        {
            LogAnimatorWarning();
            return;
        }

        if (!_hasWalkParameter)
        {
            LogAnimatorWarning();
            return;
        }

        _animator.SetBool(WalkParameter, isWalking);
    }

    private void ResetAnimationState()
    {
        StopCatFootsteps();
        SetWalking(false);

        if (_animator != null && _hasJumpParameter)
            _animator.ResetTrigger(JumpParameter);

        if (_animator != null && _hasIdleState)
            _animator.Play(IdleState, 0, 0f);
    }

    private void LogAnimatorWarning()
    {
        if (_animatorWarningLogged)
            return;

        _animatorWarningLogged = true;
        Debug.LogWarning(
            "[CatFeint] AnimatorまたはIdle State/Walk(bool)/Jump(trigger)が未設定のため、アニメーション制御をスキップします。猫フェイントは継続します。",
            this);
    }
}
