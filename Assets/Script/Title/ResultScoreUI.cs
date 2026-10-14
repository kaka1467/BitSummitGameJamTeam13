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

    [Header("親機: 同期状態の表示（任意）")]
    [Tooltip("「未同期」「前回同期データ」「同期完了」を表示する小さなテキスト。未設定でも値は正しく扱います。")]
    [SerializeField] private TextMeshProUGUI syncStateText;

    private const string KeyGameOverScore = "LastGameOverScore";
    private const string KeyTimeUpScore   = "LastTimeUpScore";
    private const string KeyGameOverRank  = "GameOverRank_";
    private const string KeyTimeUpRank    = "TimeUpRank_";
    private const int    RankingSize      = 5;

    // 親機で子機データ未同期のときに表示する文字（「最新の子機データ」と誤解させない）
    private const string NoDataText = "------";

    private void OnEnable()
    {
        // 結果データへの書き込み（GameManager.TriggerResult など）が
        // シーン読み込み後に完了したときに、表示を自動で再読み込みする。
        // （データ書き込みがシーン読み込み後の表示リフレッシュより後に行われるケースに対応するため）
        GameManager.ResultDataCommitted += RefreshResultData;
        // 親機側: 子機からUDP受信した最終スコアの保存・ランキング更新完了でも表示を再読み込みする。
        // 子機GameManagerのイベントだけに依存せず、親機のUDP受信・保存経路とも同期する。
        // 購読先は Host 個体（まだ取得できない場合は Start／次フレームの再取得経路で接続する）。
        SubscribeParentHost();
        RefreshResultData();
    }

    private void OnDisable()
    {
        GameManager.ResultDataCommitted -= RefreshResultData;
        UnsubscribeParentHost();
    }

    // 親機Host個体（購読した個体を保持し、解除も同じ個体へ行う）
    private ParentUdpRuntimeHost _subscribedHost;

    /// <summary>
    /// 親機Host個体を取得して ResultDataCommitted を購読する。
    ///  ・同じ個体への二重購読はしない。
    ///  ・個体が変わった場合は旧購読を外してから接続する。
    ///  ・まだ取得できない場合は何もしない（Start・次フレームの既存再取得経路で接続する）。
    /// </summary>
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

    /// <summary>稼働中のHost個体を優先し、無ければシーン上のHost（DDOLの生存個体）を探す。</summary>
    private static ParentUdpRuntimeHost ResolveParentHost()
    {
        ParentUdpRuntimeHost host = ParentUdpRuntimeHost.Running;
        if (host != null) return host;

        return FindFirstObjectByType<ParentUdpRuntimeHost>();
    }

    private void Start()
    {
        // 購読の再取得（OnEnable 時点で Host が取得できなかった場合に接続する）。
        SubscribeParentHost();
        RefreshResultData();

        // 書き込み側（GameManager.Start / Awake）が Start の後に走るケースに備え、
        // 1フレーム後に同じ表示処理をもう一度実行する安全網。
        // 同一の読み出し・同一の書き込みなので、通常ケースでは表示は変化しない。
        StartCoroutine(RefreshResultDataNextFrame());
    }

    private System.Collections.IEnumerator RefreshResultDataNextFrame()
    {
        yield return null;
        SubscribeParentHost();
        RefreshResultData();
    }

    private void RefreshResultData()
    {
        // 親機では「子機データの表示用コピー」だけを表示する（親機の独自ランキングは使わない）。
        // 子機では従来どおり自分のランキング（唯一の基準）を表示する。
        ParentUdpRuntimeHost host = ResolveParentHost();
        if (host != null)
        {
            RefreshFromParentSyncCopy(host);
            return;
        }

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
        UnityEngine.Debug.Log($"[ResultScoreUI] (child) resultType={resultType}, scoreKey='{scoreKey}', score={score}, " +
                              $"rankKey='{rankKey}', ranking=[{string.Join(", ", ranking)}], " +
                              $"rankTextsCount={(primaryTexts != null ? primaryTexts.Length : 0)}");

        int[] crossRanking = GetRanking(crossKey);
        TextMeshProUGUI[] otherTexts = ResolveTexts(crossRankTexts, crossRank1Text, crossRank2Text, crossRank3Text);
        ApplyRankingTexts(otherTexts, crossRanking);
    }

    /// <summary>
    /// 親機: 子機から同期した表示用コピーを表示する。
    /// ・GAME_OVER と TIME_UP は別々に読む（混ぜない）
    /// ・未同期（未接続・同期前）は「------」を表示し、古い親機ランキングを最新として出さない
    /// </summary>
    private void RefreshFromParentSyncCopy(ParentUdpRuntimeHost host)
    {
        bool isGameOver = resultType == DisplayResultType.GameOver;

        RankSyncDisplayState state = host.GetSyncDisplayState();

        bool hasGo = host.TryGetSyncedResult(true, out int goLast, out int[] goTop);
        bool hasTu = host.TryGetSyncedResult(false, out int tuLast, out int[] tuTop);
        bool hasThis = isGameOver ? hasGo : hasTu;

        TextMeshProUGUI[] primaryTexts = ResolveTexts(rankTexts, rank1Text, rank2Text, rank3Text);
        TextMeshProUGUI[] otherTexts = ResolveTexts(crossRankTexts, crossRank1Text, crossRank2Text, crossRank3Text);

        // 理由が分かるように状態を表示（未設定ならログのみ）
        if (syncStateText != null)
        {
            syncStateText.text = RankSyncDisplay.ToLabel(state);
        }

        // 初回同期前（一度も受信していない）
        if (state == RankSyncDisplayState.NoData || !hasThis)
        {
            if (resultScoreText != null) resultScoreText.text = NoDataText;
            ApplyNoDataTexts(primaryTexts);
            ApplyNoDataTexts(otherTexts);
            UnityEngine.Debug.LogWarning($"[ResultScoreUI] (parent) {RankSyncDisplay.ToLabel(state)}: 子機データがまだありません。'{NoDataText}' を表示 (resultType={resultType}).");
            return;
        }

        // 前回同期データ（未接続・切替確認中）: 最新の子機データと誤表示しない。
        // 状態ラベル（syncStateText）が未設定の場合は値を伏せて『------』表示にする（誤表示防止を優先）。
        // Current（同期完了）ならラベル未割当でも値を表示する。判定は RankSyncDisplay に集約。
        if (!RankSyncDisplay.ShouldShowValues(state, syncStateText != null))
        {
            if (resultScoreText != null) resultScoreText.text = NoDataText;
            ApplyNoDataTexts(primaryTexts);
            ApplyNoDataTexts(otherTexts);
            UnityEngine.Debug.LogWarning($"[ResultScoreUI] (parent) 前回同期データ（最新の接続相手ではありません）。" +
                                         $"状態表示先(syncStateText)が未設定のため値を伏せます (resultType={resultType}). " +
                                         "値を表示する場合は ResultScoreUI.syncStateText を割り当ててください。");
            return;
        }

        int score = isGameOver ? goLast : tuLast;
        int[] ranking = isGameOver ? goTop : tuTop;
        int[] crossRanking = isGameOver ? tuTop : goTop;

        if (resultScoreText != null)
            resultScoreText.text = score.ToString("000000");

        ApplyRankingTexts(primaryTexts, ranking);
        ApplyRankingTexts(otherTexts, crossRanking);

        UnityEngine.Debug.Log($"[ResultScoreUI] (parent {RankSyncDisplay.ToLabel(state)}) resultType={resultType}, score={score}, " +
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
