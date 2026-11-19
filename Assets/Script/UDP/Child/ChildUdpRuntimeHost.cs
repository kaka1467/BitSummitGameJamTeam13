using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Profiling;

/// <summary>
/// ChildUdpRuntimeHost：子機UDPランタイムの実行所有者。
/// </summary>
public sealed class ChildUdpRuntimeHost : MonoBehaviour
{
    private static readonly ProfilerMarker s_drainChannelQueuesMarker =
        new ProfilerMarker("UDP.DrainChannelQueues");
    private static readonly ProfilerMarker s_processReceiveQueueMarker =
        new ProfilerMarker("UDP.ProcessReceiveQueue");
    private static readonly ProfilerMarker s_processLogQueueMarker =
        new ProfilerMarker("UDP.ProcessLogQueue");

    public enum RuntimeState { Created = 0, Configured = 1, Running = 2, Stopped = 3 }

    public static ChildUdpRuntimeHost Running { get; private set; }

    public RuntimeState State { get; private set; } = RuntimeState.Created;
    public bool IsConfigured => State == RuntimeState.Configured || State == RuntimeState.Running;
    public bool IsRunning => State == RuntimeState.Running;

    private const string MagicNumber = "TEAM13_";
    private const string StartCommand = "START_GAME";
    private const string GameAudioSePrefix = "GAME_AUDIO_SE:";
    private const string GameAudioStatePrefix = "GAME_AUDIO_STATE:";

    private ChildUdpRuntimeHostSettings _settingsFallback;
    private Func<ChildUdpRuntimeHostSettings> _settingsProvider;
    private Func<bool> _showDebugProvider;

    private ChildUdpRuntimeHostSettings _settings => _settingsProvider != null ? _settingsProvider() : _settingsFallback;

    private UdpChannel _channel;
    private ChildConnectionLink _link;
    private ChildPlaySession _session;
    private ChildNotificationHub _notifications;
    private ChildRankSyncCoordinator _rankSync;

    public ChildConnectionLink Link => _link;
    public ChildPlaySession Session => _session;
    public ChildNotificationHub Notifications => _notifications;
    public ChildRankSyncCoordinator RankSync => _rankSync;
    public ChildUdpRuntimeHostSettings Settings => _settings;

    private bool ShowDebug => _showDebugProvider != null ? _showDebugProvider() : (_settings != null && _settings.ShowDebugLogs);

    public void BindShowDebugProvider(Func<bool> provider) => _showDebugProvider = provider;
    public void BindSettingsProvider(Func<ChildUdpRuntimeHostSettings> provider) => _settingsProvider = provider;

    private readonly ConcurrentQueue<string> _receiveQueue = new ConcurrentQueue<string>();
    private readonly ConcurrentQueue<UdpChannel.Issue> _issueQueue = new ConcurrentQueue<UdpChannel.Issue>();
    private readonly ConcurrentQueue<LogEntry> _logQueue = new ConcurrentQueue<LogEntry>();
    private readonly ConcurrentQueue<Action> _actionQueue = new ConcurrentQueue<Action>();

    private Coroutine _heartbeatCoroutine;
    private Coroutine _rankSyncResendCoroutine;
    private Coroutine _discoveryCoroutine;

    private readonly List<TrackedCoroutine> _sendResendCoroutines = new List<TrackedCoroutine>();

    private sealed class TrackedCoroutine
    {
        public Coroutine Handle;
    }

    private int _returnToTitleSendSeq = 0;
    private int _lastPeerReturnToTitleSeq = 0;

    /// <summary>子機単独スタート（Solo Start）によるプレイ中か（M-F06）。</summary>
    public bool IsSoloPlayActive { get; private set; }

    /// <summary>子機チュートリアル完了後、親機チュートリアル終了まで中断画面を維持するか。</summary>
    public bool KeepTutorialInterruptScreen { get; set; }

    private bool _sceneLoadedSubscribed;
    private bool _startupPrepared;
    private bool _rankSyncRestored;

    private const string PrefRankSyncSourceId = "RankSync_SourceId";
    private const string PrefRankSyncUpdateNo = "RankSync_UpdateNo";
    private const string PrefRankSyncConfirmed = "RankSync_Confirmed";
    private const string PrefRankSyncPending = "RankSync_PendingMessage";
    private const string PrefRankSyncSessionSeq = "RankSync_SessionSeq";

    private const string PrefKeyLastGameOverScore = "LastGameOverScore";
    private const string PrefKeyGameOverRankPrefix = "GameOverRank_";
    private const string PrefKeyLastTimeUpScore = "LastTimeUpScore";
    private const string PrefKeyTimeUpRankPrefix = "TimeUpRank_";

    private const float RankSyncResendInterval = 0.5f;
    private const int RankSyncMaxAttempts = 6;

    private struct LogEntry
    {
        public LogType Type;
        public string Message;
        public LogEntry(LogType type, string message) { Type = type; Message = message; }
    }

    public bool Initialize(ChildUdpRuntimeHostSettings settings, out string error)
    {
        if (State != RuntimeState.Created)
        {
            error = $"Initialize は状態 {State} では受け付けません（初期化は1回だけ）。";
            return false;
        }
        if (settings == null)
        {
            error = "設定が null です。";
            return false;
        }

        _settingsFallback = settings;

        _link = new ChildConnectionLink();
        _session = new ChildPlaySession();
        _notifications = new ChildNotificationHub();
        _rankSync = new ChildRankSyncCoordinator();

        _link.StateChanged += HandleLinkStateChanged;

        State = RuntimeState.Configured;
        error = "";
        return true;
    }

    private void PrepareStartup()
    {
        if (_startupPrepared) return;
        _startupPrepared = true;

        RestoreRankSyncStateOnce();

        if (!_sceneLoadedSubscribed)
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            _sceneLoadedSubscribed = true;
        }

