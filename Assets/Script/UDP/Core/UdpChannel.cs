using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

/// <summary>
/// 受信経路の識別子。
/// 同じ UdpChannel でも経路ごとに別のソケット・別の受信スレッドで受信し、
/// 受信データには「どの経路で届いたか」を付けて呼び出し側へ渡す。
/// 経路の意味（何を意味するデータか）の解釈は呼び出し側が行う。
/// </summary>
public enum UdpChannelRoute
{
    /// <summary>Discovery（探索・接続確認）用の受信経路。</summary>
    Discovery = 0,

    /// <summary>通常データ（ゲーム中のメッセージ）用の受信経路。</summary>
    NormalData = 1
}

/// <summary>
/// UdpChannel：
/// UDPの送受信だけを行う通信基盤（通常のC#クラス・MonoBehaviourではない）。
///
/// 役割の境界：
///   本クラス   — ソケット生成とbind・UDP送信・ブロッキング受信と受信スレッド・
///                受信データのスレッドセーフな受け渡し（送信元エンドポイント／本文／経路識別）・
///                通信エラーの受け渡し・停止とソケット解放・受信スレッドの終了。
///   呼び出し側 — 受信内容の解釈（メッセージ種別判定・接続先確定・接続状態変更・PING処理・
///                パーサ呼び出し）と、それを使ったゲーム処理。すべてメインスレッドで行う。
///
/// 設計上の約束：
///   ・Unity（シーン・GameObject・UI・PlayerPrefs・ゲーム結果・ランキング）に依存しない。
///   ・受信スレッドでは「受信してキューへ積む」以外の処理をしない。
///     呼び出し側のコード（任意のUnity処理）を受信スレッドで実行するAPIは提供しない。
///   ・送信（Send）はメインスレッドからのみ呼ぶ。受信スレッドからは送信しない。
///   ・受信データとエラーはキューで渡し、メインスレッドが取り出した順に処理する。
///   ・Close／Dispose は複数回呼んでも安全。停止要求後にソケットを閉じ、
///     ブロッキング受信を終了させる。受信スレッドの終了待ちは有限時間のみ（無期限Joinはしない）。
/// </summary>
public sealed class UdpChannel : IDisposable
{
    /// <summary>受信した1件（本文・送信元エンドポイント・受信経路）。</summary>
    public readonly struct Datagram
    {
        /// <summary>どの受信経路で届いたか。</summary>
        public readonly UdpChannelRoute Route;

        /// <summary>受信した本文（UTF-8デコード済み）。</summary>
        public readonly string Text;

        /// <summary>送信元エンドポイント。宛先（IP）判定は呼び出し側が行う。</summary>
        public readonly IPEndPoint RemoteEndPoint;

        public Datagram(UdpChannelRoute route, string text, IPEndPoint remoteEndPoint)
        {
            Route = route;
            Text = text;
            RemoteEndPoint = remoteEndPoint;
        }
    }

    /// <summary>運用中に受信スレッドで起きた通信エラー1件（受信経路と例外メッセージ）。</summary>
    public readonly struct Issue
    {
        /// <summary>どの受信経路で起きたか。</summary>
        public readonly UdpChannelRoute Route;

        /// <summary>例外メッセージ（ログの整形・出力は呼び出し側が行う）。</summary>
        public readonly string Message;

        public Issue(UdpChannelRoute route, string message)
        {
            Route = route;
            Message = message;
        }
    }

    // 受信スレッドが積み、メインスレッドが取り出すキュー。
    private readonly ConcurrentQueue<Datagram> _received = new ConcurrentQueue<Datagram>();
    private readonly ConcurrentQueue<Issue> _issues = new ConcurrentQueue<Issue>();

    // 開いた受信経路（生成・停止はメインスレッドのみが行う）。
    private readonly List<Receiver> _receivers = new List<Receiver>();

    // 送信ソケット（bind しない＝送信元ポートはOS任せ。従来の new UdpClient() と同じ）。
    private UdpClient _senderClient;

