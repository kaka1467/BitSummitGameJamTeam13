using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// FeverReachEffect（E02）:
/// 「フィーバー中ではなく、発動まで残り1枚」のときだけ、既存のフィーバー枚数表示と
/// 画面内のフィーバーカードへ穏やかな強調（明滅・発光・わずかな拡縮）を付ける。
///
/// 方針（既存を壊さないための約束）:
///  ・枚数・必要枚数・フィーバー状態は GameManager の既存値を参照するだけ（別の管理値を作らない）。
///  ・既存の色・スケールを覚えてから掛け、終了時に必ず元へ戻す（値を累積して掛けない）。
///  ・共有 Material／共有 Sprite は変更しない（SpriteRenderer.color と transform だけを触る）。
///  ・取得判定・Collider・移動速度・出現配置には触れない
///    （Collider が付いているルートのスケールは動かさない）。
///  ・子機停止（Time.timeScale = 0。ChildGamePause／ChildSleepLockApplier）に自動追従するよう
///    scaled な Time.deltaTime だけを使う（この演出だけ勝手に進め続けない）。
///  ・新しい画像素材・新しいSE・常設UIは追加しない。
/// </summary>
[DisallowMultipleComponent]
public class FeverReachEffect : MonoBehaviour
{
    [Header("有効/無効")]
    [Tooltip("リーチ強調を有効にする。OFFなら一切何もしない（元の見た目のまま）。")]
    [SerializeField] private bool enableReachEffect = true;

    [Header("強さ（控えめな既定値。Inspectorで調整可）")]
    [Tooltip("明滅の速さ（回/秒）。")]
    [SerializeField, Min(0.05f)] private float pulseCyclesPerSecond = 0.7f;
    [Tooltip("カードの発光の強さ（0〜1。元の色からこの割合だけ発光色へ寄せる）。")]
    [SerializeField, Range(0f, 1f)] private float cardGlowStrength = 0.35f;
    [Tooltip("カードの発光色。")]
    [SerializeField] private Color cardGlowColor = new Color(1f, 0.92f, 0.45f, 1f);
    [Tooltip("カードの拡縮幅（0〜0.2。元スケールに対する割合）。Colliderが付いたルートでは適用しない。")]
    [SerializeField, Range(0f, 0.2f)] private float cardScaleAmplitude = 0.06f;
    [Tooltip("枚数表示の明滅の強さ（0〜1。元の色のアルファをこの割合だけ下げる）。")]
    [SerializeField, Range(0f, 1f)] private float gaugeBlinkStrength = 0.35f;

    [Header("参照・再検出")]
    [Tooltip("枚数表示。未設定ならシーンから探す。")]
    [SerializeField] private FeverGaugeUI gaugeUI;
    [Tooltip("カードの再検出間隔（秒）。後から出現したカードにも反映するため。")]
    [SerializeField, Min(0.1f)] private float cardRefreshSeconds = 0.5f;

    private readonly Dictionary<Item, CardState> _cards = new Dictionary<Item, CardState>();
    private readonly List<Item> _stale = new List<Item>();
    private readonly Dictionary<Image, GaugeState> _gaugeStates = new Dictionary<Image, GaugeState>();

    private bool _reachActive;
    private float _phase;
    private float _nextRefreshAt;

    /// <summary>1枚のカードについて、掛ける前の見た目と「直前に自分が書いた値」を覚えておく。</summary>
    private sealed class CardState
    {
        public SpriteRenderer renderer;
        public Color baseColor;
        public Vector3 baseScale;
        public bool canScale;          // Collider保護：ルートや Collider 付きの絵は拡縮しない
        public Color appliedColor;     // 直前に自分が書いた色
        public Vector3 appliedScale;   // 直前に自分が書いたスケール
        public bool hasApplied;
    }

    /// <summary>枚数表示1枠について、元の色と「直前に自分が書いた色」を覚えておく。</summary>
    private sealed class GaugeState
    {
        public Color baseColor;
        public Color appliedColor;
        public bool hasApplied;
    }

    private void Awake()
    {
        if (gaugeUI == null) gaugeUI = FindFirstObjectByType<FeverGaugeUI>();
        CaptureGaugeBaseColors();
    }

    private void OnDisable()
    {
        // オブジェクト無効化で元の見た目へ戻す。
        RestoreAll();
    }

    private void OnDestroy()
    {
        RestoreAll();
    }

    private void Update()
    {
        bool reach = enableReachEffect && IsReach();

        if (reach != _reachActive)
        {
            _reachActive = reach;
            _phase = 0f;
            _nextRefreshAt = 0f;

            if (!reach) RestoreAll();     // フィーバー開始・枚数リセット・ゲーム終了で元へ戻す
        }

        if (!_reachActive) return;

        // 明滅は scaled 時間で進める（子機停止中は止まる）。
        _phase += Time.deltaTime * Mathf.Max(0.05f, pulseCyclesPerSecond);
        float pulse = 0.5f * (1f - Mathf.Cos(_phase * Mathf.PI * 2f));   // 0〜1 の滑らかな往復

        // 後から出現したカードも拾うため、一定間隔で取り直す。
        if (Time.time >= _nextRefreshAt)
        {
            _nextRefreshAt = Time.time + cardRefreshSeconds;
            RefreshCards();
        }

        ApplyPulse(pulse);
    }

    /// <summary>リーチ判定：フィーバー中ではなく、発動まで残り1枚。既存状態のみを参照する。</summary>
    private bool IsReach()
    {
        GameManager gm = GameManager.instance;
        if (gm == null) return false;
        if (gm.IsFeverActive) return false;

        int needed = Mathf.Max(1, gm.feverNeeded);
        return gm.feverCount == needed - 1;
    }

