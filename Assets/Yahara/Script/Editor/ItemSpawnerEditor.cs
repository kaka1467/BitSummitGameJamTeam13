using UnityEditor;
using UnityEngine;
using System.Collections.Generic;

[CustomEditor(typeof(ItemSpawner))]
public class ItemSpawnerEditor : Editor
{
    private SerializedProperty normalSpawnRulesProp;
    private SerializedProperty hugeObstacleSpawnCountProp;
    private SerializedProperty minSpawnDistanceXProp;
    private SerializedProperty minSpawnDistanceYProp;
    private SerializedProperty recentSpawnWindowProp;
    private SerializedProperty overlapAvoidanceXStepProp;
    private SerializedProperty maxPlacementAttemptsProp;
    private SerializedProperty spawnOffsetFromRightProp;
    private SerializedProperty lanesYProp;
    private SerializedProperty hugeInitialDelayProp;
    private SerializedProperty hugeCooldownAfterQteProp;

    private void OnEnable()
    {
        normalSpawnRulesProp = serializedObject.FindProperty("normalSpawnRules");
        hugeObstacleSpawnCountProp = serializedObject.FindProperty("hugeObstacleSpawnCount");
        minSpawnDistanceXProp = serializedObject.FindProperty("minSpawnDistanceX");
        minSpawnDistanceYProp = serializedObject.FindProperty("minSpawnDistanceY");
        recentSpawnWindowProp = serializedObject.FindProperty("recentSpawnWindow");
        overlapAvoidanceXStepProp = serializedObject.FindProperty("overlapAvoidanceXStep");
        maxPlacementAttemptsProp = serializedObject.FindProperty("maxPlacementAttempts");
        spawnOffsetFromRightProp = serializedObject.FindProperty("spawnOffsetFromRight");
        lanesYProp = serializedObject.FindProperty("lanesY");
        hugeInitialDelayProp = serializedObject.FindProperty("hugeInitialDelay");
        hugeCooldownAfterQteProp = serializedObject.FindProperty("hugeCooldownAfterQte");

        EnsureRules();
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        EnsureRules();

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

            SerializedProperty countsProp = addedRule.FindPropertyRelative("prefabSpawnCounts");
            countsProp.ClearArray();
            EnsureRuleEntries(countsProp, displayPrefabs);
        }

        EditorGUILayout.Space(6f);
        EditorGUILayout.PropertyField(hugeObstacleSpawnCountProp, new GUIContent("HugeObstacle Count (per huge event)"));
        EditorGUILayout.HelpBox("QTEのオブジェクトはItemSpawnerで管理されます。", MessageType.Info);

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

        EditorGUILayout.Space(6f);
        EditorGUILayout.PropertyField(hugeInitialDelayProp);
        EditorGUILayout.PropertyField(hugeCooldownAfterQteProp);

        EditorGUILayout.Space(6f);
        EditorGUILayout.PropertyField(spawnOffsetFromRightProp);
        EditorGUILayout.PropertyField(lanesYProp, true);

        serializedObject.ApplyModifiedProperties();
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
