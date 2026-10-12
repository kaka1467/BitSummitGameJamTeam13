using System;

/// <summary>
/// ParentConnectionLink：
/// 親機の「接続管理」だけを持つ通常C#クラス（MonoBehaviourではない）。
///
/// 役割：
///  ・状態遷移（Disconnected／Connecting／Connected）
///  ・Discoveryによる接続先確定の判断（接続確定と接続先IPの保持）
///  ・PING受信時の接続復帰
///  ・最終受信時刻（時刻は呼び出し側が渡す）
///  ・タイムアウト判定
///  ・接続開始／キャンセルの状態管理
///
/// 境界（このクラスが触らないもの）：
///  ・ソケットと受信スレッドは UdpChannel が所有する（本クラスはソケットを生成・破棄しない）。
///  ・UnityEngine／SceneManager／GameObject／PlayerPrefs／UI／ランキング／ゲーム結果を参照しない。
///  ・UnityのTimeを読まない（時刻は呼び出し側が引数で渡す）。
///  ・シーン名を埋め込まない（シーン別のタイムアウト例外は呼び出し側が評価して渡す）。
///  ・接続後のポリシー（RankSyncPeerGateによるランキング検証・チャレンジ開始など）は扱わない
///    （呼び出し側が接続確定の結果を受けて実行する）。
///
/// 状態の保持元：
///  ・実行時の接続状態はこのクラスが保持する。ParentUdpSender の currentState／targetIP は
///    互換のためのミラーであり、このクラスの StateChanged 経由で同時に更新される。
/// </summary>
public sealed class ParentConnectionLink
{
    /// <summary>接続状態（ParentUdpSender.ConnectionState と1対1に対応する）。</summary>
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

    /// <summary>接続操作中か。</summary>
    public bool IsConnecting => _state == LinkState.Connecting;

    /// <summary>最後に受信した時刻（呼び出し側が渡した値）。</summary>
    public double LastReceiveTimeSeconds => _lastReceiveTimeSeconds;

    /// <summary>
    /// 状態が変化したときだけ発火する（同じ状態の再設定では発火しない＝二重通知しない）。
    /// 既存の ParentUdpSender.ConnectionStateChanged へ1回だけ転送するために使う。
    /// </summary>
    public event Action<LinkState> StateChanged;

    /// <summary>
    /// Inspectorから読み込んだ接続先を、このリンクの初期値として渡す（Awakeで互換フィールドへミラーする前に呼ぶ）。
    ///  ・空文字なら何もしない（リンクの既定値でInspector値を上書きしない）。
    ///  ・状態は変更しない（Transitions もイベントも出さない）。
    /// </summary>
    public void SeedTargetIP(string initialTargetIP)
    {
        if (!string.IsNullOrEmpty(initialTargetIP))
            _targetIP = initialTargetIP;
    }

    /// <summary>接続開始（Connecting へ。既に同じ状態なら何もしない）。</summary>
    public void BeginConnecting() => SetState(LinkState.Connecting);

    /// <summary>接続キャンセル／切断（Disconnected へ。既に同じ状態なら何もしない）。</summary>
    public void Disconnect() => SetState(LinkState.Disconnected);

    /// <summary>
    /// 接続先IPだけを更新する（状態は変更しない・イベントも出さない）。
    ///  ・Discovery受理で接続先が変わったときに、同値（Connected→Connected）でも互換ミラーを更新するために使う。
    /// </summary>
    public void SetTargetIP(string peerIP)
    {
        if (!string.IsNullOrEmpty(peerIP))
            _targetIP = peerIP;
    }

    /// <summary>
    /// Discovery受理による接続確定（接続先IPを確定して Connected へ）。
    ///  notifyStateChanged=false は、状態と通知を分けて呼びたい場合の互換用（通知は SetState 側の責務）。
    /// </summary>
    public void AcceptPeer(string peerIP, bool notifyStateChanged = true)
    {
        SetTargetIP(peerIP);
        SetState(LinkState.Connected, notifyStateChanged);
    }

    /// <summary>受信時刻の記録（生存確認）。状態は変更しない。</summary>
    public void NoteReceive(double nowSeconds) => _lastReceiveTimeSeconds = nowSeconds;

    /// <summary>
    /// タイムアウト判定：接続中で、最後の受信から timeoutSeconds を超えているか。
    ///  ・シーン別の例外（ロード中はタイムアウト扱いにしない等）は呼び出し側が判断して渡す。
    /// </summary>
    public bool IsTimedOut(double nowSeconds, double timeoutSeconds)
        => _state == LinkState.Connected && (nowSeconds - _lastReceiveTimeSeconds) > timeoutSeconds;

    /// <summary>
    /// タイトル復帰などのセッション初期化。
    ///  ・従来の順序（SetConnectionState → targetIP → 最終受信時刻）を保つ。
    /// </summary>
    public void ResetForNewSession()
    {
        SetState(LinkState.Disconnected);
        _targetIP = DefaultTargetIP;
        _lastReceiveTimeSeconds = 0d;
    }

    /// <summary>
    /// 状態を設定する（同じ状態ならイベントを出さない）。
    ///  notifyStateChanged=false の場合は状態だけ変更する（従来の直接代入と同じ）。
    /// </summary>
    public void SetState(LinkState next, bool notifyStateChanged = true)
    {
        if (_state == next) return;

        _state = next;

        if (notifyStateChanged)
            StateChanged?.Invoke(next);
    }
}
