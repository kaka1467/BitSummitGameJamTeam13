using System;

/// <summary>
/// ChildPlaySession：
/// 子機の「プレイセッション管理」だけを持つ通常C#クラス（MonoBehaviourではない）。
///
/// 保持する状態（実行時の唯一の保持元）：
///  ・現在プレイID（PlaySessionId）
///  ・開始・ロード状態（GameSceneLoaded＝ゲームシーンへ遷移済みか）
///  ・CAUGHTの受理済み状態（CaughtHandled＝このプレイで捕獲を1回だけ受理したか）
///
/// 境界（このクラスが触らないもの）：
///  ・UnityEngine／Coroutine／GameObject／UI／PlayerPrefs／ランキング保存を参照しない。
///  ・シーン条件・接続状態は判断に必要な値だけ引数で受け取る（SceneManager や ChildConnectionLink を参照しない）。
///  ・パケット解析・送信・シーンロード・イベント通知は呼び出し側が行う。
///
/// 捕獲演出待機（None／Waiting／Completed・完了通知の先着・ローカルタイムアウト）の所有：
///  ・**通知担当（ChildUdpReceiver）が保持する**。理由：
///    待機状態は「演出完了通知（SEQUENCE_DONE / ACK / 重複・先着）」の受理結果そのものであり、
///    通知の解釈・購読者（ChildSleepLockApplier／GameManager）への通知と同じ場所で管理しないと
///    二重管理になるため。プレイID（本クラス）は判定材料として参照するだけにする。
///
/// 互換：
///  ・ChildUdpReceiver の _playSessionId／gameSceneLoaded／caughtHandled は、
///    このクラスを読み書きする互換ミラー（プロパティ経由）として残す。値の保持元はこのクラス。
/// </summary>
public sealed class ChildPlaySession
{
    /// <summary>START_GAME の新プレイ判定（空IDの既存例外を含む）。</summary>
    public enum StartGameBoundary
    {
        /// <summary>新しいプレイ（プレイIDの採用・前プレイの受理済み状態のリセットを行う）。</summary>
        NewPlay = 0,

        /// <summary>同じプレイの再送（受理済み状態を消さない）。</summary>
        SamePlayRepeat = 1,
    }

    private string _playSessionId = "";
    private bool _gameSceneLoaded;
    private bool _caughtHandled;

    /// <summary>現在プレイID。</summary>
    public string PlaySessionId
    {
        get => _playSessionId;
        set => _playSessionId = value ?? "";
    }

    /// <summary>ゲームシーンへ遷移済みか（開始・ロード状態）。</summary>
    public bool GameSceneLoaded
    {
        get => _gameSceneLoaded;
        set => _gameSceneLoaded = value;
    }

    /// <summary>このプレイで捕獲（CAUGHT）を受理済みか。</summary>
    public bool CaughtHandled
    {
        get => _caughtHandled;
        set => _caughtHandled = value;
    }

    /// <summary>
    /// START_GAME の新プレイ／同一プレイ再送の境界判定と、プレイIDの採用。
    ///  ・incomingPlayId が非空：以前のIDと違えば NewPlay（採用する）。同じなら SamePlayRepeat（採用もリセットもしない）。
    ///  ・incomingPlayId が空（旧フォーマット＝空IDで直接Playする既存例外）：ゲームシーン未遷移なら NewPlay。
    ///  NewPlay のときだけ、前プレイの受理済み状態（CaughtHandled）をリセットする。
    /// </summary>
    public StartGameBoundary EvaluateStartGame(string incomingPlayId, out string adoptedPlayId)
    {
        adoptedPlayId = _playSessionId;

        if (!string.IsNullOrEmpty(incomingPlayId))
        {
            bool isNewPlay = incomingPlayId != _playSessionId;

            if (isNewPlay)
            {
                _playSessionId = incomingPlayId;
                adoptedPlayId = _playSessionId;
                ResetAcceptedStateForNewPlay();
                return StartGameBoundary.NewPlay;
            }

            return StartGameBoundary.SamePlayRepeat;
        }

        // 空IDの既存例外：ゲームシーンへ未遷移なら新プレイとして扱う（IDは親機側の発行に委ねる）。
        if (!_gameSceneLoaded)
        {
            ResetAcceptedStateForNewPlay();
            return StartGameBoundary.NewPlay;
        }

        return StartGameBoundary.SamePlayRepeat;
    }

    /// <summary>新プレイ開始時に、前プレイの受理済み状態をリセットする（プレイIDは呼び出し側が採用する）。</summary>
    public void ResetAcceptedStateForNewPlay() => _caughtHandled = false;

    /// <summary>
    /// タイトル復帰などのセッションリセット（プレイID・ロード状態・捕獲受理済み）。
    ///  ・捕獲演出待機の状態は通知担当（ChildUdpReceiver）が別途リセットする。
    /// </summary>
    public void ResetForNewSession()
    {
        _playSessionId = "";
        _gameSceneLoaded = false;
        _caughtHandled = false;
    }
}
