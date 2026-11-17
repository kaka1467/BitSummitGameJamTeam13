using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// レーン制限：ランダムな1レーンを、右から左へ流れてくる「ポイントが減る帯」でふさぐ。アイテムと同じ向き・速さで動く。
///
/// 流れ（待機 → 予告 → 帯が流れる → 待機…）
///  ・予告：そのレーンの右端に警告マーク（▲！）が点滅する。
///  ・帯：画面の右の外から入ってきて、左の外へ抜けるまで流れる。長さは lengthMin〜lengthMax の範囲でランダム。
///        プレイヤーがそのレーンにいて、帯と重なっている間、ポイントが毎秒 drainPointsPerSecond ずつ減る。
///  ・出るレーンは毎回ランダム（直前と同じレーンは避ける）。レーンの位置は「プレイヤーのレーン」（PlayerMove）に合わせる。
///
/// 帯は、左端の絵（BarLeft）・右端の絵（BarRight）・その間をうめる縞模様（BarTile＝1周期ぶん）を組み合わせて作る。
/// 長さを変えても、3種類とも同じ倍率で拡大縮小するため、絵の縦横比は崩れない。
///
/// 出さない・止める条件
///  ・配置パターンが画面に残っている間は始めない（ItemSpawner.IsPatternVisible）。レーン制限の最中は、逆にパターンを始めない。
///  ・制限レーンには、予告から帯が抜けきるまでの間、通常のアイテムを出さない（ItemSpawner が IsLaneBlocked を見る）。
///    巨大障害物（QTE）も、この間は出さずに先送りする。
///  ・フィーバー中は始めない。フィーバーが始まったら途中でも中止する（稼ぐ時間を邪魔しない）。
///  ・チュートリアル中・本編前・結果確定後は動かない。
///
/// 時間は Time.time / deltaTime（ゲーム内時間）で進める。QTE や寝たふり中の停止（timeScale=0）では一緒に止まる。
/// 絵は Resources/LaneRestriction/（BarLeft・BarRight・BarTile・WarningIcon）。GameManager が、シーンに無ければ自動で追加する。
/// </summary>
public class LaneRestriction : MonoBehaviour
{
    /// <summary>予告〜帯が抜けきるまでの間は true（ItemSpawner が、この間はパターンを始めないために見る）。</summary>
    public static bool IsRunning { get; private set; }

    /// <summary>レーン制限が出ているレーン番号（0=上, 1=中, 2=下）。出ていないときは -1。</summary>
    public static int BlockedLane { get; private set; } = -1;

    /// <summary>指定レーンが、レーン制限の予告〜通過の間で、アイテムを出してはいけないレーンか。</summary>
    public static bool IsLaneBlocked(int lane)
    {
        return IsRunning && lane == BlockedLane;
    }

    private enum Phase { Idle, Warning, Moving }

    [Header("出現のタイミング（ゲーム内時間・秒）")]
    [Tooltip("本編開始から最初のレーン制限までの秒数。")]
    [SerializeField, Min(0f)] private float firstDelaySeconds = 25f;
    [Tooltip("帯が抜けきってから次のレーン制限までの最小秒数。")]
    [SerializeField, Min(0f)] private float intervalMinSeconds = 15f;
    [Tooltip("同・最大秒数。最小より小さい値は最小として扱う。")]
    [SerializeField, Min(0f)] private float intervalMaxSeconds = 30f;
    [Tooltip("帯が入ってくる前に、警告マークを点滅させる秒数。")]
    [SerializeField, Min(0.1f)] private float warningSeconds = 1.5f;

    [Header("帯の長さ（ワールド単位。左右の端の絵を含む全体の長さ）")]
    [Tooltip("帯の長さの最小。左右の端の絵の合計より短くはならない（その場合は端の絵だけの長さになる）。")]
    [SerializeField, Min(0.1f)] private float lengthMin = 2.5f;
    [Tooltip("帯の長さの最大。出るたびに、最小〜最大の範囲でランダムに決まる。")]
    [SerializeField, Min(0.1f)] private float lengthMax = 5f;

