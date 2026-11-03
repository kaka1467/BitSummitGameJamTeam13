using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// 得点が増減したとき、プレイヤーキャラの右上に「+300」のような数字を浮かび上がらせる。
///  ・加点は白、フィーバー2倍が掛かった加点は金色（「+1200 x2」）、減点は赤（「-500」）。
///  ・位置はプレイヤーの絵の右上（絵の範囲の右上の角）に、表示の座標系での小さなずれを足したところ。
///  ・表示用のTMPは、スコア桁と同じフォント・同じCanvas上に実行時に作る（シーンの編集は不要）。
///  ・GameManager が、シーンに無ければ自動で追加する。動きは実時間なので、寝たふり中などの停止の影響を受けない。
/// </summary>
public class PlayerScorePopup : MonoBehaviour
{
    [Header("位置")]
    [Tooltip("プレイヤーの絵の右上の角からのずれ（Canvas座標）。")]
    [SerializeField] private Vector2 offset = new Vector2(20f, 20f);
    [SerializeField, Min(0f)] private float riseDistance = 90f;

    [Header("見た目")]
    [SerializeField, Min(1f)] private float fontSize = 56f;
    [SerializeField] private Color gainColor = Color.white;
    [SerializeField] private Color doubledColor = new Color(1f, 0.85f, 0.2f, 1f);
    [SerializeField] private Color lossColor = new Color(1f, 0.3f, 0.3f, 1f);
    [SerializeField, Min(0.1f)] private float seconds = 0.9f;
    [Tooltip("出た瞬間の拡大率（1にすると拡大なし）。出てから通常の大きさへ戻る。")]
    [SerializeField, Min(1f)] private float popScale = 1.35f;

    [Header("表示する対象")]
    [SerializeField] private bool showGain = true;
    [SerializeField] private bool showLoss = true;

    private class Popup
    {
        public TextMeshProUGUI text;
        public float age;
        public Vector2 start;
        public Color color;
        public bool active;
    }

    private const int PoolSize = 6;

    private readonly List<Popup> _popups = new List<Popup>();
    private RectTransform _canvasRect;
    private Canvas _canvas;
    private Transform _player;
    private Renderer _playerRenderer;
    private bool _built;

    private void OnEnable()
    {
        GameManager.ScoreAdded += HandleScoreAdded;
    }

    private void OnDisable()
    {
        GameManager.ScoreAdded -= HandleScoreAdded;
    }

    private void Update()
    {
        if (!_built) TryBuild();

        for (int i = 0; i < _popups.Count; i++)
        {
            Popup p = _popups[i];
            if (!p.active) continue;

            p.age += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(p.age / seconds);

            p.text.rectTransform.anchoredPosition = p.start + new Vector2(0f, riseDistance * t);

            // 出た直後だけ大きく、すぐ通常の大きさへ戻す。
            float pop = Mathf.Lerp(popScale, 1f, Mathf.Clamp01(p.age / 0.15f));
            p.text.rectTransform.localScale = Vector3.one * pop;

            Color c = p.color;
            c.a = 1f - t * t * t;   // 前半ははっきり、最後に素早く消える
            p.text.color = c;

            if (t >= 1f)
            {
                p.active = false;
                p.text.gameObject.SetActive(false);
            }
        }
    }

    /// <summary>GameManager とスコア桁が揃ったら、スコア桁と同じCanvas上に表示用のTMPを作る。</summary>
    private void TryBuild()
    {
        GameManager gm = GameManager.instance;
        if (gm == null || gm.scoreDigitTexts == null || gm.scoreDigitTexts.Length == 0) return;

        TextMeshProUGUI like = gm.scoreDigitTexts[0];
        if (like == null) return;

        _canvas = like.canvas != null ? like.canvas.rootCanvas : null;
        if (_canvas == null) return;
        _canvasRect = _canvas.transform as RectTransform;
        if (_canvasRect == null) return;

        for (int i = 0; i < PoolSize; i++)
        {
            var go = new GameObject("PlayerScorePopup", typeof(RectTransform));
            go.transform.SetParent(_canvasRect, false);
            go.transform.SetAsLastSibling();

            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);   // Canvas中心基準の座標で置く
            rect.pivot = new Vector2(0f, 0f);                             // 左下を基準にして、右上へ伸ばす
            rect.sizeDelta = new Vector2(800f, 100f);

            TextMeshProUGUI text = go.AddComponent<TextMeshProUGUI>();
            text.font = like.font;
            text.fontSharedMaterial = like.fontSharedMaterial;
            text.fontSize = fontSize;
            text.alignment = TextAlignmentOptions.BottomLeft;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Overflow;
            text.raycastTarget = false;
            go.SetActive(false);

            _popups.Add(new Popup { text = text });
        }

        _built = true;
    }

    private void HandleScoreAdded(int gained, bool doubled)
    {
        if (!_built || gained == 0) return;
        if (gained > 0 && !showGain) return;
        if (gained < 0 && !showLoss) return;

        if (!TryGetStartPosition(out Vector2 start)) return;

        Popup popup = null;
        for (int i = 0; i < _popups.Count; i++)
        {
            if (!_popups[i].active) { popup = _popups[i]; break; }
        }

        if (popup == null) popup = _popups[0];   // 全部使用中なら先頭を使い回す

        popup.start = start;
        popup.age = 0f;
        popup.active = true;
        popup.color = gained < 0 ? lossColor : (doubled ? doubledColor : gainColor);
        popup.text.text = gained < 0 ? gained.ToString() : (doubled ? $"+{gained} x2" : $"+{gained}");
        popup.text.color = popup.color;
        popup.text.rectTransform.anchoredPosition = start;
        popup.text.rectTransform.localScale = Vector3.one * popScale;
        popup.text.gameObject.SetActive(true);
    }

    /// <summary>プレイヤーの絵の右上の角を、Canvas座標へ変換して返す。</summary>
    private bool TryGetStartPosition(out Vector2 position)
    {
        position = Vector2.zero;

        Camera cam = Camera.main;
        if (cam == null) return false;

        if (_player == null)
        {
            GameObject p = GameObject.FindWithTag("Player");
            if (p == null) return false;
            _player = p.transform;
            _playerRenderer = p.GetComponentInChildren<Renderer>();
        }

        Vector3 corner;
        if (_playerRenderer != null)
        {
            Bounds b = _playerRenderer.bounds;
            corner = new Vector3(b.max.x, b.max.y, b.center.z);
        }
        else
        {
            corner = _player.position;
        }

        Vector2 screen = cam.WorldToScreenPoint(corner);
        Camera uiCam = (_canvas.renderMode == RenderMode.ScreenSpaceOverlay) ? null : _canvas.worldCamera;

        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvasRect, screen, uiCam, out Vector2 local))
            return false;

        position = local + offset;
        return true;
    }
}
