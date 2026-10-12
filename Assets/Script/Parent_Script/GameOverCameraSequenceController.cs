using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// GameOverCameraSequenceController：
/// ゲームオーバー演出中に「実際に描画している一人称Camera」を、登録した Waypoint 順に動かす
/// カメラ演出専用の小さなController。
///
/// 担当：
///   ・カメラ制御の引き継ぎ（捕獲時の現在位置・回転を記録して開始点にする）
///   ・カメラWaypoint移動（位置と回転を同じ進行率で補間し、到着時に誤差なく合わせる）
///   ・実行中／正常完了／失敗の状態
///   ・中断と必要な状態復元
///
/// 担当しない（他クラスに委譲する）：
///   ・母親の移動・Animator操作 … MotherGameOverSequenceController / MotherAnimationPlayer
///   ・ドア開け                    … MotherGameOverSequenceController / DoorController
///   ・結果画面への遷移            … CaughtReactionController
///
/// 前提（このプロジェクトの親機シーン）：
///   ・親機にカメラを動かす通常処理は無く、Main Camera は固定（root）です。
///     そのため「通常カメラ操作の停止」は実質不要ですが、他スクリプトに上書きされても
///     負けないよう LateUpdate で適用ポーズを書き込み続けます。
///   ・Camera 自体の enabled は変更しません（描画を止めない）。
/// </summary>
public class GameOverCameraSequenceController : MonoBehaviour
{
    /// <summary>カメラ用の1ステップ。</summary>
    [Serializable]
    public class CameraStep
    {
        [Tooltip("到着先Waypoint。ワールド位置とワールド回転を使います（Waypointのローカル＋Z が見る方向）。")]
        public Transform waypoint;

        [Tooltip("このステップの移動時間（秒）。【大きいほど遅くスライドします】\n" +
                 "0 は『そのステップだけ即時到達』として明示的に扱います（通常は1.5〜2秒推奨）。\n" +
                 "全体をまとめて遅くしたい場合は GameOverCameraSequenceController の cameraDurationMultiplier を使います。")]
        [Min(0f)] public float moveDuration = 1.5f;

        [Tooltip("到着後の保持時間（秒）。【移動時間とは別の設定】\n" +
                 "0 なら保持せず、次の区間へ連続して進みます（中間Waypointで毎回止まりません）。")]
        [Min(0f)] public float holdDuration = 0f;

        [Tooltip("補間カーブ（0→1）。位置と回転に同じ進行率を適用します。\n" +
                 "キーが0件の空カーブは線形（等速）として扱います（空カーブは常に0を返し、終端で瞬間スナップするため）。")]
        public AnimationCurve ease = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
    }

    /// <summary>カメラ用のルート（ステップの並び）。複数ルートで同内容を指定して共有しても構いません。</summary>
    [Serializable]
    public class CameraRoute
    {
        [Tooltip("カメラの手順。上から順に進みます。")]
        public List<CameraStep> steps = new List<CameraStep>();
    }

    // ── 参照 ──────────────────────────────────────────────────────────────────
    [Header("参照")]
    [Tooltip("動かす Camera（通常はシーンの Main Camera）。未設定なら Camera.main から解決します。")]
    [SerializeField] private Camera targetCamera;

    [Header("カメラ速度")]
    [Tooltip("全区間の移動時間にかける倍率。1=登録した moveDuration どおり / 2=移動に2倍の時間がかかる（＝遅くなる）。\n" +
             "※保持時間（holdDuration）には適用しません。移動時間だけに掛かります。")]
    [SerializeField, Min(0.01f)] private float cameraDurationMultiplier = 1f;

    [Header("着地の揺れ（JumpScarePoint到着時）")]
    [Tooltip("揺れの長さ（秒）。0にすると揺れません。【終了時は自動でオフセット0へ戻ります】")]
    [SerializeField, Min(0f)] private float shakeDuration = 0.35f;

    [Tooltip("揺れの位置の強さ（メートル）。控えめ推奨：0.03〜0.10。カメラのローカル基準で揺れます。")]
    [SerializeField, Min(0f)] private float shakePositionAmplitude = 0.06f;

