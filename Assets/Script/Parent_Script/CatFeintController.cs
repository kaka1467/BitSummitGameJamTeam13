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

    private Vector3 _homePosition;
    private Quaternion _homeRotation;
    private Coroutine _meowCoroutine;
    private bool _walkAborted;
    private Animator _animator;
    private bool _hasWalkParameter;
    private bool _hasJumpParameter;
    private bool _hasIdleState;
    private bool _animatorWarningLogged;

    private const string WalkParameter = "Walk";
    private const string JumpParameter = "Jump";
    private const string IdleState = "Idle";

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
        if (catObject == null) return;
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
