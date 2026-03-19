using UnityEngine;

public class BackgroundScroll : MonoBehaviour
{
   // スクロールの速さ
    [SerializeField] private float scrollSpeed = 0.5f;
    [Header("Fever")]
    [SerializeField] private float feverSpeedMultiplier = 2f;
    
    private Material _material;
    private Vector2 _offset = Vector2.zero;

    void Start()
    {
        //マテリアルを取得
        _material = GetComponent<Renderer>().material;
    }

    void Update()
    {
        // フィーバー時は速さを上げる
        float currentSpeed = scrollSpeed;
        if (GameManager.instance != null && GameManager.instance.IsFeverMagnetActive)
        {
            currentSpeed *= feverSpeedMultiplier;
        }

        // 時間の経過に合わせてオフセット値を計算
        _offset.x += currentSpeed * Time.deltaTime;
        
        // マテリアルのメインテクスチャのオフセットを更新
        _material.mainTextureOffset = _offset;
    }
}