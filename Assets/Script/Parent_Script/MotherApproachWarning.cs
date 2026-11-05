using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;

/// <summary>
/// MotherApproachWarning：
/// 接近前の演出（予告灯、遅延、速度スケーリング、ルート選択）をすべて管理する。
/// 大きな音による突入の入口も管理する（灯りと遅延を省略し、高速でDoorPeekを強制）。
///
/// 公開エントリーポイント：
///   StartWarningSequence()             — 通常の自動フロー（スケジューラーから呼び出し）
///   StartManualDoorWarningSequence()   — 1キーのデバッグ：完全な予告、DoorPeekを強制
///   StartManualHallwayPassByWarningSequence() — 2キーのデバッグ：完全な予告、フェイントA（ドア前を停止せず通過）を強制
///   StartManualGardenPassByWarningSequence() — 5キーのデバッグ：廊下灯1/2の予告後、庭側を通過
///   StartManualGardenPeekWarningSequence()   — 4キーのデバッグ：廊下灯1/2と庭灯の予告後、GardenPeekPointで庭を覗き、所定時間後に帰還
///   StartManualCatFeintWarningSequence()     — 3キーのデバッグ：ドア側の既存予告の後、猫だけが母親と同じ経路で歩きドアから覗く
///   TriggerInstantPassBy()             — 即時デバッグ通過、灯りと遅延なし
///   TriggerInstantDoor()               — 即時デバッグDoorPeek、灯りと遅延なし
///   StartLoudItemRushInSequence()      — 大きな音による突入：2階の灯りのみ、速度=loudItemRushInMoveSpeed
///   StopWarningSequence()              — 強制停止してリセット（ゲームオーバー、シーンアンロードなど）
///   EndWarningSequence()               — サイクル完了後にMotherSuspicionSystemが呼ぶ正常終了
///
/// 責務の境界：
///   MotherApproachWarning     — 灯り、遅延、速度、ルート確率、突入設定
///   MotherApproachController— 経路移動と向き
///   MotherSuspicionSystem       — ドア分岐、疑惑、部屋チェックの結果、捕獲
/// </summary>
public class MotherApproachWarning : MonoBehaviour
{
    // ── 主要な参照 ────────────────────────────────────────────────────────────
    [Header("参照")]
    [SerializeField] public MotherApproachController approachController;
    [SerializeField] public MotherSuspicionSystem        parentDetection;
    [SerializeField] public MotherGauge              motherGauge;

    // ── 予告灯 ────────────────────────────────────────────────────────────────
    [Header("予告灯")]
    [SerializeField] private GameObject hallwayLight1;   // Lights_Hallway
    [SerializeField] private GameObject hallwayLight2;   // Lights_Hallway 2
    [SerializeField] private GameObject hallwayLight3;   // Lights_Hallway 3
    [SerializeField] private GameObject frontLight;      // Lights_Front
    [SerializeField] private GameObject outsideLight;    // Lights_Outside

    [SerializeField] private AudioSource lightSwitchAudioSource;

    // ── 電気スイッチ音の段階別 音量係数（通常予兆用） ──────────────────────────
    //  ・再生音量係数であり、AudioSource の基準音量（m_Volume=1）と距離減衰に掛かる。
    //  ・共有 AudioSource.volume を都度書き換えず、再生のたびに係数を渡す（PlaySwitchSound）。
    //  ・試作初期値。最適値ではない（Inspector で調整する）。
    [Header("電気スイッチ音の音量係数（通常予兆）")]
    [Tooltip("第1段階（1階の灯り）のスイッチ音に掛ける音量係数。遠くで鳴った印象にするため小さめにする。" +
             "小さくしすぎると予兆に気づけないため 0 にはしない。")]
    [SerializeField, Range(0.05f, 1f)] private float lightSwitchStage1VolumeScale = 0.5f;

    [Tooltip("第2段階（2階の灯り／庭灯）のスイッチ音に掛ける音量係数。第1段階より近く・明瞭にする。")]
    [SerializeField, Range(0.05f, 1f)] private float lightSwitchStage2VolumeScale = 1f;

    // ── 母親の足音（MotherSuspicionSystem から移管） ─────────────────────────
    //   歩行の開始/停止・位置は MotherApproachController が通知し、
    //   音の再生・停止・音量・間隔は本クラス（MotherApproachWarning）が管理する。
    [Header("母親の足音")]
    [Tooltip("廊下ルート用の足音AudioSource（3D）。ループ再生で使う。")]
    [SerializeField] private AudioSource hallwayFootstepAudioSource;
    [Tooltip("庭ルート用の足音AudioSource（3D）。ループ再生で使う。")]
    [SerializeField] private AudioSource gardenFootstepAudioSource;

    [Tooltip("遠方（廊下の初期段階）での足音音量。")]
    [Range(0f, 1f)] [SerializeField] private float farVolume = 0.2f;
    [Tooltip("中間（廊下フェーズ）での足音音量。")]
    [Range(0f, 1f)] [SerializeField] private float midVolume = 0.5f;
    [Tooltip("ドア前（接近完了間際）での足音音量。")]
    [Range(0f, 1f)] [SerializeField] private float nearDoorVolume = 1f;
    [Tooltip("音量を目標値へ近づける速さ（毎秒）。")]
    [SerializeField] private float volumeChangeSpeed = 1f;

    [Tooltip("庭ルート（草）の足音だけに掛ける音量係数。1で廊下と同じ。庭が聞き取りづらい場合に1より大きくする" +
             "（音源の位置・Spatial Blend・段階差は変更しない）。")]
    [SerializeField, Range(0.1f, 3f)] private float gardenFootstepVolumeScale = 1.35f;

    // ── 庭足音の専用増幅ゲイン（AudioMixer 経由） ──────────────────────────────
    //  ・AudioSource.volume は 0〜1 にクランプされるため、1 に達した後は Mixer で増幅する。
    //  ・Range の上限を上げて飽和させる実装にはしない（Mixer の dB ゲインを使う）。
    //  ・Far/Mid/Close の段階制御とは分離する（段階はコード、ゲインは Mixer）。
    //  ・毎フレーム加算・累積しない（Start で1回だけ適用する）。
    //  ・庭音源にだけ適用する（廊下・スイッチ・BGM・その他SEへ影響させない）。
    [Header("庭足音の専用増幅ゲイン（AudioMixer・未接続なら無効）")]
    [Tooltip("庭足音専用の AudioMixerGroup。未接続なら庭専用ゲインは適用されず、gardenFootstepVolumeScale のみで調整される。" +
             "Editor で Mixer アセットと庭専用グループを作成し、ここへ割り当てる。")]
    [SerializeField] private UnityEngine.Audio.AudioMixerGroup gardenFootstepMixerGroup;

    [Tooltip("庭足音の増幅ゲイン（dB）。0 で従来の聞こえ方に戻る。試作範囲は 0〜+20dB、初期比較値は +6dB。" +
             "Mixer の Exposed Parameter として公開した名前を gardenFootstepGainParameter に設定する。")]
    [SerializeField, Range(0f, 20f)] private float gardenFootstepGainDb = 6f;

    [Tooltip("Mixer 側で Exposed Parameter として公開したパラメータ名（庭専用グループの音量）。")]
    [SerializeField] private string gardenFootstepGainParameter = "GardenFootstepGain";

    // ── 足音テンポ（ループ音源の pitch による刻み速度） ─────────────────────────
    //  ・廊下／庭の足音は「ループ AudioSource」のため、AudioSource.pitch で刻みを速くする。
    //    単発音の間隔制御ではないので、再生間隔側の調整は行わない（二重加速にしない）。
    //  ・pitch は音の高さも同時に変える（テンポ＝ピッチ。元pitch基準で掛けるため音程が上がる）。
    //  ・音源の元 pitch（Scene設定）を基準に掛けるので、1以外の設定も上書きしない。
    //  ・倍率は毎回「元pitch × 現在の倍率」で算出し、現在値へ掛けて累積させない。
    //  ・音量・3D設定・Mixerゲイン・音源位置は変更しない。
    [Header("足音テンポ（ループ音源のpitch・廊下／庭）")]
    [Tooltip("通常速度（基準移動速度）での足音テンポ倍率。1で従来（元pitch）。1より大きいほど刻みが速くなる。")]
    [SerializeField, Range(0.25f, 3f)] private float footstepTempoBaseMultiplier = 1.2f;

    [Tooltip("足音テンポ倍率の上限。移動速度が上がってもこの倍率を超えない（RushIn等の極端な早回しを防ぐ）。")]
    [SerializeField, Range(0.25f, 3f)] private float footstepTempoMaxMultiplier = 1.8f;

    [Tooltip("テンポが基本倍率（footstepTempoBaseMultiplier）になる基準の移動速度。" +
             "通常接近速度の最小（MotherSuspicionSystem.approachMoveSpeedMin の既定 15）に合わせる。これより速い移動でテンポが上がる。")]
    [SerializeField, Min(0.01f)] private float footstepTempoReferenceSpeed = 15f;

    // ── 予兆中の足音（庭イベントの接近感） ────────────────────────────────────
    //  ・イベントが確定した時点で分類（庭/廊下）を決める（接近開始後に立つ Controller フラグに依存しない）。
    //  ・予兆中は本クラスが音源と音量を一元管理し、UpdateMotherFootsteps は制御を譲る（二重制御の回避）。
    private bool _previewFootstepActive;    // 予兆中の足音を本クラスが駆動しているか
    private bool _previewFootstepIsGarden;  // 予兆の足音分類（庭=true / 廊下=false）
    private AudioSource _previewFootstepSource;

    // 通常イベント用の足音分類（予兆開始から通常イベント終了まで維持する）。
    //  ・一時的な IsGardenRoute=false（BeginApproach 直後など）で廊下音へ戻さないため。
    //  ・終了・取消・サイレン割り込み・ゲームオーバー・無効化（ResetFootstepStages）で解除する。
    private bool _activeFootstepRouteValid;
    private bool _activeFootstepIsGarden;

    /// <summary>自動抽選で確定したルートの足音分類（庭=true）。PrepareAutomaticRoute で確定する。</summary>
    private bool _preparedIsGardenFootstep;

    // ── 母親の物音・フェイント音（MotherSuspicionSystem から移管） ───────────
    [Header("母親の物音・フェイント音")]
    [Tooltip("ダミー（覗き見）ドアイベント発生時に再生。")]
    [SerializeField] private AudioSource dummyDoorAudioSource;
    [Tooltip("本チェック（全開）でドアが開いたときに再生。")]
    [SerializeField] private AudioSource mainDoorOpenAudioSource;
    [Tooltip("各イベント終了時にドアが閉じるときに再生。")]
    [SerializeField] private AudioSource mainDoorCloseAudioSource;
    [Tooltip("大きな音による突入が発生した直後に再生。")]
    [SerializeField] private AudioSource rushInAudioSource;
    [Tooltip("廊下を通過した後に再生する偽ドア音。")]
    [SerializeField] private AudioSource passByDoorAudioSource;
    [Tooltip("通過完了から偽ドア音を再生するまでの秒数。")]
    [SerializeField, Min(0f)] private float passByDoorSoundDelay = 1f;
    [Tooltip("庭の窓覗きで、窓の外に親が来たことに気づくための効果音。")]
    [SerializeField] private AudioSource windowPeekAudioSource;
    [Tooltip("窓覗きの効果音を、覗き開始から何秒後に再生するか（秒）。")]
    [SerializeField, Min(0f)] private float windowPeekSoundDelaySeconds = 1.7f;

    // ── 予兆の段階間の待機（旧: secondFloorDelay / approachDelay） ─────────────
    //   実際の待機は次の2つ。旧名は「2階」「接近」という場所名に依存していたため、
    //   現在の役割が分かる名前に変更した（秒数・待機順は変更していない）。
    // ── 予兆の合計時間（比較調整用） ──────────────────────────────────────────
    //  ・「最初の有効な灯り・音の予兆開始」から「接近移動／フェイント本体の開始」までの合計秒数。
    //  ・区間全体をこの1つの値として扱う（既存待機へ加算しない／灯り1段階ごとに与えない）。
    //  ・乱数による伸縮と、高疑惑による短縮は適用しない（通常疑惑・高疑惑とも同じ合計）。
    //  ・サイレン・片付け・捕獲演出はこの値を使わない（RunChoreForeshadowLights は従来のまま）。
    [Header("予兆の合計時間（比較調整用）")]
    [Tooltip("最初の灯り・音の予兆開始から、接近移動またはフェイント本体の開始までの合計秒数。" +
             "乱数伸縮・高疑惑短縮は適用しない。0以下や非有限は設定異常として既定値10秒を使う。")]
    [SerializeField, Min(0f)] private float foreshadowTotalSeconds = 10f;

    /// <summary>予兆の合計時間が不正だった場合に使う既定値（秒）。</summary>
    private const float ForeshadowTotalFallbackSeconds = 10f;

    /// <summary>
    /// 予兆区間の段階配分：第1段階の灯り → 第2段階の灯り の割合（残りが第2段階 → 本体開始）。
    ///  ・Inspector から調整する（初期値 0.5 ＝ 第1→第2 と 第2→本体 が半分ずつ）。
    ///  ・灯りと対応する電気音は同じ段階で発生させる（この比率は待機時間だけを決める）。
    /// </summary>
    [SerializeField, Range(0f, 1f)] private float foreshadowStage1Ratio = 0.5f;

    /// <summary>予兆区間の段階配分の比率が不正だった場合に使う既定値。</summary>
    private const float ForeshadowStage1RatioFallback = 0.5f;

    //     1) 第1段階の灯りON → 第2段階の灯りON まで
    //     2) 第2段階の灯りON → 移動開始（接近開始）まで
    [Header("予兆の段階間の待機（低疑惑）")]
    [Tooltip("第1段階の灯りを点けてから、第2段階の灯りを点けるまでの最小秒数。")]
    [FormerlySerializedAs("secondFloorDelayMin")]
    public float foreshadowStageIntervalMin = 1f;

    [Tooltip("第1段階の灯りを点けてから、第2段階の灯りを点けるまでの最大秒数。")]
    [FormerlySerializedAs("secondFloorDelayMax")]
    public float foreshadowStageIntervalMax = 10f;

    [Tooltip("第2段階の灯りを点けてから、移動（接近）を開始するまでの最小秒数。")]
    [FormerlySerializedAs("approachDelayMin")]
    public float moveStartDelayMin = 1f;

    [Tooltip("第2段階の灯りを点けてから、移動（接近）を開始するまでの最大秒数。")]
    [FormerlySerializedAs("approachDelayMax")]
    public float moveStartDelayMax = 3f;

    [Header("予兆の段階間の待機（高疑惑）")]
    [Tooltip("現在のゲージがこの閾値を超えた場合、以下の高疑惑用の待機範囲を使用する。")]
    public int highSuspicionDelayGaugeThreshold = 5;

    [Tooltip("ゲージが閾値を超えたときの、第1段階の灯りから第2段階の灯りまでの最小秒数。")]
    [FormerlySerializedAs("highSuspicionSecondFloorDelayMin")]
    public float highSuspicionForeshadowStageIntervalMin = 1f;

    [Tooltip("ゲージが閾値を超えたときの、第1段階の灯りから第2段階の灯りまでの最大秒数。")]
    [FormerlySerializedAs("highSuspicionSecondFloorDelayMax")]
    public float highSuspicionForeshadowStageIntervalMax = 3f;

    [Tooltip("ゲージが閾値を超えたときの、第2段階の灯りから移動（接近）開始までの最小秒数。")]
    [FormerlySerializedAs("highSuspicionApproachDelayMin")]
    public float highSuspicionMoveStartDelayMin;

    [Tooltip("ゲージが閾値を超えたときの、第2段階の灯りから移動（接近）開始までの最大秒数。")]
    [FormerlySerializedAs("highSuspicionApproachDelayMax")]
    public float highSuspicionMoveStartDelayMax = 1f;

    // ── ルート確率 ────────────────────────────────────────────────────────────
    [Header("ルート確率")]

    // ── 連続制限 ──────────────────────────────────────────────────────────────
    [Header("連続制限")]
    [Tooltip("同じルートが連続して選ばれる最大回数。1 = 連続禁止、2 = 2回まで連続可。")]
    [SerializeField, Min(1)] private int maxSameRouteStreak = 1;

    // ── 段階別候補（P15 第1段階） ─────────────────────────────────────────────
    /// <summary>進行段階。親機の経過時間で決まる（60秒／120秒境界は変更しない）。</summary>
    public enum RouteStage { Early = 0, Mid = 1, Late = 2 }

    /// <summary>
    /// 1つの段階における候補設定。
    ///  ・グループ確率（feintProbability）とグループ内の個別重み（*Weight）を分けて持つ。
    ///  ・有効／無効は *Enabled で切り替える（重みが0以下でも無効として扱う）。
    ///  ・初期値は現行相当（全5ルート有効・個別重み1）。
    /// </summary>
    [System.Serializable]
    public class StageRouteSettings
    {
        [Tooltip("この段階で Feint グループ（HallwayPassBy / GardenPassBy / CatFeint）を選ぶ確率（0〜1）。")]
        [Range(0f, 1f)] public float feintProbability = 0.5f;

        [Header("Peek グループ（個別重み）")]
        [Tooltip("DoorPeek を候補にするか。")]
        public bool doorPeekEnabled = true;
        [Tooltip("DoorPeek の個別重み。0以下なら候補から除外する。")]
        [Min(0f)] public float doorPeekWeight = 1f;

        [Tooltip("GardenPeek を候補にするか。")]
        public bool gardenPeekEnabled = true;
        [Tooltip("GardenPeek の個別重み。0以下なら候補から除外する。")]
        [Min(0f)] public float gardenPeekWeight = 1f;

        [Header("Feint グループ（個別重み）")]
        [Tooltip("HallwayPassBy を候補にするか。")]
        public bool hallwayPassByEnabled = true;
        [Tooltip("HallwayPassBy の個別重み。0以下なら候補から除外する。")]
        [Min(0f)] public float hallwayPassByWeight = 1f;

        [Tooltip("GardenPassBy を候補にするか。")]
        public bool gardenPassByEnabled = true;
        [Tooltip("GardenPassBy の個別重み。0以下なら候補から除外する。")]
        [Min(0f)] public float gardenPassByWeight = 1f;

        [Tooltip("CatFeint を候補にするか。")]
        public bool catFeintEnabled = true;
        [Tooltip("CatFeint の個別重み。0以下なら候補から除外する。")]
        [Min(0f)] public float catFeintWeight = 1f;
    }

