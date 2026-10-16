using UnityEngine;

/// <summary>
/// 親機のゲーム機モデルの「画面」点灯／消灯（表示専用コンポーネント）。
///
///  ・状態の基準は既存の <see cref="SleepingController.IsSleeping"/> だけ。寝たふりの判定や通信は複製しない。
///  ・false（起きている）＝ピンクに点灯（画面自体が光る＝暗い場所でも明るく見える）
///  ・true（寝たふり中）＝即座に消灯し、画面表面そのものを黒くする（Emissionだけを0にしない）
///  ・画面は Unlit 系の専用マテリアルにする。ライト・環境光・反射の影響を受けないため、
///    消灯時に周囲の光や反射でピンクや明るい灰色に見えない。
///  ・Planeを非表示にして下のモデルを見せる方法は使わない（Rendererは有効のまま色だけ変える）。
///  ・共有マテリアルは変更しない（画面のスロットだけ専用インスタンスに差し替え、破棄時に元へ戻す）。
///  ・参照が未設定でも例外にしない（警告を1回だけ出して何もしない）。
/// </summary>
[DisallowMultipleComponent]
public class ParentConsoleScreenDisplay : MonoBehaviour
{
    private const string BaseColorProperty = "_BaseColor";     // URP系
    private const string ColorProperty = "_Color";             // Legacy/Unlit系
    private const string EmissionColorProperty = "_EmissionColor";
    private const string EmissionKeyword = "_EMISSION";
    private const string BaseMapProperty = "_BaseMap";
    private const string MainTexProperty = "_MainTex";

    [Header("状態の基準（親機の寝たふり）")]
    [Tooltip("画面の点灯／消灯はこの SleepingController.IsSleeping だけで判定します（UDP接続やBGMの有無は見ません）。\n" +
             "未設定なら起動時に1回だけ自動で探します（毎フレームは探しません）。")]
    [SerializeField]
    private SleepingController sleepingController;

    [Header("画面の参照")]
    [Tooltip("ゲーム機の画面Planeの Renderer（MeshRenderer）を指定します。\n" +
             "未設定なら、このGameObject自身のRendererを使います。")]
    [SerializeField]
    private Renderer screenRenderer;

    [Tooltip("対象マテリアルのスロット番号（通常は0）。")]
    [SerializeField, Min(0)]
    private int materialSlot;

    [Header("点灯（起きているとき）")]
    [Tooltip("点灯色。既定はピンク。")]
    [SerializeField]
    private Color litColor = new Color(1f, 0.35f, 0.65f, 1f);

    [Tooltip("発光の強さ。1より大きいとHDRの明るさになり、カメラにBloomがあれば光がにじみます。\n" +
             "（BloomはカメラのPost ProcessingとVolumeで別途必要な設定です。このコンポーネントでは変更しません）\n" +
             "Post ProcessingがOFFのときは1.5〜1.8くらいがピンクのまま明るく見えます（大きすぎると白っぽくなります）。")]
    [SerializeField, Min(0f)]
    private float litIntensity = 1.8f;

    [Tooltip("画面に貼る画像（任意）。未設定なら点灯色のベタ塗りになります。")]
    [SerializeField]
    private Texture2D screenTexture;

    [Header("消灯（寝たふり中）")]
    [Tooltip("消灯時の画面の色。既定は真っ黒。")]
    [SerializeField]
    private Color offColor = Color.black;

    [Header("画面用の補助ライト（任意）")]
    [Tooltip("画面の点灯に合わせて光らせたい「ゲーム機画面専用」のLightを指定します。\n" +
             "未設定なら何もしません。部屋全体のLightは操作しません。寝たふり中は消灯します。")]
    [SerializeField]
    private Light screenLight;

    [Tooltip("補助Lightの点灯時の強さ。0ならLightに設定されている元の強さをそのまま使います。")]
    [SerializeField, Min(0f)]
    private float screenLightIntensity;

    [Header("画面のマテリアル元（任意）")]
    [Tooltip("実行時に画面専用マテリアルを作るときの元。指定するとビルドでも確実に使えます。\n" +
             "未設定なら下の Shader を使います。")]
    [SerializeField]
    private Material screenMaterialTemplate;

    [Tooltip("上のテンプレートが未設定のときに使うShader。\n" +
             "未設定なら \"Universal Render Pipeline/Unlit\" → \"Unlit/Color\" → \"Sprites/Default\" の順で探します。\n" +
             "（ビルドでShaderが見つからない場合はここに Universal Render Pipeline/Unlit を指定してください）")]
    [SerializeField]
    private Shader screenShader;

    [Header("デバッグ")]
    [Tooltip("点灯／消灯が切り替わったときにログを出します。")]
    [SerializeField]
    private bool showDebugLog = false;