    // ── カード ────────────────────────────────────────────────────────────────
    /// <summary>画面内のフィーバーカードを集め直し、消えたカードは元へ戻して忘れる。</summary>
    private void RefreshCards()
    {
        Item[] items = FindObjectsByType<Item>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);

        // 取得・プール返却・無効化で見つからなくなったカードは元へ戻す
        _stale.Clear();
        foreach (var pair in _cards)
        {
            if (pair.Key == null || System.Array.IndexOf(items, pair.Key) < 0) _stale.Add(pair.Key);
        }
        foreach (Item item in _stale) ForgetCard(item);

        // 新しく出たカードを登録（リーチになった時点で画面内にあるカードもここで入る）
        foreach (Item item in items)
        {
            if (item == null || item.itemType != ItemType.Fever) continue;
            if (_cards.ContainsKey(item)) continue;
            RegisterCard(item);
        }
    }

    private void RegisterCard(Item item)
    {
        SpriteRenderer sr = FindItemRenderer(item);
        if (sr == null) return;

        _cards[item] = new CardState
        {
            renderer = sr,
            baseColor = sr.color,
            baseScale = sr.transform.localScale,
            // 拡縮してよいのは「Item の子の絵」かつ「その絵と子孫に Collider が無い」場合だけ。
            // ルートや Collider 付きの絵を拡縮すると取得判定（Collider）まで変わってしまうため、
            // その場合は拡縮を使わず色の強調だけにする。
            canScale = sr.transform != item.transform && !HasCollider(sr.transform),
        };
    }

    /// <summary>この絵、またはその子孫に Collider があるか（あるなら拡縮しない）。</summary>
    private static bool HasCollider(Transform t)
    {
        if (t == null) return false;
        if (t.GetComponent<Collider2D>() != null || t.GetComponent<Collider>() != null) return true;
        if (t.GetComponentInChildren<Collider2D>(true) != null) return true;
        if (t.GetComponentInChildren<Collider>(true) != null) return true;
        return false;
    }

    private void ForgetCard(Item item)
    {
        if (item == null) { _cards.Remove(item); return; }

        if (_cards.TryGetValue(item, out CardState state)) RestoreCardState(state);

        _cards.Remove(item);
    }

    /// <summary>アイテム本体の絵（付属の印などは除く）。FeverDisabledMark と同じ探し方。</summary>
    private static SpriteRenderer FindItemRenderer(Item item)
    {
        SpriteRenderer[] all = item.GetComponentsInChildren<SpriteRenderer>(true);
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] == null) continue;
            if (all[i].gameObject.name == "FeverDisabledMark") continue;
            return all[i];
        }
        return null;
    }

    /// <summary>pulse(0〜1) を既存の見た目へ控えめに重ねる。毎回「元の値」から作り直す（累積しない）。</summary>
    private void ApplyPulse(float pulse)
    {
        foreach (var pair in _cards)
        {
            CardState s = pair.Value;
            if (s == null || s.renderer == null) continue;

            s.renderer.color = Color.Lerp(s.baseColor, cardGlowColor, cardGlowStrength * pulse);
            s.appliedColor = s.renderer.color;
            s.hasApplied = true;

            if (s.canScale)
            {
                s.renderer.transform.localScale = s.baseScale * (1f + cardScaleAmplitude * pulse);
                s.appliedScale = s.renderer.transform.localScale;
            }
        }

        ApplyGaugeBlink(pulse);
    }

    // ── 枚数表示（既存の FeverGaugeUI を参照するだけ。色相・スケールは変えない）────────
    private void CaptureGaugeBaseColors()
    {
        _gaugeStates.Clear();
        if (gaugeUI == null || gaugeUI.gaugeImages == null) return;

        foreach (Image img in gaugeUI.gaugeImages)
        {
            if (img == null || _gaugeStates.ContainsKey(img)) continue;
            _gaugeStates[img] = new GaugeState { baseColor = img.color };
        }
    }

    private void ApplyGaugeBlink(float pulse)
    {
        if (_gaugeStates.Count == 0) CaptureGaugeBaseColors();

        foreach (var pair in _gaugeStates)
        {
            Image img = pair.Key;
            if (img == null) continue;

            GaugeState s = pair.Value;
            Color c = s.baseColor;
            c.a = s.baseColor.a * (1f - gaugeBlinkStrength * (1f - pulse));
            img.color = c;
            s.appliedColor = c;
            s.hasApplied = true;
        }
    }

    // ── 後始末 ────────────────────────────────────────────────────────────────
    /// <summary>
    /// カードの強調を外す。戻すのは「自分が書いた値のままのとき」だけ。
    /// 取得・プール返却・再配置など他の処理が新しい値を書いていたら巻き戻さない。
    /// </summary>
    private static void RestoreCardState(CardState s)
    {
        if (s == null || s.renderer == null || !s.hasApplied) return;

        if (s.renderer.color == s.appliedColor) s.renderer.color = s.baseColor;
        if (s.canScale && s.renderer.transform.localScale == s.appliedScale)
            s.renderer.transform.localScale = s.baseScale;

        s.hasApplied = false;
    }

    private static void RestoreGaugeState(Image img, GaugeState s)
    {
        if (img == null || s == null || !s.hasApplied) return;

        if (img.color == s.appliedColor) img.color = s.baseColor;
        s.hasApplied = false;
    }

    /// <summary>掛けた強調をすべて外し、元の色・スケールへ戻す。</summary>
    private void RestoreAll()
    {
        foreach (var pair in _cards) RestoreCardState(pair.Value);
        _cards.Clear();

        foreach (var pair in _gaugeStates) RestoreGaugeState(pair.Key, pair.Value);
    }
}
