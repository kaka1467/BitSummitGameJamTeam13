using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

public class GaugeSceneChanger : MonoBehaviour
{
    [Header("UI設定")]
    [SerializeField] private Image gaugeImage; // ゲージとなるUI Image

    [Header("タイマー設定")]
    [SerializeField] private float duration = 5.0f; // ゲージが満タンになるまでの時間（秒）

    [Header("遷移先シーン名")]
    [SerializeField] private string nextSceneName; // 切り替えるシーンの名前

    [Header("挙動設定")]
    [SerializeField] private bool changeSceneOnComplete = true;

    private float currentFillAmount = 0.0f;
    private bool isSceneChanging = false;
    public bool IsComplete { get; private set; }

    void Start()
    {
        if (gaugeImage == null)
        {
            // 自身にImageコンポーネントがあれば自動取得
            gaugeImage = GetComponent<Image>();
        }

        // 初期状態はゲージを空にする
        if (gaugeImage != null)
        {
            gaugeImage.fillAmount = 0;
        }
        else
        {
            Debug.LogError("UI Imageが設定されていません！");
        }
    }

    void Update()
    {
        if (gaugeImage == null || isSceneChanging) return;

        // 時間の経過に合わせてfillAmountを増やしていく
        currentFillAmount += Time.deltaTime / duration;
        gaugeImage.fillAmount = Mathf.Clamp01(currentFillAmount);

        // ゲージが1（満タン）になったらシーン遷移
        if (gaugeImage.fillAmount >= 1.0f)
        {
            IsComplete = true;
            if (changeSceneOnComplete)
            {
                ChangeScene();
            }
        }
    }

    public void SetChangeSceneOnComplete(bool enabled)
    {
        changeSceneOnComplete = enabled;
    }

    private void ChangeScene()
    {
        isSceneChanging = true;

        if (!string.IsNullOrEmpty(nextSceneName))
        {
            SceneManager.LoadScene(nextSceneName);
        }
        else
        {
            Debug.LogError("遷移先のシーン名が空です！インスペクターを確認してください。");
        }
    }
}