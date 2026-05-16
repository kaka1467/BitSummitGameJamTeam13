using System.Collections;
using UnityEngine;

/// <summary>
/// 親の出現予兆システムを管理します。
/// 一階の明かり → 二階の明かり → ドアに手をかける音 → 親が出現
/// の流れを制御します。
/// </summary>
public class ParentWarningSystem : MonoBehaviour
{
    [Header("ライト設定")]
    [Tooltip("一階のライト（1つ）")]
    public Light firstFloorLight;
    
    [Tooltip("二階のライト（3つ）")]
    public Light[] secondFloorLights = new Light[3];

    [Tooltip("二階でメインカメラ時に点灯させるポイントライト")]
    public Light secondFloorPointLight;

    [Header("親の出現設定")]
    [Tooltip("親のゲームオブジェクト")]
    public GameObject parentObject;
    
    [Tooltip("ドアに手をかける音")]
    public AudioSource doorKnockSound;

    [Tooltip("動物の鳴き声（AudioSource）")]
    public AudioSource animalCrySound;

    [Header("ドア設定")]
    [Tooltip("ドアの開閉を制御するドアマネージャー（任意）")]
    public DoorController doorController;

    [Header("タイミング設定（秒）")]
    [Tooltip("一階の明かりがつく時間")]
    public float firstFloorLightDuration = 2.0f;
    
    [Tooltip("二階の明かりがつく時間")]
    public float secondFloorLightDuration = 3.0f;
    
    [Tooltip("親がドアで待機する時間")]
    public float parentWaitAtDoorDuration = 2.0f;
    
    [Tooltip("親がドアを閉めてから消えるまでの時間")]
    public float parentDisappearDelay = 1.0f;

    [Header("親の表示設定")]
    [Tooltip("親のGameObjectをアクティブ/非アクティブで切り替えます")]
    public bool toggleParentActive = true;

    [Header("フェイント設定")]
    [Tooltip("二階の明かり後にフェイントが発生する確率（0-1）")]
    [Range(0f, 1f)]
    public float feintChance = 0.2f;

    [Tooltip("フェイント時、音が鳴ってから終了するまでの待機時間")]
    public float feintEndDelay = 1.0f;

    [Header("動物パターン設定")]
    [Tooltip("動物パターンが発生する確率（0-1）")]
    [Range(0f, 1f)]
    public float animalPatternChance = 0.2f;

    [Tooltip("最初の鳴き声の待機時間")]
    public float firstAnimalCryDelay = 0.0f;

    [Tooltip("二回目の鳴き声の待機時間")]
    public float secondAnimalCryDelay = 0.0f;

    [Header("手動発動")]
    [Tooltip("キー入力で手動発動を許可するか")]
    public bool enableManualKeyTrigger = false;

    [Tooltip("手動発動キー")]
    public KeyCode manualTriggerKey = KeyCode.T;

    [Header("デバッグ")]
    [Tooltip("予兆システムが実行中かどうか")]
    public bool isWarningActive = false;

    private Coroutine warningCoroutine;

    void Start()
    {
        ResetLights();
        if (doorController != null)
        {
            doorController.SetDoorOpen(false);
            doorController.SetParentVisible(false);
        }

        if (parentObject != null && toggleParentActive)
        {
            parentObject.SetActive(false);
        }
    }

    void Update()
    {
        if (!enableManualKeyTrigger)
        {
            return;
        }

        if (Input.GetKeyDown(manualTriggerKey))
        {
            StartWarningSequence();
        }
    }

    /// <summary>
    /// 予兆システムを開始します
    /// </summary>
    public void StartWarningSequence()
    {
        if (isWarningActive)
        {
            Debug.LogWarning("Warning sequence is already running!");
            return;
        }

        if (warningCoroutine != null)
        {
            StopCoroutine(warningCoroutine);
        }

        warningCoroutine = StartCoroutine(WarningSequenceCoroutine());
    }

    [ContextMenu("Trigger Warning Now")]
    public void TriggerWarningNow()
    {
        StartWarningSequence();
    }

    /// <summary>
    /// 予兆システムを強制停止します
    /// </summary>
    public void StopWarningSequence()
    {
        if (warningCoroutine != null)
        {
            StopCoroutine(warningCoroutine);
            warningCoroutine = null;
        }

        isWarningActive = false;
        ResetLights();
        if (doorController != null)
        {
            doorController.SetDoorOpen(false);
            doorController.SetParentVisible(false);
        }
        
        if (parentObject != null)
        {
            parentObject.SetActive(false);
        }
    }

