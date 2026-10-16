using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 親機シーンの「窓の外」に置いた板（Quad）のテクスチャUVを下へ流して、軽量に雨の筋を見せる表示専用コンポーネント。
///
///  ・板（Transform）は動かさず、マテリアルのテクスチャUVオフセットだけを動かす。
///  ・Unlit の透明マテリアル（アセット参照）を実行時に1回だけ複製して使う。元アセットや他オブジェクトの
///    マテリアルは変更しない（破棄時に元へ戻し、複製したマテリアルも片付ける）。
///  ・毎フレームのMaterial／GameObject生成はしない。
///  ・深度テストは無効にしない（壁越しに表示する方法は使わない）。影・衝突・反射・追加Lightは作らない。
///  ・寝たふり中でも雨は止めない（SleepingController を参照しない）。音は鳴らさない（既存の雨音に触れない）。
/// </summary>
[DisallowMultipleComponent]
public class ParentWindowRainEffect : MonoBehaviour
{
    private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int LegacyColorId = Shader.PropertyToID("_Color");

    [Header("雨を表示する板（窓の外に置いたQuad）")]
    [Tooltip("雨を表示するRenderer。複数指定しても同じUVで流れます（最初は1枚）。\n" +
             "空のときは「このGameObjectのRendererを使う」がONなら自身のRendererを使います。")]
    [SerializeField]
    private Renderer[] rainRenderers;

    [Tooltip("rainRenderers が空のとき、このGameObject自身のRendererを使います。")]
    [SerializeField]
    private bool autoUseOwnRenderer = true;

    [Header("マテリアル（アセット参照。Shader.Findには依存しない）")]
    [Tooltip("雨用の透明Unlitマテリアル（URP/Unlit）。実行時に複製して使い、元アセットは変更しません。\n" +
             "未設定なら何もしません（警告を1回だけ出します）。")]
    [SerializeField]
    private Material rainMaterialTemplate;

    [Header("雨の動き")]
    [Tooltip("雨が流れる速さ（UV/秒）。大きいほど速く降ります。")]
    [SerializeField]
    private float fallSpeed = 0.55f;

    [Tooltip("横に流れる量（UV/秒）。0で真下に落ちます。")]
    [SerializeField]
    private float horizontalDrift = 0.04f;

    [Tooltip("雨筋の細かさ（Tiling）。大きいほど細かく密になります。")]
    [SerializeField]
    private Vector2 tiling = new Vector2(3f, 4f);

    [Header("見た目")]
    [Tooltip("雨の色（テクスチャに掛かる色）。")]
    [SerializeField]
    private Color rainColor = new Color(0.72f, 0.8f, 0.9f, 1f);

    [Tooltip("雨の濃さ（0で見えなくなります）。")]
    [SerializeField, Range(0f, 1f)]
    private float opacity = 0.55f;

    [Header("有効／無効")]
    [Tooltip("OFFにすると雨を消します（元のマテリアルへ戻すので描画も止まります）。")]
    [SerializeField]
    private bool rainEnabled = true;

    [Header("デバッグ")]
    [Tooltip("有効化・無効化の切り替え時にログを出します。")]
    [SerializeField]
    private bool showDebugLog = false;

    // ── 実行時状態 ─────────────────────────────────────────────────────────────
    private Material rainMaterial;              // 実行時複製（1個だけ）
    private readonly List<Renderer> targetRenderers = new List<Renderer>();
    private readonly List<Renderer> assignedRenderers = new List<Renderer>();
    private readonly List<Material[]> originalSharedMaterials = new List<Material[]>();   // 元のマテリアル（戻す用）
    private readonly List<bool> originalEnabled = new List<bool>();                       // 元の有効状態（戻す用）

    private Vector2 currentOffset;
    private bool applied;
    private bool warnedMissingTemplate;
    private bool warnedMissingRenderer;

    // 直前に適用した値（Inspectorの変更を検知して反映するため）
    private Vector2 lastTiling;
    private Color lastColor;
    private bool hasLastVisual;

    private void OnEnable()
    {
        // シーン開始時から現在の状態を反映する。
        applied = false;
        hasLastVisual = false;
        ApplyEnabledState(force: true);
    }

    private void Update()
    {
        if (!rainEnabled)
        {
            if (applied) ApplyEnabledState(force: true);
            return;
        }

        if (!EnsureMaterial())
        {
            // 参照が揃うまでは何もしない（警告は1回だけ・例外は出さない）。
            return;
        }

        if (!applied) ApplyEnabledState(force: true);

        ApplyVisualIfChanged();

        // 板は動かさず、UVオフセットだけを流す（時間停止中でも雨は降らせ続ける）。
        float dt = Time.unscaledDeltaTime;
        currentOffset.y -= fallSpeed * dt;
        currentOffset.x += horizontalDrift * dt;

        // 値が大きくなりすぎないように1周期分だけ丸める（見た目は変わらない）。
        currentOffset.x -= Mathf.Floor(currentOffset.x);
        currentOffset.y -= Mathf.Floor(currentOffset.y);

        rainMaterial.SetTextureOffset(BaseMapId, currentOffset);
    }

    private void OnDisable()
    {
        RestoreAndCleanup();
    }

    private void OnDestroy()
    {
        RestoreAndCleanup();
    }

    // ── マテリアルの準備（実行時複製は1回だけ）─────────────────────────────────

