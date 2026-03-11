using UnityEngine;

public class BackgroundScroll : MonoBehaviour
{
   // スクロールの速さ
    [SerializeField] private float scrollSpeed = 0.5f;
    
    private Material _material;
    private Vector2 _offset = Vector2.zero;

    void Start()
    {
        //マテリアルを取得
        _material = GetComponent<Renderer>().material;
    }

    void Update()
    {
        // 時間の経過に合わせてオフセット値を計算
        _offset.x += scrollSpeed * Time.deltaTime;
        
        // マテリアルのメインテクスチャのオフセットを更新
        _material.mainTextureOffset = _offset;
    }
}