    [Header("流れる速さ")]
    [Tooltip("帯が右から左へ流れる速さ（ワールド単位/秒）。アイテムの速さ（Prefab設定 0.8）と同じ値が初期値。" +
             "得点段階の倍率と、出現側の速度倍率は、アイテムと同じように掛かる。")]
    [SerializeField, Min(0.01f)] private float moveSpeed = 0.8f;

    [Header("効果")]
    [Tooltip("プレイヤーが制限レーンにいて、帯と重なっている間、1秒あたりに減らすポイント。")]
    [SerializeField, Min(0f)] private float drainPointsPerSecond = 120f;

    [Header("帯の大きさ・位置（ワールド座標）")]
    [Tooltip("帯の高さ。左右の端の絵・縞模様の絵は、この高さに合わせて同じ倍率で拡大縮小される。")]
    [SerializeField, Min(0.02f)] private float barHeight = 0.17f;
    [Tooltip("ONなら、帯の縦位置を、下の『レーンごとの帯のY座標』ではなく、プレイヤーの各レーンのY座標（PlayerMove）にする。")]
    [SerializeField] private bool usePlayerLaneY = false;
    [Tooltip("帯の縦位置を、全レーンまとめてずらす量（上が正）。レーンごとの指定にも、プレイヤーのレーンにも掛かる。")]
    [SerializeField] private float barYOffset = 0f;
    [Tooltip("レーンごとの帯のY座標（ワールド座標）。[0]=上、[1]=中、[2]=下。再生中に変えると、すぐ反映される。" +
             "GameManger を選んでいる間は、Scene ビューに各レーンの位置が線で出る。")]
    [SerializeField] private float[] barLaneY = { 0.41f, -0.08f, -0.56f };

    [Tooltip("縞模様を、左右の端の絵の下へもぐり込ませる幅（端の絵の元の画像でのピクセル数）。" +
             "端の絵の、縞模様と接する側が斜めに切れていて透明になっている部分を、縞模様でうめるための値。" +
             "境目に隙間が残るなら大きく、縞模様が端の絵の崩れた部分から透けて見えるなら小さくする。")]
    [SerializeField, Min(0f)] private float capTuckPixels = 90f;

    [Header("警告マーク")]
    [Tooltip("予告中の警告マークの位置。画面の右端から内側へ、カメラ幅の何割入ったところか。")]
    [SerializeField, Range(0f, 0.5f)] private float iconInsetRatio = 0.08f;
    [Tooltip("警告マークの縦位置。帯の中心からのずれ（ワールド単位。上が正）。")]
    [SerializeField] private float iconYOffset = 0.07f;
    [Tooltip("警告マークの高さ（ワールド単位）。")]
    [SerializeField, Min(0.01f)] private float iconHeight = 0.2f;
    [SerializeField, Min(0.02f)] private float iconBlinkSeconds = 0.2f;

    [Header("その他の見た目")]
    [Tooltip("ポイントが減っている間、帯が染まる色。")]
    [SerializeField] private Color drainTint = new Color(1f, 0.55f, 0.55f, 1f);
    [Tooltip("帯・警告マークを描く順番（Order in Layer）。プレイヤーと同じレイヤーで、プレイヤーの値にこの値を足す。" +
             "背景より後ろに隠れてしまうときは、この値を大きくする。前後はZ座標（下の Depth Step）でも決めている。")]
    [SerializeField] private int sortingOrderOffset = 0;
    [Tooltip("帯の各パーツのZ方向の間隔。アイテムより Depth Step だけ奥に帯の端の絵を置き、縞模様はさらに奥、警告マークは手前に置く。" +
             "同じ描画順のとき、奥にあるものから先に描かれる。")]
    [SerializeField, Min(0.0001f)] private float depthStep = 0.01f;

    [Header("デバッグ（Editor／Development Buildのみ）")]
    [Tooltip("数字キー8で、すぐにレーン制限を始める。")]
    [SerializeField] private bool enableDebugKey = true;
    [SerializeField] private bool showDebugLogs = false;

    private const string LeftResource = "LaneRestriction/BarLeft";
    private const string RightResource = "LaneRestriction/BarRight";
    private const string TileResource = "LaneRestriction/BarTile";
    private const string IconResource = "LaneRestriction/WarningIcon";
    private const float SeamOverlap = 0.004f;   // 絵と絵の境目に出る、画面上1ピクセル未満のすき間を消すための重なり（約2ピクセル）
    private const float OffscreenMargin = 0.1f; // 画面の外へ置く余白

