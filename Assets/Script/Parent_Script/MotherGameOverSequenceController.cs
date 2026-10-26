using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 捕獲時に保存する「ゲームオーバー演出への引き継ぎ情報」。
///  ・捕獲種類（廊下Peek／庭Peek／片付け入室中／片付けPeek）
///  ・捕獲した瞬間の位置・向き
///  ・片付けの進行段階／ドア通過状況
/// 状態が消える前に CaughtReactionController が保存し、MotherGameOverSequenceController が読む。
/// </summary>
public struct MotherGameOverCaptureContext
{
    public Vector3 startPosition;        // 捕獲した瞬間の母親の位置
    public Quaternion startRotation;     // 捕獲した瞬間の向き
    public bool gardenPeek;              // 庭覗きで見つかったか（B）
    public bool choreCapture;            // 片付け中に見つかったか（C/D）
    public bool choreLooking;            // 片付けのPeek中だったか（D）
    public bool choreDoorPassed;         // 片付けの入室でドアを通過済みか
    public MotherChoreController.ChorePhase chorePhase;  // 捕獲直前の片付け段階
}

/// <summary>
/// MotherGameOverSequenceController：
/// 捕獲時のゲームオーバー演出（4パターン）の「順番」だけを管理する小さなコントローラ。
///
/// 責務（ここだけが行う）：
///   ・開始状況に応じて 廊下／庭／片付け入室中／片付けPeek のルートを選ぶ
///   ・ルートのステップを順に実行する（移動 → 到着・向き合わせ → 到着後の動作）
///   ・各ステップのタイムアウトを管理し、失敗しても結果画面へ進めるようにする
///
/// 責務ではない（他クラスに委譲する。ここに重複実装しない）：
///   ・Animator 操作        … MotherAnimationPlayer（用途別APIのみ呼ぶ）
///   ・位置・向きの移動処理 … MotherApproachController（MoveToWaypointForGameOver 等）
///   ・ドア本体の回転       … DoorController（既存APIのみ）
///   ・捕獲確定・UDP通知・シーン遷移 … CaughtReactionController
///
/// ルート設計：
///   ・ステップは Inspector の Serializable なリストで編集する（通常ルートの既存Transformリストは壊さない）。
///   ・「移動中の動作」＝早歩き等、「到着後の動作」＝なし／ドア開け／ジャンプスケア。
///   ・早歩きは移動区間の間ずっと再生し、ドア開け・ジャンプスケアは到着・向き合わせ後に再生する。
///   ・カメラ前へは専用の「ジャンプスケア位置Waypoint」を配置して最終ステップの到着動作にする。
/// </summary>
public class MotherGameOverSequenceController : MonoBehaviour
{
    /// <summary>移動区間の動作。</summary>
    /// <remarks>
    /// Teleport は既存値の意味と数値を変えないため必ず末尾へ追加する
    /// （None=0 / Walk=1 / WalkFast=2 / FaceOnly=3 はそのまま。既存 Scene のシリアライズ値を壊さない）。
    /// </remarks>
    public enum MoveStyle
    {
        None,      // 移動しない（位置・向きも変えない）
        Walk,      // 通常歩き（Walk）で移動し、到着後に Waypoint の＋Zへ向きを合わせる
        WalkFast,  // 早歩き（Walk_Fast）で移動し、到着後に Waypoint の＋Zへ向きを合わせる
        FaceOnly,  // 現在位置のまま向きだけ合わせる（位置を書き換えない。JumpscareChorePoint 用）
        Teleport   // 位置と向きを同時に合わせる（歩行移動・移動ステート到達待ち・旋回待ちを行わない。庭ゲームオーバーの大回り省略用）
    }

    /// <summary>到着・向き合わせ後の動作。</summary>
    public enum ArriveAction
    {
        None,      // 何もしない
        DoorOpen,  // 手が押すタイミングでドアを開ける（Door_Open＋ドア本体）
        JumpScare  // ジャンプスケアを1回再生し、完了を待つ（Jumpscare）
    }

    [Serializable]
    public class Step
    {
        [Tooltip("移動先のWaypoint。到着後にこのWaypointのローカル＋Zへ向きを合わせます。")]
        public Transform waypoint;

        [Tooltip("移動中の動作。WalkFast=早歩き(Walk_Fast)、Walk=通常歩き(Walk)、FaceOnly=現在位置のまま向きだけ、None=何もしない。")]
        public MoveStyle moveStyle = MoveStyle.WalkFast;

        [Tooltip("到着・向き合わせ後の動作。None=なし、DoorOpen=ドア開け(Door_Open)、JumpScare=ジャンプスケア。")]
        public ArriveAction arriveAction = ArriveAction.None;

        [Tooltip("移動速度(units/秒)。0以下なら MotherApproachController の現在速度を使います。アニメの再生速度とは別です。")]
        public float moveSpeed;
    }

    [Serializable]
    public class Route
    {
        [Tooltip("このルートの手順。上から順に実行します（通過済みWaypointへ戻らないよう、捕獲位置より先だけを登録してください）。")]
        public List<Step> steps = new List<Step>();

        [Tooltip("カメラ演出の開始タイミング。廊下／庭=ドア本体を開き始めるタイミング、" +
                 "片付け移動中／片付けPeek=捕獲演出の開始時（ドア開けが無いため待たない）。")]
        public CameraStartTiming cameraStartTiming = CameraStartTiming.OnDoorOpenPush;

        [Tooltip("このルートで使うカメラルート。未設定/空ならカメラ演出のみ省略します（母親の演出は継続）。" +
                 "複数ルートで同じ内容を指定して共有して構いません。")]
        public GameOverCameraSequenceController.CameraRoute cameraRoute = new GameOverCameraSequenceController.CameraRoute();
    }

    /// <summary>カメラ演出の開始タイミング。</summary>
    public enum CameraStartTiming
    {
        /// <summary>廊下／庭：ドア本体を開き始めるタイミングでカメラを開始する。</summary>
        OnDoorOpenPush,
        /// <summary>片付け移動中／片付けPeek：捕獲演出の開始時にカメラを開始する（ドア開けを待たない）。</summary>
        OnCaptureStart,
    }

    // ── 参照 ──────────────────────────────────────────────────────────────────
    [Header("参照")]
    [Tooltip("ゲームオーバー演出の移動・向きを担当する（内部の既存移動処理を再利用する）。未設定なら自動検索。")]
    [SerializeField] private MotherApproachController approachController;