    [Header("段階別候補（序盤／中盤／終盤）")]
    [Tooltip("親機の経過時間で決まる3段階の候補設定（序盤／中盤／終盤）。" +
             "未設定の要素は既定値（Feint確率 0.50／0.40／0.25・全5ルート有効・個別重み1）で補います。")]
    [SerializeField] private StageRouteSettings[] stageSettings;

    // ── 状態 ──────────────────────────────────────────────────────────────────
    [Header("状態")]
    [Tooltip("警告／接近シーケンス中はtrue。")]
    public bool isWarningActive;

    // ── 片付け演出との連携 ───────────────────────────────────────────────────
    /// <summary>
    /// 片付け演出（MotherChoreController）中は true。通常の警告開始をすべて抑止する。
    /// 設定は MotherChoreController.SetBlockedByChore() 経由で行う。
    /// </summary>
    private bool _choreBlocked;

    /// <summary>片付け演出中で通常の警告を抑止しているか。</summary>
    public bool IsChoreBlocked => _choreBlocked;

    /// <summary>
    /// ゲーム進行率（0〜1）。親機の時間管理から取得し、未配置時は 0 を返す。
    /// 親機側の参考値。片付け開始の判定は子機から届く実際の進行率のみで行い、この値では解禁しない。
    /// </summary>
    public float GameplayProgressRate =>
        ParentGameTimeManager.Instance != null ? ParentGameTimeManager.Instance.ProgressRate : 0f;

    [Header("ゲーム進行率")]
    [Tooltip("ゲーム進行率の計算に使う基準秒数（子機の制限時間と同じ240秒を想定）。")]
    [SerializeField, Min(1f)] private float gameplaySecondsForProgressRate = 240f;

    // ── 片付け演出の設定（ここを唯一の設定元にする） ─────────────────────────
    //   MotherChoreController / MotherApproachController はここを参照する
    //   （同じ設定を重複して持たない）。
    [Header("片付け演出")]
    [Tooltip("片付けの滞在時間（秒）。chorePoint到着・Chore開始から計測する。" +
             "【注意】これは上限ではなく「分散の目安」。MotherSuspicionSystem の片付け視線回数（回数保証）を" +
             "この時間内に終えられない場合は、時間を超えて残りの視線を完了してから Chore_End へ進む。")]
    [SerializeField, Min(1f)] private float choreStayDuration = 10f;

    [Tooltip("自然に覗く間隔の最小秒数。Chore中だけ有効。この範囲から次の覗きまでの間隔を抽選する。")]
    [SerializeField, Min(0f)] private float choreNaturalLookIntervalMin = 4f;

    [Tooltip("自然に覗く間隔の最大秒数。Chore中だけ有効。この範囲から次の覗きまでの間隔を抽選する。")]
    [SerializeField, Min(0f)] private float choreNaturalLookIntervalMax = 8f;

    [Tooltip("庭覗き（GardenPeek）でGardenPeekPointに留まる基本秒数。" +
             "実際の覗き時間 = この値 + GardenPeekPoint到着時のゲージ値（到着時に一度だけ決定）。")]
    [SerializeField, Min(0f)] private float gardenPeekDurationBase = 3f;

    /// <summary>片付けの滞在時間（秒）。MotherChoreController が参照する。</summary>
    public float ChoreStayDuration => choreStayDuration;

    /// <summary>自然に覗く間隔の最小秒数。</summary>
    public float ChoreNaturalLookIntervalMin => choreNaturalLookIntervalMin;

    /// <summary>自然に覗く間隔の最大秒数。</summary>
    public float ChoreNaturalLookIntervalMax => choreNaturalLookIntervalMax;

    /// <summary>庭覗きの基本時間（秒）。MotherApproachController が参照する。</summary>
    public float GardenPeekDurationBase => gardenPeekDurationBase;

    /// <summary>
    /// 自然に覗く次の間隔を抽選する（最小〜最大。順序が逆でも安全）。
    /// 不正値（両方0以下）の場合は0を返し、呼び出し側が「自然な覗きなし」として扱う。
    /// </summary>
    public float RollNaturalLookInterval()
    {
        float min = Mathf.Min(choreNaturalLookIntervalMin, choreNaturalLookIntervalMax);
        float max = Mathf.Max(choreNaturalLookIntervalMin, choreNaturalLookIntervalMax);

        if (max <= 0f)
        {
            Debug.LogWarning($"[MotherApproachWarning] 自然な覗きの間隔が0以下のため無効化します " +
                             $"(min={choreNaturalLookIntervalMin:F2}, max={choreNaturalLookIntervalMax:F2})", this);
            return 0f;
        }

        return Random.Range(min, max);
    }

    /// <summary>片付け演出の抑止を設定する（MotherChoreController から呼ばれる）。</summary>
    public void SetChoreBlocked(bool blocked)
    {
        if (_choreBlocked == blocked) return;
        _choreBlocked = blocked;
        Debug.Log($"[MotherApproachWarning] 片付け演出による警告抑止: {blocked}");

        // 抑止解除時は、進行中の予告があれば安全に停止して状態を残さない。
        if (!blocked)
        {
            // 何もしない（通常サイクル側が管理する）。
        }
    }

    // ── 現在のルート状態 ──────────────────────────────────────────────────────
    /// <summary>現在の実行で選択されたルート。移動開始前に設定され、シーケンス終了時に解除される。</summary>
    public enum RouteState { None, DoorPeek, HallwayPassBy, GardenPassBy, GardenPeek, CatFeint }
    public RouteState ActiveRoute { get; private set; } = RouteState.None;

    // ── 非公開 ───────────────────────────────────────────────────────────────
    private bool      _eventsSubscribed;
    private Coroutine _foreshadowCoroutine;

    /// <summary>廊下通過後の偽ドア音の遅延再生コルーチン（MotherSuspicionSystem から移管）。</summary>
    private Coroutine _passByDoorSoundCoroutine;

    /// <summary>窓覗きの効果音（覗き開始から windowPeekSoundDelaySeconds 後に再生）の遅延再生コルーチン。</summary>
    private Coroutine _windowPeekSoundCoroutine;
    private float     _gameplayElapsedSeconds;
    private bool      _gameplayClockActive;
    private int       _consecutiveAutomaticFeints;

    // 直前の自動抽選で確定したルート。連続制限の判定に使う。
    //  ・ActiveRoute はイベント終了時に None へ戻るため、直前ルートの記憶には使えない。
    private RouteState _lastChosenRoute = RouteState.None;

    // 同じルートが連続して確定した回数（1 = 1回目）。
    private int _sameRouteStreak = 0;

    // ── 段階別候補（P15 第1段階）：抽選を1回だけ行うための一時状態 ──────────
    // 自動開始の直前に確定したルート（コルーチンがそのまま消費する）。
    //  ・これが「予約」の実体。ClearPreparedRoute() 以外では立てない。
    private RouteState _preparedRoute = RouteState.None;
    private bool       _hasPreparedRoute;

    // ログ専用：直近に確定したグループ（予約の実体ではない。_hasPreparedRoute とは独立）。
    private bool _lastPickIsFeint;
    // ログ専用：自動抽選の通し番号（初回の抽選をログで判別するため。抽選には影響しない）。
    private int _automaticDrawCount;

    // 抽選に使う作業用リスト（毎回の抽選で Clear して再利用する）。
    private readonly List<RouteCandidate> _stagePeekPool = new List<RouteCandidate>();
    private readonly List<RouteCandidate> _stageFeintPool = new List<RouteCandidate>();
    private readonly List<RouteState>     _stageMissingRefs = new List<RouteState>();

    // 設定異常（重みの不正など）を1回だけ記録するためのフラグ。
    private bool _invalidWeightLogged;

    // 候補0件の理由を1回だけログするための記録（連投防止）。
    private RoutePickFailure _lastPickFailureLogged = RoutePickFailure.None;

    /// <summary>
    /// 短縮接近の足音段階通知を受け取る（MotherApproachController から呼ばれる）。
    ///  ・既存の ApplyFootstepStage を再利用する（段階進行の規則はそちらに一本化）。
    ///  ・Waypoint を省略するため、通過していない Waypoint の到達通知は偽装しない。
    ///  ・ApplyFootstepStage は「逆行は無視」するため、旧 Waypoint 通知と重複しても段階は戻らない。
    /// </summary>
    public void HandleShortApproachFootstepStage(FootstepStage stage, string reason)
    {
        ApplyFootstepStage(stage, reason);
    }

    /// <summary>
    /// 段階を進める（短縮接近用：waypoint を持たない）。既存の段階進行規則を共有する。
    ///  ・同じ段階・逆行では何もしない（ログも増やさない）。
    ///  ・ログは waypoint.name を参照しない専用の文言にする（実在の Waypoint を偽装しない）。
    /// </summary>
    private void ApplyFootstepStage(FootstepStage stage, string reason)
    {
        if (stage <= _footstepStage) return;

        _footstepStage = stage;

        Debug.Log($"[MotherApproachWarning] 足音段階（短縮接近）：{reason} → {FootstepStageName(stage)}" +
                  $" | イベント={CurrentEventLabel()} | 進行=行き | 固定={_choreCloseLocked}");
    }

    // ── 予兆中の足音（庭イベントの接近感） ────────────────────────────────────

    /// <summary>
    /// 予兆の足音分類（庭=true）を、イベント確定時点の情報から決める。
    ///  ・GardenPeek / GardenPassBy → 庭音（true）
    ///  ・DoorPeek / HallwayPassBy  → 廊下音（false）
    ///  ・CatFeint                  → 母親の足音を追加しない（null を返し、呼び出し側が開始しない）
    ///  ・接近開始後に立つ Controller フラグ（IsGardenRoute）には依存しない。
    /// </summary>
    private static bool IsGardenFootstepRoute(MotherApproachWarning.RouteState route)
    {
        return route == MotherApproachWarning.RouteState.GardenPeek ||
               route == MotherApproachWarning.RouteState.GardenPassBy;
    }

    /// <summary>
    /// この予兆で「庭側の照明ルール」を使うか（＝廊下照明を点灯しない）。
    ///  ・GardenPeek / GardenPassBy のみ true（足音分類 IsGardenFootstepRoute と同じルート判定）。
    ///  ・判定は予兆開始時点で確定済みのルート（routeOverride ／ 自動予約 _preparedRoute）で行う。
    ///  ・庭側は第2段階で outsideLight（庭灯）だけを点け、廊下照明・front は点けない。
    /// </summary>
    private static bool UsesGardenLighting(MotherApproachWarning.RouteState route)
    {
        return IsGardenFootstepRoute(route);
    }

    /// <summary>
    /// 予兆中の足音を開始する（スイッチ音と同時）。
    ///  ・分類に応じて庭/廊下の AudioSource を選び、Far から再生を始める。
    ///  ・音量と段階は本クラスが一元管理する（UpdateMotherFootsteps は _previewFootstepActive 中は制御しない）。
    /// </summary>
    private void StartPreviewFootstep(bool isGarden)
    {
        AudioSource source = isGarden ? gardenFootstepAudioSource : hallwayFootstepAudioSource;
        if (source == null) return;

        _previewFootstepActive = true;
        _previewFootstepIsGarden = isGarden;
        _previewFootstepSource = source;

        // 通常イベント用の分類として確定する（予兆開始から通常イベント終了まで維持）。
        _activeFootstepRouteValid = true;
        _activeFootstepIsGarden = isGarden;

        // 段階と音量は Far から始める（庭は係数を一度だけ適用した基準値を使う）。
        _footstepStage = FootstepStage.Far;
        _footstepCurrentVolume = PreviewStageVolume(FootstepStage.Far);
        _footstepTargetVolume = _footstepCurrentVolume;

        source.loop = true;
        source.volume = ParentAudioSettings.ApplySe(_footstepCurrentVolume);
        // 予感の足音は基本テンポで再生する（実移動の速度には連動しない）。
        ApplyFootstepPitch(source, Mathf.Max(0.01f, footstepTempoBaseMultiplier));
        if (!source.isPlaying) source.Play();

        // 反対側の音源は止める（廊下と庭の意図しない同時再生を防ぐ）。
        AudioSource other = isGarden ? hallwayFootstepAudioSource : gardenFootstepAudioSource;
        if (other != null) other.Stop();

        Debug.Log($"[MotherApproachWarning] 予兆の足音を開始（{(isGarden ? "庭" : "廊下")}） | " +
                  $"volume={_footstepCurrentVolume:F3}");
    }

    /// <summary>
    /// 予兆中の足音を毎フレーム更新する（段階へ滑らかに進める）。
    ///  ・現在の段階の目標音量へ volumeChangeSpeed で近づける（係数の累積はしない）。
    ///  ・段階は呼び出し側が SetPreviewFootstepStage で進める。
    /// </summary>
    private void UpdatePreviewFootstep()
    {
        if (!_previewFootstepActive) return;

        AudioSource source = _previewFootstepSource;
        if (source == null)
        {
            StopPreviewFootstep("音源が失われた");
            return;
        }

        // 目標音量は「現在の段階」の基準音量（庭は係数を一度だけ適用済み）。
        _footstepTargetVolume = PreviewStageVolume(_footstepStage);

        _footstepCurrentVolume = Mathf.MoveTowards(
            _footstepCurrentVolume, _footstepTargetVolume, volumeChangeSpeed * Time.deltaTime);
        source.volume = ParentAudioSettings.ApplySe(_footstepCurrentVolume);

        // 予感の足音は基本テンポで再生する（実移動の速度には連動しない）。
        ApplyFootstepPitch(source, Mathf.Max(0.01f, footstepTempoBaseMultiplier));
    }

    /// <summary>予兆の段階を進める（逆行しない）。</summary>
    private void SetPreviewFootstepStage(FootstepStage stage)
    {
        if (!_previewFootstepActive) return;
        if (stage <= _footstepStage) return;

        _footstepStage = stage;
        Debug.Log($"[MotherApproachWarning] 予兆の足音段階 → {FootstepStageName(stage)} | " +
                  $"target={PreviewStageVolume(stage):F3}");
    }

    /// <summary>予兆の段階に対する基準音量（庭は係数を一度だけ適用する）。</summary>
    private float PreviewStageVolume(FootstepStage stage)
    {
        float baseVolume = VolumeForStage(stage);
        return _previewFootstepIsGarden ? baseVolume * gardenFootstepVolumeScale : baseVolume;
    }

    /// <summary>
    /// 予兆の足音を停止して、実移動の足音制御へ引き継ぐ。
    ///  ・本体開始時に Far へ戻さない（既存の接近音処理が段階と音量を引き継ぐ）。
    ///  ・音源は止めない（同じループを実移動がそのまま使う）。
    /// </summary>
    private void HandOffPreviewFootstepToApproach()
    {
        if (!_previewFootstepActive) return;

        _previewFootstepActive = false;
        _previewFootstepSource = null;
        // _footstepStage / _footstepCurrentVolume はそのまま残し、実移動が引き継ぐ。

        Debug.Log($"[MotherApproachWarning] 予兆の足音を実移動へ引き継ぎ | " +
                  $"stage={_footstepStage} | volume={_footstepCurrentVolume:F3}");
    }

    /// <summary>
    /// 指定時間かけて予兆の足音段階を目標段階へ進める（滑らかな接近感）。
    ///  ・待機の開始時点で段階を進める（その後は UpdatePreviewFootstep が音量を追従）。
    ///  ・逆行は無視するため、Far を渡すと「段階を据え置いたまま待つ」指定になる。
    ///    通常イベントの予感は 第1段階=Far（据え置き）／第2段階=Mid で呼び、
    ///    Close へは本体側の接近通知（NotifyShortApproachFootstepStage）で進める。
    ///  ・実移動と同じ音量制御（volumeChangeSpeed）を使うため、跳ねや二重進行が起きない。
    /// </summary>
    private IEnumerator MovePreviewStageOverTime(float seconds, FootstepStage targetStage)
    {
        SetPreviewFootstepStage(targetStage);

        float remaining = Mathf.Max(0f, seconds);
        while (remaining > 0f)
        {
            remaining -= Time.deltaTime;
            yield return null;
        }
    }

    /// <summary>予兆の足音を停止する（終了・中断・取消・無効化）。</summary>
    private void StopPreviewFootstep(string reason)
    {
        if (!_previewFootstepActive) return;

        _previewFootstepActive = false;
        if (_previewFootstepSource != null)
        {
            _previewFootstepSource.Stop();
            // 次回開始時に速いテンポを持ち越さない（基本テンポへ戻す）。
            _previewFootstepSource.pitch = BasePitchFor(_previewFootstepSource) * footstepTempoBaseMultiplier;
        }
        _previewFootstepSource = null;
        _footstepTempoMultiplier = footstepTempoBaseMultiplier;

        Debug.Log($"[MotherApproachWarning] 予兆の足音を停止（{reason}）");
    }

    /// <summary>段階名をログ用に返す。</summary>
    private static string FootstepStageName(FootstepStage stage)
    {
        switch (stage)
        {
            case FootstepStage.Mid: return "Mid（中間）";
            case FootstepStage.Close: return "Close（ドア前）";
            default: return "Far（遠方）";
        }
    }

    // ── 予兆の合計時間（共通ヘルパー） ────────────────────────────────────────

    /// <summary>
    /// 予兆の合計時間（秒）を返す。実行開始時に1回だけ呼び、その値を実行内で使う。
    ///  ・0以下・NaN・±Infinity は設定異常として既定値（10秒）を使う。
    ///  ・乱数伸縮・高疑惑短縮は行わない（通常疑惑・高疑惑とも同じ合計）。
    /// </summary>
    private float ResolveForeshadowTotalSeconds()
    {
        float total = foreshadowTotalSeconds;

        if (float.IsNaN(total) || float.IsInfinity(total) || total <= 0f)
        {
            Debug.LogWarning($"[MotherApproachWarning] 予兆の合計時間が不正（{total}）のため既定値 " +
                             $"{ForeshadowTotalFallbackSeconds:F1}s を使用します");
            return ForeshadowTotalFallbackSeconds;
        }

        return total;
    }

    /// <summary>
    /// 予兆区間の待機を2つに配分する（第1段階→第2段階、第2段階→本体開始）。
    ///  ・合計が totalSeconds になるように配分する（既存待機へ加算しない）。
    ///  ・段階順序（第1段階 → 第2段階 → 本体）は保つ。
    ///  ・比率が不正（NaN/±Infinity）な場合は既定値 0.5 を使う。
    /// </summary>
    private void SplitForeshadowWaits(float totalSeconds, out float waitBeforeSecondStage, out float waitBeforeBody)
    {
        float ratio = foreshadowStage1Ratio;
        if (float.IsNaN(ratio) || float.IsInfinity(ratio))
        {
            Debug.LogWarning($"[MotherApproachWarning] 予兆の段階配分比率が不正（{ratio}）のため既定値 " +
                             $"{ForeshadowStage1RatioFallback:F2} を使用します");
            ratio = ForeshadowStage1RatioFallback;
        }

        ratio = Mathf.Clamp01(ratio);

        waitBeforeSecondStage = Mathf.Max(0f, totalSeconds) * ratio;
        waitBeforeBody = Mathf.Max(0f, totalSeconds) - waitBeforeSecondStage;
    }

