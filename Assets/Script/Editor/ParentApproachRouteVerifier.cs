using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// ParentApproachRouteVerifier：
/// 廊下ルート（可変List化）と移行処理を EditMode で機械的に確認する検証ツール。
/// テスト群をメニューで分離してあるので、必要な群だけを実行できる。
///
/// メニュー：
///   Tools/親機ルート検証/A: 二重移行しない (EditMode)
///   Tools/親機ルート検証/B: 編集時移行と再読み込み (一時シーン)
///   Tools/親機ルート検証/C: 意図した空Listの維持
///   Tools/親機ルート検証/D: 母親と猫の経路取得順
///
/// A. 二重移行しないこと（メモリ上・EditMode）
///     ・同じオブジェクトで移行を複数回呼んでも点が重複しない
///     ・既存のList要素を勝手に消さない／並べ替えない
/// B. 編集時の保存と再読み込み（一時シーンを新規作成して使用）
///     ・一時シーンを作る → 編集時移行 → 保存 → シーンを閉じて読み直す → 保持を確認
///     ※ 実際の作業シーン（GameScene等）は一切開かない・保存しない
/// C. 意図した空Listを維持すること
///     ・移行後に Before だけ／After だけ／両方を空にし、保存・再読み込み・Start相当を行っても旧値が復活しない
/// D. 母親と猫の経路取得順
///     ・猫が先／母親が先のどちらでも同じ経路設定になる／初期化前の空Listを返さない
///
/// 本体コードは一切変更しない。privateメンバはリフレクションで操作する。
/// 確認が終わればこのファイルごと削除してよい（Editorフォルダなのでビルドには含まれない）。
/// </summary>
public static class ParentApproachRouteVerifier
{
    private const int CoroutineGuard = 100000;

    private static int _passCount;
    private static int _failCount;

    private static readonly List<string> _failures = new List<string>();

    [MenuItem("Tools/親機ルート検証/A: 二重移行しない (EditMode)", priority = 300)]
    public static void RunA() => RunGroup("A");

    [MenuItem("Tools/親機ルート検証/B: 編集時移行と再読み込み (一時シーン)", priority = 301)]
    public static void RunB() => RunGroup("B");

    [MenuItem("Tools/親機ルート検証/C: 意図した空Listの維持", priority = 302)]
    public static void RunC() => RunGroup("C");

    [MenuItem("Tools/親機ルート検証/D: 母親と猫の経路取得順", priority = 303)]
    public static void RunD() => RunGroup("D");

