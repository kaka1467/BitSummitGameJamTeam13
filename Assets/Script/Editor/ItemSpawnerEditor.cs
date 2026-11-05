using UnityEditor;
using UnityEngine;
using System.Collections.Generic;

[CustomEditor(typeof(ItemSpawner))]
public class ItemSpawnerEditor : Editor
{
    // ループ用プロパティはルールごとに FindPropertyRelative で取得するため、ここでは宣言しない
    private SerializedProperty normalSpawnRulesProp;
    private SerializedProperty hugeObstacleSpawnCountProp;
    private SerializedProperty hugeObstacleSpawnYProp;
    private SerializedProperty minSpawnDistanceXProp;
    private SerializedProperty minSpawnDistanceYProp;
    private SerializedProperty recentSpawnWindowProp;
    private SerializedProperty overlapAvoidanceXStepProp;
    private SerializedProperty maxPlacementAttemptsProp;
    private SerializedProperty spawnOffsetFromRightProp;
    private SerializedProperty lanesYProp;
    private SerializedProperty hugeInitialDelayProp;
    private SerializedProperty hugeCooldownAfterQteProp;
    private SerializedProperty showDebugLogsProp;

    // 以前はカスタムインスペクタに出ておらず、Debugモードでしか触れなかった設定
    private SerializedProperty spawnEnabledProp;
    private SerializedProperty bgmNormalSpawnElapsedSecondsProp;
    private SerializedProperty hugeFeverDeferralSecondsProp;
    private SerializedProperty retryIntervalProp;
    private SerializedProperty maxRetryCountProp;
    private SerializedProperty enableDebugSpawnKeysProp;
    private SerializedProperty showDebugSpawnKeyLogsProp;

    // 調整値・配置パターン
    private SerializedProperty itemSpeedMultiplierProp;
    private SerializedProperty spawnAmountMultiplierProp;
    private SerializedProperty badItemWeightMultiplierProp;
    private SerializedProperty patternsEnabledProp;
    private SerializedProperty firstPatternDelaySecondsProp;
    private SerializedProperty patternIntervalMinSecondsProp;
    private SerializedProperty patternIntervalMaxSecondsProp;
    private SerializedProperty patternClearanceSecondsProp;
    private SerializedProperty spawnFeverPatternOnFeverStartProp;
    private SerializedProperty patternsProp;

