using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// ParentVolumeSettingsPanel：親機タイトルの「設定」入口と設定パネルを制御する。
///
/// 責務の境界：
///  ・パネル本体・見出し・ラベル・スライダー・ボタンは Scene 側で用意し、ここは SerializeField 参照で操作する
///    （実行時にパネル一式を生成しない）。
///  ・音量の保存・適用は ParentAudioSettings（親機専用キー）へ委譲する。
///  ・背景ぼかし（キャプチャ→ぼかし画像）だけは実行時に生成・解放する。
///  ・位置・サイズ・文字サイズ・色はコードで上書きしない（スライダー値の同期のみ）。
///  ・参照不足時は明確なエラーを出し、不完全な状態で開かない（自動生成フォールバックは持たない）。
/// </summary>
public class ParentVolumeSettingsPanel : MonoBehaviour
{
    [Header("設定パネル（Scene側の参照）")]
    [SerializeField] private GameObject settingsPanel;
    [SerializeField] private TextMeshProUGUI titleText;
    [SerializeField] private TextMeshProUGUI bgmLabel;
    [SerializeField] private Slider bgmSlider;
    [SerializeField] private TextMeshProUGUI seLabel;
    [SerializeField] private Slider seSlider;
    [SerializeField] private TextMeshProUGUI childBgmLabel;
    [SerializeField] private Slider childBgmSlider;
    [SerializeField] private TextMeshProUGUI childSeLabel;
    [SerializeField] private Slider childSeSlider;
    [SerializeField] private Button resetButton;
    [SerializeField] private Button closeButton;

    [Header("背景ぼかし（弱めの初期値。Inspectorで調整可）")]
    [Tooltip("縮小倍率。1で等倍。大きいほど軽いが強くぼける。")]
    [SerializeField, Range(1, 8)] private int blurDownscale = 2;
    [Tooltip("ガウスぼかしの半径（縮小後テクセル）。1前後が「わずかにピントが外れる」強さ。")]
    [SerializeField, Min(0.5f)] private float blurRadius = 1f;
    [Tooltip("キャプチャ画像の上下反転補正。補正箇所はここ1箇所にまとめてある。")]
    [SerializeField] private bool flipCaptureVertical = true;
    [Tooltip("確認用：ぼかしを通さずキャプチャをそのまま表示する（向き・位置・比率の確認）。")]
    [SerializeField] private bool debugSkipBlur = false;

    [Header("暗転レイヤー（ぼかしとパネルの間。背景だけを暗くする）")]
    [Tooltip("暗転の濃さ（0=なし, 1=真っ黒）。パネルと選択矢印は暗くならない。")]
    [SerializeField, Range(0f, 1f)] private float dimOpacity = 0.35f;

    [Header("設定内の選択と音量変更")]
    [Tooltip("左右1回で変化させる音量（0.05 = 5%）。")]
    [SerializeField, Range(0.01f, 0.25f)] private float volumeStepPercent = 0.05f;
    [Tooltip("設定中の選択矢印。未設定ならタイトル矢印と同じ見た目のものを実行時に1つ用意する。")]
    [SerializeField] private RectTransform settingsArrow;
    [Tooltip("選択項目に対する矢印の位置オフセット（項目の左に出す）。")]
    [SerializeField] private Vector2 settingsArrowOffset = new Vector2(-140f, 0f);

    private TitleMenuHighlight _highlight;
    private TitleScreenVideoPlayer _videoPlayer;

    private RawImage _blurImage;
    private RenderTexture _blurRt;
    private Material _blurMaterial;
    private Coroutine _openRoutine;
    private bool _opening;

    private Image _dimImage;              // 暗転レイヤー（ぼかしとパネルの間に置く）
    private RectTransform _arrowRt;       // 設定中の選択矢印（タイトル矢印とは別オブジェクト）
    private Button _entryButton;          // 閉じたときに選択を戻す「設定」入口
    private SettingsVolumeRow _bgmRow;
    private SettingsVolumeRow _seRow;
    private SettingsVolumeRow _childBgmRow;
    private SettingsVolumeRow _childSeRow;
    private bool _isOpen;

    private readonly List<Selectable> _blockedSelectables = new List<Selectable>();
    private readonly List<bool> _blockedPrevState = new List<bool>();

