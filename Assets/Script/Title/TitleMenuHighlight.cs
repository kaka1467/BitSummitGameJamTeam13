using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// タイトル画面のメニュー（Connect / Start Button など）で、
///   1) 今選ばれている項目に矢印アイコンを追従させる
///   2) 選択中の項目の「後ろ」に置いた光る画像(selectionGlow)も同じ位置に追従させる
///   3) ボタンを「決定（クリック／Submit）」した瞬間、その selectionGlow を一瞬光らせてから
///      フェードアウトし、最後にそのボタンを非表示にする
///
/// 矢印やテキスト自体の色は一切変更しない。光るのはあくまで selectionGlow という
/// 別の画像（矢印・文字の背後に置く）。Unity 標準の円スプライト（Knob等）で代用可能なので、
/// 専用のグロー素材が無くても使える。
///
/// ナビゲーション（↑↓←→やゲームパッドでの移動）自体は Unity 標準の EventSystem / Selectable に任せる。
///
/// 使い方（決定フラッシュ）:
///   ボタンの OnClick に、既存の処理（ParentUdpSender.OnXxxClicked など）より先に
///   このコンポーネントの FlashAndDeactivate(Selectable) を登録し、引数にそのボタン自身を渡す。
///   このメソッドがフェード完了後にボタンを非表示にするので、
///   既存側で同じボタンを即座に SetActive(false) する処理は外しておくこと
///   （先に消してしまうと、光る演出が1フレームも表示されない）。
///
/// 前提:
///   - arrowIndicator / selectionGlow は、メニュー項目（Button等）と同じ親（同じ Anchor/Pivot）の
///     RectTransform にすること。anchoredPosition をそのままコピーして追従させるため。
///   - selectionGlow は矢印・文字より「後ろ」（Hierarchy で上）に置くこと。
///   - 各メニュー項目の Button の Navigation は Automatic のままでよい（Unity が自動で隣接判定する）。
/// </summary>
public class TitleMenuHighlight : MonoBehaviour
{
    [Header("追従させる矢印")]
    [Tooltip("選択中の項目の横に出す矢印アイコン。")]
    [SerializeField] private RectTransform arrowIndicator;
    [Tooltip("矢印の、選択項目に対する位置オフセット。")]
    [SerializeField] private Vector2 arrowOffset = new Vector2(-60f, 0f);

    [Header("選択中の背景グロー（矢印・文字の後ろに置く画像）")]
    [Tooltip("矢印・文字の後ろに置く、光らせる用の画像。専用素材が無ければ Unity 標準の円スプライト(Knob等)で代用可。")]
    [SerializeField] private RectTransform selectionGlow;
    [Tooltip("グローの、選択項目に対する位置オフセット。")]
    [SerializeField] private Vector2 glowOffset = Vector2.zero;
    [Tooltip("光っていない通常時のグローの透明度。0なら完全に消しておく。")]
    [SerializeField, Range(0f, 1f)] private float glowIdleAlpha = 0f;

    [Header("動き")]
    [Tooltip("矢印・グローの追従の速さ。大きいほど素早く追いつく。0なら瞬時にスナップする。")]
    [SerializeField, Min(0f)] private float followSpeed = 18f;

    [Header("決定フラッシュ（決定した瞬間、後ろが一瞬光って消える）")]
    [SerializeField] private Color flashColor = new Color(1f, 0.85f, 0.2f, 1f);
    [Tooltip("フラッシュで一番明るい状態を保つ秒数。")]
    [SerializeField, Min(0f)] private float flashHoldSeconds = 0.08f;
    [Tooltip("そこからフェードアウトするまでの秒数。")]
    [SerializeField, Min(0.01f)] private float flashFadeSeconds = 0.25f;

