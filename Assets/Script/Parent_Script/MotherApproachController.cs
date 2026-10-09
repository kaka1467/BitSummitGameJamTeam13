using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Serialization;

/// <summary>
/// MotherApproachController：
/// インスペクターで設定したウェイポイントに沿って、明示的なルートで親機を移動させる。
///   廊下ルート（TurnPointの前後で可変）：
///     startPoint → hallwayPointsBeforeTurn[]（登録順）→ turnPoint（旋回）
///     → hallwayPointsAfterTurn[]（登録順）
///   DoorPeek：上記の廊下ルート → doorPoint（停止・扉前で向きを合わせる）
///             →（PDが入室を要求した場合のみ）roomEntryPoints[]へ入室 → doorPointへ復帰（退室）
///   フェイントA（HallwayPassBy）：廊下ルート → doorPoint（停止せず通過・ドア操作なし）
///             → hallwayPassByPoint（画面外で停止）→ onPassedByDoor（PD.OnApproachPassedBy経由でサイクル終了）
///   庭側素通り（GardenPassBy）：TurnPointでドア側経路から分岐し、gardenRoutePoints[]の中間ウェイポイントを
///             順番に通ってGardenPeekPoint（停止せず通過）→ GardenPassByPointへ進む。
///   庭側覗き（GardenPeek）：GardenPassByと同じ経路でGardenPeekPointまで進み、そこで停止して
///             gardenPeekPoint.rotationのY角（＝覗き位置Waypointの向き）へ回転し、
///             Peek_Windowアニメーションを再生する。覗き時間経過後はGardenPassByPointへ進む。
///
/// 部屋入室（任意）：
///   PDがOnStoppedAtDoorの処理中にRequestRoomEntry()を呼んだときのみ、ドア停止後にroomEntryPoints[]へ進む。
///   入室完了でonEnteredRoom、退室完了（doorPoint復帰）でonExitedRoomを発生する。
///
/// 向きの仕様（X/Z固定、Yのみ）【新仕様：Waypoint到着時だけ旋回する】：
///   1. 移動中は母親の向きを変更しない（位置のみ更新）。
///   2. Waypointに到着したときだけ旋回する。
///   3. 向く方向は「到着したWaypointのローカル＋Z」のみ。次の移動先への位置ベクトルからは計算しない。
///   4. 旋回が完了してから、次の移動や演出へ進む。
///   5. HallwayとGardenで同じルールを使う（経路ごとの分岐なし）。
///   ※ 以前の「Gardenだけ移動中に進行方向を向く」仕様は廃止した。
///   ※ 共通処理は MoveAndFaceWaypoint()（= MovePositionOnly → RotateToWaypointForward）。
///   ※ forward の Y成分を落として地面上の向きとして扱う（母親を上下に傾けない）。
///   ※ 水平成分がほぼゼロの不正な向き（真上・真下）は警告し、現在の向きを維持する。
///   ※ 旋回ループの現在角は transform.rotation.eulerAngles.y を毎フレーム読み直さず、
///     水平forward（Atan2(x,z)）から求めて保持する（下記 RotateToYaw のコメント参照）。
///     Quaternion.Euler が yaw ±180度付近で等価な別表現（X/Zが180反転）に再分解されうるため、
///     eulerAngles.y を読み直すと誤差の符号が反転し、2角度間の往復が起こり得る。
///   ※ waypoint.right や固定90/180度補正は使わない。
///
/// _fixedPitch / _fixedRoll（傾きの固定）：
///   BeginApproach() で startPoint.rotation.eulerAngles の X / Z を取得して固定する。
///   現在のシーンでは startPoint の rotation が identity（0,0,0）のため、両方とも 0。
///   つまり水平旋回の仕様と矛盾する傾きは入っていない。
///   （startPointに傾きを付けると母親全体がその分傾くため、注意すること）
///
///   適用先：hallwayPointsBeforeTurn / turnPoint / hallwayPointsAfterTurn /
///           doorPoint / gardenRoutePoints / gardenPeekPoint / gardenPassByPoint /
///           roomEntryPoints / hallwayPassByPoint
///
///   例外：退室開始時の向きだけは固定角度 roomExitYaw のまま（対応する向き指定Waypointが
///         存在しないため。詳細は RoomPhaseCoroutine のコメント参照）。
///
/// 突入モード（IsRushIn=true）：
///   大きな音による突入でStartApproachDoorOnly()を呼ぶ前にMotherApproachWarningが設定する。
///   ResetStateFlags()で自動的に解除される。
/// </summary>
public class MotherApproachController : MonoBehaviour
{
    // ── ウェイポイント ────────────────────────────────────────────────────────
    [Header("ウェイポイント")] [Tooltip("親機が出現し、リセット時に戻る場所。")]
    public Transform startPoint;

    // ── 廊下ルート（TurnPointの前後で可変） ─────────────────────────────────
    // 経路は Inspector から自由に追加・削除・並べ替えできる List で指定する。
    //
    // 旧個別フィールド（hallwayPoint1〜3）からの移行は2種類ある。混同しないこと：
    //   ・【編集時の移行】Editor メニュー「Tools/親機ルート: 選択中を移行」
    //       → 編集時のオブジェクトを実際に書き換え、Undo・Dirty・PrefabOverride を伴う。
    //         ユーザーがシーンを保存して初めて永続化される（保存は自動では行わない）。
    //   ・【実行時の互換移行】MigrateLegacyHallwayPoints()
    //       → 未移行の古いシーンを動かすための、そのPlay限りの読み替え。
    //         メモリ上だけで完結し、編集時のシーンへは書き戻さない（Play停止で元に戻る）。
    // 編集時に移行済み（hallwayRouteMigrated == true）なら、実行時は List が唯一の設定元になる。
    [Header("廊下ルート（TurnPointより前）")]
    [Tooltip("TurnPointより前に、登録順に通過するウェイポイント。\n" +
             "ここが経路の唯一の設定元。空にするとTurnPointより前の経路を持たない（旧値は復活しない）。")]
    public List<Transform> hallwayPointsBeforeTurn = new List<Transform>();

    [Header("廊下ルート（TurnPoint）")]
    [Tooltip("方向転換地点。到着後、このTransformのローカル＋Z（青い矢印）が示す方向へ滑らかに回転する。\n" +
             "未設定の場合は警告を出して廊下ルートを安全に中断する。")]
    public Transform turnPoint;

    [Header("廊下ルート（TurnPointより後）")]
    [Tooltip("TurnPointより後に、登録順に通過するウェイポイント（ドア確認ルートのみ使用）。\n" +
             "ここが経路の唯一の設定元。空にするとTurnPointより後の経路を持たない（旧値は復活しない）。")]
    public List<Transform> hallwayPointsAfterTurn = new List<Transform>();

    /// <summary>
    /// 旧個別フィールド（hallwayPoint1〜3）から List への移行が【編集時に確定して保存された】か。
    /// true のとき、List が経路の唯一の設定元になる（空リストも「空」として尊重する）。
    ///
    /// この値は通常のInspectorでは表示しない（[SerializeField, HideInInspector]）。
    /// 編集時に移行するには Editor メニュー「Tools/親機ルート: 選択中を移行」を使う（Assets/Script/Editor/ParentApproachRouteMigrator.cs）。
    ///
    /// 【重要】実行時に MigrateLegacyHallwayPoints() がこの値を true にしても、
    /// それはメモリ上だけで、編集時のシーン/Prefabへは書き戻されない（Playを止めると元に戻る）。
    /// 永続化するには Editor メニューから移行し、ユーザー自身がシーン/Prefabを保存する必要がある。
    /// </summary>
    [SerializeField, HideInInspector] private bool hallwayRouteMigrated;

    /// <summary>
    /// 未移行のシーンを実行したときだけ行う「実行時の互換移行」を有効にするか。
    /// OFFのときは実行時に旧フィールドを一切見ず、List だけを使う。
    /// （編集時に移行済みなら、この値に関係なく List がそのまま使われる）
    /// </summary>
    [Tooltip("未移行のシーンを実行したときだけ、旧hallwayPoint1〜3をListへ読み替える実行時の互換処理。\n" +
             "編集時に移行済み（Editorメニューで移行）なら、この設定に関係なくListがそのまま使われます。")]
    [SerializeField]
    private bool allowRuntimeCompatMigration = true;

    // ── 旧フィールド（互換用・非表示） ──────────────────────────────────────
    // 既存シーン／Prefabの参照を消さないため、フィールド名は変更せず残す。
    // 実際の経路は上記の List に自動移行される（Inspector では折りたたみ表示）。
    [HideInInspector] [Tooltip("（互換用）TurnPointより前の1つ目のウェイポイント。通常は hallwayPointsBeforeTurn を使用する。")]
    public Transform hallwayPoint1;

    [HideInInspector] [Tooltip("（互換用）TurnPointより前の2つ目のウェイポイント。通常は hallwayPointsBeforeTurn を使用する。")]
    public Transform hallwayPoint2;

    [HideInInspector] [Tooltip("（互換用）TurnPointより後のウェイポイント。通常は hallwayPointsAfterTurn を使用する。")]
    public Transform hallwayPoint3;

    [Tooltip("ドア前の位置。到着時にこのTransformのローカル＋Z（青い矢印）が示す方向へ回転する。")]
    public Transform doorPoint;

    [Tooltip("フェイントA（HallwayPassBy）用の画面外到達点。doorPointを停止せず通過した後に進む。未設定の場合はHallwayPassByを開始しない。")]
    public Transform hallwayPassByPoint;

    [Tooltip("庭側素通り用の到達点。doorPointを停止せず通過した後、庭側の実配置に沿って進む。")]
    public Transform gardenPassByPoint;

    [Tooltip("庭側ルートの最初の到達点。Window Peek（GardenPeek）では、ここで停止して\n" +
             "このTransformのローカル＋Z（青い矢印）が示す方向へ向いてから Peek_Windows を再生する。")]
    public Transform gardenPeekPoint;

    [Tooltip("庭側ルートの中間ウェイポイント（TurnPointからGardenPeekPointへ向かう順番に設定）。未設定の場合はGardenPassByを開始しない。")]
    public Transform[] gardenRoutePoints;

    // ── 帰路（Door Peek / Window Peek 終了後） ───────────────────────────────
    // 通常のPeek終了後、その場で帰る向きへ旋回し、帰路Listを順に通って
    // 最終点へ到達してからモデルを非表示にする。
    // 最終点は経路ごとに異なる（Routineの引数で指定する）：
    //   ・Door Peek側  = hallwayPassThroughPoint（廊下の画面外）
    //   ・Window Peek側 = gardenPassByPoint（庭側の到達点）
    // ※ 既存の hallwayPassByPoint（フェイントA用）とは用途が違うため別フィールドにしている。
    [Header("帰路（Door Peek / Window Peek 終了後）")]
    [Tooltip("Door Peek 終了後、帰る向きへその場旋回するための地点（回転専用）。\n" +
             "doorPoint と同じ位置に置き、向き（ローカル＋Z／青い矢印）だけ帰る方向へ設定する。\n" +
             "この地点では位置移動を行わず、＋Zへ旋回してから帰路の移動を始める。")]
    public Transform hallwayTurnBackPoint;

    [Tooltip("Door Peek 終了後の帰路ウェイポイント（Turn Back地点から hallwayPassThroughPoint へ向かう順）。\n" +
             "登録順に通過する。空でも可（空なら Turn Back 旋回後、hallwayPassThroughPoint へ直接向かう）。\n" +
             "Inspectorで自由に追加・削除・並べ替えができる。")]
    public List<Transform> hallwayGoBackPoints = new List<Transform>();

    [Tooltip("庭側（Window Peek）終了後、帰る向きへその場旋回するための地点（回転専用）。\n" +
             "gardenPeekPoint と同じ位置に置き、向きだけ帰る方向へ設定する。")]
    public Transform gardenTurnBackPoint;

    [Tooltip("Window Peek 終了後の帰路の中間ウェイポイント（Garden Turn Back地点から gardenPassByPoint へ向かう順）。\n" +
             "中間点だけを登録する。登録順に通過する。空でも可（空なら Turn Back 旋回後、gardenPassByPoint へ直接向かう）。\n" +
             "※ 最終点（gardenPassByPoint）はコードが最後に必ず通るため、このListに登録する必要はない\n" +
             "  （末尾に登録されていても重複しないよう安全に扱う）。\n" +
             "Inspectorで自由に追加・削除・並べ替えができる。")]
    public List<Transform> gardenGoBackPoints = new List<Transform>();

    [Tooltip("Door Peek 終了後の帰路の最終到達点（廊下の画面外）。\n" +
             "ここへ到着してから母親モデルを非表示にし、サイクルを終了する。Door側のみで使用する。\n" +
             "Window Peek側の最終点は gardenPassByPoint（既存フィールド）を使う。\n" +
             "未設定なら帰路を開始せず、警告を出して安全に非表示・終了する。")]
    public Transform hallwayPassThroughPoint;

    [Tooltip("帰路の最終点に到着してモデルを非表示にしたときに発生する。\n" +
             "（サイクル終了は既存の ResetCycle／EndWarningSequence／ResetApproach が担当する）")]
    public UnityEvent onReturnedHome;

    // ── Pass By（素通り）の立ち止まり ────────────────────────────────────────
    // 2キー（Hallway Pass By）／5キー（Garden Pass By）だけで使用する。
    // 往路リストの「最後の有効な点」へ到着 → 向き合わせ → 指定秒数立ち止まり →
    // Back Points へ進む、という流れにするための待機時間。
    // 0秒なら待機・Idle待機・停止旋回を省略し、従来の連続通過に戻る。
    [Header("Pass By（素通り）の立ち止まり")]
    [Tooltip("2キー（Hallway Pass By）で、往路の最後の点に到着して向きを合わせた後、\n" +
             "Back Points へ進む前に立ち止まる秒数（ゲーム内時間。0で無効＝連続通過）。\n" +
             "現在は hallwayPointsAfterTurn の最後の点（例：HallwayPoint_4）で停止します。\n" +
             "停止中は歩行表示（Walk=false）と足音を止め、再開時に歩行と足音を戻します。")]
    [SerializeField, Min(0f)]
    private float hallwayPassByPauseSeconds = 1f;

    [Tooltip("5キー（Garden Pass By）で、往路の最後の点に到着して向きを合わせた後、\n" +
             "Back Points へ進む前に立ち止まる秒数（ゲーム内時間。0で無効＝連続通過）。\n" +
             "現在は gardenRoutePoints の最後の点（例：GardenPoint_4）で停止します。\n" +
             "停止中は歩行表示（Walk=false）と足音を止め、再開時に歩行と足音を戻します。")]
    [SerializeField, Min(0f)]
    private float gardenPassByPauseSeconds = 1f;

    // ── 部屋内部（入室） ──────────────────────────────────────────────────────
    [Header("部屋内部（入室）")] [Tooltip("部屋内部の立ち位置。doorPointから近い順に設定する。未設定または空の場合は入室せず、従来どおりdoorPointで停止する。")]
    public Transform[] roomEntryPoints;

    [Tooltip("部屋から出るとき（doorPointへ戻るとき）に親機が向くY角度（度）。入室時はdoorPointでの向き（Y=90）を維持する。")] [SerializeField]
    private float roomExitYaw = -90f;

    [Tooltip("部屋内に留まれる最大秒数。この時間を超えると、退室要求がなくても自動的に退室する。0以下で無効（無制限）。")] [SerializeField]
    private float roomStayTimeoutSeconds = 20f;

    // ── 片付け専用ルート（MotherChoreController から開始される） ──────────────
    //   既存の廊下ルート（hallwaypeak）と固定地点はそのまま使い、途中地点だけを
    //   この List<Transform> で追加指定する。既存の廊下／庭ルートと同じ
    //   「Inspectorで登録順に通過」する形式。
    //   roomEntryPoints（入室ルート）とは独立で、片付けではこちらだけを使う
    //   （専用経路と roomEntryPoints を続けて再生しない）。
    [Header("片付け専用ルート")]
    [Tooltip("行きのドア開け地点①。既存廊下ルート（HallwayPoint_4 まで）の後にここへ到着し、" +
             "Door_Open を再生してドアを全開にする。必須参照（未設定なら片付けを開始しない）。")]
    public Transform choreApproachPoint_1;

    [Tooltip("行きのドア閉め地点②。ドア全開の完了後にここへ到着し、ドアを閉める（Door_Open は再生しない）。" +
             "必須参照（未設定なら片付けを開始しない）。")]
    public Transform choreApproachPoint_2;

    [Tooltip("行きの自由な途中地点。ドア閉め後に登録順に通過してから chorePoint へ向かう。\n" +
             "0個なら chorePoint へ直接進む。途中地点では停止・Idle待機をしない（通過のみ）。\n" +
             "null要素は警告してスキップする。固定のドア操作地点（_1/_2）とは役割を分けて維持する。")]
    public List<Transform> choreApproachPoints = new List<Transform>();

    [Tooltip("片付けを行う固定地点（choreapproachpoint）。ここで停止して向きを合わせ、Chore を開始する。未設定なら片付けを開始しない。")]
    public Transform chorePoint;

    [Tooltip("帰りの途中地点。Chore_End 完了後、登録順に通過してから choreReturnPoint_1 へ向かう。\n" +
             "0個なら choreReturnPoint_1 へ直接進む。途中地点では停止・Idle待機をしない（通過のみ）。\n" +
             "null要素は警告してスキップする。")]
    public List<Transform> choreEndPoints = new List<Transform>();

    [Tooltip("帰りの停止地点①。ここで停止・向き合わせを行い、Door_Open を再生してドアを全開にする。\n" +
             "片付けの帰りでドアを開けるのはこの地点だけ。必須参照（未設定なら片付けを開始しない）。")]
    [FormerlySerializedAs("choreReturnPoint")]
    public Transform choreReturnPoint_1;

    [Tooltip("帰りの停止地点②。ここへ移動して停止し、ドアを閉める（Door_Open は再生しない）。\n" +
             "ドアを閉めるのはこの地点だけ。必須参照（未設定なら片付けを開始しない）。")]
    public Transform choreReturnPoint_2;

    [Tooltip("片付けのドア開閉を担当する MotherChoreController。" +
             "Door_Open の再生とドア本体の回転を連携させるために使う。未設定なら自動検索する。")]
    [SerializeField]
    private MotherChoreController choreController;

    [Header("片付け固有")] [Tooltip("片付けルートで中間地点を通過するときの旋回速度（度／秒）。")] [SerializeField, Min(1f)]
    private float choreTurnRotationSpeed = 200f;

    // ── 移動 ──────────────────────────────────────────────────────────────────
    [Header("移動")] [Tooltip("到着と判定するウェイポイントまでの距離（単位）。")]
    public float stopDistance = 0.05f;

    [Tooltip("母親の足元が床に埋まる／浮く場合に調整。ワールドYに加算")] [SerializeField]
    private float motherHeightOffset;

    // ── 回転速度 ──────────────────────────────────────────────────────────────
    [Header("回転速度")] [Tooltip("階段の角で旋回するときの速度（度／秒）。")]
    public float turnRotation = 90f;

    [Tooltip("到着時にドアへ向く回転速度（度／秒）。")] public float doorTurnRotationSpeed = 120f;

    // ── 表示 ──────────────────────────────────────────────────────────────────
    [Header("親機モデルの表示")]
    [Tooltip("歩く親機モデルのルートGameObject。接近開始時にSetActive(true)にする。ここではSetActive(false)を呼ばず、PDがrealMotherObject経由で非表示を管理する。")]
    public GameObject motherModelRoot;

    [Tooltip("任意：motherModelRootだけでは不十分な場合に有効／無効にする子Renderer（例：LODの子）。")]
    public Renderer[] motherModelRenderers;

    // ── 演出 ──────────────────────────────────────────────────────────────────
    [Header("演出")] [Tooltip("母親の覗き見・捕獲突入時に点灯する目のオブジェクト。")] [SerializeField]
    private GameObject glowingEyesObject;

    [Header("窓覗き時の顔ライト")]
    [Tooltip("庭側の窓から覗くとき（GardenPeek）だけ母親の顔を照らすライト。母親モデルの子（顔の前）に置く。" +
             "覗き待機の開始でフェードイン、終了・リセットでフェードアウトする。未設定なら何もしない。")]
    [SerializeField]
    private Light windowPeekFaceLight;

    [Tooltip("顔ライトの明るさの基準値はここでは指定せず、シーンに置いたLightのintensityを" +
             "点灯時の明るさとしてStart時にキャッシュする（消灯中はintensity=0にするため）。")]
    [SerializeField]
    private bool windowPeekFaceLightEnabledNote = true;

    [Header("目の発光マテリアル/カラー")] [Tooltip("左目の発光用Renderer。")] [SerializeField]
    private Renderer eyeRendererL;

    [Tooltip("右目の発光用Renderer。")] [SerializeField]
    private Renderer eyeRendererR;

    [ColorUsage(true, true)] [SerializeField]
    private Color normalGlowColor = new Color(1f, 0.8f, 0.2f, 1f);

    [ColorUsage(true, true)] [SerializeField]
    private Color dangerGlowColor = new Color(1f, 0f, 0f, 1f);

    [Tooltip("怪しさメーターが紫（SuspicionVisualFeedbackの紫状態）のときの目の発光色。\n" +
             "赤より暗く見えないよう、既定は明るめの紫にしています。")]
    [ColorUsage(true, true)]
    [SerializeField]
    private Color purpleGlowColor = new Color(0.75f, 0.35f, 1f, 1f);

    [Tooltip("目の発光の強さ倍率。1で従来の明るさ（HDRの8倍）。\n" +
             "比較用に2へ上げると従来の約2倍になる。0で発光なし。\n" +
             "シーン全体のBloomは変更しないため、Bloomを強めたい場合はVolume側で別途調整する。")]
    [SerializeField, Min(0f)]
    private float eyeEmissionIntensity = 2f;

    [Tooltip("目が発光色で明滅する速さ（回/秒）。0で明滅なし（一定の明るさ）。")] [SerializeField, Min(0f)]
    private float eyeGlowPulseSpeed;

    [Header("母親モデルへの照明の追従")] [Tooltip("常時点灯する間、顔ライトを毎フレーム頭部のワールド回転へ一致させる。OFFならライトの向きはシーン配置のまま（従来挙動）。")] [SerializeField]
    private bool faceLightFollowsHead;

    [Tooltip("faceLightFollowsHeadがONのときに使う頭部のTransform。未設定ならmotherAnimatorのavatarから自動取得する。")] [SerializeField]
    private Transform faceLightHeadAnchor;

    [Tooltip("faceLightFollowsHeadがONのとき、頭部の向きに対して顔ライトが向く方向（頭部のローカル角度）。未設定（0,0,0）なら頭部の＋Z。")] [SerializeField]
    private Vector3 faceLightRotationOffset;

    [Tooltip("怪しさゲージ参照。未設定の場合はシーンから自動取得する。")] [SerializeField]
    private MotherGauge motherGauge;

    [SerializeField] private MotherSuspicionSystem parentDetection;

    // ── Door Peek 横スライド ──────────────────────────────────────────────────
    [Header("Door Peek 横スライド")]
    [Tooltip("Door Peek中に、母親自身の左／右へ短くスライドして戻る演出を有効にする。\n" +
             "Window Peek・Pass By・猫・入室・Rush Inには適用されません。")]
    [SerializeField]
    private bool enableDoorPeekSlide = true;

    [Tooltip("スライド量（ワールド単位）。正で母親自身の左、負で右。0で位置を変えない。\n" +
             "このモデルは親のスケールが計18倍（MotherRouteRoot 3 × TARGET_MASTER 6）なので、\n" +
             "4.0 で成人の肩幅ぶん（約0.22m相当）の控えめな動きになります。")]
    [SerializeField]
    private float doorPeekSlideDistance = 4.0f;