    private void Awake()
    {
        EnsureChildVolumeRows();
        // 実行開始時は閉じた状態にする（Scene の配置・サイズ・文字には触れない）。
        if (settingsPanel != null) settingsPanel.SetActive(false);

        _highlight = FindFirstObjectByType<TitleMenuHighlight>();
        _videoPlayer = FindFirstObjectByType<TitleScreenVideoPlayer>();

        BuildBlurImage();       // ぼかし画像だけは実行時に用意する（解放は後始末で行う）
        BuildDimLayer();        // 暗転レイヤーも実行時に1つだけ用意する
        BuildSettingsArrow();   // 設定用の矢印も1つだけ用意する（タイトル矢印とは別）
        SetupEntryButton();
        SetupPanelButtons();
        SetupRows();
        ConfigureNavigation();
    }

    private void OnEnable()
    {
        if (IsReady()) SyncDisplay();
    }

    /// <summary>必須参照がすべて揃っているか。</summary>
    private bool IsReady()
    {
        return settingsPanel != null && bgmSlider != null && seSlider != null &&
               childBgmSlider != null && childSeSlider != null;
    }

    private void ReportMissingReferences()
    {
        Debug.LogError(
            "[ParentVolumeSettingsPanel] 必須参照が不足しているため設定パネルを開けません。" +
            "Tools/Team13/Create Parent Settings UI を実行して参照を割り当ててください。" +
            $"（settingsPanel={(settingsPanel != null)}, bgmSlider={(bgmSlider != null)}, seSlider={(seSlider != null)}）", this);
    }

    // ── 入口ボタン（見た目は Scene 側。ここは Button を用意して Open を繋ぐだけ）──
    private void SetupEntryButton()
    {
        var btn = GetComponent<Button>();
        if (btn == null) btn = gameObject.AddComponent<Button>();
        _entryButton = btn;                 // 閉じたときに選択を戻す先

        if (btn.targetGraphic == null)
        {
            var graphic = GetComponentInChildren<Graphic>(true);
            if (graphic != null) btn.targetGraphic = graphic;
        }

        btn.onClick.RemoveListener(Open);   // 二重登録を防ぐ
        btn.onClick.AddListener(Open);
    }

    // ── パネル内ボタン（Reset / Close）──
    private void SetupPanelButtons()
    {
        if (resetButton != null)
        {
            resetButton.onClick.RemoveListener(ResetToDefaults);
            resetButton.onClick.AddListener(ResetToDefaults);
        }

        if (closeButton != null)
        {
            closeButton.onClick.RemoveListener(Close);
            closeButton.onClick.AddListener(Close);
        }
    }

    // ── 開閉 ──
    public void Open()
    {
        if (_opening) return;
        if (settingsPanel != null && settingsPanel.activeSelf) return;

        if (!IsReady())
        {
            ReportMissingReferences();
            return;                     // 不完全な状態では開かない
        }

        if (_openRoutine != null) StopCoroutine(_openRoutine);
        _openRoutine = StartCoroutine(OpenRoutine());
    }

    public void Close()
    {
        if (_openRoutine != null) { StopCoroutine(_openRoutine); _openRoutine = null; }
        _opening = false;
        _isOpen = false;

        if (settingsPanel != null) settingsPanel.SetActive(false);

        ReleaseBlur();
        HideDim();                  // 暗転を確実に解除
        HideArrow();                // 設定用の矢印も確実に解除
        SetTitleInteractable(true);
        if (_highlight != null) _highlight.SetIndicatorVisible(true);
        if (_videoPlayer != null) _videoPlayer.SetIdleSuppressed(false);

        SelectItem(_entryButton);   // タイトルの「設定」へ選択を戻す
    }