    // ── 実行時状態 ─────────────────────────────────────────────────────────────
    private Material screenMaterial;      // このコンポーネント専用（実行時インスタンス）
    private Material originalMaterial;    // 元の共有マテリアル（破棄時に戻す）
    private bool hasOriginalMaterial;

    private bool applied;
    private bool lastSleeping;
    private bool warnedMissingRenderer;
    private bool warnedMissingShader;
    private bool sleepingSearched;

    private Light cachedLight;
    private float lightOriginalIntensity = 1f;
    private bool lightOriginalEnabled = true;
    private bool lightCaptured;

    private void OnEnable()
    {
        // シーン開始時から現在状態を反映する。
        applied = false;
        ApplyState(force: true);
    }

    private void Update()
    {
        // 変化したときだけマテリアルに触る（毎フレームの生成・更新をしない）。
        bool sleeping = IsSleepingNow();
        if (applied && sleeping == lastSleeping) return;

        ApplyState(force: false);
    }

    private void OnDisable()
    {
        RestoreAndCleanup();
    }

    private void OnDestroy()
    {
        RestoreAndCleanup();
    }

    // ── 状態の反映 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 寝たふり中か（既存 SleepingController の値をそのまま参照する。判定ロジックは持たない）。
    /// </summary>
    private bool IsSleepingNow()
    {
        if (sleepingController == null)
        {
            if (sleepingSearched) return false;

            // 毎フレームは探さない（起動時の1回だけ）。
            sleepingSearched = true;
            sleepingController = FindFirstObjectByType<SleepingController>();
            if (sleepingController == null && showDebugLog)
                Debug.LogWarning("[ParentConsoleScreenDisplay] SleepingController が見つかりません。Inspectorで参照を指定してください。", this);
        }

        return sleepingController != null && sleepingController.IsSleeping;
    }

    private void ApplyState(bool force)
    {
        bool sleeping = IsSleepingNow();

        if (!force && applied && sleeping == lastSleeping) return;

        if (!EnsureMaterial())
        {
            // 参照が揃うまでは画面に触らない（補助Lightだけ状態に合わせ、例外は出さない）。
            ApplyLight(sleeping);
            applied = false;
            lastSleeping = sleeping;
            return;
        }

        // 点灯：ピンクをHDRの明るさで。消灯：真っ黒（Emissionだけを0にせず、表面自体を黒くする）。
        Color target = sleeping ? offColor : litColor * Mathf.Max(0f, litIntensity);

        // 画面は不透明のまま（Planeを透かして下のモデルを見せる方法は使わない）。
        target.a = 1f;

        SetScreenColor(screenMaterial, target);
        ApplyLight(sleeping);

        applied = true;
        lastSleeping = sleeping;

        if (showDebugLog)
            Debug.Log($"[ParentConsoleScreenDisplay] 画面を{(sleeping ? "消灯（黒）" : "点灯（ピンク）")}にしました。", this);
    }

    // ── 画面専用マテリアル ─────────────────────────────────────────────────────

    /// <summary>
    /// 画面のスロットだけを専用マテリアルに差し替える（共有マテリアルは変更しない）。
    /// 生成は1回だけ。Renderer等が未設定なら false を返す（例外は出さない）。
    /// </summary>
    private bool EnsureMaterial()
    {
        if (screenMaterial != null) return true;

        if (screenRenderer == null) screenRenderer = GetComponent<Renderer>();

        if (screenRenderer == null)
        {
            if (!warnedMissingRenderer)
            {
                warnedMissingRenderer = true;
                Debug.LogWarning(
                    "[ParentConsoleScreenDisplay] 画面のRendererが未設定です。" +
                    "Inspectorの Screen Renderer に、ゲーム機の画面PlaneのRendererを指定してください（画面は変更しません）。", this);
            }
            return false;
        }

        Material[] materials = screenRenderer.sharedMaterials;
        if (materials == null || materials.Length == 0)
        {
            if (!warnedMissingRenderer)
            {
                warnedMissingRenderer = true;
                Debug.LogWarning("[ParentConsoleScreenDisplay] 画面のRendererにマテリアルが設定されていません（画面は変更しません）。", this);
            }
            return false;
        }

        if (screenMaterialTemplate != null)
        {
            // テンプレートの設定（テクスチャ等）を引き継いでインスタンス化する。
            screenMaterial = new Material(screenMaterialTemplate);
        }
        else
        {
            Shader shader = ResolveScreenShader();
            if (shader == null)
            {
                if (!warnedMissingShader)
                {
                    warnedMissingShader = true;
                    Debug.LogWarning(
                        "[ParentConsoleScreenDisplay] 画面用のShaderが見つかりません。" +
                        "Inspectorの Screen Shader に Universal Render Pipeline/Unlit を指定してください（画面は変更しません）。", this);
                }
                return false;
            }

            screenMaterial = new Material(shader);
        }

        screenMaterial.name = "ConsoleScreen (Runtime)";

        // 画面に貼る画像（任意）。存在するプロパティだけに設定する。
        if (screenTexture != null)
        {
            if (screenMaterial.HasProperty(BaseMapProperty)) screenMaterial.SetTexture(BaseMapProperty, screenTexture);
            if (screenMaterial.HasProperty(MainTexProperty)) screenMaterial.SetTexture(MainTexProperty, screenTexture);
        }

        // 初期色も設定しておく（OnEnableの初回適用でも上書きされる）。
        SetScreenColor(screenMaterial, offColor);

        // 画面のスロットだけ差し替える（他のスロット＝ゲーム機本体などには触れない）。
        int slot = Mathf.Clamp(materialSlot, 0, materials.Length - 1);
        originalMaterial = materials[slot];
        hasOriginalMaterial = true;

        materials[slot] = screenMaterial;
        screenRenderer.sharedMaterials = materials;

        return true;
    }

