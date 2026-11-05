using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(Item))]
public class ItemEditor : Editor
{
    private SerializedProperty itemTypeProp;
    private SerializedProperty scoreAmountProp;
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

        switch (itemType)
        {
            // case ItemType.Carrot:
            case ItemType.Score:
                EditorGUILayout.LabelField("Score Settings", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(scoreAmountProp, new GUIContent("Score Amount"));
                break;

            case ItemType.Clock:
                // 時計は減点アイテム（時間ではなくポイントを減らす）のため、Score と同じ scoreAmount を編集できるようにする
                // （負の値で減点。新しい設定フィールドは増やさない）。
                EditorGUILayout.LabelField("Score Settings", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(scoreAmountProp, new GUIContent("Score Amount"));
                break;

            case ItemType.HugeObstacle:
                EditorGUILayout.HelpBox("HugeObstacle は QTE の報酬（成功時の加点）を QTEManager で設定します。", MessageType.None);
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

                // Loud Item は減点アイテムのため、Score と同じ scoreAmount を編集できるようにする
                // （負の値で減点。新しい設定フィールドは増やさない）。
                EditorGUILayout.LabelField("Score Settings", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(scoreAmountProp, new GUIContent("Score Amount"));
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