    private IEnumerator OpenRoutine()
    {
        _opening = true;
        _isOpen = false;

        // 背景の入力停止・矢印/放置動画の抑止（タイトル矢印はここで消え、設定用の矢印へ切り替わる）
        SetTitleInteractable(false);
        if (_highlight != null) _highlight.SetIndicatorVisible(false);
        if (_videoPlayer != null) _videoPlayer.SetIdleSuppressed(true);

        // 撮り込み防止：キャプチャ前にパネル・暗転・ぼかし画像・設定矢印を確実に非表示にする
        HideArrow();
        HideDim();
        if (_blurImage != null) _blurImage.gameObject.SetActive(false);
        settingsPanel.SetActive(false);

        // 非表示が描画へ反映された「完成フレーム」を待ってから取り込む
        yield return null;
        yield return new WaitForEndOfFrame();
        yield return CaptureAndBlur();

        // キャプチャ完了後に表示する（ぼかし → 暗転 → パネル → 矢印 の順）
        SyncDisplay();
        if (_blurImage != null && _blurImage.texture != null) _blurImage.gameObject.SetActive(true);
        ShowDim();
        settingsPanel.SetActive(true);

        _isOpen = true;
        SelectItem(_bgmRow);     // 開いた直後は BGM を選択
        ShowArrow();

        _openRoutine = null;
        _opening = false;
    }

    // ── 表示同期（値のみ。位置・サイズ・文字・色は触らない）──
    private void SyncDisplay()
    {
        if (bgmSlider != null)
        {
            bgmSlider.SetValueWithoutNotify(ParentAudioSettings.BgmVolume);
            bgmSlider.onValueChanged.RemoveListener(OnBgmChanged);
            bgmSlider.onValueChanged.AddListener(OnBgmChanged);
        }

        if (seSlider != null)
        {
            seSlider.SetValueWithoutNotify(ParentAudioSettings.SeVolume);
            seSlider.onValueChanged.RemoveListener(OnSeChanged);
            seSlider.onValueChanged.AddListener(OnSeChanged);
        }

        if (childBgmSlider != null)
        {
            childBgmSlider.SetValueWithoutNotify(ParentAudioSettings.ChildBgmVolume);
            childBgmSlider.onValueChanged.RemoveListener(OnChildBgmChanged);
            childBgmSlider.onValueChanged.AddListener(OnChildBgmChanged);
        }

        if (childSeSlider != null)
        {
            childSeSlider.SetValueWithoutNotify(ParentAudioSettings.ChildSeVolume);
            childSeSlider.onValueChanged.RemoveListener(OnChildSeChanged);
            childSeSlider.onValueChanged.AddListener(OnChildSeChanged);
        }
    }

    private void OnBgmChanged(float v)
    {
        if (ParentAudioSettings.Instance != null) ParentAudioSettings.Instance.SetBgmVolume(v);
    }

    private void OnSeChanged(float v)
    {
        if (ParentAudioSettings.Instance != null) ParentAudioSettings.Instance.SetSeVolume(v);
    }

    private void OnChildBgmChanged(float v)
    {
        if (ParentAudioSettings.Instance != null) ParentAudioSettings.Instance.SetChildBgmVolume(v);
    }

    private void OnChildSeChanged(float v)
    {
        if (ParentAudioSettings.Instance != null) ParentAudioSettings.Instance.SetChildSeVolume(v);
    }

    public void ResetToDefaults()
    {
        if (ParentAudioSettings.Instance != null) ParentAudioSettings.Instance.ResetToDefaults();
        SyncDisplay();
    }

    // ── 背景の入力停止（パネル内は除く）──
    private void SetTitleInteractable(bool enabled)
    {
        if (enabled)
        {
            for (int i = 0; i < _blockedSelectables.Count; i++)
                if (_blockedSelectables[i] != null) _blockedSelectables[i].interactable = _blockedPrevState[i];
            _blockedSelectables.Clear();
            _blockedPrevState.Clear();
            return;
        }

        if (_blockedSelectables.Count > 0) return;

        var canvas = settingsPanel != null ? settingsPanel.transform.parent : null;
        if (canvas == null) return;

        var all = canvas.GetComponentsInChildren<Selectable>(true);
        foreach (var s in all)
        {
            if (s == null) continue;
            if (settingsPanel != null && s.transform.IsChildOf(settingsPanel.transform)) continue;

            _blockedSelectables.Add(s);
            _blockedPrevState.Add(s.interactable);
            s.interactable = false;
        }
    }

