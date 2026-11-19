using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 子機タイトルの「スコアリセット」。確認パネルで「はい」を選んだときだけ、スコア・ランキングの保存値を消す。
/// 全難易度（EASY / NORMAL / HARD）のスコア・ランキングと同期用ミラーキーのみをリセットし、
/// 言語・音量・選択難易度などの設定や、接続・同期の識別子には触れない（M-Q04 / D-Q03）。
/// </summary>
public class RankingResetButton : MonoBehaviour
{
    [SerializeField] private GameObject confirmationPanel; // 確認UIのパネル

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

        GameDifficulty.ResetAllRankings();

        Debug.Log("[RankingResetButton] 全難易度のスコア・ランキングをリセットしました（言語・音量・難易度設定は保持しています）。");

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