    private Phase _phase = Phase.Idle;
    private float _phaseTimer;
    private float _nextStartTime = -1f;
    private int _lane = -1;
    private int _lastLane = -1;
    private float _drainRemainder;

    // 流れている帯の状態（左端のX座標と全体の長さ）
    private float _barLeftX;
    private float _barLength;

    private ItemSpawner _spawner;
    private PlayerMove _playerMove;
    private Transform _player;
    private Renderer _playerRenderer;
    private bool _wasMainPlay;

    // 帯＝左端・右端・縞模様（縞模様は1周期ぶんの絵を整数枚並べる）
    private Transform _barRoot;
    private SpriteRenderer _leftCap;
    private SpriteRenderer _rightCap;
    private readonly System.Collections.Generic.List<SpriteRenderer> _tiles = new System.Collections.Generic.List<SpriteRenderer>();
    private Sprite _leftSprite;
    private Sprite _rightSprite;
    private Sprite _tileSprite;
    private SpriteRenderer _icon;
    private Sprite _iconSprite;
    private bool _assetsTried;
    private Color _barTint = Color.white;

    private void OnDisable()
    {
        EndImmediately();
    }

    private void Update()
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        if (enableDebugKey && Keyboard.current != null &&
            (Keyboard.current.digit8Key.wasPressedThisFrame || Keyboard.current.numpad8Key.wasPressedThisFrame))
        {
            StartNow();
        }
#endif

        GameManager gm = GameManager.instance;
        bool playing = gm != null && gm.IsMainPlayActive && !gm.IsTutorialMode && !gm.IsGameOver;

        if (!playing)
        {
            if (_phase != Phase.Idle) EndImmediately();
            _wasMainPlay = false;
            return;
        }

        // 本編が始まった瞬間に、最初の出現時刻を決める。
        if (!_wasMainPlay)
        {
            _wasMainPlay = true;
            _nextStartTime = Time.time + firstDelaySeconds;
        }

        // フィーバーが始まったら中止する（稼ぐ時間を邪魔しない）。終わった後の間隔は通常どおり。
        if (gm.IsFeverActive)
        {
            if (_phase != Phase.Idle)
            {
                EndImmediately();
                ScheduleNext();
            }
            return;
        }

