using UnityEngine;
using TMPro;

/// <summary>
/// ClockFacePresenter：ゲーム内時刻（ParentGameTimeManager.GetClockText() の戻り値）を、
/// 時計の表示面へワールド空間の TextMeshPro（3D）で表示する「表示専用」コンポーネント。
///
/// 責務の境界（ここでは行わない）：
///  ・時刻は取得するだけ。ここで ProgressRate から時刻を再計算しない。
///  ・別タイマー・独自の時間加算を作らない。
///  ・時間管理・制限時間・クリア判定には一切触れない。
///  ・画面固定の HUD にはしない（ワールド空間で時計の表示面へ置く）。
///
/// 変形対策：
///  ・時計本体や親 Room に非等方スケールがあるため、表示は「別階層のアンカー（displayAnchor）」の
///    子に生成する。アンカーが非等方スケールでも、文字側の localScale で打ち消して数字の変形を防ぐ。
///  ・時計モデル・マテリアル・Room のスケールは変更しない。
///
/// 初期表示：
///  ・時間管理元の Start 実行順に依存しないよう、Update でも解決を試みる（取得できたら即表示）。
///  ・一時的な参照未取得は警告1回のみ（毎フレーム警告しない）。
///  ・表示文字列が変わったときだけテキストを更新する。
/// </summary>
[DisallowMultipleComponent]
public class ClockFacePresenter : MonoBehaviour
{
    [Header("参照")]
    [Tooltip("時刻の取得元。未設定なら ParentGameTimeManager.Instance を毎回解決する（Start 実行順に依存しない）。")]
    [SerializeField] private ParentGameTimeManager timeManager;

    [Tooltip("表示面のアンカー（均一スケール推奨の別階層）。文字はこの子に生成する。未設定ならこの GameObject 自身。")]
    [SerializeField] private Transform displayAnchor;

    [Tooltip("使用フォント。未設定なら TMP_Settings.defaultFontAsset を使う。プロジェクト既存フォントを優先する。")]
    [SerializeField] private TMP_FontAsset fontAsset;

    [Header("表示")]
    [Tooltip("文字サイズ（TMPのローカルサイズ）。アンカーのスケールに合わせて調整する。")]
    [SerializeField, Min(0.0001f)] private float fontSize = 1f;

    [Tooltip("文字色（暗い画面でも読める明色を推奨）。")]
    [SerializeField] private Color textColor = Color.white;

    [Tooltip("発光色。黒なら発光なし。暗い画面での視認性を上げたい場合に設定する（新しい照明は追加しない）。")]
    [SerializeField] private Color emissionColor = Color.black;

    [Tooltip("表示面から手前へ離す距離（ちらつき・表面との重なり防止）。")]
    [SerializeField] private float surfaceOffset = 0.01f;

    [Tooltip("表示面の手前方向（アンカーのローカル）。既定は -Z（TMPの正面）。")]
    [SerializeField] private Vector3 surfaceForward = Vector3.back;

    [Tooltip("背面表示になる場合に 180° 回して正面へ向ける。")]
    [SerializeField] private bool flipToFront = false;

    [Tooltip("アンカーの非等方スケールを文字側の localScale で打ち消す（数字の変形防止）。")]
    [SerializeField] private bool cancelNonUniformScale = true;

    [Header("デバッグ")]
    [SerializeField] private bool showDebugLogs;

    private TextMeshPro _text;
    private string _lastText;
    private bool _missingManagerWarned;
    private bool _diagnosticsLogged;

    private void Awake()
    {
        EnsureDisplay();
    }

    private void OnEnable()
    {
        // 時間管理元の Start より先に OnEnable が走っても、EnsureDisplay + Update が追従する。
        EnsureDisplay();
        Refresh(force: true);
    }

    private void Update()
    {
        Refresh(force: false);
    }

    /// <summary>表示用の TextMeshPro を用意する（既存があれば再利用し、無ければ生成する）。</summary>
    private void EnsureDisplay()
    {
        if (_text != null) return;

        Transform anchor = displayAnchor != null ? displayAnchor : transform;

        // 既存の子に TMP があれば再利用する（再生成・二重生成しない）。
        _text = anchor.GetComponentInChildren<TextMeshPro>(true);
        if (_text == null)
        {
            // 【重要】非アクティブで生成する。TMP の初期化がフォント設定前に走らないようにする。
            var go = new GameObject("ClockFaceText");
            go.SetActive(false);
            go.transform.SetParent(anchor, false);
            _text = go.AddComponent<TextMeshPro>();

            ConfigureText(anchor);
            EnsureFontAndRenderer();

            go.SetActive(true);
        }
        else
        {
            ConfigureText(anchor);
            EnsureFontAndRenderer();
        }
    }

    /// <summary>位置・向き・スケール・書式・色を設定する。</summary>
    private void ConfigureText(Transform anchor)
    {
        if (_text == null) return;

        // 表示面からわずかに手前へ離す（表面とのちらつき防止）。
        Vector3 forward = surfaceForward.sqrMagnitude > 1e-8f ? surfaceForward.normalized : Vector3.back;
        _text.transform.localPosition = forward * surfaceOffset;
        _text.transform.localRotation = flipToFront ? Quaternion.Euler(0f, 180f, 0f) : Quaternion.identity;
        _text.transform.localScale = ComputeDisplayScale(anchor);

        _text.alignment = TextAlignmentOptions.Center;
        _text.overflowMode = TextOverflowModes.Overflow;
        _text.color = textColor;
        _text.fontSize = fontSize;
        _text.text = string.Empty;
    }