    [Tooltip("【時間の基準はDoor Peekアニメーションの実再生開始（Peek Stateへ入った瞬間）】\n" +
             "実再生開始から外向きスライドを始めるまでの待ち時間（秒）。")]
    [SerializeField, Min(0f)]
    private float doorPeekSlideStartDelay = 2.10f;

    [Tooltip("【時間の基準は外向きスライドの開始時点】\n" +
             "外向きスライド開始から、戻りスライドを始めるまでの時間（秒）。\n" +
             "「外向き移動が完了してから2秒」ではありません。")]
    [SerializeField, Min(0f)]
    private float doorPeekSlideReturnDelay = 2.00f;

    [Tooltip("外向きスライドの移動時間（秒）。0ならその区間だけ即時移動。")] [SerializeField, Min(0f)]
    private float doorPeekSlideOutDuration = 0.30f;

    [Tooltip("戻りスライドの移動時間（秒）。0ならその区間だけ即時移動。")] [SerializeField, Min(0f)]
    private float doorPeekSlideBackDuration = 0.30f;

    [Tooltip("Door Peek Stateへ入るまで待つ上限【ゲーム内時間の秒数】。\n" +
             "上限に達しても入れなければ、スライドを開始せず警告して安全に中止します。\n" +
             "Time.deltaTimeで計測するため、timeScale=0の通常一時停止中は進みません。\n" +
             "（フレーム数ではなく秒数なので、高フレームレートでも遷移完了を待てます）")]
    [SerializeField, Min(0f)]
    private float doorPeekSlideStateWaitTimeoutSeconds = 3f;

    [Tooltip("Door Peek横スライドの各段階（要求受理／State待ち／外向き／戻り／完了・取消）を" +
             "コンソールにログ出力する。原因調査用。通常はOFFのままで構いません。")]
    [SerializeField]
    private bool doorPeekSlideVerboseLog;

    // ── タイミング ────────────────────────────────────────────────────────────
    [Header("タイミング")] [Tooltip("通常ルートで、OnStoppedAtDoorイベント前にドアで停止する秒数。")]
    public float pauseAtDoorSeconds = 2f;

    [Tooltip("大きな音による突入ルートで、OnStoppedAtDoorイベント前にドアで停止する秒数。")]
    public float rushInPauseAtDoorSeconds = 0.2f;

    // ── イベント ──────────────────────────────────────────────────────────────
    [Header("イベント")] [FormerlySerializedAs("OnApproachStarted")]
    public UnityEvent onApproachStarted;

    [FormerlySerializedAs("OnReachedDoor")]
    public UnityEvent onReachedDoor;

    [FormerlySerializedAs("OnStoppedAtDoor")]
    public UnityEvent onStoppedAtDoor;

    [FormerlySerializedAs("OnPassedByDoor")]
    public UnityEvent onPassedByDoor;

    [Tooltip("親機が部屋内部への入室を完了したときに発生する（入室が受理されたサイクルのみ）。")]
    public UnityEvent onEnteredRoom;

    [Tooltip("親機が部屋内部からの退室を完了し、doorPointへ戻ったときに発生する。")]
    public UnityEvent onExitedRoom;

    [Tooltip("親機がGardenPeekPointで覗き待機を開始したときに発生する。庭覗き中の継続疑惑（MotherSuspicionSystem）の開始トリガー。")]
    public UnityEvent onGardenPeekStarted;

    // ── 片付け専用ルートのイベント・状態 ─────────────────────────────────────
    [Tooltip("片付けルートで chorePoint へ到着し、向き合わせが完了したときに発生する（演技開始の合図）。")]
    public UnityEvent onChoreArrived;

    [Tooltip("片付けルートで choreReturnPoint_2 へ到着し、ドアを閉め終えたときに発生する。")]
    public UnityEvent onChoreCompleted;

    /// <summary>片付け専用ルートが進行中か（行き〜帰りの完了まで）。</summary>
    public bool IsChoreRouteActive { get; private set; }

    public event Action<bool> MovementStateChanged;

    // ── 公開読み取り専用状態 ──────────────────────────────────────────────────
    public bool IsApproaching { get; private set; }
    public bool ReachedDoor { get; private set; }
    public bool StoppedAtDoor { get; private set; }
    public bool PassedByDoor { get; private set; }
    public bool IsInHallwayPhase { get; private set; }

    /// <summary>GardenPeekPointで覗き待機中か。庭覗き専用の状態で、isMotherLookingNow（ドア側の本チェック）には影響しない。</summary>
    public bool IsGardenPeeking => _isGardenPeeking;

    public bool IsGardenRoute { get; private set; }
    public float CurrentApproachSpeed => parentDetection != null ? parentDetection.CurrentApproachSpeed : 1.5f;

    // ── 実行モード ────────────────────────────────────────────────────────────
    /// <summary>大きな音による突入開始前にMotherApproachWarningが設定する。移動ループ音を抑制し、rushInPauseAtDoorSecondsを使用する。</summary>
    public bool IsRushIn { get; set; }

    // ── 非公開 ───────────────────────────────────────────────────────────────
    private Coroutine _approachCoroutine;
    private float _fixedPitch;
    private float _fixedRoll;
    private float _gardenPeekDuration; // GardenPeekの覗き時間（秒）。GardenPeekPoint到着時に一度だけ決定する。
    private bool _isGardenPeeking; // GardenPeekPointで覗き待機中か。isMotherLookingNowには影響しない。
    private float _faceLightBaseIntensity = 1f; // 顔ライトの「点灯時の明るさ」（Inspectorで設定された値）

    // 目の発光マテリアルの実行時キャッシュ（実行中にマテリアルを繰り返し生成しないための保持）。
    //
    //  _eyeOriginalMaterialX : 生成前にRendererが使っていた元マテリアル（参照復元用）
    //  _eyeOwnedMaterialX    : このコンポーネントが new Material で生成し所有するインスタンス
    //
    //  「現在の sharedMaterial と一致するか」はアセット判定に使わない。生成したインスタンスを
    //  Rendererへ割り当てると sharedMaterial もそのインスタンスを返すため、判定に使うと
    //  自分のインスタンスを破棄できなくなる。所有判定は _eyeOwnedMaterialX の有無だけで行う。
    private Material _eyeOriginalMaterialL;
    private Material _eyeOriginalMaterialR;
    private Material _eyeOwnedMaterialL;
    private Material _eyeOwnedMaterialR;

    /// <summary>直近に適用した発光色（ゲージ由来の通常色／危険色）。閾値またぎの検出に使う。</summary>
    private Color _lastAppliedGlowColor;

    private bool _eyesOn; // 目の発光がONか（グループ（A）基本）か
    private bool _faceLightOn; // 顔ライトがONか（グループ（A）基本）か
    private bool _peekLightingOn; // Peek中の追加照明がONか（グループ（B））か
    private bool _faceLightFollowActive; // 顔ライトの頭部追従が有効か

    // Door Peek 横スライドの実行状態。
    // ・_peekSlideCoroutine : スライド演出のコルーチン（Peekごとに1本だけ）
    // ・_peekSlideOffset    : 現在のずれ量（ワールド）。0なら元位置にいる。
    // ・_peekSlideOriginPosition : 元位置。ずれを戻す先（必ずこの位置へそろえる）。
    private Coroutine _peekSlideCoroutine;
    private bool _peekSlideRequested; // 同一Peekで一度だけ開始するための予約フラグ
    private Vector3 _peekSlideOffset;
    private Vector3 _peekSlideOriginPosition;
    private bool _peekSlideOriginValid;
    private Transform _faceLightHeadTransformCache; // avatarから自動取得した頭部Transform
    private bool _faceLightHeadResolveAttempted; // 自動取得を試みたか（無駄な再探索を避ける）

    /// <summary>
    /// 進行中の庭ルート旋回コルーチン。停止・リセット時に確実に停止させるためのハンドル。
    /// </summary>
    private Coroutine _rotateCoroutine;

    // 部屋入室（案B）の状態
    private bool _cycleStartedAsRushIn; // このサイクルが突入（大きな音）として開始されたか — BeginApproach()で捕捉する
    private bool _doorRoutineActive; // DoorRoutine()が実行中か（入室要求の受付条件）
    private bool _roomEntryRequested; // OnStoppedAtDoor中にPDから入室要求を受けたか
    private bool _roomPhaseActive; // 入室フェーズ（部屋内部への移動〜doorPoint復帰）が進行中か
    private bool _leaveRoomRequested; // 部屋内部からの退室要求を受けたか
    private bool _routeExecutionFailed; // 必須条件に失敗して現在のルートを中断したか

    // 片付け専用ルートの状態
    private Coroutine _choreCoroutine; // 片付けルート（行き〜帰り）のコルーチン
    private bool _choreRouteAborted; // 片付けルートを中断したか（帰路の各ループが参照する）
    private bool _chorePerformanceFinished; // 片付け演技が終了したか（演技待ちを抜ける合図。IsChoreRouteActive は帰りも維持する）

    // ──────────────────────────────────────────────────────────────────────────
    //  Unityライフサイクル
    // ──────────────────────────────────────────────────────────────────────────

    // ──────────────────────────────────────────────────────────────────────────
    //  廊下ルートの解決（List ＋ 旧個別フィールドの互換）
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>このインスタンスで実行時の互換移行を実施済みか（多重実行を避けるためのメモリ上フラグ）。</summary>
    private bool _hallwayRouteResolved;

    /// <summary>編集時に移行済みか（Editorツール・テストから参照するための読み取り専用公開）。</summary>
    public bool IsHallwayRouteMigrated => hallwayRouteMigrated;

    /// <summary>
    /// 【Editor専用】旧フィールドから List への移行を、編集時のオブジェクトへ反映する。
    /// 呼び出し側（Editorツール）が Undo・SetDirty・PrefabOverride を担当する。
    ///
    /// 戻り値：実際に List へ追加した点数（0 なら「既に移行済み」または「旧フィールドが空」）。
    /// 既存の List 要素は変更・削除しない（重複追加もしない）。
    /// 片側だけ未設定の場合は、その側だけを取り込む。
    /// </summary>
    public int ApplyEditTimeMigration()
    {
        int migrated = 0;

        if (hallwayPointsBeforeTurn == null) hallwayPointsBeforeTurn = new List<Transform>();
        if (hallwayPointsAfterTurn == null) hallwayPointsAfterTurn = new List<Transform>();

        // 未移行のときだけ旧フィールドを取り込む（移行済みなら旧フィールドは読まない）。
        if (!hallwayRouteMigrated)
        {
            // TurnPointより前：旧フィールドを登録順（hallwayPoint1 → hallwayPoint2）に追加する。
            // 既にListに入っている点は追加しない（重複防止）。
            if (hallwayPoint1 != null && !hallwayPointsBeforeTurn.Contains(hallwayPoint1))
            {
                hallwayPointsBeforeTurn.Add(hallwayPoint1);
                migrated++;
            }

            if (hallwayPoint2 != null && !hallwayPointsBeforeTurn.Contains(hallwayPoint2))
            {
                hallwayPointsBeforeTurn.Add(hallwayPoint2);
                migrated++;
            }

            // TurnPointより後：旧フィールドを追加する。
            if (hallwayPoint3 != null && !hallwayPointsAfterTurn.Contains(hallwayPoint3))
            {
                hallwayPointsAfterTurn.Add(hallwayPoint3);
                migrated++;
            }

            hallwayRouteMigrated = true;
        }

        return migrated;
    }

    /// <summary>
    /// 【Editor専用】旧フィールドから List を作り直す（既存の List 内容を破棄する）。
    /// 「移行はしたが経路を旧値の状態からやり直したい」場合の明示的な操作で、
    /// 通常の ApplyEditTimeMigration() とは別物。呼び出し側が事前に警告すること。
    /// </summary>
    public int RebuildFromLegacyFields()
    {
        hallwayPointsBeforeTurn = new List<Transform>();
        hallwayPointsAfterTurn = new List<Transform>();

        if (hallwayPoint1 != null) hallwayPointsBeforeTurn.Add(hallwayPoint1);
        if (hallwayPoint2 != null) hallwayPointsBeforeTurn.Add(hallwayPoint2);
        if (hallwayPoint3 != null) hallwayPointsAfterTurn.Add(hallwayPoint3);

        hallwayRouteMigrated = true;
        _hallwayRouteResolved = true;

        return hallwayPointsBeforeTurn.Count + hallwayPointsAfterTurn.Count;
    }

    /// <summary>
    /// 実行時の互換移行。未移行の古いシーンを実行するためだけの処理で、
    /// メモリ上の List を埋めるだけで、編集時のシーン/Prefabへは一切書き戻さない。
    ///
    ///   ・編集時に移行済み（hallwayRouteMigrated == true）→ List を唯一の設定元としてそのまま使う
    ///     （空リストも「空」という設定として尊重し、旧フィールドは読まない）
    ///   ・未移行 → allowRuntimeCompatMigration が ON のときだけ旧フィールドを List へ読み替える
    ///
    /// 呼び出し元は MotherSuspicionSystem／MotherApproachController／CatFeintController のいずれでも、
    /// 判定がシリアライズ値のみなので、呼び出し順によって結果が変わらない。
    /// </summary>
    public void MigrateLegacyHallwayPoints()
    {
        _hallwayRouteResolved = true;

        if (hallwayPointsBeforeTurn == null) hallwayPointsBeforeTurn = new List<Transform>();
        if (hallwayPointsAfterTurn == null) hallwayPointsAfterTurn = new List<Transform>();

        // 編集時に移行済み：Listが唯一の設定元（空でもそのまま尊重し、旧フィールドは読まない）。
        if (hallwayRouteMigrated) return;

        // 実行時の互換移行を無効化している場合は何もしない（Listだけを使う）。
        if (!allowRuntimeCompatMigration) return;

        int migrated = 0;

        // TurnPointより前：旧フィールドを登録順（hallwayPoint1 → hallwayPoint2）に取り込む。
        if (hallwayPoint1 != null && !hallwayPointsBeforeTurn.Contains(hallwayPoint1))
        {
            hallwayPointsBeforeTurn.Add(hallwayPoint1);
            migrated++;
        }

        if (hallwayPoint2 != null && !hallwayPointsBeforeTurn.Contains(hallwayPoint2))
        {
            hallwayPointsBeforeTurn.Add(hallwayPoint2);
            migrated++;
        }

        // TurnPointより後：旧フィールドを取り込む。
        if (hallwayPoint3 != null && !hallwayPointsAfterTurn.Contains(hallwayPoint3))
        {
            hallwayPointsAfterTurn.Add(hallwayPoint3);
            migrated++;
        }

        if (migrated > 0)
        {
            Debug.LogWarning($"[MotherApproachController] 未移行のシーンのため、実行時だけ旧hallwayPoint1〜3をListへ読み替えました" +
                             $"（取り込み={migrated}点）。これはメモリ上だけで、編集時のシーンには保存されません。" +
                             "永続化するには Editor メニュー「Tools/親機ルート: 選択中を移行」を実行し、シーンを保存してください。", this);
        }
    }

    /// <summary>
    /// 廊下ルート（TurnPointより前）の通過順リストを取得する。
    /// 猫フェイントが母親とまったく同じ経路を辿れるよう、MotherApproachController側の設定をそのまま使う。
    ///
    /// 経路取得の入口として、未解決ならここで1度だけ実行時の互換移行を実施する。
    /// これにより「猫と母親のどちらが先に取得しても」「Startを経由せず取得しても」、
    /// 初期化前の空Listを返さず、同じ経路設定になる（実行順設定には依存しない）。
    /// </summary>
    public List<Transform> GetHallwayPointsBeforeTurn() => BuildHallwayPath(hallwayPointsBeforeTurn);

    /// <summary>
    /// 廊下ルート（TurnPointより後）の通過順リストを取得する（ドア確認ルート／猫フェイント用）。
    /// 取得順に依存しないよう、こちらも入口で1度だけ解決する（GetHallwayPointsBeforeTurn参照）。
    /// </summary>
    public List<Transform> GetHallwayPointsAfterTurn() => BuildHallwayPath(hallwayPointsAfterTurn);

    /// <summary>
    /// 経路を「通過する順」の一時リストで返す。
    /// 未解決なら先に実行時の互換移行を実施する（＝経路取得のどの入口から呼ばれても初期化前の空Listを返さない）。
    /// 同じTransformの連続登録は後に来る方を残す（同じ点で停止し続けるのを防ぐ）。
    /// </summary>
    private List<Transform> BuildHallwayPath(List<Transform> points)
    {
        if (!_hallwayRouteResolved) MigrateLegacyHallwayPoints();

        var path = new List<Transform>();
        if (points == null) return path;

        for (int i = 0; i < points.Count; i++)
        {
            Transform point = points[i];
            if (point == null) continue; // null要素はスキップ
            if (path.Count > 0 && path[path.Count - 1] == point) continue; // 同じ点の連続登録
            path.Add(point);
        }

        return path;
    }

    /// <summary>
    /// List&lt;Transform&gt; を「通過する順」の一時リストにする（帰路List用の共通処理）。
    /// null要素はスキップし、同じ点の連続登録は後に来る方だけを残す
    /// （同じ点で止まり続けたり、例外・無限待機になったりしない）。
    /// 空Listはそのまま空を返す（呼び出し側が「直接最終点へ向かう」よう扱う）。
    /// </summary>
    private static List<Transform> BuildTransformPath(List<Transform> points)
    {
        var path = new List<Transform>();
        if (points == null) return path;

        for (int i = 0; i < points.Count; i++)
        {
            Transform point = points[i];
            if (point == null) continue; // null要素はスキップ
            if (path.Count > 0 && path[path.Count - 1] == point) continue; // 同じ点の連続登録
            path.Add(point);
        }

        return path;
    }

    private void Start()
    {
        // 実行時の互換移行を1回だけ実施する（編集時に移行済みなら List がそのまま使われる）。
        MigrateLegacyHallwayPoints();

        CacheFaceLightIntensity();
        // 起動時は母親モデル非表示のため、グループ（A)(B）とも消灯しておく。
        SetMotherStageLighting(false);
        SetPeekLighting(false);
        if (parentDetection == null)
            parentDetection = UnityEngine.Object.FindFirstObjectByType<MotherSuspicionSystem>();

        // 覗き時間の計算に使う疑惑ゲージをキャッシュする（見つからない場合は覗き開始時に再試行する）。
        if (motherGauge == null)
            motherGauge = UnityEngine.Object.FindFirstObjectByType<MotherGauge>();
    }

    private void Update()
    {
        if (!_eyesOn) return;

        // 明滅が設定されているときだけ、毎フレーム見た目の色を更新する。
        if (eyeGlowPulseSpeed > 0f)
        {
            UpdateEyeColor();
            return;
        }

        // 明滅なしでも、疑惑ゲージが閾値をまたいだら通常色／危険色を切り替える。
        // 毎フレーム強制書き込みはせず、「前回適用した色と違うとき」だけ更新する。
        if (_lastAppliedGlowColor != ColorForCurrentGauge())
            UpdateEyeColor();
    }

    /// <summary>
    /// 頭部追従を常時点灯中だけ反映する（Animatorの更新後に合わせるためLateUpdateで行う）。
    /// </summary>
    private void LateUpdate()
    {
        FaceLightLateUpdate();
    }

