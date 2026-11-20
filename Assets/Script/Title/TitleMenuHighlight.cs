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
    [Tooltip("ONなら、ボタン固定幅ではなくボタン内 TextMeshProUGUI の実際の文字描画左端から textLeftGap だけ左に矢印を揃える。")]
    [SerializeField] private bool alignArrowToTextLeftEdge = true;
    [SerializeField] private bool placeArrowOnRightEdge = false;
    [Tooltip("alignArrowToTextLeftEdge が ON のとき、文字の左端から矢印中心までの余白（px）。")]
    [SerializeField] private float textLeftGap = 36f;

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
    private bool _indicatorSuppressed;   // 設定パネル表示中など、矢印・グローを隠す

    /// <summary>
    /// 矢印・選択グローを一時的に隠す／戻す（設定パネル表示中など）。
    ///  ・隠している間は追従更新も止める（勝手に再表示しない）。
    ///  ・ナビゲーション自体（EventSystem）には触れない。
    /// </summary>
    /// <summary>追従している矢印（設定パネル用の矢印を同じ見た目で作るために参照する。読み取り専用）。</summary>
    public RectTransform ArrowIndicator => arrowIndicator;

    public void SetArrowOnRightEdge(bool rightSide)
    {
        placeArrowOnRightEdge = rightSide;
    }

    public void SetIndicatorVisible(bool visible)
    {
        _indicatorSuppressed = !visible;

        if (!visible)
        {
            if (arrowIndicator != null) arrowIndicator.gameObject.SetActive(false);
            if (selectionGlow != null) selectionGlow.gameObject.SetActive(false);
        }
    }

    /// <summary>
    /// 外部から選択対象の Selectable を明示的に切り替え、矢印位置を更新する。
    /// </summary>
    public void SetSelectedSelectable(Selectable target, bool snap = true)
    {
        if (target == null) return;

        _lastValidSelection = target;
        if (EventSystem.current != null)
        {
            EventSystem.current.SetSelectedGameObject(target.gameObject);
        }

        UpdateFollowPosition(useSnap: snap);
    }

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

    private Selectable _lastValidSelection;

    private void Start()
    {
        Selectable targetSelection = ResolveValidInitialSelection();
        if (targetSelection != null && EventSystem.current != null)
        {
            _lastValidSelection = targetSelection;
            EventSystem.current.SetSelectedGameObject(targetSelection.gameObject);
        }

        UpdateFollowPosition(useSnap: true);
    }

    private void Update()
    {
        EnsureValidSelection();
        UpdateFollowPosition(useSnap: false);
    }

    // 選択中のオブジェクトが非表示・破棄・操作不能（interactable=false）になっていたら、直前の有効選択または初期選択へ選び直す。
    private void EnsureValidSelection()
    {
        if (EventSystem.current == null) return;

        GameObject selected = EventSystem.current.currentSelectedGameObject;
        Selectable selectedSelectable = selected != null ? selected.GetComponent<Selectable>() : null;
        bool selectionIsValid = selected != null
            && selected.activeInHierarchy
            && selectedSelectable != null
            && selectedSelectable.IsInteractable();

        if (selectionIsValid)
        {
            _lastValidSelection = selectedSelectable;
            return;
        }

        if (_lastValidSelection != null
            && _lastValidSelection.gameObject.activeInHierarchy
            && _lastValidSelection.IsInteractable())
        {
            EventSystem.current.SetSelectedGameObject(_lastValidSelection.gameObject);
            return;
        }

        Selectable fallback = ResolveValidInitialSelection();
        if (fallback != null)
        {
            _lastValidSelection = fallback;
            EventSystem.current.SetSelectedGameObject(fallback.gameObject);
        }
    }

    private Selectable ResolveValidInitialSelection()
    {
        if (initialSelection != null
            && initialSelection.gameObject.activeInHierarchy
            && initialSelection.IsInteractable())
        {
            return initialSelection;
        }

        GameObject soloObj = GameObject.Find("SoloStartButton");
        if (soloObj != null && soloObj.activeInHierarchy)
        {
            Selectable soloSel = soloObj.GetComponent<Selectable>();
            if (soloSel != null && soloSel.IsInteractable())
            {
                return soloSel;
            }
        }

        Selectable[] allSelectables = Selectable.allSelectablesArray;
        for (int i = 0; i < allSelectables.Length; i++)
        {
            Selectable s = allSelectables[i];
            if (s != null && s.gameObject.activeInHierarchy && s.IsInteractable() && s.navigation.mode != Navigation.Mode.None)
            {
                return s;
            }
        }

        return null;
    }

    private void UpdateFollowPosition(bool useSnap)
    {
        if (_indicatorSuppressed) return;   // 設定パネル表示中は追従・再表示しない

        GameObject selected = EventSystem.current != null
            ? EventSystem.current.currentSelectedGameObject
            : null;

        if (selected == null) return;

        RectTransform target = selected.transform as RectTransform;
        if (target == null) return;

        // 選択項目と矢印の親が違う場合（実行時生成の設定ボタンなど）は、
        // 生の anchoredPosition をそのまま使うと矢印が中央へ飛ぶ。ワールド位置を矢印の親のローカルへ変換する。
        Vector2 basePos = GetAnchoredInArrowParent(target);

        if (arrowIndicator != null)
        {
            if (!arrowIndicator.gameObject.activeSelf) arrowIndicator.gameObject.SetActive(true);
            Vector2 arrowTargetPos = CalculateArrowTargetPosition(target, basePos);
            MoveTowards(arrowIndicator, arrowTargetPos, useSnap);
        }

        if (selectionGlow != null)
        {
            if (!selectionGlow.gameObject.activeSelf) selectionGlow.gameObject.SetActive(true);
            MoveTowards(selectionGlow, basePos + glowOffset, useSnap);
        }
    }

    /// <summary>
    /// 対象ボタン内の TextMeshProUGUI の実際の文字描画左端（textBounds / preferredValues）を基準に、
    /// 文字に被らないよう一定間隔（textLeftGap および矢印幅）だけ左の位置へ矢印の座標を算出する。
    /// </summary>
    private Vector2 CalculateArrowTargetPosition(RectTransform target, Vector2 basePos)
    {
        if (!alignArrowToTextLeftEdge || arrowIndicator == null)
            return basePos + arrowOffset;

        RectTransform arrowParent = arrowIndicator.parent as RectTransform;
        if (arrowParent == null)
            return basePos + arrowOffset;

        return placeArrowOnRightEdge
            ? ComputeTextAlignedRightArrowPosition(target, arrowIndicator, arrowParent, basePos, arrowOffset, textLeftGap)
            : ComputeTextAlignedArrowPosition(target, arrowIndicator, arrowParent, basePos, arrowOffset, textLeftGap);
    }

    /// <summary>
    /// 親機・子機共通：対象ボタン内の TMP_Text の描画左端（またはボタン左端）を arrowParent ローカル座標へ変換し、
    /// 矢印が文字に被らず綺麗に左側に揃う座標を算出する。
    /// </summary>
    public static Vector2 ComputeTextAlignedArrowPosition(
        RectTransform target,
        RectTransform arrowRect,
        RectTransform arrowParent,
        Vector2 fallbackBasePos,
        Vector2 offset,
        float gapFromTextLeft)
    {
        if (target == null || arrowRect == null || arrowParent == null)
            return fallbackBasePos + offset;

        TMP_Text tmp = target.GetComponentInChildren<TMP_Text>();
        float arrowHalfWidth = Mathf.Abs(arrowRect.rect.width * arrowRect.localScale.x) * 0.5f;
        // 矢印スプライトの見た目幅を考慮し、文字左端から十分な距離を確保して文字被りを防ぐ
        float effectiveGap = Mathf.Max(gapFromTextLeft, arrowHalfWidth * 0.55f + 16f);

        if (tmp != null)
        {
            RectTransform textRt = tmp.rectTransform;
            tmp.ForceMeshUpdate();

            float localLeftX;
            float localCenterY;

            if (TryGetVisibleTextLocalBounds(tmp, out float minX, out float centerY))
            {
                localLeftX = minX;
                localCenterY = centerY;
            }
            else
            {
                string rawText = tmp.text != null ? tmp.text.Trim() : string.Empty;
                Vector2 pref = tmp.GetPreferredValues(rawText);
                float width = Mathf.Max(0f, pref.x);
                localLeftX = textRt.rect.center.x - width * 0.5f;
                localCenterY = textRt.rect.center.y;
            }

            Vector3 worldLeft = textRt.TransformPoint(new Vector3(localLeftX, localCenterY, 0f));
            Vector3 parentLocalLeft = arrowParent.InverseTransformPoint(worldLeft);

            return new Vector2(parentLocalLeft.x - effectiveGap, parentLocalLeft.y + offset.y);
        }

        // TMP_Text が無いボタンの場合はボタン矩形の左端を基準にする
        Vector3 buttonLeftWorld = target.TransformPoint(new Vector3(target.rect.xMin, target.rect.center.y, 0f));
        Vector3 buttonLeftLocal = arrowParent.InverseTransformPoint(buttonLeftWorld);
        return new Vector2(buttonLeftLocal.x - effectiveGap, buttonLeftLocal.y + offset.y);
    }

    /// <summary>
    /// 右側矢印用：TMP の実際の文字描画右端、またはボタン矩形右端を基準に、
    /// 矢印幅と余白を加えて右側へ配置する。
    /// </summary>
    public static Vector2 ComputeTextAlignedRightArrowPosition(
        RectTransform target,
        RectTransform arrowRect,
        RectTransform arrowParent,
        Vector2 fallbackBasePos,
        Vector2 offset,
        float gapFromTextRight)
    {
        if (target == null || arrowRect == null || arrowParent == null)
            return fallbackBasePos + offset;

        float arrowHalfWidth = Mathf.Abs(arrowRect.rect.width * arrowRect.localScale.x) * 0.5f;
        float effectiveGap = Mathf.Max(gapFromTextRight + 28f, 28f);

        Vector3 buttonRightWorld = target.TransformPoint(new Vector3(target.rect.xMax, target.rect.center.y, 0f));
        Vector3 buttonRightLocal = arrowParent.InverseTransformPoint(buttonRightWorld);
        return new Vector2(buttonRightLocal.x + arrowHalfWidth + effectiveGap + offset.x, buttonRightLocal.y + offset.y);
    }

    private static bool TryGetVisibleTextRightBounds(TMP_Text tmp, out float maxX, out float centerY)
    {
        maxX = 0f;
        centerY = 0f;
        if (tmp == null) return false;

        TMP_TextInfo textInfo = tmp.textInfo;
        if (textInfo != null && textInfo.characterCount > 0)
        {
            float foundMaxX = float.NegativeInfinity;
            float foundMinY = float.PositiveInfinity;
            float foundMaxY = float.NegativeInfinity;
            bool hasVisible = false;

            int count = Mathf.Min(textInfo.characterCount, textInfo.characterInfo != null ? textInfo.characterInfo.Length : 0);
            for (int i = 0; i < count; i++)
            {
                TMP_CharacterInfo charInfo = textInfo.characterInfo[i];
                if (!charInfo.isVisible) continue;
                hasVisible = true;
                if (charInfo.topRight.x > foundMaxX) foundMaxX = charInfo.topRight.x;
                if (charInfo.bottomLeft.y < foundMinY) foundMinY = charInfo.bottomLeft.y;
                if (charInfo.topRight.y > foundMaxY) foundMaxY = charInfo.topRight.y;
            }

            if (hasVisible)
            {
                maxX = foundMaxX;
                centerY = (foundMinY + foundMaxY) * 0.5f;
                return true;
            }
        }

        Bounds textBounds = tmp.textBounds;
        if (textBounds.size.x > 0.01f)
        {
            maxX = textBounds.max.x;
            centerY = textBounds.center.y;
            return true;
        }

        return false;
    }

    private static bool TryGetVisibleTextLocalBounds(TMP_Text tmp, out float minX, out float centerY)
    {
        minX = 0f;
        centerY = 0f;

        if (tmp == null) return false;

        TMP_TextInfo textInfo = tmp.textInfo;
        if (textInfo != null && textInfo.characterCount > 0)
        {
            float foundMinX = float.PositiveInfinity;
            float foundMinY = float.PositiveInfinity;
            float foundMaxY = float.NegativeInfinity;
            bool hasVisible = false;

            int count = Mathf.Min(textInfo.characterCount, textInfo.characterInfo != null ? textInfo.characterInfo.Length : 0);
            for (int i = 0; i < count; i++)
            {
                TMP_CharacterInfo charInfo = textInfo.characterInfo[i];
                if (!charInfo.isVisible) continue;

                hasVisible = true;
                if (charInfo.bottomLeft.x < foundMinX) foundMinX = charInfo.bottomLeft.x;
                if (charInfo.bottomLeft.y < foundMinY) foundMinY = charInfo.bottomLeft.y;
                if (charInfo.topRight.y > foundMaxY) foundMaxY = charInfo.topRight.y;
            }

            if (hasVisible)
            {
                minX = foundMinX;
                centerY = (foundMinY + foundMaxY) * 0.5f;
                return true;
            }
        }

        Bounds textBounds = tmp.textBounds;
        if (textBounds.size.x > 0.01f)
        {
            minX = textBounds.min.x;
            centerY = textBounds.center.y;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 選択項目の位置を、矢印（arrowIndicator）の親のローカル座標へ変換して返す。
    ///  ・同じ親なら従来どおり anchoredPosition をそのまま使う（既存挙動を変えない）。
    ///  ・別の親なら、ワールド位置を変換して使う（中央へ飛ぶのを防ぐ）。
    /// </summary>
    private Vector2 GetAnchoredInArrowParent(RectTransform target)
    {
        RectTransform arrowParent = arrowIndicator != null ? arrowIndicator.parent as RectTransform : null;
        if (arrowParent == null || target.parent == arrowParent)
            return target.anchoredPosition;

        Vector3 world = target.TransformPoint(target.rect.center);
        Vector3 local = arrowParent.InverseTransformPoint(world);
        return new Vector2(local.x, local.y);
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