    /// <summary>指定した群だけを実行する。検証用オブジェクトは実行後に必ず破棄する。</summary>
    private static void RunGroup(string group)
    {
        _passCount = 0;
        _failCount = 0;
        _failures.Clear();

        // 検証用の小さなオブジェクトを作る（既存シーンの中身には触れず、実行後に必ず破棄する）。
        var controllerGo = new GameObject("検証用 MotherRouteRoot");
        var h1Go = new GameObject("検証用 HallwayPoint_1");
        var h2Go = new GameObject("検証用 HallwayPoint_2");
        var h3Go = new GameObject("検証用 HallwayPoint_3");
        var h4Go = new GameObject("検証用 HallwayPoint_4");

        try
        {
            switch (group)
            {
                case "A": GroupA_NoDoubleMigration(controllerGo, h1Go, h2Go, h3Go, h4Go); break;
                case "B": GroupB_SaveAndReload(); break;
                case "C": GroupC_EmptyListsStayEmpty(controllerGo, h1Go, h2Go, h3Go); break;
                case "D": GroupD_MotherAndCatOrder(controllerGo, h1Go, h2Go, h3Go); break;
                default:
                    Debug.LogError($"[検証] 不明な群 '{group}'");
                    return;
            }

            Debug.Log($"[検証:{group}] 完了：PASS={_passCount} / FAIL={_failCount}" +
                      (_failCount == 0 ? " — この群の確認項目を満たしています。"
                                       : $" — FAIL項目を確認してください: {string.Join(" / ", _failures)}"));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(controllerGo);
            UnityEngine.Object.DestroyImmediate(h1Go);
            UnityEngine.Object.DestroyImmediate(h2Go);
            UnityEngine.Object.DestroyImmediate(h3Go);
            UnityEngine.Object.DestroyImmediate(h4Go);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  A. 二重移行しないこと（メモリ上）
    // ══════════════════════════════════════════════════════════════════════
    private static void GroupA_NoDoubleMigration(
        GameObject controllerGo, GameObject h1Go, GameObject h2Go, GameObject h3Go, GameObject h4Go)
    {
        var controller = controllerGo.AddComponent<ParentApproachController>();
        var beforeField = Field("hallwayPointsBeforeTurn");
        var afterField = Field("hallwayPointsAfterTurn");

        // 未移行の状態にして、旧フィールドを設定する。
        SetMigrated(controller, false);
        controller.hallwayPoint1 = h1Go.transform;
        controller.hallwayPoint2 = h2Go.transform;
        controller.hallwayPoint3 = h3Go.transform;
        beforeField.SetValue(controller, new List<Transform>());
        afterField.SetValue(controller, new List<Transform>());

        // 同じオブジェクトで移行を複数回呼ぶ（編集時移行API）。
        int first = controller.ApplyEditTimeMigration();
        int second = controller.ApplyEditTimeMigration();
        int third = controller.ApplyEditTimeMigration();

        Check("A 初回の移行で旧3点が取り込まれる", first == 3);
        Check("A 2回目の移行では何も追加されない（二重移行しない）", second == 0);
        Check("A 3回目の移行でも何も追加されない", third == 0);

        Check("A H1/H2が重複していない（Before=2点）", controller.GetHallwayPointsBeforeTurn().Count == 2);
        Check("A H3が重複していない（After=1点）", controller.GetHallwayPointsAfterTurn().Count == 1);
        CheckEquals("A Beforeの順序がH1→H2のまま",
                    controller.GetHallwayPointsBeforeTurn(), new[] { h1Go.transform, h2Go.transform });
        CheckEquals("A AfterがH3のまま",
                    controller.GetHallwayPointsAfterTurn(), new[] { h3Go.transform });

        // 既存Listを勝手に消さないこと：別の点を入れてから移行を呼ぶ。
        SetMigrated(controller, false);
        beforeField.SetValue(controller, new List<Transform> { h4Go.transform });
        afterField.SetValue(controller, new List<Transform>());

        controller.ApplyEditTimeMigration();

        CheckEquals("A 移行時に既存Listの要素を消さない（H4が残り、H1/H2が後ろに追加される）",
                    controller.GetHallwayPointsBeforeTurn(),
                    new[] { h4Go.transform, h1Go.transform, h2Go.transform });
    }

    // ══════════════════════════════════════════════════════════════════════
    //  C. 意図した空Listを維持すること
    // ══════════════════════════════════════════════════════════════════════
    private static void GroupC_EmptyListsStayEmpty(
        GameObject controllerGo, GameObject h1Go, GameObject h2Go, GameObject h3Go)
    {
        var controller = controllerGo.AddComponent<ParentApproachController>();
        var beforeField = Field("hallwayPointsBeforeTurn");
        var afterField = Field("hallwayPointsAfterTurn");

        // 移行済み＋片側／両方を空にする → 実行時の互換移行（Start相当）を呼んでも復活しないことを確認する。
        SetMigrated(controller, true);
        controller.hallwayPoint1 = h1Go.transform;
        controller.hallwayPoint2 = h2Go.transform;
        controller.hallwayPoint3 = h3Go.transform;

        // Before だけ空
        beforeField.SetValue(controller, new List<Transform>());
        afterField.SetValue(controller, new List<Transform> { h3Go.transform });
        controller.MigrateLegacyHallwayPoints();
        CheckCount("C 移行後にBeforeだけ空 → 実行時互換移行を経ても空のまま（H1/H2が復活しない）",
                   controller.GetHallwayPointsBeforeTurn(), 0);

        // After だけ空
        beforeField.SetValue(controller, new List<Transform> { h1Go.transform, h2Go.transform });
        afterField.SetValue(controller, new List<Transform>());
        controller.MigrateLegacyHallwayPoints();
        CheckCount("C 移行後にAfterだけ空 → 実行時互換移行を経ても空のまま（H3が復活しない）",
                   controller.GetHallwayPointsAfterTurn(), 0);

        // 両方空
        beforeField.SetValue(controller, new List<Transform>());
        afterField.SetValue(controller, new List<Transform>());
        controller.MigrateLegacyHallwayPoints();
        CheckCount("C 両方空 → Beforeが空のまま", controller.GetHallwayPointsBeforeTurn(), 0);
        CheckCount("C 両方空 → Afterが空のまま", controller.GetHallwayPointsAfterTurn(), 0);

        // 未移行なら実行時の互換移行が働く（＝古いシーンが動く）も併せて確認する。
        SetMigrated(controller, false);
        beforeField.SetValue(controller, new List<Transform>());
        afterField.SetValue(controller, new List<Transform>());
        controller.MigrateLegacyHallwayPoints();
        CheckCount("C 未移行なら実行時の互換移行でBeforeが埋まる（古いシーンが動く）",
                   controller.GetHallwayPointsBeforeTurn(), 2);
        Check("C 未移行の互換移行は編集時フラグを立てない（保存と区別されている）",
              !controller.IsHallwayRouteMigrated);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  D. 母親と猫の経路取得順
    // ══════════════════════════════════════════════════════════════════════
    private static void GroupD_MotherAndCatOrder(
        GameObject controllerGo, GameObject h1Go, GameObject h2Go, GameObject h3Go)
    {
        var controller = controllerGo.AddComponent<ParentApproachController>();
        var beforeField = Field("hallwayPointsBeforeTurn");
        var afterField = Field("hallwayPointsAfterTurn");

        // 未移行（＝古いシーン）で、猫が先に経路取得するケース。
        SetMigrated(controller, false);
        controller.hallwayPoint1 = h1Go.transform;
        controller.hallwayPoint2 = h2Go.transform;
        controller.hallwayPoint3 = h3Go.transform;
        beforeField.SetValue(controller, new List<Transform>());
        afterField.SetValue(controller, new List<Transform>());

        // 猫が最初に取得（MotherSuspicionSystem.Start より前に CatFeintController が動く想定）。
        List<Transform> catFirst = controller.GetHallwayPointsBeforeTurn();
        List<Transform> catFirstAfter = controller.GetHallwayPointsAfterTurn();

        Check("D 猫が先に取得しても初期化前の空Listを返さない（Before=2点）", catFirst.Count == 2);
        Check("D 猫が先に取得しても初期化前の空Listを返さない（After=1点）", catFirstAfter.Count == 1);

        // 母親が後から取得しても同じ結果になること。
        CheckEquals("D 猫が先→母親が後でも同じ経路になる",
                    controller.GetHallwayPointsBeforeTurn(), catFirst.ToArray());

        // 逆順（母親が先）を別インスタンスで確認する。
        var otherGo = new GameObject("検証用 MotherRouteRoot(逆順)");
        try
        {
            var motherFirstController = otherGo.AddComponent<ParentApproachController>();
            SetMigrated(motherFirstController, false);
            motherFirstController.hallwayPoint1 = h1Go.transform;
            motherFirstController.hallwayPoint2 = h2Go.transform;
            motherFirstController.hallwayPoint3 = h3Go.transform;
            beforeField.SetValue(motherFirstController, new List<Transform>());
            afterField.SetValue(motherFirstController, new List<Transform>());

            // 母親が先に取得 → その後で猫が取得。
            List<Transform> motherFirst = motherFirstController.GetHallwayPointsBeforeTurn();
            List<Transform> catLater = motherFirstController.GetHallwayPointsBeforeTurn();

            CheckEquals("D 母親が先でも猫が先でも同じ経路になる", catLater, motherFirst.ToArray());
            CheckEquals("D どちらの順でもBeforeの内容が一致する", motherFirst, catFirst.ToArray());
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(otherGo);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  B. 編集時の保存と再読み込み（一時シーンを使用）
    //
    //  ※ 実際の作業シーン（GameScene等）は一切開かない・保存しない。
    //     新規の一時シーンを作り、そこだけで保存・読み直しを検証して削除する。
    // ══════════════════════════════════════════════════════════════════════
    private static void GroupB_SaveAndReload()
    {
        const string tempScenePath = "Assets/__TempRouteVerifyScene.unity";

        // 念のため、前回の残骸があれば消す。
        if (System.IO.File.Exists(tempScenePath)) AssetDatabase.DeleteAsset(tempScenePath);

        Scene originalScene = SceneManager.GetActiveScene();
        Scene tempScene = default;

        try
        {
            tempScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);

            var rootGo = new GameObject("検証用 MotherRouteRoot");
            var h1 = new GameObject("検証用 HallwayPoint_1");
            var h2 = new GameObject("検証用 HallwayPoint_2");
            var h3 = new GameObject("検証用 HallwayPoint_3");
            SceneManager.MoveGameObjectToScene(rootGo, tempScene);
            SceneManager.MoveGameObjectToScene(h1, tempScene);
            SceneManager.MoveGameObjectToScene(h2, tempScene);
            SceneManager.MoveGameObjectToScene(h3, tempScene);

            var controller = rootGo.AddComponent<ParentApproachController>();
            controller.hallwayPoint1 = h1.transform;
            controller.hallwayPoint2 = h2.transform;
            controller.hallwayPoint3 = h3.transform;

            // 編集時の移行（Editorツールが行う処理と同じAPI）。
            SetMigrated(controller, false);
            Check("B 編集時移行で旧3点が取り込まれる", controller.ApplyEditTimeMigration() == 3);

            // この一時シーンだけを保存する（他のシーンには触れない）。
            Check("B 一時シーンの保存に成功する",
                  EditorSceneManager.SaveScene(tempScene, tempScenePath));

            // シーンを閉じて読み直す。
            EditorSceneManager.CloseScene(tempScene, true);
            tempScene = default;

            AssetDatabase.Refresh();
            Scene reloaded = EditorSceneManager.OpenScene(tempScenePath, OpenSceneMode.Additive);
            var reloadedController = FindInScene<ParentApproachController>(reloaded);

            Check("B 再読み込み後も ParentApproachController が存在する", reloadedController != null);
            if (reloadedController != null)
            {
                Check("B 再読み込み後も移行済みフラグが保持される", reloadedController.IsHallwayRouteMigrated);
                CheckEquals("B 再読み込み後も Before(List) が保持される",
                            reloadedController.GetHallwayPointsBeforeTurn(),
                            new[] { h1.transform, h2.transform });
                CheckEquals("B 再読み込み後も After(List) が保持される",
                            reloadedController.GetHallwayPointsAfterTurn(),
                            new[] { h3.transform });
            }

            EditorSceneManager.CloseScene(reloaded, true);
        }
        catch (Exception e)
        {
            Record("B 一時シーンでの保存・再読み込み検証", false,
                   $"例外: {e.GetType().Name} {e.Message}");
        }
        finally
        {
            if (tempScene.IsValid() && tempScene.isLoaded) EditorSceneManager.CloseScene(tempScene, true);
            if (System.IO.File.Exists(tempScenePath)) AssetDatabase.DeleteAsset(tempScenePath);
            AssetDatabase.Refresh();

            Check("B 元の作業シーンが有効なまま残っている",
                  originalScene.IsValid() && originalScene.isLoaded);
        }
    }

    private static T FindInScene<T>(Scene scene) where T : Component
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            T found = root.GetComponentInChildren<T>(true);
            if (found != null) return found;
        }
        return null;
    }

    // ──────────────────────────────────────────────────────────────────────
    //  ヘルパー
    // ──────────────────────────────────────────────────────────────────────

    private static FieldInfo Field(string name) =>
        typeof(ParentApproachController).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);

    /// <summary>編集時移行フラグをテスト用に設定し、実行時の解決をやり直させる。</summary>
    private static void SetMigrated(ParentApproachController controller, bool migrated)
    {
        Field("hallwayRouteMigrated")?.SetValue(controller, migrated);
        Field("_hallwayRouteResolved")?.SetValue(controller, false);
    }

    private static void CheckEquals(string label, List<Transform> actual, Transform[] expected)
    {
        bool ok = actual != null && actual.Count == expected.Length;
        if (ok)
        {
            for (int i = 0; i < expected.Length; i++)
            {
                if (actual[i] != expected[i]) { ok = false; break; }
            }
        }
        Record(label, ok, $"actual=[{Describe(actual)}] expected=[{Describe(expected)}]");
    }

    private static void CheckCount(string label, List<Transform> actual, int expectedCount)
    {
        int count = (actual != null) ? actual.Count : -1;
        Record(label, count == expectedCount, $"actual count={count} expected={expectedCount}");
    }

    private static void Check(string label, bool ok) => Record(label, ok, $"ok={ok}");

    private static void Record(string label, bool ok, string detail)
    {
        if (ok)
        {
            _passCount++;
            Debug.Log($"[検証] PASS  {label} | {detail}");
        }
        else
        {
            _failCount++;
            _failures.Add(label);
            Debug.LogError($"[検証] FAIL  {label} | {detail}");
        }
    }

    private static string Describe(IEnumerable<Transform> points)
    {
        if (points == null) return "null";
        var names = new List<string>();
        foreach (Transform t in points) names.Add(t != null ? t.name : "null");
        return string.Join(", ", names);
    }
}