    private bool EnsureMaterial()
    {
        if (rainMaterial != null) return true;

        if (rainMaterialTemplate == null)
        {
            if (!warnedMissingTemplate)
            {
                warnedMissingTemplate = true;
                Debug.LogWarning(
                    "[ParentWindowRainEffect] Rain Material Template が未設定です。" +
                    "Inspectorに WindowRain.mat（URP/Unlitの透明マテリアル）を指定してください（雨は表示しません）。", this);
            }
            return false;
        }

        CollectRenderers();
        if (targetRenderers.Count == 0)
        {
            if (!warnedMissingRenderer)
            {
                warnedMissingRenderer = true;
                Debug.LogWarning(
                    "[ParentWindowRainEffect] 雨を表示するRendererがありません。" +
                    "Inspectorの Rain Renderers に窓の外の板（Quad）のRendererを指定してください（雨は表示しません）。", this);
            }
            return false;
        }

        // 実行時の複製は1回だけ。元アセットは変更しない。
        rainMaterial = new Material(rainMaterialTemplate);
        rainMaterial.name = "WindowRain (Runtime)";

        // テクスチャはRepeatで使う（UVスクロールで継ぎ目が繰り返しになるようにする）。
        if (rainMaterial.HasProperty(BaseMapId))
        {
            Texture baseMap = rainMaterial.GetTexture(BaseMapId);
            if (baseMap != null) baseMap.wrapMode = TextureWrapMode.Repeat;
        }

        return true;
    }

    private void CollectRenderers()
    {
        targetRenderers.Clear();

        if (rainRenderers != null)
        {
            for (int i = 0; i < rainRenderers.Length; i++)
            {
                Renderer r = rainRenderers[i];
                if (r != null && !targetRenderers.Contains(r)) targetRenderers.Add(r);
            }
        }

        if (targetRenderers.Count == 0 && autoUseOwnRenderer)
        {
            Renderer own = GetComponent<Renderer>();
            if (own != null) targetRenderers.Add(own);
        }
    }

    // ── 有効／無効の反映 ───────────────────────────────────────────────────────

    private void ApplyEnabledState(bool force)
    {
        if (rainEnabled)
        {
            if (!EnsureMaterial())
            {
                applied = false;
                return;
            }

            if (assignedRenderers.Count == 0) AssignMaterialToRenderers();

            ApplyVisualIfChanged(force: true);
            applied = true;

            if (showDebugLog)
                Debug.Log($"[ParentWindowRainEffect] 雨を表示します（板 {assignedRenderers.Count} 枚）。", this);
        }
        else
        {
            // OFFのときは元のマテリアルへ戻し、Renderer自体も止める（描画負荷を0にする）。
            RestoreRenderers();
            applied = false;

            if (showDebugLog)
                Debug.Log("[ParentWindowRainEffect] 雨を非表示にしました（Rendererを止めています）。", this);
        }
    }

    /// <summary>雨の板へ実行時マテリアル（スロット0）を割り当てる。元のマテリアルと有効状態を覚えておく。</summary>
    private void AssignMaterialToRenderers()
    {
        assignedRenderers.Clear();
        originalSharedMaterials.Clear();
        originalEnabled.Clear();

        for (int i = 0; i < targetRenderers.Count; i++)
        {
            Renderer r = targetRenderers[i];
            if (r == null) continue;

            Material[] original = r.sharedMaterials;
            if (original == null || original.Length == 0) continue;

            Material[] copy = new Material[original.Length];
            for (int j = 0; j < original.Length; j++) copy[j] = original[j];
            copy[0] = rainMaterial;

            assignedRenderers.Add(r);
            originalSharedMaterials.Add(original);
            originalEnabled.Add(r.enabled);

            r.sharedMaterials = copy;
            r.enabled = true;
        }
    }

    /// <summary>Inspectorの見た目（Tiling／色／濃さ）が変わったときだけ反映する。</summary>
    private void ApplyVisualIfChanged(bool force = false)
    {
        if (rainMaterial == null) return;

        Vector2 wantTiling = new Vector2(Mathf.Max(0.01f, Mathf.Abs(tiling.x)), Mathf.Max(0.01f, Mathf.Abs(tiling.y)));
        Color wantColor = rainColor;
        wantColor.a = Mathf.Clamp01(rainColor.a * opacity);

        if (!force && hasLastVisual && wantTiling == lastTiling && wantColor == lastColor) return;

        if (rainMaterial.HasProperty(BaseMapId))
        {
            rainMaterial.SetTextureScale(BaseMapId, wantTiling);
            rainMaterial.SetTextureOffset(BaseMapId, currentOffset);
        }

        if (rainMaterial.HasProperty(BaseColorId)) rainMaterial.SetColor(BaseColorId, wantColor);
        if (rainMaterial.HasProperty(LegacyColorId)) rainMaterial.SetColor(LegacyColorId, wantColor);

        lastTiling = wantTiling;
        lastColor = wantColor;
        hasLastVisual = true;
    }

    // ── 後片付け ───────────────────────────────────────────────────────────────

    /// <summary>元のマテリアルと有効状態へ戻す（何度呼んでも安全）。</summary>
    private void RestoreRenderers()
    {
        for (int i = 0; i < assignedRenderers.Count; i++)
        {
            Renderer r = assignedRenderers[i];
            if (r == null) continue;

            if (i < originalSharedMaterials.Count && originalSharedMaterials[i] != null)
                r.sharedMaterials = originalSharedMaterials[i];

            if (i < originalEnabled.Count) r.enabled = originalEnabled[i];
        }

        assignedRenderers.Clear();
        originalSharedMaterials.Clear();
        originalEnabled.Clear();
    }

    /// <summary>複製したマテリアルを破棄し、参照を片付ける。</summary>
    private void RestoreAndCleanup()
    {
        RestoreRenderers();

        if (rainMaterial != null)
        {
            if (Application.isPlaying) Destroy(rainMaterial);
            else DestroyImmediate(rainMaterial);

            rainMaterial = null;
        }

        applied = false;
        hasLastVisual = false;
    }
}
