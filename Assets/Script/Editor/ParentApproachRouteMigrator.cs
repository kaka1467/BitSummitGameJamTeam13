using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// ParentApproachRouteMigrator：
/// 廊下ルート（旧 hallwayPoint1〜3 → List）の移行を【編集時に明示的に】実行するEditorツール。
///
/// 実行時の互換移行（MotherApproachController.MigrateLegacyHallwayPoints）とは別物で、
/// こちらは「編集時のオブジェクトを実際に書き換える」唯一の手段。
/// 実行時側はメモリ上だけで完結し、編集時のシーン/Prefabへは書き戻さない。
///
/// メニュー：
///   ・Tools/親機ルート: 選択中を移行           … 選択中の MotherApproachController を移行（通常はこちら）
///   ・Tools/親機ルート: 開いているシーンを移行 … 開いているシーン内の該当コンポーネントをすべて移行
///   ・Tools/親機ルート: 選択中を旧フィールドから作り直す … 既存Listを破棄して旧値から作り直す（警告あり）
///
/// 安全のため、以下を守る：
///   ・Play中は実行を拒否する
///   ・Undo に登録する（Ctrl/Cmd+Z で移行前に戻せる）
///   ・対象オブジェクトを Dirty にして「変更済み」として扱う
///   ・Prefabインスタンスの場合は RecordPrefabInstancePropertyModifications で Override を記録する
///   ・シーンを自動保存しない（保存はユーザー操作に委ねる）
///   ・選択中／開いているシーン以外には一切触れない
/// </summary>
public static class ParentApproachRouteMigrator
{
    private const string MenuMigrateSelected   = "Tools/親機ルート: 選択中を移行";
    private const string MenuMigrateOpenScenes = "Tools/親機ルート: 開いているシーンを移行";
    private const string MenuRebuildSelected   = "Tools/親機ルート: 選択中を旧フィールドから作り直す";

    // ── 通常の移行：選択中のコンポーネント ────────────────────────────────
    [MenuItem(MenuMigrateSelected, priority = 100)]
    public static void MigrateSelected()
    {
        if (!ValidateEditMode()) return;

        var controllers = Selection.GetFiltered<MotherApproachController>(SelectionMode.Editable);
        if (controllers == null || controllers.Length == 0)
        {
            Debug.LogWarning("[親機ルート移行] MotherApproachController を持つオブジェクトを選択してから実行してください。");
            return;
        }

        int totalAdded = 0;
        int migratedCount = 0;

        foreach (MotherApproachController controller in controllers)
        {
            int added = MigrateOne(controller);
            if (added < 0) continue;   // 既に移行済み
            totalAdded += added;
            migratedCount++;
        }

        ReportResult(migratedCount, controllers.Length, totalAdded);
    }

    // ── 通常の移行：開いているシーン全体 ──────────────────────────────────
    [MenuItem(MenuMigrateOpenScenes, priority = 101)]
    public static void MigrateOpenScenes()
    {
        if (!ValidateEditMode()) return;

        var controllers = Object.FindObjectsByType<MotherApproachController>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);

        if (controllers == null || controllers.Length == 0)
        {
            Debug.LogWarning("[親機ルート移行] 開いているシーンに MotherApproachController が見つかりません。");
            return;
        }

        int totalAdded = 0;
        int migratedCount = 0;

        foreach (MotherApproachController controller in controllers)
        {
            // Prefabアセット自体（シーン外）と、保存済みPrefabは対象外にする。
            if (EditorUtility.IsPersistent(controller)) continue;

            int added = MigrateOne(controller);
            if (added < 0) continue;
            totalAdded += added;
            migratedCount++;
        }