    private void Start()
    {
        _gameplayElapsedSeconds = 0f;
        _gameplayClockActive = true;
        _consecutiveAutomaticFeints = 0;
        // 再プレイ・シーン再読み込みで連続制限の履歴を持ち越さない。
        _lastChosenRoute = RouteState.None;
        _sameRouteStreak = 0;
        // 段階別候補の一時状態も初期化する（前回の確定結果を持ち越さない）。
        ClearPreparedRoute();
        _lastPickIsFeint = false;
        _automaticDrawCount = 0;
        _invalidWeightLogged = false;
        _lastPickFailureLogged = RoutePickFailure.None;
        _stagePeekPool.Clear();
        _stageFeintPool.Clear();
        _stageMissingRefs.Clear();
        // 再プレイ・シーン再読み込みで片付け抑止が残らないようにする。
        _choreBlocked = false;

        // 足音の初期化（ループ設定・停止・初期音量）。MotherSuspicionSystem から移管。
        InitializeMotherFootsteps();

        // 庭足音の専用増幅ゲイン（Mixer）を1回だけ適用する。
        //  ・毎フレームの加算・累積を避けるため Start で1回だけ行う。
        //  ・未接続なら警告のみ（gardenFootstepVolumeScale のみで調整される）。
        ApplyGardenFootstepGainOnce();

        if (approachController == null)
            approachController = Object.FindFirstObjectByType<MotherApproachController>();

        if (parentDetection == null)
            parentDetection = Object.FindFirstObjectByType<MotherSuspicionSystem>();

        if (motherGauge == null)
            motherGauge = Object.FindFirstObjectByType<MotherGauge>();

        Debug.Log(
            $"[MotherApproachWarning] Start | approachController={(approachController != null ? approachController.name : "NULL")} | parentDetection={(parentDetection != null ? parentDetection.name : "NULL")} | motherGauge={(motherGauge != null ? motherGauge.name : "NULL")}"
        );

        SubscribeApproachEvents();
    }

    private void OnEnable()
    {
        SubscribeApproachEvents();
    }

    private void OnDisable()
    {
        UnsubscribeApproachEvents();
        // 中断・ゲームオーバー・シーン離脱で足音と予約音が残らないようにする。
        StopAllMotherSounds();
        // 未消費の予約も破棄する（無効化中に古い抽選結果を残さない）。
        ClearPreparedRoute();
    }

    private void Update()
    {
        if (_gameplayClockActive)
            _gameplayElapsedSeconds += Time.deltaTime;

        // 母親の足音の音量を毎フレーム更新する（MotherSuspicionSystem から移管）。
        UpdateMotherFootsteps();

        // デバッグ：2キーでフェイントA（HallwayPassBy）を手動起動する。
        // 親機デバッグキーは 1（母親ドア確認：ParentWarningScheduler）／2（本キー）／
        // 0（RushIn：MotherSuspicionSystem）／P（ドア開閉：DoorController）／I・O（疑惑±1：MotherGauge）で構成する。
        // 6キーは片付け演出の手動テスト開始（MotherChoreController.RequestManualStart）。
        if (Keyboard.current == null) return;

        if (Keyboard.current.digit6Key.wasPressedThisFrame)
        {
            Debug.Log("[MotherApproachWarning] 6キー押下 — 片付け演出の手動テスト開始を要求");
            if (parentDetection != null && parentDetection.RequestManualChoreStart())
                return;

            if (parentDetection == null)
                Debug.LogWarning("[MotherApproachWarning] 6キー：parentDetection が未設定のため片付けを開始できません");
        }

        if (Keyboard.current.digit2Key.wasPressedThisFrame)
        {
            Debug.Log("[MotherApproachWarning] 2キー押下 — フェイントA（HallwayPassBy）を手動起動");
            StartManualHallwayPassByWarningSequence();
        }

        if (Keyboard.current.digit3Key.wasPressedThisFrame)
        {
            Debug.Log("[MotherApproachWarning] 3キー押下 — 猫フェイント（CatFeint）を手動起動");
            StartManualCatFeintWarningSequence();
        }

        if (Keyboard.current.digit4Key.wasPressedThisFrame)
        {
            Debug.Log("[MotherApproachWarning] 4キー押下 — 庭側覗き（GardenPeek）を手動起動");
            StartManualGardenPeekWarningSequence();
        }

        if (Keyboard.current.digit5Key.wasPressedThisFrame)
        {
            Debug.Log("[MotherApproachWarning] 5キー押下 — 庭側素通り（GardenPassBy）を手動起動");
            StartManualGardenPassByWarningSequence();
        }

        // デバッグ：7キーは未使用（庭覗きの専用デバッグ起動は正式な4キーへ統合した）。
    }

    /// <summary>
    /// 本プレイが終了したか（新規の親イベントを開始してはいけない状態か）。
    ///  ・共通判定は ParentGameTimeManager.IsPlayFinished へ委譲する（参照を1本化）。
    ///  ・Manager 未配置（単体デバッグ）の場合は false（＝従来どおり動作を継続）。
    /// </summary>
    public bool IsPlayFinished =>
        ParentGameTimeManager.Instance != null && ParentGameTimeManager.Instance.IsPlayFinished;

    /// <summary>
    /// 自動開始を拒否すべきか（終了または判定不能）。共通窓口へ委譲する。
    ///  ・Warning 参照の有無に関係なく判定できる（null で共通判定を飛ばさない）。
    /// </summary>
    private static bool IsAutoStartRejected(out ParentGameTimeManager.AutoStartDecision decision)
    {
        decision = ParentGameTimeManager.EvaluateAutoStart();
        return decision == ParentGameTimeManager.AutoStartDecision.RejectPlayFinished ||
               decision == ParentGameTimeManager.AutoStartDecision.RejectUnavailable;
    }

    /// <summary>
    /// 本プレイ終了の通知。未消費の P15 予約を破棄する。
    ///  ・冪等（複数回呼ばれても安全）。
    ///  ・開始済みイベントの状態（isWarningActive / ActiveRoute / 灯り / 移動）には触らない。
    ///    進行中演出の終了処理は既存の所有者（MotherSuspicionSystem / MotherApproachController）に任せる。
    /// </summary>
    public void NotifyPlayFinished()
    {
        ClearPreparedRoute();
    }

    public void StartWarningSequence()
    {
        if (isWarningActive)
        {
            Debug.Log("[MotherApproachWarning] StartWarningSequence: BLOCKED — sequence already active");
            return;
        }

        // 片付け演出中は通常の警告を開始しない（重複防止）。
        if (_choreBlocked)
        {
            Debug.Log("[MotherApproachWarning] StartWarningSequence: BLOCKED — 片付け演出中");
            return;
        }

        // 本プレイ終了後は新しい親イベントを開始しない（予告・灯り・移動を一切開始しない）。
        //  ・判定不能（通常ゲームシーンで Manager 不在）も開始しない（終了ラッチは立てない）。
        if (IsAutoStartRejected(out var startDecision))
        {
            if (startDecision == ParentGameTimeManager.AutoStartDecision.RejectUnavailable)
                ParentGameTimeManager.LogUnavailableRejectOnce();

            Debug.LogWarning($"[MotherApproachWarning] StartWarningSequence: BLOCKED — 開始不可（{startDecision}）のため開始しません");
            ClearPreparedRoute();
            return;
        }

        if (!ValidateController()) return;

        // ── 段階と候補をここで1回だけ確定する（抽選途中で段階を読み直さない） ──
        //  ・候補0件の場合は isWarningActive を立てずに中止する。
        //    → 灯り・移動・コルーチンを一切開始しないため EndWarningSequence() を呼ばず、
        //      履歴もP13休憩も発生しない（通常の開始失敗に見せかけない）。
        //    → 次ウィンドウで自然に再試行される（スケジューラー側の変更は不要）。
        if (!PrepareAutomaticRoute())
        {
            return;
        }

        isWarningActive = true;
        Debug.Log("[MotherApproachWarning] WARNING START — beginning foreshadowing sequence");

        if (_foreshadowCoroutine != null) StopCoroutine(_foreshadowCoroutine);
        _foreshadowCoroutine = StartCoroutine(ForeshadowAndApproachCoroutine(RouteOverride.None));
    }

    /// <summary>
    /// 自動開始の前に、段階別候補から「今回のルート」を1回だけ確定する。
    ///  ・成功：戻り値 true（_preparedRoute に結果を保持し、コルーチンがそのまま使う）。
    ///  ・候補0件：戻り値 false。理由を区別してログする（同一理由の連投は抑制する）。
    ///  ・状態を変更しない（isWarningActive・灯り・移動・履歴はここでは触らない）。
    /// </summary>
    private bool PrepareAutomaticRoute()
    {
        // 前回の予約が残っていれば破棄する（未消費の予約を持ち越さない）。
        ClearPreparedRoute();

        RouteStage stage = ResolveStage(out float elapsedSeconds);

        // 設定の解決。未設定（旧シーン互換）と「設定済みだが不正」を区別する。
        if (!TryResolveStageSettings(stage, out StageRouteSettings settings, out RoutePickFailure settingsFailure))
        {
            LogRoutePickFailure(settingsFailure, stage, elapsedSeconds, forcedPeek: false);
            return false;
        }

        _stageMissingRefs.Clear();
        _stagePeekPool.Clear();
        _stageFeintPool.Clear();
        _invalidWeightLogged = false;

        BuildRouteCandidates(settings, _stagePeekPool, _stageFeintPool, _stageMissingRefs);

        // ── 診断ログ：候補の内訳（自動抽選ごとに1回だけ／乱数は消費しない）───────────
        //  ・draw#1 が「自然発生する最初のイベント」の抽選。
        //  ・peek/feint は「有効な候補と個別重み」、除外は「参照不足で落ちたルート」。
        //  ・どのルートが候補に無い場合は、除外（参照不足）／設定で無効／重み0 のいずれか。
        _automaticDrawCount++;
        int gaugeAtDraw = (motherGauge != null) ? motherGauge.currentGauge : -1;
        string suspicionStage = (motherGauge != null) ? motherGauge.CurrentColorState.ToString() : "n/a";
        Debug.Log($"[MotherApproachWarning] 自動抽選の候補 | draw#{_automaticDrawCount}" +
                  $" | stage={stage} | elapsed={elapsedSeconds:F2}s | gauge={gaugeAtDraw}（{suspicionStage}）" +
                  $" | peek=[{DescribeCandidates(_stagePeekPool)}]" +
                  $" | feint=[{DescribeCandidates(_stageFeintPool)}]" +
                  $" | 除外=[{(_stageMissingRefs.Count > 0 ? string.Join(",", _stageMissingRefs) : "なし")}]" +
                  $" | feintProbability={settings.feintProbability:F2}" +
                  $" | forcedPeek={_consecutiveAutomaticFeints >= 2}" +
                  $" | last={_lastChosenRoute} | streak={_sameRouteStreak}");

        // ── 検証順序（副作用より前）──
        // 1) 重みの不正（負値・NaN・±Infinity）は設定異常として記録する（候補からは除外済み）。
        if (_invalidWeightLogged)
        {
            LogRoutePickFailure(RoutePickFailure.InvalidWeight, stage, elapsedSeconds, forcedPeek: false);
            return false;
        }

        // 2) 候補が両グループとも空：定義されていない（設定異常）か、参照不足かを区別する。
        if (_stagePeekPool.Count == 0 && _stageFeintPool.Count == 0)
        {
            RoutePickFailure reason = _stageMissingRefs.Count > 0
                ? RoutePickFailure.MissingReference
                : RoutePickFailure.InvalidSettings;
            LogRoutePickFailure(reason, stage, elapsedSeconds, forcedPeek: false);
            return false;
        }

        // 3) 一時競合（接近中・帰路中）は開始できない。
        //    ここで弾くことで灯り・移動・速度などの副作用を起こさない。
        if (IsControllerBusy())
        {
            LogRoutePickFailure(RoutePickFailure.TransientBusy, stage, elapsedSeconds, forcedPeek: false);
            return false;
        }

        // 連続Feint制限（既存）：2連続したら Peek を強制する。
        bool forcePeek = _consecutiveAutomaticFeints >= 2;

        // 強制PeekでPeek候補が空なら開始しない（Feintへは切り替えない）。
        if (forcePeek && _stagePeekPool.Count == 0)
        {
            LogRoutePickFailure(RoutePickFailure.ForcedPeekUnavailable, stage, elapsedSeconds, forcedPeek: true);
            return false;
        }

        // 連続制限（P14）：上限に達している場合のみ回避対象を除外する。
        bool avoid = _lastChosenRoute != RouteState.None && _sameRouteStreak >= maxSameRouteStreak;
        RouteState avoidTarget = avoid ? _lastChosenRoute : RouteState.None;

        bool ok = TryPickRoute(settings, _stagePeekPool, _stageFeintPool,
                               forcePeek, avoid, avoidTarget,
                               out RouteState route, out bool isFeint,
                               out RoutePickFailure failure, out bool limitedBySameRoute);

        if (!ok)
        {
            LogRoutePickFailure(failure, stage, elapsedSeconds, forcePeek, limitedBySameRoute);
            return false;
        }

        _preparedRoute = route;
        _hasPreparedRoute = true;
        _lastPickIsFeint = isFeint;
        _lastPickFailureLogged = RoutePickFailure.None;

        // 予兆の足音分類を、イベント確定時点で決める（接近開始後に立つ Controller フラグに依存しない）。
        _preparedIsGardenFootstep = IsGardenFootstepRoute(route);

        Debug.Log($"[MotherApproachWarning] ChooseRoute | stage={stage} | elapsed={elapsedSeconds:F2}s | " +
                  $"feintProbability={settings.feintProbability:F2} | forcedPeek={forcePeek} | " +
                  $"avoidLast={(avoid ? _lastChosenRoute.ToString() : "none")} | " +
                  $"group={(isFeint ? "Feint" : "Peek")} | result={route}");
        return true;
    }

    /// <summary>予約を明示的に破棄する（準備開始・消費・停止・中断・無効化で使う）。</summary>
    private void ClearPreparedRoute()
    {
        _preparedRoute = RouteState.None;
        _hasPreparedRoute = false;
    }

    /// <summary>候補0件などの理由をログする（同一理由の連投は抑制。毎フレーム出力しない）。</summary>
    private void LogRoutePickFailure(RoutePickFailure failure, RouteStage stage, float elapsedSeconds,
                                     bool forcedPeek, bool limitedBySameRoute = false)
    {
        if (_lastPickFailureLogged == failure) return;   // 同一理由は1回だけ
        _lastPickFailureLogged = failure;

        string missing = _stageMissingRefs.Count > 0
            ? $" | missingRefs=[{string.Join(",", _stageMissingRefs)}]"
            : string.Empty;

        Debug.LogWarning($"[MotherApproachWarning] ChooseRoute REJECTED | reason={DescribeFailure(failure)} | " +
                         $"stage={stage} | elapsed={elapsedSeconds:F2}s | forcedPeek={forcedPeek} | " +
                         $"peekCandidates={_stagePeekPool.Count} | feintCandidates={_stageFeintPool.Count} | " +
                         $"limitedBySameRoute={limitedBySameRoute} | last={_lastChosenRoute} | " +
                         $"streak={_sameRouteStreak} | maxStreak={maxSameRouteStreak}{missing} " +
                         "— このウィンドウは発火せず、次ウィンドウで再試行します（履歴・休憩は変更しません）");
    }

    /// <summary>
    /// 【片付け用】通常の hallway イベントと同じ照明の予兆（点灯順・待ち時間）を再利用して実行する。
    ///  ・既存の TurnOnFirstStageLights / TurnOnSecondStageLights と
    ///    foreshadowStageInterval* / moveStartDelay*（＋高疑惑版）をそのまま使う。
    ///  ・片付け専用の秒数・照明設定はコピーして増やさない。
    ///  ・移動は開始しない（照明と待機だけ）。呼び出し元（MotherChoreController）が経路を開始する。
    ///  ・isManual=true（6キー）でも照明の予兆は省略しない。高疑惑の短縮判定のみ isManual に従う。
    /// </summary>
    public IEnumerator RunChoreForeshadowLights(bool isManual)
    {
        // ── 予兆の合計時間（通常予兆と同じ10秒・段階配分0.5）を使う ──
        //  ・既存のランダム予兆（foreshadowStageInterval* / moveStartDelay*）と
        //    高疑惑短縮は重ねない（通常予兆と同じ規則にそろえる）。
        //  ・スイッチ音も通常予兆と同じ段階別の音量係数を使う。
        float foreshadowTotal = ResolveForeshadowTotalSeconds();
        SplitForeshadowWaits(foreshadowTotal, out float stageIntervalDelay, out float moveStartDelay);
        Debug.Log($"[MotherApproachWarning] 片付け予兆区間: 合計={foreshadowTotal:F2}s " +
                  $"(第1→第2={stageIntervalDelay:F2}s / 第2→本体={moveStartDelay:F2}s)");

        // 第1段階の灯り（通常予兆と同じ）。
        TurnOnFirstStageLights();
        PlaySwitchSoundStage1();
        Debug.Log("[MotherApproachWarning] 片付け予兆: HALLWAY LIGHTS 1/2 ON");

        yield return new WaitForSeconds(stageIntervalDelay);

        // 第2段階の灯り（通常予兆と同じ）。
        TurnOnSecondStageLights();
        PlaySwitchSoundStage2();
        Debug.Log("[MotherApproachWarning] 片付け予兆: SECOND FLOOR LIGHTS ON");

        yield return new WaitForSeconds(moveStartDelay);
    }

    /// <summary>
    /// 片付けの予兆（灯り・足音）だけを閉じる（MotherChoreController が本体開始失敗時に使う）。
    ///  ・通常イベントの予告（isWarningActive）が進行中の場合は何もしない（他イベントをリセットしない）。
    ///  ・isWarningActive は触らない（片付けの予兆は isWarningActive を持たないため、
    ///    ここで false にすると他イベントの終了エッジと誤認される）。
    ///  ・サイレン・捕獲・通信・P13休憩・P14履歴には触れない（灯りと足音段階だけを戻す）。
    /// </summary>
    public void CloseChoreForeshadowLights(string reason)
    {
        if (isWarningActive) return;   // 通常イベントが動いている場合は触らない

        TurnOffAllLights();
        ResetFootstepStages($"片付けの予兆取消（{reason}）");
        Debug.Log($"[MotherApproachWarning] 片付けの予兆（灯り・足音）を取消しました（{reason}）");
    }