    [Tooltip("揺れの回転の強さ（度）。控えめ推奨：0.5〜2.0。")]
    [SerializeField, Min(0f)] private float shakeRotationAmplitudeDegrees = 1.2f;

    [Tooltip("揺れの速さ（1秒あたりの振動数）。大きいほど細かく速く揺れます。推奨：10〜30。")]
    [SerializeField, Min(0.1f)] private float shakeFrequency = 18f;

    [Tooltip("時間経過による減衰カーブ（0→1）。X=経過割合、Y=残る強さ（1=最大、0=なし）。\n" +
             "キーが0件なら 2乗の減衰（1-k)^2 を使います。")]
    [SerializeField] private AnimationCurve shakeFalloff = AnimationCurve.Linear(0f, 1f, 1f, 0f);

    [Header("デバッグ")]
    [Tooltip("段階ごとの診断ログを出す（毎フレームは出しません）。")]
    [SerializeField] private bool showDebugLogs = true;

    [Header("Gizmos")]
    [Tooltip("カメラルート（Waypoint間の線と向き）を Scene ビューに表示する。")]
    [SerializeField] private bool drawRouteGizmos = true;

    [Tooltip("Gizmo に使うカメラルート（単体で確認したい場合に設定）。")]
    [SerializeField] private CameraRoute gizmoRoute;

    // ── 内部状態 ──────────────────────────────────────────────────────────────
    private bool _controlCaptured;   // カメラ制御を引き継いだか
    private bool _running;           // カメラ移動中か
    private bool _completed;         // 正常完了したか
    private bool _holding;           // 最終ポーズ保持中か
    private Coroutine _routine;

    // 引き継ぎ時に記録（中断復元用）
    private Vector3 _originalPosition;
    private Quaternion _originalRotation;
    private Transform _originalParent;
    private bool _originalCameraEnabled;

    // LateUpdate で適用する現在ポーズ（他スクリプトの上書きに負けないため）
    private bool _applyPose;
    private Vector3 _posePosition;
    private Quaternion _poseRotation;

    // ── 着地の揺れ（JumpScarePoint到着時）─────────────────────────────────────
    //  ・_posePosition/_poseRotation（基本ポーズ）には触れず、LateUpdate で
    //    「基本ポーズ＋オフセット」を書き込む（毎フレーム加算して位置がずれる方式にしない）。
    private bool _shakeActive;
    private float _shakeElapsed;
    private bool _shakeUsedThisCapture;   // 同じ捕獲では1回だけ

    // ── 公開状態 ──────────────────────────────────────────────────────────────
    /// <summary>カメラ移動中か。</summary>
    public bool IsRunning => _running;

    /// <summary>正常完了したか。</summary>
    public bool CompletedSuccessfully => _completed;

    /// <summary>最終ポーズを保持中か。</summary>
    public bool IsHoldingFinalPose => _holding;

    /// <summary>カメラ演出が開始可能か（ルートに有効な Waypoint があるか）。</summary>
    public bool HasUsableRoute(CameraRoute route)
    {
        if (route == null || route.steps == null) return false;
        foreach (var s in route.steps)
        {
            if (s != null && s.waypoint != null) return true;
        }
        return false;
    }

    /// <summary>診断用：解決済み Camera 名（未解決なら "NULL"）。</summary>
    public string DescribeResolvedCamera()
    {
        var cam = ResolveCamera();
        return cam != null ? cam.name : "NULL";
    }

    /// <summary>診断用：gizmoRoute（Scene表示専用）にだけ Waypoint が登録されていないかの判定材料。</summary>
    public bool HasGizmoRouteSteps => HasUsableRoute(gizmoRoute);

