using System;

/// <summary>
/// ParentPlaySession：
/// 親機の「プレイセッション管理」だけを持つ通常C#クラス（MonoBehaviourではない）。
///
/// 保持する状態（実行時の唯一の保持元）：
///  ・現在プレイID（PlaySessionId）
///  ・開始済み状態（GameStarted）
///  ・子機ロード完了の記録（ChildLoadingComplete）
///  ・結果処理済み状態（ResultProcessed）
///  ・捕獲確定プレイID（CaptureConfirmedPlayId）＝捕獲優先の判断材料
///
/// 境界（このクラスが触らないもの）：
///  ・UnityEngine／Coroutine／GameObject／UI／PlayerPrefs／ランキング保存を参照しない。
///  ・接続状態（Connected 等）やシーン条件は判断に必要な値だけ引数で受け取る
///    （ParentConnectionLink や SceneManager を直接参照しない）。
///  ・パケット解析・送信・シーンロード・イベント通知・ランキング同期は呼び出し側が行う。
///
/// 互換：
///  ・ParentUdpSender の _currentPlaySessionId／_gameStarted／ChildLoadingComplete／
///    _resultProcessed／_captureConfirmedPlayId は、このクラスを読み書きする互換ミラー
///    （プロパティ経由）として残す。値の保持元はこのクラス。
/// </summary>
public sealed class ParentPlaySession
{
    /// <summary>START_GAME の受理判断の結果（拒否理由・同一プレイ再送の区別を呼び出し側へ返す）。</summary>
    public enum StartGameDecision
    {
        /// <summary>受理（新しいプレイ）。受信記録の初期化を行う。</summary>
        AcceptNewPlay = 0,

        /// <summary>受理（同じプレイIDの再送）。受信記録は初期化しない（受理済みの記録を消さない）。</summary>
        AcceptRepeatOfAcceptedPlay = 1,

        /// <summary>拒否：既に開始済み（_gameStarted が true）。</summary>
        RejectAlreadyStarted = 2,

        /// <summary>拒否：接続が Connected ではない（呼び出し側から渡された接続状態で判断）。</summary>
        RejectNotConnected = 3,
    }

    // ── 状態（保持元）─────────────────────────────────────────────────────────
    private string _playSessionId = "";
    private bool _gameStarted;
    private bool _childLoadingComplete;
    private bool _resultProcessed;
    private string _captureConfirmedPlayId = "";

    /// <summary>現在プレイID。</summary>
    public string PlaySessionId
    {
        get => _playSessionId;
        set => _playSessionId = value ?? "";
    }

    /// <summary>このプレイの開始を受理済みか。</summary>
    public bool GameStarted
    {
        get => _gameStarted;
        set => _gameStarted = value;
    }

    /// <summary>子機の LOADING_COMPLETE を受信済みか（現在プレイの受信記録）。</summary>
    public bool ChildLoadingComplete
    {
        get => _childLoadingComplete;
        set => _childLoadingComplete = value;
    }

    /// <summary>結果処理済みか（GAME_OVER wins race）。</summary>
    public bool ResultProcessed
    {
        get => _resultProcessed;
        set => _resultProcessed = value;
    }

    /// <summary>捕獲（CAUGHT）が確定したプレイID（TIME_UPを最終結果として受理しない）。</summary>
    public string CaptureConfirmedPlayId
    {
        get => _captureConfirmedPlayId;
        set => _captureConfirmedPlayId = value ?? "";
    }

    /// <summary>
    /// 既存のプレイID形式（Guid:N）を維持して新しいIDを発行する。
    ///  （ID形式・採用条件は変えない。新しいメッセージは追加しない）
    /// </summary>
    public static string NewPlaySessionId() => Guid.NewGuid().ToString("N");

    // ── 判断・更新 ────────────────────────────────────────────────────────────

    /// <summary>
    /// START_GAME の受理判断（接続状態は呼び出し側から渡す。シーン条件も呼び出し側が判断する）。
    /// 受理した場合は状態を更新する（開始済み・プレイIDの採用・捕獲確定IDのクリア）。
    ///  ・incomingPlayId が空（旧フォーマット）の場合は親機側で新規発行する（従来どおり）。
    ///  ・同じプレイIDの再送は AcceptRepeatOfAcceptedPlay を返す（受信記録は初期化しない）。
    /// </summary>
    public StartGameDecision AcceptStartGame(string incomingPlayId, bool isConnected)
    {
        if (_gameStarted) return StartGameDecision.RejectAlreadyStarted;
        if (!isConnected) return StartGameDecision.RejectNotConnected;

        _gameStarted = true;

        // 子機が発行したプレイ識別子を採用する（旧フォーマットで未付与なら親機側で発行）。
        bool isRepeatOfAcceptedPlay = !string.IsNullOrEmpty(incomingPlayId) && incomingPlayId == _playSessionId;

        _playSessionId = string.IsNullOrEmpty(incomingPlayId) ? NewPlaySessionId() : incomingPlayId;
        _captureConfirmedPlayId = "";

        return isRepeatOfAcceptedPlay
            ? StartGameDecision.AcceptRepeatOfAcceptedPlay
            : StartGameDecision.AcceptNewPlay;
    }

    /// <summary>
    /// 子機のロード完了記録を更新し、更新前の値を返す（false→true の判定は呼び出し側が行う）。
    /// </summary>
    public bool SetChildLoadingComplete(bool value)
    {
        bool previous = _childLoadingComplete;
        _childLoadingComplete = value;
        return previous;
    }

    /// <summary>結果処理済み状態を初期化する（ゲームシーン読込・タイトル復帰）。</summary>
    public void ResetResultProcessed() => _resultProcessed = false;

    /// <summary>捕獲確定プレイIDを現在プレイのIDで確定する（空IDのときは何もしない）。</summary>
    public void ConfirmCaptureForCurrentPlay()
    {
        if (!string.IsNullOrEmpty(_playSessionId))
            _captureConfirmedPlayId = _playSessionId;
    }

    /// <summary>捕獲確定プレイIDをクリアする。</summary>
    public void ClearCaptureConfirmed() => _captureConfirmedPlayId = "";

    /// <summary>
    /// タイトル復帰などのセッションリセット。
    ///  ・既存コードと同じ範囲だけを初期化する：プレイID／開始済み／結果処理済み／ロード完了記録。
    ///  ・捕獲確定プレイIDは、従来の ResetForNewSession でも消していないため、ここでも消さない
    ///    （新しいプレイの開始受理（AcceptStartGame）でクリアされる既存挙動を維持する）。
    /// </summary>
    public void ResetForNewSession()
    {
        _gameStarted = false;
        _resultProcessed = false;
        _childLoadingComplete = false;
        _playSessionId = "";
    }
}