    public void StartManualDoorWarningSequence()
    {
        if (isWarningActive)
        {
            Debug.Log("[MotherApproachWarning] StartManualDoorWarningSequence: BLOCKED — sequence already active");
            return;
        }

        // 片付け演出中は通常の警告を開始しない（デバッグ操作も同様に抑止）。
        if (_choreBlocked)
        {
            Debug.Log("[MotherApproachWarning] StartManualDoorWarningSequence: BLOCKED — 片付け演出中");
            return;
        }

        if (!ValidateController()) return;

        // DoorPeek短縮接近の設定不備は、灯り・音より前に拒否する（旧方式へは戻さない）。
        if (!approachController.IsDoorPeekAvailableForAutoStart())
        {
            Debug.LogWarning("[MotherApproachWarning] DoorPeek：配置地点が未設定、または最低反応時間が不正のため開始しません" +
                             "（灯り・音は開始しません）", this);
            return;
        }

        // 手動へ切り替わるため、未消費の自動予約を破棄する（古い予約を使わない）。
        ClearPreparedRoute();

        isWarningActive = true;
        Debug.Log("[MotherApproachWarning] MANUAL ROUTE: M — DOOR/PEEK（短縮接近）");

        if (_foreshadowCoroutine != null) StopCoroutine(_foreshadowCoroutine);
        _foreshadowCoroutine = StartCoroutine(ForeshadowAndApproachCoroutine(RouteOverride.Door, true));
    }

    /// <summary>
    /// フェイントA（HallwayPassBy）の手動テスト起動：完全な予告（灯り2段階）の後に、
    /// ドアで停止せず通り過ぎるルートを強制する。2キーからも呼び出される。
    /// 通常抽選でも使用するHallwayPassByの手動確認。
    /// hallwayPassByPointが未設定の場合は警告を1回出して中止する。
    /// </summary>
    public void StartManualHallwayPassByWarningSequence()
    {
        if (isWarningActive)
        {
            Debug.Log("[MotherApproachWarning] StartManualHallwayPassByWarningSequence: BLOCKED — sequence already active");
            return;
        }

        if (_choreBlocked)
        {
            Debug.Log("[MotherApproachWarning] StartManualHallwayPassByWarningSequence: BLOCKED — 片付け演出中");
            return;
        }

        if (!ValidateController()) return;

        // HallwayPassBy短縮接近の設定不備は、灯り・音より前に拒否する（旧方式へは戻さない）。
        if (!approachController.IsHallwayPassByAvailableForAutoStart())
        {
            Debug.LogWarning("[MotherApproachWarning] HallwayPassBy：配置地点が未設定のため開始しません" +
                             "（灯り・音は開始しません）", this);
            return;
        }

        // 手動へ切り替わるため、未消費の自動予約を破棄する（古い予約を使わない）。
        ClearPreparedRoute();

        isWarningActive = true;
        Debug.Log("[MotherApproachWarning] MANUAL ROUTE: FEINT-A — HALLWAY PASS-BY（短縮接近）");

        if (_foreshadowCoroutine != null) StopCoroutine(_foreshadowCoroutine);
        _foreshadowCoroutine = StartCoroutine(ForeshadowAndApproachCoroutine(RouteOverride.HallwayPassBy, true));
    }

    /// <summary>
    /// 猫フェイントの手動テスト起動：ドア側の既存予告（灯り2段階）の後、猫だけが母親と同じ経路で
    /// DoorPointまで歩き、ドアが隙間だけ開いて猫が覗く。母親は移動せず、庭灯・庭waypointは使わない。
    /// 疑惑加算・捕獲判定・本チェックは発生しない。自動抽選には含まれない。
    /// </summary>
    public void StartManualCatFeintWarningSequence()
    {
        if (isWarningActive)
        {
            Debug.Log("[MotherApproachWarning] StartManualCatFeintWarningSequence: BLOCKED — sequence already active");
            return;
        }

        if (_choreBlocked)
        {
            Debug.Log("[MotherApproachWarning] StartManualCatFeintWarningSequence: BLOCKED — 片付け演出中");
            return;
        }

        if (!ValidateController()) return;

        // 手動へ切り替わるため、未消費の自動予約を破棄する（古い予約を使わない）。
        ClearPreparedRoute();

        isWarningActive = true;
        Debug.Log("[MotherApproachWarning] MANUAL ROUTE: CAT FEINT");

        if (_foreshadowCoroutine != null) StopCoroutine(_foreshadowCoroutine);
        _foreshadowCoroutine = StartCoroutine(CatFeintForeshadowCoroutine());
    }

    private IEnumerator CatFeintForeshadowCoroutine()
    {
        // ドア側と同じ既存予告：灯り1段階 → 遅延 → 灯り2段階 → 遅延。庭灯・庭waypointは使わない。
        TurnOnFirstStageLights();
        PlaySwitchSoundStage1();
        Debug.Log("[MotherApproachWarning] CAT FEINT: HALLWAY LIGHTS 1/2 ON");

        // 予兆区間の合計時間（比較調整用）。乱数伸縮・高疑惑短縮は適用しない。
        //  ・本体開始は「猫フェイントの実処理開始」＝ TriggerCatFeintEvent() とする。
        float foreshadowTotal = ResolveForeshadowTotalSeconds();
        SplitForeshadowWaits(foreshadowTotal, out float stageIntervalDelay, out float moveStartDelay);
        Debug.Log($"[MotherApproachWarning] CAT FEINT 予兆区間: 合計={foreshadowTotal:F2}s " +
                  $"(第1→第2={stageIntervalDelay:F2}s / 第2→本体={moveStartDelay:F2}s)");

        // CatFeint には母親の足音を追加しない（予兆の足音を開始しない）。
        yield return new WaitForSeconds(stageIntervalDelay);

        TurnOnSecondStageLights();
        PlaySwitchSoundStage2();
        Debug.Log("[MotherApproachWarning] CAT FEINT: SECOND FLOOR LIGHTS ON");

        yield return new WaitForSeconds(moveStartDelay);

        // 母親は移動させない。ドアの猫覗きイベントへ引き渡す（ドア開閉はPDが既存APIで行う）。
        ActiveRoute = RouteState.CatFeint;
        bool started = false;
        if (parentDetection != null)
            started = parentDetection.TriggerCatFeintEvent();
        else
            Debug.LogWarning("[MotherApproachWarning] parentDetection is NULL — TriggerCatFeintEventを呼べません", this);

        // 【開始失敗の後始末】開始できなかった場合は、この要求で確保した予兆状態（占有・灯り）を
        // 既存の終了処理（EndWarningSequence）で解除する（自動経路と同じ扱い）。
        //  ・ここへ来る時点の isWarningActive は「この要求が立てたもの」。
        //    別イベント進行中は入口（isWarningActive）で拒否されるため、他のイベントの照明・
        //    ドア・占有には触れない。
        //  ・EndWarningSequence は isWarningActive を見て二重終了しない（正常終了と衝突しない）。
        if (!started)
        {
            Debug.LogWarning("[MotherApproachWarning] CAT FEINT 開始結果=失敗 | route=CatFeint " +
                             "— 予兆の灯り・占有を取消します（自動経路と同じ後始末）");
            EndWarningSequence();
        }

        _foreshadowCoroutine = null;
    }

    /// <summary>
    /// 庭側素通りの手動テスト起動：廊下灯1/2の予告後に庭側を通過する。
    /// ドア停止イベントを発生させないため、疑惑・捕獲・ドア分岐は実行されない。
    /// </summary>
    public void StartManualGardenPassByWarningSequence()
    {
        if (isWarningActive)
        {
            Debug.Log("[MotherApproachWarning] StartManualGardenPassByWarningSequence: BLOCKED — sequence already active");
            return;
        }

        if (_choreBlocked)
        {
            Debug.Log("[MotherApproachWarning] StartManualGardenPassByWarningSequence: BLOCKED — 片付け演出中");
            return;
        }

        if (!ValidateController()) return;

        // GardenPassBy短縮接近の設定不備は、灯り・音より前に拒否する（旧方式へは戻さない）。
        if (!approachController.IsGardenPassByAvailableForAutoStart())
        {
            Debug.LogWarning("[MotherApproachWarning] GardenPassBy：配置地点が未設定のため開始しません" +
                             "（灯り・音は開始しません）", this);
            return;
        }

        // 手動へ切り替わるため、未消費の自動予約を破棄する（古い予約を使わない）。
        ClearPreparedRoute();

        isWarningActive = true;
        Debug.Log("[MotherApproachWarning] MANUAL ROUTE: GARDEN PASS-BY（短縮接近）");

        if (_foreshadowCoroutine != null) StopCoroutine(_foreshadowCoroutine);
        _foreshadowCoroutine = StartCoroutine(GardenPassByForeshadowCoroutine());
    }

    /// <summary>
    /// 庭側覗きの手動テスト起動：廊下灯1/2と庭灯の予告後に、GardenPeekPointで停止して庭を覗く。
    /// ドア停止イベント・ドア開閉は発生させず、覗き中の疑惑加算・捕獲判定はPD側で行う。
    /// 覗き時間は gardenPeekDurationBase+GardenPeekPoint到着時のゲージ値（controller側で到着時に一度だけ決定）。
    /// </summary>
    public void StartManualGardenPeekWarningSequence()
    {
        if (isWarningActive)
        {
            Debug.Log("[MotherApproachWarning] StartManualGardenPeekWarningSequence: BLOCKED — sequence already active");
            return;
        }

        if (_choreBlocked)
        {
            Debug.Log("[MotherApproachWarning] StartManualGardenPeekWarningSequence: BLOCKED — 片付け演出中");
            return;
        }

        if (!ValidateController()) return;

        // 終了・判定不能は自動と同じ窓口で確認する（終了後は開始しない）。
        //  ・旧7キーの排他・終了・判定不能ガードを4キーへ引き継ぐ。
        if (IsAutoStartRejected(out var decision))
        {
            if (decision == ParentGameTimeManager.AutoStartDecision.RejectUnavailable)
                ParentGameTimeManager.LogUnavailableRejectOnce();

            Debug.LogWarning($"[MotherApproachWarning] StartManualGardenPeekWarningSequence: BLOCKED — 開始不可（{decision}）");
            return;
        }

        // 手動へ切り替わるため、未消費の自動予約を破棄する（古い予約を使わない）。
        ClearPreparedRoute();

        isWarningActive = true;
        Debug.Log("[MotherApproachWarning] MANUAL ROUTE: GARDEN PEEK（短縮接近）");

        if (_foreshadowCoroutine != null) StopCoroutine(_foreshadowCoroutine);
        _foreshadowCoroutine = StartCoroutine(GardenPeekForeshadowCoroutine());
    }

    public void TriggerInstantPassBy()
    {
        if (isWarningActive) return;
        if (_choreBlocked) return;
        if (!ValidateController()) return;

        // 即時デバッグ開始へ切り替わるため、未消費の自動予約を破棄する。
        ClearPreparedRoute();

        // 配置地点・経路の不備は、灯り・音より前に拒否する。
        if (!approachController.IsShortApproachRouteReady(
                MotherApproachController.ShortApproachRouteKind.HallwayPassBy, out string rejectReason))
        {
            Debug.LogWarning($"[MotherApproachWarning] INSTANT DEBUG 開始拒否 — HallwayPassBy：{rejectReason}");
            return;
        }

        isWarningActive = true;
        ActiveRoute = RouteState.HallwayPassBy;
        Debug.Log("[MotherApproachWarning] INSTANT DEBUG: HALLWAY PASS-BY");

        parentDetection?.ApplyApproachSpeed(true);
        if (!approachController.StartApproachHallwayPassBy())
        {
            Debug.LogWarning("[MotherApproachWarning] INSTANT DEBUG 開始結果=失敗 — HallwayPassBy（このサイクルを終了します）");
            EndWarningSequence();
            return;
        }

        Debug.Log("[MotherApproachWarning] INSTANT DEBUG 開始結果=成功 — HallwayPassBy");
    }

    public void TriggerInstantDoor()
    {
        if (isWarningActive) return;
        if (_choreBlocked) return;
        if (!ValidateController()) return;

        // 即時デバッグ開始へ切り替わるため、未消費の自動予約を破棄する。
        ClearPreparedRoute();

        // 配置地点・経路の不備は、灯り・音より前に拒否する。
        if (!approachController.IsShortApproachRouteReady(
                MotherApproachController.ShortApproachRouteKind.DoorPeek, out string rejectReason))
        {
            Debug.LogWarning($"[MotherApproachWarning] INSTANT DEBUG 開始拒否 — DoorPeek：{rejectReason}");
            return;
        }

        isWarningActive = true;
        ActiveRoute = RouteState.DoorPeek;
        Debug.Log("[MotherApproachWarning] INSTANT DEBUG: DOOR/PEEK");

        parentDetection?.ApplyApproachSpeed(true);
        if (!approachController.StartApproachDoorOnly())
        {
            Debug.LogWarning("[MotherApproachWarning] INSTANT DEBUG 開始結果=失敗 — DoorPeek（このサイクルを終了します）");
            EndWarningSequence();
            return;
        }

        Debug.Log("[MotherApproachWarning] INSTANT DEBUG 開始結果=成功 — DoorPeek");
    }

    /// <summary>
    /// 大きな音による突入：1階の灯りと予告遅延を省略する。
    /// 2階の灯りだけを点灯し、RushIn本来の速度（loudItemRushInMoveSpeed）でDoorPeekルートを強制する。
    /// 音声とゲージの処理後にMotherSuspicionSystem.OnLoudItemTriggered()から呼び出される。
    /// 警告シーケンスがすでに進行中の場合は何もしない（二重起動防止）。
    /// </summary>
    public void StartLoudItemRushInSequence()
    {
        if (isWarningActive)
        {
            Debug.Log("[MotherApproachWarning] StartLoudItemRushInSequence: BLOCKED — sequence already active");
            return;
        }

        // 片付け演出中は突入も開始しない（重複防止。通常の親イベントと重複させない）。
        if (_choreBlocked)
        {
            Debug.Log("[MotherApproachWarning] StartLoudItemRushInSequence: BLOCKED — 片付け演出中");
            return;
        }

        if (!ValidateController()) return;

        // 配置地点・経路の不備は、灯り・音より前に拒否する（音だけの失敗を防ぐ）。
        if (!approachController.IsShortApproachRouteReady(
                MotherApproachController.ShortApproachRouteKind.DoorPeek, out string rushInRejectReason))
        {
            Debug.LogWarning($"[MotherApproachWarning] 突入（RushIn）開始拒否 — DoorPeek：{rushInRejectReason}" +
                             "（灯り・音は開始しません）");
            return;
        }

        // RushIn（自動抽選とは別の開始経路）へ切り替わるため、未消費の自動予約を破棄する。
        ClearPreparedRoute();

        isWarningActive = true;
        ActiveRoute     = RouteState.DoorPeek;

        if (approachController != null)
            approachController.IsRushIn = true;

        // 2階の灯りのみ — 突入時は1階の灯りを省略する。
        TurnOnSecondStageLights();
        if (lightSwitchAudioSource != null) lightSwitchAudioSource.Play();

        // 移動速度は RushIn 本来の制御箇所（メガホン専用の loudItemRushInMoveSpeed）を再び有効にする。
        // 通常接近（ApplyApproachSpeed）とは混同しない。
        if (parentDetection != null)
            parentDetection.SetLoudItemRushInSpeed(parentDetection.loudItemRushInMoveSpeed);

        Debug.Log($"[MotherApproachWarning] LOUD-ITEM RUSH-IN | speed={parentDetection?.loudItemRushInMoveSpeed} (loudItemRushInMoveSpeed) | route=DoorPeek");

        // 【RushInの予兆開始時刻】突入は通常予兆（10秒）と1階の灯りを省略するため、
        // この突入の開始時刻そのものを「この実行の予兆開始」として記録する。
        //  ・短縮接近の最低反応時間（doorPeekShortApproachMinReactionSeconds）は
        //    「予兆開始からの経過」で不足分だけ配置地点（不可視）で待つ仕様のため、
        //    記録が無い（0以下）と StartApproachDoorOnly が開始を拒否する。
        //  ・通常予兆の実行履歴に依存させない（新規Play直後・LOUD_ITEM先行でも突入を開始できるようにする）。
        //  ・通常予兆の開始時刻（ForeshadowAndApproachCoroutine）と同じく、実際の現在時刻を記録する
        //    （過去時刻の偽装はしない）。
        //  ・通常予兆10秒は挿入しない（突入の速度・照明・待機構成は従来どおり）。
        approachController.MarkShortApproachForeshadowStart(Time.time);

        // 本体開始：戻り値を確認する（開始失敗を成功として扱わない）。
        if (!approachController.StartApproachDoorOnly())
        {
            Debug.LogWarning("[MotherApproachWarning] 突入（RushIn）開始結果=失敗 — DoorPeek " +
                             "（自分が付けた灯り・速度上書き・RushInを閉じます）");

            // 自分が設定した突入状態だけを戻す（他のイベント・捕獲演出には触れない）。
            if (parentDetection != null)
                parentDetection.RestoreRushInSpeed("突入の開始失敗");

            approachController.IsRushIn = false;
            EndWarningSequence();
            return;
        }

        Debug.Log("[MotherApproachWarning] 突入（RushIn）開始結果=成功 — DoorPeek");
    }

    /// <summary>実行中の予告を強制停止し、EndWarningSequence()を呼び出す。</summary>
    public void StopWarningSequence()
    {
        // 予約（未消費の抽選結果）を明示的に破棄する（停止後に古い予約を使わない）。
        ClearPreparedRoute();

        if (_foreshadowCoroutine != null)
        {
            StopCoroutine(_foreshadowCoroutine);
            _foreshadowCoroutine = null;
        }
        CancelPassByDoorSound();

        Debug.Log("[MotherApproachWarning] WARNING STOPPED");
        TurnOffAllLights();
        // 中断：足音の到着段階と片付けClose固定を解除する（EndWarningSequenceが早期returnでも残さない）。
        ResetFootstepStages("警告の強制停止");
        EndWarningSequence();
    }

    public void NotifyGameOver()
    {
        _gameplayClockActive = false;
        // ゲームオーバー時は片付け抑止も解除しておく（停止状態を残さない）。
        _choreBlocked = false;
        // 捕獲（ゲームオーバー）：足音の到着段階と片付けClose固定も解除する。
        ResetFootstepStages("ゲームオーバー");
    }