    /// <summary>
    /// このルートの合計所要時間（各ステップの「移動時間×cameraDurationMultiplier」＋「保持時間」の合計）。
    ///  ・カメラ完了待ちの上限（MotherGameOverSequence の cameraCompletionWaitTimeoutSeconds）に
    ///    収まるかの警告に使う。
    /// </summary>
    public float ComputeTotalDurationSeconds(CameraRoute route)
    {
        if (route == null || route.steps == null) return 0f;

        float mult = Mathf.Max(0.01f, cameraDurationMultiplier);
        float total = 0f;
        foreach (var s in route.steps)
        {
            if (s == null) continue;
            total += Mathf.Max(0f, s.moveDuration) * mult + Mathf.Max(0f, s.holdDuration);
        }
        return total;
    }

    /// <summary>ルートの登録状況（診断用）。</summary>
    public static string DescribeRouteConfiguration(CameraRoute route)
    {
        if (route == null) return "カメラルート未設定(NULL)";

        int valid = 0;
        int total = route.steps != null ? route.steps.Count : 0;
        if (route.steps != null)
        {
            foreach (var s in route.steps)
            {
                if (s != null && s.waypoint != null) valid++;
            }
        }
        return $"カメラルート: 有効ステップ={valid}/{total}";
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  制御の引き継ぎ
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// カメラ制御を引き継ぐ（捕獲確定時に呼ぶ）。現在のワールド位置・回転を開始点として記録する。
    ///  ・Camera の enabled は変更しない（描画を止めない）。
    ///  ・既に引き継ぎ済みなら何もしない（二重引き継ぎ防止）。
    /// </summary>
    public bool CaptureControl(string reason)
    {
        Camera cam = ResolveCamera();
        if (cam == null)
        {
            Debug.LogWarning("[GameOverCameraSequence] Camera を解決できないため、カメラ演出を開始できません（カメラ演出のみ省略）", this);
            return false;
        }

        if (_controlCaptured)
        {
            if (showDebugLogs)
                Debug.Log($"[GameOverCameraSequence] カメラ制御は既に引き継ぎ済みです（{reason}）", this);
            return false;
        }

        Transform t = cam.transform;
        _originalPosition = t.position;
        _originalRotation = t.rotation;
        _originalParent = t.parent;
        _originalCameraEnabled = cam.enabled;
        _controlCaptured = true;

        Debug.Log($"[GameOverCameraSequence] カメラ制御を引き継ぎました（{reason}）: camera='{cam.name}' / " +
                  $"開始位置={_originalPosition.ToString("F2")} / 開始回転={_originalRotation.eulerAngles.ToString("F1")} / " +
                  $"parent={(_originalParent != null ? _originalParent.name : "root")} / cameraEnabled={_originalCameraEnabled}", this);
        return true;
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  ルート開始（母親とは並行。呼び出し側は待たない）
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// カメラルートの再生を開始する（内部コルーチン。呼び出し側は待たない＝母親演出を止めない）。
    ///  ・ルート未設定/空/Waypoint不足なら警告して開始しない（false）。
    ///  ・既に実行中/保持中なら二重開始しない（false）。
    ///  ・Camera 未解決なら開始しない（false）。
    /// </summary>
    public bool StartRoute(CameraRoute route, string reason)
    {
        if (_running || _holding)
        {
            if (showDebugLogs)
                Debug.Log($"[GameOverCameraSequence] カメラ演出は既に実行中/保持中のため開始しません（{reason}）", this);
            return false;
        }

        Camera cam = ResolveCamera();
        if (cam == null)
        {
            Debug.LogWarning("[GameOverCameraSequence] Camera が未設定のためカメラ演出を開始しません（カメラ演出のみ省略）", this);
            return false;
        }

        // コンポーネントが無効/非アクティブだと StartCoroutine できないため、理由を明示して開始しない。
        if (!isActiveAndEnabled)
        {
            Debug.LogWarning("[GameOverCameraSequence] このコンポーネントが無効/非アクティブのため、カメラ演出を開始できません" +
                             "（コルーチンを開始できません）。ゲームオブジェクトとコンポーネントを有効にしてください", this);
            return false;
        }

        if (!HasUsableRoute(route))
        {
            Debug.LogWarning($"[GameOverCameraSequence] 【設定不足】{DescribeRouteConfiguration(route)} — " +
                             "カメラ演出を開始しません（カメラ演出のみ省略。母親の演出は継続します）", this);
            return false;
        }

        // 引き継ぎがまだなら、この時点で引き継ぐ（開始位置＝現在の視点）。
        CaptureControl($"StartRoute:{reason}");

        _running = true;
        _completed = false;
        _holding = false;

        // 新しい捕獲のカメラ演出を始めるので、揺れの状態を戻す（前回の揺れを残さない）。
        StopShake("カメラ演出開始");
        _shakeUsedThisCapture = false;

        _routine = StartCoroutine(RunRoute(route));

        Debug.Log($"[GameOverCameraSequence] カメラ演出を開始しました（{reason}）: {DescribeRouteConfiguration(route)}", this);
        return true;
    }

    /// <summary>
    /// カメラルートを順に実行する。先頭ステップは「引き継いだ現在の位置・回転」から補間するため、
    /// 先頭Waypointへ瞬間移動しません（moveDuration=0 のときだけ即時到達）。
    /// </summary>
    private IEnumerator RunRoute(CameraRoute route)
    {
        Camera cam = ResolveCamera();
        if (cam == null)
        {
            Fail("Camera を解決できませんでした");
            yield break;
        }

        Transform camT = cam.transform;

        for (int i = 0; i < route.steps.Count; i++)
        {
            CameraStep step = route.steps[i];
            if (step == null)
            {
                Debug.LogWarning($"[GameOverCameraSequence] カメラステップ {i + 1}/{route.steps.Count} は null のためスキップします", this);
                continue;
            }

            if (step.waypoint == null)
            {
                Debug.LogWarning($"[GameOverCameraSequence] 【設定不足】カメラステップ {i + 1}/{route.steps.Count} の Waypoint が未設定のためスキップします", this);
                continue;
            }

            Vector3 fromPos = camT.position;
            Quaternion fromRot = camT.rotation;
            Vector3 toPos = step.waypoint.position;
            Quaternion toRot = step.waypoint.rotation;   // 上下も含めた回転（Y角だけにしない）
            float duration = Mathf.Max(0f, step.moveDuration) * Mathf.Max(0.01f, cameraDurationMultiplier);

            Debug.Log($"[GameOverCameraSequence] カメラ移動開始 {i + 1}/{route.steps.Count}: waypoint='{step.waypoint.name}' / " +
                      $"開始位置={fromPos.ToString("F2")} / 目標位置={toPos.ToString("F2")} / " +
                      $"開始回転={fromRot.eulerAngles.ToString("F1")} / 目標回転={toRot.eulerAngles.ToString("F1")} / " +
                      $"移動時間={duration:F2}s（moveDuration={step.moveDuration:F2}s × 倍率={cameraDurationMultiplier:F2}） / " +
                      $"保持={step.holdDuration:F2}s", this);

            if (duration <= 0f)
            {
                // 移動時間0は「そのステップだけ即時到達」として明示的に扱う。
                SetPose(toPos, toRot);
            }
            else
            {
                float elapsed = 0f;
                bool loggedMid = false;
                while (elapsed < duration)
                {
                    elapsed += Time.deltaTime;
                    float k = Mathf.Clamp01(elapsed / duration);
                    // 【重要】キーが0件の空カーブは Evaluate が常に0を返し、移動中ホールド→終端で瞬間スナップになる。
                    //   そのため「キーが無いカーブは線形」として扱う。
                    float e = (step.ease != null && step.ease.length > 0) ? Mathf.Clamp01(step.ease.Evaluate(k)) : k;

                    // 位置と回転に同じ進行率を適用する（回転は Quaternion で補間）。
                    SetPose(Vector3.Lerp(fromPos, toPos, e), Quaternion.Slerp(fromRot, toRot, e));

                    // 途中ポーズは1回だけ記録する（毎フレームは出さない）。
                    if (!loggedMid && k >= 0.5f)
                    {
                        loggedMid = true;
                        Debug.Log($"[GameOverCameraSequence] カメラ途中ポーズ {i + 1}/{route.steps.Count}: k={k:F2} / " +
                                  $"位置={_posePosition.ToString("F2")} / 回転={_poseRotation.eulerAngles.ToString("F1")}", this);
                    }

                    yield return null;
                }

                // 到着時に誤差を残さない（位置・回転を Waypoint に正確に合わせる）。
                SetPose(toPos, toRot);
            }

            // LateUpdate 適用後に誤差を確認する（診断）。
            yield return null;
            float posErr = Vector3.Distance(camT.position, toPos);
            float rotErr = Quaternion.Angle(camT.rotation, toRot);
            Debug.Log($"[GameOverCameraSequence] カメラ到着 {i + 1}/{route.steps.Count}: waypoint='{step.waypoint.name}' / " +
                      $"実際の位置={camT.position.ToString("F2")} / 実際の回転={camT.rotation.eulerAngles.ToString("F1")} / " +
                      $"位置誤差={posErr:F4} / 回転誤差={rotErr:F3}度", this);

            if (step.holdDuration > 0f)
                yield return new WaitForSeconds(step.holdDuration);
        }

        // 最後のWaypointの位置・向きを保持する。
        //  ・基本ポーズは直前のステップで SetPose 済みの値（Waypoint姿勢）をそのまま使う。
        //    camT から取り直すと、適用済みの揺れオフセットが混ざる。
        //  ・ここでは揺れを止めない：正常完了は「ラストWaypoint姿勢の保持」であり、
        //    母親の到着で始まった揺れは自身の duration で減衰させる（途中終了させない）。
        //    揺れを止めるのは中断復元（RestoreControl）と新しい演出の開始（StartRoute）だけ。
        if (!_applyPose)
        {
            _posePosition = camT.position;
            _poseRotation = camT.rotation;
        }
        _applyPose = true;

        _running = false;
        _completed = true;
        _holding = true;
        _routine = null;

        Debug.Log("[GameOverCameraSequence] 【正常完了】カメラ演出が完了しました。最終視点を保持します", this);
    }

    private void Fail(string reason)
    {
        _running = false;
        _completed = false;
        _routine = null;
        Debug.LogWarning($"[GameOverCameraSequence] 【失敗】カメラ演出を打ち切りました: {reason}", this);
    }

    /// <summary>LateUpdate で適用する現在ポーズを更新する（他スクリプトの上書きに負けないため）。</summary>
    private void SetPose(Vector3 position, Quaternion rotation)
    {
        _posePosition = position;
        _poseRotation = rotation;
        _applyPose = true;
    }

    /// <summary>
    /// 【JumpScarePoint到着】カメラを短時間揺らす（同じ捕獲では1回だけ）。
    ///  ・呼び出し元は MotherGameOverSequenceController（arriveAction=JumpScare のステップへ
    ///    正常到着した時点）。位置・回転を書くのはこのクラスだけなので競合しない。
    ///  ・カメラ移動中でも基本ポーズに重ねて揺れる（移動は止めない）。
    /// </summary>
    public void TriggerArrivalShake(string reason)
    {
        if (_shakeUsedThisCapture)
        {
            if (showDebugLogs)
                Debug.Log($"[GameOverCameraSequence] カメラ揺れは既に実行済みのため、再実行しません（理由={reason}）", this);
            return;
        }

        if (shakeDuration <= 0f)
        {
            Debug.LogWarning("[GameOverCameraSequence] カメラ揺れの時間（shakeDuration）が0のため、揺らしません", this);
            return;
        }

        _shakeUsedThisCapture = true;
        _shakeActive = true;
        _shakeElapsed = 0f;

        // カメラルートが未設定／先に完了していて、まだ基本ポーズを適用していない場合でも
        // 揺れを適用できるよう、現在の視点を基本ポーズとして記録する（遷移は遅らせない）。
        if (!_applyPose)
        {
            Camera cam = ResolveCamera();
            if (cam != null)
            {
                _posePosition = cam.transform.position;
                _poseRotation = cam.transform.rotation;
                _applyPose = true;
                Debug.Log("[GameOverCameraSequence] カメラルート未適用のため、現在の視点を基準ポーズとして揺れを適用します", this);
            }
            else
            {
                Debug.LogWarning("[GameOverCameraSequence] Camera を解決できないため、カメラ揺れを適用できません", this);
            }
        }

        Debug.Log($"[GameOverCameraSequence] カメラ揺れを開始（理由={reason}）: 時間={shakeDuration:F2}s / " +
                  $"位置={shakePositionAmplitude:F3}m / 回転={shakeRotationAmplitudeDegrees:F2}度 / 速さ={shakeFrequency:F1}", this);
    }

    /// <summary>揺れを止める（オフセットは即0へ戻る）。</summary>
    public void StopShake(string reason)
    {
        if (!_shakeActive) return;

        _shakeActive = false;
        _shakeElapsed = shakeDuration;

        if (showDebugLogs)
            Debug.Log($"[GameOverCameraSequence] カメラ揺れを停止しました（理由={reason}）", this);
    }

    /// <summary>
    /// 揺れのオフセットを「基本ポーズ」に加える。
    ///  ・基本ポーズ（_posePosition/_poseRotation）自体は変更しない（毎フレーム加算してずれる方式にしない）。
    ///  ・カメラのローカル基準で位置をずらし、ローカル回転を重ねる。
    ///  ・減衰カーブが0になった時点でオフセットは正確に0になる。
    /// </summary>
    private void ApplyShake(ref Vector3 position, ref Quaternion rotation)
    {
        if (!_shakeActive) return;

        _shakeElapsed += Time.deltaTime;
        float k = Mathf.Clamp01(shakeDuration > 0f ? _shakeElapsed / shakeDuration : 1f);

        if (k >= 1f)
        {
            _shakeActive = false;   // 以降はオフセット0（基本ポーズのみ）
            return;
        }

        float falloff = (shakeFalloff != null && shakeFalloff.length > 0)
            ? Mathf.Clamp01(shakeFalloff.Evaluate(k))
            : Mathf.Pow(1f - k, 2f);

        float t = Time.time * Mathf.Max(0.1f, shakeFrequency);

        Vector3 localOffset = new Vector3(
            Mathf.Sin(t),
            Mathf.Sin(t * 1.37f + 1.1f),
            0f) * (shakePositionAmplitude * falloff);

        Vector3 eulerOffset = new Vector3(
            Mathf.Sin(t * 0.91f + 0.4f),
            Mathf.Sin(t * 1.19f + 2.3f),
            Mathf.Sin(t * 1.53f)) * (shakeRotationAmplitudeDegrees * falloff);

        position += rotation * localOffset;      // カメラのローカル基準でずらす
        rotation = rotation * Quaternion.Euler(eulerOffset);
    }

    /// <summary>
    /// LateUpdate：実行中／最終ポーズ保持中は、適用ポーズを書き込み続ける。
    /// 親機では通常カメラ操作が無いが、他スクリプトが Update で書き戻しても LateUpdate で上書きする。
    /// </summary>
    private void LateUpdate()
    {
        if (!_applyPose) return;

        Camera cam = ResolveCamera();
        if (cam == null) return;

        // 基本ポーズ（Waypoint補間の結果）に揺れのオフセットを重ねて書き込む。
        //  ・基本ポーズは毎フレーム同じ値から始めるため、揺れが位置ずれとして積み上がらない。
        Vector3 position = _posePosition;
        Quaternion rotation = _poseRotation;
        ApplyShake(ref position, ref rotation);

        cam.transform.SetPositionAndRotation(position, rotation);
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  完了待ち（上限付き。結果画面へ行く前にだけ使う）
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// カメラ演出の完了を上限付きで待つ。
    ///  ・未開始/完了済みなら即座に true(completed) を返す。
    ///  ・上限を超えたら false（打ち切り）を返す（無限待機しない）。
    /// </summary>
    public IEnumerator WaitForCompletion(float timeoutSeconds, Action<bool> onResult)
    {
        if (!_running)
        {
            onResult?.Invoke(_completed);
            yield break;
        }

        float timeout = Mathf.Max(0f, timeoutSeconds);
        float elapsed = 0f;

        while (_running && elapsed < timeout)
        {
            elapsed += Time.deltaTime;
            yield return null;
        }

        if (_running)
        {
            Debug.LogWarning($"[GameOverCameraSequence] カメラ演出の完了を {timeout:F1}s で確認できませんでした（打ち切り）", this);
            onResult?.Invoke(false);
            yield break;
        }

        if (showDebugLogs)
            Debug.Log($"[GameOverCameraSequence] カメラ演出の完了を確認しました（待機 {elapsed:F2}s）", this);
        onResult?.Invoke(_completed);
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  中断・復元
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// カメラ演出を中断して、引き継ぎ前の位置・回転・親へ戻す（明示的な中断・再プレイ用）。
    ///  ・シーン遷移前の OnDisable では呼ばない（一瞬元の位置へ戻るのを避けるため。
    ///     正常終了時は最終視点を結果画面まで保持する）。
    /// </summary>
    public void AbortAndRestore(string reason)
    {
        if (_routine != null)
        {
            StopCoroutine(_routine);
            _routine = null;
        }

        Camera cam = ResolveCamera();
        if (cam != null && _controlCaptured)
        {
            Transform t = cam.transform;
            if (t.parent != _originalParent) t.SetParent(_originalParent, true);
            t.SetPositionAndRotation(_originalPosition, _originalRotation);
            cam.enabled = _originalCameraEnabled;
        }

        _running = false;
        _completed = false;
        _holding = false;
        _applyPose = false;
        _controlCaptured = false;

        // 中断（強制）では揺れを止めて状態も戻す（正常ルート完了とは区別する）。
        StopShake("中断復元");
        _shakeUsedThisCapture = false;

        Debug.LogWarning($"[GameOverCameraSequence] カメラ演出を中断し、元の視点へ復元しました（{reason}）", this);
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  検証（設定ミスの警告）
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 設定ミスを検出して issues に追記する（Editorツール・実行時の検証用）。
    ///  ・Camera 未解決
    ///  ・ルート未設定/空/Waypoint不足
    ///  ・Waypoint が動かす Camera の子にある（Camera と一緒に動いてしまう設定ミス）
    /// </summary>
    public bool Validate(CameraRoute route, List<string> issues)
    {
        if (issues == null) issues = new List<string>();
        bool ok = true;

        Camera cam = ResolveCamera();
        if (cam == null)
        {
            issues.Add("Camera が未設定です（Camera.main も見つかりません）。");
            ok = false;
        }

        if (!HasUsableRoute(route))
        {
            issues.Add($"{DescribeRouteConfiguration(route)} — 有効な Waypoint がありません。");
            return false;
        }

        if (cam != null && route != null && route.steps != null)
        {
            for (int i = 0; i < route.steps.Count; i++)
            {
                var s = route.steps[i];
                if (s == null || s.waypoint == null) continue;

                if (s.waypoint == cam.transform || s.waypoint.IsChildOf(cam.transform))
                {
                    issues.Add($"ステップ {i + 1} の Waypoint '{s.waypoint.name}' が Camera の子にあります" +
                               "（Camera と一緒に動くため設定ミスです。Camera の外へ置いてください）。");
                    ok = false;
                }
            }
        }

        return ok;
    }

    private Camera ResolveCamera()
    {
        if (targetCamera != null) return targetCamera;
        targetCamera = Camera.main;
        return targetCamera;
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Gizmos（カメラルートと向きの確認用）
    // ──────────────────────────────────────────────────────────────────────────

    private void OnDrawGizmos()
    {
        if (!drawRouteGizmos || gizmoRoute == null || gizmoRoute.steps == null) return;

        Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.9f);
        Vector3 prev = transform.position;

        for (int i = 0; i < gizmoRoute.steps.Count; i++)
        {
            var s = gizmoRoute.steps[i];
            if (s == null || s.waypoint == null) continue;

            Vector3 p = s.waypoint.position;
            Gizmos.DrawLine(prev, p);
            Gizmos.DrawWireSphere(p, 0.35f);
            // カメラのローカル＋Z が見る方向。
            Gizmos.DrawLine(p, p + s.waypoint.forward * 1.5f);
            prev = p;
        }
    }
}
