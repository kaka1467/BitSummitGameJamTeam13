using UnityEngine;
using TMPro;

/// <summary>
/// ResultScoreUI: Displays latest score and top-3 rankings for a specific result type.
///
/// Set resultType to GameOver on the GAME_OVER result scene,
/// and to TimeUp on the TIME_UP result scene.
/// Optionally assign cross-type rank texts to show the other type's ranking.
/// </summary>
public class ResultScoreUI : MonoBehaviour
{
    public enum DisplayResultType { GameOver, TimeUp }

    [Header("Result Type")]
    [Tooltip("Set to GameOver on the GameOver result scene, TimeUp on the TimeUp result scene.")]
    [SerializeField] private DisplayResultType resultType = DisplayResultType.GameOver;

    [Header("This Result Score + Ranking")]
    [SerializeField] private TextMeshProUGUI resultScoreText;
    [SerializeField] private TextMeshProUGUI rank1Text;
    [SerializeField] private TextMeshProUGUI rank2Text;
    [SerializeField] private TextMeshProUGUI rank3Text;

    [Header("Cross-Type Ranking (optional)")]
    [Tooltip("Assign to show the other result type's top 3 on this scene.")]
    [SerializeField] private TextMeshProUGUI crossRank1Text;
    [SerializeField] private TextMeshProUGUI crossRank2Text;
    [SerializeField] private TextMeshProUGUI crossRank3Text;

    private const string KeyGameOverScore = "LastGameOverScore";
    private const string KeyTimeUpScore   = "LastTimeUpScore";
    private const string KeyGameOverRank  = "GameOverRank_";
    private const string KeyTimeUpRank    = "TimeUpRank_";
    private const int    RankingSize      = 3;

    private void Start()
    {
        string scoreKey   = resultType == DisplayResultType.GameOver ? KeyGameOverScore  : KeyTimeUpScore;
        string rankKey    = resultType == DisplayResultType.GameOver ? KeyGameOverRank   : KeyTimeUpRank;
        string crossKey   = resultType == DisplayResultType.GameOver ? KeyTimeUpRank     : KeyGameOverRank;

        int score = PlayerPrefs.GetInt(scoreKey, 0);

        if (resultScoreText != null)
            resultScoreText.text = score.ToString("000000");

        int[] ranking = GetRanking(rankKey);
        ApplyRankingText(rank1Text, ranking, 0);
        ApplyRankingText(rank2Text, ranking, 1);
        ApplyRankingText(rank3Text, ranking, 2);

        int[] crossRanking = GetRanking(crossKey);
        ApplyRankingText(crossRank1Text, crossRanking, 0);
        ApplyRankingText(crossRank2Text, crossRanking, 1);
        ApplyRankingText(crossRank3Text, crossRanking, 2);
    }

    private static int[] GetRanking(string keyPrefix)
    {
        int[] ranking = new int[RankingSize];
        for (int i = 0; i < RankingSize; i++)
            ranking[i] = PlayerPrefs.GetInt(keyPrefix + i, 0);
        return ranking;
    }

    private static void ApplyRankingText(TextMeshProUGUI text, int[] ranking, int index)
    {
        if (text == null || ranking == null || index < 0 || index >= ranking.Length) return;
        text.text = ranking[index].ToString("000000");
    }
}