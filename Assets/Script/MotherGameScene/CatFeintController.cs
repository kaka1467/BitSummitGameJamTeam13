using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// CatFeintController：猫フェイント（3キー）で、猫だけを母親のドア側と同じwaypoint順で移動させ、
/// DoorPoint到着後にドアから覗く猫として見せるためのコンポーネント。
/// 経路はParentApproachControllerの公開waypoint参照（startPoint〜doorPoint）をそのまま再利用し、
/// 猫本体（catObject）だけを移動させる（母親モデル・母親のイベントは一切動かさない）。
/// 移動アニメーションは未導入（位置とY回転のみ）。鳴き声は専用meowAudioSourceから再生する。
/// </summary>
public class CatFeintController : MonoBehaviour
{
    [Header("猫の参照")]
    [Tooltip("猫フェイントで移動・表示する猫のGameObject。移動開始時にStartPointへスナップされるため、Scene上の配置位置はどこでもよい。")]
    [SerializeField] private GameObject catObject;

    [Tooltip("猫の鳴き声を再生する専用のAudioSource。既存の親の音源とは別に割り当てる。")]
    [SerializeField] private AudioSource meowAudioSource;

    [Header("経路（母親と同じwaypoint参照を再利用）")]
    [Tooltip("母親のドア側経路を持つParentApproachController。startPoint〜doorPointの公開参照をそのまま使う。")]
    [SerializeField] private ParentApproachController routeController;

    [Header("移動")]
    [Tooltip("猫の移動速度（単位／秒）。")]
    [SerializeField] private float catMoveSpeed = 4f;

    [Tooltip("旋回速度（度／秒）。TurnPoint／DoorPoint到着時の向き調整に使う。")]
    [SerializeField] private float catTurnSpeed = 90f;

    [Tooltip("到着と判定するウェイポイントまでの距離（単位）。")]
    [SerializeField] private float stopDistance = 0.05f;

    [Tooltip("猫の足が床に埋まる場合に、ワールドY方向へ持ち上げるオフセット（単位）。StartPointへの出現・各waypoint移動・DoorPoint到着・帰位のすべてに同じ値が1回だけ適用される（累積しない）。Playで猫の足元を見ながら調整する（初期値0=持ち上げなし）。")]
    [SerializeField] private float catHeightOffset = 0f;

    [Header("表示タイミング")]
    [Tooltip("ドアが開いてから鳴き声を再生するまでの秒数。")]
    [SerializeField] private float meowDelaySeconds = 0f;

    [Tooltip("ドアを閉じる前に猫を非表示にしておく秒数（ドアを閉じるX秒前に消す）。")]
    [SerializeField] private float catHideBeforeCloseSeconds = 0f;

    private Vector3 _homePosition;
    private Quaternion _homeRotation;
    private Coroutine _meowCoroutine;
    private bool _walkAborted;

    private void Start()
    {
        if (catObject != null)
        {
            _homePosition = catObject.transform.position;
            _homeRotation = catObject.transform.rotation;
            // 通常プレイ中は猫を出しておかない（3キー開始時にStartPointへ出現させる）。
            catObject.SetActive(false);
        }
    }

    /// <summary>ドアを閉じる前に猫を隠しておく秒数（ParentDetectionV2のドア閉鎖タイミング計算に使う）。</summary>
    public float HideBeforeCloseSeconds => Mathf.Max(0f, catHideBeforeCloseSeconds);

    /// <summary>
    /// 猫をStartPointに出現させ、母親のDoorRoutineと同じwaypoint順でDoorPointまで移動させる。
    /// shouldContinueがfalseを返すと中断する（後始末は呼び出し側で行う）。
    /// </summary>
    public IEnumerator MoveAlongDoorRoute(Func<bool> shouldContinue)
    {
        _walkAborted = false;

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

        // 母親のDoorRoutineと同じ順序：H1 → H2 → TurnPoint（旋回）→ H3 → DoorPoint（旋回）。
        // 未設定のwaypointは母親側と同じくスキップする。
        yield return MoveCatTo(routeController.hallwayPoint1, shouldContinue);
        if (_walkAborted) yield break;

        yield return MoveCatTo(routeController.hallwayPoint2, shouldContinue);
        if (_walkAborted) yield break;

        if (routeController.turnPoint != null)
        {
            yield return MoveCatTo(routeController.turnPoint, shouldContinue);
            if (_walkAborted) yield break;
            yield return RotateCatToYaw(routeController.turnPoint.rotation.eulerAngles.y, shouldContinue);
            if (_walkAborted) yield break;
        }

        yield return MoveCatTo(routeController.hallwayPoint3, shouldContinue);
        if (_walkAborted) yield break;

        if (routeController.doorPoint != null)
        {
            yield return MoveCatTo(routeController.doorPoint, shouldContinue);
            if (_walkAborted) yield break;
            yield return RotateCatToYaw(routeController.doorPoint.rotation.eulerAngles.y, shouldContinue);
            if (_walkAborted) yield break;
        }
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
        if (catObject != null && catObject.activeSelf)
            catObject.SetActive(false);
    }

    /// <summary>猫を次回用の開始状態へ戻す（ホーム位置・向きに戻して非表示。高さはcatHeightOffsetを1回だけ加算）。</summary>
    public void ReturnToStartPosition()
    {
        if (catObject == null) return;
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

        // 到達点は「waypoint位置＋catHeightOffset」を毎回waypointの生座標から再計算する。
        // 現在位置へオフセットを加算しないため、フレーム間・waypoint間で猫が浮き上がり続けたり
        // 加算が累積したりしない（Yは常にwaypoint＋offsetの一定高さを保つ）。
        Vector3 goal = CatGoalPosition(target.position);

        while (Vector3.Distance(catObject.transform.position, goal) > stopDistance)
        {
            if (shouldContinue != null && !shouldContinue())
            {
                _walkAborted = true;
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
        if (meowDelaySeconds > 0f)
            yield return new WaitForSeconds(meowDelaySeconds);

        if (meowAudioSource != null)
            meowAudioSource.Play();

        _meowCoroutine = null;
    }

    private static float NormalizeAngle(float angle)
    {
        angle %= 360f;
        if (angle > 180f) angle -= 360f;
        if (angle < -180f) angle += 360f;
        return angle;
    }
}
