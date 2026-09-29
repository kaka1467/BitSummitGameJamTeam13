using System.Collections;
using UnityEngine;

/// <summary>
/// タイトル画面でゲームを開始するとき、画面を黒くフェードアウトする（BGMのフェードとは別物）。
/// - 全画面を覆う黒い Image を持つ GameObject に、この CanvasGroup ごとアタッチする。
///   Canvas の一番最後の子（最前面）に置くこと。
/// - 初期状態は透明（alpha 0）。ParentUdpSender がゲーム開始時（Start Button / Solo Start）に
///   FadeOut() を呼ぶと、fadeOutSeconds かけて黒（alpha 1）まで暗くなる。
/// - 暗くなったまま、シーン遷移でこの GameObject ごと消える（フェードインの処理は無い）。
/// </summary>
[RequireComponent(typeof(CanvasGroup))]
public class TitleScreenFader : MonoBehaviour
{
    [Tooltip("画面が真っ黒になるまでの秒数。")]
    [SerializeField, Min(0f)] private float fadeOutSeconds = 1f;

    public float FadeOutSeconds => fadeOutSeconds;

    private CanvasGroup canvasGroup;
    private Coroutine fadeRoutine;

    private void Awake()
    {
        canvasGroup = GetComponent<CanvasGroup>();
        canvasGroup.alpha = 0f;
        canvasGroup.blocksRaycasts = false;
        canvasGroup.interactable = false;
    }

    /// <summary>
    /// 画面を黒くフェードアウトする。多重に呼んでも二重には走らない。
    /// 戻り値は常に true（呼び出し側の判定を単純にするため）。
    /// </summary>
    public bool FadeOut()
    {
        if (fadeRoutine == null)
        {
            fadeRoutine = StartCoroutine(FadeOutRoutine());
        }
        return true;
    }

    private IEnumerator FadeOutRoutine()
    {
        // フェード開始と同時にクリックを塞ぎ、暗転中にボタンを連打されないようにする
        canvasGroup.blocksRaycasts = true;

        float from = canvasGroup.alpha;
        float elapsed = 0f;
        while (elapsed < fadeOutSeconds)
        {
            elapsed += Time.unscaledDeltaTime;
            canvasGroup.alpha = Mathf.Lerp(from, 1f, Mathf.Clamp01(elapsed / fadeOutSeconds));
            yield return null;
        }

        canvasGroup.alpha = 1f;
    }
}