    // ── 設定内の項目（BGM / SE 行）。選択されるのは「行」で、Slider 自体は選択対象にしない ──
    private void SetupRows()
    {
        _bgmRow = EnsureRow(bgmLabel, bgmSlider);
        _seRow = EnsureRow(seLabel, seSlider);
        _childBgmRow = EnsureRow(childBgmLabel, childBgmSlider);
        _childSeRow = EnsureRow(childSeLabel, childSeSlider);
    }

    private void EnsureChildVolumeRows()
    {
        if (settingsPanel == null) return;

        Transform existingChildBgm = settingsPanel.transform.Find("ChildBgmLabel");
        if (existingChildBgm != null)
        {
            childBgmLabel = existingChildBgm.GetComponent<TextMeshProUGUI>();
            childBgmSlider = settingsPanel.transform.Find("ChildBgmSlider")?.GetComponent<Slider>();
        }
        if (existingChildBgm == null)
        {
            CreateRuntimeChildVolumeRow("Child BGM", "ChildBgmLabel", "ChildBgmSlider", -6f,
                out childBgmLabel, out childBgmSlider);
        }

        Transform existingChildSe = settingsPanel.transform.Find("ChildSeLabel");
        if (existingChildSe != null)
        {
            childSeLabel = existingChildSe.GetComponent<TextMeshProUGUI>();
            childSeSlider = settingsPanel.transform.Find("ChildSeSlider")?.GetComponent<Slider>();
        }
        if (existingChildSe == null)
        {
            CreateRuntimeChildVolumeRow("Child SE", "ChildSeLabel", "ChildSeSlider", -94f,
                out childSeLabel, out childSeSlider);
        }
    }

