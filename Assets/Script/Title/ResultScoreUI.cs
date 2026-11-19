using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// ResultScoreUI: Displays latest score and top rankings for a specific result type,
/// scoped per difficulty (EASY / NORMAL / HARD) with a difficulty badge (D-Q03 / M-R06).
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

    [Header("親機: 同期状態の表示（任意）")]
    [Tooltip("「未同期」「前回同期データ」「同期完了」を表示する小さなテキスト。未設定でも値は正しく扱います。")]
    [SerializeField] private TextMeshProUGUI syncStateText;

    private const int RankingSize = 5;

    // 親機で子機データ未同期のときに表示する文字（「最新の子機データ」と誤解させない）
    private const string NoDataText = "------";

    private void OnEnable()
    {
        GameManager.ResultDataCommitted += RefreshResultData;
        GameDifficulty.OnSettingsChanged += RefreshResultData;
        SubscribeParentHost();
        HideLegacyDifficultyBadgeIfPresent();
        RefreshResultData();
    }

    private void OnDisable()
    {
        GameManager.ResultDataCommitted -= RefreshResultData;
        GameDifficulty.OnSettingsChanged -= RefreshResultData;
        UnsubscribeParentHost();
    }

    // 親機Host個体（購読した個体を保持し、解除も同じ個体へ行う）
    private ParentUdpRuntimeHost _subscribedHost;

    private void SubscribeParentHost()
    {
        ParentUdpRuntimeHost host = ResolveParentHost();
        if (host == null || host == _subscribedHost) return;

        UnsubscribeParentHost();
        host.ResultDataCommitted += RefreshResultData;
        _subscribedHost = host;
    }

    private void UnsubscribeParentHost()
    {
        if (_subscribedHost == null) return;

        _subscribedHost.ResultDataCommitted -= RefreshResultData;
        _subscribedHost = null;
    }

    private static ParentUdpRuntimeHost ResolveParentHost()
    {
        ParentUdpRuntimeHost host = ParentUdpRuntimeHost.Running;
        if (host != null) return host;

        return FindFirstObjectByType<ParentUdpRuntimeHost>();
    }

    private void Start()
    {
        SubscribeParentHost();
        HideLegacyDifficultyBadgeIfPresent();
        RefreshResultData();
        StartCoroutine(RefreshResultDataNextFrame());
    }

    private System.Collections.IEnumerator RefreshResultDataNextFrame()
    {
        yield return null;
        SubscribeParentHost();
        RefreshResultData();
    }

    private void HideLegacyDifficultyBadgeIfPresent()
    {
        Canvas canvas = GetComponentInParent<Canvas>();
        if (canvas == null) canvas = FindFirstObjectByType<Canvas>();
        if (canvas == null) return;

        Transform existing = canvas.transform.Find("DifficultyRankingBadge");
        if (existing != null && existing.gameObject.activeSelf)
        {
            existing.gameObject.SetActive(false);
        }
    }

    private void RefreshResultData()
    {

        // 選択中の難易度のランキングをミラーキーへ同期する（D-Q03）
        GameDifficulty.SyncActiveDifficultyRankingToMirrorKeys(false);

        ParentUdpRuntimeHost host = ResolveParentHost();
        if (host != null)
        {
            RefreshFromParentSyncCopy(host);
            return;
        }

        GameDifficulty.Level diff = GameDifficulty.Current;
        string scoreKey = resultType == DisplayResultType.GameOver
            ? GameDifficulty.GetLastGameOverScoreKey(diff)
            : GameDifficulty.GetLastTimeUpScoreKey(diff);
        string rankKey = resultType == DisplayResultType.GameOver
            ? GameDifficulty.GetGameOverRankPrefix(diff)
            : GameDifficulty.GetTimeUpRankPrefix(diff);
        string crossKey = resultType == DisplayResultType.GameOver
            ? GameDifficulty.GetTimeUpRankPrefix(diff)
            : GameDifficulty.GetGameOverRankPrefix(diff);

        int score = PlayerPrefs.GetInt(scoreKey, 0);

        if (resultScoreText != null)
            resultScoreText.text = score.ToString("000000");

        int[] ranking = GetRanking(rankKey);
        TextMeshProUGUI[] primaryTexts = ResolveTexts(rankTexts, rank1Text, rank2Text, rank3Text);
        ApplyRankingTexts(primaryTexts, ranking);

        UnityEngine.Debug.Log($"[ResultScoreUI] (child) diff={diff}, resultType={resultType}, scoreKey='{scoreKey}', score={score}, " +
                              $"rankKey='{rankKey}', ranking=[{string.Join(", ", ranking)}], " +
                              $"rankTextsCount={(primaryTexts != null ? primaryTexts.Length : 0)}");

        int[] crossRanking = GetRanking(crossKey);
        TextMeshProUGUI[] otherTexts = ResolveTexts(crossRankTexts, crossRank1Text, crossRank2Text, crossRank3Text);
        ApplyRankingTexts(otherTexts, crossRanking);
    }

    private void RefreshFromParentSyncCopy(ParentUdpRuntimeHost host)
    {
        bool isGameOver = resultType == DisplayResultType.GameOver;

        RankSyncDisplayState state = host.GetSyncDisplayState();

        bool hasGo = host.TryGetSyncedResult(true, out int goLast, out int[] goTop);
        bool hasTu = host.TryGetSyncedResult(false, out int tuLast, out int[] tuTop);
        bool hasThis = isGameOver ? hasGo : hasTu;

        TextMeshProUGUI[] primaryTexts = ResolveTexts(rankTexts, rank1Text, rank2Text, rank3Text);
        TextMeshProUGUI[] otherTexts = ResolveTexts(crossRankTexts, crossRank1Text, crossRank2Text, crossRank3Text);

        if (syncStateText != null)
        {
            syncStateText.text = $"{GameDifficulty.DisplayName} / {RankSyncDisplay.ToLabel(state)}";
        }

        if (state == RankSyncDisplayState.NoData || !hasThis)
        {
            if (resultScoreText != null) resultScoreText.text = NoDataText;
            ApplyNoDataTexts(primaryTexts);
            ApplyNoDataTexts(otherTexts);
            UnityEngine.Debug.LogWarning($"[ResultScoreUI] (parent) {RankSyncDisplay.ToLabel(state)}: 子機データがまだありません。'{NoDataText}' を表示 (resultType={resultType}).");
            return;
        }

        if (!RankSyncDisplay.ShouldShowValues(state, syncStateText != null))
        {
            if (resultScoreText != null) resultScoreText.text = NoDataText;
            ApplyNoDataTexts(primaryTexts);
            ApplyNoDataTexts(otherTexts);
            UnityEngine.Debug.LogWarning($"[ResultScoreUI] (parent) 前回同期データ（最新の接続相手ではありません）。" +
                                         $"状態表示先(syncStateText)が未設定のため値を伏せます (resultType={resultType}).");
            return;
        }

        int score = isGameOver ? goLast : tuLast;
        int[] ranking = isGameOver ? goTop : tuTop;
        int[] crossRanking = isGameOver ? tuTop : goTop;

        if (resultScoreText != null)
            resultScoreText.text = score.ToString("000000");

        ApplyRankingTexts(primaryTexts, ranking);
        ApplyRankingTexts(otherTexts, crossRanking);

        UnityEngine.Debug.Log($"[ResultScoreUI] (parent {GameDifficulty.DisplayName} {RankSyncDisplay.ToLabel(state)}) resultType={resultType}, score={score}, " +
                              $"ranking=[{string.Join(", ", ranking)}], source='{host.RankSyncSourceId}', update={host.RankSyncUpdateNo}, peer='{host.ConnectedPeerSourceId}'");
    }

    private static void ApplyNoDataTexts(TextMeshProUGUI[] texts)
    {
        if (texts == null) return;
        for (int i = 0; i < texts.Length; i++)
        {
            if (texts[i] == null) continue;
            texts[i].text = NoDataText;
        }
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
