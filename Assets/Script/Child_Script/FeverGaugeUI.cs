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
    
    private GameManager gameManager;
    private int lastFeverCount = 0;

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