    public void EndWarningSequence()
    {
        if (!isWarningActive) return;

        isWarningActive = false;
        ActiveRoute = RouteState.None;
        Debug.Log("[MotherApproachWarning] WARNING ENDED — resetting approach controller");

        // 正常終了：足音の到着段階と片付けClose固定を解除する（次イベントへ持ち越さない）。
        ResetFootstepStages("イベント終了");

        TurnOffAllLights();

        if (approachController != null)
            approachController.ResetApproach();
    }

    private enum RouteOverride { None, Door, HallwayPassBy }

    private IEnumerator GardenPassByForeshadowCoroutine()
    {
        // 灯りを点ける前に、共用配置地点（庭側）と必要な経路を検証する。
        // 未設定なら灯り・足音・移動を一切始めず、自分の予約だけを閉じて終了する。
        string passByRejectReason = null;
        if (approachController == null ||
            !approachController.IsShortApproachRouteReady(
                MotherApproachController.ShortApproachRouteKind.GardenPassBy, out passByRejectReason))
        {
            Debug.LogWarning("[MotherApproachWarning] GardenPassBy：開始拒否 — " +
                             (approachController == null ? "approachController が未設定" : passByRejectReason) +
                             "（灯り・足音は開始しません）", this);
            _foreshadowCoroutine = null;
            EndWarningSequence();
            yield break;
        }

        // 庭側イベント：廊下照明は点灯しない（点灯要求そのものを出さない）。
        //  ・第1段階のスイッチ音は廊下照明の点灯と対になるため、庭側では鳴らさない。
        //  ・庭灯（outsideLight）は既存どおり第2段階で点ける。
        Debug.Log("[MotherApproachWarning] GARDEN PASS-BY: 廊下照明は点灯しません（庭側の予兆のみ）");

        // 予兆区間の合計時間（比較調整用）。乱数伸縮・高疑惑短縮は適用しない。
        float foreshadowTotal = ResolveForeshadowTotalSeconds();
        SplitForeshadowWaits(foreshadowTotal, out float stageIntervalDelay, out float moveStartDelay);
        Debug.Log($"[MotherApproachWarning] GARDEN PASS-BY 予兆区間: 合計={foreshadowTotal:F2}s " +
                  $"(第1→第2={stageIntervalDelay:F2}s / 第2→本体={moveStartDelay:F2}s)");

        // 庭予兆の足音を開始する（第1段階の開始と同時）。庭素通りにも同じ原則を適用する。
        StartPreviewFootstep(isGarden: true);

        // 第1段階：Far のまま（予感側では段階を進めない）。
        yield return MovePreviewStageOverTime(stageIntervalDelay, FootstepStage.Far);

        SetLightActive(outsideLight, true);
        PlaySwitchSoundStage2();
        Debug.Log("[MotherApproachWarning] GARDEN PASS-BY: OUTSIDE LIGHT ON");

        // 第2段階の開始：Mid へ進める（予感側では Close まで進めない。音源は再起動しない）。
        yield return MovePreviewStageOverTime(moveStartDelay, FootstepStage.Mid);

        // 本体開始：音量を Far へ戻さず、既存の接近音処理へ引き継ぐ。
        HandOffPreviewFootstepToApproach();

        parentDetection?.ApplyApproachSpeed(true);
        ActiveRoute = RouteState.GardenPassBy;

        // 本体開始：戻り値を確認する（開始失敗を成功として扱わない）。
        bool started = approachController.StartApproachGardenPassBy();
        _foreshadowCoroutine = null;

        if (!started)
        {
            // 自分が流した予兆（灯り・足音）だけを閉じる。他のイベントや捕獲演出には触れない。
            Debug.LogWarning("[MotherApproachWarning] GardenPassBy：開始結果=失敗 — " +
                             "予兆の灯り・足音を取消し、このサイクルを終了します");
            EndWarningSequence();
        }
        else
        {
            Debug.Log($"[MotherApproachWarning] GardenPassBy：開始結果=成功 | route={ActiveRoute}");
        }
    }

    private IEnumerator GardenPeekForeshadowCoroutine()
    {
        // 灯りを点ける前に、共用配置地点（庭側）と必要な経路を検証する。
        //  ・GardenPeek は短縮接近のため、庭ルートの中間ウェイポイント（gardenRoutePoints）は使わない。
        //  ・配置設定の不備は、灯りより前に拒否する（自動・手動4キーとも同じ検証を使う）。
        string peekRejectReason = null;
        if (approachController == null ||
            !approachController.IsShortApproachRouteReady(
                MotherApproachController.ShortApproachRouteKind.GardenPeek, out peekRejectReason))
        {
            Debug.LogWarning("[MotherApproachWarning] GardenPeek：開始拒否 — " +
                             (approachController == null ? "approachController が未設定" : peekRejectReason) +
                             "（灯り・移動は開始しません）", this);
            _foreshadowCoroutine = null;
            EndWarningSequence();
            yield break;
        }

        // 予兆開始時刻をこの実行に紐づけて記録する（通常の自動と同じ扱い）。
        //  ・短縮接近の最低反応時間は「この実行の予兆開始」からの経過で判定する。
        //  ・Time.time を使い、既存待機（WaitForSeconds）と同じ時間基準にする。
        approachController.MarkShortApproachForeshadowStart(Time.time);

        // 庭側イベント：廊下照明は点灯しない（点灯要求そのものを出さない）。
        //  ・第1段階のスイッチ音は廊下照明の点灯と対になるため、庭側では鳴らさない。
        //  ・庭灯（outsideLight）は既存どおり第2段階で点ける。
        Debug.Log("[MotherApproachWarning] GARDEN PEEK: 廊下照明は点灯しません（庭側の予兆のみ）");

        // 予兆区間の合計時間（比較調整用）。乱数伸縮・高疑惑短縮は適用しない。
        float foreshadowTotal = ResolveForeshadowTotalSeconds();
        SplitForeshadowWaits(foreshadowTotal, out float stageIntervalDelay, out float moveStartDelay);
        Debug.Log($"[MotherApproachWarning] GARDEN PEEK 予兆区間: 合計={foreshadowTotal:F2}s " +
                  $"(第1→第2={stageIntervalDelay:F2}s / 第2→本体={moveStartDelay:F2}s)");

        // 庭予兆の足音を開始する（第1段階の開始と同時）。
        StartPreviewFootstep(isGarden: true);

        // 第1段階：Far のまま（予感側では段階を進めない）。
        yield return MovePreviewStageOverTime(stageIntervalDelay, FootstepStage.Far);

        SetLightActive(outsideLight, true);
        PlaySwitchSoundStage2();
        Debug.Log("[MotherApproachWarning] GARDEN PEEK: OUTSIDE LIGHT ON");

        // 第2段階の開始：Mid へ進める（予感側では Close まで進めない。音源は再起動しない）。
        yield return MovePreviewStageOverTime(moveStartDelay, FootstepStage.Mid);

        // 本体開始：音量を Far へ戻さず、既存の接近音処理へ引き継ぐ。
        HandOffPreviewFootstepToApproach();

        parentDetection?.ApplyApproachSpeed(true);

        // 覗き時間（gardenPeekDurationBase+GardenPeekPoint到着時のゲージ値）は
        // MotherApproachController側で覗き開始時に一度だけ決定するため、ここでは移動開始のみを行う。
        ActiveRoute = RouteState.GardenPeek;
        // 自動・手動4キーとも同じ正式方式（短縮接近）で開始する（戻り値を確認する）。
        bool started = approachController.StartApproachGardenPeek();
        _foreshadowCoroutine = null;

        if (!started)
        {
            // 自分が流した予兆（灯り・足音）だけを閉じる。他のイベントや捕獲演出には触れない。
            Debug.LogWarning("[MotherApproachWarning] GardenPeek：開始結果=失敗 — " +
                             "予兆の灯り・足音を取消し、このサイクルを終了します");
            EndWarningSequence();
        }
        else
        {
            Debug.Log($"[MotherApproachWarning] GardenPeek：開始結果=成功 | route={ActiveRoute}");
        }
    }

    private IEnumerator ForeshadowAndApproachCoroutine(RouteOverride routeOverride, bool isManual = false)
    {
        // フェイントA（HallwayPassBy）は、ドア側共通の配置地点と hallwayPassByPoint が前提。
        // 未設定の場合は灯り・足音・移動を一切始めずにサイクルを即終了する（音だけの失敗を防ぐ）。
        string hallwayRejectReason = null;
        if (routeOverride == RouteOverride.HallwayPassBy &&
            (approachController == null ||
             !approachController.IsShortApproachRouteReady(
                 MotherApproachController.ShortApproachRouteKind.HallwayPassBy, out hallwayRejectReason)))
        {
            Debug.LogWarning("[MotherApproachWarning] HallwayPassBy：開始拒否 — " +
                             (approachController == null ? "approachController が未設定" : hallwayRejectReason) +
                             "（灯り・足音は開始しません）", this);
            _foreshadowCoroutine = null;
            EndWarningSequence();
            yield break;
        }

        int gauge = (motherGauge != null) ? motherGauge.currentGauge : 0;

        // ── 予兆開始時刻をこの実行に紐づけて記録する ──
        //  ・短縮接近の最低反応時間は「この実行の予兆開始」からの経過で判定する。
        //  ・共有時刻（別イベントで上書きされ得るもの）には依存しない。
        //  ・Time.time を使い、既存待機（WaitForSeconds）と同じ時間基準にする。
        //  ・全ルートで記録する（GardenPeek 以外は使わないが、実行に紐づく値として常に更新する）。
        float foreshadowStartTime = Time.time;
        if (approachController != null)
        {
            approachController.MarkShortApproachForeshadowStart(foreshadowStartTime);
        }

        // ── この予兆のルートを「開始前」に確定する（照明と足音の共通判定）──
        //  ・routeOverride 優先（手動1/2キー）。それ以外は自動抽選で事前に確定した _preparedRoute。
        //  ・ここで確定したルートで照明を分岐する。本体開始後に立つ ActiveRoute や、
        //    前回イベントのルートには依存しない。照明のためにルートを抽選し直さない。
        RouteState previewRoute = routeOverride == RouteOverride.Door
            ? RouteState.DoorPeek
            : routeOverride == RouteOverride.HallwayPassBy
                ? RouteState.HallwayPassBy
                : _preparedRoute;
        bool previewGarden = IsGardenFootstepRoute(previewRoute);
        bool previewHasFootstep = previewRoute != RouteState.None && previewRoute != RouteState.CatFeint;
        bool gardenLighting = UsesGardenLighting(previewRoute);

        // ── 第1段階の照明 ──
        //  ・庭側（GardenPeek / GardenPassBy）は廊下照明を点けない。
        //    点灯要求そのものを出さない（一瞬点いて消える構造にしない）。
        //    第1段階のスイッチ音は廊下照明の点灯と対になるため、庭側では鳴らさない。
        //  ・ドア側（DoorPeek / HallwayPassBy / CatFeint / 突入）は従来どおり。
        if (gardenLighting)
        {
            Debug.Log($"[MotherApproachWarning] GARDEN SIDE FORESHADOW: 廊下照明は点灯しません | route={previewRoute}");
        }
        else
        {
            TurnOnFirstStageLights();
            PlaySwitchSoundStage1();
            Debug.Log("[MotherApproachWarning] FIRST FLOOR LIGHT ON");
        }

        // ── 予兆の合計時間（比較調整用）：区間全体を1つの値として扱う ──
        //  ・乱数伸縮・高疑惑短縮は適用しない（通常疑惑・高疑惑とも同じ合計）。
        //  ・実行開始時に1回だけ確定し、この実行内で使い回す。
        float foreshadowTotal = ResolveForeshadowTotalSeconds();
        SplitForeshadowWaits(foreshadowTotal, out float stageIntervalDelay, out float moveStartDelay);
        Debug.Log($"[MotherApproachWarning] 予兆区間: 合計={foreshadowTotal:F2}s " +
                  $"(第1→第2={stageIntervalDelay:F2}s / 第2→本体={moveStartDelay:F2}s) | gauge={gauge}");

        // ── 予兆の足音を開始する（第1段階の開始と同時） ──
        //  ・分類は「今回のイベントが確定した時点」の情報で決める（routeOverride 優先、次に自動予約）。
        //  ・CatFeint には母親の足音を追加しない（開始しない）。
        if (previewHasFootstep) StartPreviewFootstep(previewGarden);

        // 第1段階：Far のまま（予感側では段階を進めない）。
        yield return MovePreviewStageOverTime(stageIntervalDelay, FootstepStage.Far);

        // ── 第2段階の照明 ──
        //  ・庭側：廊下3・front は点けず、庭灯（outsideLight）だけを点ける（手動4/5キーと同じ）。
        //  ・ドア側：従来どおり廊下3・front を点ける。
        if (gardenLighting)
        {
            SetLightActive(outsideLight, true);
            PlaySwitchSoundStage2();
            Debug.Log("[MotherApproachWarning] GARDEN SIDE FORESHADOW: OUTSIDE LIGHT ON（廊下照明は点灯しません）");
        }
        else
        {
            TurnOnSecondStageLights();
            PlaySwitchSoundStage2();
            Debug.Log("[MotherApproachWarning] SECOND FLOOR LIGHTS ON");
        }

        // 第2段階の開始：Mid へ進める（予感側では Close まで進めない）。
        //  ・音源は再起動しない（音量だけが Mid へ追従し、本体へ引き継いだ後も鳴り続ける）。
        //  ・Close へは本体側の接近通知（NotifyShortApproachFootstepStage）で進める。
        yield return MovePreviewStageOverTime(moveStartDelay, FootstepStage.Mid);

        // 本体開始：音量を Far へ戻さず、既存の接近音処理へ引き継ぐ。
        HandOffPreviewFootstepToApproach();

        parentDetection?.ApplyApproachSpeed(isManual);

        RouteState chosenRoute;
        if (routeOverride == RouteOverride.Door)
        {
            chosenRoute = RouteState.DoorPeek;
        }
        else if (routeOverride == RouteOverride.HallwayPassBy)
        {
            chosenRoute = RouteState.HallwayPassBy;
        }
        else
        {
            chosenRoute = ChooseRoute();
        }

        ActiveRoute = chosenRoute;
        bool isAutomatic = !isManual && routeOverride == RouteOverride.None;

        // ── 候補0件（安全な中止）：通常の開始失敗に見せかけない ──
        //  ・自動抽選で候補が無かった場合、ChooseRoute は None を返す。
        //  ・EndWarningSequence() を呼ぶと isWarningActive の立ち下がりエッジが発生し、
        //    P13の休憩・強イベント判定が誤って走るため、ここでは呼ばない。
        //  ・履歴（_lastChosenRoute / _sameRouteStreak / _consecutiveAutomaticFeints）も更新しない。
        //  ・状態はここで静かに閉じる（isWarningActive は立てたままなので、スケジューラーの
        //    終了待ちが次フレームで解けるよう、フラグだけを戻す）。
        if (chosenRoute == RouteState.None)
        {
            Debug.LogWarning("[MotherApproachWarning] ROUTE CHOSEN | result=None — 候補0件のため" +
                             "このサイクルは発火せず静かに終了します（EndWarningSequence・履歴更新・休憩は行いません）");
            // 予兆で点けた灯りと足音段階だけを戻す（EndWarningSequence は呼ばない）。
            TurnOffAllLights();
            ResetFootstepStages("候補0件の静かな中止");
            isWarningActive = false;
            ActiveRoute = RouteState.None;
            _foreshadowCoroutine = null;
            yield break;
        }

        Debug.Log($"[MotherApproachWarning] ROUTE CHOSEN | mode={(isAutomatic ? "Automatic" : "Manual")} | " +
                  $"route={ActiveRoute} | consecutiveFeints={_consecutiveAutomaticFeints}");

        // ── 実際の開始API直前：開始可否を再確認する ──
        //  ・予告（灯り・待機）が済んでいても、終了／開始不可なら StartApproach*／TriggerCatFeintEvent を
        //    新しく呼ばない（移動・捕獲演出を開始しない）。
        //  ・「この予告の所有が自分にある」と安全に判定できる場合だけ、予告ぶんの後始末を閉じる。
        //  ・捕獲・RushIn・別イベント（片付け）へ所有が移っている場合は触らない
        //    （既存の取消・後始末を優先する）。
        //
        // 【所有判定の根拠（確認できる範囲のみ）】
        //  ・ここへ到達するのは ForeshadowAndApproachCoroutine（自動）または
        //    CatFeint/GardenPassBy/GardenPeek の各予告コルーチン。isAutomatic は
        //    「手動起動でも routeOverride でもない」場合のみ true。
        //  ・approachController == null は「Start* を呼べない」ため、この分岐で
        //    ownsForeshadow=true となっても移動は開始されない（安全側）。
        //    ただし「null だから所有している」という証明にはならない。
        //  ・parentDetection == null の場合、CatFeint は選択されない（ChooseRoute が
        //    CatFeint を返すには parentDetection が必要）が、これは DoorPeek /
        //    HallwayPassBy / GardenPassBy / GardenPeek の各ルートから本判定へ
        //    到達しないことの証明にはならない（他ルートは parentDetection 無しでも到達する）。
        //  ・RushIn は StopWarningSequence() が同期的に _foreshadowCoroutine を停止してから
        //    StartLoudItemRushInSequence() を呼ぶ（同一フレーム・この順序）。
        //  ・手動は isWarningActive ガードで自動予告中に入れない。
        //  ・片付けは while(isWarningActive) で予告の自発終了を待つ。
        //  ・開始時の ValidateController() は「呼び出し時点で approachController が
        //    非 null」を保証するだけで、待機後に参照が存在し続けることは保証しない
        //    （参照が後から失われる可能性はここでは判定していない）。
        if (IsAutoStartRejected(out var routeStartDecision))
        {
            bool ownsForeshadow = isAutomatic &&
                                  !_choreBlocked &&
                                  (approachController == null || !approachController.IsRushIn) &&
                                  (parentDetection == null ||
                                   (!parentDetection.isCaught && !parentDetection.HasPermanentGameOver));

            if (ownsForeshadow)
            {
                // 自分が所有する未開始予告だけを閉じる（移動・捕獲演出は開始していない）。
                TurnOffAllLights();
                ResetFootstepStages("開始不可による予告取消");
                ClearPreparedRoute();
                isWarningActive = false;   // 所有が自分にあるため、進行中演出を中断しない
                ActiveRoute = RouteState.None;
                _foreshadowCoroutine = null;

                Debug.Log("[MotherApproachWarning] ROUTE START SKIPPED — 開始不可のため開始APIを呼びません" +
                          $"（route={ActiveRoute} | decision={routeStartDecision} | ownsForeshadow=true | 予告の灯り・足音段階を取消）");
                yield break;
            }

            // 所有が移っている／判定できない場合は、状態を閉じずに開始APIを呼ばないだけにする。
            //  （isWarningActive は進行中演出の中断シグナルでもあるため、勝手に false にしない）
            Debug.LogWarning("[MotherApproachWarning] ROUTE START SKIPPED — 開始不可のため開始APIを呼びません" +
                             $"（route={ActiveRoute} | decision={routeStartDecision} | ownsForeshadow=false | 進行中演出の所有は既存処理に任せます）");
            yield break;
        }

        bool started;
        switch (ActiveRoute)
        {
            case RouteState.DoorPeek:
                started = approachController.StartApproachDoorOnly();
                break;

            case RouteState.HallwayPassBy:
                started = approachController.StartApproachHallwayPassBy();
                break;

            case RouteState.GardenPassBy:
                started = approachController.StartApproachGardenPassBy();
                break;

            case RouteState.GardenPeek:
                // 通常の自動 GardenPeek も、手動4キーと同じ正式方式（短縮接近）で開始する。
                started = approachController.StartApproachGardenPeek();
                break;

            case RouteState.CatFeint:
                if (parentDetection != null)
                    started = parentDetection.TriggerCatFeintEvent();
                else
                {
                    started = false;
                    EndWarningSequence();
                }
                break;

            default:
                started = false;
                break;
        }

        if (started && isAutomatic)
        {
            bool isFeint = ActiveRoute == RouteState.HallwayPassBy ||
                           ActiveRoute == RouteState.GardenPassBy ||
                           ActiveRoute == RouteState.CatFeint;
            _consecutiveAutomaticFeints = isFeint
                ? _consecutiveAutomaticFeints + 1
                : 0;

            // 連続制限用の履歴を更新する（実際に開始できた自動ルートのみ）。
            if (ActiveRoute == _lastChosenRoute)
            {
                _sameRouteStreak++;
            }
            else
            {
                _lastChosenRoute = ActiveRoute;
                _sameRouteStreak = 1;
            }

            Debug.Log($"[MotherApproachWarning] AUTOMATIC ROUTE COMMITTED | route={ActiveRoute} | " +
                      $"group={(isFeint ? "Feint" : "Peek")} | lastPickIsFeint={_lastPickIsFeint} | " +
                      $"consecutiveFeints={_consecutiveAutomaticFeints} | " +
                      $"sameRouteStreak={_sameRouteStreak}");
        }
        else if (!started)
        {
            Debug.LogWarning($"[MotherApproachWarning] {(isAutomatic ? "AUTOMATIC" : "MANUAL")} 開始結果=失敗 | " +
                             $"route={ActiveRoute} — 予兆の灯り・足音を取消し、履歴は更新しません（counter unchanged）");
            EndWarningSequence();
        }

        _foreshadowCoroutine = null;
    }

