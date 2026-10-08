using UnityEngine;

/// <summary>
/// RankingUtil:
/// ランキング配列（降順TOP N）への挿入ロジックを1か所に集約する。
///
/// 目的:
///   - 親機(ParentUdpSender)と子機(GameManager)で同一のロジックを共有する（重複実装のズレ防止）。
///   - PlayerPrefs に依存しない純粋関数にすることで、実機なしでも単体テスト（Editor検証）できるようにする。
///
/// 仕様（既存実装と完全に同一の挙動を維持する）:
///   - ranking は降順（ranking[0] が最高スコア）でサイズ N。
///   - newScore を先頭から順に見て、strict に大きい枠（newScore &gt; ranking[i]）へ挿入する。
///   - 挿入時は後ろへ1つずつ繰り下げ、末尾（N-1）は捨てる。
///   - 同点は既存の値が優先（後から来た同点はその下に入る）。
///   - 0 スコアは未登録枠と同じ扱い（0 は 0 より大きくないため挿入されない＝表示は000000のまま）。
/// </summary>
public static class RankingUtil
{
    /// <summary>
    /// ranking 配列へ newScore を挿入する（配列を直接書き換える）。PlayerPrefs には触れない。
    /// </summary>
    public static void InsertScore(int[] ranking, int newScore)
    {
        if (ranking == null || ranking.Length == 0) return;

        int size = ranking.Length;
        for (int i = 0; i < size; i++)
        {
            if (newScore > ranking[i])
            {
                for (int j = size - 1; j > i; j--)
                {
                    ranking[j] = ranking[j - 1];
                }
                ranking[i] = newScore;
                break;
            }
        }
    }

    /// <summary>
    /// PlayerPrefs から ranking を読み出して newScore を挿入し、書き戻す。
    /// keyPrefix + i （i = 0..size-1）を読み書きする。
    /// </summary>
    public static void InsertScoreToPlayerPrefs(string keyPrefix, int newScore, int size)
    {
        int[] ranking = ReadFromPlayerPrefs(keyPrefix, size);
        InsertScore(ranking, newScore);
        WriteToPlayerPrefs(keyPrefix, ranking);

        // 2位がどこで0になるかを追えるように、保存直前・保存後の全順位をログに出す。
        Debug.Log($"[RankingUtil] key='{keyPrefix}', newScore={newScore}, saved=[{string.Join(", ", ranking)}]");
    }

    public static int[] ReadFromPlayerPrefs(string keyPrefix, int size)
    {
        int[] ranking = new int[size];
        for (int i = 0; i < size; i++)
        {
            ranking[i] = PlayerPrefs.GetInt(keyPrefix + i, 0);
        }
        return ranking;
    }

    public static void WriteToPlayerPrefs(string keyPrefix, int[] ranking)
    {
        if (ranking == null) return;
        for (int i = 0; i < ranking.Length; i++)
        {
            PlayerPrefs.SetInt(keyPrefix + i, ranking[i]);
        }
    }
}
