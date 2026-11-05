using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// SettingsVolumeRow:
/// 設定パネルの BGM / SE 行の「選択表示＋左右での音量変更」を担当する Selectable。
///
/// 役割分担（二重処理を避けるため）:
///  ・選択されるのは「行」＝このコンポーネントを持つオブジェクト（Scene のラベル等）。
///  ・Slider 自体は選択対象にしない（＝Slider 標準の左右入力は発火しない）。
///    マウスのクリック・ドラッグは Slider がそのまま処理する。
///  ・左右 → このクラスが音量を stepPercent だけ増減（0〜100% にクランプ）。
///  ・上下 → base.OnMove に任せ、Unity 標準の Navigation で次/前の項目へ移動する。
///
/// 音量の保存・適用は Slider.onValueChanged → ParentVolumeSettingsPanel → ParentAudioSettings の
/// 既存経路を通る（ここでは保存しない）。
/// </summary>
[RequireComponent(typeof(RectTransform))]
public class SettingsVolumeRow : Selectable
{
    [Tooltip("左右入力で操作するスライダー。実行時に設定パネルから割り当てる。")]
    public Slider targetSlider;

    [Tooltip("1回の左右入力で変化させる割合（0.05 = 5%）。設定パネル側の値が実行時に設定される。")]
    [Range(0.01f, 0.5f)] public float stepPercent = 0.05f;

    /// <summary>選択中の表示（矢印追従など）に使う RectTransform。</summary>
    public RectTransform RowRect => (RectTransform)transform;

    public override void OnMove(AxisEventData eventData)
    {
        if (eventData == null) return;

        if (eventData.moveDir == MoveDirection.Left || eventData.moveDir == MoveDirection.Right)
        {
            if (!IsActive() || !IsInteractable()) return;

            if (targetSlider != null)
            {
                float delta = (eventData.moveDir == MoveDirection.Right ? stepPercent : -stepPercent);
                // 0〜100% にクランプ。Slider.value への代入で onValueChanged が発火し、既存の保存・適用経路が動く。
                targetSlider.value = Mathf.Clamp01(targetSlider.value + delta);
            }
            return;   // 左右では選択を動かさない（別項目へ飛ばない）
        }

        base.OnMove(eventData);   // 上下は Unity 標準の Navigation に任せる
    }
}