    // 自動抽選は、開始前に確定した段階別候補（_preparedRoute）をそのまま使う。
    //  ・抽選は StartWarningSequence() → PrepareAutomaticRoute() で1回だけ行う。
    //  ・ここでは段階を読み直さない（抽選途中の段階変化を防ぐ）。
    //  ・手動起動・RushIn・片付けはここを通らない。
    //  ・予約が無い場合は内部不整合として拒否する（保険の再抽選はしない）。
    private RouteState ChooseRoute()
    {
        if (!_hasPreparedRoute)
        {
            // 自動開始前の不整合。ここに到達する時点で副作用は起きていないため、
            // 灯り・音・速度・占有には触らず、理由を記録して拒否する。
            LogRoutePickFailure(RoutePickFailure.NoPreparedRoute, ResolveStage(out _), 0f, forcedPeek: false);
            return RouteState.None;
        }

        RouteState route = _preparedRoute;
        ClearPreparedRoute();   // 消費時に明示クリアする（予約を残さない）
        return route;
    }

    // ── 段階別候補（P15 第1段階） ─────────────────────────────────────────────

    /// <summary>候補抽選が失敗した理由。設定異常・参照不足・一時競合・連続制限を区別する。</summary>
    public enum RoutePickFailure
    {
        None = 0,
        /// <summary>候補が1つも定義されていない／全て無効・重み0（設定の問題）。</summary>
        InvalidSettings,
        /// <summary>必須参照（Transform等）が未設定で開始できない（参照の問題）。</summary>
        MissingReference,
        /// <summary>接近中・帰路中などで開始できない（一時的な競合）。</summary>
        TransientBusy,
        /// <summary>連続制限（同一ルート除外）で候補が0件になった。</summary>
        SameRouteLimited,
        /// <summary>重みに不正値（負値・NaN・±Infinity）がある（設定の問題）。</summary>
        InvalidWeight,
        /// <summary>強制Peek（Feint2連続）だが Peek 候補が無い（設定・参照の問題）。</summary>
        ForcedPeekUnavailable,
        /// <summary>設定済み配列の段階不足・null段階（編集済み設定の不正）。</summary>
        StageSettingsMissing,
        /// <summary>自動開始の直前に予約が無い（内部不整合）。</summary>
        NoPreparedRoute,
    }

    /// <summary>
    /// 現在の段階を決定する。基準は親機の経過時間（60秒／120秒境界は変更しない）。
    /// 親機タイマー未配置時のみ自前の経過時間へフォールバックする（P12の参照統一と同じ規則）。
    /// </summary>
    private RouteStage ResolveStage(out float elapsedSeconds)
    {
        elapsedSeconds = ParentGameTimeManager.Instance != null
            ? ParentGameTimeManager.Instance.ElapsedSeconds
            : _gameplayElapsedSeconds;

        if (elapsedSeconds < 60f) return RouteStage.Early;
        if (elapsedSeconds < 120f) return RouteStage.Mid;
        return RouteStage.Late;
    }

    /// <summary>
    /// 指定段階の候補設定を解決する。未設定（旧シーン互換）と「設定済みだが不正」を区別する。
    ///  ・stageSettings が null／空配列：旧シーン互換として現行相当（Feint確率 0.50／0.40／0.25・
    ///    全5ルート有効・個別重み1）を補完する。戻り値 true。
    ///  ・stageSettings に要素があるのに、対象段階が不足／null：
    ///    編集済み設定の不正として補完しない（戻り値 false・理由 StageSettingsMissing）。
    ///  ・要素がある：そのまま使う（全無効・全重み0 でも補完しない）。戻り値 true。
    /// </summary>
    private bool TryResolveStageSettings(RouteStage stage, out StageRouteSettings settings,
                                         out RoutePickFailure failure)
    {
        failure = RoutePickFailure.None;

        // ① 未設定（旧シーン互換）：null／空配列のみ補完する。
        if (stageSettings == null || stageSettings.Length == 0)
        {
            settings = new StageRouteSettings
            {
                feintProbability = stage == RouteStage.Early ? 0.50f
                                 : stage == RouteStage.Mid   ? 0.40f
                                                             : 0.25f,
            };
            return true;
        }

        // ② 設定済み配列の段階不足・null段階は補完しない（編集済み設定の不正）。
        if ((int)stage >= stageSettings.Length || stageSettings[(int)stage] == null)
        {
            settings = null;
            failure = RoutePickFailure.StageSettingsMissing;
            return false;
        }

        // ③ 要素がある：そのまま使う（全無効・全重み0 でも補完しない）。
        settings = stageSettings[(int)stage];
        return true;
    }

    /// <summary>候補1件（ルート・グループ・個別重み）。</summary>
    private struct RouteCandidate
    {
        public RouteState Route;
        public bool IsFeint;
        public float Weight;

        public RouteCandidate(RouteState route, bool isFeint, float weight)
        {
            Route = route;
            IsFeint = isFeint;
            Weight = weight;
        }
    }

    /// <summary>
    /// 段階設定から候補集合（Peek／Feint）を構築する。
    ///  ・有効／無効と重み（0以下は除外）を反映する。
    ///  ・必須参照が未設定のルートは「参照不足」として記録し、候補から除外する。
    ///  ・既存の必須参照は削除・変更しない（ここでは読み取りのみ）。
    /// </summary>
    private void BuildRouteCandidates(StageRouteSettings s,
                                      List<RouteCandidate> peekPool, List<RouteCandidate> feintPool,
                                      List<RouteState> missingRefs)
    {
        peekPool.Clear();
        feintPool.Clear();
        missingRefs.Clear();

        // 重みの検証（0は候補除外／正常。負値・NaN・±Infinity は設定異常として記録する）。
        void AddCandidate(bool enabled, float weight, RouteState route, bool isFeint, bool hasReference)
        {
            if (!enabled) return;

            if (!IsValidWeight(weight))
            {
                // 不正値は候補にしない（禁止ルートを復活させない）。理由を1回だけ記録する。
                if (!_invalidWeightLogged)
                {
                    _invalidWeightLogged = true;
                    Debug.LogWarning($"[MotherApproachWarning] ChooseRoute 設定異常：重みが不正なため候補から除外します" +
                                     $"（route={route} | weight={weight} | 有効なのは正の有限値のみ）");
                }
                return;
            }

            if (weight == 0f) return;   // 0 は候補除外（正常）

            if (hasReference)
            {
                if (isFeint) feintPool.Add(new RouteCandidate(route, true, weight));
                else peekPool.Add(new RouteCandidate(route, false, weight));
            }
            else
            {
                missingRefs.Add(route);
            }
        }

        // ── Peek グループ ──
        AddCandidate(s.doorPeekEnabled, s.doorPeekWeight, RouteState.DoorPeek, false, HasDoorPeekReference());
        AddCandidate(s.gardenPeekEnabled, s.gardenPeekWeight, RouteState.GardenPeek, false, HasGardenPeekReference());

        // ── Feint グループ ──
        AddCandidate(s.hallwayPassByEnabled, s.hallwayPassByWeight, RouteState.HallwayPassBy, true, HasHallwayPassByReference());
        AddCandidate(s.gardenPassByEnabled, s.gardenPassByWeight, RouteState.GardenPassBy, true, HasGardenPassByReference());
        AddCandidate(s.catFeintEnabled, s.catFeintWeight, RouteState.CatFeint, true, HasCatFeintReference());
    }

    /// <summary>重みとして有効か（正の有限値のみ有効。負値・NaN・±Infinity は不正）。</summary>
    private static bool IsValidWeight(float weight)
    {
        if (float.IsNaN(weight)) return false;
        if (float.IsInfinity(weight)) return false;
        return weight >= 0f;
    }

    // ── 必須参照の事前確認（読み取りのみ。移動・開始APIは呼ばない） ──────────
    //  ・配置地点は MotherApproachController の共用設定（ドア側／庭側）をそのまま使う。
    //  ・短縮接近は startPoint を経由しないため、startPoint 未設定は候補から除外しない。

    private bool HasDoorPeekReference() =>
        approachController != null &&
        approachController.doorPoint != null &&
        approachController.IsDoorPeekAvailableForAutoStart();

    private bool HasHallwayPassByReference() =>
        approachController != null &&
        approachController.hallwayPassByPoint != null &&
        approachController.IsHallwayPassByAvailableForAutoStart();

    private bool HasGardenPassByReference() =>
        approachController != null &&
        approachController.gardenPassByPoint != null &&
        approachController.IsGardenPassByAvailableForAutoStart();

    private bool HasGardenPeekReference() =>
        approachController != null &&
        approachController.gardenPeekPoint != null &&
        approachController.IsGardenPeekAvailableForAutoStart();

    // 通過区間（gardenRoutePoints の有効要素）の判定は、配置地点の判定と同じく
    // MotherApproachController.IsShortApproachRouteReady に集約している（二重に持たない）。

    private bool HasCatFeintReference() => parentDetection != null;

    /// <summary>
    /// 経路が一時的に使用中か（接近中・帰路中）。読み取りのみで、開始APIは呼ばない。
    /// 既存の Start* 側ガードと同じ条件を事前確認に使う（二重チェックは残す）。
    /// </summary>
    private bool IsControllerBusy()
    {
        if (approachController == null) return false;
        return approachController.IsApproaching || approachController.IsReturningHome;
    }

    /// <summary>
    /// 重み付き抽選（1パス）。合計が0以下／非有限なら false を返す（候補なし）。
    ///  ・呼び出し前に重みは「正の有限値」に検証済みである前提。
    ///  ・浮動小数の誤差で選ばれなかった場合も、末尾候補へ流さず false を返す（不正値の隠蔽を防ぐ）。
    /// </summary>
    private static bool TryPickWeighted(List<RouteCandidate> pool, out RouteCandidate picked)
    {
        picked = default;

        float total = 0f;
        for (int i = 0; i < pool.Count; i++)
        {
            if (pool[i].Weight > 0f) total += pool[i].Weight;
        }

        // 合計が壊れている（0以下・非有限）場合は候補なしとして扱う。
        if (!IsValidWeight(total) || total <= 0f) return false;

        float roll = Random.value * total;
        for (int i = 0; i < pool.Count; i++)
        {
            if (pool[i].Weight <= 0f) continue;
            roll -= pool[i].Weight;
            if (roll <= 0f)
            {
                picked = pool[i];

                // 診断：実際に使った重みと選択結果（自動抽選ごとに1回だけ）。
                Debug.Log($"[MotherApproachWarning] 重み付き抽選 | picked={picked.Route}" +
                          $" | weight={picked.Weight:F2} | total={total:F2} | candidates={pool.Count}");

                return true;
            }
        }

        // 誤差で決まらなかった場合：末尾候補へフォールバックせず失敗させる。
        return false;
    }

    /// <summary>プールから指定ルートを除外する。除外した件数を返す。</summary>
    private static int RemoveRoute(List<RouteCandidate> pool, RouteState target)
    {
        int removed = 0;
        for (int i = pool.Count - 1; i >= 0; i--)
        {
            if (pool[i].Route == target)
            {
                pool.RemoveAt(i);
                removed++;
            }
        }
        return removed;
    }

    /// <summary>拒否理由をログ用の短い文字列にする。</summary>
    private static string DescribeFailure(RoutePickFailure failure)
    {
        switch (failure)
        {
            case RoutePickFailure.InvalidSettings:        return "設定異常（候補が1つも有効でない／重みが全て0）";
            case RoutePickFailure.MissingReference:       return "参照不足（必須Transform・参照が未設定）";
            case RoutePickFailure.TransientBusy:          return "一時競合（接近中・帰路中など）";
            case RoutePickFailure.SameRouteLimited:       return "連続制限（同一ルート除外で候補0件）";
            case RoutePickFailure.InvalidWeight:          return "設定異常（重みに負値・NaN・±Infinity）";
            case RoutePickFailure.ForcedPeekUnavailable:  return "強制Peek不可（Peek候補が無い）";
            case RoutePickFailure.StageSettingsMissing:   return "設定異常（設定済み配列の段階不足・null段階）";
            case RoutePickFailure.NoPreparedRoute:        return "内部不整合（自動開始前の予約が無い）";
            default:                                      return "なし";
        }
    }

    /// <summary>
    /// グループ選択（feintProbability）とグループ内の重み付き抽選を行う。
    ///  ・グループは一度だけ選ぶ。空でも別グループへは切り替えない（第1段階の仕様）。
    ///  ・forcePeek=true のときはグループ選択を行わず Peek 固定（空なら失敗）。
    ///  ・avoidRoute=true のときは avoidTarget をプールから除外する（P14の連続制限を抽選前に統合）。
    ///  ・候補が0件の場合は false を返し、理由を out で返す（呼び出し側が安全に中止する）。
    ///  ・このメソッドの実行中は段階を読み直さない（引数の設定だけを使う）。
    ///  ・固定の代替プールは使わない（禁止ルートを復活させない）。
    /// </summary>
    private bool TryPickRoute(StageRouteSettings s,
                              List<RouteCandidate> peekPool, List<RouteCandidate> feintPool,
                              bool forcePeek, bool avoidRoute, RouteState avoidTarget,
                              out RouteState route, out bool isFeint,
                              out RoutePickFailure failure, out bool limitedBySameRoute)
    {
        route = RouteState.None;
        isFeint = false;
        failure = RoutePickFailure.None;
        limitedBySameRoute = false;

        // グループを一度だけ選ぶ。強制Peekではグループ選択を行わない。
        //  ・診断用に、実際に使った乱数値をそのまま保持する（乱数を余分に消費しない）。
        float clampFeintProbability = Mathf.Clamp01(s.feintProbability);
        float groupRoll = forcePeek ? -1f : Random.value;
        bool usePeek = forcePeek || groupRoll >= clampFeintProbability;
        List<RouteCandidate> pool = usePeek ? peekPool : feintPool;

        Debug.Log($"[MotherApproachWarning] グループ抽選 | feintProbability={clampFeintProbability:F2}" +
                  $" | roll={(forcePeek ? "forced" : groupRoll.ToString("F4"))}" +
                  $" | group={(usePeek ? "Peek" : "Feint")}" +
                  $" | peek候補={peekPool.Count} | feint候補={feintPool.Count}" +
                  $" | avoidLast={(avoidRoute ? avoidTarget.ToString() : "none")}");

        // 選択グループが空：別グループへは切り替えない。
        //  ・強制Peek（Feint2連続）で Peek が空：強制Peek不可として区別する。
        //  ・通常抽選で選ばれたグループが空：設定異常として区別する。
        if (pool.Count == 0)
        {
            failure = forcePeek ? RoutePickFailure.ForcedPeekUnavailable : RoutePickFailure.InvalidSettings;
            return false;
        }

        // 連続制限（P14）：回避対象を選択グループから除外する（別グループへは回さない）。
        if (avoidRoute)
        {
            RemoveRoute(pool, avoidTarget);

            if (pool.Count == 0)
            {
                limitedBySameRoute = true;
                failure = RoutePickFailure.SameRouteLimited;
                return false;
            }
        }

        if (!TryPickWeighted(pool, out RouteCandidate picked))
        {
            failure = RoutePickFailure.InvalidSettings;
            return false;
        }

        route = picked.Route;
        isFeint = picked.IsFeint;
        return true;
    }

    private void SetLightActive(GameObject lightObject, bool active)
    {
        if (lightObject != null) lightObject.SetActive(active);
    }

    /// <summary>
    /// 診断ログ用：候補プールを「Route(w=重み)」の並びにする（空なら「なし」）。
    ///  ・ログ専用。抽選・状態には影響しない。
    /// </summary>
    private static string DescribeCandidates(List<RouteCandidate> pool)
    {
        if (pool == null || pool.Count == 0) return "なし";

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < pool.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(pool[i].Route).Append("(w=").Append(pool[i].Weight.ToString("0.##")).Append(')');
        }

