using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(RawImage))]
public class FeverLoopEffect : MonoBehaviour
{
    [SerializeField] private RawImage rawImage;
    [SerializeField] private float scrollSpeed = 0.25f;
    [SerializeField] private bool scrollDown = true;
    [SerializeField] private float uvRectHeight = 1f;

    private bool isActive;

    private void Awake()
    {
        EnsureRawImage();
    }

    private void Update()
    {
        if (!isActive || rawImage == null)
        {
            return;
        }

        float dt = Time.deltaTime;
        float direction = scrollDown ? -1f : 1f;
        Rect uv = rawImage.uvRect;
        uv.height = uvRectHeight;
        uv.y = Mathf.Repeat(uv.y + direction * scrollSpeed * dt, 1f);
        rawImage.uvRect = uv;
    }

    public void StartEffect()
    {
        EnsureRawImage();

        if (rawImage != null)
        {
            if (rawImage.texture != null)
            {
                rawImage.texture.wrapMode = TextureWrapMode.Repeat;
            }

            Rect uv = rawImage.uvRect;
            uv.x = 0f;
            uv.y = 0f;
            uv.height = uvRectHeight;
            rawImage.uvRect = uv;
        }

        isActive = true;
        gameObject.SetActive(true);
    }

    public void StopEffect()
    {
        isActive = false;
        gameObject.SetActive(false);
    }

    private void EnsureRawImage()
    {
        if (rawImage == null)
        {
            rawImage = GetComponent<RawImage>();
        }
    }
}