    private void CreateRuntimeChildVolumeRow(
        string labelText,
        string labelName,
        string sliderName,
        float y,
        out TextMeshProUGUI label,
        out Slider slider)
    {
        label = null;
        slider = null;

        TMP_Text fontSource = settingsPanel.GetComponentInChildren<TMP_Text>(true);
        if (fontSource == null) return;

        GameObject labelGo = new GameObject(labelName, typeof(RectTransform), typeof(CanvasRenderer));
        RectTransform labelRt = (RectTransform)labelGo.transform;
        labelRt.SetParent(settingsPanel.transform, false);
        labelRt.anchorMin = labelRt.anchorMax = new Vector2(0.5f, 0.5f);
        labelRt.pivot = new Vector2(0.5f, 0.5f);
        labelRt.anchoredPosition = new Vector2(-300f, y);
        labelRt.sizeDelta = new Vector2(220f, 56f);
        label = labelGo.AddComponent<TextMeshProUGUI>();
        label.font = fontSource.font;
        label.fontSharedMaterial = fontSource.fontSharedMaterial;
        label.text = labelText;
        label.fontSize = 36f;
        label.color = Color.white;
        label.alignment = TextAlignmentOptions.Center;
        label.raycastTarget = false;

        GameObject sliderGo = new GameObject(sliderName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        RectTransform sliderRt = (RectTransform)sliderGo.transform;
        sliderRt.SetParent(settingsPanel.transform, false);
        sliderRt.anchorMin = sliderRt.anchorMax = new Vector2(0.5f, 0.5f);
        sliderRt.pivot = new Vector2(0.5f, 0.5f);
        sliderRt.anchoredPosition = new Vector2(60f, y);
        sliderRt.sizeDelta = new Vector2(440f, 36f);
        sliderGo.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.18f);
        slider = sliderGo.AddComponent<Slider>();
        slider.transition = Selectable.Transition.None;

        GameObject fillAreaGo = new GameObject("Fill Area", typeof(RectTransform));
        RectTransform fillAreaRt = (RectTransform)fillAreaGo.transform;
        fillAreaRt.SetParent(sliderRt, false);
        fillAreaRt.anchorMin = Vector2.zero;
        fillAreaRt.anchorMax = Vector2.one;
        fillAreaRt.offsetMin = new Vector2(10f, 10f);
        fillAreaRt.offsetMax = new Vector2(-10f, -10f);

        GameObject fillGo = new GameObject("Fill", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        RectTransform fillRt = (RectTransform)fillGo.transform;
        fillRt.SetParent(fillAreaRt, false);
        fillRt.anchorMin = Vector2.zero;
        fillRt.anchorMax = new Vector2(0f, 1f);
        fillRt.sizeDelta = new Vector2(10f, 0f);
        fillGo.GetComponent<Image>().color = new Color(1f, 0.85f, 0.35f, 1f);

        GameObject handleAreaGo = new GameObject("Handle Slide Area", typeof(RectTransform));
        RectTransform handleAreaRt = (RectTransform)handleAreaGo.transform;
        handleAreaRt.SetParent(sliderRt, false);
        handleAreaRt.anchorMin = Vector2.zero;
        handleAreaRt.anchorMax = Vector2.one;
        handleAreaRt.offsetMin = new Vector2(10f, 0f);
        handleAreaRt.offsetMax = new Vector2(-10f, 0f);

        GameObject handleGo = new GameObject("Handle", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        RectTransform handleRt = (RectTransform)handleGo.transform;
        handleRt.SetParent(handleAreaRt, false);
        handleRt.anchorMin = Vector2.zero;
        handleRt.anchorMax = new Vector2(0f, 1f);
        handleRt.sizeDelta = new Vector2(28f, 0f);
        Image handleImage = handleGo.GetComponent<Image>();
        handleImage.color = Color.white;

        slider.fillRect = fillRt;
        slider.handleRect = handleRt;
        slider.targetGraphic = handleImage;
        slider.direction = Slider.Direction.LeftToRight;
        slider.minValue = 0f;
        slider.maxValue = 1f;
        slider.wholeNumbers = false;
    }

    private SettingsVolumeRow EnsureRow(TextMeshProUGUI label, Slider slider)
    {
        if (label == null || slider == null) return null;

        var row = label.GetComponent<SettingsVolumeRow>();
        if (row == null) row = label.gameObject.AddComponent<SettingsVolumeRow>();

        row.targetSlider = slider;
        row.stepPercent = Mathf.Clamp(volumeStepPercent, 0.01f, 0.5f);
        row.transition = Selectable.Transition.None;   // 見た目（色）は変えない。選択表示は矢印が担う。
        row.targetGraphic = null;
        return row;
    }

    /// <summary>
    /// ナビゲーションは上下のみ明示的に繋ぐ（左右は各コンポーネントが自分で処理し、選択は動かさない）。
    /// 下段の2ボタンは上下で行き来せず、左右で行き来する。
    /// ここは実行時の設定なので、Scene 側の Navigation だけを変えても戻らない点に注意。
    /// </summary>
    private void ConfigureNavigation()
    {
        SetNavigation(_bgmRow, up: null, down: _seRow, left: null, right: null);
        SetNavigation(_seRow, up: _bgmRow, down: _childBgmRow, left: null, right: null);
        SetNavigation(_childBgmRow, up: _seRow, down: _childSeRow, left: null, right: null);
        SetNavigation(_childSeRow, up: _childBgmRow, down: resetButton, left: null, right: null);
        SetNavigation(resetButton, up: _seRow, down: null, left: null, right: closeButton);
        SetNavigation(closeButton, up: _seRow, down: null, left: resetButton, right: null);
    }

    private static void SetNavigation(Selectable s, Selectable up, Selectable down, Selectable left, Selectable right)
    {
        if (s == null) return;

        Navigation nav = s.navigation;
        nav.mode = Navigation.Mode.Explicit;
        nav.selectOnUp = up;
        nav.selectOnDown = down;
        nav.selectOnLeft = left;
        nav.selectOnRight = right;
        s.navigation = nav;
    }

    /// <summary>設定内の項目を選択する（開いた直後は BGM、閉じたら「設定」入口へ戻す）。</summary>
    private void SelectItem(Selectable item)
    {
        var es = EventSystem.current;
        if (es == null || item == null) return;
        es.SetSelectedGameObject(item.gameObject);
    }

    // ── 暗転レイヤー（背景だけを暗くする。ぼかし画像とパネルの間に1つだけ置く）──
    private void BuildDimLayer()
    {
        if (settingsPanel == null) return;

        var parent = settingsPanel.transform.parent;
        if (parent == null) return;

        var go = new GameObject("SettingsDim", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        rt.SetSiblingIndex(settingsPanel.transform.GetSiblingIndex());   // ぼかしの上・パネルの下

        _dimImage = go.GetComponent<Image>();
        _dimImage.raycastTarget = false;                                  // クリックは透過
        _dimImage.color = new Color(0f, 0f, 0f, 0f);
        go.SetActive(false);
    }

    /// <summary>暗転を表示する。毎回 color を作り直すので、開き直しても暗さが重ならない。</summary>
    private void ShowDim()
    {
        if (_dimImage == null) return;
        _dimImage.color = new Color(0f, 0f, 0f, Mathf.Clamp01(dimOpacity));
        _dimImage.gameObject.SetActive(dimOpacity > 0f);
    }

    private void HideDim()
    {
        if (_dimImage == null) return;
        _dimImage.gameObject.SetActive(false);
    }

    // ── 設定中の選択矢印（タイトル矢印とは別オブジェクト。位置を書くのはこのクラスだけ）──
    private void BuildSettingsArrow()
    {
        if (settingsArrow != null)
        {
            _arrowRt = settingsArrow;                 // Scene で用意した矢印を使う場合
            _arrowRt.gameObject.SetActive(false);
            return;
        }

        if (settingsPanel == null) return;

        var go = new GameObject("SettingsArrow", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        _arrowRt = (RectTransform)go.transform;
        _arrowRt.SetParent(settingsPanel.transform, false);
        _arrowRt.anchorMin = new Vector2(0.5f, 0.5f);
        _arrowRt.anchorMax = new Vector2(0.5f, 0.5f);
        _arrowRt.pivot = new Vector2(0.5f, 0.5f);

        // タイトル矢印と「同じ見た目」にする（矢印自体は変更しない）
        RectTransform source = _highlight != null ? _highlight.ArrowIndicator : null;
        var img = go.GetComponent<Image>();
        img.raycastTarget = false;
        if (source != null)
        {
            _arrowRt.sizeDelta = source.sizeDelta;
            var src = source.GetComponent<Image>();
            if (src != null)
            {
                img.sprite = src.sprite;
                img.color = src.color;
                img.material = src.material;
                img.type = src.type;
                img.preserveAspect = src.preserveAspect;
            }
        }
        else
        {
            _arrowRt.sizeDelta = new Vector2(48f, 48f);
            img.color = Color.white;
        }

        go.SetActive(false);
    }

    private void ShowArrow()
    {
        if (_arrowRt == null) return;
        SnapArrowToSelection();
        _arrowRt.gameObject.SetActive(true);
    }

    private void HideArrow()
    {
        if (_arrowRt == null) return;
        _arrowRt.gameObject.SetActive(false);
    }

    /// <summary>選択中の項目の左へ矢印を合わせる（追従先と同じ親なら anchoredPosition、違うならワールド座標）。</summary>
    private void SnapArrowToSelection()
    {
        if (_arrowRt == null) return;

        RectTransform target = GetSelectedItemRect();
        if (target == null) return;

        if (_arrowRt.parent == target.parent)
        {
            _arrowRt.anchoredPosition = target.anchoredPosition + settingsArrowOffset;
        }
        else
        {
            _arrowRt.position = target.position + (Vector3)settingsArrowOffset;
        }
    }

    /// <summary>いま EventSystem が選択している項目（パネル内のみ）。</summary>
    private RectTransform GetSelectedItemRect()
    {
        var es = EventSystem.current;
        if (es == null) return null;

        GameObject go = es.currentSelectedGameObject;
        if (go == null || settingsPanel == null) return null;
        if (!go.transform.IsChildOf(settingsPanel.transform)) return null;

        return go.transform as RectTransform;
    }

    private void Update()
    {
        if (!_isOpen) return;

        // マウスで Slider 自体が選択された場合も、入力と選択表示を「行」に揃える。
        // （左右の音量変更を常に 5% の行処理へ一本化し、Slider 標準の左右入力と二重処理しない）
        var es = EventSystem.current;
        if (es != null)
        {
            GameObject sel = es.currentSelectedGameObject;
            if (sel != null)
            {
                if (bgmSlider != null && sel == bgmSlider.gameObject) SelectItem(_bgmRow);
                else if (seSlider != null && sel == seSlider.gameObject) SelectItem(_seRow);
            }
        }

        if (_arrowRt != null && _arrowRt.gameObject.activeSelf) SnapArrowToSelection();
    }

    // ── 背景ぼかし（実行時リソース。生成と解放のみここで行う）──
    private void BuildBlurImage()
    {
        if (settingsPanel == null) return;

        var parent = settingsPanel.transform.parent;
        if (parent == null) return;

        var go = new GameObject("SettingsBlur", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        rt.SetSiblingIndex(settingsPanel.transform.GetSiblingIndex());   // パネルの背後に描く

        _blurImage = go.GetComponent<RawImage>();
        _blurImage.raycastTarget = false;
        // 上下反転の補正は「ここ1箇所」にまとめる（キャプチャRTは上下が逆に取り込まれる環境がある）。
        // 各 Blit や Shader 側では反転させない。
        _blurImage.uvRect = flipCaptureVertical ? new Rect(0f, 1f, 1f, -1f) : new Rect(0f, 0f, 1f, 1f);
        go.SetActive(false);
    }

    /// <summary>キャプチャ→（必要なら）ぼかし。パネルの表示は呼び出し側（OpenRoutine）が行う。</summary>
    private IEnumerator CaptureAndBlur()
    {
        if (_blurImage == null) yield break;

        int sw = Screen.width, sh = Screen.height;
        if (sw <= 0 || sh <= 0) yield break;

        int d = Mathf.Clamp(blurDownscale, 1, 8);
        int bw = Mathf.Max(1, sw / d);
        int bh = Mathf.Max(1, sh / d);

        if (_blurRt == null || _blurRt.width != bw || _blurRt.height != bh)
        {
            if (_blurRt != null) { _blurRt.Release(); Destroy(_blurRt); }
            _blurRt = new RenderTexture(bw, bh, 0, RenderTextureFormat.ARGB32);
            _blurRt.Create();
        }

        var full = RenderTexture.GetTemporary(sw, sh, 0, RenderTextureFormat.ARGB32);
        var tmp = RenderTexture.GetTemporary(bw, bh, 0, RenderTextureFormat.ARGB32);

        ScreenCapture.CaptureScreenshotIntoRenderTexture(full);
        yield return new WaitForEndOfFrame();   // 取り込み完了を待つ

        if (debugSkipBlur)
        {
            // 確認用：ぼかしを通さずそのまま（向き・位置・比率の確認）
            Graphics.Blit(full, _blurRt);
        }
        else
        {
            Shader shader = Shader.Find("Parent/TitleBackgroundBlur");
            if (shader == null)
            {
                Debug.LogError("[ParentVolumeSettingsPanel] シェーダ Parent/TitleBackgroundBlur が見つからないためぼかしを適用できません。", this);
                Graphics.Blit(full, _blurRt);
            }
            else
            {
                if (_blurMaterial == null) _blurMaterial = new Material(shader);
                _blurMaterial.SetFloat("_BlurRadius", blurRadius);

                _blurMaterial.SetVector("_Direction", new Vector4(1f, 0f, 0f, 0f));   // 横
                Graphics.Blit(full, tmp, _blurMaterial);

                _blurMaterial.SetVector("_Direction", new Vector4(0f, 1f, 0f, 0f));   // 縦
                Graphics.Blit(tmp, _blurRt, _blurMaterial);
            }
        }

        _blurImage.texture = _blurRt;
        _blurImage.color = Color.white;

        RenderTexture.ReleaseTemporary(full);
        RenderTexture.ReleaseTemporary(tmp);
    }

    private void ReleaseBlur()
    {
        if (_blurImage != null)
        {
            _blurImage.texture = null;
            _blurImage.gameObject.SetActive(false);
        }

        if (_blurRt != null)
        {
            _blurRt.Release();
            Destroy(_blurRt);
            _blurRt = null;
        }
    }

    private void OnDisable()
    {
        if (_openRoutine != null) { StopCoroutine(_openRoutine); _openRoutine = null; }
        _opening = false;
        _isOpen = false;
        HideDim();      // 無効化・準備中断でも暗転を残さない
        HideArrow();
    }

    private void OnDestroy()
    {
        HideDim();
        HideArrow();
        ReleaseBlur();
        if (_blurMaterial != null) { Destroy(_blurMaterial); _blurMaterial = null; }
    }
}