    // 受信ループの継続条件（受信スレッドから読むため volatile）。
    // Close では「停止要求 → ソケットを閉じる」の順に行い、この値で
    // 停止時に予期されるソケット例外（想定内）と運用中の異常（通知対象）を区別する。
    private volatile bool _isRunning;

    private bool _closed;     // Close 済み（冪等化と再オープン禁止）
    private bool _disposed;

    private sealed class Receiver
    {
        public UdpChannelRoute Route;
        public UdpClient Client;
        public Thread Thread;
    }

    /// <summary>停止時に受信スレッドの終了を待つ合計時間（有限）。無期限には待たない。</summary>
    private const int JoinTotalTimeoutMs = 250;

    /// <summary>送信可能か（ソケットが開いていて、まだ停止していない）。</summary>
    public bool IsOpen => _isRunning && _senderClient != null;

    // ── 初期化 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 送信ソケットと、指定された受信経路（bind先ポート）をまとめて開く。
    ///  ・送信ソケットは bind しない（送信元ポートはOS任せ。従来の new UdpClient() と同じ）。
    ///  ・bindPort に 0 以下を渡した受信経路は作らない。
    ///  ・途中で失敗した場合は、それまでに生成したソケットを全て破棄して false を返す
    ///    （部分的に初期化されたソケットやスレッドを残さない）。
    /// </summary>
    /// <param name="discoveryBindPort">Discovery用の受信bind先ポート（0以下なら作らない）。</param>
    /// <param name="normalBindPort">通常データ用の受信bind先ポート（0以下なら作らない）。</param>
    /// <param name="error">失敗理由（成功時は空文字）。</param>
    public bool TryOpen(int discoveryBindPort, int normalBindPort, out string error)
    {
        return TryOpen(discoveryBindPort, normalBindPort, false, out error);
    }

    /// <summary>
    /// 送信ソケットの broadcast 許可を指定して開く（子機の Discovery が 255.255.255.255 へ送るため）。
    ///  ・enableBroadcastSend=false は従来と同じ挙動（親機の既存呼び出しは既定の false を使う）。
    ///  ・受信bind先・送信元ポート（OS任せ）・失敗時の後始末は従来どおり。
    /// </summary>
    /// <param name="discoveryBindPort">Discovery用の受信bind先ポート（0以下なら作らない）。</param>
    /// <param name="normalBindPort">通常データ用の受信bind先ポート（0以下なら作らない）。</param>
    /// <param name="enableBroadcastSend">送信ソケットで broadcast（255.255.255.255）を許可するか。</param>
    /// <param name="error">失敗理由（成功時は空文字）。</param>
    public bool TryOpen(int discoveryBindPort, int normalBindPort, bool enableBroadcastSend, out string error)
    {
        error = "";

        if (_disposed)  { error = "disposed";           return false; }
        if (_closed)    { error = "channel was closed"; return false; }
        if (_isRunning) { error = "already open";       return false; }

        try
        {
            _senderClient = new UdpClient();
            _senderClient.EnableBroadcast = enableBroadcastSend;

            if (discoveryBindPort > 0)
                OpenReceiver(UdpChannelRoute.Discovery, discoveryBindPort);

            if (normalBindPort > 0)
                OpenReceiver(UdpChannelRoute.NormalData, normalBindPort);
        }
        catch (Exception e)
        {
            error = e.Message;

            // 部分的な初期化の後始末（受信スレッドはまだ起動していないので待機は不要）。
            _isRunning = false;
            CloseSockets();
            _receivers.Clear();
            return false;
        }

        _isRunning = true;

        // 受信スレッドを起動する（引数で経路を渡す。ここで初めて受信が動き出す）。
        foreach (Receiver receiver in _receivers)
        {
            receiver.Thread = new Thread(ReceiveLoop) { IsBackground = true };
            receiver.Thread.Start(receiver);
        }

        return true;
    }

    private void OpenReceiver(UdpChannelRoute route, int bindPort)
    {
        Receiver receiver = new Receiver
        {
            Route = route,
            Client = new UdpClient(bindPort)
        };

        _receivers.Add(receiver);
    }

    // ── 送信（メインスレッドからのみ呼ぶ）────────────────────────────────────

