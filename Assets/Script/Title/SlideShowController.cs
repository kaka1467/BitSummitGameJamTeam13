using System.Threading;
using UnityEngine;

[RequireComponent(typeof(RectTransform))]
public class SlideShowController : MonoBehaviour
{
    [Header("スライドする画像（順番に表示）")]
    [SerializeField] private RectTransform[] slides;

    [Header("タイミング設定")]
    [SerializeField] private float displayDuration = 3f;
    [SerializeField] private float slideDuration   = 0.5f;

    [Header("イージング")]
    [SerializeField] private AnimationCurve slideCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);

    private float  _slideWidth;
    private float  _currentOffset = 0f;   // 全体の現在X位置
    private CancellationTokenSource _cts;

    private void Awake()
    {
        _slideWidth = GetComponent<RectTransform>().rect.width;
        InitSlidePositions();
    }

    private void OnEnable()
    {
        _cts = new CancellationTokenSource();
        _ = SlideLoopAsync(_cts.Token);
    }

    private void OnDisable()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>初期配置：0枚目が正面、1枚目が右隣</summary>
    private void InitSlidePositions()
    {
        _currentOffset = 0f;
        for (int i = 0; i < slides.Length; i++)
            slides[i].anchoredPosition = new Vector2(_slideWidth * i, 0f);
    }

    private async Awaitable SlideLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Awaitable.WaitForSecondsAsync(displayDuration, ct);
                await SlideNextAsync(ct);
            }
        }
        catch (System.OperationCanceledException) { }
    }

    private async Awaitable SlideNextAsync(CancellationToken ct)
    {
        float startOffset = _currentOffset;
        float targetOffset = _currentOffset - _slideWidth;  // 1枚分左へ

        float elapsed = 0f;
        while (elapsed < slideDuration)
        {
            ct.ThrowIfCancellationRequested();
            elapsed += Time.unscaledDeltaTime;
            float t = slideCurve.Evaluate(Mathf.Clamp01(elapsed / slideDuration));

            float offset = Mathf.Lerp(startOffset, targetOffset, t);
            ApplyOffset(offset);

            await Awaitable.NextFrameAsync(ct);
        }

        _currentOffset = targetOffset;
        ApplyOffset(_currentOffset);

        // ★ スライド完了後にループ補正（画面外で瞬間移動するので見えない）
        NormalizePositions();
    }

    /// <summary>全スライドにオフセットを適用</summary>
    private void ApplyOffset(float offset)
    {
        for (int i = 0; i < slides.Length; i++)
            slides[i].anchoredPosition = new Vector2(_slideWidth * i + offset, 0f);
    }

    /// <summary>
    /// 一番左に外れたスライドを右端に移動してオフセットをリセット。
    /// アニメーション完了後（画面外）に実行するので見えない。
    /// </summary>
    private void NormalizePositions()
    {
        int count = slides.Length;
        float totalWidth = _slideWidth * count;

        // _currentOffset が -slideWidth の倍数になるたびに補正
        // 全スライドを右にtotalWidth分ずらしてオフセットを0に近づける
        while (_currentOffset <= -_slideWidth)
        {
            _currentOffset += _slideWidth;

            // 一番左のスライド（先頭）を右端へ移動
            RectTransform first = slides[0];
            for (int i = 0; i < count - 1; i++)
                slides[i] = slides[i + 1];
            slides[count - 1] = first;

            // 右端の位置に配置
            slides[count - 1].anchoredPosition =
                new Vector2(_slideWidth * (count - 1) + _currentOffset, 0f);
        }

        // 全体位置を再適用
        ApplyOffset(_currentOffset);
    }
}