    private void OnDisable()
    {
        // 非表示時はマテリアルを破棄せず保持する（再表示で同じインスタンスを使い回す）。
        // 破棄はコンポーネント破棄時の OnDestroy に1回だけ集約する。
        ReleaseEyeMaterials();
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  公開API
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>既定の入口 — StartApproachDoorOnly()へ委譲する。既存シーンの接続との互換性のため残す。</summary>
    public void StartApproach()
    {
        StartApproachDoorOnly();
    }

    /// <summary>ドア停止ルートを開始する：親機がdoorPointまで歩き、部屋の方向を向いて停止する。突入ルートでも使用する。</summary>
    public bool StartApproachDoorOnly()
    {
        if (IsApproaching || IsReturningHome)
        {
            Debug.Log(
                $"[MotherApproachController] 接近中または帰路中のため開始しません (IsApproaching={IsApproaching} IsReturningHome={IsReturningHome}) — StartApproachDoorOnlyを無視");
            return false;
        }

        if (!ValidateWaypoints()) return false;

        BeginApproach();
        return true;
    }

    /// <summary>
    /// フェイントA（HallwayPassBy）を開始する：TurnPointからドア方向へ進み、
    /// doorPointでは停止せずに通り過ぎてhallwayPassByPoint（画面外）へ到達する。
    /// onReachedDoor／onStoppedAtDoorは発生させず、DoorControllerは操作しない。
    /// 終了時は既存のonPassedByDoorを発生させる（PD.OnApproachPassedBy → ResetCycle → EndWarningSequence）。
    /// hallwayPassByPointが未設定の場合は警告を1回出して開始しない。
    /// </summary>
    public bool StartApproachHallwayPassBy()
    {
        if (IsApproaching || IsReturningHome)
        {
            Debug.Log(
                $"[MotherApproachController] 接近中または帰路中のため開始しません (IsApproaching={IsApproaching} IsReturningHome={IsReturningHome}) — StartApproachHallwayPassByを無視");
            return false;
        }

        if (hallwayPassByPoint == null)
        {
            Debug.LogWarning(
                "[MotherApproachController] hallwayPassByPointが未設定のためHallwayPassByを開始しません。SceneでTransformを割り当ててください。",
                this);
            return false;
        }

        if (!ValidateWaypoints()) return false;

        BeginApproach(hallwayPassBy: true);
        return true;
    }

    /// <summary>
    /// 庭側素通りを開始する。ドア停止イベントを発生させず、庭側到達点でonPassedByDoorを発生させる。
    /// </summary>
    public bool StartApproachGardenPassBy()
    {
        if (IsApproaching || IsReturningHome)
        {
            Debug.Log(
                $"[MotherApproachController] 接近中または帰路中のため開始しません (IsApproaching={IsApproaching} IsReturningHome={IsReturningHome}) — StartApproachGardenPassByを無視");
            return false;
        }

        if (gardenPassByPoint == null)
        {
            Debug.LogWarning(
                "[MotherApproachController] gardenPassByPointが未設定のためGardenPassByを開始しません。SceneでTransformを割り当ててください。",
                this);
            return false;
        }

        if (!HasValidGardenRoutePoints())
        {
            Debug.LogWarning(
                "[MotherApproachController] gardenRoutePointsが未設定のためGardenPassByを開始しません。SceneでTurnPoint→GardenPeekPoint間の中間ウェイポイントを順番に割り当ててください。",
                this);
            return false;
        }

        if (!ValidateWaypoints()) return false;

        BeginApproach(gardenPassBy: true);
        return true;
    }

    /// <summary>
    /// gardenRoutePointsに有効（null以外）な中間ウェイポイントが1つ以上あるかを返す。
    /// </summary>
    private bool HasValidGardenRoutePoints()
    {
        if (gardenRoutePoints == null) return false;
        for (int i = 0; i < gardenRoutePoints.Length; i++)
        {
            if (gardenRoutePoints[i] != null) return true;
        }

        return false;
    }

    /// <summary>
    /// 庭側覗きを開始する。GardenPassByと同じ経路でGardenPeekPointまで進み、停止して覗き方向へ回転する。
    /// 覗き時間（MotherApproachWarning の庭覗き時間 + GardenPeekPoint到着時のゲージ値。到着時に一度だけ取得）経過後、
    /// GardenPassByPointまで進み、onPassedByDoorを一度だけ発生させて既存の終了処理でStartPointへ復帰する。
    /// </summary>
    public bool StartApproachGardenPeek()
    {
        if (IsApproaching || IsReturningHome)
        {
            Debug.Log(
                $"[MotherApproachController] 接近中または帰路中のため開始しません (IsApproaching={IsApproaching} IsReturningHome={IsReturningHome}) — StartApproachGardenPeekを無視");
            return false;
        }

        if (gardenPeekPoint == null)
        {
            Debug.LogWarning(
                "[MotherApproachController] gardenPeekPointが未設定のためGardenPeekを開始しません。SceneでTransformを割り当ててください。", this);
            return false;
        }

        if (!HasValidGardenRoutePoints())
        {
            Debug.LogWarning(
                "[MotherApproachController] gardenRoutePointsが未設定のためGardenPeekを開始しません。SceneでTurnPoint→GardenPeekPoint間の中間ウェイポイントを順番に割り当ててください。",
                this);
            return false;
        }

        if (!ValidateWaypoints()) return false;

        BeginApproach(gardenPeek: true);
        return true;
    }

    /// <summary>
    /// 親機を部屋内部へ入室させる要求。OnStoppedAtDoorの処理中（＝ドア停止ルート実行中）にPDから呼ばれる。
    /// 受理した場合は、ドア停止後にroomEntryPoints[]へ移動し、入室完了でonEnteredRoomを発生する。
    /// 次のいずれかに該当する場合はfalseを返し、呼び出し側は従来どおりの即時処理を行う：
    ///   ドア停止ルートが実行中ではない／突入（IsRushIn）サイクルである／roomEntryPointsが未設定または空である。
    /// </summary>
    public bool RequestRoomEntry()
    {
        if (!_doorRoutineActive)
        {
            Debug.Log("[MotherApproachController] RequestRoomEntry 却下：ドア停止ルートが実行中ではない");
            return false;
        }

        if (_cycleStartedAsRushIn)
        {
            Debug.Log("[MotherApproachController] RequestRoomEntry 却下：突入サイクルのため入室しない");
            return false;
        }

        if (CountValidRoomEntryPoints() == 0)
        {
            // 未設定／全要素Noneの場合はログを出さず、従来どおりdoorPointで停止する挙動へフォールバックする。
            // 有効なTransformが1個以上あるときだけ入室を受理する。
            return false;
        }

        if (doorPoint == null)
        {
            Debug.LogWarning("[MotherApproachController] RequestRoomEntry 却下：doorPointがNULLです。", this);
            return false;
        }

        _roomEntryRequested = true;
        Debug.Log($"[MotherApproachController] RequestRoomEntry 受理 | roomEntryPoints={roomEntryPoints.Length}");
        return true;
    }

    /// <summary>
    /// 親機を部屋内部から退室させる要求。入室フェーズが進行中の場合のみ受理する。
    /// 親機は入室時と逆順でdoorPointへ戻り、退室完了でonExitedRoomを発生する。
    /// </summary>
    public bool RequestLeaveRoom()
    {
        if (!_roomPhaseActive)
        {
            Debug.Log("[MotherApproachController] RequestLeaveRoom 却下：入室フェーズが進行中ではない");
            return false;
        }

        _leaveRoomRequested = true;
        Debug.Log("[MotherApproachController] RequestLeaveRoom 受理 — 部屋から退室する");
        return true;
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  片付け専用ルート（MotherChoreController から開始・中断される）
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 片付け専用ルートを開始する。
    ///  行き：既存の廊下ルート（hallwaypeak と同じ移動順序・移動方法）
    ///        startPoint → hallwayPointsBeforeTurn → turnPoint → hallwayPointsAfterTurn
    ///        → doorPoint（Door_Open で開ける）→ choreApproachPoints（登録順に通過）
    ///        → chorePoint（到着・その点の＋Zへ向き合わせ）→ onChoreArrived（演技開始の合図）
    ///  演技待ち → 帰り：choreEndPoints（登録順に通過）→ choreReturnPoint_1（停止・向き合わせ・Door_Open で全開）
    ///        → choreReturnPoint_2（停止・向き合わせ・ドアを閉める）→ HallwayPoint_3（ドア操作なし）
    ///        → hallwayPassByPoint（退場）→ onChoreCompleted
    ///
    /// 仕様：
    ///   ・専用の経路リストは新設しない（既存の廊下ルートを再利用する）。
    ///   ・途中地点リスト（choreApproachPoints / choreEndPoints）は0個なら固定地点へ直接進む。
    ///   ・途中地点は停止・Idle待機をしない（既存の通過移動処理 PassThroughWaypoint を使う）。
    ///   ・片付け演技の開始は chorePoint 到着時のみ。片付けの帰りでドアを開けるのは choreReturnPoint_1 到着時のみ、
    ///     ドアを閉めるのは choreReturnPoint_2 到着時のみ（HallwayPoint_3 ではドア操作をしない）。
    ///   ・Door_Peek は再生しない（片付けのドア開けは Door_Open 専用）。
    ///   ・chorePoint / choreReturnPoint_1 / choreReturnPoint_2 が未設定なら開始せず、設定不足を警告する。
    ///   ・roomEntryPoints（入室ルート）は使わない（専用経路と入室ルートを続けて再生しない）。
    ///   ・通常の接近中／帰路中／片付けルート進行中は開始しない。
    /// </summary>
    public bool StartChoreRoute()
    {
        // 段階ログ（6キー手動開始の追跡用・1回だけ）。必須参照の有無を1行で出す。
        Debug.Log(
            "[MotherApproachController] StartChoreRoute：必須参照チェック | " +
            $"choreApproachPoint_1={(choreApproachPoint_1 != null ? choreApproachPoint_1.name : "NULL")} | " +
            $"choreApproachPoint_2={(choreApproachPoint_2 != null ? choreApproachPoint_2.name : "NULL")} | " +
            $"chorePoint={(chorePoint != null ? chorePoint.name : "NULL")} | " +
            $"choreReturnPoint_1={(choreReturnPoint_1 != null ? choreReturnPoint_1.name : "NULL")} | " +
            $"choreReturnPoint_2={(choreReturnPoint_2 != null ? choreReturnPoint_2.name : "NULL")} | " +
            $"hallwayPassByPoint={(hallwayPassByPoint != null ? hallwayPassByPoint.name : "NULL")} | " +
            $"startPoint={(startPoint != null ? startPoint.name : "NULL")}");

        if (IsApproaching || IsReturningHome || IsChoreRouteActive)
        {
            Debug.Log(
                $"[MotherApproachController] 片付けルートを開始しません (IsApproaching={IsApproaching} IsReturningHome={IsReturningHome} IsChoreRouteActive={IsChoreRouteActive})");
            return false;
        }

        // 行きのドア開け地点①（必須）。行きはここで Door_Open を再生して全開にする。
        if (choreApproachPoint_1 == null)
        {
            Debug.LogWarning("[MotherApproachController] 片付けルートの choreApproachPoint_1（行きのドア開け地点）が未設定です。" +
                             "Scene で Transform を割り当ててください（設定不足のため開始しません）。", this);
            return false;
        }

        // 行きのドア閉め地点②（必須）。ドア操作はこの明示参照の到着時だけ行う。
        if (choreApproachPoint_2 == null)
        {
            Debug.LogWarning("[MotherApproachController] 片付けルートの choreApproachPoint_2（行きのドア閉め地点）が未設定です。" +
                             "Scene で Transform を割り当ててください（設定不足のため開始しません）。", this);
            return false;
        }

        if (chorePoint == null)
        {
            Debug.LogWarning("[MotherApproachController] 片付けルートの chorePoint が未設定です。" +
                             "Scene で片付け地点の Transform を割り当ててください（設定不足のため開始しません）。", this);
            return false;
        }

        if (choreReturnPoint_1 == null)
        {
            Debug.LogWarning("[MotherApproachController] 片付けルートの choreReturnPoint_1（帰りのドア開け地点）が未設定です。" +
                             "Scene で Transform を割り当ててください（設定不足のため開始しません）。", this);
            return false;
        }

        if (choreReturnPoint_2 == null)
        {
            Debug.LogWarning("[MotherApproachController] 片付けルートの choreReturnPoint_2（帰りのドア閉め地点）が未設定です。" +
                             "Scene で Transform を割り当ててください（設定不足のため開始しません）。", this);
            return false;
        }

        // 帰りの最終点（hallwayPassByPoint）。未設定だと退場待ちが成立しないため開始しない。
        if (hallwayPassByPoint == null)
        {
            Debug.LogWarning("[MotherApproachController] 片付けの帰りの最終点（hallwayPassByPoint）が未設定のため、" +
                             "片付けルートを開始しません。Scene で Transform を割り当ててください。", this);
            return false;
        }

        if (startPoint == null)
        {
            Debug.LogWarning("[MotherApproachController] startPoint が未設定のため片付けルートを開始できません。", this);
            return false;
        }

        BeginApproach(choreRoute: true);
        Debug.Log("[MotherApproachController] StartChoreRoute：検証OK — 片付けルート（行き）を開始します");
        return true;
    }

    /// <summary>
    /// 片付けルートを中断する（ゲームオーバー・OnDisable・再プレイ用）。
    ///   ・移動ループを中断フラグで止める
    ///   ・進行中の旋回コルーチンを停止する
    ///   ・歩行・足音（MovementStateChanged(false)）を止める
    /// 位置の復帰は呼び出し側（ResetApproach）に委ねる（画面内で瞬間移動しない）。
    /// </summary>
    public void AbortChoreRoute(string reason)
    {
        if (!IsChoreRouteActive && _choreCoroutine == null) return;

        Debug.LogWarning($"[MotherApproachController] 片付けルートを中断します（{reason}） — 停止・後始末を実施");

        _choreRouteAborted = true;
        IsChoreRouteActive = false;

        StopRotateCoroutine();
        MovementStateChanged?.Invoke(false);
        SetPeekLighting(false);
        SetMotherStageLighting(false);
        IsApproaching = false;
        IsInHallwayPhase = false;

        // 片付けの歩き/移動抑止が残ると通常の親イベントが動かなくなるため、必ず解除する。
        if (parentDetection != null)
        {
            parentDetection.SetChoreWalkingOverrideSuppressed(false);
            parentDetection.SetChoreMovementSuppressed(false);
        }
    }

    /// <summary>再プレイ・シーン開始時に片付けルートの状態を初期化する。</summary>
    public void ResetChoreRoute()
    {
        if (_choreCoroutine != null)
        {
            StopCoroutine(_choreCoroutine);
            _choreCoroutine = null;
        }

        _choreRouteAborted = false;
        _chorePerformanceFinished = false;
        IsChoreRouteActive = false;
    }

    /// <summary>
    /// 片付けの演技が終わったことを通知する（MotherChoreController から呼ばれる）。
    /// これを受けて ChoreRoutine は演技待ちを抜け、帰りの経路へ進む。
    /// </summary>
    public void NotifyChorePerformanceFinished()
    {
        if (!IsChoreRouteActive) return;
        Debug.Log("[MotherApproachController] 片付け演技の終了通知を受信 — 帰りの経路へ進みます");
        // IsChoreRouteActive は帰りの経路中も true のまま維持する（Walk・足音・経路ガードに使う）。
        // 演技待ちループは _chorePerformanceFinished を見て抜ける。
        _chorePerformanceFinished = true;
    }

    /// <summary>
    /// 片付けルート本体（行き → 演技待ち → 帰り）。
    ///  行き：既存廊下ルート（HallwayPoint_4 まで）→ choreApproachPoint_1（Door_Open 全開）
    ///        → choreApproachPoint_2（ドア閉め）→ choreApproachPoints（自由通過）→ chorePoint（停止・向き合わせ）
    ///  帰り：choreReturnPoint_1（Door_Open 全開）→ choreReturnPoint_2（ドア閉め）
    ///        → hallwayPassByPoint（退場）
    /// ドア操作は明示した固定参照の到着時だけ行い、途中地点リストの番号からは決めない。
    /// </summary>
    private IEnumerator ChoreRoutine()
    {
        IsChoreRouteActive = true;
        _choreRouteAborted = false;
        _chorePerformanceFinished = false;

        // ══ 行き：hallwaypeak と同じ既存廊下ルートを歩く ═══════════════════════
        //   startPoint → hallwayPointsBeforeTurn → turnPoint → hallwayPointsAfterTurn（HallwayPoint_4 まで）
        //   → choreApproachPoint_1（Door_Open で全開）→ choreApproachPoint_2（ドア閉め）
        //   → choreApproachPoints（自由通過）→ chorePoint（停止・向き合わせ）
        // ※ doorPoint は通らない（行きのドア操作は choreApproachPoint_1/_2 で行う）。
        // ※ Door_Peek は再生しない（片付けのドア開けは Door_Open 専用）。
        // ※ roomEntryPoints（通常の入室ルート）は使わない。
        Debug.Log($"[MotherApproachController] 片付けルート：行き開始（既存廊下ルートを再利用）| chorePoint='{chorePoint.name}'");

        // 1) 廊下ルート（TurnPointまで）。既存の DoorRoutine と同じ移動処理。
        yield return MoveToTurnPoint();
        if (_choreRouteAborted || _routeExecutionFailed)
        {
            FinishChoreRouteAborted();
            yield break;
        }

        // 2) TurnPointより後の廊下（HallwayPoint_3 / HallwayPoint_4）を通過する。
        yield return MoveAlongHallwayAfterTurn();
        if (_choreRouteAborted || _routeExecutionFailed)
        {
            FinishChoreRouteAborted();
            yield break;
        }

        // 3) 行きのドア開け地点①（choreApproachPoint_1）へ移動し、向きを合わせる。
        //    ※ doorPoint は通らない（既存廊下ルートの HallwayPoint_4 から直接ここへ向かう）。
        //    ここでは Door_Open を再生してドアを全開にする。
        yield return MoveAndFaceWaypoint(choreApproachPoint_1, choreTurnRotationSpeed, "choreApproachPoint_1");
        if (_choreRouteAborted || _routeExecutionFailed)
        {
            FinishChoreRouteAborted();
            yield break;
        }

        Debug.Log($"[MotherApproachController] 片付けルート：choreApproachPoint_1('{choreApproachPoint_1.name}')到着 — Door_Open でドアを開けます");

        // ドア操作中は歩行・足音を止める（開け終わってから再開する）。
        MovementStateChanged?.Invoke(false);

        // 4) Door_Open 再生 + ドアを全開まで開く（完了待ち）。
        //    開き終わるまで歩行の位置移動を止め、母親が通り抜けないようにする。
        //    isApproach=true（行き）: 完了後に怪しさ加算を開始する。
        yield return ChoreDoorOpenRoutine(DoorController.DoorState.Full, isApproach: true);
        if (_choreRouteAborted || _routeExecutionFailed)
        {
            FinishChoreRouteAborted();
            yield break;
        }

        // 5) 行きのドア閉め地点②（choreApproachPoint_2）へ移動し、停止して向きを合わせる。
        //    ※ ドア閉めは「リストの先頭」ではなく、この明示参照の到着時だけ行う。
        yield return MoveAndFaceWaypoint(choreApproachPoint_2, choreTurnRotationSpeed, "choreApproachPoint_2");
        if (_choreRouteAborted || _routeExecutionFailed)
        {
            FinishChoreRouteAborted();
            yield break;
        }

        // 5-2) 一時停止してドアを閉め、閉じ終わるまで待つ（モデルのアニメーションは再生しない）。
        MovementStateChanged?.Invoke(false);   // ドア閉めのため停止する
        Debug.Log($"[MotherApproachController] 片付けルート：choreApproachPoint_2('{choreApproachPoint_2.name}')到着 — ドアを閉めます");

        yield return ChoreDoorCloseRoutine();
        if (_choreRouteAborted || _routeExecutionFailed)
        {
            FinishChoreRouteAborted();
            yield break;
        }

        // 5-3) 自由な途中地点（choreApproachPoints）を登録順に通過する。
        //      null要素は警告してスキップする。ドア操作は行わない（番号から勝手に決めない）。
        yield return PassChorePoints(choreApproachPoints, "choreApproach", allowNullSkip: true);
        if (_choreRouteAborted || _routeExecutionFailed)
        {
            FinishChoreRouteAborted();
            yield break;
        }

        // 6) 片付け地点（chorePoint）へ移動し、その＋Zへ向き合わせる。
        //    片付け演技の開始は、途中地点ではなくこの chorePoint 到着時のみ。
        yield return MoveAndFaceWaypoint(chorePoint, choreTurnRotationSpeed, "chorePoint");
        if (_choreRouteAborted || _routeExecutionFailed)
        {
            FinishChoreRouteAborted();
            yield break;
        }

        // 演技（Chore）中は歩行・足音を止める（帰りの移動開始まで）。
        MovementStateChanged?.Invoke(false);

        Debug.Log("[MotherApproachController] 片付けルート：chorePointへ到着・向き合わせ完了 — onChoreArrivedを発生");
        onChoreArrived?.Invoke();

        // ── 演技待ち：外部（MotherChoreController）が演技終了を通知するまで待つ ──
        while (!_chorePerformanceFinished && !_choreRouteAborted)
            yield return null;

        if (_choreRouteAborted)
        {
            FinishChoreRouteAborted();
            yield break;
        }

        // ══ 帰り：通常の歩きで choreReturnPoint_1（ドア開け）→ choreReturnPoint_2（ドア閉め）
        //           → hallwayPassByPoint（退場） ═══════════════════
        //   ※ HallwayPoint_3 / HallwayPoint_4 / doorPoint は通らない。
        //   ※ choreReturnPoint_1 / _2 到着では片付け完了にしない（hallwayPassBy まで継続）。
        //   ※ ドアを開けるのは _1 到着時のみ、閉めるのは _2 到着時のみ。
        Debug.Log($"[MotherApproachController] 片付けルート：帰り開始 | " +
                  $"choreReturnPoint_1='{(choreReturnPoint_1 != null ? choreReturnPoint_1.name : "NULL")}' | " +
                  $"choreReturnPoint_2='{(choreReturnPoint_2 != null ? choreReturnPoint_2.name : "NULL")}'");

        MovementStateChanged?.Invoke(true);

        // 1) 帰りの途中地点（choreEndPoints）を登録順に通過する。
        //    0個の場合は choreReturnPoint_1 へ直接進む（ループが0回）。
        //    途中地点は停止・Idle待機をしない（PassThroughWaypoint／既存の通過移動処理）。
        yield return PassChorePoints(choreEndPoints, "choreEnd", allowNullSkip: true);
        if (_choreRouteAborted || _routeExecutionFailed)
        {
            FinishChoreRouteAborted();
            yield break;
        }

        // 2) 帰りの停止地点①（choreReturnPoint_1）へ移動し、停止して向きを合わせる。
        //    帰りでドアを開けるのはこの地点だけ。
        yield return MoveAndFaceWaypoint(choreReturnPoint_1, choreTurnRotationSpeed, "choreReturnPoint_1");
        if (_choreRouteAborted || _routeExecutionFailed)
        {
            FinishChoreRouteAborted();
            yield break;
        }

        MovementStateChanged?.Invoke(false); // ドア開けのため停止する

        Debug.Log("[MotherApproachController] 片付けルート：choreReturnPoint_1到着 — Door_Open でドアを開けます");

        // 3) Door_Open 再生 + ドアを fullopen（DoorState.Full = openAngle）まで開く。
        //    帰り（isApproach=false）: 怪しさ加算は開始しない。
        yield return ChoreDoorOpenRoutine(DoorController.DoorState.Full, isApproach: false);
        if (_choreRouteAborted || _routeExecutionFailed)
        {
            FinishChoreRouteAborted();
            yield break;
        }

        // 4) 帰りの停止地点②（choreReturnPoint_2）へ移動して停止・向き合わせ。
        //    ここでドアを閉める（Door_Open は再生しない）。ドア閉めはこの地点だけ。
        yield return MoveAndFaceWaypoint(choreReturnPoint_2, choreTurnRotationSpeed, "choreReturnPoint_2");
        if (_choreRouteAborted || _routeExecutionFailed)
        {
            FinishChoreRouteAborted();
            yield break;
        }

        MovementStateChanged?.Invoke(false); // ドア閉めのため停止する

        Debug.Log("[MotherApproachController] 片付けルート：choreReturnPoint_2到着 — ドアを閉めます");
        yield return ChoreDoorCloseRoutine();
        if (_choreRouteAborted || _routeExecutionFailed)
        {
            FinishChoreRouteAborted();
            yield break;
        }

        // 5) hallwayPassBy へ歩く（帰りは HallwayPoint_3 / HallwayPoint_4 / doorPoint を通らない）。
        yield return MovePositionOnly(hallwayPassByPoint);
        if (_choreRouteAborted || _routeExecutionFailed)
        {
            FinishChoreRouteAborted();
            yield break;
        }

        // 6) 既存の非表示・退場処理を使って消える（初期位置へのリセットは呼び出し側に委ねる）。
        HideMotherForReturn("Chore", "hallwayPassBy到達", success: true);

        Debug.Log("[MotherApproachController] 片付けルート：退場完了 — onChoreCompletedを発生");
        IsChoreRouteActive = false;
        _choreCoroutine = null;
        onChoreCompleted?.Invoke();
    }

    /// <summary>
    /// 片付けルートの中断後始末。位置の復帰・非表示は呼び出し側（MotherChoreController /
    /// ResetApproach）に委ね、ここでは進行状態だけを閉じる。
    /// </summary>
    private void FinishChoreRouteAborted()
    {
        Debug.Log("[MotherApproachController] 片付けルート：中断のため終了します");
        IsChoreRouteActive = false;
        _choreCoroutine = null;
    }

    /// <summary>
    /// 片付けの帰り：HallwayPoint_3（hallwayPointsAfterTurn[0]）へ歩く。
    ///  ・HallwayPoint_4 と doorPoint は通らない。
    // ──────────────────────────────────────────────────────────────────────────
    //  片付けの途中地点（行き／帰り）
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 片付けの途中地点リストを登録順に通過する。
    ///  ・0個なら何もせず戻る（次の固定地点へ直接進む）。
    ///  ・停止・Idle待機をしない（既存の通過移動処理 PassThroughWaypoint を使う）。
    ///  ・null要素は警告してスキップする（Index と件数を出す）。
    ///  ・リスト自体が null の場合も安全にスキップする。
    /// </summary>
    private IEnumerator PassChorePoints(List<Transform> points, string routeLabel, bool allowNullSkip)
    {
        if (points == null || points.Count == 0)
        {
            Debug.Log($"[MotherApproachController] 片付けルート：{routeLabel} の途中地点は0個 — 次の固定地点へ直接進みます");
            yield break;
        }

        Debug.Log($"[MotherApproachController] 片付けルート：{routeLabel} の途中地点を通過します | 登録数={points.Count}");

        int nullCount = 0;
        for (int i = 0; i < points.Count; i++)
        {
            if (_choreRouteAborted || _routeExecutionFailed) yield break;

            Transform point = points[i];

            // null要素は警告してスキップする（allowNullSkip=false なら中断）。
            if (point == null)
            {
                nullCount++;
                if (allowNullSkip)
                {
                    Debug.LogWarning($"[MotherApproachController] 片付けルート：{routeLabel}[{i}] が未設定（null）のため" +
                                     "スキップします", this);
                    continue;
                }

                Debug.LogWarning($"[MotherApproachController] 片付けルート：{routeLabel}[{i}] が未設定（null）のため" +
                                 "進行を中断します", this);
                yield break;
            }

            // 通過のみ（停止・Idle待機なし）。既存の通過移動処理を使う。
            yield return PassThroughWaypoint(point, choreTurnRotationSpeed, $"{routeLabel}[{i}]('{point.name}')");
            if (_routeExecutionFailed) yield break;
        }

        if (nullCount > 0)
            Debug.LogWarning($"[MotherApproachController] 片付けルート：{routeLabel} で未設定（null）の要素を " +
                             $"{nullCount} 件スキップしました（登録数={points.Count}）", this);
    }

    /// <summary>
    /// 片付けのドア開け（Door_Open）を MotherChoreController に依頼し、完了を待つ。
    ///  ・ドアの回転とモデルの再生を連携させるため、実処理は MotherChoreController 側が持つ。
    ///  ・未設定の場合は警告してスキップする（完了待ちが永久に続かないようにする）。
    /// </summary>
    private IEnumerator ChoreDoorOpenRoutine(DoorController.DoorState targetState, bool isApproach)
    {
        MotherChoreController controller = ResolveChoreController();
        if (controller == null)
        {
            Debug.LogWarning("[MotherApproachController] MotherChoreController が見つからないため、" +
                             "片付けの Door_Open をスキップします", this);
            _routeExecutionFailed = true;   // 成功扱いにしない（移動許可・加算へ進まない）
            yield break;
        }

        yield return controller.ChoreDoorOpenRoutine(targetState, isApproach);

        // MotherChoreController 側で再生失敗が確定した場合は経路を失敗にする
        // （呼び出し元が FinishChoreRouteAborted で移動抑止・加算・通常イベント停止を解除する）。
        if (controller.IsChoreRouteFailed)
        {
            Debug.LogWarning("[MotherApproachController] Door_Open の再生失敗を検出 — 片付けルートを中断します", this);
            _routeExecutionFailed = true;
        }
    }

    /// <summary>
    /// 片付けのドア閉めを MotherChoreController に依頼し、完了を待つ。
    /// </summary>
    private IEnumerator ChoreDoorCloseRoutine()
    {
        MotherChoreController controller = ResolveChoreController();
        if (controller == null)
        {
            Debug.LogWarning("[MotherApproachController] MotherChoreController が見つからないため、" +
                             "片付けのドア閉めをスキップします", this);
            yield break;
        }

        yield return controller.ChoreDoorCloseRoutine();
    }

    /// <summary>片付けのドア操作を担当する MotherChoreController を解決する。</summary>
    private MotherChoreController ResolveChoreController()
    {
        if (choreController == null)
            choreController = UnityEngine.Object.FindFirstObjectByType<MotherChoreController>();

        return choreController;
    }

    /// <summary>
    /// 片付けのドア開け（Door_Open）中で、歩行による位置移動を止めるべきか。
    /// MotherSuspicionSystem が持つ抑止フラグを見る（設定は MotherChoreController 側が行う）。
    /// </summary>
    private bool IsChoreMovementSuppressedNow
    {
        get
        {
            if (parentDetection == null) return false;
            return parentDetection.IsChoreMovementSuppressed;
        }
    }

    public void ResetApproach()
    {
        Debug.Log($"[MotherApproachController] ResetApproach | IsApproaching={IsApproaching}");

        if (_approachCoroutine != null)
        {
            StopCoroutine(_approachCoroutine);
            _approachCoroutine = null;
        }

        ResetStateFlags();
        // 片付け専用ルートの状態も初期化する（次のサイクルへ持ち越さない）。
        ResetChoreRoute();

        if (startPoint != null)
        {
            transform.position = OffsetGoalPosition(startPoint.position);
            transform.rotation = startPoint.rotation;
        }
        else
        {
            Debug.LogWarning("[MotherApproachController] ResetApproach: startPoint is NULL — cannot reposition.");
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  内部開始ヘルパー
    // ──────────────────────────────────────────────────────────────────────────

    private void BeginApproach(bool hallwayPassBy = false, bool gardenPassBy = false, bool gardenPeek = false,
        bool choreRoute = false)
    {
        MovementStateChanged?.Invoke(false);
        // 「このサイクルは突入（大きな音）として開始されたか」を記録する。
        // MotherApproachWarningはIsRushIn=trueを設定してから本メソッドを呼ぶため、
        // ResetStateFlags()でIsRushInが消える前にここで捕捉する（入室可否の判定に使用する）。
        _cycleStartedAsRushIn = IsRushIn;

        ResetStateFlags();

        // 接近開始時は遠い段階の音量から初期化

        // startPoint.rotationからピッチ／ロールを取得し、キャンセルされたサイクル後に
        // 実行途中の古いTransformが誤った値を引き継がないようにする。
        Vector3 startEuler = startPoint.rotation.eulerAngles;
        _fixedPitch = startEuler.x;
        _fixedRoll = startEuler.z;

        transform.position = OffsetGoalPosition(startPoint.position);
        transform.rotation = startPoint.rotation;

        ShowMotherModel();
        // グループ（A）基本：モデル表示中は顔ライト・目の発光を常時ONにする。
        // （Peekの開始／終了では消さず、HideMotherForReturn／ResetStateFlagsまで維持する）
        SetMotherStageLighting(true);
        MovementStateChanged?.Invoke(true);

        IsApproaching = true;
        onApproachStarted?.Invoke();

        Debug.Log(
            $"[MotherApproachController] BeginApproach | hallwayPassBy={hallwayPassBy} | gardenPassBy={gardenPassBy} | gardenPeek={gardenPeek} | choreRoute={choreRoute} | pitch={_fixedPitch:F1} roll={_fixedRoll:F1}");

        if (choreRoute)
        {
            // 片付け専用ルート：roomEntryPoints は使わない。
            if (_choreCoroutine != null) StopCoroutine(_choreCoroutine);
            _choreCoroutine = StartCoroutine(ChoreRoutine());
        }
        else if (gardenPeek)
            _approachCoroutine = StartCoroutine(GardenPeekRoutine());
        else if (gardenPassBy)
            _approachCoroutine = StartCoroutine(GardenPassByRoutine());
        else if (hallwayPassBy)
            _approachCoroutine = StartCoroutine(HallwayPassByRoutine());
        else
            _approachCoroutine = StartCoroutine(DoorRoutine());
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  コルーチン
    // ──────────────────────────────────────────────────────────────────────────

    private IEnumerator DoorRoutine()
    {
        _doorRoutineActive = true;
        Debug.Log("[MotherApproachController] DoorRoutine：開始");

        yield return MoveToTurnPoint();
        if (_routeExecutionFailed) yield break;

        yield return MoveAlongHallwayAfterTurn();
        if (_routeExecutionFailed) yield break;

        // 扉前フェーズ：目標音量を扉前（最大段階）に設定
        Debug.Log($"[MotherApproachController] Phase: DOOR | moving to '{doorPoint.name}' then face its forward (+Z)");
        // 【新仕様】doorPointへ移動 → 到着 → この点の＋Zへ旋回（旋回完了を待つ）。
        yield return MoveAndFaceWaypoint(doorPoint, doorTurnRotationSpeed, "doorPoint");
        if (_routeExecutionFailed) yield break;

        MovementStateChanged?.Invoke(false);
        // 到着後もグループ（A）基本の点灯を維持する（ここでの消灯はしない）。

        ReachedDoor = true;
        Debug.Log("[MotherApproachController] ドアに到着 — OnReachedDoorを発生");
        onReachedDoor?.Invoke();

        float doorPause = IsRushIn ? rushInPauseAtDoorSeconds : pauseAtDoorSeconds;
        Debug.Log($"[MotherApproachController] Door pause: {doorPause:F2}s (IsRushIn={IsRushIn})");
        yield return new WaitForSeconds(doorPause);

        StoppedAtDoor = true;
        IsApproaching = false;
        // IsInHallwayPhaseは意図的にここでは解除しない。
        // 完全なサイクル終了後、ResetApproach経由のResetStateFlags()で解除する。

        Debug.Log("[MotherApproachController] ドアで停止 — OnStoppedAtDoorを発生");
        onStoppedAtDoor?.Invoke();

        // 【Door Peek 横スライド】
        // PD側がこの直後に Peek_Door を発火して Door Peek State に入るため、
        // スライドは「実際に Peek State へ入った瞬間」から時間を数え始める
        // （BeginDoorPeekSlide 内で待ってから開始する）。
        // 通常Door Peek（入室なし・突入でない）だけに適用する。
        SlideLog("CALLSITE",
            $"roomEntryRequested={_roomEntryRequested} isRushIn={IsRushIn} " +
            $"doorRoutineActive={_doorRoutineActive}");
        if (!_roomEntryRequested && !IsRushIn)
            BeginDoorPeekSlide();
        else
            SlideLog("CALLSITE_SKIP", "入室要求またはRushInのためスライド対象外");

        // OnStoppedAtDoorの処理中にPDが入室を要求した場合のみ、部屋内部へ移動する。
        // 要求がない場合は従来どおりドア前で停止したままコルーチンを終了する。
        if (_roomEntryRequested)
        {
            _roomEntryRequested = false;
            yield return RoomPhaseCoroutine();
            // 退室後もモデル表示中はグループ（A）基本の点灯を維持する（帰路の非表示で消灯）。
            _doorRoutineActive = false;
            yield break; // 入室ルートは既存の終了処理に委ねる（帰路は通常Peek終了のみ）
        }

        // 【重要】onStoppedAtDoor の Invoke が戻った時点は「Peek開始」であって終了ではない。
        // 購読側（MotherApproachWarning → MotherSuspicionSystem.OnApproachReachedDoor）は
        // HandlePrimaryResetSequence というコルーチンを開始し、プレイヤーが寝る（または安全タイムアウト）
        // まで続く。その終了直前に MotherSuspicionSystem が RequestReturnHome() を呼ぶので、
        // ここでは「要求が来るまで」待ってから帰路を開始する（少しも早く始めない）。
        while (ReturnHomePhase != ReturnHomeState.Requested && !_returnHomeAborted())
        {
            yield return null;
        }

        if (ReturnHomePhase == ReturnHomeState.Requested)
        {
            // 受付済み → 実行中へ（PDの待機は IsReturnHomePending を見ているので途切れない）。
            ReturnHomePhase = ReturnHomeState.Running;

            // 【Door Peek 横スライド・通常終了】
            // 未実行のスライド予約を取り消し、ずれていれば現在位置から保存位置へ
            // doorPeekSlideBackDuration で滑らかに戻す。戻り切ってから帰路へ進む。
            // 既に元位置なら SlideSmoothlyTo を呼ばず即座に進む（不要な待機なし）。
            // 発見判定の解除は PD 側が担当し、この戻りで延長しない。
            yield return RestoreDoorPeekSlidePosition(doorPeekSlideBackDuration);
            ResetDoorPeekSlideState();

            // 帰路の直前：覗きの終了通知（発見判定の解除）は PD 側の ResetCycle が担当する。
            // 照明はモデル表示中（＝帰路の最終点で非表示になるまで）維持する（ここでは消灯しない）。

            // Door Peek の帰路：最終点は廊下側の画面外（hallwayPassThroughPoint）。
            yield return ReturnHomeRoutine(hallwayTurnBackPoint, hallwayGoBackPoints,
                hallwayPassThroughPoint, "DoorPeek");

            // 結果は ReturnHomeRoutine 内で Completed／Failed として確定済み（ここでは触らない）。
        }
        else
        {
            // 帰路が要求されなかった（割り込みで終了）場合も、予約を取り消して即時復帰する。
            yield return RestoreDoorPeekSlidePosition(0f, immediate: true);
            ResetDoorPeekSlideState();
        }

        _doorRoutineActive = false;
    }

    /// <summary>
    /// 帰路の待ち合わせを打ち切るべきか（ゲームオーバー・突入への切り替え・中断）。
    /// ここで true になった場合は帰路を開始しない（既存分岐を優先する）。
    /// </summary>
    private bool _returnHomeAborted()
    {
        if (IsRushIn) return true;

        MotherSuspicionSystem pd = parentDetection != null
            ? parentDetection
            : UnityEngine.Object.FindFirstObjectByType<MotherSuspicionSystem>();
        if (pd == null) return false;

        return pd.isCaught; // ゲームオーバー確定後は帰路を始めない
    }

    /// <summary>
    /// 【帰路の開始要求】MotherSuspicionSystem の通常Door Peek終了処理から呼ばれる。
    ///
    /// onStoppedAtDoor の Invoke が戻った時点は「Peek開始」であって終了ではない
    /// （購読側 HandleStoppedAtDoor → MotherSuspicionSystem.OnApproachReachedDoor が
    ///   コルーチン HandlePrimaryResetSequence を開始し、プレイヤーが寝るまで続く）。
    /// そのため帰路は「PDが通常終了と判断した時点」で、この API を通して開始する。
    ///
    /// 受理条件（それ以外は何もしない。呼び出し側は従来どおり即時 ResetApproach してよい）：
    ///   ・DoorRoutine が実行中で、まだ帰路を開始していない
    ///   ・突入（IsRushIn）ではない
    ///   ・入室ルートではない
    ///
    /// 戻り値：帰路を開始したら true（＝呼び出し側は ResetApproach を待ってよい）。
    /// </summary>
    public bool RequestReturnHome()
    {
        // 既に受付済み／実行中なら二重起動しない（既存の帰路をそのまま使う）。
        if (IsReturnHomePending)
        {
            Debug.Log($"[MotherApproachController] RequestReturnHome: 既に帰路を{ReturnHomePhase}で進行中 — 重複要求を無視");
            return true; // 待つべき帰路が存在するので true を返す（PDは待機してよい）
        }

        if (!_doorRoutineActive)
        {
            Debug.Log("[MotherApproachController] RequestReturnHome 却下：ドア停止ルートが実行中ではない");
            return false;
        }

        if (IsRushIn)
        {
            Debug.Log("[MotherApproachController] RequestReturnHome 却下：突入サイクルのため帰路を開始しない");
            return false;
        }

        if (!ShouldReturnHome())
        {
            Debug.Log("[MotherApproachController] RequestReturnHome 却下：帰路の対象外（ゲームオーバー等）");
            return false;
        }

        // 【重要】ここで同期的に Requested へ遷移させる。
        // DoorRoutine が次フレームで検知するまでの間も IsReturnHomePending が true になるため、
        // PDは「まだ受け付けただけ」を完了と誤認しない。
        ReturnHomePhase = ReturnHomeState.Requested;
        Debug.Log("[MotherApproachController] RequestReturnHome 受理（Requested）— DoorRoutineの検知を待つ");
        return true;
    }

    /// <summary>
    /// 帰路の進行状態。要求受付から完了／失敗までを1つの値で区別する。
    ///   ・Idle       : 帰路なし（開始前・リセット後）
    ///   ・Requested  : RequestReturnHome() が受理した（DoorRoutine がまだ検知していない）
    ///   ・Running    : DoorRoutine が帰路を実行中
    ///   ・Completed  : 最終点に到達しモデルを非表示にした（正常終了）
    ///   ・Failed     : 必須参照未設定・中断・タイムアウトで終了した（非表示と後始末は実施済み）
    /// </summary>
    public enum ReturnHomeState
    {
        Idle,
        Requested,
        Running,
        Completed,
        Failed
    }

    /// <summary>帰路の進行状態（読み取り専用）。</summary>
    public ReturnHomeState ReturnHomePhase { get; private set; } = ReturnHomeState.Idle;

    /// <summary>帰路が受付済み／実行中か（＝PDが完了を待つべき状態か）。</summary>
    public bool IsReturnHomePending =>
        ReturnHomePhase == ReturnHomeState.Requested || ReturnHomePhase == ReturnHomeState.Running;

    /// <summary>帰路の実行中か（次の母親イベントを開始させないための状態）。</summary>
    public bool IsReturningHome => ReturnHomePhase == ReturnHomeState.Running;

    /// <summary>
    /// 帰路の「結果」を1回だけ取り出す。取り出すと Idle に戻る（次サイクルへ結果を持ち越さない）。
    /// PD はこれを待って、Completed／Failed を見てからサイクル終了へ進む。
    /// </summary>
    public bool TryConsumeReturnHomeResult(out bool success)
    {
        switch (ReturnHomePhase)
        {
            case ReturnHomeState.Completed:
                success = true;
                ReturnHomePhase = ReturnHomeState.Idle;
                return true;
            case ReturnHomeState.Failed:
                success = false;
                ReturnHomePhase = ReturnHomeState.Idle;
                return true;
            default:
                success = false;
                return false; // まだ受付前／実行中／Idle
        }
    }

    /// <summary>帰路を開始してよいかを判定する（突入・ゲームオーバーは対象外）。</summary>
    private bool ShouldReturnHome()
    {
        if (IsRushIn) return false;

        MotherSuspicionSystem pd = parentDetection != null
            ? parentDetection
            : UnityEngine.Object.FindFirstObjectByType<MotherSuspicionSystem>();
        if (pd == null) return true;

        // ゲームオーバー（捕獲）確定後は新しい帰路を始めない。
        if (pd.isCaught) return false;

        return true;
    }

    /// <summary>
    /// 【Pass By 共通】素通りルートの「後半」を処理する。
    ///   routePoints（登録順）→ goBackPoints（登録順）→ endPoint（到着して停止）
    ///
    /// 中間点（routePoints / goBackPoints）は歩行を止めず、到着した点の＋Zを目標に
    /// 並行して旋回する（PassThroughWaypoint）。
    /// 最終点（endPoint）は位置移動のみ（旋回・Idle待機・追加の待ち時間なし）で停止する。
    ///
    /// 帰路（ReturnHomeRoutine）との違い：
    ///   ・Turn Back Point でのその場旋回をしない
    ///   ・Peek地点へ寄らない
    ///   ・Peek用の帰路要求／待ち合わせをしない
    /// Pass By で必要な「中間点の通過」だけを共有するための処理。
    ///
    /// 点の名前や番号はコードに固定しない（Listの登録順のみを使う）。
    /// null要素・同じTransformの連続登録は BuildTransformPath が除外する。
    /// List末尾が最終点と同じ場合は、二重移動や不要な中間点旋回をしないよう1要素だけ除外する。
    /// </summary>
    private IEnumerator PassByTailRoutine(List<Transform> routePoints, List<Transform> goBackPoints,
        Transform endPoint, float pauseSeconds, string routeLabel)
    {
        // 1) 往路の残り（TurnPointより後など）を登録順に通過する。
        //    ただし「最後の有効な点」では、指定秒数だけ立ち止まる（pauseSeconds > 0 のとき）。
        //    ※ 点の名前や番号は固定しない。BuildTransformPath で null・連続重複を整理した後の
        //      「最後の要素」を停止点として扱う。
        List<Transform> route = BuildTransformPath(routePoints);
        int stopIndex = (pauseSeconds > 0f && route.Count > 0) ? route.Count - 1 : -1;

        for (int i = 0; i < route.Count; i++)
        {
            if (_routeExecutionFailed) yield break;

            if (i == stopIndex)
            {
                // 停止点：到着して停止 → 旋回完了 → 指定秒数立ち止まる。
                yield return MoveAndFaceWaypoint(route[i], turnRotation, $"{routeLabel}:StopPoint[{i}]");
                if (_routeExecutionFailed) yield break;

                yield return PauseOnStopPoint(route[i], pauseSeconds, $"{routeLabel}:StopPoint[{i}]");
                if (_routeExecutionFailed) yield break;
            }
            else
            {
                // 途中の往路点は今までどおり止まらず通過する。
                yield return PassThroughWaypoint(route[i], turnRotation, $"{routeLabel}:Route[{i}]");
            }
        }

        // 2) Back Points を登録順に通過する（素通りルート追加分）。
        //    到着したら歩行と足音を戻す（停止点で止めていた場合の再開）。
        List<Transform> goBack = BuildTransformPath(goBackPoints);

        // 末尾が最終点と同じTransformなら、その要素は中間点として扱わない
        // （中間点用の旋回を開始してから、もう一度最終点処理をするのを避ける）。
        int lastIndex = goBack.Count - 1;
        if (endPoint != null && lastIndex >= 0 && goBack[lastIndex] == endPoint)
        {
            Debug.Log($"[MotherApproachController] {routeLabel}: Back Pointsの末尾が最終点 " +
                      $"'{endPoint.name}' と同じため、その要素は中間点としては扱いません（重複を回避）");
            goBack.RemoveAt(lastIndex);
        }

        for (int i = 0; i < goBack.Count; i++)
        {
            if (_routeExecutionFailed) yield break;
            yield return PassThroughWaypoint(goBack[i], turnRotation, $"{routeLabel}:Back[{i}]");
        }

        // 3) 最終点：位置移動のみ（旋回・Idle待機・追加の待ち時間なし）。
        if (endPoint == null)
        {
            Debug.LogWarning($"[MotherApproachController] {routeLabel}: 最終点が未設定のため、" +
                             "最終点への移動をスキップします。Inspectorで設定してください。", this);
            yield break;
        }

        Debug.Log($"[MotherApproachController] {routeLabel}: 最終点 '{endPoint.name}' へ進む");
        yield return MovePositionOnly(endPoint);
    }

    /// <summary>
    /// Pass By の停止点で、指定秒数だけ立ち止まる。
    ///  ・到着時点で既に停止している（位置移動は行わない）
    ///  ・向き合わせは呼び出し側が完了済み（ここでは旋回しない）
    ///  ・歩行表示を止める（Walk=false → Standing/Idle へ）
    ///  ・足音を止める
    ///  ・待機後に歩行と足音を戻す（次の Back Points へ進むため）
    ///
    /// 待機時間はゲーム内時間（Time.timeScale の影響を受ける）。ポーズ中は進まない。
    /// 経路中断（タイムアウト／ゲームオーバー等）が起きた場合は、待機を打ち切って
    /// 歩行を再開しない（古い経路を再開させない）。
    /// </summary>
    private IEnumerator PauseOnStopPoint(Transform stopPoint, float seconds, string label)
    {
        float pause = Mathf.Max(0f, seconds);
        if (pause <= 0f) yield break;

        Debug.Log($"[MotherApproachController] {label} '{stopPoint?.name}' で {pause:F2}s 立ち止まります");

        // 歩行表示と足音を止める（Standing/Idle になる。Body Orientation 設定はそのまま）。
        MovementStateChanged?.Invoke(false);

        // 待機（ゲーム内時間）。中断されたら即座に抜けて再開しない。
        float elapsed = 0f;
        while (elapsed < pause)
        {
            if (IsReturnHomeAborted || _routeExecutionFailed)
            {
                Debug.Log($"[MotherApproachController] {label}: 待機中に経路が中断されたため、待機を打ち切ります");
                yield break;
            }

            elapsed += Time.deltaTime; // timeScale の影響を受けるゲーム内時間
            yield return null;
        }

        // 中断されていなければ、歩行と足音を再開して Back Points へ進む。
        if (IsReturnHomeAborted || _routeExecutionFailed) yield break;

        Debug.Log($"[MotherApproachController] {label} 立ち止まり終了 — 歩行を再開します");
        MovementStateChanged?.Invoke(true);
    }

    /// <summary>
    /// フェイントA（HallwayPassBy）の移動ルーチン：
    ///   startPoint → hallwayPointsBeforeTurn[] → turnPoint（旋回）
    ///   → hallwayPointsAfterTurn[]（登録順に通過）
    ///   → hallwayGoBackPoints[]（登録順に通過）
    ///   → hallwayPassByPoint（最終点。到着して停止）
    ///
    /// 【重要】doorPoint は経由しない（Door側の覗き地点へ寄らない）。
    /// onReachedDoor／onStoppedAtDoorは一切発生させないため、MotherSuspicionSystemの
    /// ドア分岐（primary=固定でドア全開になる経路）には到達しない。
    /// 到達後に既存のonPassedByDoorを発生させ、
    /// PD.OnApproachPassedBy → ResetCycle → EndWarningSequence → ResetApproach（StartPointへ即時復帰）
    /// の既存フローで怪しさ・捕獲なしのままサイクルを終える。
    /// </summary>
    private IEnumerator HallwayPassByRoutine()
    {
        Debug.Log("[MotherApproachController] HallwayPassByRoutine（フェイントA：ドア前を停止せず通り過ぎる）：開始");

        // TurnPointが未設定ならMoveToTurnPointが警告を出して中断するため、ここでも安全に終了する。
        yield return MoveToTurnPoint();
        if (_routeExecutionFailed) yield break;

        // 往路の残り（hallwayPointsAfterTurn）→ 最後の点で立ち止まり → Back Points → hallwayPassByPoint。
        // ※ doorPoint は通らない。Turn Back Point も使わない（それはPeek後の帰路専用）。
        yield return PassByTailRoutine(hallwayPointsAfterTurn, hallwayGoBackPoints,
            hallwayPassByPoint, hallwayPassByPauseSeconds, "HallwayPassBy");
        if (_routeExecutionFailed) yield break;

        PassedByDoor = true;
        IsApproaching = false;
        // IsInHallwayPhaseは既存DoorRoutineと同じくResetStateFlags()でのみ解除する。

        Debug.Log("[MotherApproachController] フェイントA完了 — 画面外で停止しOnPassedByDoorを発生");
        onPassedByDoor?.Invoke();
    }

    private IEnumerator GardenPassByRoutine()
    {
        Debug.Log("[MotherApproachController] GardenPassByRoutine（庭側素通り）：開始");

        // TurnPointが未設定なら MoveToTurnPoint が警告を出して中断するため、ここで安全に終了する。
        yield return MoveToTurnPoint();
        if (_routeExecutionFailed || turnPoint == null) yield break;

        // TurnPointから庭側経路を分岐し、庭側のウェイポイントを設定順に進む。
        // 往路の残り（gardenRoutePoints）→ 最後の点で立ち止まり → Back Points → gardenPassByPoint。
        // ※ gardenPeekPoint は通らない（Window Peek の覗き地点へ寄らない）。
        // ※ Turn Back Point も使わない（それはPeek後の帰路専用）。
        yield return PassByTailRoutine(new List<Transform>(gardenRoutePoints), gardenGoBackPoints,
            gardenPassByPoint, gardenPassByPauseSeconds, "GardenPassBy");
        if (_routeExecutionFailed) yield break;

        MovementStateChanged?.Invoke(false);
        PassedByDoor = true;
        IsApproaching = false;

        Debug.Log("[MotherApproachController] 庭側素通り完了 — OnPassedByDoorを発生");
        onPassedByDoor?.Invoke();
    }

    /// <summary>
    /// 庭側覗きの移動ルーチン：
    ///   GardenPassByと同じ経路（TurnPoint → gardenRoutePoints[] → GardenPeekPoint）で進む。
    ///   【新仕様】各点へ移動 → 到着 → その点の＋Zへ旋回。移動中は向きを変えない。
    ///   GardenPeekPointでは到着して＋Zへ旋回し終えてから Peek_Windows を再生する。
    ///   覗き時間は覗き開始時に一度だけ決定された_gardenPeekDurationを使用する（覗き中のゲージ変化では延長しない）。
    ///   時間経過後はGardenPassByPointまで進み、そこで既存のonPassedByDoorを一度だけ発生させる。
    ///   終了処理（警告終了・全灯消灯・StartPoint復帰）はPWS.HandlePassedByDoor →
    ///   PD.OnApproachPassedBy → ResetCycle + EndWarningSequence の既存経路で行う
    ///   （ドア開閉・疑惑加算・捕獲判定には到達しない）。
    /// </summary>
    private IEnumerator GardenPeekRoutine()
    {
        Debug.Log("[MotherApproachController] GardenPeekRoutine（庭側覗き）：開始");

        // 5キーと同じ経路：MoveToTurnPoint → gardenRoutePointsを設定順に進む。
        // 移動中は進行方向を向く。TurnPointより後の廊下ウェイポイント／doorPointはドア側の経由点のため、庭ルートでは通らない。
        yield return MoveToTurnPoint();
        if (_routeExecutionFailed || turnPoint == null) yield break;

        // 【新仕様】各点へ移動 → 到着 → その点の＋Zへ旋回（Hallwayと同じルール）。
        for (int i = 0; i < gardenRoutePoints.Length; i++)
        {
            // 庭の中間点も止まらずに通過する。
            yield return PassThroughWaypoint(gardenRoutePoints[i], turnRotation, $"gardenRoutePoints[{i}]");
            if (_routeExecutionFailed) yield break;
        }

        // GardenPeekPointへ到着し、この点の＋Zへ旋回し終えてから覗きを再生する。
        // 旋回は MoveAndFaceWaypoint が完了まで待つため、覗き開始時に移動は残っていない。
        yield return MoveAndFaceWaypoint(gardenPeekPoint, doorTurnRotationSpeed, "gardenPeekPoint");
        if (_routeExecutionFailed) yield break;

        // グループ（B）追加：庭Peek中だけ顔ライトも点灯する（グループ（A）基本は維持したまま）。
        SetPeekLighting(true);

        // window peek は Idle からのみ到達できる遷移のため、
        // 実際に Idle State へ到達していることを確認してから Trigger を発火する。
        IdleWaitResult peekIdleResult = default;
        yield return WaitForIdleState("覗き開始前", gardenPeekPoint?.name, r => peekIdleResult = r);

        if (!peekIdleResult.Reached)
        {
            // Idleへ到達できなかった → 覗きを要求せず、安全に中断する（ライト・覗きフラグを残さない）。
            HandleIdleWaitFailure("WindowPeek");
            yield break;
        }

        // 窓覗きのアニメーションを再生する。
        // 実際のController（mother_animation_controller）のTrigger名 Peek_Windows に合わせて発火する。
        TriggerPeekAnimation(PeekWindowsParameter);

        // 覗き時間の基本値は MotherApproachWarning の「庭覗き時間」を参照する（重複保持しない）。
        // 覗き時間は「GardenPeekPoint到着時のゲージ値」を一度だけ取得して決定する（覗き中のゲージ変化では延長しない）。
        if (motherGauge == null)
            motherGauge = UnityEngine.Object.FindFirstObjectByType<MotherGauge>();
        int gaugeAtPeekStart = (motherGauge != null) ? motherGauge.currentGauge : 0;

        float peekBaseSeconds = ResolveGardenPeekDurationBase();
        _gardenPeekDuration = Mathf.Max(0f, peekBaseSeconds) + gaugeAtPeekStart;
        Debug.Log(
            $"[MotherApproachController]   GardenPeekPointで覗き | {_gardenPeekDuration:F1}s (base={peekBaseSeconds:F1} + gauge={gaugeAtPeekStart})");

        // 覗き待機開始：庭覗き中の継続疑惑（PD）に開始を通知する。
        _isGardenPeeking = true;
        onGardenPeekStarted?.Invoke();

        yield return new WaitForSeconds(_gardenPeekDuration);

        // 覗き待機終了：継続疑惑が次tick以内に確実に停止するよう、フラグを先に解除する。
        _isGardenPeeking = false;
        // グループ（B）追加照明の終了。グループ（A）がONなら点灯は維持され、
        // 顔ライトもここでは消えない（帰路の非表示でまとめて消灯する）。
        SetPeekLighting(false);

        // 覗き終了：
        // 【帰路】Garden Turn Back Pointで帰る向きへその場旋回し、
        // 帰路Listを通って最終点（gardenPassByPoint）へ到達してからモデルを非表示にする。
        // 廊下側（hallwayPassThroughPoint）へは進まない。
        Debug.Log("[MotherApproachController]   覗き終了 — 帰路（Garden Turn Back）へ");
        // 帰路Listは null でも空でも同じ扱い（Turn Back旋回後、最終点へ直接向かう）。
        // Window Peek の帰路：最終点は gardenPassByPoint（庭側の到達点）。
        // 到着したらそこで非表示にし、廊下側（hallwayPassThroughPoint）へは進まない。
        yield return ReturnHomeRoutine(gardenTurnBackPoint, gardenGoBackPoints,
            gardenPassByPoint, "WindowPeek");

        PassedByDoor = true;
        IsApproaching = false;

        // 帰路完了後に一度だけ通知する（二重通知なし）。
        // PWS.HandlePassedByDoor → PD.OnApproachPassedBy → ResetCycle + EndWarningSequence
        // （全灯消灯・isWarningActive解除・ResetApproachでStartPointへ復帰）が既存経路で実行される。
        // モデルは既に非表示のため、ResetApproach で初期位置へ戻しても画面内で瞬間移動しない。
        Debug.Log("[MotherApproachController] 庭側覗き完了（帰路済み） — OnPassedByDoorを発生");
        onPassedByDoor?.Invoke();
    }

    /// <summary>
    /// roomEntryPointsのうち有効（null以外）な要素の数を返す。
    /// </summary>
    private int CountValidRoomEntryPoints()
    {
        if (roomEntryPoints == null) return 0;

        int count = 0;
        for (int i = 0; i < roomEntryPoints.Length; i++)
        {
            if (roomEntryPoints[i] != null) count++;
        }

        return count;
    }

    /// <summary>
    /// ドア停止後、親機を部屋内部へ移動させ、退室要求（または安全タイムアウト）まで部屋に留まらせる。
    /// 【新仕様】各roomEntryPointsへ「位置移動 → 到着 → その点の＋Zへ旋回」を順に行う。
    /// 退室開始時のみ、対応する向き指定Waypointが無いため固定角度 roomExitYaw を使う（要確認）。
    /// 入室完了でonEnteredRoom、doorPointへ戻った時点でonExitedRoomを発生する。
    /// </summary>
    private IEnumerator RoomPhaseCoroutine()
    {
        _roomPhaseActive = true;

        int entryCount = CountValidRoomEntryPoints();
        Debug.Log(
            $"[MotherApproachController] RoomPhase：入室開始 | 有効なroomEntryPoints={entryCount} | yaw={transform.rotation.eulerAngles.y:F1}");

        if (entryCount > 0)
        {
            // 部屋内部へ入る（doorPointから近い順に設定されたウェイポイントを順に進む）。
            // 【新仕様】各点へ移動 → 到着 → その点の＋Zへ旋回。
            foreach (Transform entryPoint in roomEntryPoints)
            {
                yield return MoveAndFaceWaypoint(entryPoint, doorTurnRotationSpeed, "roomEntryPoints");
                if (_routeExecutionFailed) yield break;
            }
        }
        else
        {
            // 有効なウェイポイントがない場合はその場（doorPoint）を部屋内部とみなす。
            // onEnteredRoomは必ず発生させ、呼び出し側が待ち続けないようにする。
            Debug.LogWarning("[MotherApproachController] RoomPhase：有効なroomEntryPointsがないため、doorPointで入室完了とする。", this);
        }

        Debug.Log("[MotherApproachController] 入室完了 — OnEnteredRoomを発生");
        onEnteredRoom?.Invoke();

        // 退室要求（RequestLeaveRoom）または安全タイムアウトまで部屋に留まる。
        float timeout = Mathf.Max(0f, roomStayTimeoutSeconds);
        float elapsed = 0f;
        while (!_leaveRoomRequested)
        {
            if (timeout > 0f && elapsed >= timeout)
            {
                Debug.Log($"[MotherApproachController] 部屋滞在が安全タイムアウト（{timeout:F1}s）に達した — 自動的に退室する");
                break;
            }

            elapsed += Time.deltaTime;
            yield return null;
        }

        _leaveRoomRequested = false;

        // 部屋から出る：退室の向きへ回転してから、入室時と逆順でdoorPointへ戻る。
        //
        // 【新仕様との関係・要確認】
        // 退室時の向きは、対応する「向きを指定するWaypoint」が現在存在しないため、
        // 既存の固定角度 roomExitYaw を使い続けています（今回この値は変更していません）。
        // Waypointの＋Zで指定したい場合は、退室用の向き指定Transformを新設して
        // Inspectorで割り当てる必要があります（勝手にシーンへ追加していません）。
        Debug.Log($"[MotherApproachController] RoomPhase：退室開始 | yaw={roomExitYaw:F1}（固定角度。対応Waypoint未設定）");
        yield return RotateToYaw(roomExitYaw, doorTurnRotationSpeed);
        if (_routeExecutionFailed) yield break;

        // 退室経路：入室時と逆順に各点へ移動 → 到着 → その点の＋Zへ旋回。
        for (int i = roomEntryPoints.Length - 1; i >= 0; i--)
        {
            yield return MoveAndFaceWaypoint(roomEntryPoints[i], doorTurnRotationSpeed, $"roomExit[{i}]");
            if (_routeExecutionFailed) yield break;
        }

        // doorPointへ戻り、その点の＋Zへ旋回する（＝ドア前の既定の向きに復帰）。
        yield return MoveAndFaceWaypoint(doorPoint, doorTurnRotationSpeed, "doorPoint（退室）");
        if (_routeExecutionFailed) yield break;

        _roomPhaseActive = false;
        Debug.Log("[MotherApproachController] 退室完了 — OnExitedRoomを発生");
        onExitedRoom?.Invoke();
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  共通フェーズヘルパー
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 必須Waypoint未設定など、経路が成立しないときに親機の進行を安全に中断する。
    ///  ・移動中のフラグを解除（足音が止まる／音量が遠景へ戻る）
    ///  ・進行中の旋回コルーチンを停止（回転指示を残さない）
    ///  ・目の発光・窓覗きライトを消灯
    /// 「到着」「覗き成功」とは扱わないため、到達フラグ（ReachedDoor／StoppedAtDoor／PassedByDoor）や
    /// UnityEvent（onReachedDoor／onStoppedAtDoor／onPassedByDoor）は一切発生させない。
    /// サイクルの後始末（疑惑解除・警告終了・StartPoint復帰）は既存どおり
    /// ResetCycle → EndWarningSequence → ResetApproach に委ねる。
    /// </summary>
    private void AbortApproach()
    {
        _routeExecutionFailed = true;
        IsApproaching = false;
        IsInHallwayPhase = false;
        _isGardenPeeking = false;

        StopRotateCoroutine();
        _doorRoutineActive = false;
        _roomEntryRequested = false;

        // 進行を中断するため、スライドの予約を取り消す（位置の復帰は非表示側が担当）。
        ResetDoorPeekSlideState();

        // 進行を中断するため、グループ（A)(B）の照明をここで消灯する。
        SetPeekLighting(false);
        SetMotherStageLighting(false);

        // 足音などの移動演出を止める（MotherSuspicionSystemが購読している）。
        MovementStateChanged?.Invoke(false);
    }

    /// <summary>
    /// 廊下フェーズ（往路）：
    ///   startPoint → hallwayPointsBeforeTurn[] → turnPoint
    /// 【新仕様】各Waypointへ「位置移動 → 到着 → そのWaypointの＋Zへ旋回」を順に行う。
    /// 移動中は向きを変えない。旋回が完了してから次のWaypointへ進む。
    /// turnPoint が未設定の場合は警告を出して廊下フェーズを安全に中断する（無限待機しない）。
    /// </summary>
    private IEnumerator MoveToTurnPoint()
    {
        IsInHallwayPhase = true;
        // 中間段階（廊下）：turnPointで方向転換するまでは中間音量を維持する
        Debug.Log("[MotherApproachController] フェーズ：廊下 | IsInHallwayPhase=true");

        // TurnPointが未設定なら、その経路は成立しないため警告して安全に中断する。
        if (turnPoint == null)
        {
            Debug.LogError("[MotherApproachController] turnPointが未設定のため廊下ルートを開始できません。" +
                           "この経路はTurnPointが必須です。SceneでTurnPointのTransformを割り当ててください。", this);
            AbortApproach();
            yield break;
        }

        // TurnPointより前は中間点：止まらず通過し、到着した点の＋Zを次区間と並行して向く。
        List<Transform> beforeTurn = BuildHallwayPath(hallwayPointsBeforeTurn);
        for (int i = 0; i < beforeTurn.Count; i++)
        {
            yield return PassThroughWaypoint(beforeTurn[i], turnRotation, $"hallwayBeforeTurn[{i}]");
            if (_routeExecutionFailed) yield break;
        }

        // TurnPoint：ここは方向転換の要所なので、到着して＋Zへ旋回完了まで待つ。
        yield return MoveAndFaceWaypoint(turnPoint, turnRotation, "turnPoint");
        if (_routeExecutionFailed) yield break;
    }

    /// <summary>
    /// TurnPointより後の廊下ウェイポイントを登録順に進む（ドア確認ルート／フェイントA用）。
    /// 【新仕様】各点へ移動 → 到着 → その点の＋Zへ旋回。
    /// 未設定／空なら何もせずスキップする。
    /// </summary>
    private IEnumerator MoveAlongHallwayAfterTurn()
    {
        // 中間点なので止まらずに通過する（停止するのはdoorPointだけ）。
        List<Transform> afterTurn = BuildHallwayPath(hallwayPointsAfterTurn);
        for (int i = 0; i < afterTurn.Count; i++)
        {
            yield return PassThroughWaypoint(afterTurn[i], turnRotation, $"hallwayAfterTurn[{i}]");
            if (_routeExecutionFailed) yield break;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  帰路（Door Peek / Window Peek 終了後）
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 通常のPeek終了後の帰路。
    ///   Turn Back Point（その場旋回・位置移動なし）
    ///   → GoBackPoints（登録順に通過。中間点なので止まらない）
    ///   → PassThroughPoint（画面外の最終点）
    ///   → モデル非表示 → onReturnedHome
    ///
    /// 停止地点（Peek点）と違い、Turn Back Point 以降は歩行を止めずに戻る。
    /// 旋回は Turn Back Point だけ「完了を待つ」（帰る向きを確定させてから歩き出すため）。
    ///
    /// 最終点（endPoint）は引数で受け取る：
    ///   ・Door Peek側  = hallwayPassThroughPoint（廊下の画面外）
    ///   ・Window Peek側 = gardenPassByPoint（庭側の到達点）
    /// 既存参照を使うため、新しいInspectorフィールドは追加しない。
    ///
    /// 必須参照（turnBackPoint / endPoint / goBackPoints）が無い場合は警告を出し、
    /// 画面内で瞬間移動せず、安全に非表示にして終了する。
    /// </summary>
    private IEnumerator ReturnHomeRoutine(Transform turnBackPoint, List<Transform> goBackPoints,
        Transform endPoint, string routeLabel)
    {
        Debug.Log($"[MotherApproachController] ReturnHome（帰路）：開始 [{routeLabel}] " +
                  $"endPoint={(endPoint != null ? endPoint.name : "null")}");

        // 必須参照の確認。Turn Back Point と 最終点 は帰路に必須。
        if (turnBackPoint == null)
        {
            Debug.LogWarning($"[MotherApproachController] {routeLabel}: Turn Back Point が" +
                             "未設定のため帰路を開始できません。Inspectorで設定してください。", this);
            HideMotherForReturn(routeLabel, "TurnBackPoint未設定", success: false);
            yield break;
        }

        if (endPoint == null)
        {
            Debug.LogWarning($"[MotherApproachController] {routeLabel}: 帰路の最終点（endPoint）が" +
                             "未設定のため帰路を開始できません。Inspectorで設定してください。", this);
            HideMotherForReturn(routeLabel, "最終点未設定", success: false);
            yield break;
        }

        // 1) Turn Back Point：位置移動なし。その場で＋Zへ旋回完了してから帰路の歩行を始める。
        //    位置が現在地と離れている場合は設定ミスの可能性が高いので警告する（勝手に移動はしない）。
        float offsetFromCurrent = Vector3.Distance(
            new Vector3(transform.position.x, 0f, transform.position.z),
            new Vector3(turnBackPoint.position.x, 0f, turnBackPoint.position.z));
        if (offsetFromCurrent > 0.5f)
        {
            Debug.LogWarning($"[MotherApproachController] {routeLabel}: Turn Back Point '{turnBackPoint.name}' は" +
                             $"回転専用の地点ですが、現在地から水平距離 {offsetFromCurrent:F2} 離れています。" +
                             "（Peek点と同じ位置に置く想定）Inspectorの配置を確認してください。位置移動は行いません。", this);
        }

        // 旋回完了まで待つ（帰る向きを確定させる）。
        yield return RotateToWaypointForward(turnBackPoint, turnRotation, $"{routeLabel}:TurnBack");
        if (_routeExecutionFailed) yield break;

        // 2) GoBackPoints：登録順に通過（中間点なので止まらない・Idle待機なし）。
        //    gardenGoBackPoints は「中間点だけ」を登録するListなので、
        //    最終点（endPoint）と同じ点が末尾に登録されていても、ここでは通過扱いにせず
        //    最終点処理（旋回なしの移動＋非表示）へ委ねる。＝末尾の重複を安全に扱う。
        List<Transform> goBack = BuildTransformPath(goBackPoints);

        // 末尾が最終点と同じTransformなら、その要素は中間点として扱わない
        // （中間点用の旋回を開始してから、もう一度最終点処理をするのを避ける）。
        int lastIndex = goBack.Count - 1;
        if (lastIndex >= 0 && goBack[lastIndex] == endPoint)
        {
            Debug.Log($"[MotherApproachController] {routeLabel}: 帰路Listの末尾が最終点 " +
                      $"'{endPoint.name}' と同じため、その要素は中間点としては扱いません（重複を回避）");
            goBack.RemoveAt(lastIndex);
        }

        for (int i = 0; i < goBack.Count; i++)
        {
            // 中断（タイムアウト／ゲームオーバー）が確定していたら、それ以上歩かせない。
            if (IsReturnHomeAborted)
            {
                Debug.Log("[MotherApproachController] 帰路は中断済みのため、以降の移動を中止します");
                yield break;
            }

            yield return PassThroughWaypoint(goBack[i], turnRotation, $"{routeLabel}:GoBack[{i}]");
            if (_routeExecutionFailed) yield break;
        }

        // 3) 最終点（endPoint）：ここは「通過・消失用」。
        //    到着 → 位置移動終了 → 移動/足音終了 → モデル非表示 の順で、
        //    向き合わせ（＋Z旋回）もIdle到達待機も行わない。
        if (IsReturnHomeAborted)
        {
            Debug.Log("[MotherApproachController] 帰路は中断済みのため、最終点への移動を中止します");
            yield break;
        }

        yield return MovePositionOnly(endPoint);
        if (_routeExecutionFailed) yield break;

        // 中断されていたら、非表示後の Completed 通知は行わない（中断で確定済み）。
        if (IsReturnHomeAborted)
        {
            Debug.Log("[MotherApproachController] 帰路は中断済みのため、最終点到達後の完了通知を行いません");
            yield break;
        }

        // 4) 到着してからモデルを非表示にする（不要な待ち時間を入れない）。
        HideMotherForReturn(routeLabel, $"最終点到達({endPoint.name})", success: true);
    }

    /// <summary>
    /// 帰路の最後：旋回・歩行・足音・演出状態をすべて解除してからモデルを非表示にする。
    /// 画面内で瞬間移動しない（初期位置へのリセットは既存の ResetApproach に委ねる）。
    /// success=false のときは「失敗／中断」として確定する（非表示と後始末は同じく実施する）。
    /// </summary>
    private void HideMotherForReturn(string routeLabel, string reason, bool success)
    {
        // 回転の指示元を残さない。
        StopRotateCoroutine();

        // 歩行・足音を停止する。
        MovementStateChanged?.Invoke(false);

        // 目の発光・顔ライトを消灯する（モデル非表示と同時にグループ（A)(B）ともOFF）。
        SetPeekLighting(false);
        SetMotherStageLighting(false);

        // 覗き状態を解除する（戻っているだけなのに発見し続けない）。
        _isGardenPeeking = false;

        // 【Door Peek 横スライド】先に非表示にしてから、ずれを解除する。
        // 見えている状態で元位置へスナップさせない（非表示後に座標を戻す）。
        // 戻りアニメーションは待たない（ゲームオーバー・強制中断を遅らせない）。
        StopDoorPeekSlideCoroutine();

        // モデルを非表示にする。
        if (motherModelRoot != null)
        {
            motherModelRoot.SetActive(false);
            Debug.Log($"[MotherApproachController] 母親モデルを非表示にしました（{routeLabel}:{reason}）");
        }

        if (motherModelRenderers != null)
        {
            foreach (var r in motherModelRenderers)
            {
                if (r != null) r.enabled = false;
            }
        }

        // 非表示にした後で、スライドによる位置ずれを解除する（古いずれを次サイクルへ残さない）。
        ApplyHorizontalPosition(_peekSlideOriginPosition);
        ResetDoorPeekSlideState();

        IsApproaching = false;
        IsInHallwayPhase = false;
        _doorRoutineActive = false;

        // 帰路の結果を確定する（PDはここを見てサイクル終了へ進む）。
        // 既に失敗確定している場合は上書きしない。
        if (ReturnHomePhase == ReturnHomeState.Failed)
        {
            Debug.LogWarning($"[MotherApproachController] 帰路は失敗として確定済み（{routeLabel}:{reason}）");
        }
        else
        {
            ReturnHomePhase = success ? ReturnHomeState.Completed : ReturnHomeState.Failed;
        }

        Debug.Log($"[MotherApproachController] 帰路確定 — {ReturnHomePhase}（{routeLabel}:{reason}）");
        onReturnedHome?.Invoke();
    }

    /// <summary>
    /// 【タイムアウト／強制中断用】帰路を失敗として確定し、移動・旋回を止めて非表示と後始末を行う。
    /// PDが returnHomeSafetyTimeout を超えた場合や、ゲームオーバーで打ち切る場合に呼ぶ。
    /// 画面内で初期位置へ瞬間移動しない（リセットは呼び出し側が非表示後に実行する）。
    /// </summary>
    public void AbortReturnHome(string reason)
    {
        if (!IsReturnHomePending) return; // 帰路が進行していなければ何もしない

        Debug.LogWarning($"[MotherApproachController] 帰路を中断します（{reason}） — 停止・非表示・後始末を実施");

        // 中断フラグを先に立てる。位置移動ループ・旋回ループはこれを見て次フレームで抜ける。
        // （結果の取り出しで ReturnHomePhase が Idle に戻っても、このフラグは次サイクルまで残る）
        _returnHomeAbortFlag = true;

        HideMotherForReturn("Abort", reason, success: false);
    }

    /// <summary>
    /// 【新仕様の共通処理】対象Waypointへ位置移動し、到着してから「そのWaypointの＋Z」へ旋回する。
    ///   1. 位置のみ移動（MovePositionOnly）
    ///   2. 到着判定
    ///   3. 到着したWaypointの＋Z方向へ旋回
    ///   4. 旋回完了
    /// 移動中は向きを変えず、次の移動先への位置ベクトルから向きを計算しない。
    /// HallwayとGardenで同じルールを使う（経路ごとの分岐なし）。
    /// 同じWaypointで旋回が二重に走らないよう、旋回はこの関数の中だけで実行する。
    /// </summary>
    private IEnumerator MoveAndFaceWaypoint(Transform waypoint, float turnSpeed, string routeLabel)
    {
        if (waypoint == null) yield break; // null要素はスキップ

        // 1-2. 位置のみ移動（向きは変えない）
        yield return MovePositionOnly(waypoint);
        if (_routeExecutionFailed) yield break;

        // 3-4. 到着したWaypointの＋Zへ旋回し、完了を待つ
        yield return RotateToWaypointForward(waypoint, turnSpeed, routeLabel);
    }

    /// <summary>
    /// 【中間点用】対象Waypointを「止まらずに通過」しながら、そのWaypointの＋Zへ向きを寄せる。
    ///
    /// 停止地点（Door Peek点など）と区別するための処理で、以下が異なる：
    ///   ・歩行を止めない（MovementStateChanged(false) を出さない）
    ///   ・Idle待機を挟まない
    ///   ・旋回完了を待たずに次の区間へ進む（旋回は次区間の移動と並行して継続する）
    ///
    /// 向きの基準は到着したWaypointの＋Zのみ（次の移動先の位置ベクトルからは計算しない）。
    /// 向きの指示元は常に1つ：新しい目標を設定するときに古い旋回コルーチンを停止してから開始する。
    /// </summary>
    private IEnumerator PassThroughWaypoint(Transform waypoint, float turnSpeed, string routeLabel)
    {
        if (waypoint == null) yield break;

        // 位置のみ移動。到着しても歩行は止めない（Idle待機も挟まない）。
        yield return MovePositionOnlyForPassThrough(waypoint);
        if (_routeExecutionFailed) yield break;

        // 到着したWaypointの＋Zを、次の区間と並行して向く（完了は待たない）。
        StartRotateTowardsWaypoint(waypoint, turnSpeed, routeLabel);
    }

    /// <summary>
    /// Waypointの＋Zへ向けた旋回コルーチンを開始する（完了は待たない）。
    /// 既に旋回中の場合は古い目標を破棄して新しい目標へ差し替える（指示元を1つに保つ）。
    /// </summary>
    private void StartRotateTowardsWaypoint(Transform waypoint, float turnSpeed, string routeLabel)
    {
        if (waypoint == null) return;
        if (!TryGetTargetYawFromForward(waypoint, out float targetYaw, routeLabel)) return;

        // 古い目標への旋回を残さない。
        StopRotateCoroutine();
        _rotateCoroutine = StartCoroutine(RotateToYaw(targetYaw, turnSpeed));
    }

    /// <summary>
    /// Waypointの＋Z（親の回転を考慮したワールド空間の前方向）へ旋回する。
    /// 水平面へ投影して母親を傾けない。waypoint.rightや固定角度補正は使わない。
    /// 不正な向き（水平成分ほぼゼロ）は警告して現在の向きを維持する。
    /// </summary>
    private IEnumerator RotateToWaypointForward(Transform waypoint, float turnSpeed, string routeLabel)
    {
        if (waypoint == null) yield break;
        if (!TryGetTargetYawFromForward(waypoint, out float targetYaw, routeLabel)) yield break;

        yield return RotateToYaw(targetYaw, turnSpeed);
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  移動／回転ヘルパー
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 目標位置へmotherHeightOffset（ワールドY）を1回だけ加算した到達点を返す。
    /// 常にwaypoint（またはstartPoint）の生座標から再計算するため、フレーム間・waypoint間で
    /// オフセットは累積しない。X/Zは元のwaypoint座標をそのまま使う。
    /// </summary>
    private Vector3 OffsetGoalPosition(Vector3 targetPosition)
    {
        return new Vector3(targetPosition.x, targetPosition.y + motherHeightOffset, targetPosition.z);
    }

    /// <summary>
    /// 【中間点の通過用】位置のみ更新し、到着しても歩行を止めない移動ヘルパー。
    /// MovePositionOnly との違いは「到着時に MovementStateChanged(false) を出さない」点だけ。
    /// これにより、中間点ごとに足音と歩行表示が途切れない。
    /// </summary>
    private IEnumerator MovePositionOnlyForPassThrough(Transform target)
    {
        if (target == null) yield break;

        Vector3 goal = OffsetGoalPosition(target.position);

        // 歩行中であることを通知（既に true でも問題ない）。
        MovementStateChanged?.Invoke(true);

        while (Vector3.Distance(transform.position, goal) > stopDistance)
        {
            // 帰路が中断されたら、通過中でもその場で位置更新を止める。
            if (IsReturnHomeAborted) yield break;

            // 片付けのドア開け（Door_Open）中は位置移動を止める。
            if (IsChoreMovementSuppressedNow)
            {
                yield return null;
                continue;
            }

            transform.position = Vector3.MoveTowards(
                transform.position, goal, CurrentApproachSpeed * Time.deltaTime);
            yield return null;
        }

        transform.position = goal;

        UpdateRouteState(target);
        // ここで false を出さない（歩行を継続する）。
    }

    /// <summary>
    /// 位置のみを更新する移動ヘルパー。向きは一切変更しない。
    ///  ・進行方向へ向く処理は行わない
    ///  ・次の移動先をLookAtしない
    ///  ・位置差からAtan2で向きを決めない
    ///  ・回転コルーチンを移動中に開始しない
    ///  ・移動開始時に次のWaypointへ先回りして向かせない
    /// 到着後の旋回は呼び出し側（MoveAndFaceWaypoint）が担当する。
    ///
    /// 移動中は MovementStateChanged(true) を通知する（既存の歩行状態＝足音用）。
    /// 到着後は false を通知するので、その場旋回中は「位置移動中」として扱われない。
    /// </summary>
    private IEnumerator MovePositionOnly(Transform target)
    {
        if (target == null)
        {
            // 中間ウェイポイントが未設定の場合はスキップして続行する（null安全）。
            yield break;
        }

        // 到達点は「waypoint位置＋motherHeightOffset」を1回だけ計算する。
        // 現在位置へオフセットを加算しないため、フレーム間・waypoint間で高さが累積しない。
        Vector3 goal = OffsetGoalPosition(target.position);

        if (Vector3.Distance(transform.position, goal) > stopDistance)
            MovementStateChanged?.Invoke(true);

        while (Vector3.Distance(transform.position, goal) > stopDistance)
        {
            // 帰路が中断（タイムアウト／ゲームオーバー）されたら、その場で位置更新を止める。
            if (IsReturnHomeAborted) yield break;

            // 片付けのドア開け（Door_Open）中は位置移動を止める
            // （開け終わる前に母親が通り抜けないようにする）。
            if (IsChoreMovementSuppressedNow)
            {
                yield return null;
                continue;
            }

            transform.position = Vector3.MoveTowards(
                transform.position, goal, CurrentApproachSpeed * Time.deltaTime);
            yield return null;
        }

        transform.position = goal;

        // 到着したので「位置移動中」を解除する（歩行表示と足音を止める）。
        // 【片付け】片付けの歩行区間は Walk=true を維持する必要があるため、
        //   片付けルート進行中は Walk を落とさない（到着直後は向き合わせが続く）。
        if (!IsChoreRouteActive)
            MovementStateChanged?.Invoke(false);

        UpdateRouteState(target);

        // 到着直後は Walk→Idle の遷移中であることがある。
        // 遷移中にルートを旋回すると中間姿勢が見えるため、
        // 「実際に Idle State へ到達した」ことを確認してから旋回する。
        IdleWaitResult arriveResult = default;
        yield return WaitForIdleState("到着後", target?.name, r => arriveResult = r);

        if (!arriveResult.Reached)
        {
            Debug.LogWarning($"[MotherApproachController] 到着後({target?.name}): " +
                             "Idleへ到達できなかったため、経路を中断します。", this);
            HandleIdleWaitFailure("到着後");
            yield break;
        }
    }

    /// <summary>
    /// 帰路が中断確定したか（タイムアウト／ゲームオーバー）。
    ///
    /// 位置移動ループ・旋回ループから参照する「中断判定」。
    /// 結果の取り出し（TryConsumeReturnHomeResult）で ReturnHomePhase が Idle に戻っても
    /// このフラグは次サイクルの開始まで残るため、消費後に残った帰路コルーチンが
    /// 移動・旋回・完了通知を再開することはない。
    /// </summary>
    private bool _returnHomeAbortFlag;

    /// <summary>帰路が中断確定（中断フラグ）か。移動・旋回・完了通知を止める共通条件。</summary>
    private bool IsReturnHomeAborted => _returnHomeAbortFlag;

    /// <summary>
    /// 実行時APIだけで、使用中AnimatorのBase Layer直下にあるIdle Stateを解決する。
    /// Controllerの種類（AnimatorController／AnimatorOverrideController）には依存しない。
    /// </summary>
    private static bool TryResolveIdleState(Animator animator, out int layerIndex, out int idleFullPathHash,
        out string failureReason)
    {
        layerIndex = IdleLayerIndex;
        idleFullPathHash = Animator.StringToHash(IdleStateFullPath);
        failureReason = null;

        if (animator == null)
        {
            failureReason = "Animatorを解決できません";
            return false;
        }

        if (animator.layerCount <= layerIndex)
        {
            failureReason = $"レイヤー '{IdleLayerName}' が存在しません";
            return false;
        }

        if (animator.GetLayerName(layerIndex) != IdleLayerName)
        {
            failureReason = $"レイヤー{layerIndex}が '{IdleLayerName}' ではありません";
            return false;
        }

        if (!animator.HasState(layerIndex, idleFullPathHash))
        {
            failureReason = $"State '{IdleStateFullPath}' が存在しません";
            return false;
        }

        return true;
    }

    /// <summary>Animatorの現在Stateが、指定されたフルパスのIdleかどうかを判定する。</summary>
    private static bool IsInIdleState(Animator animator, int layerIndex, int idleFullPathHash)
    {
        if (animator == null || idleFullPathHash == 0) return false;
        return animator.GetCurrentAnimatorStateInfo(layerIndex).fullPathHash == idleFullPathHash;
    }

    /// <summary>
    /// 到着後、Animatorが「実際に Idle State へ到達」するまで待つ。
    ///
    /// 終了条件（すべて満たす）：
    ///   1. Walk パラメーターが false
    ///   2. 現在State（Base Layer）が `Base Layer.Idle` のフルパスハッシュと一致
    ///   3. そのレイヤーで遷移中ではない（IsInTransition == false）
    ///
    /// パラメーター値だけでState到達を判断しない（SetBool直後は現在Stateがまだ Walk のため）。
    /// 現在Stateが既に Idle なら待機しない（不要な待機を追加しない）。
    /// 上限フレームに達しても到達しなければ失敗をかえす（成功扱いしない・無限待機しない）。
    /// 位置移動は既に終わっているため、この待機は「移動」ではない（足音に影響しない）。
    /// </summary>
    private IEnumerator WaitForIdleState(string phase, string waypointName,
        System.Action<IdleWaitResult> onComplete)
    {
        var result = new IdleWaitResult { Reached = false, AlreadyIdle = false, FramesWaited = 0 };

        Animator animator = ResolveMotherAnimator();
        if (animator == null)
        {
            WarnIdleWaitFailure(phase, waypointName, animator, "Animatorを解決できません");
            onComplete?.Invoke(result);
            yield break;
        }

        if (!HasAnimatorParameter(animator, "Walk", AnimatorControllerParameterType.Bool))
        {
            WarnIdleWaitFailure(phase, waypointName, animator, "Walk(bool)パラメーターがありません");
            onComplete?.Invoke(result);
            yield break;
        }

        if (!TryResolveIdleState(animator, out int idleLayerIndex, out int idleFullPathHash,
                out string idleFailureReason))
        {
            WarnIdleWaitFailure(phase, waypointName, animator, idleFailureReason);
            onComplete?.Invoke(result);
            yield break;
        }

        // Walk=false を確実にする（覗きは Walk からは到達できないため）。
        // 【片付け】片付けの歩行区間は Walk=true を維持する必要があるため、
        //   片付けルート進行中は強制しない（片付けアニメ保護を壊さない）。
        if (!IsChoreRouteActive && animator.GetBool("Walk")) animator.SetBool("Walk", false);

        // 既に Idle に到達していれば、不要な待機はしない。
        if (IsIdleReached(animator, idleLayerIndex, idleFullPathHash))
        {
            result.Reached = true;
            result.AlreadyIdle = true;
            onComplete?.Invoke(result);
            yield break;
        }

        // 【片付け】片付けルート進行中は、Walk=true を維持したまま移動を続ける必要がある。
        //   Idle 到達（Walk=false 必須）を待つと到着判定が成立せず経路が中断するため、
        //   片付け中は Idle 待機を行わずに成功として扱う（片付けは Idle を要求しない）。
        if (IsChoreRouteActive)
        {
            result.Reached = true;
            onComplete?.Invoke(result);
            yield break;
        }

        int budget = Mathf.Max(1, maxFramesToReachIdle);
        for (int i = 0; i < budget; i++)
        {
            if (IsIdleReached(animator, idleLayerIndex, idleFullPathHash))
            {
                result.Reached = true;
                result.FramesWaited = i + 1;
                onComplete?.Invoke(result);
                yield break;
            }

            yield return null;
        }

        // 上限到達：Idleへ到達できなかった（成功扱いにしない）。
        WarnIdleWaitFailure(phase, waypointName, animator,
            $"上限 {budget} フレーム以内に Idle へ到達しませんでした");
        result.FramesWaited = budget;
        onComplete?.Invoke(result);
    }

    /// <summary>Idle到達条件（Walk=false／現在State=Idle／遷移中でない）を判定する。</summary>
    private static bool IsIdleReached(Animator animator, int idleLayerIndex, int idleFullPathHash)
    {
        return !animator.GetBool("Walk")
               && !animator.IsInTransition(idleLayerIndex)
               && IsInIdleState(animator, idleLayerIndex, idleFullPathHash);
    }

    /// <summary>Idle待機に失敗したときの警告。現在State・遷移先・Walk値・対象Animator名を出す。</summary>
    private void WarnIdleWaitFailure(string phase, string waypointName, Animator animator, string reason)
    {
        // 実行時APIのみを使う（Editor専用APIに依存しない）。
        string stateInfo = animator == null
            ? "animator=none"
            : $"stateHash={animator.GetCurrentAnimatorStateInfo(IdleLayerIndex).fullPathHash}" +
              $" transitioning={animator.IsInTransition(IdleLayerIndex)}";

        string walk = "n/a";
        if (animator != null && HasAnimatorParameter(animator, "Walk", AnimatorControllerParameterType.Bool))
            walk = animator.GetBool("Walk").ToString();
        string animName = animator != null ? animator.gameObject.name : "n/a";
        Debug.LogWarning($"[MotherApproachController] Idle待機に失敗しました（{reason}）| " +
                         $"段階={phase} waypoint={waypointName} | Animator='{animName}' Walk={walk} | {stateInfo}", this);
    }

    /// <summary>
    /// Idle待機の失敗を受けて、親機の演出状態を安全に巻き戻す。
    /// ライト・覗きフラグ・進行フラグを残さない（AbortApproach が全て解除する）。
    /// </summary>
    private void HandleIdleWaitFailure(string phase)
    {
        Debug.LogWarning($"[MotherApproachController] {phase}: Idleへ到達できなかったため、" +
                         "覗きを要求せず安全に中断します（ライト・覗きフラグを消灯／解除）。", this);
        AbortApproach();
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  母親アニメーション（覗き）の再生
    //
    //  Animatorパラメーターを書き換える箇所は、このファイルと MotherSuspicionSystem の2つだけ：
    //   ・MotherApproachController … 覗きのTrigger（Peek_Windows）。「庭側覗きの再生開始」のみ担当。
    //   ・MotherSuspicionSystem          … Walk(bool) と ドア覗きのTrigger（Peek_Door）。検出・演出進行に追従。
    //  同じ状態を同時に上書きしないよう、MotherSuspicionSystem 側は「覗き再生中は Walk を書き換えない」
    //  ガード（IsPeekAnimationActive）を持っている。
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 窓覗きアニメーションのAnimatorパラメーター名。
    /// 実際に使用している mother_animationcontroller のTrigger名に合わせる（"Peek_Windows" と複数形）。
    /// このTriggerは AnyState → State"window peek" の遷移条件で、同Stateに
    /// Hahaoya_Peek window.fbx が割り当てられていることを確認済み。
    /// </summary>
    private const string PeekWindowsParameter = "Peek_Windows";

    /// <summary>
    /// 窓覗きアニメーションを担当するAnimator。
    /// Inspectorの明示参照（motherAnimator）が最優先。未設定なら motherModelRoot 配下に限定して自動取得する。
    /// </summary>
    [Tooltip("母親モデルのAnimator。未設定の場合は motherModelRoot の子から自動取得する。" +
             "シーン全体からは検索しないため、無関係なAnimatorを誤って掴むことはない。")]
    [SerializeField]
    private Animator motherAnimator;

    /// <summary>Animator未検出／パラメーター欠落の警告を多重出力しないためのフラグ。</summary>
    private bool _animatorWarningLogged;

    /// <summary>庭覗き時間の取得元（MotherApproachWarning）。未設定なら自動検索して結果をキャッシュする。</summary>
    private MotherApproachWarning _warningSystemCache;

    /// <summary>
    /// 庭覗きの基本時間（秒）を MotherApproachWarning から取得する。
    /// 設定の唯一の保持元は MotherApproachWarning（chore/garden の調整値を集約）。
    /// 接続できない場合は固定値へ戻さず、警告して 0 を返す（呼び出し側は最小の覗き時間になる）。
    /// </summary>
    private float ResolveGardenPeekDurationBase()
    {
        if (_warningSystemCache == null)
            _warningSystemCache = UnityEngine.Object.FindFirstObjectByType<MotherApproachWarning>();

        if (_warningSystemCache == null)
        {
            Debug.LogWarning("[MotherApproachController] MotherApproachWarning が見つからないため、" +
                             "庭覗き時間を取得できません（MotherApproachWarning を Scene に配置してください）", this);
            return 0f;
        }

        return _warningSystemCache.GardenPeekDurationBase;
    }

    /// <summary>
    /// 母親モデルのAnimator（MotherSuspicionSystemなど他スクリプトと共有するための公開アクセサ）。
    /// 「どのオブジェクトをAnimatorとして扱うか」の判断をこの1箇所に集約し、
    /// 複数スクリプトが別々に取得して別のAnimatorを掴む事態を防ぐ。
    /// </summary>
    public Animator MotherAnimator => ResolveMotherAnimator();

    /// <summary>
    /// 母親モデルのAnimatorを解決する。
    ///   1. Inspectorの明示参照（motherAnimator）
    ///   2. motherModelRoot 配下（GetComponentsInChildren で「複数見つかった場合は取得しない」）
    /// シーン全体からの検索は行わない。取得できない場合は対象オブジェクト名を含む警告を出す。
    /// </summary>
    private Animator ResolveMotherAnimator()
    {
        if (motherAnimator != null) return motherAnimator;

        if (motherModelRoot == null)
        {
            WarnAnimatorOnce("[MotherApproachController] motherAnimatorもmotherModelRootも未設定のため、" +
                             "母親モデルのAnimatorを特定できません。Inspectorで motherAnimator を明示的に割り当ててください。");
            return null;
        }

        // motherModelRoot配下に限定して探す（無関係なAnimatorを掴まないため）。
        Animator[] candidates = motherModelRoot.GetComponentsInChildren<Animator>(true);
        if (candidates.Length == 1)
        {
            motherAnimator = candidates[0];
            return motherAnimator;
        }

        if (candidates.Length == 0)
        {
            WarnAnimatorOnce($"[MotherApproachController] motherModelRoot '{motherModelRoot.name}' 配下に" +
                             "Animatorが見つかりません。対象オブジェクトとAnimatorの構成を確認してください。");
            return null;
        }

        // 複数見つかった場合は先頭を無条件に選ばず、判断材料をログに出す（誤動作を正常扱いしない）。
        var names = new System.Text.StringBuilder();
        for (int i = 0; i < candidates.Length; i++)
            names.Append(i > 0 ? ", " : "").Append(candidates[i].gameObject.name);

        WarnAnimatorOnce($"[MotherApproachController] motherModelRoot '{motherModelRoot.name}' 配下に" +
                         $"Animatorが{candidates.Length}個あります（{names}）。" +
                         "誤ったAnimatorを掴まないよう自動取得を中止しました。Inspectorで motherAnimator を明示的に割り当ててください。");
        return null;
    }

    // ── Door Peek 横スライド ──────────────────────────────────────────────────
    //
    //  Door Peekの「実際の再生開始」から時間を数え始め、母親自身の左／右へ
    //  短くスライドして元位置へ戻す演出。
    //
    //  再生開始の基準 = 使用中ControllerのDoor Peek State
    //                   （Base Layer.Peek_Door）へ入った瞬間。
    //  「IdleでもPeek_Windowでもない」といった消去法は使わない
    //  （Walk / Gosogoso なども該当してしまうため）。
    //
    //  Window Peek・Pass By・猫・入室・Rush Inでは呼ばない（Door Peek専用）。

    /// <summary>
    /// Door Peek用のスライドを開始する。呼び出しはDoor Peekの経路から1回だけ。
    ///
    /// 二重起動の扱い：
    ///   ・コルーチンが1本でも、同一Peek中に再度呼ばれたら**無視**する
    ///     （進行中の時間計測を止めてゼロから数え直さない）。
    ///   ・前のPeekが終わってから次のPeekが始まる場合は、一度だけ開始する。
    /// </summary>
    public void BeginDoorPeekSlide()
    {
        if (!enableDoorPeekSlide)
        {
            SlideLog("REJECT", "enableDoorPeekSlide=false");
            return;
        }

        // 同じPeekの重複要求は無視する（時間をゼロから数え直さない）。
        if (_peekSlideRequested)
        {
            SlideLog("REJECT", "同一Peekで要求済み（二重起動防止）");
            return;
        }

        _peekSlideRequested = true;
        SlideLog("ACCEPT", $"distance={doorPeekSlideDistance} startDelay={doorPeekSlideStartDelay} " +
                           $"returnDelay={doorPeekSlideReturnDelay} outDur={doorPeekSlideOutDuration} " +
                           $"backDur={doorPeekSlideBackDuration}");

        // 呼び出し時点のAnimatorの状態を記録する（State待ちの前提を可視化する）。
        LogDoorPeekAnimatorSnapshot("ACCEPT時点");

        _peekSlideCoroutine = StartCoroutine(DoorPeekSlideRoutine());
    }

    /// <summary>使用中AnimatorのController名・レイヤー・現在State／遷移先を1回だけ記録する。</summary>
    private void LogDoorPeekAnimatorSnapshot(string timing)
    {
        if (!doorPeekSlideVerboseLog) return;

        Animator animator = ResolveMotherAnimator();
        if (animator == null)
        {
            SlideLog("ANIMATOR", $"{timing}: Animatorを解決できません");
            return;
        }

        if (!TryResolveIdleState(animator, out int layerIndex, out _, out string layerFailure))
        {
            SlideLog("ANIMATOR", $"{timing}: レイヤー解決失敗 ({layerFailure})");
            return;
        }

        int doorHash = Animator.StringToHash(DoorPeekStateFullPath);
        AnimatorStateInfo current = animator.GetCurrentAnimatorStateInfo(layerIndex);
        bool inTransition = animator.IsInTransition(layerIndex);
        string nextInfo = "";

        if (inTransition)
        {
            AnimatorStateInfo next = animator.GetNextAnimatorStateInfo(layerIndex);
            nextInfo = $" nextHash={next.fullPathHash} nextLen={next.length:F2}";
        }

        string controller = animator.runtimeAnimatorController != null
            ? animator.runtimeAnimatorController.name
            : "null";

        SlideLog("ANIMATOR",
            $"{timing} | obj='{animator.gameObject.name}' controller='{controller}' " +
            $"layer={layerIndex}('{animator.GetLayerName(layerIndex)}') " +
            $"expected='{DoorPeekStateFullPath}' hash={doorHash} | " +
            $"currentHash={current.fullPathHash} inTransition={inTransition}{nextInfo}");
    }

    /// <summary>スライド演出のコルーチンだけを止める（状態＝予約済みフラグは呼び出し側が決める）。</summary>
    private void StopDoorPeekSlideCoroutine()
    {
        if (_peekSlideCoroutine != null)
        {
            StopCoroutine(_peekSlideCoroutine);
            _peekSlideCoroutine = null;
        }
    }

    /// <summary>
    /// スライドの予約をすべて取り消す（コルーチン停止＋予約フラグ解除）。
    /// Peekが終わった／中断した／リセットされたときに呼ぶ。
    /// 予約フラグを解除することで、次のPeekでは再び一度だけ開始できる。
    /// </summary>
    private void CancelDoorPeekSlideRequest()
    {
        bool had = _peekSlideRequested;
        StopDoorPeekSlideCoroutine();
        _peekSlideRequested = false;
        if (had) SlideLog("CANCEL_REQUEST", "予約を取り消し");
    }

    /// <summary>
    /// ずれている場合に、元位置へ戻す。
    /// duration > 0 なら補間で滑らかに戻す（通常終了時の呼び出し）。
    /// duration <= 0 または immediate なら即時復帰（強制中断・非表示時）。
    /// 歩行イベント・足音は出さない。
    /// </summary>
    public IEnumerator RestoreDoorPeekSlidePosition(float duration, bool immediate = false)
    {
        CancelDoorPeekSlideRequest();

        // 既に元位置なら不要な待機をしない。
        if (!_peekSlideOriginValid || _peekSlideOffset == Vector3.zero)
        {
            SlideLog("RESTORE_SKIP", $"originValid={_peekSlideOriginValid} offset={_peekSlideOffset}（既に元位置）");
            yield break;
        }

        SlideLog("RESTORE", $"duration={duration} immediate={immediate} from={transform.position} " +
                            $"to={_peekSlideOriginPosition}");

        if (immediate || duration <= 0f)
        {
            ApplyHorizontalPosition(_peekSlideOriginPosition);
            _peekSlideOffset = Vector3.zero;
            yield break;
        }

        // 現在位置から保存済みの元位置へ、指定時間で補間して戻す。
        yield return SlideSmoothlyTo(_peekSlideOriginPosition, duration);
    }

    /// <summary>
    /// スライドに関わる状態を初期化する（リセット時に呼ぶ）。
    /// 位置は触らない（呼び出し側が復帰・非表示を担当する）。
    /// </summary>
    private void ResetDoorPeekSlideState()
    {
        CancelDoorPeekSlideRequest();
        _peekSlideOffset = Vector3.zero;
        _peekSlideOriginValid = false;
    }

    /// <summary>Animator関連の警告を1回だけ出す（毎フレームのログ氾濫を避ける）。</summary>
    private void WarnAnimatorOnce(string message)
    {
        if (_animatorWarningLogged) return;
        _animatorWarningLogged = true;
        Debug.LogWarning(message, this);
    }

    // ── Door Peek 横スライド：段階ログ ────────────────────────────────────────
    //  doorPeekSlideVerboseLog がONのときだけ、各段階を1回ずつ出力する
    //  （同じ待機状態を毎フレーム大量出力しない）。
    private void SlideLog(string stage, string detail = null)
    {
        if (!doorPeekSlideVerboseLog) return;
        string frame = $"F:{Time.frameCount} t:{Time.time:F3}";
        string state = $"requested={_peekSlideRequested} offset={_peekSlideOffset} " +
                       $"originValid={_peekSlideOriginValid}";
        Debug.Log($"[DoorPeekSlide] {stage} | {frame} | {state}" +
                  (string.IsNullOrEmpty(detail) ? "" : $" | {detail}"), this);
    }

    /// <summary>
    /// Door Peekスライドの本体。
    ///
    /// 時間の基準は「Peek実再生開始からの経過時間（peekElapsed）」ひとつだけ。
    /// 待機と補間を逐次足し算せず、経過時間から各区間の位相を毎フレーム算出する。
    /// これにより outDuration > returnDelay でも、外向き開始から returnDelay 秒で
    /// 必ず戻り始める（外向き完了を待って戻り時刻が遅れない）。
    /// </summary>
    private IEnumerator DoorPeekSlideRoutine()
    {
        // 1. Door Peek State（正確なフルパス）へ実際に入ったことを確認してから時間を数える。
        //    上限はゲーム内時間の秒数（高フレームレートでも遷移完了を待てる）。
        bool started = false;
        yield return WaitForDoorPeekStateStarted(doorPeekSlideStateWaitTimeoutSeconds, r => started = r);
        if (!started)
        {
            LogDoorPeekAnimatorSnapshot("State待ち失敗時点");
            SlideLog("WAIT_FAIL",
                $"上限 {doorPeekSlideStateWaitTimeoutSeconds:F2}s で '{DoorPeekStateFullPath}' に入れず");
            Debug.LogWarning($"[MotherApproachController] Door Peek State '{DoorPeekStateFullPath}' へ" +
                             $" {doorPeekSlideStateWaitTimeoutSeconds:F2}秒以内に到達できなかったため、" +
                             "横スライドを開始しません（安全に中止）。", this);
            CancelDoorPeekSlideRequest();
            yield break;
        }

        SlideLog("WAIT_OK", $"'{DoorPeekStateFullPath}' に入った（実再生開始）");

        // 2. 元位置（ずれ0の位置）と、母親自身の左方向を確定する。
        //    向きは開始時の姿勢で確定し、途中の姿勢変化では変えない。
        _peekSlideOriginPosition = transform.position;
        _peekSlideOffset = Vector3.zero;
        _peekSlideOriginValid = true;

        Vector3 slideDirection = GetHorizontalLeftDirection();
        float distance = doorPeekSlideDistance;

        Debug.Log($"[MotherApproachController] Door Peek 横スライド開始 | origin={_peekSlideOriginPosition} " +
                  $"distance={distance:F2} startDelay={doorPeekSlideStartDelay:F2} " +
                  $"returnDelay={doorPeekSlideReturnDelay:F2} outDuration={doorPeekSlideOutDuration:F2}");

        Vector3 outTarget = _peekSlideOriginPosition + slideDirection * distance;

        SlideLog("ORIGIN", $"target='{transform.name}' origin={_peekSlideOriginPosition} " +
                           $"leftDir={slideDirection} outTarget={outTarget} " +
                           $"currentWorldPos={transform.position}");

        // 方向不定・距離0は位置を変えない（何もせず終了）。
        if (slideDirection == Vector3.zero || distance == 0f)
        {
            SlideLog("SKIP", $"directionZero={slideDirection == Vector3.zero} distanceZero={distance == 0f}");
            _peekSlideCoroutine = null;
            yield break;
        }

        // 3. 区間の境界（すべてPeek実再生開始からの経過時間）。
        float outStart = doorPeekSlideStartDelay;
        float outEnd = doorPeekSlideStartDelay + doorPeekSlideOutDuration;
        float returnStart = doorPeekSlideStartDelay + doorPeekSlideReturnDelay;
        // 外向きは「戻り開始時刻」を超えて続けない（returnDelayを守る）。
        float outEffectiveEnd = Mathf.Min(outEnd, returnStart);

        float peakElapsed = 0f;
        int lastPhase = -1; // 0=wait 1=out 2=hold 3=back（段階ログの1回出力用）
        while (true)
        {
            // 強制中断／ゲームオーバー／ルート失敗なら、その場でスライドを止める。
            if (IsReturnHomeAborted || _routeExecutionFailed)
            {
                SlideLog("CANCEL", $"abort={IsReturnHomeAborted} routeFailed={_routeExecutionFailed}");
                _peekSlideCoroutine = null;
                yield break;
            }

            Vector3 horizontal;
            bool finished = false;
            int phase;

            if (peakElapsed < outStart)
            {
                // 開始待ち：元位置のまま
                horizontal = HorizontalOnly(_peekSlideOriginPosition);
                phase = 0;
            }
            else if (peakElapsed < outEffectiveEnd)
            {
                // 外向き補間（outDuration=0 ならこの区間は存在せず即座に通過）
                float t = doorPeekSlideOutDuration <= 0f
                    ? 1f
                    : Mathf.Clamp01((peakElapsed - outStart) / doorPeekSlideOutDuration);
                horizontal = Vector3.Lerp(HorizontalOnly(_peekSlideOriginPosition),
                    HorizontalOnly(outTarget), t);
                phase = 1;
            }
            else if (peakElapsed < returnStart)
            {
                // 外向き完了〜戻り開始までの保持（戻り開始時刻で必ず終わる）
                horizontal = HorizontalOnly(outTarget);
                phase = 2;
            }
            else
            {
                // 戻り区間：returnStart から backDuration で元位置へ
                float backT = doorPeekSlideBackDuration <= 0f
                    ? 1f
                    : Mathf.Clamp01((peakElapsed - returnStart) / doorPeekSlideBackDuration);
                horizontal = Vector3.Lerp(HorizontalOnly(outTarget),
                    HorizontalOnly(_peekSlideOriginPosition), backT);
                phase = 3;
                if (backT >= 1f) finished = true;
            }

            // 段階の変わり目だけ1回出力する（毎フレーム大量出力しない）。
            if (phase != lastPhase)
            {
                lastPhase = phase;
                switch (phase)
                {
                    case 1: SlideLog("OUT_START", $"elapsed={peakElapsed:F3} target={horizontal}"); break;
                    case 3: SlideLog("BACK_START", $"elapsed={peakElapsed:F3}"); break;
                }
            }

            ApplyHorizontalPosition(horizontal);
            _peekSlideOffset = HorizontalOnly(horizontal - _peekSlideOriginPosition);

            if (finished)
            {
                // 戻り完了：保存位置へ正確にそろえる
                ApplyHorizontalPosition(_peekSlideOriginPosition);
                _peekSlideOffset = Vector3.zero;
                SlideLog("COMPLETE", $"elapsed={peakElapsed:F3} worldPos={transform.position}");
                _peekSlideCoroutine = null;
                yield break;
            }

            peakElapsed += Time.deltaTime; // ゲーム内時間（通常ポーズ中は進めない）
            yield return null;
        }
    }

    /// <summary>
    /// 現在位置から目標位置へ、指定時間かけて補間で移動する（通常終了時の戻り演出用）。
    /// 歩行イベント（MovementStateChanged）・足音は出さない。
    /// 中断が確定したら途中で抜ける。完了時は目標位置へ正確にそろえる。
    /// </summary>
    private IEnumerator SlideSmoothlyTo(Vector3 target, float duration)
    {
        Vector3 fromHorizontal = HorizontalOnly(transform.position);
        Vector3 targetHorizontal = HorizontalOnly(target);

        float elapsed = 0f;
        while (elapsed < duration)
        {
            if (IsReturnHomeAborted || _routeExecutionFailed) yield break;

            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            Vector3 horizontal = Vector3.Lerp(fromHorizontal, targetHorizontal, t);
            ApplyHorizontalPosition(horizontal);
            _peekSlideOffset = HorizontalOnly(horizontal - _peekSlideOriginPosition);
            yield return null;
        }

        ApplyHorizontalPosition(target);
        _peekSlideOffset = HorizontalOnly(targetHorizontal - _peekSlideOriginPosition);
    }

    /// <summary>Yを0にした水平成分（元位置との差分をY方向に残さないため）。</summary>
    private static Vector3 HorizontalOnly(Vector3 v) => new Vector3(v.x, 0f, v.z);

    /// <summary>
    /// 水平位置だけを適用する（Yは現在値を維持＝既存の高さ補正を壊さない）。
    /// 回転には触れない。
    /// </summary>
    private void ApplyHorizontalPosition(Vector3 horizontalSource)
    {
        Vector3 current = transform.position;
        transform.position = new Vector3(horizontalSource.x, current.y, horizontalSource.z);
    }

    /// <summary>
    /// 母親自身の左方向（水平面）。開始時の姿勢（transform.rotation）から取得する。
    /// </summary>
    private Vector3 GetHorizontalLeftDirection()
    {
        Vector3 left = HorizontalOnly(-transform.right);
        if (left.sqrMagnitude < 1e-6f)
            return Vector3.zero; // 真上／真下向きで水平左が定まらない場合は動かさない
        return left.normalized;
    }

    /// <summary>
    /// Door Peek State（正確なフルパス）へ実際に入るまで待つ（実行時APIのみ）。
    ///
    /// 開始成功条件（維持）：
    ///   ・対象レイヤー（Base Layer）を解決できている
    ///   ・遷移中ではない（IsInTransition == false）
    ///   ・現在Stateの fullPathHash が Door Peek のフルパス（Base Layer.Peek_Door）と一致
    ///   → Triggerを設定しただけでは成功にしない。消去法による判定もしない。
    ///
    /// 待機の上限は「ゲーム内時間の秒数」（doorPeekSlideStateWaitTimeoutSeconds）。
    /// Time.deltaTime で計測するため、timeScale=0 の通常一時停止中は進まない。
    /// フレーム数の上限は使わない（高フレームレートで遷移前に上限へ達してしまうため）。
    ///
    /// Peek終了・ゲームオーバー・経路中断・リセットでは、時間上限を待たず中止する。
    /// 負の設定値は0扱い（＝即タイムアウト）として安全に処理する。
    /// </summary>
    private IEnumerator WaitForDoorPeekStateStarted(float timeoutSeconds, System.Action<bool> onComplete)
    {
        bool started = false;

        // 負の設定値は安全に0として扱う（例外や無限待機にしない）。
        float timeout = Mathf.Max(0f, timeoutSeconds);

        Animator animator = ResolveMotherAnimator();
        if (animator != null &&
            TryResolveIdleState(animator, out int layerIndex, out _, out _))
        {
            int doorPeekHash = Animator.StringToHash(DoorPeekStateFullPath);
            float elapsed = 0f;

            while (elapsed < timeout)
            {
                // 中断が確定したら時間上限を待たずに中止する（スライドを始めない）。
                if (IsReturnHomeAborted || _routeExecutionFailed) break;

                // 開始成功条件：遷移中ではなく、現在Stateが Door Peek State と一致。
                if (!animator.IsInTransition(layerIndex) &&
                    animator.GetCurrentAnimatorStateInfo(layerIndex).fullPathHash == doorPeekHash)
                {
                    started = true; // Door Peek State に入った＝実再生開始
                    break;
                }

                elapsed += Time.deltaTime; // ゲーム内時間（一時停止中は進めない）
                yield return null;
            }

            // 上限超過の記録（経過秒数と各ハッシュを既存ログへ出す）。
            if (!started && doorPeekSlideVerboseLog)
            {
                AnimatorStateInfo current = animator.GetCurrentAnimatorStateInfo(layerIndex);
                bool inTransition = animator.IsInTransition(layerIndex);
                int nextHash = inTransition
                    ? animator.GetNextAnimatorStateInfo(layerIndex).fullPathHash
                    : current.fullPathHash;

                SlideLog("WAIT_TIMEOUT",
                    $"elapsed={elapsed:F3}s/{timeout:F2}s expected={doorPeekHash} " +
                    $"currentHash={current.fullPathHash} nextHash={nextHash} inTransition={inTransition}");
            }
        }

        onComplete?.Invoke(started);
    }


    /// <summary>
    /// 指定名のAnimator Triggerパラメーターを1回だけ発火する。
    /// パラメーターを持たないControllerの場合は警告を1度だけ出して何もしない（例外は投げない）。
    /// </summary>
    private void TriggerPeekAnimation(string parameterName)
    {
        Animator animator = ResolveMotherAnimator();
        if (animator == null)
        {
            // ResolveMotherAnimator側で理由（対象オブジェクト名・候補数）を警告済み。
            return;
        }

        if (!HasAnimatorParameter(animator, parameterName, AnimatorControllerParameterType.Trigger))
        {
            WarnAnimatorOnce($"[MotherApproachController] Animator '{animator.gameObject.name}' のControllerに" +
                             $"パラメーター'{parameterName}'(Trigger)がないため、覗きアニメーションの再生をスキップします。");
            return;
        }

        animator.ResetTrigger(parameterName);
        animator.SetTrigger(parameterName);
    }

    /// <summary>Animatorが指定名・指定型のパラメーターを持っているかを返す。</summary>
    private static bool HasAnimatorParameter(Animator animator, string parameterName,
        AnimatorControllerParameterType type)
    {
        if (animator == null) return false;
        foreach (AnimatorControllerParameter parameter in animator.parameters)
        {
            if (parameter.name == parameterName && parameter.type == type) return true;
        }

        return false;
    }

    /// <summary>
    /// 庭ルートに入ったことを記録する（足音の切り替えに使う）。
    /// 庭ルートの1つ目のウェイポイントに到達した時点、または名前が GardenPoint_1 の点で切り替える。
    /// </summary>
    private void UpdateRouteState(Transform target)
    {
        if (target == null) return;

        if (target == gardenPeekPoint || target.name == "GardenPoint_1") IsGardenRoute = true;

        if (gardenRoutePoints != null && gardenRoutePoints.Length > 0 && target == gardenRoutePoints[0])
            IsGardenRoute = true;
    }

    /// <summary>
    /// 進行中の旋回コルーチンを停止してハンドルを手放す。
    /// 停止・リセット・覗き開始の直前に呼び、回転指示が残り続けないようにする。
    /// </summary>
    private void StopRotateCoroutine()
    {
        if (_rotateCoroutine == null) return;
        StopCoroutine(_rotateCoroutine);
        _rotateCoroutine = null;
    }

    /// <summary>
    /// 旋回速度が0以下のときに使うフォールバック速度（回転が終わらず無限待機するのを防ぐ）。
    /// </summary>
    private const float FallbackTurnSpeed = 90f;

    /// <summary>
    /// 向きの基準は「ワールド空間のローカル＋Z（前方向）」に統一する。
    /// Transform.forward は親の回転を考慮したワールド空間の＋Zを返すため、
    /// Waypointの親が回転していても正しい方向になる。
    /// Vector3.forward（ワールド固定の＋Z）とは別物なので混同しないこと。
    ///
    /// Waypointの向きを目標Y角として取り出す唯一の入口。
    /// 水平成分がほぼゼロの不正な向き（真上・真下を向いたWaypoint）は警告して現在の向きを維持する。
    /// </summary>
    private bool TryGetTargetYawFromForward(Transform target, out float yaw, string label)
    {
        // ルートとWaypointで同じ入口（ローカル＋Zの水平投影）を使う。
        return TryGetYawFromTransformForward(target, out yaw, label);
    }

    /// <summary>
    /// 目標Y角へ滑らかに旋回する。
    ///
    /// 【旋回開始角度の求め方】
    ///   開始時の現在角も、MotherRouteRoot の transform.forward を水平面へ投影し
    ///   Atan2(forward.x, forward.z) で求める（eulerAngles.y は読まない）。
    ///   これで「向きの基準は常にローカル＋Zの水平投影」に統一される。
    ///
    /// 【更新方法】
    ///   保持した currentYaw を MoveTowardsAngle で更新し、毎フレーム eulerAngles.y を
    ///   読み直すことはしない。理由は、Quaternion.Euler(pitch, yaw, roll) が yaw ±180度付近で
    ///   等価な別表現（X/Zが180反転）に再分解されうるため。
    ///   eulerAngles.y を読み直すと誤差の符号が反転し、2角度間の往復が起こり得る
    ///   eulerAngles.y を読み直すと誤差の符号が反転し、2角度間の往復が起こり得る。
    ///
    /// 目標への最短方向で旋回し、完了時は許容誤差内で目標方向へそろえる。
    /// 開始角が得られない（水平成分ほぼゼロ）場合は警告して旋回せずに抜ける。
    /// </summary>
    private IEnumerator RotateToYaw(float targetYaw, float speed)
    {
        float targetYawNormalized = NormalizeAngle(targetYaw);

        // 速度0以下だと回転が終わらず無限に待ち続けるため、必ず完了する速度にする。
        float effectiveSpeed = speed > 0f ? speed : FallbackTurnSpeed;

        // 開始角もルートの水平＋Zから求める（仕様の統一）。
        if (!TryGetYawFromTransformForward(transform, out float currentYaw, "母親ルート"))
        {
            Debug.LogWarning("[MotherApproachController] 旋回を開始できません（ルートの＋Zが水平成分を持ちません）。" +
                             "現在の向きを維持します。", this);
            _rotateCoroutine = null;
            yield break;
        }

        while (Mathf.Abs(Mathf.DeltaAngle(currentYaw, targetYawNormalized)) > 0.5f)
        {
            // 帰路が中断されたら、旋回もその場で止める（非表示後の旋回を残さない）。
            if (IsReturnHomeAborted)
            {
                _rotateCoroutine = null;
                yield break;
            }

            currentYaw = Mathf.MoveTowardsAngle(
                currentYaw, targetYawNormalized, effectiveSpeed * Time.deltaTime);

            transform.rotation = Quaternion.Euler(_fixedPitch, currentYaw, _fixedRoll);

            yield return null;
        }

        // 完了時は目標へそろえる。
        SetYaw(targetYawNormalized);

        // 完了したらハンドルを手放す（以降の移動が再び向きを担当できるようにする）。
        _rotateCoroutine = null;
    }

    /// <summary>
    /// Transformの水平＋Z（forwardのYを落として正規化）からY角を求める。
    /// 「向きの基準は常にローカル＋Zの水平投影」に統一するための入口。
    /// 水平成分がほぼゼロ（真上・真下向き）の場合は false を返す。
    /// </summary>
    private static bool TryGetYawFromTransformForward(Transform t, out float yaw, string label)
    {
        yaw = 0f;
        if (t == null) return false;

        Vector3 forward = t.forward;
        forward.y = 0f; // 地面上の向きとして扱う

        if (forward.sqrMagnitude <= 0.0001f)
        {
            Debug.LogWarning($"[MotherApproachController] {label} の＋Zが水平成分を持ちません" +
                             $"（forward={t.forward}）。地面上の向きを決められません。");
            return false;
        }

        forward.Normalize();
        yaw = NormalizeAngle(Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg); // ＋Z基準
        return true;
    }

    private void SetYaw(float yaw)
    {
        transform.rotation = Quaternion.Euler(_fixedPitch, yaw, _fixedRoll);
    }

    private static float NormalizeAngle(float angle)
    {
        angle %= 360f;
        if (angle > 180f) angle -= 360f;
        if (angle < -180f) angle += 360f;
        return angle;
    }

    // ── 母親モデルの照明（顔ライト／目の発光） ───────────────────────────────
    //
    //  点灯／消灯は「グループ単位」で切り替える。Peekの開始・終了で個別に消す呼び出しは
    //  すべてこの2グループへ委譲し、途中で消灯しないようにする。
    //
    //  ・SetMotherStageLighting(true) … グループ（A）基本：母親モデル表示中は常時ON
    //  ・SetPeekLighting(true)        … グループ（B）追加：Peek中だけON（庭Peekの顔ライト演出）
    //
    //  「毎フレーム強制設定」はしない。明滅（eyeGlowPulseSpeed>0）のときだけ見た目の色を更新し、
    //  UpdateEyeColor()内の「値が変わったときだけ書き込む」ガードで無駄な更新を避ける。

    // シーンに置いたライトの明るさを「点灯時の明るさ」として覚えておく。
    // （消灯中は intensity を 0 にするため、Start 時点の値を基準にする）
    private void CacheFaceLightIntensity()
    {
        if (windowPeekFaceLight != null)
            _faceLightBaseIntensity = windowPeekFaceLight.intensity;
    }

    /// <summary>
    /// グループ（A）基本の照明：母親モデルの表示中は常時ON、非表示・サイクル初期化でOFF。
    /// グループ（B）の状態も見て、最終的な点灯状態を反映する。
    /// </summary>
    private void SetMotherStageLighting(bool on)
    {
        _eyesOn = on;
        _faceLightOn = on;
        ApplyLightingState();
    }

    /// <summary>
    /// グループ（B）追加の照明：Peek中だけON（庭Peekの顔ライト演出）。
    /// OFFにするときもグループ（A）がONなら点灯したままになる（途中で消灯しない）。
    /// </summary>
    private void SetPeekLighting(bool on)
    {
        _peekLightingOn = on;
        ApplyLightingState();
    }

    /// <summary>グループ（A)(B）の状態から、顔ライトと目の発光の点灯状態を反映する（毎フレームは呼ばない）。</summary>
    private void ApplyLightingState()
    {
        bool faceLightShouldBeOn = _faceLightOn || _peekLightingOn;

        if (windowPeekFaceLight != null)
        {
            windowPeekFaceLight.enabled = faceLightShouldBeOn;
            if (faceLightShouldBeOn)
            {
                // 点灯中の明るさは常に基準値（フェード中の上書きが残らないようにする）。
                windowPeekFaceLight.intensity = _faceLightBaseIntensity;
                // 頭部追従はグループ（A）のみのときだけ有効にする（庭Peekの見え方を変えない）。
                _faceLightFollowActive = faceLightFollowsHead && _faceLightOn && !_peekLightingOn;
                if (_faceLightFollowActive)
                    ApplyFaceLightFollow();
            }
            else
            {
                windowPeekFaceLight.intensity = 0f;
                _faceLightFollowActive = false;
            }
        }
        else
        {
            _faceLightFollowActive = false;
        }

        SetEyesVisible(_eyesOn);
    }

    /// <summary>
    /// 点灯中に限り、顔ライトを頭部のワールド回転へ一致させる（LateUpdateから呼ぶ）。
    /// 頭部が見つからない場合は追従せず、シーン配置の向きを維持する。
    /// </summary>
    private void FaceLightLateUpdate()
    {
        if (!_faceLightFollowActive || windowPeekFaceLight == null) return;
        ApplyFaceLightFollow();
    }

    private void ApplyFaceLightFollow()
    {
        Transform head = faceLightHeadAnchor != null ? faceLightHeadAnchor : _faceLightHeadTransformCache;
        if (head == null)
        {
            head = ResolveFaceLightHeadTransform();
            if (head == null) return;
        }

        windowPeekFaceLight.transform.rotation = head.rotation * Quaternion.Euler(faceLightRotationOffset);
    }

    /// <summary>
    /// avatarのHumanoid Headから頭部Transformを解決する（1度だけ試す）。
    /// 旧モデルのオブジェクトへ直接依存しないため、モデル差し替えの影響を受けない。
    /// </summary>
    private Transform ResolveFaceLightHeadTransform()
    {
        if (_faceLightHeadTransformCache != null) return _faceLightHeadTransformCache;
        if (_faceLightHeadResolveAttempted) return null;
        _faceLightHeadResolveAttempted = true;

        Animator animator = ResolveMotherAnimator();
        if (animator == null) return null;
        if (animator.avatar == null || !animator.avatar.isHuman)
        {
            WarnAnimatorOnce("[MotherApproachController] avatarがHumanoidではないため、" +
                             "顔ライトを頭部へ追従できません（faceLightHeadAnchorを明示設定してください）。");
            return null;
        }

        Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
        if (head == null)
        {
            WarnAnimatorOnce("[MotherApproachController] Headボーンが見つからないため、" +
                             "顔ライトを頭部へ追従できません（faceLightHeadAnchorを明示設定してください）。");
            return null;
        }

        _faceLightHeadTransformCache = head;
        return _faceLightHeadTransformCache;
    }

    /// <summary>目の発光を点灯／消灯する。ONのときは発光マテリアルを1度だけ用意して色を反映する。</summary>
    private void SetGlowingEyes(bool isEnabled)
    {
        SetEyesVisible(isEnabled);
    }

    private void SetEyesVisible(bool visible)
    {
        if (glowingEyesObject != null)
            glowingEyesObject.SetActive(visible);

        if (visible)
        {
            EnsureEyeMaterials();
            UpdateEyeColor();
        }
        else
        {
            ReleaseEyeMaterials();
        }
    }

    /// <summary>
    /// 発光に使うマテリアルの所有インスタンスを1つだけ生成して保持する。
    ///
    /// Renderer.material（Unityの自動インスタンス化）は使わず、元の sharedMaterial から
    /// new Material で明示的に生成し、このコンポーネントが所有者になる。
    /// これにより「どのインスタンスが自分の所有物か」が _eyeOwnedMaterialX だけで明確になり、
    /// 共有マテリアルアセットと取り違える余地がなくなる。
    ///
    /// 毎フレーム UpdateEyeColor から呼ばれるが、既に生成済みなら再生成も再バインドもしない。
    /// 外部（他スクリプト）が意図してマテリアルを差し替えていた場合は上書きしない。
    /// </summary>
    private void EnsureEyeMaterials()
    {
        // 目オブジェクトが非表示（SetActive(false)）の間は、Rendererが無効で
        // material への割り当てが正しく反映されないことがあるため、生成しない。
        if (glowingEyesObject != null && !glowingEyesObject.activeSelf)
            return;

        EnsureEyeMaterial(eyeRendererL, ref _eyeOriginalMaterialL, ref _eyeOwnedMaterialL);
        EnsureEyeMaterial(eyeRendererR, ref _eyeOriginalMaterialR, ref _eyeOwnedMaterialR);
    }

    private static void EnsureEyeMaterial(Renderer eyeRenderer, ref Material original, ref Material owned)
    {
        if (eyeRenderer == null) return;

        // 既に自分の所有インスタンスがある場合は、干渉しない。
        // ・Rendererがまだ自分のインスタンスを使っている → 何もしない（使い回す）
        // ・外部が別のマテリアルへ差し替えている → 上書きしない（意図を尊重する）
        if (owned != null) return;

        Material source = GetPrimarySharedMaterial(eyeRenderer);
        if (source == null)
        {
            Debug.LogWarning($"[MotherApproachController] Renderer '{eyeRenderer.name}' に" +
                             " sharedMaterial が無いため、目を発光できません。");
            return;
        }

        if (!source.HasProperty(EyeEmissionColorProperty))
        {
            Debug.LogWarning($"[MotherApproachController] Renderer '{eyeRenderer.name}' のマテリアルに" +
                             $" '{EyeEmissionColorProperty}' が無いため、その目は発光しません。");
            return;
        }

        // 生成前の元マテリアルを保存する（破棄時に参照を戻すため）。
        // ここへ来る時点で owned == null なので、source がそのまま「元マテリアル」になる。
        original = source;

        // 元マテリアルから自分の所有インスタンスを明示的に生成する（元アセットは変更しない）。
        owned = new Material(source)
        {
            name = source.name + " (Eye Glow, Owned)"
        };
        eyeRenderer.sharedMaterial = owned;
    }

    /// <summary>マテリアルスロットの先頭の共有マテリアルを返す（無ければ material を参照しない）。</summary>
    private static Material GetPrimarySharedMaterial(Renderer eyeRenderer)
    {
        Material[] shared = eyeRenderer.sharedMaterials;
        if (shared != null && shared.Length > 0 && shared[0] != null)
            return shared[0];
        return null;
    }

    /// <summary>
    /// 非表示にしたときの後始末。所有インスタンスは破棄せず、Rendererに割り当てたまま保持する。
    /// 破棄はコンポーネント破棄時（OnDestroy）に1回だけ集約する。
    /// </summary>
    private void ReleaseEyeMaterials()
    {
        // 非表示時は破棄しない（保持）。解放は OnDestroy に集約する。
        // _eyeOwnedMaterialX を null にしないのは、再表示時に EnsureEyeMaterials が
        // 同じインスタンスを再利用できるようにするため（新規生成を作らない）。
    }

    /// <summary>
    /// コンポーネント破棄時に、自分が生成した発光用マテリアルを1回だけ破棄する。
    ///
    /// ※ OnDestroyは「コンポーネントだけを削除した場合」にも実行され、Renderer（GameObject）が
    ///    同時に消えるとは限らない。そのため、破棄前にRendererの参照を元マテリアルへ戻し、
    ///    破棄済みマテリアルがRendererに残らないようにする。
    /// </summary>
    private void OnDestroy()
    {
        DestroyOwnedEyeMaterials();
    }

    /// <summary>自分が生成した発光用マテリアルを、参照復元 → 破棄の順で処理する。</summary>
    private void DestroyOwnedEyeMaterials()
    {
        DestroyOwnedEyeMaterial(eyeRendererL, _eyeOriginalMaterialL, ref _eyeOwnedMaterialL);
        DestroyOwnedEyeMaterial(eyeRendererR, _eyeOriginalMaterialR, ref _eyeOwnedMaterialR);
    }

    /// <summary>
    /// 所有インスタンスを破棄する。
    ///   1. Rendererがまだ自分のインスタンスを使っていれば、元マテリアルへ参照を戻す
    ///   2. Rendererが別のマテリアルへ変更されていれば、その参照には触れない
    ///   3. 自分のインスタンスを破棄する
    /// 所有判定は _eyeOwnedMaterialX だけで行い、sharedMaterial との一致は見ない。
    /// </summary>
    private static void DestroyOwnedEyeMaterial(Renderer eyeRenderer, Material originalMaterial,
        ref Material ownedMaterial)
    {
        Material owned = ownedMaterial;
        ownedMaterial = null;

        if (owned == null) return;

        // 1-2. Rendererが自分のインスタンスを使っているときだけ、元マテリアルへ戻す。
        //      外部が別マテリアルへ差し替えている場合は、その参照を上書きしない。
        if (eyeRenderer != null && ReferenceEquals(eyeRenderer.sharedMaterial, owned))
        {
            if (originalMaterial != null)
            {
                eyeRenderer.sharedMaterial = originalMaterial;
            }
            else
            {
                // 元が不明な場合は、破棄済み参照を残さないようマテリアルを空にする。
                eyeRenderer.sharedMaterial = null;
                Debug.LogWarning($"[MotherApproachController] Renderer '{eyeRenderer.name}' の" +
                                 "元マテリアルが不明なため、参照を空にしてから所有インスタンスを破棄します。");
            }
        }

        // 3. 自分が生成したインスタンスを破棄する（アセットには触れない）。
        if (Application.isPlaying) UnityEngine.Object.Destroy(owned);
        else UnityEngine.Object.DestroyImmediate(owned);
    }

    /// <summary>目の発光色をゲージに応じて更新する。値が変わらないときは書き込まない。</summary>
    private void UpdateEyeColor()
    {
        if (glowingEyesObject == null || !glowingEyesObject.activeSelf)
            return;

        EnsureEyeMaterials();

        Color glowColor = ColorForCurrentGauge();
        Color hdrGlowColor = ToHdrGlowColor(glowColor) * GetEyeGlowMultiplier();

        SetEyeEmissionColor(eyeRendererL, _eyeOwnedMaterialL, hdrGlowColor);
        SetEyeEmissionColor(eyeRendererR, _eyeOwnedMaterialR, hdrGlowColor);

        // 明滅中は毎フレーム値が変わるため、閾値判定用にはゲージ由来の色だけを記録する。
        _lastAppliedGlowColor = glowColor;
    }

    private Color ColorForCurrentGauge()
    {
        // 色の優先順位：
        //   1. メーターが紫の状態 → 紫
        //   2. それ以外 → 従来の黄／赤（赤は既存閾値ではなく、共通の色段階に合わせる）
        //
        // 閾値はMotherGauge.CurrentColorStateに一元化されているため、
        // 数値をここで推測せず、UIと同じ判定結果を使う。
        if (motherGauge != null)
        {
            switch (motherGauge.CurrentColorState)
            {
                case MotherGauge.SuspicionColorState.Purple: return purpleGlowColor;
                case MotherGauge.SuspicionColorState.Red: return dangerGlowColor;
                default: return normalGlowColor;
            }
        }

        // MotherGaugeが無い場合は従来どおりのフォールバック。
        return normalGlowColor;
    }

    private static Color ToHdrGlowColor(Color glowColor)
    {
        // 従来のHDR変換（2^3=8倍）を維持する。
        return glowColor * Mathf.Pow(2f, 3f);
    }

    /// <summary>Inspectorで調整する発光の強さ倍率。明滅が設定されていれば0.5〜1.0で揺らす。</summary>
    private float GetEyeGlowMultiplier()
    {
        float multiplier = Mathf.Max(0f, eyeEmissionIntensity);
        if (eyeGlowPulseSpeed > 0f)
            multiplier *= Mathf.Lerp(0.5f, 1f,
                Mathf.Sin(Time.unscaledTime * Mathf.PI * 2f * eyeGlowPulseSpeed) * 0.5f + 0.5f);
        return multiplier;
    }

    private static void SetEyeEmissionColor(Renderer eyeRenderer, Material eyeMaterial, Color glowColor)
    {
        if (eyeRenderer == null || eyeMaterial == null)
            return;

        // 値が変わっていなければ書き込まない（無駄なマテリアル更新を避ける）。
        if (eyeMaterial.HasProperty(EyeEmissionColorProperty) &&
            eyeMaterial.GetColor(EyeEmissionColorProperty) == glowColor)
            return;

        eyeMaterial.EnableKeyword(EyeEmissionKeyword);
        eyeMaterial.SetColor(EyeEmissionColorProperty, glowColor);
    }

    private void ShowMotherModel()
    {
        if (motherModelRoot != null)
        {
            motherModelRoot.SetActive(true);
            Debug.Log($"[MotherApproachController] 親機モデルの表示を復元 | object='{motherModelRoot.name}'");
        }

        if (motherModelRenderers != null)
        {
            foreach (var r in motherModelRenderers)
            {
                if (r == null) continue;
                r.enabled = true;
                Debug.Log($"[MotherApproachController] 親機モデルの表示を復元 | renderer='{r.name}'");
            }
        }
    }

    private void ResetStateFlags()
    {
        _routeExecutionFailed = false;
        IsApproaching = false;
        ReachedDoor = false;
        StoppedAtDoor = false;
        PassedByDoor = false;
        IsInHallwayPhase = false;
        IsRushIn = false;
        _isGardenPeeking = false;
        // サイクル初期化：スライドの予約・ずれを次サイクルへ持ち越さない。
        // 位置そのものは ResetApproach 側が startPoint へ戻すため、ここでは触らない。
        ResetDoorPeekSlideState();
        // サイクル初期化：Peek中の追加照明・常時照明の内部状態を消灯に戻す。
        SetPeekLighting(false);
        SetMotherStageLighting(false);

        // 部屋入室（案B）の状態を初期化する。_cycleStartedAsRushInはBeginApproach()で
        // ResetStateFlags()より前に設定されるため、ここではクリアしない。
        _doorRoutineActive = false;
        _roomEntryRequested = false;
        _roomPhaseActive = false;
        _leaveRoomRequested = false;

        // 帰路の状態も初期化する（リセット・中断時に古い要求/実行が残らないようにする）。
        // ReturnHomePhase を Idle に戻すことで、前サイクルの結果が次サイクルへ持ち越されない。
        // 中断フラグはここ（＝新しいサイクルの開始）で初めてクリアする。
        // 途中でクリアすると、消費後に残った帰路コルーチンが移動・旋回・完了通知を再開してしまう。
        ReturnHomePhase = ReturnHomeState.Idle;
        _returnHomeAbortFlag = false;

        MovementStateChanged?.Invoke(false);
        IsGardenRoute = false;

        // 片付け専用ルートの中断フラグをクリアする（新しいサイクル開始時に古い中断を持ち越さない）。
        // IsChoreRouteActive は ChoreRoutine 自身が管理するため、ここでは触らない
        // （BeginApproach から ChoreRoutine を起動する順序を壊さないため）。
        _choreRouteAborted = false;

        // 進行中の旋回コルーチンを止め、リセット後に回転指示が残らないようにする。
        StopRotateCoroutine();
    }

    private bool ValidateWaypoints()
    {
        if (startPoint == null)
        {
            Debug.LogWarning("[MotherApproachController] startPointがNULLです。", this);
            return false;
        }

        if (doorPoint == null)
        {
            Debug.LogWarning("[MotherApproachController] doorPointがNULLです。", this);
            return false;
        }

        // 中間ウェイポイントは List が空でも（=旧フィールド未設定でも）続行する。
        return true;
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  シーンギズモ
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>旋回／覗きの前提として「Idle Stateへ到達できたか」の結果。</summary>
    public struct IdleWaitResult
    {
        public bool Reached;
        public bool AlreadyIdle;
        public int FramesWaited;
    }

    /// <summary>
    /// 覗き開始前などに、Idle Stateへの到達を待つ上限フレーム数。
    /// 上限に達しても到達しなければ失敗として扱い、そのまま覗きを要求しない（安全のため）。
    /// ※ フレーム数なので実時間は可変（60fpsで約2秒、30fpsで約4秒。Time.timeScaleの影響も受ける）。
    /// </summary>
    [Tooltip("覗き開始前に Idle State への到達を待つ上限フレーム数。\n" +
             "上限に達しても到達しなければ、覗きを要求せず安全に中断します。\n" +
             "※ フレーム数なので実時間は可変です（60fpsで約2秒）。")]
    [SerializeField, Min(1)]
    private int maxFramesToReachIdle = 120;

    private const string IdleLayerName = "Base Layer";
    private const string IdleStateFullPath = "Base Layer.Idle";
    private const int IdleLayerIndex = 0;

    /// <summary>
    /// Door Peek State の正確なフルパス（使用中Controller Mother_Animation.controller）。
    /// 実アセットで確認済み：Base Layer 直下に State名 "Peek_Door" が存在する。
    /// 「IdleでもWindow Peekでもない」という消去法ではなく、このパスへ入ったことを直接確認する。
    /// </summary>
    private const string DoorPeekStateFullPath = "Base Layer.Peek_Door";

    // ── 目の発光（マテリアル）プロパティ名 ────────────────────────────────────
    // 判別は HasProperty で行うため、URP/Lit・Unlit などShader差があっても安全に扱える。
    private const string EyeEmissionColorProperty = "_EmissionColor";
    private const string EyeEmissionKeyword = "_EMISSION";


#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Transform prev = startPoint;

        // startPoint — 白
        if (startPoint != null)
        {
            Gizmos.color = Color.white;
            Gizmos.DrawSphere(startPoint.position, 0.08f);
        }

        // TurnPointより前の廊下ウェイポイント — 緑（登録順）
        Gizmos.color = Color.green;
        List<Transform> beforeTurnGizmo = BuildHallwayPath(hallwayPointsBeforeTurn);
        foreach (Transform wp in beforeTurnGizmo)
        {
            Gizmos.DrawSphere(wp.position, 0.06f);
            if (prev != null) Gizmos.DrawLine(prev.position, wp.position);
            prev = wp;
        }

        // turnPoint — 青（方向転換）
        if (turnPoint != null)
        {
            Gizmos.color = Color.blue;
            Gizmos.DrawSphere(turnPoint.position, 0.09f);
            if (prev != null) Gizmos.DrawLine(prev.position, turnPoint.position);
            prev = turnPoint;
        }

        // TurnPointより後の廊下ウェイポイント — 緑（登録順、ドア確認ルートのみ）
        Gizmos.color = Color.green;
        List<Transform> afterTurnGizmo = BuildHallwayPath(hallwayPointsAfterTurn);
        foreach (Transform wp in afterTurnGizmo)
        {
            Gizmos.DrawSphere(wp.position, 0.06f);
            if (prev != null) Gizmos.DrawLine(prev.position, wp.position);
            prev = wp;
        }

        // doorPoint — 黄
        if (doorPoint != null)
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawSphere(doorPoint.position, 0.09f);
            if (prev != null) Gizmos.DrawLine(prev.position, doorPoint.position);
            prev = doorPoint;
        }

        // フェイントの帰還ルート（TurnPoint → 前の点を逆順 → startPoint）— マゼンタ
        Gizmos.color = Color.magenta;
        for (int i = beforeTurnGizmo.Count - 1; i >= 0; i--)
        {
            Transform from = (i == beforeTurnGizmo.Count - 1 && turnPoint != null) ? turnPoint : beforeTurnGizmo[i + 1];
            Gizmos.DrawLine(from.position, beforeTurnGizmo[i].position);
        }

        if (beforeTurnGizmo.Count > 0 && startPoint != null)
            Gizmos.DrawLine(beforeTurnGizmo[0].position, startPoint.position);

        // フェイントA（HallwayPassBy）— シアン（TurnPoint → 後の点 → doorPoint通過 → hallwayPassByPoint）
        Gizmos.color = Color.cyan;
        if (hallwayPassByPoint != null)
        {
            Gizmos.DrawSphere(hallwayPassByPoint.position, 0.09f);
            Transform passPrev = (turnPoint != null) ? turnPoint : startPoint;
            foreach (Transform wp in afterTurnGizmo)
            {
                if (passPrev != null) Gizmos.DrawLine(passPrev.position, wp.position);
                passPrev = wp;
            }

            if (doorPoint != null)
            {
                if (passPrev != null) Gizmos.DrawLine(passPrev.position, doorPoint.position);
                passPrev = doorPoint;
            }

            if (passPrev != null) Gizmos.DrawLine(passPrev.position, hallwayPassByPoint.position);
        }

        // roomEntryPoints — 橙（doorPointからの入室ルート）
        if (roomEntryPoints != null && doorPoint != null)
        {
            Gizmos.color = new Color(1f, 0.5f, 0f);
            Transform prevRoom = doorPoint;
            foreach (Transform wp in roomEntryPoints)
            {
                if (wp == null) continue;
                Gizmos.DrawSphere(wp.position, 0.07f);
                Gizmos.DrawLine(prevRoom.position, wp.position);
                prevRoom = wp;
            }
        }
    }
#endif
}