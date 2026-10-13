using System;

/// <summary>
/// ChildConnectionLink：
/// 子機の「接続管理」だけを持つ通常C#クラス（MonoBehaviourではない）。
///
/// 役割：
///  ・接続状態（Disconnected／Connecting／Connected）と同値ガード
///  ・接続先IPの保持
///  ・接続開始／キャンセルの状態管理
///  ・DISCOVERY_ACCEPTによる接続確定
///  ・最終受信時刻（時刻は呼び出し側が渡す）
///  ・タイムアウト判定
///  ・現在の復帰条件の判断材料（状態と接続先の保持。復帰の手順は呼び出し側が行う）
///  ・接続状態のセッションリセット
///
/// 境界（このクラスが触らないもの）：
///  ・UnityEngine／SceneManager／GameObject／PlayerPrefs／UI／ランキング／ゲーム結果を参照しない。
///  ・UnityのTimeを読まない（時刻は呼び出し側が引数で渡す）。
///  ・ソケットは UdpChannel が所有する（本クラスは生成・破棄・送信を行わない）。
///  ・Discovery／Heartbeat のCoroutineと送信、パケット解析、接続確定時のランキング同期、
///    START_GAME／プレイID／gameSceneLoaded、シーン遷移、ゲームイベント、捕獲待機などは呼び出し側が行う。
///
/// 状態の保持元：
///  ・実行時の接続状態はこのクラスが保持する。ChildUdpReceiver の currentState／targetIP は
///    互換のためのミラーであり、このクラスの StateChanged 経由で同時に更新される。
/// </summary>
public sealed class ChildConnectionLink
{
    /// <summary>接続状態（ChildUdpReceiver.ConnectionState と1対1に対応する）。</summary>
    public enum LinkState
    {
        Disconnected = 0,
        Connecting = 1,
        Connected = 2,
    }

    /// <summary>セッション初期化時の既定接続先（従来の targetIP の初期値と同じ）。</summary>
    public const string DefaultTargetIP = "127.0.0.1";

    private LinkState _state = LinkState.Disconnected;
    private string _targetIP = DefaultTargetIP;
    private double _lastReceiveTimeSeconds;

    /// <summary>現在の接続状態。</summary>
    public LinkState State => _state;

    /// <summary>確定している接続先IP。</summary>
    public string TargetIP => _targetIP;

    /// <summary>接続中か。</summary>
    public bool IsConnected => _state == LinkState.Connected;

    /// <summary>接続操作中か（Discovery送信の開始・停止条件に使う）。</summary>
    public bool IsConnecting => _state == LinkState.Connecting;

    /// <summary>最後に受信した時刻（呼び出し側が渡した値）。</summary>
    public double LastReceiveTimeSeconds => _lastReceiveTimeSeconds;

    /// <summary>
    /// 状態が変化したときだけ発火する（同じ状態の再設定では発火しない＝二重通知しない）。
    /// 既存の ChildUdpReceiver.ConnectionStateChanged へ1回だけ転送するために使う。
    /// </summary>
    public event Action<LinkState> StateChanged;

    /// <summary>
    /// Inspectorから読み込んだ接続先を、このリンクの初期値として渡す（ミラー更新前に呼ぶ）。
    ///  ・空文字なら何もしない（リンクの既定値でInspector値を上書きしない）。
    ///  ・状態は変更しない（イベントも出さない）。
    /// </summary>
    public void SeedTargetIP(string initialTargetIP)
    {
        if (!string.IsNullOrEmpty(initialTargetIP))
            _targetIP = initialTargetIP;
    }

    /// <summary>
    /// Inspectorから読み込んだ接続状態を、このリンクの初期値として渡す（ミラー更新前に呼ぶ）。
    ///  ・状態変更として扱わない（イベントも出さない）。起動時の currentState の扱いを従来のまま保つために使う。
    /// </summary>
    public void SeedState(LinkState initial)
    {
        _state = initial;
    }

    /// <summary>接続先IPだけを更新する（状態は変更しない・イベントも出さない）。</summary>
    public void SetTargetIP(string peerIP)
    {
        if (!string.IsNullOrEmpty(peerIP))
            _targetIP = peerIP;
    }

    /// <summary>接続開始（Connecting へ。既に同じ状態なら何もしない）。</summary>
    public void BeginConnecting() => SetState(LinkState.Connecting);

    /// <summary>接続キャンセル／切断（Disconnected へ。既に同じ状態なら何もしない）。</summary>
    public void Disconnect() => SetState(LinkState.Disconnected);

    /// <summary>DISCOVERY_ACCEPT による接続確定（接続先IPを確定して Connected へ）。</summary>
    public void AcceptPeer(string parentIP)
    {
        SetTargetIP(parentIP);
        SetState(LinkState.Connected);
    }

    /// <summary>受信時刻の記録（生存確認）。状態は変更しない。</summary>
    public void NoteReceive(double nowSeconds) => _lastReceiveTimeSeconds = nowSeconds;

    /// <summary>
    /// タイムアウト判定：接続中で、最後の受信から timeoutSeconds を超えているか。
    ///  ・子機にはシーン別のタイムアウト例外が無いため、呼び出し側は SceneManager に依存せずそのまま使える。
    /// </summary>
    public bool IsTimedOut(double nowSeconds, double timeoutSeconds)
        => _state == LinkState.Connected && (nowSeconds - _lastReceiveTimeSeconds) > timeoutSeconds;

    /// <summary>
    /// セッション初期化。
    ///  ・従来の順序（SetConnectionState → lastReceiveTime → targetIP）を保つ。
    /// </summary>
    public void ResetForNewSession()
    {
        SetState(LinkState.Disconnected);
        _lastReceiveTimeSeconds = 0d;
        _targetIP = DefaultTargetIP;
    }

    /// <summary>
    /// 状態を設定する（同じ状態ならイベントを出さない）。
    ///  notifyStateChanged=false の場合は状態だけ変更する（通知は NotifyStateIfChanged の責務）。
    /// </summary>
    public void SetState(LinkState next, bool notifyStateChanged = true)
    {
        if (_state == next) return;

        _state = next;

        if (notifyStateChanged)
            StateChanged?.Invoke(next);
    }
}