    [Tooltip("Animator 操作の窓口。未設定なら自動検索。")]
    [SerializeField] private MotherAnimationPlayer animationPlayer;

    [Tooltip("ドア本体の開閉。未設定なら自動検索。")]
    [SerializeField] private DoorController doorController;

    // ── タイミング ────────────────────────────────────────────────────────────
    [Header("タイミング")]
    [Tooltip("1ステップの移動の上限秒数。超えたら警告して移動を打ち切る（無限待機しない）。")]
    [SerializeField, Min(0.5f)] private float stepMoveTimeoutSeconds = 20f;

    [Tooltip("ゲームオーバー接近の既定の移動速度(units/秒)。Step.moveSpeed が 0 のときに使う。" +
             "アニメの再生速度（Walk_Fast の倍率）とは別。")]
    [SerializeField, Min(0f)] private float defaultMoveSpeed = 3.5f;

    [Tooltip("停止状態（Idle）への到達を待つ上限秒数。超えたら警告して演出を打ち切る（結果画面へ進む）。")]
    [SerializeField, Min(0.1f)] private float stepStopTimeoutSeconds = 2f;

    [Tooltip("移動ステート（Walk_Fast / Walk）への到達を待つ上限秒数。超えたら演出を打ち切る。")]
    [SerializeField, Min(0.1f)] private float stepMoveStateTimeoutSeconds = 2f;

    [Tooltip("ドア本体が目標角度へ到達するのを待つ上限秒数。")]
    [SerializeField, Min(0.1f)] private float doorOpenWaitTimeoutSeconds = 5f;

    [Tooltip("【後方互換】時間方式を使わない場合のドア本体の開く速さ。\n" +
             "※これは秒ではなく Lerp 係数で、値が大きいほど速く全開になります（ゲームオーバー専用）。")]
    [SerializeField, Min(0.01f)] private float gameOverDoorOpenSpeed = 10f;

    [Tooltip("ゲームオーバーのドア本体が全開になるまでの時間（秒）。大きいほどゆっくり開きます。\n" +
             "0 のときだけ上の gameOverDoorOpenSpeed（速度方式）を使います。")]
    [SerializeField, Min(0f)] private float gameOverDoorOpenDuration = 2f;

    [Tooltip("ゲームオーバーのドア本体の開き方カーブ（0→1）。空なら線形（等速）で開きます。")]
    [SerializeField] private AnimationCurve gameOverDoorOpenCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    [Tooltip("演出全体の上限秒数。超えたら打ち切り、CaughtReactionController が結果画面へ進む。")]
    [SerializeField, Min(1f)] private float overallTimeoutSeconds = 30f;

    // ── カメラ演出（一人称カメラのカットシーン）────────────────────────────────
    [Header("カメラ演出")]
    [Tooltip("カメラ演出のController。未設定なら自動検索します。未設定/ルート空でも母親の演出は止めません。")]
    [SerializeField] private GameOverCameraSequenceController cameraSequenceController;

    [Header("音とUI（捕獲後の演出）")]
    [Tooltip("捕獲確定のMAX到達音・UI非表示・ドア開け完了後の心拍・ジャンプスケア音を受け持つController。\n" +
             "未設定ならシーンから自動検索します（見つからなければ音とUIの切り替えだけを行わず、演出は継続します）。")]
    [SerializeField] private GameOverPresentationController presentationController;

    [Tooltip("Jumpscareステートへの到達確認を待つ上限（秒）。到達を確認できた時点でジャンプスケア音を1回鳴らします。")]
    [SerializeField, Min(0.1f)] private float jumpScareStateTimeoutSeconds = 2f;

    [Tooltip("母親のJumpscare完了後、カメラがまだ移動中なら、上限付きで完了を待ってから結果画面へ進む。")]
    [SerializeField] private bool waitForCameraCompletionBeforeSceneChange = true;

    [Tooltip("カメラ完了待ちの上限秒数。超えたら結果画面へ進む（無限待機しない）。")]
    [SerializeField, Min(0f)] private float cameraCompletionWaitTimeoutSeconds = 5f;

    // ── ルート（ゲームオーバー専用・Inspectorで編集）──────────────────────────
    [Header("ルート：B DoorPeek（廊下）で捕獲")]
    [Tooltip("B. DoorPeekで捕獲：現在位置 → jumpscaredoorpoint → Door_Open → jumpscarepoint → Jumpscare。")]
    [SerializeField] private Route hallwayRoute = new Route();

    [Header("ルート：A 庭Peekで捕獲")]
    [Tooltip("A. 庭Peekで捕獲：現在位置 → 早歩きで jumpscarepoint1〜5 を順に進む → jumpscaredoorpoint → Door_Open → " +
             "jumpscarepoint → Jumpscare。壁・窓を突き抜けないよう庭側Waypointを順に登録してください。")]
    [SerializeField] private Route gardenRoute = new Route();

    [Header("ルート：C 片付けのchorepointへ移動中に捕獲")]
    [Tooltip("C. 片付け移動中に捕獲：現在位置から早歩きで jumpscarepoint へ直接向かう（chorepointへは戻らない）。")]
    [SerializeField] private Route choreEntryRoute = new Route { cameraStartTiming = CameraStartTiming.OnCaptureStart };

    [Header("ルート：D 片付けPeek中に捕獲")]
    [Tooltip("D. 片付けPeek中に捕獲：現在位置のまま JumpscareChorePoint の＋Zへ向く（FaceOnly）→ 早歩きで jumpscarepoint へ → Jumpscare。" +
             "立ち上がり（Chore_End）は挟みません。")]
    [SerializeField] private Route chorePeekRoute = new Route { cameraStartTiming = CameraStartTiming.OnCaptureStart };

    [Header("デバッグ")]
    [SerializeField] private bool showDebugLogs = true;

    // 現在のステップが失敗したか（Animator に入れない等）。次のループで打ち切って結果画面へ進む。
    private bool _stepFailed;

    // 選択したルート（カメラ開始タイミング／カメラルートの参照に使う）。
    private Route _selectedRoute;

    // カメラ開始通知を同じ捕獲で二重に出さないためのフラグ。
    private bool _cameraStartNotified;

    // 選択ルートがドア開けステップ（DoorOpen）を実行するか。
    //  ・false のルート（片付けC/D、ドア通過済み）は「捕獲後の最初の正常な停止到達」で心拍を開始する。
    private bool _routeExpectsDoorOpenStep;

