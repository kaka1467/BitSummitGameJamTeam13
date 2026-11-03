using UnityEngine;

/// <summary>
/// フィーバー中、サイレン（ItemType.BGM）の上に「×」を重ねて、効果が無効なことを伝える（C24）。
///  ・Item が、サイレンのときだけ実行時に追加する（Prefabの編集は不要）。
///  ・フィーバー中だけ表示し、終わったら隠す。画面にあるサイレンにも、フィーバー中に出たサイレンにも付く。
///  ・画像は Resources/FeverDisabledMark（ドット絵なので Point フィルタ）。無ければ何もしない。
///  ・サイレンの絵の中心に、絵の幅の markSizeRatio 倍の大きさで重ねる。
/// </summary>
public class FeverDisabledMark : MonoBehaviour
{
    private const string ResourcePath = "FeverDisabledMark";
    private const float MarkSizeRatio = 0.9f;

    private static Sprite s_sprite;
    private static bool s_spriteLoadTried;

    private SpriteRenderer _markRenderer;
    private SpriteRenderer _itemRenderer;
    private bool _shown;

    private void OnEnable()
    {
        _shown = false;
        if (_markRenderer != null) _markRenderer.enabled = false;
    }

    private void OnDisable()
    {
        _shown = false;
        if (_markRenderer != null) _markRenderer.enabled = false;
    }

    private void Update()
    {
        GameManager gm = GameManager.instance;
        bool fever = gm != null && gm.IsFeverActive;

        if (fever == _shown) return;

        if (fever && !EnsureMark()) return;

        _shown = fever;
        if (_markRenderer != null) _markRenderer.enabled = fever;
    }

    private void LateUpdate()
    {
        // 表示中は絵の中心・大きさに追従させる（アイテムの絵が差し替わっても合うように）。
        if (_shown && _markRenderer != null && _itemRenderer != null)
            Fit();
    }

    /// <summary>×の表示用SpriteRendererを（初回だけ）作る。画像が無ければ false。</summary>
    private bool EnsureMark()
    {
        if (_markRenderer != null) return true;

        if (!s_spriteLoadTried)
        {
            s_spriteLoadTried = true;
            s_sprite = Resources.Load<Sprite>(ResourcePath);
            if (s_sprite == null)
                Debug.LogWarning($"[FeverDisabledMark] Resources/{ResourcePath} が見つかりません（Spriteとして取り込まれているか確認してください）。");
        }

        if (s_sprite == null) return false;

        _itemRenderer = FindItemRenderer();

        var go = new GameObject("FeverDisabledMark");
        go.transform.SetParent(transform, false);

        _markRenderer = go.AddComponent<SpriteRenderer>();
        _markRenderer.sprite = s_sprite;
        _markRenderer.enabled = false;

        if (_itemRenderer != null)
        {
            _markRenderer.sortingLayerID = _itemRenderer.sortingLayerID;
            _markRenderer.sortingOrder = _itemRenderer.sortingOrder + 1;
        }

        Fit();
        return true;
    }

    /// <summary>サイレン本体の絵（×自身は除く）。</summary>
    private SpriteRenderer FindItemRenderer()
    {
        SpriteRenderer[] all = GetComponentsInChildren<SpriteRenderer>(true);
        for (int i = 0; i < all.Length; i++)
        {
            if (_markRenderer != null && all[i] == _markRenderer) continue;
            if (all[i].gameObject.name == "FeverDisabledMark") continue;
            return all[i];
        }

        return null;
    }

    private void Fit()
    {
        if (_itemRenderer == null) _itemRenderer = FindItemRenderer();
        if (_itemRenderer == null || s_sprite == null) return;

        Bounds itemBounds = _itemRenderer.bounds;
        Transform t = _markRenderer.transform;

        t.position = itemBounds.center;

        float spriteWidth = s_sprite.bounds.size.x;
        float parentScale = Mathf.Abs(transform.lossyScale.x);
        if (spriteWidth <= 0f || parentScale <= 0f) return;

        float worldScale = (itemBounds.size.x * MarkSizeRatio) / spriteWidth;
        float local = worldScale / parentScale;
        t.localScale = new Vector3(local, local, 1f);
    }
}