    private IEnumerator WarningSequenceCoroutine()
    {
        isWarningActive = true;
        Debug.Log("Warning sequence started!");

        // 初期状態：すべてのライトを消す
        ResetLights();
        if (doorController != null)
        {
            doorController.SetDoorOpen(false);
            doorController.SetParentVisible(false);
        }
        if (parentObject != null)
        {
            parentObject.SetActive(false);
        }

        // ステップ1: 一階の明かりがつく
        Debug.Log("Step 1: First floor light ON");
        bool useAnimalPattern = animalCrySound != null && Random.value < animalPatternChance;
        if (useAnimalPattern)
        {
            if (firstAnimalCryDelay > 0f)
            {
                yield return new WaitForSeconds(firstAnimalCryDelay);
            }

            animalCrySound.Play();
        }
        if (firstFloorLight != null)
        {
            firstFloorLight.enabled = true;
        }
        yield return new WaitForSeconds(firstFloorLightDuration);

        // 一階の明かりを消す
        if (firstFloorLight != null)
        {
            firstFloorLight.enabled = false;
        }

        // ステップ2: 二階の明かりがつく（3つ）
        Debug.Log("Step 2: Second floor lights ON");
        bool usePointLightOnly = Camera.main != null && Camera.main.CompareTag("MainCamera");
        foreach (Light light in secondFloorLights)
        {
            if (light != null)
            {
                light.enabled = !usePointLightOnly;
            }
        }
        if (secondFloorPointLight != null)
        {
            secondFloorPointLight.enabled = usePointLightOnly;
        }
        if (useAnimalPattern && animalCrySound != null)
        {
            if (secondAnimalCryDelay > 0f)
            {
                yield return new WaitForSeconds(secondAnimalCryDelay);
            }

            animalCrySound.Play();
        }
        yield return new WaitForSeconds(secondFloorLightDuration);

        // 二階の明かりを消す
        foreach (Light light in secondFloorLights)
        {
            if (light != null)
            {
                light.enabled = false;
            }
        }
        if (secondFloorPointLight != null)
        {
            secondFloorPointLight.enabled = false;
        }

        // ステップ3: ドアに手をかける音
        Debug.Log("Step 3: Door knock sound");
        if (doorKnockSound != null)
        {
            doorKnockSound.Play();
        }

        if (Random.value < feintChance)
        {
            Debug.Log("Feint triggered: sound only, no door open.");
            yield return new WaitForSeconds(feintEndDelay);
            isWarningActive = false;
            Debug.Log("Warning sequence completed (feint).");
            yield break;
        }

        if (doorController != null)
        {
            doorController.SetDoorOpen(true);
            doorController.SetParentVisible(true);
        }

        // 親を表示
        if (parentObject != null && toggleParentActive)
        {
            parentObject.SetActive(true);
        }

        // ステップ4: 親がドアで待機
        Debug.Log("Step 4: Parent waiting at door");
        yield return new WaitForSeconds(parentWaitAtDoorDuration);

        // ステップ5: 親が部屋に入る
        Debug.Log("Step 5: Parent enters room");

        // この時点で検出システム（ParentDetection）が作動し、
        // プレイヤーが寝ていなければ捕まる

        // ステップ6: 親がドアを閉めてから消える
        Debug.Log("Step 6: Parent closes door and disappears");
        if (doorController != null)
        {
            doorController.SetDoorOpen(false);
        }
        yield return new WaitForSeconds(parentDisappearDelay);
        
        if (doorController != null)
        {
            doorController.SetParentVisible(false);
        }
        if (parentObject != null && toggleParentActive)
        {
            parentObject.SetActive(false);
        }

        isWarningActive = false;
        Debug.Log("Warning sequence completed!");
    }

    /// <summary>
    /// すべてのライトを消す
    /// </summary>
    private void ResetLights()
    {
        if (firstFloorLight != null)
        {
            firstFloorLight.enabled = false;
        }

        foreach (Light light in secondFloorLights)
        {
            if (light != null)
            {
                light.enabled = false;
            }
        }

        if (secondFloorPointLight != null)
        {
            secondFloorPointLight.enabled = false;
        }
    }

    void OnDestroy()
    {
        if (warningCoroutine != null)
        {
            StopCoroutine(warningCoroutine);
        }
    }
}

/*
=== Inspector Setup ===

1) このスクリプトをシーン内のGameObjectにアタッチします（例：GameManager）

2) ライト設定:
   - "First Floor Light": 一階のLightコンポーネントをドラッグ
   - "Second Floor Lights": 配列のSizeを3にして、二階の3つのLightをドラッグ

3) 親の出現設定:
   - "Parent Object": 親のGameObjectをドラッグ
   - "Door Knock Sound": ドアノック音のAudioSourceをドラッグ

4) タイミング設定（すべて秒単位）:
   - "First Floor Light Duration": 一階の明かりがつく時間（デフォルト: 2秒）
   - "Second Floor Light Duration": 二階の明かりがつく時間（デフォルト: 3秒）
   - "Parent Wait At Door Duration": 親がドアで待機する時間（デフォルト: 2秒）
   - "Parent Disappear Delay": 親がドアを閉めてから消えるまでの時間（デフォルト: 1秒）

5) 親の移動設定:
   - "Door Wait Position": 親がドアで待機する位置（空のTransformを作成してドアの前に配置）
   - "Room Enter Position": 親が部屋に入る位置（部屋の中に空のTransformを配置）

=== 使用方法 ===

予兆システムを開始するには、他のスクリプトから以下のように呼び出します:

```csharp
ParentWarningSystem warningSystem = GetComponent<ParentWarningSystem>();
warningSystem.StartWarningSequence();
```

例えば、ランダムな間隔で予兆を発生させる場合:

```csharp
void Start()
{
    InvokeRepeating("TriggerWarning", 10f, 30f); // 10秒後に開始、30秒ごとに繰り返し
}

void TriggerWarning()
{
    GetComponent<ParentWarningSystem>().StartWarningSequence();
}
```
*/