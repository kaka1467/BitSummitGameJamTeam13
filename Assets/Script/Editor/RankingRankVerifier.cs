using UnityEditor;
using UnityEngine;

/// <summary>
/// RankingRankVerifier:
/// ランキング挿入ロジック（RankingUtil.InsertScore）の順位決定を、実機なし・PlayerPrefs非依存で検証するEditModeツール。
///
/// メニュー「Tools/ランキング順位検証 (EditMode)」を実行すると、Console に各ケースの PASS/FAIL を出力する。
///
/// 重要:
///   - 本番の PlayerPrefs（実データ）には一切触れない。int[] を直接組み立てて検証する。
///   - 本体コードは変更しない（RankingUtil を呼ぶだけ）。
///
/// 期待仕様（既存実装と同一）:
///   - 降順TOP N。strict に大きい枠へ挿入、後ろへ繰り下げ、末尾は破棄。
///   - 同点は既存優先（後から来た同点はその下）。
///   - 0 は未登録枠と同じ扱い（0 > 0 が偽なので挿入されない）。
/// </summary>
public static class RankingRankVerifier
{
    private static int _passCount;
    private static int _failCount;

    [MenuItem("Tools/ランキング順位検証 (EditMode)")]
    public static void Run()
    {
        _passCount = 0;
        _failCount = 0;

        // ① 既存 [500,400,300,200,100] に 450 → [500,450,400,300,200]
        CheckInsert("中間挿入(450)", new[] { 500, 400, 300, 200, 100 }, 450, new[] { 500, 450, 400, 300, 200 });

        // ② 1位更新: 既存 [500,400,300,200,100] に 600 → [600,500,400,300,200]
        CheckInsert("1位更新(600)", new[] { 500, 400, 300, 200, 100 }, 600, new[] { 600, 500, 400, 300, 200 });

        // ③ 最下位未満: 既存 [500,400,300,200,100] に 50 → 変化なし
        CheckInsert("最下位未満(50)", new[] { 500, 400, 300, 200, 100 }, 50, new[] { 500, 400, 300, 200, 100 });

        // ④ 同点: 既存 [500,400,300,200,100] に 400 → 既存優先で [500,400,400,300,200]
        CheckInsert("同点(400)", new[] { 500, 400, 300, 200, 100 }, 400, new[] { 500, 400, 400, 300, 200 });

        // ⑤ 0点: 既存 [500,400,300,200,100] に 0 → 変化なし（未登録枠と同じ扱い）
        CheckInsert("0点(未登録と同じ)", new[] { 500, 400, 300, 200, 100 }, 0, new[] { 500, 400, 300, 200, 100 });

        // ⑥ 空ランキング(全0) に 450 → [450,0,0,0,0]
        CheckInsert("空ランキングへ初回(450)", new[] { 0, 0, 0, 0, 0 }, 450, new[] { 450, 0, 0, 0, 0 });

        // ⑦ 1件のみ [1000,0,0,0,0] に 500 → [1000,500,0,0,0]（2位が正しく入る＝今回の調査対象）
        CheckInsert("1件のみから2位挿入(500)", new[] { 1000, 0, 0, 0, 0 }, 500, new[] { 1000, 500, 0, 0, 0 });

        Debug.Log($"[検証] 完了：PASS={_passCount} / FAIL={_failCount}" +
                  (_failCount == 0 ? " — すべての確認項目を満たしています。" : " — FAIL項目を確認してください。") +
                  "（本番PlayerPrefsには触れていません）");
    }

    private static void CheckInsert(string label, int[] initial, int newScore, int[] expected)
    {
        int[] actual = (int[])initial.Clone();
        RankingUtil.InsertScore(actual, newScore);

        bool ok = actual.Length == expected.Length;
        if (ok)
        {
            for (int i = 0; i < actual.Length; i++)
            {
                if (actual[i] != expected[i]) { ok = false; break; }
            }
        }

        if (ok)
        {
            _passCount++;
            Debug.Log($"[検証] PASS  {label} | initial=[{string.Join(", ", initial)}] + {newScore} " +
                      $"→ [{string.Join(", ", actual)}]");
        }
        else
        {
            _failCount++;
            Debug.LogError($"[検証] FAIL  {label} | initial=[{string.Join(", ", initial)}] + {newScore} " +
                           $"→ actual=[{string.Join(", ", actual)}]  expected=[{string.Join(", ", expected)}]");
        }
    }
}
