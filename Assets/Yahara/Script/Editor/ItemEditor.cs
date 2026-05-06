using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(Item))]
public class ItemEditor : Editor
{
    private SerializedProperty itemTypeProp;
    private SerializedProperty scoreAmountProp;
    private SerializedProperty timeAmountProp;
    private SerializedProperty boostDurationProp;
    private SerializedProperty boostMultiplierProp;
    private SerializedProperty isMagnetableProp;
    private SerializedProperty seClipProp;
    private SerializedProperty seVolumeProp;
    private SerializedProperty bgmClipProp;
    private SerializedProperty loopBgmProp;
    private SerializedProperty bgmVolumeProp;

    private void OnEnable()
    {
        itemTypeProp = serializedObject.FindProperty("itemType");
        scoreAmountProp = serializedObject.FindProperty("scoreAmount");
        timeAmountProp = serializedObject.FindProperty("timeAmount");
        boostDurationProp = serializedObject.FindProperty("boostDuration");
        boostMultiplierProp = serializedObject.FindProperty("boostMultiplier");
        isMagnetableProp = serializedObject.FindProperty("isMagnetable");
        seClipProp = serializedObject.FindProperty("seClip");
        seVolumeProp = serializedObject.FindProperty("seVolume");
        bgmClipProp = serializedObject.FindProperty("bgmClip");
        loopBgmProp = serializedObject.FindProperty("loopBgm");
        bgmVolumeProp = serializedObject.FindProperty("bgmVolume");
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        EditorGUILayout.PropertyField(itemTypeProp);
        EditorGUILayout.Space(4f);

        ItemType itemType = (ItemType)itemTypeProp.enumValueIndex;

        EditorGUILayout.LabelField("Time Settings", EditorStyles.boldLabel);
        if (itemType == ItemType.HugeObstacle)
        {
            EditorGUILayout.HelpBox("HugeObstacle は QTE 失敗時に timeAmount 分の時間が減少します。", MessageType.Info);
            EditorGUILayout.PropertyField(timeAmountProp, new GUIContent("Time Penalty"));
        }
        else
        {
            EditorGUILayout.HelpBox("timeAmount は取得時にそのまま加算されます。正で時間増加、負で時間減少です。", MessageType.Info);
            EditorGUILayout.PropertyField(timeAmountProp, new GUIContent("Time Amount"));
        }
        EditorGUILayout.Space(4f);

        switch (itemType)
        {
            // case ItemType.Carrot:
            case ItemType.Score:
                EditorGUILayout.LabelField("Score Settings", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(scoreAmountProp, new GUIContent("Score Amount"));
                break;

            case ItemType.Clock:
                break;

            // case ItemType.Enemy:
            //     EditorGUILayout.LabelField("Penalty Settings", EditorStyles.boldLabel);
            //     EditorGUILayout.HelpBox("Enemy は timeAmount の絶対値を時間減少として適用します。", MessageType.Info);
            //     EditorGUILayout.PropertyField(timeAmountProp, new GUIContent("Time Penalty"));
            //     break;

            case ItemType.HugeObstacle:
                break;

            case ItemType.Boost:
                EditorGUILayout.LabelField("Boost Settings", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(boostDurationProp, new GUIContent("Duration"));
                EditorGUILayout.PropertyField(boostMultiplierProp, new GUIContent("Multiplier"));
                break;

            case ItemType.Fever:
                EditorGUILayout.HelpBox("Fever は現在、追加パラメータなしです。", MessageType.None);
                break;

            case ItemType.BGM:
                EditorGUILayout.LabelField("BGM Settings", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(bgmClipProp, new GUIContent("BGM Clip"));
                EditorGUILayout.PropertyField(loopBgmProp, new GUIContent("Loop"));
                EditorGUILayout.Slider(bgmVolumeProp, 0f, 1f, new GUIContent("Volume"));
                break;
        }

        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("SFX Settings", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(seClipProp, new GUIContent("SE Clip"));
        EditorGUILayout.Slider(seVolumeProp, 0f, 1f, new GUIContent("SE Volume"));

        EditorGUILayout.Space(6f);
        EditorGUILayout.PropertyField(isMagnetableProp, new GUIContent("Magnetable"));

        serializedObject.ApplyModifiedProperties();
    }
}
