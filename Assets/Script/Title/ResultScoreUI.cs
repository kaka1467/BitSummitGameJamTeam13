using UnityEngine;
using TMPro;

/// <summary>
/// ResultScoreUI: Displays latest score and top rankings for a specific result type.
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
    [Tooltip("Assign any count; number of elements = display count.")]
    [SerializeField] private TextMeshProUGUI[] rankTexts;
    [SerializeField] private TextMeshProUGUI rank1Text;
    [SerializeField] private TextMeshProUGUI rank2Text;
    [SerializeField] private TextMeshProUGUI rank3Text;

    [Header("Cross-Type Ranking (optional)")]
    [Tooltip("Assign any count; number of elements = display count.")]
    [SerializeField] private TextMeshProUGUI[] crossRankTexts;
    [SerializeField] private TextMeshProUGUI crossRank1Text;
    [SerializeField] private TextMeshProUGUI crossRank2Text;
    [SerializeField] private TextMeshProUGUI crossRank3Text;

    private const string KeyGameOverScore = "LastGameOverScore";
    private const string KeyTimeUpScore   = "LastTimeUpScore";
    private const string KeyGameOverRank  = "GameOverRank_";
    private const string KeyTimeUpRank    = "TimeUpRank_";
    private const int    RankingSize      = 5;

    private void OnEnable()
    {
        // 結果データへの書き込み（GameManager.TriggerResult など）が
        // シーン読み込み後に完了したときに、表示を自動で再読み込みする。
        // （データ書き込みがシーン読み込み後の表示リフレッシュより後に行われるケースに対応するため）
        GameManager.ResultDataCommitted += RefreshResultData;
        // 親機側: 子機からUDP受信した最終スコアの保存・ランキング更新完了でも表示を再読み込みする。
        // 子機GameManagerのイベントだけに依存せず、親機のUDP受信・保存経路とも同期する。
        ParentUdpSender.ResultDataCommitted += RefreshResultData;
        RefreshResultData();
    }

    private void OnDisable()
    {
        GameManager.ResultDataCommitted -= RefreshResultData;
        ParentUdpSender.ResultDataCommitted -= RefreshResultData;
    }

    private void Start()
    {
        RefreshResultData();

        // 書き込み側（GameManager.Start / Awake）が Start の後に走るケースに備え、
        // 1フレーム後に同じ表示処理をもう一度実行する安全網。
        // 同一の読み出し・同一の書き込みなので、通常ケースでは表示は変化しない。
        StartCoroutine(RefreshResultDataNextFrame());
    }

    private System.Collections.IEnumerator RefreshResultDataNextFrame()
    {
        yield return null;
        RefreshResultData();
    }

    private void RefreshResultData()
    {
        string scoreKey   = resultType == DisplayResultType.GameOver ? KeyGameOverScore  : KeyTimeUpScore;
        string rankKey    = resultType == DisplayResultType.GameOver ? KeyGameOverRank   : KeyTimeUpRank;
        string crossKey   = resultType == DisplayResultType.GameOver ? KeyTimeUpRank     : KeyGameOverRank;

        int score = PlayerPrefs.GetInt(scoreKey, 0);

        if (resultScoreText != null)
            resultScoreText.text = score.ToString("000000");

        int[] ranking = GetRanking(rankKey);
        TextMeshProUGUI[] primaryTexts = ResolveTexts(rankTexts, rank1Text, rank2Text, rank3Text);
        ApplyRankingTexts(primaryTexts, ranking);

        // 2位がどこで0になるかを追えるように、UIへ渡した順位をログに出す。
        UnityEngine.Debug.Log($"[ResultScoreUI] resultType={resultType}, scoreKey='{scoreKey}', score={score}, " +
                              $"rankKey='{rankKey}', ranking=[{string.Join(", ", ranking)}], " +
                              $"rankTextsCount={(primaryTexts != null ? primaryTexts.Length : 0)}");

        int[] crossRanking = GetRanking(crossKey);
        TextMeshProUGUI[] otherTexts = ResolveTexts(crossRankTexts, crossRank1Text, crossRank2Text, crossRank3Text);
        ApplyRankingTexts(otherTexts, crossRanking);
    }

    private static int[] GetRanking(string keyPrefix)
    {
        int[] ranking = new int[RankingSize];
        for (int i = 0; i < RankingSize; i++)
            ranking[i] = PlayerPrefs.GetInt(keyPrefix + i, 0);
        return ranking;
    }

    private static TextMeshProUGUI[] ResolveTexts(TextMeshProUGUI[] preferred, params TextMeshProUGUI[] fallback)
    {
        if (preferred != null && preferred.Length > 0) return preferred;
        return fallback;
    }

    private static void ApplyRankingTexts(TextMeshProUGUI[] texts, int[] ranking)
    {
        if (texts == null || ranking == null) return;

        int count = Mathf.Min(texts.Length, ranking.Length);
        for (int i = 0; i < count; i++)
        {
            if (texts[i] == null) continue;
            texts[i].text = ranking[i].ToString("000000");
        }
    }
}