    /// <summary>
    /// 画面用のShaderを決める。Inspector指定 → URP Unlit → Legacy Unlit の順。
    ///  ライト・環境光・反射の影響を受けないShaderを使うことで、消灯時に黒のまま見える。
    /// </summary>
    private Shader ResolveScreenShader()
    {
        if (screenShader != null) return screenShader;

        Shader found = Shader.Find("Universal Render Pipeline/Unlit");
        if (found != null) return found;

        found = Shader.Find("Unlit/Color");
        if (found != null) return found;

        return Shader.Find("Sprites/Default");
    }

    /// <summary>
    /// マテリアルに色を設定する。Shaderに無いプロパティは書かない
    /// （Emissionが無いShaderではEmissionColorとキーワードに触らない）。
    /// </summary>
    private static void SetScreenColor(Material material, Color color)
    {
        if (material == null) return;

        if (material.HasProperty(BaseColorProperty)) material.SetColor(BaseColorProperty, color);
        if (material.HasProperty(ColorProperty)) material.SetColor(ColorProperty, color);

        if (material.HasProperty(EmissionColorProperty))
        {
            material.SetColor(EmissionColorProperty, color);
            material.EnableKeyword(EmissionKeyword);
        }
    }

    // ── 画面用の補助Light（任意）───────────────────────────────────────────────

    /// <summary>
    /// 補助Lightを画面の状態に合わせる（部屋全体のLightには触らない）。
    /// 未設定なら何もしない。寝たふり中は消す。破棄時に元の強さ・有効状態へ戻す。
    /// </summary>
    private void ApplyLight(bool sleeping)
    {
        if (screenLight == null) return;

        if (!lightCaptured)
        {
            lightCaptured = true;
            cachedLight = screenLight;
            lightOriginalIntensity = screenLight.intensity;
            lightOriginalEnabled = screenLight.enabled;
        }

        if (cachedLight == null) return;

        if (sleeping)
        {
            cachedLight.enabled = false;
            return;
        }

        cachedLight.enabled = lightOriginalEnabled;
        cachedLight.intensity = screenLightIntensity > 0f ? screenLightIntensity : lightOriginalIntensity;
    }

    // ── 後片付け ───────────────────────────────────────────────────────────────

    /// <summary>
    /// 実行時生成したマテリアルを破棄し、画面スロットと補助Lightを元へ戻す（何度呼んでも安全）。
    /// </summary>
    private void RestoreAndCleanup()
    {
        if (screenRenderer != null && hasOriginalMaterial)
        {
            Material[] materials = screenRenderer.sharedMaterials;
            if (materials != null && materials.Length > 0)
            {
                int slot = Mathf.Clamp(materialSlot, 0, materials.Length - 1);
                if (materials[slot] == screenMaterial)
                {
                    // 元の共有マテリアルへ戻す（破棄より先に行う）。
                    materials[slot] = originalMaterial;
                    screenRenderer.sharedMaterials = materials;
                }
            }
        }

        if (screenMaterial != null)
        {
            if (Application.isPlaying) Destroy(screenMaterial);
            else DestroyImmediate(screenMaterial);

            screenMaterial = null;
        }

        if (cachedLight != null)
        {
            cachedLight.enabled = lightOriginalEnabled;
            cachedLight.intensity = lightOriginalIntensity;
            cachedLight = null;
        }

        lightCaptured = false;
        hasOriginalMaterial = false;
        originalMaterial = null;
        applied = false;
    }
}