        switch (_phase)
        {
            case Phase.Idle:
                if (Time.time >= _nextStartTime) TryBegin();
                break;
            case Phase.Warning:
                UpdateWarning();
                break;
            case Phase.Moving:
                UpdateMoving();
                break;
        }
    }

    // ── 開始 ───────────────────────────────────────────────────────────────

    private bool Resolve()
    {
        if (_spawner == null) _spawner = FindFirstObjectByType<ItemSpawner>();
        if (_spawner == null) return false;

        if (_playerMove == null) _playerMove = FindFirstObjectByType<PlayerMove>();
        if (_playerMove == null || _playerMove.LaneYs == null || _playerMove.LaneYs.Count == 0) return false;

        if (_player != _playerMove.transform)
        {
            _player = _playerMove.transform;
            _playerRenderer = _player.GetComponentInChildren<Renderer>();
        }

        return EnsureVisuals();
    }

    private void TryBegin()
    {
        if (!Resolve())
        {
            _nextStartTime = Time.time + 1f;
            return;
        }

        // 配置パターンが画面に残っている間は始めない（少し待って再判定する）。
        if (_spawner.IsPatternVisible)
        {
            _nextStartTime = Time.time + 0.5f;
            return;
        }

        Begin();
    }

    /// <summary>デバッグ用：条件を無視して、すぐに予告から始める。</summary>
    private void StartNow()
    {
        if (_phase != Phase.Idle) return;
        if (!Resolve()) return;
        Begin();
    }

    private void Begin()
    {
        int count = _playerMove.LaneYs.Count;
        int lane = Random.Range(0, count);
        if (count > 1 && lane == _lastLane) lane = (lane + 1 + Random.Range(0, count - 1)) % count;

        _lane = lane;
        _lastLane = lane;
        BlockedLane = lane;
        _drainRemainder = 0f;
        _barTint = Color.white;
        _phase = Phase.Warning;
        _phaseTimer = 0f;
        IsRunning = true;

        SetBarVisible(false);
        SetIconVisible(true);
        PlaceIconAtEntrance();

        if (showDebugLogs)
            Debug.Log($"[LaneRestriction] 予告開始（レーン{lane}）。");
    }

    // ── 各フェーズ ─────────────────────────────────────────────────────────

    private void UpdateWarning()
    {
        _phaseTimer += Time.deltaTime;
        PlaceIconAtEntrance();

        // 警告マークの点滅
        bool on = Mathf.FloorToInt(_phaseTimer / iconBlinkSeconds) % 2 == 0;
        SetIconVisible(on);

        if (_phaseTimer < warningSeconds) return;

        // 帯を、画面の右の外から流し始める。長さは範囲の中でランダムに決める。
        Camera cam = Camera.main;
        if (cam == null) return;

        _barLength = ChooseLength();
        _barLeftX = GetCameraRight(cam) + OffscreenMargin;
        _phase = Phase.Moving;
        _phaseTimer = 0f;

        SetIconVisible(true);
        SetBarVisible(true);
        PlaceBar();

        if (showDebugLogs)
            Debug.Log($"[LaneRestriction] 帯が流れ始めました（長さ{_barLength:F2}）。");
    }

    private void UpdateMoving()
    {
        Camera cam = Camera.main;
        if (cam == null) return;

        // アイテムと同じ向き（左）・同じ倍率で流す。
        float speed = moveSpeed * GameManager.CurrentScoreSpeedMultiplier * _spawner.ItemSpeedMultiplier;
        _barLeftX -= speed * Time.deltaTime;
        PlaceBar();

        // プレイヤーが同じレーンにいて、帯と重なっている間だけ、ポイントが減る。
        bool draining = IsPlayerOnBar();
        if (draining) Drain(Time.deltaTime);

        // 減っている間は、帯をほんのり染めて脈動させる。
        _barTint = Color.white;
        if (draining)
        {
            float wave = 0.5f + 0.5f * Mathf.Sin(Time.time * 14f);
            _barTint = Color.Lerp(Color.white, drainTint, 0.5f + 0.5f * wave);
        }
        ApplyBarColor();

        // 帯の右端が、画面の左の外へ抜けたら終わり。
        if (_barLeftX + _barLength < GetCameraLeft(cam) - OffscreenMargin)
        {
            EndImmediately();
            ScheduleNext();
        }
    }

    private void ScheduleNext()
    {
        float min = Mathf.Max(0f, intervalMinSeconds);
        float max = Mathf.Max(min, intervalMaxSeconds);
        _nextStartTime = Time.time + Random.Range(min, max);
    }

    private void EndImmediately()
    {
        _phase = Phase.Idle;
        _phaseTimer = 0f;
        _lane = -1;
        BlockedLane = -1;
        IsRunning = false;
        SetBarVisible(false);
        SetIconVisible(false);
    }

    /// <summary>
    /// 帯の長さを、最小〜最大の範囲でランダムに決める（左右の端の絵の合計より短くはしない）。
    /// 縞模様は伸縮させず、1周期ぶんの絵を整数枚並べる。縞模様は左右の端の絵の下へ tuck ずつもぐり込ませるので、
    /// 長さを「端の絵 ＋ 縞模様N枚 − もぐり込み2か所ぶん」に丸める。
    /// </summary>
    private float ChooseLength()
    {
        float min = Mathf.Max(0.1f, lengthMin);
        float max = Mathf.Max(min, lengthMax);
        float caps = GetCapsWidth();
        float want = Mathf.Max(caps, Random.Range(min, max));

        float tileW = GetTileWidth();
        if (tileW <= 0.0001f) return want;

        float tuck2 = GetTuck() * 2f;
        int minCount = Mathf.CeilToInt(tuck2 / tileW);   // 端の絵の下へもぐり込ませる分は、最低でも必要
        int count = Mathf.Max(minCount, Mathf.RoundToInt((want - caps + tuck2) / tileW));
        return caps + count * tileW - tuck2;
    }

    // ── 効果 ───────────────────────────────────────────────────────────────

    /// <summary>プレイヤーが制限レーンにいて、プレイヤーの絵が帯の範囲と重なっているか。</summary>
    private bool IsPlayerOnBar()
    {
        if (_playerMove == null || _playerMove.CurrentLane != _lane) return false;

        float minX, maxX;
        if (_playerRenderer != null)
        {
            Bounds b = _playerRenderer.bounds;
            minX = b.min.x;
            maxX = b.max.x;
        }
        else
        {
            minX = maxX = _player.position.x;
        }

        return maxX > _barLeftX && minX < _barLeftX + _barLength;
    }

    /// <summary>端数をためながら整数ポイントずつ減らす（「-N」の通知は出さない）。</summary>
    private void Drain(float deltaTime)
    {
        GameManager gm = GameManager.instance;
        if (gm == null) return;

        // フィーバー中は減らさない（フィーバーが始まると帯は中止されるが、念のためここでも止める）
        if (gm.IsFeverActive) return;

        _drainRemainder += drainPointsPerSecond * deltaTime;
        int whole = Mathf.FloorToInt(_drainRemainder);
        if (whole <= 0) return;

        _drainRemainder -= whole;
        gm.DrainScore(-whole);
    }

    // ── 見た目 ─────────────────────────────────────────────────────────────

    private static float GetCameraRight(Camera cam)
    {
        return cam.transform.position.x + cam.orthographicSize * cam.aspect;
    }

    private static float GetCameraLeft(Camera cam)
    {
        return cam.transform.position.x - cam.orthographicSize * cam.aspect;
    }

    private bool EnsureVisuals()
    {
        if (_barRoot != null && _icon != null) return true;

        if (!_assetsTried)
        {
            _assetsTried = true;
            _leftSprite = Resources.Load<Sprite>(LeftResource);
            _rightSprite = Resources.Load<Sprite>(RightResource);
            _tileSprite = Resources.Load<Sprite>(TileResource);
            _iconSprite = Resources.Load<Sprite>(IconResource);
            if (_leftSprite == null || _rightSprite == null || _tileSprite == null || _iconSprite == null)
                Debug.LogWarning("[LaneRestriction] Resources/LaneRestriction の画像（BarLeft・BarRight・BarTile・WarningIcon）が見つかりません（Spriteとして取り込まれているか確認してください）。");
        }

        if (_leftSprite == null || _rightSprite == null || _tileSprite == null || _iconSprite == null) return false;

        _barRoot = new GameObject("LaneRestrictionBar").transform;
        _barRoot.SetParent(transform, false);

        _leftCap = CreateRenderer("Left", _leftSprite, _barRoot);
        _rightCap = CreateRenderer("Right", _rightSprite, _barRoot);

        _icon = CreateRenderer("LaneRestrictionIcon", _iconSprite, transform);

        SetBarVisible(false);
        SetIconVisible(false);
        return true;
    }

    private SpriteRenderer CreateRenderer(string objectName, Sprite sprite, Transform parent)
    {
        var go = new GameObject(objectName);
        go.transform.SetParent(parent, false);
        SpriteRenderer r = go.AddComponent<SpriteRenderer>();
        r.sprite = sprite;
        return r;
    }

    private SpriteRenderer GetTile(int index)
    {
        while (_tiles.Count <= index)
        {
            _tiles.Add(CreateRenderer("Tile", _tileSprite, _barRoot));
        }

        return _tiles[index];
    }

    /// <summary>絵3種類に共通の倍率（帯の高さに合わせる。縦横比は崩さない）。</summary>
    private float GetScale()
    {
        return barHeight / Mathf.Max(0.0001f, _tileSprite.bounds.size.y);
    }

    /// <summary>左右の端の絵の幅の合計（これより短い帯は作れない）。</summary>
    private float GetCapsWidth()
    {
        if (_leftSprite == null || _rightSprite == null || _tileSprite == null) return 0f;
        return (_leftSprite.bounds.size.x + _rightSprite.bounds.size.x) * GetScale();
    }

    /// <summary>縞模様1枚（1周期）の幅。</summary>
    private float GetTileWidth()
    {
        if (_tileSprite == null) return 0f;
        return _tileSprite.bounds.size.x * GetScale();
    }

    /// <summary>縞模様を端の絵の下へもぐり込ませる幅（ワールド単位）。</summary>
    private float GetTuck()
    {
        if (_leftSprite == null) return 0f;
        float ppu = Mathf.Max(1f, _leftSprite.pixelsPerUnit);
        return capTuckPixels / ppu * GetScale();
    }

    /// <summary>制限レーンの帯の中心のY座標。レーンごとの指定（barLaneY）を優先し、使わない設定ならプレイヤーのレーンのYを使う。</summary>
    private float GetBarY()
    {
        int lane = Mathf.Max(0, _lane);

        if (!usePlayerLaneY && barLaneY != null && barLaneY.Length > 0)
            return barLaneY[Mathf.Min(lane, barLaneY.Length - 1)] + barYOffset;

        return _playerMove.LaneYs[Mathf.Min(lane, _playerMove.LaneYs.Count - 1)] + barYOffset;
    }

    /// <summary>
    /// 帯を、左端のX座標（_barLeftX）と長さ（_barLength）の通りに置く。
    ///  ・左端の絵・右端の絵を両端に置き、その間を縞模様の絵でうめる（縞模様は帯と一緒に動く）。
    ///  ・警告マークは、帯の中央に乗せる。
    /// </summary>
    private void PlaceBar()
    {
        if (_barRoot == null || _lane < 0) return;

        float y = GetBarY();
        float z = _spawner.ItemPlaneZ + depthStep;      // 端の絵：アイテムより少し奥
        float zTile = z + depthStep;                    // 縞模様：端の絵よりさらに奥
        float zIcon = _spawner.ItemPlaneZ;              // 警告マーク：帯より手前
        float scale = GetScale();
        float leftW = _leftSprite.bounds.size.x * scale;
        float rightW = _rightSprite.bounds.size.x * scale;
        float x0 = _barLeftX;
        float x1 = _barLeftX + _barLength;

        _barRoot.position = new Vector3(0f, 0f, z);

        _leftCap.transform.localScale = new Vector3(scale, scale, 1f);
        _leftCap.transform.position = new Vector3(x0 + leftW * 0.5f, y, z);
        _rightCap.transform.localScale = new Vector3(scale, scale, 1f);
        _rightCap.transform.position = new Vector3(x1 - rightW * 0.5f, y, z);

        // 縞模様＝1周期ぶんの絵を、整数枚、伸縮せずに並べる。
        // 端の絵の縞模様側は斜めに切れて透明な部分があるので、縞模様を左右の端の絵の下へ tuck だけもぐり込ませ、
        // その透明な部分を縞模様でうめる（縞模様は端の絵より奥なので、端の絵の不透明な部分は隠れる）。
        // 絵と絵の境目は、少しだけ横に広げて重ね、画面上のすき間をなくす。
        float tuck = GetTuck();
        float bodyL = x0 + leftW - tuck;
        float bodyW = (x1 - rightW + tuck) - bodyL;
        float tileW = _tileSprite.bounds.size.x * scale;

        int needed = (bodyW > 0.0001f && tileW > 0.0001f) ? Mathf.RoundToInt(bodyW / tileW) : 0;
        float scaleX = (tileW > 0.0001f) ? scale * (1f + SeamOverlap / tileW) : scale;

        for (int i = 0; i < needed; i++)
        {
            SpriteRenderer tile = GetTile(i);
            tile.gameObject.SetActive(true);
            tile.transform.localScale = new Vector3(scaleX, scale, 1f);
            tile.transform.position = new Vector3(bodyL + tileW * (i + 0.5f), y, zTile);
        }
        for (int i = needed; i < _tiles.Count; i++)
            _tiles[i].gameObject.SetActive(false);

        // 警告マークは帯の中央に乗せる
        float iconScale = iconHeight / Mathf.Max(0.0001f, _iconSprite.bounds.size.y);
        _icon.transform.position = new Vector3((x0 + x1) * 0.5f, y + iconYOffset, zIcon);
        _icon.transform.localScale = new Vector3(iconScale, iconScale, 1f);

        ApplySorting();
        ApplyBarColor();
    }

    /// <summary>予告中：警告マークを、帯が入ってくる画面の右端に置く。</summary>
    private void PlaceIconAtEntrance()
    {
        if (_icon == null || _playerMove == null || _lane < 0) return;

        Camera cam = Camera.main;
        if (cam == null) return;

        float camWidth = cam.orthographicSize * cam.aspect * 2f;
        float x = GetCameraRight(cam) - camWidth * iconInsetRatio;
        float iconScale = iconHeight / Mathf.Max(0.0001f, _iconSprite.bounds.size.y);

        _icon.transform.position = new Vector3(x, GetBarY() + iconYOffset, _spawner.ItemPlaneZ);
        _icon.transform.localScale = new Vector3(iconScale, iconScale, 1f);
        ApplyIconSorting();
    }

    private void GetSortingBase(out int layerId, out int baseOrder)
    {
        layerId = 0;
        baseOrder = sortingOrderOffset;

        SpriteRenderer reference = (_player != null) ? _player.GetComponentInChildren<SpriteRenderer>() : null;
        if (reference != null)
        {
            layerId = reference.sortingLayerID;
            baseOrder = reference.sortingOrder + sortingOrderOffset;
        }
    }

    /// <summary>
    /// 描画順はすべて同じ値（プレイヤーのレイヤー・順番＋ sortingOrderOffset）にし、前後はZ座標で決める
    /// （縞模様＜端の絵＜警告マークの順に手前）。背景の Order より小さくならないよう、プレイヤーより後ろにはしない。
    /// </summary>
    private void ApplySorting()
    {
        GetSortingBase(out int layerId, out int baseOrder);

        for (int i = 0; i < _tiles.Count; i++)
        {
            _tiles[i].sortingLayerID = layerId;
            _tiles[i].sortingOrder = baseOrder;
        }

        _leftCap.sortingLayerID = layerId;
        _leftCap.sortingOrder = baseOrder;
        _rightCap.sortingLayerID = layerId;
        _rightCap.sortingOrder = baseOrder;

        ApplyIconSorting();
    }

    private void ApplyIconSorting()
    {
        if (_icon == null) return;

        GetSortingBase(out int layerId, out int baseOrder);
        _icon.sortingLayerID = layerId;
        _icon.sortingOrder = baseOrder;
    }

    private void SetBarVisible(bool visible)
    {
        if (_barRoot != null) _barRoot.gameObject.SetActive(visible);
    }

    private void ApplyBarColor()
    {
        if (_barRoot == null) return;

        if (_leftCap != null) _leftCap.color = _barTint;
        if (_rightCap != null) _rightCap.color = _barTint;
        for (int i = 0; i < _tiles.Count; i++) _tiles[i].color = _barTint;
    }

    private void SetIconVisible(bool visible)
    {
        if (_icon != null) _icon.enabled = visible;
    }

