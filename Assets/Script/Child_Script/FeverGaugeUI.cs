using UnityEngine;
using UnityEngine.UI;

public class FeverGaugeUI : MonoBehaviour
{
    [Header("Fever Gauge Settings")]
    [Tooltip("空の枠画像 (fevergage_shirowaku)")]
    public Sprite emptyFrameSprite;
    
    [Tooltip("王冠付きの瓶画像 (Fever_item)")]
    public Sprite filledBottleSprite;
    
    [Tooltip("ゲージを表示するImageコンポーネントの配列 (5個)")]
    public Image[] gaugeImages;
    
    [Header("フィーバー中の減り方（終わりを見せる）")]
    [Tooltip("フィーバー中は、ゲージを満タンから、右の瓶から順に少しずつ薄くして消していく。消えかけの瓶の最小の濃さ。")]
    [SerializeField, Range(0f, 1f)] private float drainMinAlpha = 0.3f;
    [Tooltip("フィーバーの残りがこの割合以下になったら、残っている瓶を点滅させて、もうすぐ終わることを伝える。0で点滅なし。")]
    [SerializeField, Range(0f, 1f)] private float warnRemainingRatio = 0.25f;
    [Tooltip("点滅の速さ。")]
    [SerializeField, Min(0.1f)] private float blinkSpeed = 18f;
    [Tooltip("点滅の暗いほうの濃さ（掛け算）。")]
    [SerializeField, Range(0f, 1f)] private float blinkDimAlpha = 0.4f;

    private GameManager gameManager;
    private int lastFeverCount = 0;
    private bool wasFever;

    void Start()
    {
        gameManager = GameManager.instance;
        
        if (gameManager == null)
        {
            Debug.LogError("FeverGaugeUI: GameManager.instance is null!");
            return;
        }
        
        if (gaugeImages == null || gaugeImages.Length == 0)
        {
            Debug.LogError("FeverGaugeUI: gaugeImages array is not set in Inspector!");
            return;
        }
        
        // 初期化：全て空の枠にする
        UpdateGaugeDisplay(0);
    }

    void Update()
    {
        if (gameManager == null) return;

        // フィーバー中は、残り時間に合わせてゲージを減らす（通常の表示は止める）。
        if (gameManager.IsFeverActive)
        {
            wasFever = true;
            UpdateFeverDrain(gameManager.FeverRemainingRatio);
            return;
        }

        // フィーバーが終わったら、薄くした色を戻して、通常のゲージ表示へ戻す。
        if (wasFever)
        {
            wasFever = false;
            RestoreImageAlpha();
            UpdateGaugeDisplay(gameManager.feverCount);
            lastFeverCount = gameManager.feverCount;
            return;
        }

        // FeverCountが変更されたら表示を更新
        if (gameManager.feverCount != lastFeverCount)
        {
            UpdateGaugeDisplay(gameManager.feverCount);
            lastFeverCount = gameManager.feverCount;
        }
    }

    /// <summary>
    /// ゲージの表示を更新
    /// </summary>
    /// <param name="currentCount">現在のFever取得数</param>
    private void UpdateGaugeDisplay(int currentCount)
    {
        
        if (gaugeImages == null || gaugeImages.Length == 0)
        {
            Debug.LogWarning("FeverGaugeUI: gaugeImages is empty!");
            return;
        }
        
        for (int i = 0; i < gaugeImages.Length; i++)
        {
            if (gaugeImages[i] == null)
            {
                Debug.LogWarning($"FeverGaugeUI: gaugeImages[{i}] is null!");
                continue;
            }
            
            RectTransform rectTransform = gaugeImages[i].GetComponent<RectTransform>();
            if (rectTransform == null)
            {
                Debug.LogWarning($"FeverGaugeUI: gaugeImages[{i}] has no RectTransform!");
                continue;
            }
            
            // i番目のゲージを埋めるかどうか
            if (i < currentCount)
            {
                // 王冠付きの瓶に切り替え
                gaugeImages[i].sprite = filledBottleSprite;
                
                // アイテム取得後のスケール: X=2, Y=1.6, Z=0
                rectTransform.localScale = new Vector3(2f, 1.6f, 0f);
                }
            else
            {
                // 空の枠に戻す
                gaugeImages[i].sprite = emptyFrameSprite;
                
                // 初期のスケール: X=1.149964, Y=1.325197, Z=0
                rectTransform.localScale = new Vector3(1.149964f, 1.325197f, 0f);
            }
            
            // 画像が実際に設定されているか確認
            if (gaugeImages[i].sprite == null)
            {
                Debug.LogError($"FeverGaugeUI: Gauge {i} sprite is NULL after assignment!");
            }
        }
    }
    
    /// <summary>
    /// フィーバー中の表示。残りの割合（1→0）に合わせて、瓶を右（最後の番号）から順に、薄くして消していく。
    ///  ・i番目の瓶の残り＝clamp(割合×瓶の数 − i, 0, 1)。満タンの瓶は王冠付き、0になったら空の枠に戻る。
    ///  ・消えかけの瓶は、残りに応じて薄くなる（drainMinAlpha まで）。
    ///  ・終わりが近づいたら（warnRemainingRatio 以下）、残っている瓶を点滅させる。
    /// </summary>
    private void UpdateFeverDrain(float remainingRatio)
    {
        if (gaugeImages == null || gaugeImages.Length == 0) return;

        int count = gaugeImages.Length;
        bool blinkDim = warnRemainingRatio > 0f && remainingRatio <= warnRemainingRatio &&
                        Mathf.Sin(Time.unscaledTime * blinkSpeed) < 0f;

        for (int i = 0; i < count; i++)
        {
            Image image = gaugeImages[i];
            if (image == null) continue;

            RectTransform rect = image.rectTransform;
            float bottle = Mathf.Clamp01(remainingRatio * count - i);

            if (bottle > 0f)
            {
                image.sprite = filledBottleSprite;
                rect.localScale = new Vector3(2f, 1.6f, 0f);

                float alpha = Mathf.Lerp(drainMinAlpha, 1f, bottle);
                if (blinkDim) alpha *= blinkDimAlpha;
                SetAlpha(image, alpha);
            }
            else
            {
                image.sprite = emptyFrameSprite;
                rect.localScale = new Vector3(1.149964f, 1.325197f, 0f);
                SetAlpha(image, 1f);
            }
        }
    }

    private void RestoreImageAlpha()
    {
        if (gaugeImages == null) return;

        for (int i = 0; i < gaugeImages.Length; i++)
            if (gaugeImages[i] != null) SetAlpha(gaugeImages[i], 1f);
    }

    private static void SetAlpha(Image image, float alpha)
    {
        Color c = image.color;
        c.a = alpha;
        image.color = c;
    }

    /// <summary>
    /// 外部から手動で更新する場合
    /// </summary>
    public void ForceUpdate()
    {
        if (gameManager != null)
        {
            UpdateGaugeDisplay(gameManager.feverCount);
        }
    }
}