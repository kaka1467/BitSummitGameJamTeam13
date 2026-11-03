using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// フィーバー中の「スコア2倍」を分かりやすく見せる表示（C22）。
///  ・スコアの桁を金色に光らせる（フィーバー中だけ。終わったら元の色へ戻す）。
///  ・スコアの上に「SCORE x2」のバッジを出し、脈動させる。
///  ・（任意）2倍で加点されたとき、スコア付近に「+1200 x2」を浮かび上がらせる。既定はOFF（プレイヤーの右上の表示と重なるため）。
///
/// 表示用のTMPは、バッジ・ポップアップとも未割り当てならスコア桁と同じフォント・同じ親の下に実行時に作る
/// （シーンの編集は不要）。GameManager が、シーンに無ければ自動で追加する。
/// 動きは実時間（unscaled）で進めるため、寝たふり中の停止やQTE中でも止まらない。
/// </summary>
public class ScoreMultiplierDisplay : MonoBehaviour
{
    [Header("バッジ（SCORE x2）")]
    [Tooltip("バッジ用のTMP。未割り当てなら、スコア桁と同じフォントで実行時に作る。")]
    [SerializeField] private TextMeshProUGUI badgeText;
    [SerializeField] private string badgeLabel = "SCORE x2";
    [Tooltip("バッジの位置。スコア桁の列の中央からのずれ（スコア表示の座標系）。")]
    [SerializeField] private Vector2 badgeOffset = new Vector2(0f, 110f);
    [Tooltip("バッジの文字の大きさ（スコア桁に対する倍率）。")]
    [SerializeField, Min(0.1f)] private float badgeSizeRatio = 0.6f;
    [SerializeField] private float pulseSpeed = 6f;
    [SerializeField, Min(0f)] private float pulseAmount = 0.12f;

    [Header("スコア桁の強調")]
    [SerializeField] private Color feverColor = new Color(1f, 0.85f, 0.2f, 1f);
    [Tooltip("ONなら、フィーバー中はスコア桁を金色にする。")]
    [SerializeField] private bool tintDigits = true;

    [Header("加点ポップアップ（+1200 x2）")]
    [Tooltip("スコア付近のポップアップ。プレイヤーの右上に出す表示（PlayerScorePopup）が加点を示すため、既定はOFF。")]
    [SerializeField] private bool showGainPopup = false;
    [Tooltip("ポップアップの開始位置。最下位桁の位置からのずれ（スコア表示の座標系）。")]
    [SerializeField] private Vector2 popupOffset = new Vector2(0f, 60f);
    [SerializeField, Min(0f)] private float popupRiseDistance = 90f;
    [SerializeField, Min(0.1f)] private float popupSeconds = 0.9f;
    [SerializeField, Min(0.1f)] private float popupSizeRatio = 0.5f;

    private class Popup
    {
        public TextMeshProUGUI text;
        public float age;
        public Vector2 start;
        public bool active;
    }

    private const int PopupPoolSize = 4;

    private GameManager _gm;
    private TextMeshProUGUI[] _digits;
    private Color[] _originalColors;
    private RectTransform _parent;
    private readonly List<Popup> _popups = new List<Popup>();
    private bool _feverShown;
    private bool _built;
    private Vector3 _badgeBaseScale = Vector3.one;

    private void OnEnable()
    {
        GameManager.ScoreAdded += HandleScoreAdded;
    }

    private void OnDisable()
    {
        GameManager.ScoreAdded -= HandleScoreAdded;
        SetFeverVisual(false);
    }

    private void Update()
    {
        if (!_built) TryBuild();
        if (!_built) return;

        bool fever = _gm != null && _gm.IsFeverActive;
        if (fever != _feverShown) SetFeverVisual(fever);

        if (fever)
        {
            float wave = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * pulseSpeed);

            if (badgeText != null)
                badgeText.rectTransform.localScale = _badgeBaseScale * (1f + pulseAmount * wave);

            if (tintDigits)
            {
                Color c = Color.Lerp(Color.white, feverColor, 0.7f + 0.3f * wave);
                for (int i = 0; i < _digits.Length; i++)
                    if (_digits[i] != null) _digits[i].color = c;
            }
        }