#if UNITY_EDITOR
    /// <summary>
    /// GameManger を選んでいる間、Scene ビューに各レーンの帯の位置（横線）を出す。再生していなくても見える。
    /// 線の高さ＝帯の高さ、水平方向＝画面の幅。位置を決めるときの目安にする。
    /// </summary>
    private void OnDrawGizmosSelected()
    {
        if (usePlayerLaneY || barLaneY == null) return;

        Camera cam = Camera.main;
        float halfW = (cam != null) ? cam.orthographicSize * cam.aspect : 1.4f;
        float centerX = (cam != null) ? cam.transform.position.x : 0f;
        float z = 609.47f;
        string[] names = { "上", "中", "下" };

        for (int i = 0; i < barLaneY.Length; i++)
        {
            float y = barLaneY[i] + barYOffset;
            float h = barHeight * 0.5f;

            Gizmos.color = new Color(1f, 0.85f, 0.2f, 1f);
            Gizmos.DrawLine(new Vector3(centerX - halfW, y + h, z), new Vector3(centerX + halfW, y + h, z));
            Gizmos.DrawLine(new Vector3(centerX - halfW, y - h, z), new Vector3(centerX + halfW, y - h, z));
            Gizmos.color = new Color(1f, 0.4f, 0.4f, 1f);
            Gizmos.DrawLine(new Vector3(centerX - halfW, y, z), new Vector3(centerX + halfW, y, z));

            string label = (i < names.Length ? names[i] : i.ToString()) + $"  Y={barLaneY[i]:F3}";
            UnityEditor.Handles.Label(new Vector3(centerX - halfW + 0.03f, y + h, z), label);
        }
    }
#endif
}
