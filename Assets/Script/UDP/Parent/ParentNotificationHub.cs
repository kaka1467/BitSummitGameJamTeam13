using System;

/// <summary>
/// ParentNotificationHub：
/// 親機の「通知と保留」だけを持つ通常C#クラス（MonoBehaviourではない）。
///
/// 保持する状態（実行時の唯一の保持元）：
///  ・LOUD_ITEM：取得イベントと、購読者不在時の最大1件保留
///  ・GAME_PROGRESS：最新状態イベントと、購読者不在時の最新値保留
///
/// 境界（このクラスが触らないもの）：
///  ・UnityEngine／Coroutine／GameObject／UI／PlayerPrefs／ランキング保存を参照しない。
///  ・シーン名・シーン条件は扱わない（保留を維持するかどうかのシーン判断は呼び出し側が行う）。
///  ・パケット解析・送信・ACK・再送は呼び出し側が行う。
///
/// 購読者の有無：
///  ・呼び出し側は本クラスのイベントへ add/remove を委譲する（転送ハンドラを挟まない）。
///    そのため HasLoudItemSubscriber は「実際のゲーム側購読者の有無」を示す
///    （旧側の転送ハンドラを購読者と誤認しない）。
///
/// 順序の約束（従来どおり）：
///  ・LOUD_ITEM：保留A→新規Bの受信順。新規Bの受信でAを捨てない（保留は最大1件）。
///  ・GAME_PROGRESS：最新値優先（新値の後に古い保留を通知しない）。
///  ・通知の前に保留をクリアする（通知中の再入で二重に通知しない）。
/// </summary>
public sealed class ParentNotificationHub
{
    // ── LOUD_ITEM ─────────────────────────────────────────────────────────────
    /// <summary>大きな音アイテムの取得イベント（購読者不在時は1件だけ保留する）。</summary>
    public event Action LoudItemReceived;

    /// <summary>実際の購読者が居るか（add/remove を委譲されているため実購読者と一致する）。</summary>
    public bool HasLoudItemSubscriber => LoudItemReceived != null;

    /// <summary>購読者不在で保留中の LOUD_ITEM があるか。</summary>
    public bool HasPendingLoudItem { get; set; }

    // ── GAME_PROGRESS ─────────────────────────────────────────────────────────
    /// <summary>ゲーム進行率のイベント（購読者不在時は最新値だけ保留する）。</summary>
    public event Action<float> GameProgressReceived;

    /// <summary>実際の購読者が居るか。</summary>
    public bool HasGameProgressSubscriber => GameProgressReceived != null;

    /// <summary>購読者不在で保留中の GAME_PROGRESS があるか。</summary>
    public bool HasPendingGameProgress { get; set; }

    /// <summary>保留中の GAME_PROGRESS の最新値。</summary>
    public float PendingGameProgress { get; set; }

    // ── 通知（呼び出し側から呼ぶ）─────────────────────────────────────────────
    // （未使用の RaiseLoudItem は削除：HandleLoudItemReceived が受信時処理として役割を代替）

    // （未使用の RaiseGameProgress は削除：HandleGameProgressReceived が受信時処理として役割を代替）

    /// <summary>Updateからの保留消費の結果（呼び出し側が既存の診断ログを出し分けるために使う）。</summary>
    public enum PendingOutcome
    {
        /// <summary>保留が無かった。</summary>
        None = 0,
        /// <summary>非ゲームシーンのため保留を破棄した。</summary>
        ClearedOutsideGameplay = 1,
        /// <summary>ゲームシーンだが実購読者が居ないため保留を維持した。</summary>
        KeptForLater = 2,
        /// <summary>保留をクリアしてから1回通知した。</summary>
        Notified = 3,
    }

    /// <summary>
    /// Updateからの LOUD_ITEM 保留消費。消費条件（ゲームシーンか）は呼び出し直前に評価した判定関数で受ける。
    ///  ・非ゲームシーン：保留を破棄
    ///  ・ゲームシーン＋実購読者なし：保留を維持
    ///  ・実購読者あり：クリア→1回通知
    /// </summary>
    public PendingOutcome ConsumePendingLoudItem(Func<bool> isGameplayScene)
    {
        if (!HasPendingLoudItem) return PendingOutcome.None;

        if (!isGameplayScene())
        {
            HasPendingLoudItem = false;
            return PendingOutcome.ClearedOutsideGameplay;
        }

        if (LoudItemReceived == null) return PendingOutcome.KeptForLater;

        HasPendingLoudItem = false;
        LoudItemReceived?.Invoke();
        return PendingOutcome.Notified;
    }