    private void OnEnable()
    {
        normalSpawnRulesProp = serializedObject.FindProperty("normalSpawnRules");
        hugeObstacleSpawnCountProp = serializedObject.FindProperty("hugeObstacleSpawnCount");
        hugeObstacleSpawnYProp = serializedObject.FindProperty("hugeObstacleSpawnY");
        minSpawnDistanceXProp = serializedObject.FindProperty("minSpawnDistanceX");
        minSpawnDistanceYProp = serializedObject.FindProperty("minSpawnDistanceY");
        recentSpawnWindowProp = serializedObject.FindProperty("recentSpawnWindow");
        overlapAvoidanceXStepProp = serializedObject.FindProperty("overlapAvoidanceXStep");
        maxPlacementAttemptsProp = serializedObject.FindProperty("maxPlacementAttempts");
        spawnOffsetFromRightProp = serializedObject.FindProperty("spawnOffsetFromRight");
        lanesYProp = serializedObject.FindProperty("lanesY");
        hugeInitialDelayProp = serializedObject.FindProperty("hugeInitialDelay");
        hugeCooldownAfterQteProp = serializedObject.FindProperty("hugeCooldownAfterQte");
        showDebugLogsProp = serializedObject.FindProperty("showDebugLogs");

        spawnEnabledProp = serializedObject.FindProperty("spawnEnabled");
        bgmNormalSpawnElapsedSecondsProp = serializedObject.FindProperty("bgmNormalSpawnElapsedSeconds");
        hugeFeverDeferralSecondsProp = serializedObject.FindProperty("hugeFeverDeferralSeconds");
        retryIntervalProp = serializedObject.FindProperty("retryInterval");
        maxRetryCountProp = serializedObject.FindProperty("maxRetryCount");
        enableDebugSpawnKeysProp = serializedObject.FindProperty("enableDebugSpawnKeys");
        showDebugSpawnKeyLogsProp = serializedObject.FindProperty("showDebugSpawnKeyLogs");

        itemSpeedMultiplierProp = serializedObject.FindProperty("itemSpeedMultiplier");
        spawnAmountMultiplierProp = serializedObject.FindProperty("spawnAmountMultiplier");
        badItemWeightMultiplierProp = serializedObject.FindProperty("badItemWeightMultiplier");
        patternsEnabledProp = serializedObject.FindProperty("patternsEnabled");
        firstPatternDelaySecondsProp = serializedObject.FindProperty("firstPatternDelaySeconds");
        patternIntervalMinSecondsProp = serializedObject.FindProperty("patternIntervalMinSeconds");
        patternIntervalMaxSecondsProp = serializedObject.FindProperty("patternIntervalMaxSeconds");
        patternClearanceSecondsProp = serializedObject.FindProperty("patternClearanceSeconds");
        spawnFeverPatternOnFeverStartProp = serializedObject.FindProperty("spawnFeverPatternOnFeverStart");
        patternsProp = serializedObject.FindProperty("patterns");

        EnsureRules();
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        EnsureRules();

        DrawIfExists(spawnEnabledProp, "Spawn Control", "通常アイテムの自動出現を行うか。OFF にすると通常出現とHugeObstacleの自動出現が止まる（数字キーのデバッグ出現は別）。");

        EditorGUILayout.Space(4f);
        EditorGUILayout.LabelField("Normal Spawn Rules", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("各 Rule はゲーム開始からの時間範囲です。Min〜Max 秒の間で、設定した個数だけ1回スポーンします。", MessageType.Info);
        List<GameObject> displayPrefabs = GetNormalPrefabsFromItemPool();
        if (displayPrefabs.Count == 0)
        {
            EditorGUILayout.HelpBox("ItemPool の Item Prefabs に通常アイテムのプレハブを設定すると、ここに個数設定が表示されます。", MessageType.Warning);
        }

        for (int i = 0; i < normalSpawnRulesProp.arraySize; i++)
        {
            SerializedProperty ruleProp = normalSpawnRulesProp.GetArrayElementAtIndex(i);
            SerializedProperty intervalProp = ruleProp.FindPropertyRelative("interval");
            SerializedProperty minProp = intervalProp.FindPropertyRelative("minInterval");
            SerializedProperty maxProp = intervalProp.FindPropertyRelative("maxInterval");
            SerializedProperty countsProp = ruleProp.FindPropertyRelative("prefabSpawnCounts");

            EnsureRuleEntries(countsProp, displayPrefabs);

            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"Rule {i + 1}", EditorStyles.boldLabel);
            if (GUILayout.Button("Remove", GUILayout.Width(70f)) && normalSpawnRulesProp.arraySize > 1)
            {
                normalSpawnRulesProp.DeleteArrayElementAtIndex(i);
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
                break;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PropertyField(minProp, new GUIContent("Min (sec)"));
            EditorGUILayout.PropertyField(maxProp, new GUIContent("Max (sec)"));
            EditorGUILayout.EndHorizontal();

            // ── ループ設定 ──────────────────────────────
            SerializedProperty loopEnabledProp = ruleProp.FindPropertyRelative("loopEnabled");
            SerializedProperty loopCycleProp   = ruleProp.FindPropertyRelative("loopCycleDuration");
            EditorGUILayout.PropertyField(loopEnabledProp, new GUIContent("Loop"));
            if (loopEnabledProp.boolValue)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(loopCycleProp, new GUIContent("Cycle (sec)", "ループの周期（秒）。0 にすると Max-Min の幅がそのまま使われます。"));
                if (loopCycleProp.floatValue <= 0f)
                {
                    float autoVal = Mathf.Max(1f, maxProp.floatValue - minProp.floatValue);
                    EditorGUILayout.HelpBox($"Cycle = 0 → 自動で {autoVal:F1} 秒周期になります。", MessageType.None);
                }
                EditorGUI.indentLevel--;
            }
            // ────────────────────────────────────────────

            EditorGUILayout.Space(4f);
            foreach (var prefab in displayPrefabs)
            {
                SerializedProperty countProp = FindCountProperty(countsProp, prefab);
                if (countProp == null) continue;

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(prefab.name, GUILayout.Width(160f));
                EditorGUILayout.PropertyField(countProp, GUIContent.none);
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(4f);
        }

        if (GUILayout.Button("Add Rule"))
        {
            int insertIndex = normalSpawnRulesProp.arraySize;
            normalSpawnRulesProp.InsertArrayElementAtIndex(insertIndex);
            SerializedProperty addedRule = normalSpawnRulesProp.GetArrayElementAtIndex(insertIndex);
            addedRule.FindPropertyRelative("interval").FindPropertyRelative("minInterval").floatValue = 10f;
            addedRule.FindPropertyRelative("interval").FindPropertyRelative("maxInterval").floatValue = 30f;
            addedRule.FindPropertyRelative("loopEnabled").boolValue = false;
            addedRule.FindPropertyRelative("loopCycleDuration").floatValue = 0f;

            SerializedProperty countsProp = addedRule.FindPropertyRelative("prefabSpawnCounts");
            countsProp.ClearArray();
            EnsureRuleEntries(countsProp, displayPrefabs);
        }

        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("BGM Spawn Condition", EditorStyles.boldLabel);
        if (bgmNormalSpawnElapsedSecondsProp != null)
        {
            EditorGUILayout.PropertyField(bgmNormalSpawnElapsedSecondsProp);
            EditorGUILayout.HelpBox("BGM（ラウドアイテム）は、本編開始（チュートリアル終了）からの経過時間がこの秒数以上になってから通常出現します。" +
                                    "QTE中の時間停止の影響は受けません。", MessageType.Info);
        }

        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("Tuning（速度・出現量・悪い割合）", EditorStyles.boldLabel);
        DrawIfExists(itemSpeedMultiplierProp);
        DrawIfExists(spawnAmountMultiplierProp);
        DrawIfExists(badItemWeightMultiplierProp);
        EditorGUILayout.HelpBox("速度・出現量は通常出現とパターンのアイテムに掛かります（巨大障害物・チュートリアルには掛かりません）。" +
                                "悪い割合は上の Rule の個数(重み)に倍率を掛けて変えます。1 で従来どおりです。", MessageType.None);

        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("Placement Patterns（配置パターン）", EditorStyles.boldLabel);
        DrawIfExists(patternsEnabledProp);
        if (patternsEnabledProp == null || patternsEnabledProp.boolValue)
        {
            DrawIfExists(firstPatternDelaySecondsProp);
            DrawIfExists(patternIntervalMinSecondsProp);
            DrawIfExists(patternIntervalMaxSecondsProp);
            DrawIfExists(patternClearanceSecondsProp);
            DrawIfExists(spawnFeverPatternOnFeverStartProp);
            if (patternsProp != null) EditorGUILayout.PropertyField(patternsProp, true);
            EditorGUILayout.HelpBox("パターンを出している間（と余裕の秒数）は、ランダム出現を止めて二重に出ないようにします。" +
                                    "休憩は、slots を空にして Rest Seconds を設定します。デバッグ中は数字キー7で一覧を順に1つずつ出せます。", MessageType.Info);
        }

        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("HugeObstacle Settings", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(hugeObstacleSpawnCountProp, new GUIContent("Count (per huge event)"));
        EditorGUILayout.PropertyField(hugeObstacleSpawnYProp, new GUIContent("Spawn Y Position"));
        EditorGUILayout.HelpBox("QTEのオブジェクトはItemSpawnerで管理されます。Spawn Y PositionでHugeObstacleのY座標を設定できます。", MessageType.Info);

        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("Overlap Avoidance", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.PropertyField(minSpawnDistanceXProp, new GUIContent("Min Distance X"));
        EditorGUILayout.PropertyField(minSpawnDistanceYProp, new GUIContent("Min Distance Y"));
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.PropertyField(recentSpawnWindowProp, new GUIContent("Recent Window"));
        EditorGUILayout.PropertyField(overlapAvoidanceXStepProp, new GUIContent("Fallback X Step"));
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.PropertyField(maxPlacementAttemptsProp, new GUIContent("Max Placement Attempts"));
        EditorGUILayout.BeginHorizontal();
        DrawIfExists(retryIntervalProp);
        DrawIfExists(maxRetryCountProp);
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(6f);
        EditorGUILayout.PropertyField(hugeInitialDelayProp);
        EditorGUILayout.PropertyField(hugeCooldownAfterQteProp);
        DrawIfExists(hugeFeverDeferralSecondsProp);

        EditorGUILayout.Space(6f);
        EditorGUILayout.PropertyField(spawnOffsetFromRightProp);
        EditorGUILayout.PropertyField(lanesYProp, true);

        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("Debug", EditorStyles.boldLabel);
        if (showDebugLogsProp != null)
        {
            EditorGUILayout.PropertyField(showDebugLogsProp, new GUIContent("Show Debug Logs"));
        }
        DrawIfExists(enableDebugSpawnKeysProp);
        DrawIfExists(showDebugSpawnKeyLogsProp);

        serializedObject.ApplyModifiedProperties();
    }

    // プロパティが存在するときだけ描画する（フィールドの改名・削除でエディタが落ちないようにする）。
    // label/tooltip を渡さなければ、ItemSpawner 側の [Tooltip] がそのまま使われる。
    private static void DrawIfExists(SerializedProperty prop, string label = null, string tooltip = null)
    {
        if (prop == null) return;

        if (label == null)
            EditorGUILayout.PropertyField(prop);
        else
            EditorGUILayout.PropertyField(prop, new GUIContent(label, tooltip));
    }

    private void EnsureRules()
    {
        if (normalSpawnRulesProp == null) return;

        if (normalSpawnRulesProp.arraySize == 0)
        {
            normalSpawnRulesProp.InsertArrayElementAtIndex(0);
            SerializedProperty ruleProp = normalSpawnRulesProp.GetArrayElementAtIndex(0);
            ruleProp.FindPropertyRelative("interval").FindPropertyRelative("minInterval").floatValue = 10f;
            ruleProp.FindPropertyRelative("interval").FindPropertyRelative("maxInterval").floatValue = 30f;
        }

        for (int i = 0; i < normalSpawnRulesProp.arraySize; i++)
        {
            SerializedProperty ruleProp = normalSpawnRulesProp.GetArrayElementAtIndex(i);
            SerializedProperty countsProp = ruleProp.FindPropertyRelative("prefabSpawnCounts");
            List<GameObject> displayPrefabs = GetNormalPrefabsFromItemPool();
            EnsureRuleEntries(countsProp, displayPrefabs);
        }
    }

    private void EnsureRuleEntries(SerializedProperty countsProp, List<GameObject> displayPrefabs)
    {
        if (countsProp == null) return;

        if (displayPrefabs == null) displayPrefabs = new List<GameObject>();

        foreach (var prefab in displayPrefabs)
        {
            if (prefab == null) continue;
            if (FindEntryIndex(countsProp, prefab) >= 0) continue;

            int insertIndex = countsProp.arraySize;
            countsProp.InsertArrayElementAtIndex(insertIndex);
            SerializedProperty element = countsProp.GetArrayElementAtIndex(insertIndex);
            element.FindPropertyRelative("prefab").objectReferenceValue = prefab;
            element.FindPropertyRelative("count").intValue = 0;
        }

        for (int i = countsProp.arraySize - 1; i >= 0; i--)
        {
            SerializedProperty element = countsProp.GetArrayElementAtIndex(i);
            GameObject prefab = element.FindPropertyRelative("prefab").objectReferenceValue as GameObject;
            if (prefab == null || !displayPrefabs.Contains(prefab))
            {
                countsProp.DeleteArrayElementAtIndex(i);
            }
        }
    }

    private SerializedProperty FindCountProperty(SerializedProperty countsProp, GameObject prefab)
    {
        int index = FindEntryIndex(countsProp, prefab);
        if (index < 0) return null;

        SerializedProperty element = countsProp.GetArrayElementAtIndex(index);
        return element.FindPropertyRelative("count");
    }

    private int FindEntryIndex(SerializedProperty countsProp, GameObject prefab)
    {
        if (countsProp == null) return -1;
        if (prefab == null) return -1;

        for (int i = 0; i < countsProp.arraySize; i++)
        {
            SerializedProperty element = countsProp.GetArrayElementAtIndex(i);
            GameObject current = element.FindPropertyRelative("prefab").objectReferenceValue as GameObject;
            if (current == prefab) return i;
        }

        return -1;
    }

    private List<GameObject> GetNormalPrefabsFromItemPool()
    {
        var result = new List<GameObject>();

        ItemPool pool = FindFirstObjectByType<ItemPool>();
        if (pool == null || pool.itemPrefabs == null)
        {
            return result;
        }

        foreach (var prefab in pool.itemPrefabs)
        {
            if (prefab == null) continue;
            if (result.Contains(prefab)) continue;

            Item item = prefab.GetComponent<Item>();
            if (item == null) continue;
            if (item.itemType == ItemType.HugeObstacle) continue;

            result.Add(prefab);
        }

        return result;
    }
}