    [Header("形に沿って光らせる（Outline/Shadowコンポーネントを使用、任意）")]
    [Tooltip("矢印に付けた Outline（または Shadow）コンポーネント。矢印の形に沿って縁取りが光る。未設定ならこちらは使わない。")]
    [SerializeField] private Shadow arrowShapeGlow;
    [Tooltip("ボタン文字に付けた Outline（または Shadow）コンポーネントは、押されたボタンの中から自動で探すので個別設定は不要。")]
    [SerializeField] private bool useButtonTextShapeGlow = true;
    [Tooltip("TextMeshPro の Material 自体が持つ Glow（Material Inspector で有効化するもの）を光らせる。こちらも押されたボタンの中から自動で探す。")]
    [SerializeField] private bool useTmpMaterialGlow = true;

    [Header("初期選択")]
    [Tooltip("タイトル表示時に最初から選択状態にしておく項目。未設定ならUnity標準の挙動（最初は何も選ばれない）のまま。")]
    [SerializeField] private Selectable initialSelection;

    private Graphic glowGraphic;
    private Coroutine flashRoutine;

    private void Awake()
    {
        if (selectionGlow != null)
        {
            glowGraphic = selectionGlow.GetComponent<Graphic>();
            SetGlowAlpha(glowIdleAlpha);
        }

        // 形に沿って光らせる矢印側は、最初はアルファ0（光っていない状態）にしておく
        SetShapeGlowAlpha(arrowShapeGlow, 0f);
    }

    private void Start()
    {
        if (initialSelection != null
            && initialSelection.gameObject.activeInHierarchy
            && EventSystem.current != null)
        {
            EventSystem.current.SetSelectedGameObject(initialSelection.gameObject);
        }

        UpdateFollowPosition(useSnap: true);
    }

    private void Update()
    {
        EnsureValidSelection();
        UpdateFollowPosition(useSnap: false);
    }

    // 選択中のオブジェクトが非表示・破棄などで無効になっていたら、初期選択へ選び直す。
    // （例：ボタンを押すと自身を非表示にするため、選択先が宙に浮いて
    //   矢印キーでの移動が止まってしまう問題への対策）
    private void EnsureValidSelection()
    {
        if (EventSystem.current == null) return;

        GameObject selected = EventSystem.current.currentSelectedGameObject;
        bool selectionIsValid = selected != null && selected.activeInHierarchy;

        if (!selectionIsValid
            && initialSelection != null
            && initialSelection.gameObject.activeInHierarchy)
        {
            EventSystem.current.SetSelectedGameObject(initialSelection.gameObject);
        }
    }

    private void UpdateFollowPosition(bool useSnap)
    {
        GameObject selected = EventSystem.current != null
            ? EventSystem.current.currentSelectedGameObject
            : null;

        if (selected == null) return;

        RectTransform target = selected.transform as RectTransform;
        if (target == null) return;

        Vector2 basePos = target.anchoredPosition;

        if (arrowIndicator != null)
        {
            if (!arrowIndicator.gameObject.activeSelf) arrowIndicator.gameObject.SetActive(true);
            MoveTowards(arrowIndicator, basePos + arrowOffset, useSnap);
        }

        if (selectionGlow != null)
        {
            if (!selectionGlow.gameObject.activeSelf) selectionGlow.gameObject.SetActive(true);
            MoveTowards(selectionGlow, basePos + glowOffset, useSnap);
        }
    }

    private void MoveTowards(RectTransform rt, Vector2 targetPos, bool snap)
    {
        if (snap || followSpeed <= 0f)
        {
            rt.anchoredPosition = targetPos;
        }
        else
        {
            rt.anchoredPosition = Vector2.Lerp(rt.anchoredPosition, targetPos, Time.unscaledDeltaTime * followSpeed);
        }
    }

    /// <summary>
    /// 決定された瞬間に呼ぶ。矢印・文字の色には触れず、その後ろの selectionGlow だけを
    /// 一瞬光らせてからフェードアウトし、最後にボタンを非表示にする。
    /// ボタンの OnClick にこのメソッドを、他の処理より先に登録して使う。
    /// </summary>
    public void FlashAndDeactivate(Selectable pressed)
    {
        if (pressed == null) return;

        if (flashRoutine != null)
        {
            StopCoroutine(flashRoutine);
        }
        flashRoutine = StartCoroutine(FlashAndDeactivateRoutine(pressed));
    }