    /// <summary>
    /// Updateからの GAME_PROGRESS 保留消費。最新値のみを1回通知する（値をローカルへ取得→クリア→通知）。
    /// </summary>
    public PendingOutcome ConsumePendingGameProgress(Func<bool> isGameplayScene)
    {
        if (!HasPendingGameProgress) return PendingOutcome.None;

        if (!isGameplayScene())
        {
            HasPendingGameProgress = false;
            PendingGameProgress = 0f;
            return PendingOutcome.ClearedOutsideGameplay;
        }

        if (GameProgressReceived == null) return PendingOutcome.KeptForLater;

        float pendingRate = PendingGameProgress;   // 値をローカルへ取得
        HasPendingGameProgress = false;
        PendingGameProgress = 0f;
        GameProgressReceived?.Invoke(pendingRate);
        return PendingOutcome.Notified;
    }

    /// <summary>LOUD_ITEM 受信時の結果（呼び出し側が既存の診断ログを出し分けるために使う）。</summary>
    public enum LoudItemOutcome
    {
        /// <summary>新規Bを通知した。</summary>
        NotifiedNew = 0,

        /// <summary>実購読者が居ないため1件保留した。</summary>
        PendingWithoutSubscriber = 1,

        /// <summary>保留Aを通知した後、新規Bも通知した。</summary>
        PendingNotifiedThenNewNotified = 2,

        /// <summary>保留Aを通知した後、シーンが変わったため新規Bを破棄した。</summary>
        PendingNotifiedThenNewDiscarded = 3,

        /// <summary>保留Aを通知した後、購読者が居なくなったため新規Bを1件保留した。</summary>
        PendingNotifiedThenNewPending = 4,
    }

    /// <summary>
    /// LOUD_ITEM の受信時処理（最初のゲームシーン判定は呼び出し側が済ませてから呼ぶ）。
    ///  2) 実購読者なし → 最大1件保留
    ///  3) 実購読者あり＋保留A → 保留をクリアしてからAを通知
    ///  4) A通知後に isGameplayScene を再評価（メインスレッドで評価される判定関数。事前計算boolで代用しない）
    ///  5) 新規Bを通知／保留／破棄（Bを理由にAを捨てない・B→Aの逆順にしない）
    /// </summary>
    public LoudItemOutcome HandleLoudItemReceived(Func<bool> isGameplayScene)
    {
        if (LoudItemReceived == null)
        {
            HasPendingLoudItem = true;
            return LoudItemOutcome.PendingWithoutSubscriber;
        }

        bool pendingNotified = false;
        if (HasPendingLoudItem)
        {
            // クリアしてから通知する（Update後段の保留処理で二重通知しない）。
            HasPendingLoudItem = false;
            LoudItemReceived?.Invoke();
            pendingNotified = true;

            // A の通知中にシーン遷移・購読解除が起きた可能性があるため、新規Bを扱う前に再確認する。
            if (!isGameplayScene())
                return LoudItemOutcome.PendingNotifiedThenNewDiscarded;
        }

        if (LoudItemReceived != null)
        {
            LoudItemReceived?.Invoke();
            return pendingNotified ? LoudItemOutcome.PendingNotifiedThenNewNotified : LoudItemOutcome.NotifiedNew;
        }

        HasPendingLoudItem = true;
        return pendingNotified ? LoudItemOutcome.PendingNotifiedThenNewPending : LoudItemOutcome.PendingWithoutSubscriber;
    }

    // （未使用の NotifyLoudItemNow は削除：HandleLoudItemReceived／ConsumePendingLoudItem が役割を代替）

    // （未使用の NotifyGameProgressNow は削除：HandleGameProgressReceived／ConsumePendingGameProgress が役割を代替）

    /// <summary>
    /// GAME_PROGRESS の受信時処理（ゲームシーン判定は呼び出し側が済ませてから呼ぶ）。
    ///  ・実購読者あり：古い保留を先にクリアしてから新値を通知する（新値の後に古い保留を通知しない）。
    ///  ・実購読者なし：最新値を1件だけ保留する（履歴は蓄積しない）。
    ///  ・保留状態は本メソッド内で完結して変更する（旧側と二重に処理しない）。
    /// </summary>
    public void HandleGameProgressReceived(float progressRate)
    {
        if (GameProgressReceived != null)
        {
            // 新値を即通知する「前に」古い保留を必ずクリアする（Update後段の保留消費が古い値を通知しないように）。
            HasPendingGameProgress = false;
            PendingGameProgress = 0f;

            GameProgressReceived.Invoke(progressRate);
            return;
        }

        PendingGameProgress = progressRate;
        HasPendingGameProgress = true;
    }

    // （未使用の FlushPendingLoudItem は削除：ConsumePendingLoudItem が役割を代替）

    // （未使用の FlushPendingGameProgress は削除：ConsumePendingGameProgress が役割を代替）

    /// <summary>保留をすべて破棄する（シーン変更・新セッションで持ち越さない）。</summary>
    public void ClearPending()
    {
        HasPendingLoudItem = false;
        HasPendingGameProgress = false;
        PendingGameProgress = 0f;
    }
}
