using UnityEngine;

public class BackgroundScroll : MonoBehaviour
{
    public float speed = 0.05f;
    Renderer rend;

    void Start()
    {
        rend = GetComponent<Renderer>();
    }

    void Update()
    {
        float offset = Time.time * speed;
        rend.material.mainTextureOffset = new Vector2(offset, 0);
    }
}