        return sb.ToString();
    }

    // 予告 第1段階：新部屋の Lights_Hallway / Lights_Hallway 2 を点灯する。
    private void TurnOnFirstStageLights()
    {
        SetLightActive(hallwayLight1, true);
        SetLightActive(hallwayLight2, true);
    }

    // ── 電気スイッチ音（段階別の音量係数で再生する共通ヘルパー） ──────────────

    // ── 庭足音の専用増幅ゲイン（AudioMixer 経由） ──────────────────────────────

    /// <summary>
    /// 庭足音の専用増幅ゲインを Mixer へ1回だけ適用する（Start から呼ぶ）。
    ///  ・AudioSource.volume は 0〜1 にクランプされるため、1 に達した後の増幅は Mixer で行う。
    ///  ・毎フレーム加算・累積しない（このメソッドは Start で1回だけ呼ぶ）。
    ///  ・庭音源にだけ適用する：庭音源の出力先を庭専用グループへ向け、そのグループの音量を設定する
    ///    （廊下・スイッチ・BGM・その他SEの AudioSource には触らない）。
    ///  ・未接続（Mixer／グループ／パラメータ名が未設定）の場合は「適用できない」と1回だけ警告し、
    ///    gardenFootstepVolumeScale のみで調整される（増幅できたとは扱わない）。
    /// </summary>
    private void ApplyGardenFootstepGainOnce()
    {
        if (gardenFootstepMixerGroup == null || gardenFootstepMixerGroup.audioMixer == null)
        {
            Debug.LogWarning("[MotherApproachWarning] 庭足音の専用ゲインは未接続のため適用されません" +
                             "（gardenFootstepMixerGroup 未設定）。gardenFootstepVolumeScale のみで調整されます。" +
                             "Editor で Mixer アセットと庭専用グループを作成し、割り当ててください。");
            return;
        }

        if (string.IsNullOrEmpty(gardenFootstepGainParameter))
        {
            Debug.LogWarning("[MotherApproachWarning] 庭足音の専用ゲインは未適用（パラメータ名が空）です。" +
                             "Mixer の Exposed Parameter 名を gardenFootstepGainParameter に設定してください。");
            return;
        }

        if (gardenFootstepAudioSource == null)
        {
            Debug.LogWarning("[MotherApproachWarning] 庭足音の AudioSource が未設定のため、専用ゲインを適用できません。");
            return;
        }

        // 庭音源だけを庭専用グループへ向ける（他の AudioSource は変更しない）。
        gardenFootstepAudioSource.outputAudioMixerGroup = gardenFootstepMixerGroup;

        // グループの音量を dB で設定する（0dB で従来の聞こえ方へ戻る）。
        if (gardenFootstepMixerGroup.audioMixer.SetFloat(gardenFootstepGainParameter, gardenFootstepGainDb))
        {
            Debug.Log($"[MotherApproachWarning] 庭足音の専用ゲインを適用: " +
                      $"{gardenFootstepGainParameter}={gardenFootstepGainDb:F1}dB（庭音源のみ）");
        }
        else
        {
            Debug.LogWarning($"[MotherApproachWarning] Mixer に '{gardenFootstepGainParameter}' が見つからないため、" +
                             "庭足音の専用ゲインを適用できません（Exposed Parameter として公開してください）。");
        }
    }

    /// <summary>
    /// スイッチ音を音量係数つきで1回再生する。
    ///  ・共有 AudioSource.volume を都度書き換えず、PlayOneShot に係数を渡す
    ///    （他演出の再生・停止や、次フレームの音量へ影響を残さない）。
    ///  ・既存の Play() と重ねて二重に鳴らさないよう、呼び出し側は本ヘルパーへ置き換える。
    ///  ・クリップ未設定・AudioSource未設定なら何もしない（null安全）。
    /// </summary>
    private void PlaySwitchSound(float volumeScale)
    {
        if (lightSwitchAudioSource == null) return;

        AudioClip clip = lightSwitchAudioSource.clip;
        if (clip == null) return;

        float scale = Mathf.Clamp01(volumeScale);
        lightSwitchAudioSource.PlayOneShot(clip, scale);
    }

    /// <summary>第1段階のスイッチ音（遠くで鳴った印象。小さめ）。</summary>
    private void PlaySwitchSoundStage1() => PlaySwitchSound(lightSwitchStage1VolumeScale);

    /// <summary>第2段階のスイッチ音（第1段階より近く・明瞭）。</summary>
    private void PlaySwitchSoundStage2() => PlaySwitchSound(lightSwitchStage2VolumeScale);

    // 予告 第2段階／突入の最終段階：新部屋の Lights_Hallway 3 / Lights_Front を点灯する。
    // 第1段階の灯りは消さない（積み上げ演出）。旧部屋の2階灯りがNoneの場合は何もしない。
    private void TurnOnSecondStageLights()
    {
        SetLightActive(hallwayLight3, true);
        SetLightActive(frontLight, true);
    }

    private void TurnOffAllLights()
    {
        SetLightActive(hallwayLight1, false);
        SetLightActive(hallwayLight2, false);
        SetLightActive(hallwayLight3, false);
        SetLightActive(frontLight, false);
        SetLightActive(outsideLight, false);
    }

    // ── 庭Peek専用：廊下・ドア前の灯りだけを消す ────────────────────────────────
    //  庭で母親が覗いている間に、廊下・ドア前まで点灯したままにならないようにする。
    //  ・outsideLight（庭灯）は操作しない。庭灯の点灯仕様（自動／手動の差）は変更しない。
    //  ・既存の共通点灯メソッド（TurnOnFirstStageLights / TurnOnSecondStageLights）の意味は変えない。
    //  ・通常終了時は既存の TurnOffAllLights（全消灯）がそのまま働く。
    /// <summary>
    /// 【庭Peek専用】廊下灯1・2・3と frontLight だけを消す（outsideLight は操作しない）。
    /// 呼び出し元は庭Peekの覗き開始通知（HandleGardenPeekStarted）のみ。
    /// GardenPassBy はこの通知を発生させないため、GardenPassBy の照明タイミングは変わらない。
    /// </summary>
    private void TurnOffHallwayAndFrontLights()
    {
        SetLightActive(hallwayLight1, false);
        SetLightActive(hallwayLight2, false);
        SetLightActive(hallwayLight3, false);
        SetLightActive(frontLight, false);
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  母親の足音の再生・停止・音量更新
    //   ・MotherApproachController が通知する歩行状態・位置から、
    //     本クラスが足音の再生/停止と音量を管理する。
    //   ・距離に応じた音量変化（ReachedDoor=近 / 廊下=中 / それ以外=遠）は従来どおり。
    //   ・猫専用の足音（CatFeintController）は対象外。
    // ──────────────────────────────────────────────────────────────────────────

    [Header("母親の足音の状態（読み取り専用）")]
    [Tooltip("現在の足音音量（デバッグ表示用）。")]
    [SerializeField] private float _footstepCurrentVolume;
    [SerializeField] private float _footstepTargetVolume;
    [Tooltip("現在の足音が庭ルート（草）か。")]
    [SerializeField] private bool _isGrassFootstepRoute;
    [Tooltip("現在、母親が歩行中か（MotherApproachController.MovementStateChanged の通知を保持）。")]
    [SerializeField] private bool _isMotherWalking;

    [Tooltip("現在適用中の足音テンポ倍率（読み取り専用・デバッグ表示用）。")]
    [SerializeField] private float _footstepTempoMultiplier = 1f;

    // 音源の元pitch（初期化時に捕捉。Scene側の設定を尊重し、一律1へ上書きしない）。
    private float _hallwayFootstepBasePitch = 1f;
    private float _gardenFootstepBasePitch = 1f;

    // ── 足音の到着段階（行きのみ。帰りは既存ルールを維持） ────────────────────
    /// <summary>行きの足音段階。イベント開始時に Far へ初期化する。</summary>
    public enum FootstepStage { Far = 0, Mid = 1, Close = 2 }

    [Tooltip("現在の到着段階（読み取り専用・デバッグ表示用）。")]
    [SerializeField] private FootstepStage _footstepStage = FootstepStage.Far;

    [Tooltip("片付け：ChoreApproachPoint_1 到着で true になり、イベント終了まで Close 固定を優先する（読み取り専用）。")]
    [SerializeField] private bool _choreCloseLocked;

    // 段階切替に使う到着地点（MotherApproachController から解決する。新しいInspector項目は増やさない）
    private Transform _stageHallwayClosePoint;
    private Transform _stageGardenClosePoint;
    private Transform _stageChoreClosePoint;
    private bool _stageWaypointsResolved;

    /// <summary>足音の初期化（Start時に一度）。ループ設定・停止・初期音量を適用する。</summary>
    public void InitializeMotherFootsteps()
    {
        _footstepCurrentVolume = farVolume;
        _footstepTargetVolume = farVolume;
        _isMotherWalking = false;
        _footstepTempoMultiplier = footstepTempoBaseMultiplier;
        // 音源の元pitchを捕捉する（Scene側で1以外が設定されていても尊重する）。
        _hallwayFootstepBasePitch = hallwayFootstepAudioSource != null ? hallwayFootstepAudioSource.pitch : 1f;
        _gardenFootstepBasePitch = gardenFootstepAudioSource != null ? gardenFootstepAudioSource.pitch : 1f;
        // 再プレイ・シーン再読み込みで前回の段階・Close固定を持ち越さない。
        ResetFootstepStages("足音の初期化");
        InitializeFootstepAudioSource(hallwayFootstepAudioSource);
        InitializeFootstepAudioSource(gardenFootstepAudioSource);
    }

    /// <summary>
    /// 足音を停止する（中断・ゲームオーバー・再プレイ・退場時に呼ぶ）。
    /// 音量状態も遠方へ戻し、次回開始時に違和感が出ないようにする。
    /// 到着段階・片付けClose固定もここで解除する（次イベントへ持ち越さない）。
    /// </summary>
    public void StopMotherFootsteps()
    {
        StopFootstepAudioSources();
        _footstepCurrentVolume = farVolume;
        _footstepTargetVolume = farVolume;
        _isMotherWalking = false;
        ResetFootstepStages("足音の停止");
    }

    /// <summary>
    /// MotherApproachController の歩行状態通知（MovementStateChanged）を受けて、
    /// 現在の歩行状態を保持する。通常の接近と片付けの行き・帰りの両方で使う。
    /// </summary>
    private void HandleMovementStateChanged(bool isWalking)
    {
        _isMotherWalking = isWalking;
    }

    /// <summary>
    /// 足音の音量を毎フレーム更新する（Update から呼ばれる）。
    ///  ・歩行状態は MotherApproachController.MovementStateChanged の通知で追跡し、
    ///     通常の接近に加えて片付けの行き・帰りの歩行も対象にする。
    ///  ・目標音量は次の優先順で決める（音量値・フェード速度は従来のまま）。
    ///      1) 片付け：ChoreApproachPoint_1 到着後は帰路を含めイベント終了まで Close 固定
    ///      2) 行き：到着通知で決まる段階（Far → TurnPoint=Mid → 各Close地点=Close）
    ///      3) 帰り：既存ルール（ReachedDoor／IsInHallwayPhase）を維持
    ///  ・歩行していない・突入中・AudioSource未設定の場合は停止する。
    /// </summary>
    private void UpdateMotherFootsteps()
    {
        // 予兆中は本クラス（予兆側）が音源と音量を一元管理する。
        //  ・UpdateMotherFootsteps は制御しない（二重制御・上書き・停止を避ける）。
        if (_previewFootstepActive)
        {
            UpdatePreviewFootstep();
            return;
        }

        bool shouldPlay = _isMotherWalking && approachController != null && !approachController.IsRushIn;

        // 通常イベント用の分類が有効な間は、それを使う。
        //  ・BeginApproach 直後は IsGardenRoute=false に初期化されるため、
        //    Controller フラグだけに任せると庭イベントで廊下音へ戻ってしまう。
        //  ・分類は予兆開始（StartPreviewFootstep）で確定し、ResetFootstepStages で解除される。
        //  ・サイレン・片付けは StartPreviewFootstep を呼ばないため、分類は無効のまま（既存処理へ戻る）。
        _isGrassFootstepRoute = _activeFootstepRouteValid
            ? _activeFootstepIsGarden
            : (approachController != null && approachController.IsGardenRoute);

        AudioSource activeSource = _isGrassFootstepRoute
            ? gardenFootstepAudioSource
            : hallwayFootstepAudioSource;

        // 目標音量（切替条件のみ変更。値・フェード速度は従来どおり）。
        //  ・庭ルート（草）だけ、聞き取りやすさのため係数を掛ける
        //    （音源位置・Spatial Blend・段階差は変更しない。廊下は係数1で従来どおり）。
        float routeVolumeScale = _isGrassFootstepRoute ? gardenFootstepVolumeScale : 1f;

        if (approachController != null)
        {
            if (_choreCloseLocked)
            {
                // 片付け：ChoreApproachPoint_1 到着後は帰路も含めて Close 固定を優先する。
                _footstepTargetVolume = nearDoorVolume;
            }
            else if (!approachController.IsReturnTripActive)
            {
                // 行き：到着通知で決まる段階を使う。
                _footstepTargetVolume = VolumeForStage(_footstepStage);
            }
            else
            {
                // 帰り：既存ルールを変更せず維持する。
                _footstepTargetVolume = approachController.ReachedDoor
                    ? nearDoorVolume
                    : approachController.IsInHallwayPhase
                        ? midVolume
                        : farVolume;
            }

            // 庭ルートの係数を適用する（廊下は1のため変化なし）。
            _footstepTargetVolume *= routeVolumeScale;
        }

        if (!shouldPlay || activeSource == null)
        {
            StopFootstepAudioSources();
            _footstepCurrentVolume = farVolume;
            _footstepTargetVolume = farVolume;
            return;
        }

        StopInactiveFootstepAudioSources(activeSource);
        if (!activeSource.isPlaying)
        {
            activeSource.loop = true;
            activeSource.volume = ParentAudioSettings.ApplySe(_footstepCurrentVolume);
            activeSource.Play();
        }

        // 移動速度に応じたテンポを適用する（停止・再生し直さず、pitch だけを毎フレーム更新する）。
        ApplyFootstepPitch(activeSource, ComputeFootstepTempoMultiplier());

        _footstepCurrentVolume = Mathf.MoveTowards(
            _footstepCurrentVolume, _footstepTargetVolume, volumeChangeSpeed * Time.deltaTime);
        activeSource.volume = ParentAudioSettings.ApplySe(_footstepCurrentVolume);
    }

    private void InitializeFootstepAudioSource(AudioSource source)
    {
        if (source == null) return;
        source.loop = true;
        source.Stop();
        source.volume = ParentAudioSettings.ApplySe(farVolume);
        // 開始時は基本テンポへそろえる（前回の速いテンポを持ち越さない）。
        source.pitch = BasePitchFor(source) * footstepTempoBaseMultiplier;
    }

    private void StopInactiveFootstepAudioSources(AudioSource activeSource)
    {
        AudioSource inactiveSource = activeSource == hallwayFootstepAudioSource
            ? gardenFootstepAudioSource
            : hallwayFootstepAudioSource;
        if (inactiveSource != null) inactiveSource.Stop();
    }

    private void StopFootstepAudioSources()
    {
        // 停止時にテンポ状態も基本倍率へ戻す（正常終了・中断・開始拒否で速いテンポを残さない）。
        if (hallwayFootstepAudioSource != null)
        {
            hallwayFootstepAudioSource.Stop();
            hallwayFootstepAudioSource.pitch = _hallwayFootstepBasePitch * footstepTempoBaseMultiplier;
        }

        if (gardenFootstepAudioSource != null)
        {
            gardenFootstepAudioSource.Stop();
            gardenFootstepAudioSource.pitch = _gardenFootstepBasePitch * footstepTempoBaseMultiplier;
        }

        _footstepTempoMultiplier = footstepTempoBaseMultiplier;
    }

    /// <summary>音源の元pitchを返す（初期化時に捕捉した値。対象外・未設定は1）。</summary>
    private float BasePitchFor(AudioSource source)
    {
        if (source == null) return 1f;
        if (source == hallwayFootstepAudioSource) return _hallwayFootstepBasePitch;
        if (source == gardenFootstepAudioSource) return _gardenFootstepBasePitch;
        return 1f;
    }

    /// <summary>
    /// 現在の移動速度から足音テンポ倍率を求める。
    ///  ・基準速度（footstepTempoReferenceSpeed）以下は基本倍率（footstepTempoBaseMultiplier）。
    ///  ・基準速度より速いほど倍率が上がり、上限（footstepTempoMaxMultiplier）で頭打ちにする。
    ///    RushIn速度(140)などを単純比例させず、極端な早回しにしない。
    ///  ・参照するのは「その移動で実際に使用中の速度」（CurrentApproachSpeed）だけ。
    ///    怪しさゲージは読まない（音側で速度を再計算しない）。
    ///  ・テレポート（座標差）や旋回・待機は速度として扱わない（呼び出し側が歩行中のみ適用する）。
    /// </summary>
    private float ComputeFootstepTempoMultiplier()
    {
        float baseTempo = Mathf.Max(0.01f, footstepTempoBaseMultiplier);
        float maxTempo = Mathf.Max(baseTempo, footstepTempoMaxMultiplier);
        float reference = Mathf.Max(0.01f, footstepTempoReferenceSpeed);

        float speed = approachController != null ? approachController.CurrentApproachSpeed : reference;
        float ratio = speed / reference;                 // 基準速度=1
        float tempo = baseTempo * Mathf.Max(1f, ratio);  // 基準速度以下は基本倍率のまま
        return Mathf.Clamp(tempo, baseTempo, maxTempo);
    }

    /// <summary>
    /// 足音のpitchを「元pitch × 倍率」で適用する（現在値へ掛けて累積させない）。
    ///  ・音源の停止・再生し直しは行わない（pitch の更新のみ）。
    ///  ・倍率はテンポ（刻み）とピッチ（音の高さ）を同時に変える。
    /// </summary>
    private void ApplyFootstepPitch(AudioSource source, float tempoMultiplier)
    {
        if (source == null) return;
        _footstepTempoMultiplier = tempoMultiplier;
        source.pitch = BasePitchFor(source) * tempoMultiplier;
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  足音の到着段階（行きのみ）
    //   ・MotherApproachController の到着通知（WaypointArrived）だけで段階を進める。
    //     Transform間の距離を毎フレーム判定しない。
    //   ・帰路の到着通知は行きの切替として処理しない（帰りは既存ルールを維持）。
    //   ・片付けは ChoreApproachPoint_1 到着で「イベント終了までClose固定」へ移行する。
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 段階切替に使う到着地点を MotherApproachController から解決する（Inspector項目は増やさない）。
    ///  ・廊下のClose地点        = hallwayPointsAfterTurn の 'HallwayPoint_4'
    ///  ・庭のClose地点          = gardenRoutePoints の 'GardenPoint_4'
    ///  ・片付けのClose固定地点  = choreApproachPoint_1
    /// 名前が一致しない地点は null のままにし、その地点による切替は行わない（別地点・配列indexで推測しない）。
    /// その場合は警告を1回だけ出す（解決は購読時の1回のみ）。
    /// </summary>
    private void ResolveFootstepStageWaypoints()
    {
        if (_stageWaypointsResolved || approachController == null) return;

        _stageHallwayClosePoint = FindStageWaypoint(approachController.hallwayPointsAfterTurn, "HallwayPoint_4");
        _stageGardenClosePoint = FindStageWaypoint(approachController.gardenRoutePoints, "GardenPoint_4");
        _stageChoreClosePoint = approachController.choreApproachPoint_1;

        _stageWaypointsResolved = true;

        Debug.Log(
            "[MotherApproachWarning] 足音段階の切替地点を解決 | " +
            $"廊下Close={NameOrNull(_stageHallwayClosePoint)} | " +
            $"庭Close={NameOrNull(_stageGardenClosePoint)} | 片付けClose固定={NameOrNull(_stageChoreClosePoint)}");

        if (_stageHallwayClosePoint == null)
        {
            Debug.LogWarning("[MotherApproachWarning] 足音段階：廊下のClose地点 'HallwayPoint_4' が hallwayPointsAfterTurn に見つからないため、" +
                             "廊下のClose切替は行いません（別地点・配列indexでの推測はしません）");
        }

        if (_stageGardenClosePoint == null)
        {
            Debug.LogWarning("[MotherApproachWarning] 足音段階：庭のClose地点 'GardenPoint_4' が gardenRoutePoints に見つからないため、" +
                             "庭のClose切替は行いません（別地点・配列indexでの推測はしません）");
        }

        if (_stageChoreClosePoint == null)
        {
            Debug.LogWarning("[MotherApproachWarning] 足音段階：片付けのClose固定地点（approachController.choreApproachPoint_1）が未設定のため、" +
                             "片付けのClose固定は行いません");
        }
    }

    private static string NameOrNull(Transform t) => t != null ? t.name : "NULL";

    /// <summary>
    /// 経路リストから指定名のTransformを探す。
    ///  ・見つからない場合は null を返す（別地点や配列indexでの推測はしない）。
    ///  ・この場合、その地点による段階切替は行わない（呼び出し元が警告を1回出す）。
    /// </summary>
    private static Transform FindStageWaypoint(IList<Transform> points, string waypointName)
    {
        if (points == null) return null;

        for (int i = 0; i < points.Count; i++)
        {
            Transform point = points[i];
            if (point != null && point.name == waypointName) return point;
        }

        return null;
    }

    /// <summary>
    /// 到着通知の受信（MotherApproachController.WaypointArrived）。
    ///  ・帰路（isReturnTrip=true）は行きの段階切替として扱わない。
    ///  ・同じ段階の再通知では状態変更もログも行わない。
    /// </summary>
    private void HandleWaypointArrived(Transform waypoint, bool isReturnTrip)
    {
        if (waypoint == null) return;

        // 帰路の到着通知は行きの切替として処理しない（帰りは既存ルールのまま）。
        if (isReturnTrip) return;

        // 片付け：ChoreApproachPoint_1 到着は「終了までClose固定」の契機（音量の再変更ではない）。
        if (_stageChoreClosePoint != null && waypoint == _stageChoreClosePoint)
        {
            LockChoreCloseUntilEventEnd(waypoint);
            return;
        }

        // Close固定中は段階を変更しない（固定を優先）。
        if (_choreCloseLocked) return;

        if (_stageHallwayClosePoint != null && waypoint == _stageHallwayClosePoint)
            ApplyFootstepStage(FootstepStage.Close, waypoint, "廊下");
        else if (_stageGardenClosePoint != null && waypoint == _stageGardenClosePoint)
            ApplyFootstepStage(FootstepStage.Close, waypoint, "庭");
    }

    /// <summary>段階を進める（同じ段階・逆行では何もしない＝ログも増やさない）。</summary>
    private void ApplyFootstepStage(FootstepStage stage, Transform waypoint, string routeLabel)
    {
        if (stage <= _footstepStage) return;

        _footstepStage = stage;

        Debug.Log(
            $"[MotherApproachWarning] 足音段階：{routeLabel} {waypoint.name}到着 → {FootstepStageLabel(stage)}" +
            $" | 到着地点='{waypoint.name}' | イベント={CurrentEventLabel()} | 進行=行き | 固定={_choreCloseLocked}");
    }

    /// <summary>片付け：Close固定を開始する（イベント終了まで解除しない）。</summary>
    private void LockChoreCloseUntilEventEnd(Transform waypoint)
    {
        bool stageChanged = _footstepStage != FootstepStage.Close;

        // 同じ通知の繰り返しではログを増やさない。
        if (_choreCloseLocked && !stageChanged) return;

        _footstepStage = FootstepStage.Close;
        _choreCloseLocked = true;

        Debug.Log(
            $"[MotherApproachWarning] 足音段階：片付け {waypoint.name}到着 → 終了までClose固定" +
            $" | 到着地点='{waypoint.name}' | イベント={CurrentEventLabel()} | 進行=行き | 段階=Close | 固定=true");
    }

    /// <summary>
    /// 段階とClose固定を初期化・解除する（イベント開始／正常終了／捕獲／中断／無効化／再プレイ）。
    /// 実際に変化があったときだけログを出す（同じ段階の再通知ではログを増やさない）。
    /// </summary>
    private void ResetFootstepStages(string reason)
    {
        // 予兆の足音が動いていれば停止する（終了・中断・取消・無効化で残さない）。
        //  ・実移動の足音（UpdateMotherFootsteps）は別経路で止まる（従来どおり）。
        StopPreviewFootstep(reason);

        // 通常イベント用の分類も解除する（サイレン・片付けは既存処理へ戻す）。
        //  ・「イベント開始」（HandleApproachStarted）から呼ばれる場合があるが、
        //    予兆→実移動の引き継ぎ済み（_activeFootstepRouteValid）のときは
        //    HandleApproachStarted 側が本メソッドを呼ばないため、分類は維持される。
        //  ・HandOff（予兆→実移動）では解除しない（本体中も同じ分類を使う）。
        _activeFootstepRouteValid = false;

        bool wasLocked = _choreCloseLocked;
        bool hadStage = _footstepStage != FootstepStage.Far;

        _footstepStage = FootstepStage.Far;
        _choreCloseLocked = false;

        if (wasLocked)
        {
            Debug.Log($"[MotherApproachWarning] 足音段階：片付けClose固定を解除（{reason}） | 段階=Far | 固定=false");
        }
        else if (hadStage)
        {
            Debug.Log($"[MotherApproachWarning] 足音段階をリセット（{reason}） → Far | 固定=false");
        }
    }

    /// <summary>段階に対応する音量を返す（値そのものはInspector設定のまま変更しない）。</summary>
    private float VolumeForStage(FootstepStage stage)
    {
        switch (stage)
        {
            case FootstepStage.Mid: return midVolume;
            case FootstepStage.Close: return nearDoorVolume;
            default: return farVolume;
        }
    }

    private static string FootstepStageLabel(FootstepStage stage)
    {
        switch (stage)
        {
            case FootstepStage.Mid: return "Mid";
            case FootstepStage.Close: return "Close";
            default: return "Far";
        }
    }

    /// <summary>ログ用のイベント種別（通常は RouteState。片付けルート進行中は +Chore を付ける）。</summary>
    private string CurrentEventLabel()
    {
        string label = ActiveRoute.ToString();
        if (approachController != null && approachController.IsChoreRouteActive)
            label += "+Chore";
        return label;
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  母親の物音・フェイント音の再生
    //   ・MotherSuspicionSystem から呼ばれる（再生タイミングは従来どおり）。
    //   ・AudioSource が未設定なら何もしない。
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>ダミー（覗き見）の偽ドア音を再生する。</summary>
    public void PlayDummyDoor() => dummyDoorAudioSource?.Play();

    /// <summary>ドアが開いたときの音を再生する（本チェック・ダミー共通）。</summary>
    public void PlayMainDoorOpen() => mainDoorOpenAudioSource?.Play();

    /// <summary>ドアが閉じたときの音を再生する（各イベント終了・退出）。</summary>
    public void PlayMainDoorClose() => mainDoorCloseAudioSource?.Play();

    /// <summary>大きな音による突入の直後に再生する。</summary>
    public void PlayRushIn() => rushInAudioSource?.Play();

    /// <summary>庭の窓覗きで、窓の外に親が来たことに気づくための効果音を1回再生する。</summary>
    public void PlayWindowPeek() => windowPeekAudioSource?.Play();

    /// <summary>
    /// 窓覗きの効果音を windowPeekSoundDelaySeconds 秒後に1回だけ再生する。
    ///  ・連続呼び出し時は前回の予約を破棄してから張り直す（PlayPassByDoorSound と同じ扱い）。
    ///  ・待機中に警告が終了（中断）した場合は再生しない（別イベントの音を遅れて鳴らさない）。
    /// </summary>
    public void ScheduleWindowPeekSound()
    {
        CancelWindowPeekSound();
        _windowPeekSoundCoroutine = StartCoroutine(WindowPeekSoundCoroutine());
    }

    /// <summary>予約済みの窓覗き効果音を取り消す（再生待ちも止める）。</summary>
    public void CancelWindowPeekSound()
    {
        if (_windowPeekSoundCoroutine == null) return;
        StopCoroutine(_windowPeekSoundCoroutine);
        _windowPeekSoundCoroutine = null;
    }

    private IEnumerator WindowPeekSoundCoroutine()
    {
        float delay = Mathf.Max(0f, windowPeekSoundDelaySeconds);
        Debug.Log($"[MotherApproachWarning] Window peek sound: waiting {delay:F1}s");
        yield return new WaitForSeconds(delay);

        // 待機中に中断（警告終了）した場合は再生しない。
        if (!isWarningActive)
        {
            Debug.Log("[MotherApproachWarning] Window peek sound: skipped — warning not active");
            _windowPeekSoundCoroutine = null;
            yield break;
        }

        Debug.Log("[MotherApproachWarning] Window peek sound: PLAY");
        PlayWindowPeek();
        _windowPeekSoundCoroutine = null;
    }

    /// <summary>
    /// 廊下通過後に再生する偽ドア音。passByDoorSoundDelay 秒遅延して1回だけ再生する。
    /// 連続呼び出し時は前回の予約を破棄してから張り直す。
    /// </summary>
    public void PlayPassByDoorSound()
    {
        CancelPassByDoorSound();
        _passByDoorSoundCoroutine = StartCoroutine(PlayPassByDoorSoundCoroutine());
    }

    /// <summary>予約済みの偽ドア音を取り消す。</summary>
    public void CancelPassByDoorSound()
    {
        if (_passByDoorSoundCoroutine == null) return;
        StopCoroutine(_passByDoorSoundCoroutine);
        _passByDoorSoundCoroutine = null;
    }

    private IEnumerator PlayPassByDoorSoundCoroutine()
    {
        float delay = Mathf.Max(0f, passByDoorSoundDelay);
        Debug.Log($"[MotherApproachWarning] Pass-by door sound: waiting {delay:F1}s");
        yield return new WaitForSeconds(delay);

        if (passByDoorAudioSource != null)
        {
            Debug.Log("[MotherApproachWarning] Pass-by door sound: PLAY");
            passByDoorAudioSource.Play();
        }

        _passByDoorSoundCoroutine = null;
    }

    /// <summary>足音・予約音をすべて止める（中断・ゲームオーバー・再プレイ・シーン離脱）。</summary>
    public void StopAllMotherSounds()
    {
        StopMotherFootsteps();
        CancelPassByDoorSound();
        CancelWindowPeekSound();
        // 予兆の足音も止める（中断・ゲームオーバー・シーン離脱で残さない）。
        StopPreviewFootstep("全音の停止");
    }

    private void SubscribeApproachEvents()
    {
        if (_eventsSubscribed || approachController == null) return;

        // 段階切替に使う到着地点を先に解決する（新しいInspector項目は増やさない）。
        ResolveFootstepStageWaypoints();

        approachController.onApproachStarted.AddListener(HandleApproachStarted);
        approachController.onReachedDoor.AddListener(HandleReachedDoor);
        approachController.onStoppedAtDoor.AddListener(HandleStoppedAtDoor);
        approachController.onPassedByDoor.AddListener(HandlePassedByDoor);
        // 庭Peek専用：庭で覗き始めた時点で廊下・ドア前の灯りを消す（outsideLight は操作しない）。
        approachController.onGardenPeekStarted.AddListener(HandleGardenPeekStarted);
        approachController.MovementStateChanged += HandleMovementStateChanged;
        // 到着通知：行きの段階切替だけに使う（帰路は通知側が区別して無視する）。
        approachController.WaypointArrived += HandleWaypointArrived;
        // 短縮接近の足音段階通知を受け取る（Waypoint を省略するため専用の通知口を使う）。
        approachController.SetShortApproachFootstepListener(HandleShortApproachFootstepStage);
        HandleMovementStateChanged(false);

        _eventsSubscribed = true;
        Debug.Log("[MotherApproachWarning] Subscribed to MotherApproachController events");
    }

    private void UnsubscribeApproachEvents()
    {
        if (!_eventsSubscribed || approachController == null) return;

        approachController.onApproachStarted.RemoveListener(HandleApproachStarted);
        approachController.onReachedDoor.RemoveListener(HandleReachedDoor);
        approachController.onStoppedAtDoor.RemoveListener(HandleStoppedAtDoor);
        approachController.onPassedByDoor.RemoveListener(HandlePassedByDoor);
        approachController.onGardenPeekStarted.RemoveListener(HandleGardenPeekStarted);
        approachController.MovementStateChanged -= HandleMovementStateChanged;
        approachController.WaypointArrived -= HandleWaypointArrived;
        // 短縮接近の足音段階通知の受け口も解除する（null 設定で確実に切る）。
        approachController.SetShortApproachFootstepListener(null);

        _eventsSubscribed = false;
    }

    private void HandleApproachStarted()
    {
        // 予兆で足音分類を確定済み（予兆→実移動の引き継ぎ済み）の場合は、
        // 段階・音量・分類をリセットしない。
        //  ・BeginApproach は ResetStateFlags で IsGardenRoute=false にするため、
        //    ここでリセットすると庭イベントが廊下音へ戻り、段階も Far へ逆戻りする。
        //  ・引き継ぎ済みかどうかは _activeFootstepRouteValid で判定する
        //    （サイレン・片付け・即時デバッグは分類が無効のため、従来どおりリセットする）。
        if (_activeFootstepRouteValid)
        {
            Debug.Log("[MotherApproachWarning] EVENT: Approach started" +
                      "（予兆の足音を引き継ぎ済み — 段階・音量・分類を維持）");
        }
        else
        {
            // イベント開始：足音の到着段階を Far・Close固定=false へ初期化する（前イベントのCloseを残さない）。
            ResetFootstepStages("イベント開始");
            Debug.Log("[MotherApproachWarning] EVENT: Approach started");
        }

        if (parentDetection != null)
            parentDetection.OnApproachStarted();
    }

    private void HandleReachedDoor()
    {
        Debug.Log("[MotherApproachWarning] EVENT: Reached door");
    }

    private void HandleStoppedAtDoor()
    {
        Debug.Log("[MotherApproachWarning] EVENT: Stopped at door — forwarding to MotherSuspicionSystem.OnApproachReachedDoor()");

        if (!isWarningActive)
        {
            Debug.Log("[MotherApproachWarning] HandleStoppedAtDoor: ignoring — warning not active");
            return;
        }

        if (parentDetection != null)
            parentDetection.OnApproachReachedDoor();
        else
            Debug.LogWarning("[MotherApproachWarning] HandleStoppedAtDoor: parentDetection is NULL");
    }

    private void HandlePassedByDoor()
    {
        Debug.Log("[MotherApproachWarning] EVENT: Passed by door");

        if (!isWarningActive)
        {
            Debug.Log("[MotherApproachWarning] HandlePassedByDoor: ignoring — warning not active");
            return;
        }

        bool shouldPlayPassByDoorSound = ActiveRoute == RouteState.HallwayPassBy;

        if (parentDetection != null)
        {
            parentDetection.OnApproachPassedBy();
            if (shouldPlayPassByDoorSound)
                PlayPassByDoorSound();
        }
        else
            Debug.LogWarning("[MotherApproachWarning] HandlePassedByDoor: parentDetection is NULL");
    }

    /// <summary>
    /// 【庭Peek専用】庭で覗き始めた時点（MotherApproachController.onGardenPeekStarted）で、
    /// 廊下・ドア前の灯り（廊下灯1・2・3と frontLight）を消す。
    ///  ・消灯するのは「庭の覗き開始」であり、移動開始ではない。
    ///    予告中・庭へ向かう移動中は従来どおり点灯したままにする。
    ///  ・outsideLight（庭灯）は操作しない。
    ///  ・GardenPassBy はこの通知を発生させないため、GardenPassBy の照明タイミングは変わらない。
    ///  ・帰還中は再点灯しない（消えたまま）。通常終了時は EndWarningSequence → TurnOffAllLights が全消灯する。
    ///  ・購読は SubscribeApproachEvents／UnsubscribeApproachEvents で対にして行う
    ///    （MotherSuspicionSystem 側の同じ通知の購読とは別リスナーで、互いに影響しない）。
    /// </summary>
    private void HandleGardenPeekStarted()
    {
        // 既存の兄弟ハンドラ（HandleStoppedAtDoor／HandlePassedByDoor）と同じ扱いにする。
        // 警告が終了している場合は消灯しない（中断後に別イベントの照明を消さない）。
        if (!isWarningActive)
        {
            Debug.Log("[MotherApproachWarning] HandleGardenPeekStarted: ignoring — warning not active");
            return;
        }

        Debug.Log("[MotherApproachWarning] EVENT: Garden peek started — 廊下・ドア前の灯りを消灯（outsideLight は操作しない）");
        TurnOffHallwayAndFrontLights();

        // 窓の外に親が来たことに気づけるよう、覗き開始から windowPeekSoundDelaySeconds 後に効果音を鳴らす。
        //  ・待機中に中断（isWarningActive=false）した場合は再生しない（コルーチン内で判定）。
        //  ・AudioSource 未設定なら PlayWindowPeek が何もしない（null 安全）。
        ScheduleWindowPeekSound();
    }

    private bool ValidateController()
    {
        if (approachController != null) return true;
        Debug.LogWarning("[MotherApproachWarning] approachController is NULL — assign it in the Inspector.", this);
        return false;
    }
}