        UpdatePopups();
    }

    /// <summary>GameManager とスコア桁が揃ったら、バッジとポップアップ用のTMPを用意する。</summary>
    private void TryBuild()
    {
        _gm = GameManager.instance;
        if (_gm == null || _gm.scoreDigitTexts == null || _gm.scoreDigitTexts.Length == 0) return;

        TextMeshProUGUI first = _gm.scoreDigitTexts[0];
        if (first == null) return;

        _digits = _gm.scoreDigitTexts;
        _originalColors = new Color[_digits.Length];
        for (int i = 0; i < _digits.Length; i++)
            _originalColors[i] = _digits[i] != null ? _digits[i].color : Color.white;

        _parent = first.rectTransform.parent as RectTransform;
        if (_parent == null) return;

        if (badgeText == null)
            badgeText = CreateText("FeverScoreBadge", first, badgeSizeRatio, TextAlignmentOptions.Center);

        if (badgeText != null)
        {
            badgeText.text = badgeLabel;
            badgeText.color = feverColor;
            badgeText.rectTransform.anchoredPosition = GetRowCenter() + badgeOffset;
            _badgeBaseScale = badgeText.rectTransform.localScale;
            badgeText.gameObject.SetActive(false);
        }

        for (int i = 0; showGainPopup && i < PopupPoolSize; i++)
        {
            TextMeshProUGUI t = CreateText("FeverGainPopup", first, popupSizeRatio, TextAlignmentOptions.Center);
            if (t == null) break;
            t.color = feverColor;
            t.gameObject.SetActive(false);
            _popups.Add(new Popup { text = t });
        }

        _built = true;
    }

    /// <summary>スコア桁と同じフォント・同じ親・同じ座標系で、表示用のTMPを1つ作る。</summary>
    private TextMeshProUGUI CreateText(string objectName, TextMeshProUGUI like, float sizeRatio, TextAlignmentOptions alignment)
    {
        var go = new GameObject(objectName, typeof(RectTransform));
        go.transform.SetParent(_parent, false);
        go.transform.SetAsLastSibling();

        RectTransform rect = go.GetComponent<RectTransform>();
        RectTransform src = like.rectTransform;
        rect.anchorMin = src.anchorMin;
        rect.anchorMax = src.anchorMax;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(Mathf.Max(600f, src.sizeDelta.x * 3f), src.sizeDelta.y);
        rect.localScale = src.localScale;   // 桁と同じ拡大率にそろえる（文字の大きさは fontSize の倍率で決める）

        TextMeshProUGUI text = go.AddComponent<TextMeshProUGUI>();
        text.font = like.font;
        text.fontSharedMaterial = like.fontSharedMaterial;
        text.fontSize = like.fontSize * sizeRatio;
        text.alignment = alignment;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Overflow;
        text.raycastTarget = false;
        return text;
    }

    /// <summary>スコア桁の列の中央（先頭桁と最下位桁の中間）。</summary>
    private Vector2 GetRowCenter()
    {
        TextMeshProUGUI a = _digits[0];
        TextMeshProUGUI b = _digits[_digits.Length - 1];
        Vector2 pa = a != null ? a.rectTransform.anchoredPosition : Vector2.zero;
        Vector2 pb = b != null ? b.rectTransform.anchoredPosition : pa;
        return (pa + pb) * 0.5f;
    }

    private void SetFeverVisual(bool fever)
    {
        _feverShown = fever;
        if (!_built) return;

        if (badgeText != null)
        {
            badgeText.gameObject.SetActive(fever);
            badgeText.rectTransform.localScale = _badgeBaseScale;
        }

        if (!fever)
        {
            for (int i = 0; i < _digits.Length; i++)
                if (_digits[i] != null) _digits[i].color = _originalColors[i];
        }
    }

    private void HandleScoreAdded(int gained, bool doubled)
    {
        if (!showGainPopup || !_built || !doubled || gained <= 0) return;

        Popup popup = null;
        for (int i = 0; i < _popups.Count; i++)
        {
            if (!_popups[i].active) { popup = _popups[i]; break; }
        }

        if (popup == null) popup = _popups[0];   // 全部使用中なら一番古い扱いで上書きする

        TextMeshProUGUI last = _digits[_digits.Length - 1];
        Vector2 basePos = last != null ? last.rectTransform.anchoredPosition : Vector2.zero;

        popup.start = basePos + popupOffset;
        popup.age = 0f;
        popup.active = true;
        popup.text.text = $"+{gained} x2";
        popup.text.rectTransform.anchoredPosition = popup.start;
        popup.text.gameObject.SetActive(true);
    }

    private void UpdatePopups()
    {
        for (int i = 0; i < _popups.Count; i++)
        {
            Popup p = _popups[i];
            if (!p.active) continue;

            p.age += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(p.age / popupSeconds);

            p.text.rectTransform.anchoredPosition = p.start + new Vector2(0f, popupRiseDistance * t);

            Color c = feverColor;
            c.a = 1f - t * t;   // 後半で素早く消える
            p.text.color = c;

            if (t >= 1f)
            {
                p.active = false;
                p.text.gameObject.SetActive(false);
            }
        }
    }
}