        if (IsTitleScene(SceneManager.GetActiveScene().name))
            ResetForNewSession();
    }

    private void RestoreRankSyncStateOnce()
    {
        if (_rankSyncRestored) return;
        _rankSyncRestored = true;
        RestoreRankSyncState();
    }

    public string LastReceivedMessage { get; private set; } = "";

    private void HandleLinkStateChanged(ChildConnectionLink.LinkState state)
        => ConnectionStateChanged?.Invoke(state);

    public bool TryStartRuntime(out string error)
    {
        if (State == RuntimeState.Running) { error = "既に稼働中です。"; return false; }
        if (!IsConfigured) { error = $"設定が揃っていません（状態 {State}）。"; return false; }
        if (Running != null && Running != this) { error = "別の個体が稼働中です。"; return false; }

        PrepareStartup();

        UdpChannel channel = new UdpChannel();
        if (!channel.TryOpen(0, _settings.NormalPort, true, out string openError))
        {
            channel.Dispose();
            error = $"ソケットを開けませんでした: {openError}";
            return false;
        }

        _channel = channel;
        Running = this;
        State = RuntimeState.Running;
        error = "";
        return true;
    }

    public void ShutdownRuntime(string reason)
    {
        bool wasActive = State == RuntimeState.Running || State == RuntimeState.Configured;
        State = RuntimeState.Stopped;

        StopOwnCoroutines();

        if (_link != null)
            _link.StateChanged -= HandleLinkStateChanged;

        if (_sceneLoadedSubscribed)
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            _sceneLoadedSubscribed = false;
        }

        if (_channel != null)
        {
            _channel.Close();
            _channel.Dispose();
            _channel = null;
        }

        while (_actionQueue.TryDequeue(out _)) { }
        while (_receiveQueue.TryDequeue(out _)) { }
        while (_issueQueue.TryDequeue(out _)) { }
        while (_logQueue.TryDequeue(out _)) { }

        if (Running == this) Running = null;

        if (wasActive)
            Debug.Log($"[ChildUdpRuntimeHost] 稼働を終了しました（理由={reason}）。");
    }

    private void Awake()
    {
    }

    private void Update()
    {
        if (!IsRunning) return;

        DrainChannelQueues();
        ProcessReceiveQueue();
        while (_actionQueue.TryDequeue(out Action action)) action();
        ProcessLogQueue();

        ConsumePendingCaughtIfReady();
        ConsumePendingTimeUpIfReady();

        if (_link.IsTimedOut(Time.time, _settings.TimeoutLimit))
        {
            _link.Disconnect();
            Debug.LogWarning("[ChildUdpRuntimeHost] Connection timed out — parent heartbeat lost.");
        }

        TickCoroutineLifecycles();
    }

    private void TickCoroutineLifecycles()
    {
        if (_link.IsConnecting && _discoveryCoroutine == null)
            _discoveryCoroutine = StartCoroutine(DiscoveryCoroutine());
        else if (!_link.IsConnecting && _discoveryCoroutine != null)
        {
            StopCoroutine(_discoveryCoroutine);
            _discoveryCoroutine = null;
        }

        if (_link.IsConnected && _heartbeatCoroutine == null)
            _heartbeatCoroutine = StartCoroutine(HeartbeatCoroutine());
        else if (!_link.IsConnected && _heartbeatCoroutine != null)
        {
            StopCoroutine(_heartbeatCoroutine);
            _heartbeatCoroutine = null;
        }
    }

    private void OnDestroy()
    {
        if (State == RuntimeState.Created) return;
        ShutdownRuntime("破棄");
    }

    private void StopOwnCoroutines()
    {
        if (_heartbeatCoroutine != null) { StopCoroutine(_heartbeatCoroutine); _heartbeatCoroutine = null; }
        if (_rankSyncResendCoroutine != null) { StopCoroutine(_rankSyncResendCoroutine); _rankSyncResendCoroutine = null; }
        if (_discoveryCoroutine != null) { StopCoroutine(_discoveryCoroutine); _discoveryCoroutine = null; }

        for (int i = 0; i < _sendResendCoroutines.Count; i++)
        {
            if (_sendResendCoroutines[i].Handle != null) StopCoroutine(_sendResendCoroutines[i].Handle);
        }
        _sendResendCoroutines.Clear();
    }

    private void DrainChannelQueues()
    {
        if (_channel == null) return;

        using var drainScope = s_drainChannelQueuesMarker.Auto();

        while (_channel.TryDequeueIssue(out UdpChannel.Issue issue))
            EnqueueLog(LogType.Error, $"[ChildUdpRuntimeHost] Receive error: {issue.Message}");

        while (_channel.TryDequeueReceived(out UdpChannel.Datagram datagram))
        {
            string text = datagram.Text;
            IPEndPoint ep = datagram.RemoteEndPoint;

            if (text != MagicNumber + "PING")
                EnqueueLog(LogType.Log, $"[ChildUdpRuntimeHost] Received: '{text}' from {(ep != null ? ep.Address.ToString() : "unknown")}");

            if (text == MagicNumber + "DISCOVERY_ACCEPT")
            {
                string parentIP = ep != null ? ep.Address.ToString() : "";
                EnqueueLog(LogType.Log, $"[ChildUdpRuntimeHost] DISCOVERY_ACCEPT from {parentIP} — now Connected.");
                _actionQueue.Enqueue(() => HandleDiscoveryAccept(parentIP));
            }

            _receiveQueue.Enqueue(text);
        }
    }

    private void HandleDiscoveryAccept(string parentIP)
    {
        _link.SetTargetIP(parentIP);
        _link.SetState(ChildConnectionLink.LinkState.Connected);
        _link.NoteReceive(Time.time);
        _session.GameSceneLoaded = false;

        GameDifficulty.SendSettingsSyncToPeer();
        SendRankSyncOnConnect();
    }

    private void ProcessReceiveQueue()
    {
        using var receiveScope = s_processReceiveQueueMarker.Auto();

        while (_receiveQueue.TryDequeue(out string raw))
            DispatchMessage(raw);
    }

    private void DispatchMessage(string raw)
    {
        if (string.IsNullOrEmpty(raw) || !raw.StartsWith(MagicNumber, StringComparison.Ordinal)) return;

        string msg = raw.Substring(MagicNumber.Length);
        LastReceivedMessage = msg;

        if (msg == "PING") { _link.NoteReceive(Time.time); return; }

        if (msg == StartCommand || msg.StartsWith(StartCommand + ":", StringComparison.Ordinal))
        {
            string sid = null;
            if (msg.StartsWith(StartCommand + ":", StringComparison.Ordinal))
            {
                sid = msg.Substring(StartCommand.Length + 1);
                if (!string.IsNullOrEmpty(sid)) _lastPeerReturnToTitleSeq = 0;
            }

            bool isNewPlay =
                _session.EvaluateStartGame(sid ?? "", out _) == ChildPlaySession.StartGameBoundary.NewPlay;

            IsSoloPlayActive = false;
            if (isNewPlay)
            {
                _session.CaughtHandled = false;
                _session.TimeUpHandled = false;
                ClearPendingCaught("新プレイ開始（START_GAME）");
                ClearPendingTimeUp("新プレイ開始（START_GAME）");
                ResetCaptureWaitState("新プレイ開始（START_GAME）");
            }

            if (ShowDebug)
                Debug.Log($"[ChildUdpRuntimeHost] Received START_GAME from parent — loading game scene. playSessionId={_session.PlaySessionId}, isNewPlay={isNewPlay}");

            LoadGameScene();
            return;
        }

        if (msg == "CAUGHT") { HandleCaughtReceived(); return; }

        if (msg == "TIME_UP" || msg.StartsWith("TIME_UP:", StringComparison.Ordinal))
        {
            string sid = msg.StartsWith("TIME_UP:", StringComparison.Ordinal)
                ? msg.Substring("TIME_UP:".Length).Trim()
                : "";
            HandleTimeUpReceived(sid);
            return;
        }

        if (msg.StartsWith("SEQUENCE_DONE:", StringComparison.Ordinal))
        {
            string rest = msg.Substring("SEQUENCE_DONE:".Length);
            string[] parts = rest.Split(':');
            string playId = parts.Length > 0 ? parts[0].Trim() : "";
            string reason = parts.Length > 1 ? parts[1].Trim() : "unspecified";

            HandleSequenceDoneMessage(playId, reason);
            return;
        }

        if (msg.StartsWith("RETURN_TO_TITLE:", StringComparison.Ordinal))
        {
            string rest = msg.Substring("RETURN_TO_TITLE:".Length);
            string[] parts = rest.Split(':');

            string sid = "";
            string seqText;
            bool fast = false;

            if (parts.Length == 1)
            {
                seqText = parts[0];
            }
            else if (parts.Length == 2)
            {
                sid = parts[0];
                seqText = parts[1];
            }
            else if (parts.Length == 3 && parts[2] == "FAST")
            {
                sid = parts[0];
                seqText = parts[1];
                fast = true;
            }
            else
            {
                if (ShowDebug)
                    Debug.LogWarning($"[ChildUdpRuntimeHost] RETURN_TO_TITLE ignored — invalid format: '{msg}'");
                return;
            }

            if (!int.TryParse(seqText, out int seq))
            {
                if (ShowDebug)
                    Debug.LogWarning($"[ChildUdpRuntimeHost] RETURN_TO_TITLE ignored — could not parse seq: '{seqText}'");
                return;
            }

            HandleTeamReturnToTitle(sid, seq, fast);
            return;
        }

        if (msg.StartsWith("RANK_SYNC_CHALLENGE:", StringComparison.Ordinal))
        {
            if (RankSyncChallenge.TryParse(msg, out RankSyncChallenge challenge, out string challengeError))
            {
                _rankSync.SetChallengeId(challenge.ChallengeId);
                if (ShowDebug)
                    Debug.Log($"[ChildUdpRuntimeHost] RANK_SYNC_CHALLENGE received — challenge='{challenge.ChallengeId}'.");

                if (_rankSync.HasPendingMessage) StartRankSyncResend("challenge");
                else SendRankSyncHello();
            }
            else if (ShowDebug)
            {
                Debug.LogWarning($"[ChildUdpRuntimeHost] RANK_SYNC_CHALLENGE ignored — {challengeError}: '{msg}'");
            }
            return;
        }

        if (msg.StartsWith("RANK_SYNC_ACK:", StringComparison.Ordinal))
        {
            string[] ackParts = msg.Substring("RANK_SYNC_ACK:".Length).Split(':');

            if (ackParts.Length != 3)
            {
                if (ShowDebug) Debug.LogWarning($"[ChildUdpRuntimeHost] RANK_SYNC_ACK ignored — invalid format: '{msg}'");
                return;
            }

            if (ackParts[0] != RankingSnapshot.VersionName)
            {
                if (ShowDebug) Debug.LogWarning($"[ChildUdpRuntimeHost] RANK_SYNC_ACK ignored — unsupported version: '{ackParts[0]}'");
                return;
            }

            if (!int.TryParse(ackParts[2], out int ackUpdate))
            {
                if (ShowDebug) Debug.LogWarning($"[ChildUdpRuntimeHost] RANK_SYNC_ACK ignored — invalid updateNo: '{ackParts[2]}'");
                return;
            }

            HandleRankSyncAck(ackParts[1], ackUpdate);
            return;
        }

        if (msg == "SLEEP_LOCK")
        {
            if (_notifications.IsSleepLocked)
            {
                if (ShowDebug) Debug.Log("[ChildUdpRuntimeHost] Received redundant SLEEP_LOCK (already locked) — skipped.");
                return;
            }

            _notifications.SetSleepLocked(true);
            if (ShowDebug) Debug.Log("[ChildUdpRuntimeHost] Received SLEEP_LOCK — locking input.");
            return;
        }

        if (msg == "SLEEP_UNLOCK")
        {
            if (KeepTutorialInterruptScreen)
            {
                if (ShowDebug) Debug.Log("[ChildUdpRuntimeHost] Ignored SLEEP_UNLOCK during Parent Tutorial interrupt screen.");
                return;
            }

            if (!_notifications.IsSleepLocked)
            {
                if (ShowDebug) Debug.Log("[ChildUdpRuntimeHost] Received redundant SLEEP_UNLOCK (already unlocked) — skipped.");
                return;
            }

            _notifications.SetSleepLocked(false);
            if (ShowDebug) Debug.Log("[ChildUdpRuntimeHost] Received SLEEP_UNLOCK — unlocking input.");
            return;
        }

        if (msg.StartsWith(GameDifficulty.SyncPrefix, StringComparison.Ordinal))
        {
            GameDifficulty.ApplyRemoteSettingsSync(msg);
            return;
        }

        if (ShowDebug)
            Debug.Log($"[ChildUdpRuntimeHost] Unhandled message: '{msg}'");
    }

    private void SendToUdp(string message, string ip, int port)
    {
        UdpChannel channel = _channel;
        if (channel == null)
            throw new InvalidOperationException("UDP channel is not open");

        byte[] data = Encoding.UTF8.GetBytes(MagicNumber + message);
        channel.Send(data, data.Length, ip, port);
    }

    public void SendState(string message)
    {
        if (!IsRunning || _channel == null) return;

        string ip = _link != null ? _link.TargetIP : _settings.TargetIP;
        int port = _settings.ParentReceivePort;

        if (ShowDebug) Debug.Log($"[ChildUdpRuntimeHost] → '{message}' to {ip}:{port}");

        try { SendToUdp(message, ip, port); }
        catch (Exception e) { Debug.LogError($"[ChildUdpRuntimeHost] SendState error: {e.Message}"); }
    }

    public event Action<ChildConnectionLink.LinkState> ConnectionStateChanged;

    public ChildConnectionLink.LinkState ConnectionState
        => _link != null ? _link.State : ChildConnectionLink.LinkState.Disconnected;

    public void RequestConnect() => _link?.BeginConnecting();
    public void CancelConnect() => _link?.Disconnect();

    private void LoadGameScene()
    {
        if (ShowDebug)
            Debug.Log($"[ChildUdpRuntimeHost] LoadGameScene ENTER — gameSceneLoaded={_session.GameSceneLoaded}, 遷移先='{_settings.GameSceneName}', 現在scene='{SceneManager.GetActiveScene().name}'");

        if (_session.GameSceneLoaded)
        {
            if (ShowDebug)
                Debug.Log("[ChildUdpRuntimeHost] LoadGameScene 中断 — gameSceneLoaded が既に true のため LoadScene しません。");
            return;
        }
        _session.GameSceneLoaded = true;

        ChildSceneTransitionRunner.LoadSceneNow(_settings.GameSceneName);
    }

    private void HandleCaughtReceived()
    {
        string activeScene = SceneManager.GetActiveScene().name;
        if (ShowDebug)
            Debug.Log($"[ChildUdpRuntimeHost] Received TEAM13_CAUGHT — scene='{activeScene}', GameManager.instance={(GameManager.instance != null ? "present" : "NULL")}.");

        if (IsTitleScene(activeScene) ||
            activeScene == ChildCaughtFallbackPolicy.GameOverSceneName ||
            activeScene == ChildCaughtFallbackPolicy.GameClearSceneName)
        {
            if (ShowDebug)
                Debug.Log($"[ChildUdpRuntimeHost] TEAM13_CAUGHT discarded — scene='{activeScene}'（タイトル／結果のため破棄）");
            return;
        }

        if (activeScene == _settings.GameSceneName && string.IsNullOrEmpty(_session.PlaySessionId))
        {
            if (ShowDebug)
                Debug.Log($"[ChildUdpRuntimeHost] TEAM13_CAUGHT discarded — scene='{activeScene}'（ロード中かつ有効なプレイIDが無い）");
            return;
        }

        if (_session.TimeUpHandled)
        {
            if (ShowDebug)
                Debug.Log($"[ChildUdpRuntimeHost] TEAM13_CAUGHT ignored — scene='{activeScene}', 既に TIME_UP クリア確定済み（M-R04）");
            return;
        }

        if (_session.CaughtHandled)
        {
            if (ShowDebug)
                Debug.Log($"[ChildUdpRuntimeHost] TEAM13_CAUGHT ignored — scene='{activeScene}', 既に受理済み（再送を無視）");
            return;
        }

        HandleCaughtAccepted(activeScene);
    }

    private void HandleCaughtAccepted(string activeScene)
    {
        _session.CaughtHandled = true;
        EnterCaptureWait("CAUGHT受理");

        if (_notifications.HasCaughtSubscriber)
        {
            if (ShowDebug)
                Debug.Log($"[ChildUdpRuntimeHost] CAUGHTを購読者へ通知します（メインスレッド, scene='{activeScene}'）。");
            _notifications.NotifyCaughtNow();
            return;
        }

        if (IsGameplayScene(activeScene) && GameManager.instance == null)
        {
            RunCaughtFallback(activeScene);
            return;
        }

        EnqueuePendingCaught();
    }

    private void RunCaughtFallback(string activeScene)
    {
        bool shouldRunFallback = ChildCaughtFallbackPolicy.ShouldRunFallback(activeScene, _settings.TitleSceneName);
        if (shouldRunFallback)
        {
            if (ShowDebug)
                Debug.Log($"[ChildUdpRuntimeHost] CAUGHT fallback executed — scene='{activeScene}', loading '{ChildCaughtFallbackPolicy.GameOverSceneName}'.");

            int finalScore = 0;
            GameManager gm = GameManager.instance != null ? GameManager.instance : FindFirstObjectByType<GameManager>();
            if (gm != null)
            {
                finalScore = gm.score;
            }

            GameDifficulty.RecordResultScore(true, finalScore);
            NotifyRankingChanged(RankingSnapshot.TypeGameOver);
            ChildSceneTransitionRunner.LoadSceneNow(ChildCaughtFallbackPolicy.GameOverSceneName);
        }
        else if (ShowDebug)
        {
            Debug.Log($"[ChildUdpRuntimeHost] CAUGHT fallback skipped — scene='{activeScene}'.");
        }
    }

    private void EnqueuePendingCaught()
    {
        if (_notifications.HasPendingCaught)
        {
            if (ShowDebug)
                Debug.Log("[ChildUdpRuntimeHost] CAUGHT は既に1件保留済みのため追加しません（再送は無視）。");
            return;
        }

        _notifications.EnqueuePendingCaught(_session.PlaySessionId ?? "");
        if (ShowDebug)
            Debug.Log($"[ChildUdpRuntimeHost] CAUGHT を1件保留します（playId='{_notifications.PendingCaughtPlayId}'、購読成立後に通知）。");
    }

    private void ClearPendingCaught(string reason)
    {
        if (!_notifications.HasPendingCaught) return;

        _notifications.ClearPendingCaught();
        if (ShowDebug)
            Debug.Log($"[ChildUdpRuntimeHost] CAUGHT の保留を破棄しました（{reason}）。");
    }

    private void ConsumePendingCaughtIfReady()
    {
        if (!_notifications.HasPendingCaught) return;

        string pendingScene = SceneManager.GetActiveScene().name;

        ChildNotificationHub.CaughtPendingOutcome outcome =
            _notifications.ConsumePendingCaught(
                isNonGameplayScene: IsTitleScene(pendingScene) ||
                                    pendingScene == ChildCaughtFallbackPolicy.GameOverSceneName ||
                                    pendingScene == ChildCaughtFallbackPolicy.GameClearSceneName,
                isGameplayScene: IsGameplayScene(pendingScene),
                currentPlayId: _session.PlaySessionId);

        if (outcome == ChildNotificationHub.CaughtPendingOutcome.Cleared)
        {
            if (ShowDebug)
                Debug.Log("[ChildUdpRuntimeHost] CAUGHT の保留を破棄しました（非ゲームシーンへ移行）。");
        }
        else if (outcome == ChildNotificationHub.CaughtPendingOutcome.Notified)
        {
            if (ShowDebug)
                Debug.Log("[ChildUdpRuntimeHost] 保留していた CAUGHT を購読者へ通知します（メインスレッド・同一プレイ）。");
        }
    }

    private void HandleTimeUpReceived(string playId)
    {
        string activeScene = SceneManager.GetActiveScene().name;
        if (ShowDebug)
            Debug.Log($"[ChildUdpRuntimeHost] Received TIME_UP — playId='{playId}', current='{_session.PlaySessionId}', scene='{activeScene}'.");

        if (IsTitleScene(activeScene) ||
            activeScene == ChildCaughtFallbackPolicy.GameOverSceneName ||
            activeScene == ChildCaughtFallbackPolicy.GameClearSceneName)
        {
            if (ShowDebug)
                Debug.Log($"[ChildUdpRuntimeHost] TIME_UP discarded — scene='{activeScene}'（タイトル／結果のため破棄）");
            return;
        }

        if (!string.IsNullOrEmpty(playId) &&
            !string.IsNullOrEmpty(_session.PlaySessionId) &&
            playId != _session.PlaySessionId)
        {
            if (ShowDebug)
                Debug.Log($"[ChildUdpRuntimeHost] TIME_UP ignored — 別プレイID (recv='{playId}', current='{_session.PlaySessionId}')");
            return;
        }

        if (_session.CaughtHandled)
        {
            if (ShowDebug)
                Debug.Log("[ChildUdpRuntimeHost] TIME_UP ignored — 既に捕獲確定済みのため失敗を優先します（M-R04）");
            return;
        }

        if (_session.TimeUpHandled)
        {
            if (ShowDebug)
                Debug.Log("[ChildUdpRuntimeHost] TIME_UP ignored — 既に受理済み（再送を無視）");
            return;
        }

        HandleTimeUpAccepted(activeScene);
    }

    private void HandleTimeUpAccepted(string activeScene)
    {
        _session.TimeUpHandled = true;
        _notifications.SetSleepLocked(false);

        if (_notifications.HasTimeUpSubscriber)
        {
            if (ShowDebug)
                Debug.Log($"[ChildUdpRuntimeHost] TIME_UP を購読者へ通知します（メインスレッド, scene='{activeScene}'）。");
            _notifications.NotifyTimeUpNow();
            return;
        }

        if (IsGameplayScene(activeScene) && GameManager.instance == null)
        {
            RunTimeUpFallback(activeScene);
            return;
        }

        EnqueuePendingTimeUp();
    }

    private void RunTimeUpFallback(string activeScene)
    {
        bool shouldRunFallback = ChildCaughtFallbackPolicy.ShouldRunFallback(activeScene, _settings.TitleSceneName);
        if (!shouldRunFallback) return;

        int finalScore = 0;
        GameManager gm = GameManager.instance != null ? GameManager.instance : FindFirstObjectByType<GameManager>();
        if (gm != null) finalScore = gm.score;

        GameDifficulty.RecordResultScore(false, finalScore);
        NotifyRankingChanged(RankingSnapshot.TypeTimeUp);
        ChildSceneTransitionRunner.LoadSceneNow(ChildCaughtFallbackPolicy.GameClearSceneName);
    }

    private void EnqueuePendingTimeUp()
    {
        if (_notifications.HasPendingTimeUp) return;
        _notifications.EnqueuePendingTimeUp(_session.PlaySessionId ?? "");
        if (ShowDebug)
            Debug.Log($"[ChildUdpRuntimeHost] TIME_UP を1件保留します（playId='{_notifications.PendingTimeUpPlayId}'）。");
    }

    private void ClearPendingTimeUp(string reason)
    {
        if (!_notifications.HasPendingTimeUp) return;
        _notifications.ClearPendingTimeUp();
        if (ShowDebug)
            Debug.Log($"[ChildUdpRuntimeHost] TIME_UP の保留を破棄しました（{reason}）。");
    }

    private void ConsumePendingTimeUpIfReady()
    {
        if (!_notifications.HasPendingTimeUp) return;

        string pendingScene = SceneManager.GetActiveScene().name;
        ChildNotificationHub.CaughtPendingOutcome outcome =
            _notifications.ConsumePendingTimeUp(
                isNonGameplayScene: IsTitleScene(pendingScene) ||
                                    pendingScene == ChildCaughtFallbackPolicy.GameOverSceneName ||
                                    pendingScene == ChildCaughtFallbackPolicy.GameClearSceneName,
                isGameplayScene: IsGameplayScene(pendingScene),
                currentPlayId: _session.PlaySessionId);

        if (outcome == ChildNotificationHub.CaughtPendingOutcome.Notified && ShowDebug)
            Debug.Log("[ChildUdpRuntimeHost] 保留していた TIME_UP を購読者へ通知します。");
    }

    private bool IsTitleScene(string sceneName)
        => ChildCaughtFallbackPolicy.IsTitleScene(sceneName, _settings.TitleSceneName);

    private bool IsGameplayScene(string sceneName)
        => ChildCaughtFallbackPolicy.IsGameplayScene(sceneName, _settings.GameSceneName, _settings.TitleSceneName);

    private void HandleSequenceDoneMessage(string playId, string reason)
    {
        if (!string.IsNullOrEmpty(playId) &&
            !string.IsNullOrEmpty(_session.PlaySessionId) &&
            playId != _session.PlaySessionId)
        {
            Debug.Log($"[ChildUdpRuntimeHost] SEQUENCE_DONE ignored（前回プレイの遅延通知）: playId='{playId}' / current='{_session.PlaySessionId}'");
            return;
        }

        string effectivePlayId = string.IsNullOrEmpty(playId) ? _session.PlaySessionId : playId;

        SendState($"SEQUENCE_DONE_ACK:{effectivePlayId}");

        if (!_notifications.TryAcceptSequenceDone(effectivePlayId))
        {
            Debug.Log($"[ChildUdpRuntimeHost] SEQUENCE_DONE は処理済み（重複・再送）: playId='{effectivePlayId}'（ACKのみ返しました）");
            return;
        }

        if (string.IsNullOrEmpty(effectivePlayId))
        {
            Debug.LogWarning("[ChildUdpRuntimeHost] SEQUENCE_DONE のプレイIDが空です。現在プレイとして扱います");
        }

        string releaseReason = $"演出完了通知を受信（playId='{effectivePlayId}', reason='{reason}'）";
        if (_notifications.HandleSequenceDoneAccepted(effectivePlayId, releaseReason))
        {
            Debug.Log($"[ChildUdpRuntimeHost] 捕獲演出の待機を解除しました（理由={releaseReason}）。");
            return;
        }

        Debug.Log($"[ChildUdpRuntimeHost] SEQUENCE_DONE を保留しました（捕獲通知より先に受信。待機開始時に解除します）: playId='{effectivePlayId}', reason='{reason}'");
    }

    private void EnterCaptureWait(string reason)
    {
        if (!_notifications.BeginCaptureWait(_session.PlaySessionId)) return;

        Debug.Log($"[ChildUdpRuntimeHost] 捕獲演出の待機を開始しました（理由={reason}, playSessionId='{_session.PlaySessionId}'）。");
    }

    private void ReleaseCaptureWait(string reason)
    {
        if (!_notifications.ReleaseWait(_session.PlaySessionId, reason)) return;

        Debug.Log($"[ChildUdpRuntimeHost] 捕獲演出の待機を解除しました（理由={reason}）。");
    }

    public void ReleaseCaptureWaitByLocalTimeout(string reason)
    {
        if (!_notifications.ReleaseWaitByLocalTimeout(_session.PlaySessionId, reason))
        {
            Debug.Log($"[ChildUdpRuntimeHost] 子機側の上限で待機終了を記録しました（既に解除済み。理由={reason}）");
        }
    }

    private void ResetCaptureWaitState(string reason)
    {
        _notifications.ResetWait();
        _notifications.ResetSequenceDoneRecords();
        Debug.Log($"[ChildUdpRuntimeHost] 捕獲演出の待機・完了通知の状態を初期化しました（理由={reason}）。");
    }

    public bool IsCaptureWaitCompletedForCurrentPlay => _notifications.IsWaitCompletedForPlay(_session.PlaySessionId);
    public bool HasPendingCaught => _notifications.HasPendingCaught;

    private void HandleTeamReturnToTitle(string playSessionId, int seq, bool fast)
    {
        bool sessionMatches = !string.IsNullOrEmpty(playSessionId)
            ? playSessionId == _session.PlaySessionId
            : string.IsNullOrEmpty(_session.PlaySessionId);

        if (!sessionMatches)
        {
            if (ShowDebug)
                Debug.Log($"[ChildUdpRuntimeHost] RETURN_TO_TITLE ignored — different play session (recv='{playSessionId}', current='{_session.PlaySessionId}').");
            return;
        }

        if (seq <= _lastPeerReturnToTitleSeq)
        {
            if (ShowDebug)
                Debug.Log($"[ChildUdpRuntimeHost] RETURN_TO_TITLE ignored — stale/duplicate seq={seq} (last processed={_lastPeerReturnToTitleSeq}).");
            return;
        }
        _lastPeerReturnToTitleSeq = seq;

        string activeScene = SceneManager.GetActiveScene().name;
        if (IsTitleScene(activeScene))
        {
            if (ShowDebug)
                Debug.Log("[ChildUdpRuntimeHost] RETURN_TO_TITLE ignored — already on title scene.");
            return;
        }

        ResultSceneChamger changer = FindFirstObjectByType<ResultSceneChamger>();
        if (changer != null)
        {
            if (changer.IsReturningToTitle)
            {
                if (ShowDebug)
                    Debug.Log("[ChildUdpRuntimeHost] RETURN_TO_TITLE ignored — already returning to title (fade in progress).");
                return;
            }

            if (ShowDebug)
                Debug.Log($"[ChildUdpRuntimeHost] RETURN_TO_TITLE received — starting result fade to title (fast={fast}).");
            changer.StartFadeToTitle(notifyPeer: false, skipWaits: fast);
            return;
        }

        Debug.Log($"[ChildUdpRuntimeHost] RETURN_TO_TITLE received — returning to title from scene={activeScene}.");
        ChildSceneTransitionRunner.LoadSceneNowResolved(_settings.TitleSceneName);
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        Debug.Log($"[ChildUdpRuntimeHost] Scene loaded: '{scene.name}' — refreshing scene references.");

        if (IsTitleScene(scene.name))
        {
            ResetForNewSession();
            Debug.Log($"[ChildUdpRuntimeHost] Title scene loaded ('{scene.name}') — reset state for next round.");
        }
        else if (scene.name == ChildCaughtFallbackPolicy.GameOverSceneName ||
                 scene.name == ChildCaughtFallbackPolicy.GameClearSceneName)
        {
            ClearPendingCaught("結果シーン読込");
            ClearPendingTimeUp("結果シーン読込");
        }
    }

    public void ResetForNewSession()
    {
        _link.ResetForNewSession();
        LastReceivedMessage = "";
        IsSoloPlayActive = false;
        KeepTutorialInterruptScreen = false;
        _session.GameSceneLoaded = false;
        _session.CaughtHandled = false;
        _session.TimeUpHandled = false;
        ClearPendingCaught("ResetForNewSession（タイトル復帰）");
        ClearPendingTimeUp("ResetForNewSession（タイトル復帰）");
        ResetCaptureWaitState("ResetForNewSession（タイトル復帰）");

        _notifications.SetSleepLocked(false);

        _session.PlaySessionId = "";
        _lastPeerReturnToTitleSeq = 0;

        if (_discoveryCoroutine != null) { StopCoroutine(_discoveryCoroutine); _discoveryCoroutine = null; }
        if (_heartbeatCoroutine != null) { StopCoroutine(_heartbeatCoroutine); _heartbeatCoroutine = null; }
    }

    public void SendStateRepeated(string message, int extraSends = 3, float interval = 0.15f)
    {
        SendState(message);

        if (extraSends <= 0) return;

        TrackedCoroutine tracked = new TrackedCoroutine();
        _sendResendCoroutines.Add(tracked);
        tracked.Handle = StartCoroutine(
            ResendRoutine(message, extraSends, interval, () => _sendResendCoroutines.Remove(tracked)));
    }

    private IEnumerator ResendRoutine(string message, int count, float interval, Action onCompleted)
    {
        for (int i = 0; i < count; i++)
        {
            yield return new WaitForSecondsRealtime(interval);
            SendState(message);
        }

        onCompleted?.Invoke();
    }

    public void SendLoudItem()
    {
        SendState("LOUD_ITEM");
        Debug.Log("[ChildUdpRuntimeHost] Sent LOUD_ITEM packet to Parent.");
    }

    public void SendGameAudioSe(string playId, int seq, int soundId, float volume)
    {
        if (!IsGameAudioPayloadValid(playId, seq, soundId)) return;

        int volumeMilli = ToVolumeMilli(volume);
        SendState($"{GameAudioSePrefix}{playId}:{seq}:{soundId}:{volumeMilli}");

        if (ShowDebug)
            Debug.Log($"[ChildUdpRuntimeHost] Sent GAME_AUDIO_SE id={soundId} seq={seq} vol={volumeMilli}");
    }

    public void SendGameAudioState(string playId, int seq, int soundId, bool playing, bool loop, float volume)
    {
        if (!IsGameAudioPayloadValid(playId, seq, soundId)) return;

        int volumeMilli = ToVolumeMilli(volume);
        int playingFlag = playing ? 1 : 0;
        int loopFlag = loop ? 1 : 0;
        SendState($"{GameAudioStatePrefix}{playId}:{seq}:{soundId}:{playingFlag}:{loopFlag}:{volumeMilli}");

        if (ShowDebug)
            Debug.Log($"[ChildUdpRuntimeHost] Sent GAME_AUDIO_STATE id={soundId} seq={seq} playing={playingFlag} loop={loopFlag} vol={volumeMilli}");
    }

    private static int ToVolumeMilli(float volume)
    {
        return Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(volume) * 1000f), 0, 1000);
    }

    private static bool IsGameAudioPayloadValid(string playId, int seq, int soundId)
    {
        if (string.IsNullOrEmpty(playId) || playId.Length > 64) return false;
        if (playId.IndexOf(':') >= 0) return false;
        if (seq < 0 || soundId < 0) return false;
        return true;
    }

    public void SendGameProgress(float progressRate)
    {
        int progressMilli = Mathf.RoundToInt(Mathf.Clamp01(progressRate) * 1000f);
        SendState($"GAME_PROGRESS:{progressMilli}");
        if (ShowDebug)
            Debug.Log($"[ChildUdpRuntimeHost] Sent GAME_PROGRESS:{progressMilli} packet to Parent.");
    }

    public void notifyReturnToTitle(bool fast = false)
    {
        _returnToTitleSendSeq++;

        string message = fast
            ? $"RETURN_TO_TITLE:{_session.PlaySessionId}:{_returnToTitleSendSeq}:FAST"
            : $"RETURN_TO_TITLE:{_session.PlaySessionId}:{_returnToTitleSendSeq}";

        SendStateRepeated(message);
        Debug.Log($"[ChildUdpRuntimeHost] Sent RETURN_TO_TITLE (playSessionId={_session.PlaySessionId}, seq={_returnToTitleSendSeq}, fast={fast}) with retries to parent.");
    }

    public void OnStartButtonClicked()
    {
        _session.PlaySessionId = Guid.NewGuid().ToString("N");
        _lastPeerReturnToTitleSeq = 0;

        IsSoloPlayActive = false;
        _session.CaughtHandled = false;
        _session.TimeUpHandled = false;
        ClearPendingCaught("新プレイ開始（子機起点）");
        ClearPendingTimeUp("新プレイ開始（子機起点）");
        ResetCaptureWaitState("新プレイ開始（子機起点）");

        GameDifficulty.SendSettingsSyncToPeer();
        SendState($"{StartCommand}:{_session.PlaySessionId}");
        Debug.Log($"[ChildUdpRuntimeHost] Sent START_GAME (playSessionId={_session.PlaySessionId}) to parent at {_link.TargetIP}:{_settings.ParentReceivePort}");
        LoadGameScene();
    }

    public void OnSoloStartButtonClicked()
    {
        _session.PlaySessionId = Guid.NewGuid().ToString("N");
        _lastPeerReturnToTitleSeq = 0;

        IsSoloPlayActive = true;
        _session.CaughtHandled = false;
        _session.TimeUpHandled = false;
        ClearPendingCaught("新プレイ開始（子機単独）");
        ClearPendingTimeUp("新プレイ開始（子機単独）");
        ResetCaptureWaitState("新プレイ開始（子機単独）");

        Debug.Log($"[ChildUdpRuntimeHost] Solo start (親機なし) — START_GAME は送信しません (playSessionId={_session.PlaySessionId}, connection={ConnectionState})");
        LoadGameScene();
    }

    public void OnConnectButtonClicked()
    {
        if (ConnectionState == ChildConnectionLink.LinkState.Connected) OnStartButtonClicked();
        else RequestConnect();
    }

    private IEnumerator DiscoveryCoroutine()
    {
        while (_link.IsConnecting)
        {
            try
            {
                SendToUdp(BuildDiscoveryRequest(), "255.255.255.255", _settings.BroadcastPort);
                Debug.Log($"[ChildUdpRuntimeHost] Sent DISCOVERY_REQUEST (broadcast) source='{_rankSync.SourceId}' sessionSeq={_rankSync.SessionSeq}");
            }
            catch (Exception e) { Debug.LogError($"[ChildUdpRuntimeHost] Discovery send error: {e.Message}"); }

            yield return new WaitForSeconds(0.5f);
        }
    }

    private IEnumerator HeartbeatCoroutine()
    {
        while (_link.IsConnected)
        {
            try { SendToUdp("PING", _link.TargetIP, _settings.ParentReceivePort); }
            catch (Exception e) { Debug.LogError($"[ChildUdpRuntimeHost] Heartbeat error: {e.Message}"); }

            yield return new WaitForSecondsRealtime(_settings.PingInterval);
        }
    }

    private string BuildDiscoveryRequest()
        => "DISCOVERY_REQUEST:" + _rankSync.SourceId + ":" + _rankSync.SessionSeq;

    private void RequestChallengeIfNeeded()
    {
        if (_rankSync.HasChallengeId) return;

        try
        {
            SendToUdp(BuildDiscoveryRequest(), "255.255.255.255", _settings.BroadcastPort);
            if (ShowDebug)
                Debug.Log("[ChildUdpRuntimeHost] RANK_SYNC_CHALLENGE 未受信のため DISCOVERY_REQUEST を再送。");
        }
        catch (Exception e) { Debug.LogError($"[ChildUdpRuntimeHost] Challenge request error: {e.Message}"); }
    }

    private void RestoreRankSyncState()
    {
        _rankSync.RestoreSourceIdentity(
            PlayerPrefs.GetString(PrefRankSyncSourceId, ""),
            PlayerPrefs.GetInt(PrefRankSyncSessionSeq, 0),
            out bool sourceIdGenerated);

        if (sourceIdGenerated)
            Debug.LogWarning($"[ChildUdpRuntimeHost] RankSync sourceId generated: {_rankSync.SourceId}");

        PlayerPrefs.SetInt(PrefRankSyncSessionSeq, _rankSync.SessionSeq);

        bool needsFreshSnapshot = _rankSync.RestorePendingState(
            PlayerPrefs.GetInt(PrefRankSyncUpdateNo, 0),
            PlayerPrefs.GetInt(PrefRankSyncConfirmed, 1) != 0,
            PlayerPrefs.GetString(PrefRankSyncPending, ""));

        if (needsFreshSnapshot)
        {
            BuildAndStorePendingSnapshot("", RankingSnapshot.TypeNone);
            Debug.LogWarning($"[ChildUdpRuntimeHost] RankSync migrated to a new update={_rankSync.UpdateNo}.");
        }
        else if (_rankSync.IsConfirmed)
        {
            PlayerPrefs.SetString(PrefRankSyncPending, "");
        }
        else if (_rankSync.HasPendingMessage)
        {
            Debug.LogWarning($"[ChildUdpRuntimeHost] RankSync update={_rankSync.UpdateNo} pending restored verbatim — will resend on connect.");
        }

        PersistRankSyncState();
    }

    public void NotifyRankingChanged(string latestResultType)
    {
        string type = string.IsNullOrEmpty(latestResultType) ? RankingSnapshot.TypeNone : latestResultType;
        string latestPlayId = (type == RankingSnapshot.TypeNone) ? "" : (_session.PlaySessionId ?? "");

        if (type != RankingSnapshot.TypeNone && string.IsNullOrEmpty(latestPlayId))
        {
            Debug.LogWarning("[ChildUdpRuntimeHost] NotifyRankingChanged: playId is empty — sending snapshot with latestType=NONE.");
            type = RankingSnapshot.TypeNone;
        }

        _rankSync.BeginNewUpdate();
        BuildAndStorePendingSnapshot(latestPlayId, type);
        PersistRankSyncState();

        if (ShowDebug)
            Debug.Log($"[ChildUdpRuntimeHost] RankSync update={_rankSync.UpdateNo} latest={type}:{latestPlayId} queued.");

        StartRankSyncResend("changed");
    }

    public void SendRankSyncOnConnect()
    {
        if (_rankSync.HasUnconfirmedPending)
        {
            StartRankSyncResend("reconnect");
            return;
        }

        _rankSync.BeginNewUpdate();
        BuildAndStorePendingSnapshot("", RankingSnapshot.TypeNone);
        PersistRankSyncState();

        if (ShowDebug)
            Debug.Log($"[ChildUdpRuntimeHost] RankSync update={_rankSync.UpdateNo} sent on connect.");

        StartRankSyncResend("connect");
    }

    private string BuildAndStorePendingSnapshot(string latestPlayId, string latestResultType)
    {
        GameDifficulty.SyncActiveDifficultyRankingToMirrorKeys(false);
        int goLast = PlayerPrefs.GetInt(PrefKeyLastGameOverScore, 0);
        int[] goTop = ReadRankingKeys(PrefKeyGameOverRankPrefix);
        int tuLast = PlayerPrefs.GetInt(PrefKeyLastTimeUpScore, 0);
        int[] tuTop = ReadRankingKeys(PrefKeyTimeUpRankPrefix);

        return _rankSync.SetPendingSnapshotFrom(goLast, goTop, tuLast, tuTop, latestPlayId, latestResultType);
    }

    private static int[] ReadRankingKeys(string keyPrefix)
    {
        int[] r = new int[RankingSnapshot.TopSize];
        for (int i = 0; i < r.Length; i++)
            r[i] = PlayerPrefs.GetInt(keyPrefix + i, 0);
        return r;
    }

    private void StartRankSyncResend(string reason)
    {
        if (!_rankSync.HasPendingMessage) return;

        RequestChallengeIfNeeded();
        SendRankSyncHello();
        SendState(_rankSync.PendingMessage);

        if (_rankSyncResendCoroutine != null)
            StopCoroutine(_rankSyncResendCoroutine);

        _rankSyncResendCoroutine = StartCoroutine(RankSyncResendRoutine(reason));
    }

    private IEnumerator RankSyncResendRoutine(string reason)
    {
        for (int attempt = 0; attempt < RankSyncMaxAttempts; attempt++)
        {
            yield return new WaitForSecondsRealtime(RankSyncResendInterval);

            if (_rankSync.IsConfirmed || !_rankSync.HasPendingMessage)
            {
                _rankSyncResendCoroutine = null;
                yield break;
            }

            RequestChallengeIfNeeded();
            SendRankSyncHello();
            SendState(_rankSync.PendingMessage);
        }

        if (ShowDebug)
            Debug.LogWarning($"[ChildUdpRuntimeHost] RankSync update={_rankSync.UpdateNo} still unconfirmed after {RankSyncMaxAttempts} resends ({reason}).");
        _rankSyncResendCoroutine = null;
    }

    private void SendRankSyncHello()
    {
        if (!_rankSync.HasChallengeId) return;
        SendState(RankSyncHello.Format(_rankSync.ChallengeId, _rankSync.SourceId, _rankSync.SessionSeq));
    }

    private void HandleRankSyncAck(string sourceId, int updateNo)
    {
        if (!_rankSync.ShouldAcceptAck(sourceId, updateNo)) return;

        _rankSync.NoteAckAccepted();
        PersistRankSyncState();

        if (_rankSyncResendCoroutine != null) { StopCoroutine(_rankSyncResendCoroutine); _rankSyncResendCoroutine = null; }

        if (ShowDebug) Debug.Log($"[ChildUdpRuntimeHost] RankSync confirmed: update={updateNo}.");
    }

    private void PersistRankSyncState()
    {
        PlayerPrefs.SetString(PrefRankSyncSourceId, _rankSync.SourceId);
        PlayerPrefs.SetInt(PrefRankSyncUpdateNo, _rankSync.UpdateNo);
        PlayerPrefs.SetInt(PrefRankSyncConfirmed, _rankSync.IsConfirmed ? 1 : 0);
        PlayerPrefs.SetString(PrefRankSyncPending, _rankSync.PendingMessage ?? "");
        PlayerPrefs.Save();
    }

    public void EnqueueLog(LogType type, string message) => _logQueue.Enqueue(new LogEntry(type, message));

    private void ProcessLogQueue()
    {
        using var logScope = s_processLogQueueMarker.Auto();

        while (_logQueue.TryDequeue(out LogEntry entry))
        {
            switch (entry.Type)
            {
                case LogType.Warning: Debug.LogWarning(entry.Message); break;
                case LogType.Error:
                case LogType.Exception:
                case LogType.Assert: Debug.LogError(entry.Message); break;
                default: Debug.Log(entry.Message); break;
            }
        }
    }
}

public sealed class ChildUdpRuntimeHostSettings
{
    public int NormalPort = 8000;
    public int BroadcastPort = 8001;
    public int ParentReceivePort = 8002;
    public string TargetIP = "127.0.0.1";
    public float PingInterval = 1.0f;
    public float TimeoutLimit = 3.0f;
    public bool ShowDebugLogs = true;
    public string GameSceneName = "ChildLoading";
    public string TitleSceneName = "ChildTitle";
}
