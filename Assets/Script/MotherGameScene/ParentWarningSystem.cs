using System.Collections;
using UnityEngine;

/// <summary>
/// お母さんの予兆（廊下の明かり、ノック音）から、実際に部屋に入ってくるまでの一連の流れを制御するスクリプトです。
/// </summary>
public class ParentWarningSystem : MonoBehaviour
{
    [Header("演出オブジェクトの参照")]
    [Tooltip("一階の明かり（廊下の明かり等）を表現するオブジェクト")]
    public GameObject lightFirstFloor;
    [Tooltip("二階の明かり（部屋の前の明かり等）を表現するオブジェクト")]
    public GameObject lightSecondFloor;
    [Tooltip("ドアの前に現れるお母さん自身の3Dモデル・画像オブジェクト")]
    public GameObject motherModel;
    [Tooltip("ドアをガチャガチャさせる、または回転させる制御スクリプト（あればアサイン）")]
    public DoorController doorController;

    [Header("演出時間の設定（秒）")]
    [Tooltip("一階の明かりがついている時間")]
    public float firstFloorLightDuration = 3.0f;
    [Tooltip("二階の明かりがついている時間")]
    public float secondFloorLightDuration = 3.0f;
    [Tooltip("お母さんが部屋に居座って凝視している時間")]
    public float motherStayDuration = 4.0f;

    [Header("現在のステータス（デバッグ用）")]
    [Tooltip("現在、お母さんの襲撃シーケンスが実行中かどうか")]
    public bool isWarningActive = false;

    void Start()
    {
        // 最初はすべての演出用オブジェクトを非アクティブ（非表示）にしておく
        if (lightFirstFloor != null) lightFirstFloor.SetActive(false);
        if (lightSecondFloor != null) lightSecondFloor.SetActive(false);
        if (motherModel != null) motherModel.SetActive(false);
        if (doorController != null) doorController.SetParentVisible(false);
    }

    /// <summary>
    /// お母さんが出現する一連のシーケンスを開始します（スケジューラーやデバッグキーから呼ばれます）
    /// </summary>
    public void StartWarningSequence()
    {
        if (!isWarningActive)
        {
            StartCoroutine(WarningSequenceCoroutine());
        }
    }

    /// <summary>
    /// お母さんのシーケンスを途中で強制停止します（ゲームオーバー時などの安全弁）
    /// </summary>
    public void StopWarningSequence()
    {
        StopAllCoroutines();
        isWarningActive = false;

        // すべて非表示に戻す
        if (lightFirstFloor != null) lightFirstFloor.SetActive(false);
        if (lightSecondFloor != null) lightSecondFloor.SetActive(false);
        if (motherModel != null) motherModel.SetActive(false);
        if (doorController != null)
        {
            doorController.SetParentVisible(false);
            doorController.SetDoorOpen(false);
        }
    }

    private IEnumerator WarningSequenceCoroutine()
    {
        isWarningActive = true;
        Debug.Log("【予兆システム】お母さんが動き出しました...（1階の明かりON）");

        // 1. 一階の明かりがつく（予兆1）
        if (lightFirstFloor != null) lightFirstFloor.SetActive(true);
        yield return new WaitForSeconds(firstFloorLightDuration);
        if (lightFirstFloor != null) lightFirstFloor.SetActive(false);

        // 2. 二階の明かりがつく（予兆2：部屋に近づいてきた！）
        Debug.Log("【予兆システム】お母さんが階段を上がってきました...（2階の明かりON）");
        if (lightSecondFloor != null) lightSecondFloor.SetActive(true);
        yield return new WaitForSeconds(secondFloorLightDuration);
        if (lightSecondFloor != null) lightSecondFloor.SetActive(false);

        // 3. ドアが開き、お母さんが出現！（襲撃本番）
        Debug.Log("★【予兆システム】お母さんがドアを開けて部屋を覗き込みました！！");
        if (doorController != null)
        {
            doorController.SetDoorOpen(true);
            doorController.SetParentVisible(true);
        }
        if (motherModel != null) motherModel.SetActive(true);

        // お母さんが部屋を凝視している間、待機（この間に寝ていないとDetection側でゲージが上がります）
        yield return new WaitForSeconds(motherStayDuration);

        // 4. お母さんが帰っていく（撤退）
        Debug.Log("【予兆システム】お母さんが去っていきました。（ドアを閉めます）");
        if (motherModel != null) motherModel.SetActive(false);
        if (doorController != null)
        {
            doorController.SetParentVisible(false);
            doorController.SetDoorOpen(false);
        }

        isWarningActive = false;
    }
}