    private IEnumerator FlashAndDeactivateRoutine(Selectable pressed)
    {
        // フラッシュ開始時点で、グローを確実に押したボタンの位置へスナップしておく
        RectTransform pressedRect = pressed.transform as RectTransform;
        if (selectionGlow != null && pressedRect != null)
        {
            selectionGlow.anchoredPosition = pressedRect.anchoredPosition + glowOffset;
        }

        // ボタン文字側の Outline/Shadow コンポーネントは、押されたボタンの中から毎回自動で探す
        Shadow buttonShapeGlow = useButtonTextShapeGlow ? pressed.GetComponentInChildren<Shadow>() : null;
        // TMPのMaterial自体のGlowを使う場合も、同様に押されたボタンの中から探す
        TMP_Text buttonTmpText = useTmpMaterialGlow ? pressed.GetComponentInChildren<TMP_Text>() : null;

        SetGlowColor(flashColor);
        SetShapeGlowColor(arrowShapeGlow, flashColor);
        SetShapeGlowColor(buttonShapeGlow, flashColor);
        SetTmpGlowColor(buttonTmpText, flashColor);

        if (flashHoldSeconds > 0f)
        {
            yield return new WaitForSecondsRealtime(flashHoldSeconds);
        }

        float elapsed = 0f;
        while (elapsed < flashFadeSeconds)
        {
            elapsed += Time.unscaledDeltaTime;
            float alpha = Mathf.Lerp(1f, glowIdleAlpha, Mathf.Clamp01(elapsed / flashFadeSeconds));
            SetGlowAlpha(alpha);
            SetShapeGlowAlpha(arrowShapeGlow, alpha);
            SetShapeGlowAlpha(buttonShapeGlow, alpha);
            SetTmpGlowAlpha(buttonTmpText, alpha);
            yield return null;
        }

        SetGlowAlpha(glowIdleAlpha);
        SetShapeGlowAlpha(arrowShapeGlow, 0f);
        SetShapeGlowAlpha(buttonShapeGlow, 0f);
        SetTmpGlowAlpha(buttonTmpText, 0f);
        flashRoutine = null;

        if (pressed != null && pressed.gameObject != null)
        {
            pressed.gameObject.SetActive(false);
        }
    }

    private void SetGlowColor(Color color)
    {
        if (glowGraphic == null) return;
        glowGraphic.color = color;
    }

    private void SetGlowAlpha(float alpha)
    {
        if (glowGraphic == null) return;
        Color c = glowGraphic.color;
        c.a = alpha;
        glowGraphic.color = c;
    }

    private void SetShapeGlowColor(Shadow shadow, Color color)
    {
        if (shadow == null) return;
        shadow.effectColor = color;
    }

    private void SetShapeGlowAlpha(Shadow shadow, float alpha)
    {
        if (shadow == null) return;
        Color c = shadow.effectColor;
        c.a = alpha;
        shadow.effectColor = c;
    }

    // TMP の Material 自体が持つ Glow（Material Inspector の Glow セクション）を操作する。
    // fontMaterial を使うことで、このテキスト専用のMaterialインスタンスを自動生成し、
    // 他で同じフォントを使っているテキストに影響しないようにしている。
    private void SetTmpGlowColor(TMP_Text text, Color color)
    {
        if (text == null) return;

        Material material = text.fontMaterial;
        if (!material.IsKeywordEnabled(ShaderUtilities.Keyword_Glow))
        {
            material.EnableKeyword(ShaderUtilities.Keyword_Glow);
        }
        material.SetColor(ShaderUtilities.ID_GlowColor, color);
    }

    private void SetTmpGlowAlpha(TMP_Text text, float alpha)
    {
        if (text == null) return;

        Material material = text.fontMaterial;
        Color c = material.GetColor(ShaderUtilities.ID_GlowColor);
        c.a = alpha;
        material.SetColor(ShaderUtilities.ID_GlowColor, c);
    }
}