    // 捕獲後の心拍開始を依頼済みか（二重開始防止。依頼先でも1回だけにしているが、ここでも抑止する）。
    private bool _heartbeatStartRequested;

    // 直前の移動が正常に到着したか（揺れなど「到着時の動作」を到着前・移動失敗時に始めないため）。
    private bool _lastMoveArrived = true;


    // ── 公開 ──────────────────────────────────────────────────────────────────
    /// <summary>4ルートのいずれかに有効なステップ（Waypoint設定済み）があるか。</summary>
    public bool HasAnyRouteConfigured =>
        CountValidSteps(hallwayRoute) > 0 || CountValidSteps(gardenRoute) > 0 ||
        CountValidSteps(choreEntryRoute) > 0 || CountValidSteps(chorePeekRoute) > 0;

    private static bool HasSteps(Route route) => CountValidSteps(route) > 0;

    /// <summary>有効なステップ数（Waypoint が設定されているステップのみ数える）。</summary>
    private static int CountValidSteps(Route route)
    {
        if (route == null || route.steps == null) return 0;
        int n = 0;
        foreach (Step step in route.steps)
        {
            if (step != null && step.waypoint != null) n++;
        }
        return n;
    }

    /// <summary>
    /// 4ルートの登録状況を1行で返す（診断用。Waypoint オブジェクトの有無ではなく、
    /// ルート（steps）に実際に登録されているかを示す）。
    /// </summary>
    public string DescribeRouteConfiguration()
    {
        return $"登録状況: B廊下={CountValidSteps(hallwayRoute)}件 / A庭={CountValidSteps(gardenRoute)}件 / " +
               $"C片付け移動中={CountValidSteps(choreEntryRoute)}件 / D片付けPeek={CountValidSteps(chorePeekRoute)}件";
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  本体
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 捕獲時の状況に応じてルートを選び、演出を順に実行する。
    ///  ・失敗・タイムアウトでも正常完了とは扱わず、onResult(false) を返して呼び出し側を結果画面へ進める。
    ///  ・選択ルートが未設定なら警告して即 false（結果画面への遷移のみ行われる）。
    /// </summary>
    public IEnumerator PlaySequence(MotherGameOverCaptureContext context, Action<bool> onResult = null)
    {
        ResolveReferences();

        // 同じ捕獲でのカメラ二重開始を防ぐフラグをリセットする。
        _cameraStartNotified = false;

        // 捕獲後の心拍開始も、この捕獲で1回だけにする。
        _heartbeatStartRequested = false;

        // 参照の解決結果とルート登録状況を1回だけ記録する（診断用）。
        Debug.Log($"[MotherGameOverSequence] 参照解決: approachController={(approachController != null ? approachController.name : "NULL")}, " +
                  $"animationPlayer={(animationPlayer != null ? animationPlayer.name : "NULL")}, " +
                  $"doorController={(doorController != null ? doorController.name : "NULL")}, " +
                  $"cameraSequenceController={(cameraSequenceController != null ? cameraSequenceController.name : "NULL")}, " +
                  $"対象Camera={(cameraSequenceController != null ? cameraSequenceController.DescribeResolvedCamera() : "n/a")} | " +
                  $"{DescribeRouteConfiguration()}", this);

        Route route = SelectRoute(context);

        // このルートがドア開けステップを実行するかを決める（実行しないルートは最初の停止到達で心拍を開始する）。
        _routeExpectsDoorOpenStep = RouteHasDoorOpenStep(route, context);

        int stepCount = CountValidSteps(route);
        var selectedCameraRoute = route != null ? route.cameraRoute : null;
        Debug.Log($"[MotherGameOverSequence] 選択ルート={RouteLabel(route)} / 有効ステップ数={stepCount} / " +
                  $"カメラ開始タイミング={(route != null ? route.cameraStartTiming.ToString() : "n/a")} / " +
                  $"{GameOverCameraSequenceController.DescribeRouteConfiguration(selectedCameraRoute)} | " +
                  $"{DescribeContext(context)} | {DescribeRouteConfiguration()}", this);

        if (stepCount == 0)
        {
            Debug.LogError("[MotherGameOverSequence] 【設定不足】選択したルートにステップ（Waypoint）が登録されていないため、" +
                           "演出を開始できません。" + DescribeRouteConfiguration() +
                           " — 結果画面への遷移のみ行います。" +
                           "（Waypointオブジェクトが存在するだけではルート登録になりません。" +
                           "MotherGameOverSequenceController の各 Route.steps へ Waypoint と動作を登録してください）", this);
            onResult?.Invoke(false);
            yield break;
        }

        Debug.Log($"[MotherGameOverSequence] PlaySequence 開始 | {DescribeContext(context)} | ステップ数={stepCount}", this);

        // 通常の移動・帰路・片付け・旋回を止め、演出へ制御を渡す（位置・モデル表示は維持）。
        //  ・CaughtReactionController 側でも performanceStartDelay より前に同じ準備を呼ぶため、
        //    ここは二重になるが冪等（何度呼んでも安全）。
        PrepareForCapture();

        // 捕獲時の動作順：GameOverActive=true（PrepareForCapture 内）→ 停止要求 → Idle 到達確認。
        yield return StopAndWait();
        if (_stepFailed)
        {
            Debug.LogError("[MotherGameOverSequence] 【再生失敗】捕獲直後に停止状態（Idle）へ入れませんでした" +
                           "（GameOverStopTrigger/Idle 到達の設定を確認）。結果画面へ進みます", this);
            animationPlayer?.EndGameOverPerformance();
            onResult?.Invoke(false);
            yield break;
        }
        Debug.Log("[MotherGameOverSequence] 捕獲直後の停止状態（Idle）到達 OK", this);

        // 片付けルート（ドア開けが無い）は、捕獲演出の開始時にカメラ演出を開始する。
        if (_selectedRoute != null && _selectedRoute.cameraStartTiming == CameraStartTiming.OnCaptureStart)
            NotifyCameraStartOnce("CaptureStart");

        // 演出全体の上限は timeScale に依存しない実時間で管理する。
        float overallStart = Time.realtimeSinceStartup;
        bool aborted = false;

        // ※ 立ち上がり（Chore_End）は今回の仕様では挟まない（D も方向転換のみ）。
        //   playChoreEndFirst / PlayStandUp は撤去した（勝手に立ち上がりを挿入しない）。
        if (route.steps != null)
        {
            for (int i = 0; i < route.steps.Count; i++)
            {
                if (Time.realtimeSinceStartup - overallStart > overallTimeoutSeconds)
                {
                    Debug.LogWarning($"[MotherGameOverSequence] 演出が全体上限 {overallTimeoutSeconds:F1}s を超えたため打ち切ります", this);
                    aborted = true;
                    break;
                }

                Step step = route.steps[i];
                if (step == null)
                {
                    Debug.LogWarning($"[MotherGameOverSequence] ステップ {i + 1}/{route.steps.Count} は null のためスキップします", this);
                    continue;
                }

                // ステップの内容を1回だけ記録する（毎フレームは出さない）。
                Debug.Log($"[MotherGameOverSequence] ステップ {i + 1}/{route.steps.Count}: " +
                          $"waypoint={(step.waypoint != null ? step.waypoint.name : "NULL")} / " +
                          $"move={step.moveStyle} / arrive={step.arriveAction} / " +
                          $"speed={(step.moveSpeed > 0f ? step.moveSpeed.ToString("F2") : "既定")}", this);

                if (step.waypoint == null)
                {
                    Debug.LogError($"[MotherGameOverSequence] 【設定不足】ステップ {i + 1} の Waypoint が未設定です。" +
                                   "演出を打ち切り、結果画面へ進みます", this);
                    aborted = true;
                    break;
                }

                // 母親がドア通過済みなら、そのステップ（移動＋到着動作）ごと省略する
                // （通過済みの外側ドア地点へ逆戻りしない）。
                //  ※「ドアが全開」は別条件。全開でも外側にいる場合は、入室のための移動は残す。
                //    その場合の到着動作（DoorOpen）だけを ExecuteDoorOpen 内でスキップする。
                if (step.arriveAction == ArriveAction.DoorOpen && context.choreDoorPassed)
                {
                    Debug.Log($"[MotherGameOverSequence] ステップ {i + 1}: ドア通過済みのため、ドア開けステップ（移動＋動作）を省略します", this);
                    continue;
                }

                yield return ExecuteStep(step);

                // ステップ失敗（Animator に入れない等）は打ち切り、結果画面へ進める。
                if (_stepFailed)
                {
                    Debug.LogError($"[MotherGameOverSequence] 【再生失敗】ステップ {i + 1}" +
                                   $"（waypoint={(step.waypoint != null ? step.waypoint.name : "NULL")}, " +
                                   $"move={step.moveStyle}, arrive={step.arriveAction}）で失敗しました。" +
                                   "演出を打ち切り、結果画面へ進みます", this);
                    aborted = true;
                    break;
                }
            }
        }

        // カメラ演出がまだ移動中なら、上限付きで完了を待つ（母親の演出は既に完了している）。
        //  ・カメラが先に完了している場合は何も待たない（最終視点を保持したまま）。
        //  ・上限を超えても結果画面へ進む（無限待機しない）。
        if (!aborted && waitForCameraCompletionBeforeSceneChange &&
            cameraSequenceController != null && cameraSequenceController.IsRunning)
        {
            bool cameraCompleted = false;
            yield return cameraSequenceController.WaitForCompletion(cameraCompletionWaitTimeoutSeconds, ok => cameraCompleted = ok);
            Debug.Log($"[MotherGameOverSequence] カメラ演出の完了待ち: 結果={(cameraCompleted ? "完了" : "未完了（上限で打ち切り）")}", this);
        }

        // 歩行速度の上書きを通常値へ戻す。
        animationPlayer?.EndGameOverPerformance();

        if (aborted)
        {
            Debug.LogError("[MotherGameOverSequence] 【打ち切り】演出を最後まで再生できませんでした（原因は上のログ参照）。結果画面へ進みます", this);

            // 中断：心拍を止めて通常制御へ戻す（音を残さない）。
            presentationController?.NotifySequenceEnded("演出打ち切り");

            // 中断：カメラの揺れも止める（正常完了では止めない＝到着揺れを自身の減衰で終わらせる）。
            cameraSequenceController?.StopShake("演出打ち切り");
        }
        else
            Debug.Log("[MotherGameOverSequence] 【正常完了】演出を最後まで再生しました。結果画面へ進みます", this);

        onResult?.Invoke(!aborted);
    }

    /// <summary>
    /// ルートがドア開け（DoorOpen）ステップを実行するか。
    ///  ・ドア通過済み（choreDoorPassed）のステップは PlaySequence が丸ごと省略するため「実行しない」扱い。
    /// </summary>
    private static bool RouteHasDoorOpenStep(Route route, MotherGameOverCaptureContext context)
    {
        if (route == null || route.steps == null) return false;
        if (context.choreDoorPassed) return false;

        foreach (Step step in route.steps)
        {
            if (step != null && step.waypoint != null && step.arriveAction == ArriveAction.DoorOpen)
                return true;
        }

        return false;
    }

    /// <summary>
    /// 捕獲後の心拍開始を1回だけ依頼する。
    ///  ・ドア開け完了（Door_Openの正常完了＋ドア本体の全開）
    ///  ・ドア開けが無いルートは、捕獲後の最初の正常な停止状態到達
    ///  のどちらかから呼ぶ。ドアを押す瞬間やカメラ開始通知では呼ばない。
    /// </summary>
    private void RequestGameOverHeartbeatStartOnce(string reason)
    {
        if (_heartbeatStartRequested) return;
        _heartbeatStartRequested = true;

        if (presentationController == null)
        {
            Debug.LogWarning("[MotherGameOverSequence] 【設定不足】GameOverPresentationController が見つからないため、" +
                             $"捕獲後の心拍を開始できません（理由={reason}。演出は継続します）", this);
            return;
        }

        Debug.Log($"[MotherGameOverSequence] 捕獲後の心拍開始を依頼します（理由={reason}）", this);
        presentationController.NotifyDoorOpenCompleted();
    }

    /// <summary>選択したルートの表示名（診断ログ用）。</summary>
    private string RouteLabel(Route route)
    {
        if (route == hallwayRoute) return "B廊下(DoorPeek)";
        if (route == gardenRoute) return "A庭(Peek)";
        if (route == choreEntryRoute) return "C片付け移動中";
        if (route == chorePeekRoute) return "D片付けPeek";
        return "不明";
    }

    /// <summary>
    /// カメラ演出の開始通知（同じ捕獲で1回だけ）。
    ///  ・カメラController未設定／カメラルート未設定・空の場合は、警告してカメラ演出のみ省略する
    ///    （母親の演出は止めない）。
    /// </summary>
    private void NotifyCameraStartOnce(string reason)
    {
        if (_cameraStartNotified)
        {
            if (showDebugLogs)
                Debug.Log($"[MotherGameOverSequence] カメラ開始通知は既に成功済みのため無視します（{reason}）", this);
            return;
        }

        Debug.Log($"[MotherGameOverSequence] カメラ開始通知に到達しました（理由={reason}）", this);

        ResolveReferences();

        // 選択ルート・開始タイミング・カメラステップ数・Controller解決結果を1回だけ記録する（診断用）。
        var camRouteForLog = _selectedRoute != null ? _selectedRoute.cameraRoute : null;
        var timingForLog = _selectedRoute != null ? _selectedRoute.cameraStartTiming : CameraStartTiming.OnDoorOpenPush;
        Debug.Log($"[MotherGameOverSequence] カメラ設定: 選択ルート={RouteLabel(_selectedRoute)} / 開始タイミング={timingForLog} / " +
                  $"{GameOverCameraSequenceController.DescribeRouteConfiguration(camRouteForLog)} / " +
                  $"cameraController={(cameraSequenceController != null ? cameraSequenceController.name : "NULL")}", this);

        if (cameraSequenceController == null)
        {
            Debug.LogWarning("[MotherGameOverSequence] 【設定不足】GameOverCameraSequenceController がシーンに存在しないため、" +
                             "カメラ演出のみ省略します（母親の演出は継続）。" +
                             "GameOverController などのGameObjectへコンポーネントを追加してください", this);
            return;
        }

        var camRoute = _selectedRoute != null ? _selectedRoute.cameraRoute : null;
        if (!cameraSequenceController.HasUsableRoute(camRoute))
        {
            Debug.LogWarning($"[MotherGameOverSequence] 【設定不足】{GameOverCameraSequenceController.DescribeRouteConfiguration(camRoute)} — " +
                             $"カメラ演出のみ省略します（開始理由={reason}。母親の演出は継続）。" +
                             (cameraSequenceController.HasGizmoRouteSteps
                                 ? "※ gizmoRoute は Scene 表示専用です。演出には各 Route.cameraRoute.steps へ登録してください。"
                                 : "各 Route.cameraRoute.steps へ Waypoint を登録してください。"), this);
            return;
        }

        // カメラ移動を遅くした場合、カメラ完了待ちの上限に収まるかを警告する。
        float camTotal = cameraSequenceController.ComputeTotalDurationSeconds(camRoute);
        if (camTotal > cameraCompletionWaitTimeoutSeconds)
            Debug.LogWarning($"[MotherGameOverSequence] カメラ演出の合計所要 {camTotal:F2}s が" +
                             $"カメラ完了待ち上限 {cameraCompletionWaitTimeoutSeconds:F2}s を超えています。" +
                             "結果画面への遷移時にカメラが途中で打ち切られる可能性があります" +
                             "（cameraDurationMultiplier / moveDuration / holdDuration、または上限を調整してください）", this);

        bool started = cameraSequenceController.StartRoute(camRoute, reason);

        // 実際に開始できたときだけ「通知済み」にする（設定不足で以後の通知を握りつぶさない）。
        if (started)
        {
            _cameraStartNotified = true;
            Debug.Log($"[MotherGameOverSequence] カメラ演出の開始に成功しました（理由={reason}）", this);
        }
        else
        {
            Debug.LogWarning($"[MotherGameOverSequence] カメラ演出の開始に失敗しました（理由={reason}）。" +
                             "母親の演出は継続します（詳細は GameOverCameraSequence のログ参照）", this);
        }
    }

    private IEnumerator ExecuteStep(Step step)
    {
        _stepFailed = false;
        _lastMoveArrived = true;   // 移動しないステップ（FaceOnly/None）は「到着済み」として扱う


        Transform target = step.waypoint;
        if (target == null)
        {
            Debug.LogWarning("[MotherGameOverSequence] Waypoint未設定のステップをスキップします", this);
            yield break;
        }

        // 1) 移動の前に必ず「停止状態（Idle）」を経由する（距離0でも省略しない）。
        yield return StopAndWait();
        if (_stepFailed) yield break;

        // ドア開けステップが無いルート（片付けC/Dなど）は、捕獲後の最初の正常な停止到達で心拍を開始する。
        //  ・ドア開けイベントを待ち続けない（待つ相手が存在しないため）。
        if (!_routeExpectsDoorOpenStep)
            RequestGameOverHeartbeatStartOnce("最初の停止到達（ドア開け無しルート）");

        // 2) 移動（FaceOnly は移動しない。向き合わせだけ行う）。
        //    Teleport は「位置と向きを同時に合わせる」専用区間のため、
        //    歩行アニメ要求・移動ステート到達待ち・歩行移動Coroutine・移動後の停止待ちを行わない。
        //    位置と向きは approachController.TeleportForGameOver が同時に適用する。
        bool rotationHandledByTeleport = false;

        if (step.moveStyle == MoveStyle.Teleport)
        {
            bool teleported = approachController != null &&
                              approachController.TeleportForGameOver(step.waypoint);

            // 成功時は「到着済み」として後続の到着動作を許可する。
            // 失敗時は古い移動結果を持ち越さず、既存の失敗終了規則へ合流する。
            _lastMoveArrived = teleported;

            if (!teleported)
            {
                Debug.LogError($"[MotherGameOverSequence] 【再生失敗】テレポートできませんでした" +
                               $"（waypoint={(step.waypoint != null ? step.waypoint.name : "NULL")}）。" +
                               "演出を打ち切り、結果画面へ進みます", this);
                _stepFailed = true;
                yield break;
            }

            // 向きは TeleportForGameOver が同時に合わせ済み。
            // 下の向き合わせを実行すると、同じ向きへ徐々に旋回する待機が重なるためスキップする。
            rotationHandledByTeleport = true;
        }

        if (step.moveStyle != MoveStyle.None && step.moveStyle != MoveStyle.FaceOnly &&
            step.moveStyle != MoveStyle.Teleport)
        {
            // Idle から早歩き／通常歩きを要求する。
            if (!PlayMoveAnimation(step.moveStyle))
            {
                // Animator に入れない（パラメーター/ステート不足）。直接 Play へはフォールバックしない。
                Debug.LogWarning($"[MotherGameOverSequence] 移動アニメーション（{step.moveStyle}）に入れませんでした。" +
                                 "設定不足のため演出を打ち切り、結果画面へ進みます", this);
                _stepFailed = true;
                yield break;
            }

            // 対象ステート（Walk_Fast / Walk）への到達を確認する。
            yield return WaitForMoveStateArrival(step.moveStyle);
            if (_stepFailed) yield break;

            // 移動距離が0でも MoveToWaypointForGameOver を呼ぶ（到着扱い。省略しない）。
            if (approachController != null)
            {
                // Step.moveSpeed=0 のときは Inspector の既定接近速度（defaultMoveSpeed）を使う。
                float speed = step.moveSpeed > 0f ? step.moveSpeed : defaultMoveSpeed;

                Vector3 startPos = approachController.transform.position;
                Vector3 goalPos = step.waypoint.position;   // 参考値（内部で motherHeightOffset を加算）
                float distance = Vector3.Distance(startPos, goalPos);
                float moveStart = Time.realtimeSinceStartup;

                Debug.Log($"[MotherGameOverSequence] 移動開始: waypoint='{step.waypoint.name}' / " +
                          $"開始={startPos.ToString("F2")} / 目標={goalPos.ToString("F2")} / 距離={distance:F2} / " +
                          $"Step.moveSpeed={step.moveSpeed:F2} / defaultMoveSpeed={defaultMoveSpeed:F2} / 実効速度={speed:F2}", this);

                yield return approachController.MoveToWaypointForGameOver(step.waypoint, speed, stepMoveTimeoutSeconds);

                float moveSeconds = Time.realtimeSinceStartup - moveStart;
                float movedDistance = Vector3.Distance(startPos, approachController.transform.position);
                bool arrived = Vector3.Distance(approachController.transform.position, goalPos) <= 0.2f;
                _lastMoveArrived = arrived;   // 到着時の動作（カメラ揺れなど）を移動失敗時に始めないため

                Debug.Log($"[MotherGameOverSequence] 移動終了: waypoint='{step.waypoint.name}' / 所要={moveSeconds:F2}s / " +
                          $"実移動距離={movedDistance:F2} / 結果={(arrived ? "正常到達" : "未到達（タイムアウトの可能性）")}", this);
            }

            // 移動後は停止状態（Idle）へ戻す。
            yield return StopAndWait();
            if (_stepFailed) yield break;
        }

        // 3) 向き合わせ（FaceOnly もここで位置を変えずに向きだけ合わせる）。
        //    Teleport は位置と同時に向きを合わせ済みのため、同じ向きへ徐々に旋回する待機を重ねない。
        if (approachController != null && !rotationHandledByTeleport)
            yield return approachController.FaceWaypointForwardForGameOver(target);

        // 4) 到着後の動作（なし／ドア開け／ジャンプスケア）。停止状態で行う。
        switch (step.arriveAction)
        {
            case ArriveAction.DoorOpen:
                yield return ExecuteDoorOpen();
                break;
            case ArriveAction.JumpScare:
                // 最終JumpScarePointへ正常到着した時点で、カメラを短時間揺らす（同じ捕獲で1回だけ）。
                //  ・オブジェクト名の文字列比較ではなく、ステップの arriveAction=JumpScare で判定する。
                //  ・到着していない（移動タイムアウト等）場合は開始しない。
                //  ・カメラの位置・回転を書くのは GameOverCameraSequenceController だけ（競合しない）。
                if (_lastMoveArrived)
                {
                    if (cameraSequenceController != null)
                        cameraSequenceController.TriggerArrivalShake($"JumpScarePoint到着（{(target != null ? target.name : "NULL")}）");
                    else
                        Debug.LogWarning("[MotherGameOverSequence] 【設定不足】GameOverCameraSequenceController が無いため、カメラ揺れを行いません（演出は継続します）", this);
                }
                else
                {
                    Debug.LogWarning($"[MotherGameOverSequence] 移動が正常に完了していないため、カメラ揺れを開始しません" +
                                     $"（waypoint='{(target != null ? target.name : "NULL")}'）", this);
                }

                yield return ExecuteJumpScare();
                break;
        }
    }

    /// <summary>移動中の動作を要求する。Animator に入れない場合は false（直接 Play へフォールバックしない）。</summary>
    private bool PlayMoveAnimation(MoveStyle style)
    {
        if (animationPlayer == null)
        {
            Debug.LogWarning("[MotherGameOverSequence] Animator参照が未設定のため、移動アニメーションを要求できません", this);
            return false;
        }

        if (style == MoveStyle.WalkFast)
            return animationPlayer.RequestWalkFast();

        // 通常歩き（Walk）は既存の Walk パラメーターを使う（不足時は MotherAnimationPlayer が警告を出す）。
        animationPlayer.RequestWalk();
        return true;
    }

    /// <summary>
    /// 移動ステート（Walk_Fast / Walk）への到達（遷移完了）をタイムアウト付きで待つ。
    /// 到達できなければ _stepFailed を立てて打ち切る。
    /// </summary>
    private IEnumerator WaitForMoveStateArrival(MoveStyle style)
    {
        if (animationPlayer == null)
        {
            _stepFailed = true;
            yield break;
        }

        bool reached = false;
        if (style == MoveStyle.WalkFast)
            yield return animationPlayer.WaitForWalkFastState(stepMoveStateTimeoutSeconds, ok => reached = ok);
        else
            yield return animationPlayer.WaitForWalkState(stepMoveStateTimeoutSeconds, ok => reached = ok);

        if (!reached)
        {
            Debug.LogError($"[MotherGameOverSequence] 【再生失敗】移動ステート（{style}）へ {stepMoveStateTimeoutSeconds:F1}s で到達できませんでした。" +
                           "演出を打ち切り、結果画面へ進みます", this);
            _stepFailed = true;
        }
        else
        {
            Debug.Log($"[MotherGameOverSequence] 移動ステート（{style}）到達 OK", this);
        }
    }

    /// <summary>
    /// 停止要求を出し、停止状態（Idle）への到達をタイムアウト付きで待つ。
    ///  ・早歩きを経由していない（Chore_Peek/Peek_Door などからの捕獲）場合でも停止状態へ入る。
    ///  ・到達できなければ _stepFailed を立てて打ち切る（到達前にドア開け／FaceOnly の次段階へ進まない）。
    /// </summary>
    private IEnumerator StopAndWait()
    {
        if (animationPlayer == null)
        {
            Debug.LogWarning("[MotherGameOverSequence] Animator参照が未設定のため停止できません。演出を打ち切ります", this);
            _stepFailed = true;
            yield break;
        }

        if (!animationPlayer.RequestStop())
        {
            Debug.LogWarning("[MotherGameOverSequence] 停止要求を出せませんでした（設定不足）。演出を打ち切り、結果画面へ進みます", this);
            _stepFailed = true;
            yield break;
        }

        bool reached = false;
        yield return animationPlayer.WaitForStopState(stepStopTimeoutSeconds, ok => reached = ok);

        if (!reached)
        {
            Debug.LogError($"[MotherGameOverSequence] 【再生失敗】停止状態（Idle）へ {stepStopTimeoutSeconds:F1}s で到達できませんでした。" +
                           "演出を打ち切り、結果画面へ進みます", this);
            _stepFailed = true;
        }
        else
        {
            Debug.Log("[MotherGameOverSequence] Idle到達 OK", this);
        }
    }


    /// <summary>
    /// ドア開け：モデルの Door_Open とドア本体を連動させる。
    ///  ・手が押すタイミングより前にドアを開けない（WaitForDoorOpenPush 成立後に開く）。
    ///  ・モデルの再生完了とドア本体の到達を確認する（タイムアウトでも無限待機しない）。
    ///  ・Door_Open に入れない（パラメーター/ステート不足）場合は打ち切り、結果画面へ進める。
    /// </summary>
    private IEnumerator ExecuteDoorOpen()
    {
        if (animationPlayer == null)
        {
            Debug.LogWarning("[MotherGameOverSequence] Animator参照が未設定のため、ドア開けを実行できません。演出を打ち切ります", this);
            _stepFailed = true;
            yield break;
        }

        // ※ 到着動作の前に停止状態（Idle）であることは ExecuteStep 側で保証済み（二重の停止待ちはしない）。

        if (doorController == null)
            Debug.LogWarning("[MotherGameOverSequence] DoorController 参照が未設定のため、ドア本体は開けません（モデルのみ再生）", this);

        if (doorController != null && doorController.CurrentDoorState == DoorController.DoorState.Full)
        {
            if (showDebugLogs)
                Debug.Log("[MotherGameOverSequence] ドアは既に全開のため、開け直しません");

            // すでに全開で DoorOpen が省略される場合も、そのドアステップの到着動作を処理する時点でカメラを開始する。
            NotifyCameraStartOnce("DoorAlreadyOpen");

            // 全開済みでドア開けを省略した場合も、このドア到着動作を正常に処理した時点で心拍を開始する。
            RequestGameOverHeartbeatStartOnce("ドア全開済み（ドア到着動作を正常処理）");
            yield break;
        }

        float t0 = Time.realtimeSinceStartup;   // 演出内の実時間（timeScale 非依存）

        // モデルの Door_Open を要求（Walk を落とし、Door_OpenSpeed を適用する）。
        if (!animationPlayer.RequestDoorOpen())
        {
            Debug.LogError("[MotherGameOverSequence] 【再生失敗】Door_Open に入れませんでした（Door_OpenTrigger/ステートの設定不足）。" +
                           "演出を打ち切り、結果画面へ進みます", this);
            _stepFailed = true;
            yield break;
        }
        Debug.Log($"[MotherGameOverSequence] Door_Open 開始（Trigger発火） t=+{Time.realtimeSinceStartup - t0:F2}s", this);

        // 手が押すタイミングまで待つ。
        bool pushOk = false;
        yield return animationPlayer.WaitForDoorOpenPush(ok => pushOk = ok);
        Debug.Log($"[MotherGameOverSequence] 押すタイミング成立: pushOk={pushOk} t=+{Time.realtimeSinceStartup - t0:F2}s{DescribeAnimatorState()}", this);

        // 押すタイミング成立（またはタイムアウト後）にドア本体を開き始める。
        //  ・gameOverDoorOpenDuration>0 なら「秒」指定（大きいほどゆっくり）。ドア角度の書き込み元は DoorController のみ。
        //  ・0 のときだけ既存の速度方式（gameOverDoorOpenSpeed）を使う（後方互換）。
        if (doorController != null)
        {
            if (gameOverDoorOpenDuration > 0f)
            {
                if (gameOverDoorOpenDuration > doorOpenWaitTimeoutSeconds)
                    Debug.LogWarning($"[MotherGameOverSequence] ドア本体の開く時間 {gameOverDoorOpenDuration:F2}s が" +
                                     $"完了待ち上限 {doorOpenWaitTimeoutSeconds:F2}s を超えています（全開確認がタイムアウトします）", this);

                doorController.BeginTimedMove(DoorController.DoorState.Full, gameOverDoorOpenDuration, gameOverDoorOpenCurve);
            }
            else
            {
                doorController.BeginChoreFullOpen(gameOverDoorOpenSpeed);
            }
        }

        // ドア本体の開き要求を出すタイミング＝カメラ開始（成功時だけ・同じ捕獲で1回だけ）。
        if (pushOk)
            NotifyCameraStartOnce("DoorOpenPush");
        else
            Debug.LogWarning("[MotherGameOverSequence] 押すタイミングが成立しなかったため、カメラ開始通知は出しません" +
                             "（カメラ演出のみ省略。母親の演出は継続）", this);

        // モデルの再生完了を待つ（Door_Open の残り尺はここで消費される。クリップ長も記録する）。
        bool modelOk = false;
        yield return animationPlayer.WaitForDoorOpen(ok => modelOk = ok);
        Debug.Log($"[MotherGameOverSequence] Door_Open アニメ完了: modelOk={modelOk} t=+{Time.realtimeSinceStartup - t0:F2}s{DescribeAnimatorState()}", this);

        // ドア本体が通過可能な角度へ到達するのを待つ。
        if (doorController != null)
        {
            bool doorOk = true;
            yield return doorController.WaitForDoorReached(doorOpenWaitTimeoutSeconds, ok => doorOk = ok);
            Debug.Log($"[MotherGameOverSequence] ドア本体 全開完了: doorOk={doorOk} t=+{Time.realtimeSinceStartup - t0:F2}s", this);

            // 速度上書きを残さない。
            doorController.ClearChoreSpeedOverride();
        }

        // ドア開け完了：モデルの Door_Open 正常完了とドア本体の全開の「両方」を確認できた時点で心拍を開始する。
        RequestGameOverHeartbeatStartOnce("Door_Open完了＋ドア本体全開");

        Debug.Log($"[MotherGameOverSequence] Door_Open ステップ完了（所要 {Time.realtimeSinceStartup - t0:F2}s）", this);
    }

    /// <summary>診断用：母親 Animator の現在ステートのクリップ長・正規化時間（1行。毎フレームは出さない）。</summary>
    private string DescribeAnimatorState()
    {
        if (animationPlayer == null) return "";
        var a = animationPlayer.Animator;
        if (a == null) return "";

        var info = a.GetCurrentAnimatorStateInfo(0);
        return $" / Door_Open中={animationPlayer.IsPlayingState(animationPlayer.DoorOpenStateName)}" +
               $" / clipLen={info.length:F2}s / norm={info.normalizedTime:F2}";
    }

    /// <summary>ジャンプスケアを1回再生し、再生完了を待つ。入れない場合は打ち切って結果画面へ進める。</summary>
    private IEnumerator ExecuteJumpScare()
    {
        if (animationPlayer == null)
        {
            Debug.LogWarning("[MotherGameOverSequence] Animator参照が未設定のため、ジャンプスケアを再生できません。演出を打ち切ります", this);
            _stepFailed = true;
            yield break;
        }

        // ※ 到着動作の前に停止状態（Idle）であることは ExecuteStep 側で保証済み（二重の停止待ちはしない）。

        float t0 = Time.realtimeSinceStartup;

        if (!animationPlayer.RequestJumpScare())
        {
            Debug.LogError("[MotherGameOverSequence] 【再生失敗】ジャンプスケア ステートに入れませんでした" +
                           "（JumpscareTrigger/ステートの設定不足）。演出を打ち切り、結果画面へ進みます", this);
            _stepFailed = true;
            yield break;
        }
        Debug.Log($"[MotherGameOverSequence] Jumpscare 開始（Trigger発火） t=+{Time.realtimeSinceStartup - t0:F2}s", this);

        // Trigger発火だけでなく、実際に Jumpscare ステートへ到達したことを確認してから効果音を1回鳴らす。
        bool reached = false;
        yield return animationPlayer.WaitForStateArrival(animationPlayer.JumpScareStateName, jumpScareStateTimeoutSeconds, ok => reached = ok);

        if (reached)
        {
            Debug.Log($"[MotherGameOverSequence] Jumpscare ステート到達を確認（音を再生） t=+{Time.realtimeSinceStartup - t0:F2}s", this);

            if (presentationController != null)
                presentationController.NotifyJumpScareStarted();
            else
                Debug.LogWarning("[MotherGameOverSequence] 【設定不足】GameOverPresentationController が無いため、" +
                                 "ジャンプスケア音を再生できません（演出は継続します）", this);
        }
        else
        {
            Debug.LogWarning($"[MotherGameOverSequence] Jumpscare ステートへの到達を {jumpScareStateTimeoutSeconds:F1}s で確認できませんでした。" +
                             "効果音は再生しません（演出は継続します）", this);
        }

        bool finished = false;
        yield return animationPlayer.WaitForJumpScare(ok => finished = ok);

        if (!finished)
            Debug.LogWarning("[MotherGameOverSequence] ジャンプスケアの再生完了を確認できませんでした（続行）", this);
        else
            Debug.Log($"[MotherGameOverSequence] Jumpscare 完了（再生完了を確認。所要 {Time.realtimeSinceStartup - t0:F2}s）", this);
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  ヘルパー
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>捕獲状況からルートを選ぶ（片付け中 → 庭覗き → 廊下覗き の優先順）。</summary>
    private Route SelectRoute(MotherGameOverCaptureContext context)
    {
        Route route;

        if (context.choreCapture)
        {
            // 片付けの帰路中に捕獲された場合（通常ルールでは発生しない想定）も、
            // 入室ルートは使うが「ドア通過済み」として扱われるため、外側のドア地点へは戻らない
            // （PlaySequence が DoorOpen ステップを丸ごと省略する）。
            if (context.chorePhase == MotherChoreController.ChorePhase.Returning && showDebugLogs)
                Debug.Log("[MotherGameOverSequence] 片付け帰路中の捕獲として扱います（外側のドア地点へは戻りません）");

            route = context.choreLooking ? chorePeekRoute : choreEntryRoute;
        }
        else if (context.gardenPeek)
        {
            route = gardenRoute;
        }
        else
        {
            route = hallwayRoute;
        }

        // カメラ開始タイミング／カメラルートの参照に使う。
        _selectedRoute = route;
        return route;
    }

    /// <summary>
    /// 捕獲直後に、通常移動と通常の歩行復帰を止める（演出開始前の待機＝performanceStartDelay より前に呼ぶ）。
    ///  ・位置リセット・モデル非表示は行わない（呼び先の EnterGameOverExclusiveControl が保証する）。
    ///  ・Animator・ドア回転は止めない（演出用の移動・再生は継続できる）。
    ///  ・冪等：複数回呼んでも安全。
    /// </summary>
    public void PrepareForCapture()
    {
        ResolveReferences();

        if (approachController != null)
            approachController.EnterGameOverExclusiveControl();

        if (animationPlayer != null)
            animationPlayer.BeginGameOverPerformance();

        // カメラ制御の引き継ぎ（捕獲確定時の現在の視点を記録＝カットシーンの開始点）。
        //  ・カメラ演出Controller未設定なら何もしない（母親の演出は継続）。
        if (cameraSequenceController != null)
            cameraSequenceController.CaptureControl("Captured");
    }

    private void ResolveReferences()
    {
        // System.Object との名前衝突を避けるため UnityEngine.Object を明示する。
        if (approachController == null) approachController = UnityEngine.Object.FindFirstObjectByType<MotherApproachController>();
        if (animationPlayer == null) animationPlayer = UnityEngine.Object.FindFirstObjectByType<MotherAnimationPlayer>();
        if (doorController == null) doorController = UnityEngine.Object.FindFirstObjectByType<DoorController>();
        if (cameraSequenceController == null) cameraSequenceController = UnityEngine.Object.FindFirstObjectByType<GameOverCameraSequenceController>();
        if (presentationController == null) presentationController = UnityEngine.Object.FindFirstObjectByType<GameOverPresentationController>();
    }

    private static string DescribeContext(MotherGameOverCaptureContext context)
    {
        string kind = context.choreCapture
            ? (context.choreLooking ? "片付けPeek(D)" : "片付け入室中(C)")
            : (context.gardenPeek ? "庭覗き(B)" : "廊下覗き(A)");

        return $"捕獲種類={kind} 片付け段階={context.chorePhase} ドア通過済み={context.choreDoorPassed}";
    }
}

