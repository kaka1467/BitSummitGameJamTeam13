using UnityEngine;
using TMPro;

public class ResultScoreUI : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI resultScoreText;
    [SerializeField] private TextMeshProUGUI rank1Text;
    [SerializeField] private TextMeshProUGUI rank2Text;
    [SerializeField] private TextMeshProUGUI rank3Text;

    private const string ResultScoreKey = "ResultScore";
    private const string ResultScorePendingKey = "ResultScorePending";
    private const string RankingScoreKeyPrefix = "RankingScore_";
    private const int RankingSize = 3;

    private void Start()
    {
        int score = PlayerPrefs.GetInt(ResultScoreKey, 0);

        // ゲーム終了直後だけランキングへ反映し、Resultシーン再表示時の重複登録を防ぐ
        if (PlayerPrefs.GetInt(ResultScorePendingKey, 0) == 1)
        {
            UpdateRanking(score);
            PlayerPrefs.SetInt(ResultScorePendingKey, 0);
            PlayerPrefs.Save();
        }

        if (resultScoreText != null)
        {
            resultScoreText.text = score.ToString("000000");
        }

        int[] ranking = GetRanking();
        ApplyRankingText(rank1Text, ranking, 0);
        ApplyRankingText(rank2Text, ranking, 1);
        ApplyRankingText(rank3Text, ranking, 2);

    }

    private static void UpdateRanking(int newScore)
    {
        int[] ranking = GetRanking();

        for (int i = 0; i < RankingSize; i++)
        {
            if (newScore > ranking[i])
            {
                for (int j = RankingSize - 1; j > i; j--)
                {
                    ranking[j] = ranking[j - 1];
                }

                ranking[i] = newScore;
                break;
            }
        }

        SaveRanking(ranking);
    }

    private static int[] GetRanking()
    {
        int[] ranking = new int[RankingSize];
        for (int i = 0; i < RankingSize; i++)
        {
            ranking[i] = PlayerPrefs.GetInt(RankingScoreKeyPrefix + i, 0);
        }

        return ranking;
    }

    private static void SaveRanking(int[] ranking)
    {
        for (int i = 0; i < RankingSize; i++)
        {
            PlayerPrefs.SetInt(RankingScoreKeyPrefix + i, ranking[i]);
        }
    }

    private static void ApplyRankingText(TextMeshProUGUI text, int[] ranking, int index)
    {
        if (text == null || ranking == null || index < 0 || index >= ranking.Length)
        {
            return;
        }

        text.text = ranking[index].ToString("000000");
    }
}