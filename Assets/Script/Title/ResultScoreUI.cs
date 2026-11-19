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

    private Image _difficultyBadgeImage;
    private TextMeshProUGUI _difficultyBadgeLabel;

    private void OnEnable()
    {
        GameManager.ResultDataCommitted += RefreshResultData;
        GameDifficulty.OnSettingsChanged += RefreshResultData;
        SubscribeParentHost();
        EnsureDifficultyBadgeUI();
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
        EnsureDifficultyBadgeUI();
        RefreshResultData();
        StartCoroutine(RefreshResultDataNextFrame());
    }

    private System.Collections.IEnumerator RefreshResultDataNextFrame()
    {
        yield return null;
        SubscribeParentHost();
        RefreshResultData();
    }

    /// <summary>
    /// リザルト・ランキング画面に現在の難易度バッジ（EASY / NORMAL / HARD）を表示し、
    /// タップで別難易度のランキングも切り替え確認できるようにする（D-Q03）。
    /// </summary>
    private void EnsureDifficultyBadgeUI()
    {
        if (_difficultyBadgeImage != null || _difficultyBadgeLabel != null)
        {
            UpdateDifficultyBadgeVisual();
            return;
        }

        Canvas canvas = GetComponentInParent<Canvas>();
        if (canvas == null) canvas = FindFirstObjectByType<Canvas>();
        if (canvas == null) return;

        Transform existing = canvas.transform.Find("DifficultyRankingBadge");
        if (existing != null)
        {
            _difficultyBadgeImage = existing.GetComponent<Image>();
            _difficultyBadgeLabel = existing.GetComponentInChildren<TextMeshProUGUI>(true);
            UpdateDifficultyBadgeVisual();
            return;
        }

        GameObject badgeObj = new GameObject("DifficultyRankingBadge", typeof(RectTransform), typeof(Image), typeof(Button));
        badgeObj.transform.SetParent(canvas.transform, false);
        RectTransform rect = badgeObj.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.02f, 0.90f);
        rect.anchorMax = new Vector2(0.22f, 0.98f);
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        _difficultyBadgeImage = badgeObj.GetComponent<Image>();
        _difficultyBadgeImage.preserveAspect = true;

        Button btn = badgeObj.GetComponent<Button>();
        btn.onClick.AddListener(CycleDifficultyRankingView);

        GameObject labelObj = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        labelObj.transform.SetParent(badgeObj.transform, false);
        RectTransform labelRect = labelObj.GetComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;

        _difficultyBadgeLabel = labelObj.GetComponent<TextMeshProUGUI>();
        _difficultyBadgeLabel.alignment = TextAlignmentOptions.Center;
        _difficultyBadgeLabel.fontSize = 24f;
        _difficultyBadgeLabel.color = Color.white;

        UpdateDifficultyBadgeVisual();
    }

    private void CycleDifficultyRankingView()
    {
        int next = ((int)GameDifficulty.Current + 1) % 3;
        GameDifficulty.SetDifficulty((GameDifficulty.Level)next, true);
        RefreshResultData();
    }

    private void UpdateDifficultyBadgeVisual()
    {
        string spritePath = GameDifficulty.Current switch
        {
            GameDifficulty.Level.Easy => "Difficulty/Easy_UI",
            GameDifficulty.Level.Hard => "Difficulty/Hard_UI",
            _ => "Difficulty/Normal_UI"
        };
        Sprite sp = Resources.Load<Sprite>(spritePath);
        if (_difficultyBadgeImage != null)
        {
            if (sp != null)
            {
                _difficultyBadgeImage.sprite = sp;
                _difficultyBadgeImage.color = Color.white;
                if (_difficultyBadgeLabel != null) _difficultyBadgeLabel.text = "";
            }
            else
            {
                _difficultyBadgeImage.color = new Color(0.15f, 0.15f, 0.22f, 0.9f);
                if (_difficultyBadgeLabel != null)
                    _difficultyBadgeLabel.text = $"RANKING: {GameDifficulty.DisplayName}";
            }
        }
    }

    private void RefreshResultData()
    {
        UpdateDifficultyBadgeVisual();

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