    /// <summary>
    /// 1件送信する。停止後・未オープン時は InvalidOperationException を投げる
    /// （エラーの扱いは呼び出し元の既存の try/catch に任せる）。
    /// </summary>
    public void Send(byte[] data, int length, string ip, int port)
    {
        UdpClient sender = _senderClient;
        if (!_isRunning || sender == null)
            throw new InvalidOperationException("UdpChannel is not open");

        sender.Send(data, length, ip, port);
    }

    // ── 受信データ・通信エラーの取り出し（メインスレッド）────────────────────

    /// <summary>受信データを1件取り出す（受信順）。</summary>
    public bool TryDequeueReceived(out Datagram datagram)
    {
        return _received.TryDequeue(out datagram);
    }

    /// <summary>運用中に起きた通信エラーを1件取り出す（発生順）。</summary>
    public bool TryDequeueIssue(out Issue issue)
    {
        return _issues.TryDequeue(out issue);
    }

    // ── 受信スレッド ─────────────────────────────────────────────────────────

    /// <summary>
    /// 受信スレッドの本体。ここでは「ブロッキング受信してキューへ積む」以外を行わない
    /// （呼び出し側のコードは実行しない＝スレッドセーフな値の受け渡しだけを行う）。
    /// </summary>
    private void ReceiveLoop(object state)
    {
        Receiver receiver = (Receiver)state;
        UdpClient client = receiver.Client;
        UdpChannelRoute route = receiver.Route;

        while (_isRunning)
        {
            try
            {
                IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                byte[] data = client.Receive(ref remote);
                _received.Enqueue(new Datagram(route, Encoding.UTF8.GetString(data), remote));
            }
            catch (Exception e)
            {
                // 停止要求後にソケットを閉じることで起きる例外は想定内なので通知しない。
                // 運用中（_isRunning）に起きた例外だけを通信エラーとしてメインスレッドへ渡す。
                if (_isRunning)
                {
                    _issues.Enqueue(new Issue(route, e.Message));
                }
            }
        }
    }

    // ── 停止 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// 停止する：停止要求 → ソケットを閉じてブロッキング受信を解除 → 受信スレッドの終了を有限時間だけ待つ。
    /// 複数回呼んでも安全（2回目以降は何もしない）。
    /// </summary>
    public void Close()
    {
        if (_closed) return;
        _closed = true;

        // 1) 停止要求（受信ループの継続条件を落とす）
        _isRunning = false;

        // 2) ソケットを閉じる（これでブロッキング中の Receive が解除される）
        CloseSockets();

        // 3) 受信スレッドの終了を待つ（合計で有限時間。無期限Joinも受信スレッド自身へのJoinもしない）
        WaitForReceiveThreads();

        // 4) 終了した個体の受信結果を新しい個体へ流用しない（未処理分を捨てる）
        while (_received.TryDequeue(out _)) { }
        while (_issues.TryDequeue(out _)) { }

        _receivers.Clear();
    }

    private void CloseSockets()
    {
        UdpClient sender = _senderClient;
        _senderClient = null;
        CloseUdpClient(sender);

        foreach (Receiver receiver in _receivers)
        {
            UdpClient client = receiver.Client;
            receiver.Client = null;
            CloseUdpClient(client);
        }
    }

    private void WaitForReceiveThreads()
    {
        if (_receivers.Count == 0) return;

        System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();

        foreach (Receiver receiver in _receivers)
        {
            Thread thread = receiver.Thread;
            if (thread == null) continue;
            if (thread == Thread.CurrentThread) continue;   // 受信スレッド自身からの Join は行わない
            if (!thread.IsAlive) continue;

            int remaining = JoinTotalTimeoutMs - (int)elapsed.ElapsedMilliseconds;
            if (remaining <= 0) break;                      // 無期限には待たない

            thread.Join(remaining);
        }
    }

    private static void CloseUdpClient(UdpClient client)
    {
        if (client == null) return;

        try
        {
            client.Close();
            client.Dispose();
        }
        catch (Exception)
        {
            // 停止時の解放失敗は呼び出し側の運用に影響させない（ここでは通知しない）。
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
    }
}