        ReportResult(migratedCount, controllers.Length, totalAdded);
    }

    // ── 旧フィールドから作り直す（既存Listを破棄する明示操作） ─────────────
    [MenuItem(MenuRebuildSelected, priority = 200)]
    public static void RebuildSelected()
    {
        if (!ValidateEditMode()) return;

        var controllers = Selection.GetFiltered<MotherApproachController>(SelectionMode.Editable);
        if (controllers == null || controllers.Length == 0)
        {
            Debug.LogWarning("[親機ルート移行] MotherApproachController を持つオブジェクトを選択してから実行してください。");
            return;
        }

        bool confirmed = EditorUtility.DisplayDialog(
            "廊下ルートを旧フィールドから作り直す",
            "現在の hallwayPointsBeforeTurn / hallwayPointsAfterTurn の内容を破棄し、\n" +
            "旧 hallwayPoint1〜3 の値から作り直します。\n\n" +
            "この操作は Ctrl/Cmd+Z で戻せます。よろしいですか？",
            "作り直す", "やめる");

        if (!confirmed) return;

        int count = 0;
        int total = 0;

        foreach (MotherApproachController controller in controllers)
        {
            Undo.RecordObject(controller, "廊下ルートを旧フィールドから作り直す");
            controller.RebuildFromLegacyFields();
            MarkDirtyAndRecordPrefabOverrides(controller);

            count++;
            total += controller.GetHallwayPointsBeforeTurn().Count + controller.GetHallwayPointsAfterTurn().Count;
        }

        Debug.Log($"[親機ルート移行] 旧フィールドから作り直しました：{count}件 / 合計{total}点。\n" +
                  "この操作は Undo（Ctrl/Cmd+Z）で戻せます。内容を確認して保存してください。");
    }

    // ──────────────────────────────────────────────────────────────────────
    //  内部処理
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>1つのコンポーネントを移行する。戻り値：追加した点数（移行済みなら -1）。</summary>
    private static int MigrateOne(MotherApproachController controller)
    {
        if (controller.IsHallwayRouteMigrated) return -1;   // 未移行のときだけ取り込む

        Undo.RecordObject(controller, "親機の廊下ルートを移行");

        int added = controller.ApplyEditTimeMigration();

        MarkDirtyAndRecordPrefabOverrides(controller);

        if (added > 0)
        {
            Debug.Log($"[親機ルート移行] '{controller.gameObject.name}' を移行しました（取り込み={added}点）| " +
                      $"Before={controller.GetHallwayPointsBeforeTurn().Count}件 " +
                      $"After={controller.GetHallwayPointsAfterTurn().Count}件");
        }
        else
        {
            Debug.Log($"[親機ルート移行] '{controller.gameObject.name}' は旧フィールドが空のため、" +
                      "移行済みフラグのみ確定しました（Listは現在の内容のまま）。");
        }

        return added;
    }

    /// <summary>
    /// 対象を「変更済み」として扱い、PrefabインスタンスならOverrideを記録する。
    /// シーンの自動保存は行わない（保存はユーザー操作に委ねる）。
    /// </summary>
    private static void MarkDirtyAndRecordPrefabOverrides(MotherApproachController controller)
    {
        EditorUtility.SetDirty(controller);

        // Prefabインスタンスの場合、変更をOverrideとして記録する。
        if (PrefabUtility.IsPartOfPrefabInstance(controller))
        {
            PrefabUtility.RecordPrefabInstancePropertyModifications(controller);
        }

        // シーンを変更済みとして扱う（Unityが保存確認を行えるようにする）。
        Scene scene = controller.gameObject.scene;
        if (scene.IsValid() && scene.isLoaded && !EditorUtility.IsPersistent(controller))
        {
            EditorSceneManager.MarkSceneDirty(scene);
        }
    }

    private static bool ValidateEditMode()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogWarning("[親機ルート移行] Play中は実行できません。Playを停止してから実行してください。" +
                             "（実行時は互換移行がメモリ上でだけ働き、編集時のシーンには保存されません）");
            return false;
        }

        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            Debug.LogWarning("[親機ルート移行] コンパイル中／更新中は実行できません。完了してから実行してください。");
            return false;
        }

        return true;
    }

    private static void ReportResult(int migratedCount, int scannedCount, int totalAdded)
    {
        if (migratedCount == 0)
        {
            Debug.Log($"[親機ルート移行] 対象{scannedCount}件はすべて移行済みでした（変更なし）。");
            return;
        }

        Debug.Log($"[親機ルート移行] 完了：{migratedCount}/{scannedCount}件を移行（取り込み合計={totalAdded}点）。\n" +
                  "この操作は Undo（Ctrl/Cmd+Z）で戻せます。\n" +
                  "永続化するには シーン（またはPrefab）を保存 してください。自動保存はしていません。");
    }
}
