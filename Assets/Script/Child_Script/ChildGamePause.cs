using System;
using UnityEngine;

/// <summary>
/// 子機ゲーム全体の一時停止（寝たふり通知による中断・再開）。
///
/// 止めるもの：移動・背景・出現物・接触（物理）・取得・得点・フィーバー時間など、Time.timeScale に従うもの全部。
/// 実時間（unscaled）で動くもの（出現タイマー・QTEの制限時間・本編経過時間）は、それぞれ
///   ・IsPaused を見て止める
///   ・再開時に Resumed（停止していた秒数）を受けて時刻をずらす
/// ことで、停止中の分が再開後に「まとめて」処理されないようにする。
///
/// timeScale の所有者は他にもある（QTE・結果確定）ため、再開時は「QTE中または結果確定後なら 0、それ以外は 1」に戻す。
/// </summary>
public static class ChildGamePause
{
    private static float _pausedAtRealtime;
    private static float _completedPausedSeconds;

    public static bool IsPaused { get; private set; }

    /// <summary>停止状態が変わったとき（true=停止開始、false=再開）。</summary>
    public static event Action<bool> PauseChanged;

    /// <summary>再開した直後に、停止していた実時間（秒）を渡して呼ばれる。</summary>
    public static event Action<float> Resumed;

    /// <summary>これまでに停止していた実時間の合計（現在停止中ならその分も含む）。</summary>
    public static float TotalPausedSeconds =>
        _completedPausedSeconds + (IsPaused ? Mathf.Max(0f, Time.unscaledTime - _pausedAtRealtime) : 0f);

    public static void SetPaused(bool paused)
    {
        if (IsPaused == paused) return;

        IsPaused = paused;

        if (paused)
        {
            _pausedAtRealtime = Time.unscaledTime;
            Time.timeScale = 0f;
            PauseChanged?.Invoke(true);
            return;
        }

        float duration = Mathf.Max(0f, Time.unscaledTime - _pausedAtRealtime);
        _completedPausedSeconds += duration;

        // 他の停止要因（QTE・結果確定）が続いているなら 0 のまま、無ければ通常速度へ戻す。
        QTEManager qte = QTEManager.Instance;
        GameManager gm = GameManager.instance;
        bool otherStop = (qte != null && qte.IsQteActive) || (gm != null && gm.IsGameOver);
        Time.timeScale = otherStop ? 0f : 1f;

        PauseChanged?.Invoke(false);
        Resumed?.Invoke(duration);
    }
}
