using UnityEngine;
using UnityEngine.UI;

public class RankingResetButton : MonoBehaviour
{
    [SerializeField] private GameObject confirmationPanel; // 確認UIのパネル

    private const string KeyGameOverRank = "GameOverRank_";
    private const string KeyTimeUpRank   = "TimeUpRank_";
    private const int    RankingSize     = 5;

    private void Start()
    {
        // 起動時は確認パネルを非表示にしておく
        if (confirmationPanel != null)
        {
            confirmationPanel.SetActive(false);
        }
    }

    /// <summary>
    /// リセットボタンを押したときに確認パネルを表示する。
    /// Button.OnClick() に登録する。
    /// </summary>
    public void OnClickResetButton()
    {
        if (confirmationPanel != null)
        {
            confirmationPanel.SetActive(true);
        }
    }

    /// <summary>
    /// 確認パネルの「はい」ボタンに登録する。
    /// </summary>
    public void OnClickConfirm()
    {
        for (int i = 0; i < RankingSize; i++)
        {
            PlayerPrefs.SetInt(KeyGameOverRank + i, 0);
            PlayerPrefs.SetInt(KeyTimeUpRank   + i, 0);
        }
        PlayerPrefs.SetInt("LastGameOverScore", 0);
        PlayerPrefs.SetInt("LastTimeUpScore",   0);
        PlayerPrefs.Save();

        Debug.Log("[RankingResetButton] ランキングをリセットしました。");

        if (confirmationPanel != null)
        {
            confirmationPanel.SetActive(false);
        }
    }

    /// <summary>
    /// 確認パネルの「いいえ」ボタンに登録する。
    /// </summary>
    public void OnClickCancel()
    {
        if (confirmationPanel != null)
        {
            confirmationPanel.SetActive(false);
        }
    }
}