    /// <summary>
    /// フォントとレンダラーを確定する。
    ///  ・フォントは「null のときだけ」ではなく必ず fontAsset を設定する（TMP 既定フォントで固定されない）。
    ///  ・MeshRenderer を有効化する。
    ///  ・発光は「非黒のときだけ」インスタンスマテリアルへ反映する（共有マテリアルは変更しない）。
    /// </summary>
    private void EnsureFontAndRenderer()
    {
        if (_text == null) return;

        TMP_FontAsset font = fontAsset != null ? fontAsset : TMP_Settings.defaultFontAsset;
        if (font != null && _text.font != font)
            _text.font = font;

        var renderer = _text.GetComponent<Renderer>();
        if (renderer != null) renderer.enabled = true;

        bool hasEmission = emissionColor.r > 0f || emissionColor.g > 0f || emissionColor.b > 0f;
        if (!hasEmission) return;

        Material mat = _text.fontMaterial; // インスタンスマテリアル
        if (mat == null) return;

        if (mat.HasProperty("_EmissionColor"))
            mat.SetColor("_EmissionColor", emissionColor);
        if (mat.HasProperty(ShaderUtilities.ID_GlowColor))
            mat.SetColor(ShaderUtilities.ID_GlowColor, emissionColor);
    }

    /// <summary>
    /// アンカーが「非等方スケール」のときだけ、その歪みを文字の localScale で打ち消す。
    ///  ・均一スケールのときは (1,1,1) のまま（アンカーの拡大縮小がそのまま文字に効く＝ユーザー調整を尊重）。
    ///  ・モデルや Room のスケール自体は変更しない。
    /// </summary>
    private Vector3 ComputeDisplayScale(Transform anchor)
    {
        if (!cancelNonUniformScale || anchor == null) return Vector3.one;

        Vector3 s = anchor.lossyScale;
        const float eps = 1e-5f;
        float ax = Mathf.Abs(s.x) > eps ? Mathf.Abs(s.x) : 1f;
        float ay = Mathf.Abs(s.y) > eps ? Mathf.Abs(s.y) : 1f;
        float az = Mathf.Abs(s.z) > eps ? Mathf.Abs(s.z) : 1f;

        float min = Mathf.Min(ax, Mathf.Min(ay, az));
        float max = Mathf.Max(ax, Mathf.Max(ay, az));
        bool nonUniform = (max - min) > (0.001f * max);

        // 均一スケールは尊重する（文字側で打ち消さない）。
        if (!nonUniform) return Vector3.one;

        return new Vector3(1f / ax, 1f / ay, 1f / az);
    }

    /// <summary>
    /// 初期化時に一度だけ、生成成否・時刻文字列・フォント／Material・active／Renderer状態・
    /// 文字数／頂点数／bounds・world位置／scale／Layer をまとめて記録する（毎フレームは出さない）。
    /// </summary>
    private void LogDiagnosticsOnce(Transform anchor)
    {
        if (_diagnosticsLogged) return;
        _diagnosticsLogged = true;

        if (_text == null)
        {
            Debug.LogWarning($"[ClockFacePresenter] 初期化: TMPの生成に失敗しました（anchor='{(anchor != null ? anchor.name : "NULL")}'）。", this);
            return;
        }

        // 文字数を確定させる（設定直後の textInfo を読むため）。
        _text.ForceMeshUpdate();

        var renderer = _text.GetComponent<Renderer>();
        Material mat = _text.fontMaterial;
        TMP_FontAsset font = _text.font;

        int charCount = _text.textInfo != null ? _text.textInfo.characterCount : -1;
        int vertCount = (_text.textInfo != null && _text.textInfo.meshInfo != null &&
                         _text.textInfo.meshInfo.Length > 0)
            ? _text.textInfo.meshInfo[0].vertexCount
            : -1;
        Bounds bounds = renderer != null ? renderer.bounds : new Bounds();

        Debug.Log(
            $"[ClockFacePresenter] 初期化: text='{_text.text}' | " +
            $"font={(font != null ? font.name : "NULL")} | " +
            $"material={(mat != null ? mat.name : "NULL")} shader={(mat != null && mat.shader != null ? mat.shader.name : "NULL")} | " +
            $"active={_text.gameObject.activeInHierarchy} rendererEnabled={(renderer != null && renderer.enabled)} | " +
            $"chars={charCount} verts={vertCount} | " +
            $"worldPos={_text.transform.position.ToString("F2")} worldScale={_text.transform.lossyScale.ToString("F3")} " +
            $"layer={_text.gameObject.layer} | boundsSize={bounds.size.ToString("F2")} | fontSize={fontSize}", this);
    }

    private void Refresh(bool force)
    {
        ParentGameTimeManager manager = ResolveTimeManager();
        if (manager == null)
        {
            WarnMissingManagerOnce();
            return;
        }

        string text = manager.GetClockText();
        if (!force && text == _lastText) return;

        _lastText = text;
        if (_text == null) EnsureDisplay();
        if (_text != null) _text.text = text;

        // 初期化直後の状態を一度だけ記録する（時刻文字列を含む）。
        LogDiagnosticsOnce(displayAnchor != null ? displayAnchor : transform);
    }

    private ParentGameTimeManager ResolveTimeManager()
    {
        if (timeManager != null) return timeManager;
        return ParentGameTimeManager.Instance;
    }

    private void WarnMissingManagerOnce()
    {
        if (_missingManagerWarned) return;
        _missingManagerWarned = true;
        Debug.LogWarning("[ClockFacePresenter] ParentGameTimeManager が見つからないため、時刻表示は取得できるまで保留します。", this);
    }
}
