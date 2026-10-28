using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 子機タイトルの「スコアリセット」。確認パネルで「はい」を選んだときだけ、スコア・ランキングの保存値を消す。
/// 消す対象は下の12キー（ゲームオーバー／クリアの最新得点とTOP5）だけ。
/// 言語・音量などの設定や、接続・同期の保存値には触れない（PlayerPrefs.DeleteAll は使わない）。
/// </summary>
public class RankingResetButton : MonoBehaviour
{
    [SerializeField] private GameObject confirmationPanel; // 確認UIのパネル

    private const string KeyGameOverRank = "GameOverRank_";
    private const string KeyTimeUpRank   = "TimeUpRank_";
    private const string KeyLastGameOverScore = "LastGameOverScore";
    private const string KeyLastTimeUpScore   = "LastTimeUpScore";
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
        // 確認パネルが開いていないときの誤発火では消さない（キー操作の取りこぼし対策）。
        if (confirmationPanel != null && !confirmationPanel.activeInHierarchy) return;

        for (int i = 0; i < RankingSize; i++)
        {
            PlayerPrefs.SetInt(KeyGameOverRank + i, 0);
            PlayerPrefs.SetInt(KeyTimeUpRank   + i, 0);
        }
        PlayerPrefs.SetInt(KeyLastGameOverScore, 0);
        PlayerPrefs.SetInt(KeyLastTimeUpScore,   0);
        PlayerPrefs.Save();

        Debug.Log("[RankingResetButton] スコア・ランキングをリセットしました（言語・音量などの設定は変更していません）。");

        // 子機が唯一の基準: リセット後の状態（全0・最新結果なし）を親機へ同期する
        ChildUdpRuntimeHost host = ChildUdpRuntimeHost.Running != null
            ? ChildUdpRuntimeHost.Running
            : FindFirstObjectByType<ChildUdpRuntimeHost>();

        if (host != null)
        {
            host.NotifyRankingChanged(RankingSnapshot.TypeNone);
        }
        else
        {
            Debug.LogWarning("[RankingResetButton] ChildUdpRuntimeHost not found — リセット内容を親機へ同期できません（子機ビルド以外では正常）。");
        }

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