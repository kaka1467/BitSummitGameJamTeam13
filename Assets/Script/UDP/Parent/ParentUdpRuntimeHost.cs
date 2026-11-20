using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

/// <summary>
/// ParentUdpRuntimeHost：親機UDPランタイムの実行所有者。
/// </summary>
public sealed class ParentUdpRuntimeHost : MonoBehaviour
{
    public enum RuntimeState
    {
        Created = 0,
        Configured = 1,
        Running = 2,
        Stopped = 3,
    }

    public static ParentUdpRuntimeHost Running { get; private set; }

    public RuntimeState State { get; private set; } = RuntimeState.Created;
    public bool IsConfigured => State == RuntimeState.Configured || State == RuntimeState.Running;
    public bool IsRunning => State == RuntimeState.Running;

    private ParentUdpRuntimeHostSettings _settingsFallback;
    private Func<ParentUdpRuntimeHostSettings> _settingsProvider;

    private ParentUdpRuntimeHostSettings _settings => _settingsProvider != null ? _settingsProvider() : _settingsFallback;
    private UdpChannel _channel;
    private ParentConnectionLink _link;
    private ParentPlaySession _session;
    private ParentNotificationHub _notifications;
    private ParentRankSyncCoordinator _rankSync;

    public ParentConnectionLink Link => _link;
    public ParentPlaySession Session => _session;
    public ParentNotificationHub Notifications => _notifications;
    public ParentRankSyncCoordinator RankSync => _rankSync;
    public ParentUdpRuntimeHostSettings Settings => _settings;

    private readonly ConcurrentQueue<string> _receiveQueue = new ConcurrentQueue<string>();
    private readonly ConcurrentQueue<UdpChannel.Issue> _issueQueue = new ConcurrentQueue<UdpChannel.Issue>();
    private readonly ConcurrentQueue<LogEntry> _logQueue = new ConcurrentQueue<LogEntry>();

    private const string MagicNumber = "TEAM13_";

    private const string PrefRankSyncData = "RankSync_Data";
    private const string PrefRankSyncAt = "RankSync_ReceivedAt";

    private readonly ConcurrentQueue<Action> _actionQueue = new ConcurrentQueue<Action>();

    private Coroutine _challengeResendCoroutine;
    private Coroutine _heartbeatCoroutine;
    private bool _sceneLoadedSubscribed;
    private bool _sequenceDoneAcked;
    private bool _returningToTitle;
    private float _returnToTitleHeld;
    private int _lastPeerReturnToTitleSeq;
    private Coroutine _caughtRetryCoroutine;
    private Coroutine _timeUpRetryCoroutine;
    private Coroutine _sequenceDoneRetryCoroutine;
    private Coroutine _returnToTitleNotifyCoroutine;
    private int _returnToTitleSendSeq;
    private bool _isMotherPeakActive;

    public bool IsMotherPeakActive => _isMotherPeakActive;

    private readonly List<Coroutine> _transitionCoroutines = new List<Coroutine>();

    private Func<bool> _showDebugProvider;

    public void BindShowDebugProvider(Func<bool> provider) => _showDebugProvider = provider;
    public void BindSettingsProvider(Func<ParentUdpRuntimeHostSettings> provider) => _settingsProvider = provider;

    private struct LogEntry
    {
        public LogType Type;
        public string Message;

        public LogEntry(LogType type, string message)
        {
            Type = type;
            Message = message;
        }
    }

    private void Awake()
    {
    }

    private void Update()
    {
        if (!IsRunning) return;

        DrainChannelQueues();

        while (_actionQueue.TryDequeue(out Action action))
            action();

        ProcessReceiveQueue();
        ProcessLogQueue();

        HandleReturnToTitleKey();

        ParentNotificationHub.PendingOutcome loudPendingOutcome =
            _notifications.ConsumePendingLoudItem(() => ParentSceneBoundaryPolicy.IsGameplayScene(SceneManager.GetActiveScene().name, _settings.GameplaySceneName));
        if (ShowDebug && loudPendingOutcome == ParentNotificationHub.PendingOutcome.Notified)
            Debug.Log("[ParentUdpRuntimeHost] 保留していた LOUD_ITEM を購読者へ通知します（メインスレッド）。");

        ParentNotificationHub.PendingOutcome progressPendingOutcome =
            _notifications.ConsumePendingGameProgress(() => ParentSceneBoundaryPolicy.IsGameplayScene(SceneManager.GetActiveScene().name, _settings.GameplaySceneName));
        if (ShowDebug && progressPendingOutcome == ParentNotificationHub.PendingOutcome.Notified)
            Debug.Log("[ParentUdpRuntimeHost] 保留していた GAME_PROGRESS を購読者へ通知します（メインスレッド・最新値のみ）。");

        if (_link.IsTimedOut(Time.time, _settings.TimeoutLimit))
        {
            string sceneName = SceneManager.GetActiveScene().name;
            if (ParentSceneBoundaryPolicy.IsLoadingScene(sceneName, _settings.GameSceneName))
            {
                _link.NoteReceive(Time.time);
            }
            else
            {
                _link.Disconnect();
                Debug.LogWarning("[ParentUdpRuntimeHost] Connection timed out — child heartbeat lost.");
            }
        }

        if (_link.IsConnected && _heartbeatCoroutine == null)
        {
            _heartbeatCoroutine = StartCoroutine(HeartbeatCoroutine());
        }
        else if (!_link.IsConnected && _heartbeatCoroutine != null)
        {
            StopCoroutine(_heartbeatCoroutine);
            _heartbeatCoroutine = null;
        }
    }

    private void OnDestroy()
    {
        ShutdownRuntime("破棄");
    }

    public bool Initialize(ParentUdpRuntimeHostSettings settings, out string error)
    {
        if (State == RuntimeState.Running || State == RuntimeState.Stopped)
        {
            error = $"Initialize は状態 {State} では受け付けません（稼働中・停止済みは再初期化しない）。";
            return false;
        }

        if (settings == null)
        {
            error = "設定が null です。";
            return false;
        }

        _settingsFallback = settings;

        _link = new ParentConnectionLink();
        _session = new ParentPlaySession();
        _notifications = new ParentNotificationHub();
        _rankSync = new ParentRankSyncCoordinator();

        _link.StateChanged += HandleLinkStateChanged;

        State = RuntimeState.Configured;
        error = "";
        return true;
    }

    public bool TryStartRuntime(out string error)
    {
        if (State == RuntimeState.Running)
        {
            error = "既に稼働中です（同じ個体への二重起動は行いません）。";
            return false;
        }

        if (!IsConfigured)
        {
            error = $"設定が揃っていません（状態 {State}）。Initialize を先に成功させてください。";
            return false;
        }

        if (Running != null && Running != this)
        {
            error = "別の個体が稼働中です（稼働個体は1つだけ。相手のリソースには触れません）。";
            return false;
        }

        UdpChannel channel = new UdpChannel();
        if (!channel.TryOpen(_settings.BroadcastPort, _settings.ParentReceivePort, out string openError))
        {
            channel.Dispose();
            error = $"ソケットを開けませんでした: {openError}";
            return false;
        }

        _channel = channel;

        RestoreRankSyncFromPrefs();

        Running = this;
        State = RuntimeState.Running;

        if (!_sceneLoadedSubscribed)
        {
            SceneManager.sceneLoaded += OnSceneLoadedPrepared;
            _sceneLoadedSubscribed = true;
        }
        error = "";
        return true;
    }

    private Coroutine StartTrackedTransition(IEnumerator routine)
    {
        Coroutine handle = null;

        IEnumerator Tracked()
        {
            yield return routine;
            if (handle != null) _transitionCoroutines.Remove(handle);
        }

        handle = StartCoroutine(Tracked());
        _transitionCoroutines.Add(handle);
        return handle;
    }

    private void StopTransitionCoroutines()
    {
        for (int i = 0; i < _transitionCoroutines.Count; i++)
        {
            Coroutine handle = _transitionCoroutines[i];
            if (handle != null) StopCoroutine(handle);
        }
        _transitionCoroutines.Clear();
    }

    public void ShutdownRuntime(string reason)
    {
        bool firstShutdown = State != RuntimeState.Stopped;
        State = RuntimeState.Stopped;

        StopTransitionCoroutines();

        if (_challengeResendCoroutine != null)
        {
            StopCoroutine(_challengeResendCoroutine);
            _challengeResendCoroutine = null;
        }

        if (_heartbeatCoroutine != null)
        {
            StopCoroutine(_heartbeatCoroutine);
            _heartbeatCoroutine = null;
        }

        if (_caughtRetryCoroutine != null)
        {
            StopCoroutine(_caughtRetryCoroutine);
            _caughtRetryCoroutine = null;
        }

        if (_timeUpRetryCoroutine != null)
        {
            StopCoroutine(_timeUpRetryCoroutine);
            _timeUpRetryCoroutine = null;
        }

        if (_returnToTitleNotifyCoroutine != null)
        {
            StopCoroutine(_returnToTitleNotifyCoroutine);
            _returnToTitleNotifyCoroutine = null;
        }

        if (_sequenceDoneRetryCoroutine != null)
        {
            StopCoroutine(_sequenceDoneRetryCoroutine);
            _sequenceDoneRetryCoroutine = null;
        }

        if (_sceneLoadedSubscribed)
        {
            SceneManager.sceneLoaded -= OnSceneLoadedPrepared;
            _sceneLoadedSubscribed = false;
        }

        if (_link != null) _link.StateChanged -= HandleLinkStateChanged;

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

        if (firstShutdown)
        {
            Debug.Log($"[ParentUdpRuntimeHost] 稼働を終了しました（理由={reason}）。");
        }
    }

    public IEnumerator LoadSceneAfterBgmFade(string sceneName)
    {
        return ParentSceneTransitionRunner.LoadSceneAfterBgmFade(
            sceneName,
            () => _settings != null && _settings.ShowDebugLogs,
            this,
            $"host={name}, sessionId={(_session != null ? _session.PlaySessionId : "")}");
    }

    private void DrainChannelQueues()
    {
        if (_channel == null) return;

        while (_channel.TryDequeueReceived(out UdpChannel.Datagram datagram))
        {
            if (datagram.Route == UdpChannelRoute.Discovery)
            {
                EnqueueLog(LogType.Log, $"[ParentUdpRuntimeHost] Broadcast received: '{datagram.Text}' from {datagram.RemoteEndPoint.Address}");

                ParentUdpMessage message = ParentUdpMessageParser.Parse(datagram.Text);
                if (message.Type == ParentMessageType.DiscoveryRequest)
                {
                    string senderIP = datagram.RemoteEndPoint.Address.ToString();
                    EnqueueLog(LogType.Log, $"[ParentUdpRuntimeHost] DISCOVERY_REQUEST from {senderIP} — queuing targetIP update and DISCOVERY_ACCEPT.");

                    _actionQueue.Enqueue(() => HandleDiscoveryRequestPrepared(message.PlaySessionId, message.Score, senderIP));
                }

                continue;
            }

            if (ParentUdpMessageParser.Parse(datagram.Text).Type != ParentMessageType.Ping)
            {
                EnqueueLog(LogType.Log, $"[ParentUdpRuntimeHost] Normal received: '{datagram.Text}' from {datagram.RemoteEndPoint.Address}");
            }

            _receiveQueue.Enqueue(datagram.Text);
        }

        while (_channel.TryDequeueIssue(out UdpChannel.Issue issue))
        {
            string label = issue.Route == UdpChannelRoute.Discovery ? "ReceiveDiscovery" : "ReceiveNormalData";
            EnqueueLog(LogType.Error, $"[ParentUdpRuntimeHost] {label} error: {issue.Message}");
        }
    }

    private void ProcessReceiveQueue()
    {
        while (_receiveQueue.TryDequeue(out string raw))
            DispatchMessage(raw);
    }

    private void DispatchMessage(string raw)
    {
        if (raw != null)
        {
            if (raw.StartsWith(MagicNumber, StringComparison.Ordinal))
            {
                string payload = raw.Substring(MagicNumber.Length);
                if (payload.StartsWith(GameDifficulty.SyncPrefix, StringComparison.Ordinal))
                {
                    _link.NoteReceive(Time.time);
                    GameDifficulty.ApplyRemoteSettingsSync(payload);
                    return;
                }
                if (payload.StartsWith(GameDifficulty.TitleCmdPrefix, StringComparison.Ordinal))
                {
                    _link.NoteReceive(Time.time);
                    GameDifficulty.HandleRemoteTitleCommandOnParent(payload);
                    return;
                }
                if (payload == "TUTORIAL_NEXT" || payload == "TUTORIAL_OK")
                {
                    _link.NoteReceive(Time.time);
                    if (ParentGameTimeManager.Instance != null)
                    {
                        ParentGameTimeManager.Instance.NotifyRemoteTutorialNext();
                    }
                    return;
                }
            }

            const string ackToken = "SEQUENCE_DONE_ACK:";
            int ackIndex = raw.IndexOf(ackToken, StringComparison.Ordinal);
            if (ackIndex >= 0)
            {
                HandleSequenceDoneAckPrepared(raw.Substring(ackIndex + ackToken.Length).Trim());
                return;
            }
        }

        ParentUdpMessage message = ParentUdpMessageParser.Parse(raw);
        if (message.Type == ParentMessageType.Invalid)
        {
            if (!string.IsNullOrEmpty(message.ParseError))
                Debug.LogWarning(message.ParseError);
            return;
        }

        if (message.Type != ParentMessageType.Ping && _settings != null && _settings.ShowDebugLogs)
            Debug.Log($"[ParentUdpRuntimeHost] Dispatch: '{message.RawPayload}' | scene='{SceneManager.GetActiveScene().name}' | loudItemSubscribers={_notifications.HasLoudItemSubscriber} | gameProgressSubscribers={_notifications.HasGameProgressSubscriber} | resultProcessed={_session.ResultProcessed}");

        _link.NoteReceive(Time.time);

        switch (message.Type)
        {
            case ParentMessageType.Ping:
                TryRecoverConnectionFromPing();
                break;

            case ParentMessageType.RankSyncHello:
                HandleRankSyncHelloPrepared(message.Hello);
                break;

            case ParentMessageType.LoudItem:
                HandleLoudItemPrepared();
                break;

            case ParentMessageType.GameAudio:
                HandleGameAudioPrepared(message.GameAudio);
                break;

            case ParentMessageType.GameProgress:
                HandleGameProgressPrepared(message.Score);
                break;

            case ParentMessageType.LoadingComplete:
                HandleLoadingCompletePrepared(message.PlaySessionId);
                break;

            case ParentMessageType.StartGame:
                HandleStartGamePrepared(message.PlaySessionId);
                break;

            case ParentMessageType.TimeUp:
            case ParentMessageType.ChildDead:
                HandleTimeUpPrepared(message.RawPayload);
                break;

            case ParentMessageType.TeamReturnToTitle:
                HandleTeamReturnToTitlePrepared(message.PlaySessionId, message.Score, message.ReturnToTitleFast);
                break;

            case ParentMessageType.ChildScore:
                Debug.LogWarning("[ParentUdpRuntimeHost] Legacy CHILD_SCORE received — ignored.");
                break;

            case ParentMessageType.Unknown:
                Debug.Log($"[ParentUdpRuntimeHost] Unhandled message: '{message.RawPayload}'");
                break;

            case ParentMessageType.RankingSync:
                HandleRankSyncPrepared(message.Snapshot);
                break;

            default:
                Debug.Log($"[ParentUdpRuntimeHost] Unhandled message: '{message.RawPayload}'");
                break;
        }
    }

    public bool HandleSequenceDoneAckPrepared(string playId)
    {
        if (!string.IsNullOrEmpty(playId) && !string.IsNullOrEmpty(_session.PlaySessionId) && playId != _session.PlaySessionId)
        {
            Debug.Log($"[ParentUdpRuntimeHost] SEQUENCE_DONE_ACK ignored（別プレイのACK）: playId='{playId}' / current='{_session.PlaySessionId}'");
            return false;
        }

        _sequenceDoneAcked = true;
        Debug.Log($"[ParentUdpRuntimeHost] SEQUENCE_DONE_ACK を受信しました（playId='{playId}'）。");
        return true;
    }

    public void NotifyGameOverSequenceCompletedPrepared(string reason)
    {
        if (!IsRunning) return;

        string message = $"SEQUENCE_DONE:{_session.PlaySessionId}:{reason}";
        _sequenceDoneAcked = false;

        if (_sequenceDoneRetryCoroutine != null)
        {
            StopCoroutine(_sequenceDoneRetryCoroutine);
            _sequenceDoneRetryCoroutine = null;
        }

        _sequenceDoneRetryCoroutine = StartCoroutine(SequenceDoneRetryRoutine(message));

        Debug.Log($"[ParentUdpRuntimeHost] 演出完了通知の送信を開始します: '{message}' → {_link.TargetIP}:{_settings.NormalPort}（ACKまで再送）");
    }

    private IEnumerator SequenceDoneRetryRoutine(string message)
    {
        const int retryCount = 8;
        const float interval = 0.25f;

        for (int i = 0; i < retryCount; i++)
        {
            if (_sequenceDoneAcked)
            {
                Debug.Log($"[ParentUdpRuntimeHost] SEQUENCE_DONE のACKを受領したため再送を終了します（送信 {i} 回）。");
                break;
            }

            SendState(message);
            yield return new WaitForSecondsRealtime(interval);
        }

        _sequenceDoneRetryCoroutine = null;
    }

    private void HandleStartGamePrepared(string playSessionId)
    {
        ParentPlaySession.StartGameDecision startDecision =
            _session.AcceptStartGame(playSessionId, _link.IsConnected);

        if (startDecision == ParentPlaySession.StartGameDecision.AcceptNewPlay ||
            startDecision == ParentPlaySession.StartGameDecision.AcceptRepeatOfAcceptedPlay)
        {
            SetMotherPeakActive(false, forceSend: true);

            if (startDecision == ParentPlaySession.StartGameDecision.AcceptNewPlay)
                ResetChildLoadingCompleteRecordPrepared("START_GAME受理");

            if (ShowDebug)
                Debug.Log($"[ParentUdpRuntimeHost] Received START_GAME from child — loading game scene. playSessionId={_session.PlaySessionId}");

            StartTrackedTransition(LoadSceneAfterBgmFade(_settings.GameSceneName));
        }
        else if (ShowDebug)
        {
            Debug.Log($"[ParentUdpRuntimeHost] START_GAME 分岐=開始しない — 条件不成立: gameStarted={_session.GameStarted}, connected={_link.IsConnected}");
        }
    }

    private void HandleTimeUpPrepared(string rawPayload)
    {
        SetMotherPeakActive(false, forceSend: true);

        if (!_session.ResultProcessed)
        {
            _session.ResultProcessed = true;
            Debug.Log($"[ParentUdpRuntimeHost] Received {rawPayload} — TIME_UP result. Loading {_settings.TimeUpSceneName}.");
            ParentSceneTransitionRunner.LoadSceneNow(_settings.TimeUpSceneName);
        }
        else if (ShowDebug)
        {
            Debug.Log($"[ParentUdpRuntimeHost] {rawPayload} result ignored — result already processed.");
        }
    }

    private void HandleTeamReturnToTitlePrepared(string playSessionId, int seq, bool fast)
    {
        bool sessionMatches = !string.IsNullOrEmpty(playSessionId)
            ? playSessionId == _session.PlaySessionId
            : string.IsNullOrEmpty(_session.PlaySessionId);

        if (!sessionMatches)
        {
            if (ShowDebug)
                Debug.Log($"[ParentUdpRuntimeHost] RETURN_TO_TITLE ignored — different play session (recv='{playSessionId}', current='{_session.PlaySessionId}').");
            return;
        }

        if (seq <= _lastPeerReturnToTitleSeq)
        {
            if (ShowDebug)
                Debug.Log($"[ParentUdpRuntimeHost] RETURN_TO_TITLE ignored — stale/duplicate seq={seq} (last processed={_lastPeerReturnToTitleSeq}).");
            return;
        }
        _lastPeerReturnToTitleSeq = seq;

        string currentScene = SceneManager.GetActiveScene().name;
        if (ParentSceneBoundaryPolicy.IsTitleScene(currentScene, _settings.MotherTitleSceneName, _settings.TitleSceneName))
        {
            if (ShowDebug)
                Debug.Log("[ParentUdpRuntimeHost] RETURN_TO_TITLE ignored — already on title scene.");
            return;
        }

        SetMotherPeakActive(false, forceSend: true);

        ResultSceneChamger changer = FindFirstObjectByType<ResultSceneChamger>();
        if (changer != null)
        {
            if (changer.IsReturningToTitle)
            {
                if (ShowDebug)
                    Debug.Log("[ParentUdpRuntimeHost] RETURN_TO_TITLE ignored — already returning to title (fade in progress).");
                return;
            }

            if (ShowDebug)
                Debug.Log($"[ParentUdpRuntimeHost] RETURN_TO_TITLE received — starting result fade to title (fast={fast}).");
            changer.StartFadeToTitle(notifyPeer: false, skipWaits: fast);
            return;
        }

        Debug.Log("[ParentUdpRuntimeHost] RETURN_TO_TITLE received — returning to title immediately.");
        ReturnToTitlePrepared(notifyPeer: false);
    }

    public void ReturnToTitlePrepared(bool notifyPeer)
    {
        if (!IsRunning) return;
        if (_returningToTitle) return;
        _returningToTitle = true;
        _returnToTitleHeld = 0f;

        SetMotherPeakActive(false, forceSend: true);

        Debug.Log($"[ParentUdpRuntimeHost] ReturnToTitle — '{SceneManager.GetActiveScene().name}' から '{_settings.MotherTitleSceneName}' へ戻ります。");

        Time.timeScale = 1f;

        if (notifyPeer)
        {
            NotifyReturnToTitleToChildPrepared(fast: false);
        }

        ParentSceneTransitionRunner.LoadSceneNowResolved(_settings.MotherTitleSceneName);
    }

    public void NotifyReturnToTitleToChildPrepared(bool fast)
    {
        if (!IsRunning) return;

        string message = NotifyReturnToTitleMessagePrepared(fast);
        Debug.Log($"[ParentUdpRuntimeHost] Sent RETURN_TO_TITLE (playSessionId={_session.PlaySessionId}, seq={_returnToTitleSendSeq}, fast={fast}) to child.");

        if (_returnToTitleNotifyCoroutine != null)
        {
            StopCoroutine(_returnToTitleNotifyCoroutine);
        }
        _returnToTitleNotifyCoroutine = StartCoroutine(ReturnToTitleNotifyRoutine(message));
    }

    public string NotifyReturnToTitleMessagePrepared(bool fast)
    {
        _returnToTitleSendSeq++;

        string message = fast
            ? $"RETURN_TO_TITLE:{_session.PlaySessionId}:{_returnToTitleSendSeq}:FAST"
            : $"RETURN_TO_TITLE:{_session.PlaySessionId}:{_returnToTitleSendSeq}";

        SendState(message);
        return message;
    }

    private IEnumerator ReturnToTitleNotifyRoutine(string message)
    {
        const int retryCount = 3;
        const float retryInterval = 0.15f;

        for (int i = 0; i < retryCount; i++)
        {
            yield return new WaitForSecondsRealtime(retryInterval);
            if (ShowDebug)
                Debug.Log($"[ParentUdpRuntimeHost] Sending RETURN_TO_TITLE retry ({i + 1}/{retryCount}) — '{message}'");
            SendState(message);
        }

        _returnToTitleNotifyCoroutine = null;
    }

    private void HandleReturnToTitleKey()
    {
        if (!_settings.EnableReturnToTitleKey || _returningToTitle) return;

        Keyboard keyboard = Keyboard.current;
        if (keyboard == null || ParentSceneBoundaryPolicy.IsTitleScene(
                SceneManager.GetActiveScene().name, _settings.MotherTitleSceneName, _settings.TitleSceneName))
        {
            _returnToTitleHeld = 0f;
            return;
        }

        if (!keyboard[_settings.ReturnToTitleKey].isPressed)
        {
            _returnToTitleHeld = 0f;
            return;
        }

        _returnToTitleHeld += Time.unscaledDeltaTime;
        if (_returnToTitleHeld >= _settings.ReturnToTitleHoldSeconds)
        {
            ReturnToTitlePrepared(notifyPeer: true);
        }
    }

    private IEnumerator HeartbeatCoroutine()
    {
        while (_link.IsConnected)
        {
            try
            {
                if (_channel != null)
                {
                    byte[] data = Encoding.UTF8.GetBytes(MagicNumber + "PING");
                    _channel.Send(data, data.Length, _link.TargetIP, _settings.NormalPort);

                    byte[] peakData = Encoding.UTF8.GetBytes(MagicNumber + (_isMotherPeakActive ? "MOTHER_PEAK:1" : "MOTHER_PEAK:0"));
                    _channel.Send(peakData, peakData.Length, _link.TargetIP, _settings.NormalPort);
                }
            }
            catch (Exception e) { Debug.LogError($"[ParentUdpRuntimeHost] Heartbeat error: {e.Message}"); }

            yield return new WaitForSecondsRealtime(_settings.PingInterval);
        }
    }

    private void ResetChildLoadingCompleteRecordPrepared(string reason)
    {
        bool previous = _session.SetChildLoadingComplete(false);
        Debug.Log($"[ParentUdpRuntimeHost] 現在プレイの受信記録(ChildLoadingComplete)を初期化 — {previous} → false, 理由={reason}");
    }

    private void OnSceneLoadedPrepared(Scene scene, LoadSceneMode mode)
    {
        _returningToTitle = false;
        _returnToTitleHeld = 0f;

        if (ShowDebug)
            Debug.Log($"[ParentUdpRuntimeHost] Scene loaded: '{scene.name}' — refreshing scene references.");

        if (ParentSceneBoundaryPolicy.IsTitleScene(scene.name, _settings.MotherTitleSceneName, _settings.TitleSceneName))
        {
            ResetForNewSessionPrepared();
            if (ShowDebug)
                Debug.Log($"[ParentUdpRuntimeHost] Title scene loaded ('{scene.name}') — reset state for next round.");
            return;
        }

        if (ParentSceneBoundaryPolicy.IsNewSessionScene(scene.name, _settings.GameSceneName, _settings.SoloGameSceneName))
        {
            SetMotherPeakActive(false, forceSend: true);
            _session.ResetResultProcessed();
            _notifications.ClearPending();

            if (_caughtRetryCoroutine != null)
            {
                StopCoroutine(_caughtRetryCoroutine);
                _caughtRetryCoroutine = null;
            }

            if (_timeUpRetryCoroutine != null)
            {
                StopCoroutine(_timeUpRetryCoroutine);
                _timeUpRetryCoroutine = null;
            }

            if (_sequenceDoneRetryCoroutine != null)
            {
                StopCoroutine(_sequenceDoneRetryCoroutine);
                _sequenceDoneRetryCoroutine = null;
            }

            _sequenceDoneAcked = false;

            if (ShowDebug)
                Debug.Log("[ParentUdpRuntimeHost] resultProcessed reset for new game session.");
        }
        else
        {
            SetMotherPeakActive(false, forceSend: true);
            _notifications.ClearPending();
        }
    }

    public void ResetForNewSessionPrepared()
    {
        SetMotherPeakActive(false, forceSend: true);
        _link.ResetForNewSession();
        _session.ResetForNewSession();
        ResetChildLoadingCompleteRecordPrepared("タイトル復帰");
        _notifications.ClearPending();
        _lastPeerReturnToTitleSeq = 0;

        if (_heartbeatCoroutine != null)
        {
            StopCoroutine(_heartbeatCoroutine);
            _heartbeatCoroutine = null;
        }

        if (_caughtRetryCoroutine != null)
        {
            StopCoroutine(_caughtRetryCoroutine);
            _caughtRetryCoroutine = null;
        }

        if (_timeUpRetryCoroutine != null)
        {
            StopCoroutine(_timeUpRetryCoroutine);
            _timeUpRetryCoroutine = null;
        }

        if (_returnToTitleNotifyCoroutine != null)
        {
            StopCoroutine(_returnToTitleNotifyCoroutine);
            _returnToTitleNotifyCoroutine = null;
        }

        if (_sequenceDoneRetryCoroutine != null)
        {
            StopCoroutine(_sequenceDoneRetryCoroutine);
            _sequenceDoneRetryCoroutine = null;
        }

        if (_challengeResendCoroutine != null)
        {
            StopCoroutine(_challengeResendCoroutine);
            _challengeResendCoroutine = null;
        }

        _sequenceDoneAcked = false;

        Debug.Log("[ParentUdpRuntimeHost] ResetForNewSession: session flags cleared.");
    }

    private void HandleDiscoveryRequestPrepared(string childSourceId, int childSessionSeq, string senderIP)
    {
        _link.SetTargetIP(senderIP);
        _link.NoteReceive(Time.time);
        _session.GameStarted = false;

        RankSyncPeerGate.DiscoveryResult dr = _rankSync.OnDiscovery(childSourceId, childSessionSeq, out string challengeId);

        if (dr == RankSyncPeerGate.DiscoveryResult.NewChallenge)
        {
            _rankSync.ResetSyncVerifiedForNewChallenge();
            NotifySyncStateChanged();
        }

        if (!string.IsNullOrEmpty(challengeId))
        {
            SendRankSyncChallenge(senderIP, challengeId);
            _rankSync.NoteChallengeSent();
            StartChallengeResend(senderIP, challengeId);
        }

        if (ShowDebug)
            Debug.Log($"[ParentUdpRuntimeHost] Discovery({dr}) — challenge='{challengeId}', confirmed='{_rankSync.ConfirmedPeerSourceId}', childSource='{childSourceId}', sessionSeq={childSessionSeq}.");

        SendDiscoveryAccept(senderIP);

        _link.SetState(ParentConnectionLink.LinkState.Connected);
        GameDifficulty.SendSettingsSyncToPeer();
        SendMotherPeakStateNow();
    }

    private void HandleLinkStateChanged(ParentConnectionLink.LinkState state)
        => RaiseConnectionStateChanged(state);

    private void TryRecoverConnectionFromPing()
    {
        if (_link.State != ParentConnectionLink.LinkState.Disconnected) return;
        if (ParentSceneBoundaryPolicy.IsTitleScene(SceneManager.GetActiveScene().name, _settings.MotherTitleSceneName, _settings.TitleSceneName)) return;

        _link.SetState(ParentConnectionLink.LinkState.Connected);
        SendMotherPeakStateNow();
        Debug.LogWarning("[ParentUdpRuntimeHost] PING resumed after timeout — connection restored.");
    }

    private void NotifySyncStateChanged()
    {
        if (!_rankSync.ShouldNotifySyncState()) return;

        RankSyncDisplayState previous = _rankSync.CommitSyncState();
        RankSyncDisplayState state = _rankSync.GetDisplayState();

        if (ShowDebug)
            Debug.Log($"[ParentUdpRuntimeHost] Sync display state {previous} → {state} — notifying ResultScoreUI.");

        RaiseResultDataCommitted($"sync state {previous} → {state}");
    }

    private void SendRankSyncChallenge(string ip, string challengeId)
    {
        if (!IsRunning || _channel == null) return;
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(MagicNumber + RankSyncChallenge.Format(challengeId));
            _channel.Send(data, data.Length, ip, _settings.NormalPort);
            if (ShowDebug)
                Debug.Log($"[ParentUdpRuntimeHost] Sent RANK_SYNC_CHALLENGE '{challengeId}' to {ip}:{_settings.NormalPort}.");
        }
        catch (Exception e) { Debug.LogError($"[ParentUdpRuntimeHost] SendRankSyncChallenge error: {e.Message}"); }
    }

    private void SendDiscoveryAccept(string ip)
    {
        if (!IsRunning || _channel == null) return;
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(MagicNumber + "DISCOVERY_ACCEPT");
            _channel.Send(data, data.Length, ip, _settings.NormalPort);
            Debug.Log($"[ParentUdpRuntimeHost] Sent DISCOVERY_ACCEPT to {ip}:{_settings.NormalPort}");
        }
        catch (Exception e) { Debug.LogError($"[ParentUdpRuntimeHost] SendDiscoveryAccept error: {e.Message}"); }
    }

    private void StartChallengeResend(string ip, string challengeId)
    {
        if (!IsRunning) return;

        if (_challengeResendCoroutine != null) StopCoroutine(_challengeResendCoroutine);
        _challengeResendCoroutine = StartCoroutine(ChallengeResendRoutine(ip, challengeId));
    }

    private IEnumerator ChallengeResendRoutine(string ip, string challengeId)
    {
        const float interval = 0.5f;

        while (IsRunning && _rankSync.HasPendingChallenge && _rankSync.PendingPeerChallengeId == challengeId)
        {
            yield return new WaitForSecondsRealtime(interval);

            if (_rankSync.ChallengeExhausted)
            {
                if (ShowDebug)
                    Debug.Log($"[ParentUdpRuntimeHost] RANK_SYNC_CHALLENGE '{challengeId}' 自動再送の上限に達しました。");
                break;
            }

            SendRankSyncChallenge(ip, challengeId);
            _rankSync.NoteChallengeSent();
        }

        _challengeResendCoroutine = null;
    }

    public void EnqueueLog(LogType type, string message) => _logQueue.Enqueue(new LogEntry(type, message));

    private void ProcessLogQueue()
    {
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

    private void HandleRankSyncHelloPrepared(RankSyncHello hello)
    {
        RankSyncPeerGate.HelloResult r = _rankSync.OnHello(hello.ChallengeId, hello.SourceId, hello.SessionSeq);

        switch (r)
        {
            case RankSyncPeerGate.HelloResult.Accepted:
                Debug.Log($"[ParentUdpRuntimeHost] RANK_SYNC_HELLO accepted — peer sourceId confirmed: '{_rankSync.ConfirmedPeerSourceId}'.");
                break;
            case RankSyncPeerGate.HelloResult.Duplicate:
                if (ShowDebug)
                    Debug.Log($"[ParentUdpRuntimeHost] RANK_SYNC_HELLO duplicate — peer sourceId stays '{_rankSync.ConfirmedPeerSourceId}'.");
                break;
            case RankSyncPeerGate.HelloResult.StaleResponse:
                Debug.LogWarning($"[ParentUdpRuntimeHost] RANK_SYNC_HELLO ignored — challenge='{hello.ChallengeId}' is not the pending one.");
                break;
            case RankSyncPeerGate.HelloResult.NoPending:
                Debug.LogWarning($"[ParentUdpRuntimeHost] RANK_SYNC_HELLO ignored — no pending challenge (sourceId='{hello.SourceId}').");
                break;
            default:
                Debug.LogWarning($"[ParentUdpRuntimeHost] RANK_SYNC_HELLO invalid.");
                break;
        }

        if (r == RankSyncPeerGate.HelloResult.Accepted)
            NotifySyncStateChanged();
    }

    private void HandleLoudItemPrepared()
    {
        if (ShowDebug)
            Debug.Log("[ParentUdpRuntimeHost] Received LOUD_ITEM network packet from Child.");

        ParentNotificationHub.LoudItemOutcome outcome = _notifications.HandleLoudItemReceived(
            () => ParentSceneBoundaryPolicy.IsGameplayScene(SceneManager.GetActiveScene().name, _settings.GameplaySceneName));

        if (ShowDebug)
            Debug.Log($"[ParentUdpRuntimeHost] LOUD_ITEM outcome={outcome}。");
    }

    private void HandleGameProgressPrepared(int rawScore)
    {
        float progressRate = Mathf.Clamp01(rawScore / 1000f);
        _notifications.HandleGameProgressReceived(progressRate);
    }

    private void HandleGameAudioPrepared(ParentGameAudioEvent evt)
    {
        string sceneName = SceneManager.GetActiveScene().name;
        bool canPlay = ParentSceneBoundaryPolicy.IsGameplayScene(sceneName, _settings.GameplaySceneName) ||
                       ParentSceneBoundaryPolicy.IsLoadingScene(sceneName, _settings.GameSceneName);

        if (!canPlay)
        {
            if (ShowDebug)
                Debug.Log("[ParentUdpRuntimeHost] GAME_AUDIO discarded — ゲーム／ロードシーンではないため再生しません。");
            return;
        }

        _notifications.HandleGameAudioReceived(evt);
    }

    private void HandleLoadingCompletePrepared(string playSessionId = "")
    {
        if (!string.IsNullOrEmpty(playSessionId) &&
            !string.IsNullOrEmpty(_session.PlaySessionId) &&
            playSessionId != _session.PlaySessionId)
        {
            if (ShowDebug)
                Debug.Log($"[ParentUdpRuntimeHost] LOADING_COMPLETE ignored — different play session (recv='{playSessionId}', current='{_session.PlaySessionId}').");
            return;
        }

        bool previous = _session.SetChildLoadingComplete(true);
        Debug.Log($"[ParentUdpRuntimeHost] Received LOADING_COMPLETE from child (playSessionId='{playSessionId}'). Property set to true.");

        if (!previous)
            RaiseLoadingCompleteReceived();
    }

    public void SendState(string message)
    {
        if (!IsRunning || _channel == null) return;
        if (!_link.IsConnected) return;

        if (ShowDebug)
            Debug.Log($"[ParentUdpRuntimeHost] → '{message}' to {_link.TargetIP}:{_settings.NormalPort} | state={_link.State}");

        try
        {
            byte[] data = Encoding.UTF8.GetBytes(MagicNumber + message);
            _channel.Send(data, data.Length, _link.TargetIP, _settings.NormalPort);
        }
        catch (Exception e)
        {
            Debug.LogError($"[ParentUdpRuntimeHost] SendState error: {e.Message}");
        }
    }

    private void HandleRankSyncPrepared(RankingSnapshot snap)
    {
        string activeScene = SceneManager.GetActiveScene().name;
        bool onTitle = ParentSceneBoundaryPolicy.IsTitleScene(activeScene, _settings.MotherTitleSceneName, _settings.TitleSceneName);

        ParentRankSyncCoordinator.SnapshotDecision decision = _rankSync.EvaluateSnapshot(snap, out RankSyncApply result);

        if (decision == ParentRankSyncCoordinator.SnapshotDecision.NotAccepted)
        {
            Debug.LogWarning($"[ParentUdpRuntimeHost] RANK_SYNC ignored — source='{snap.SourceId}' does not match the confirmed peer.");
            return;
        }

        if (result == RankSyncApply.Mismatch)
        {
            Debug.LogError($"[ParentUdpRuntimeHost] RANK_SYNC MISMATCH — source='{snap.SourceId}', update={snap.UpdateNo}.");
            return;
        }

        if (result == RankSyncApply.Duplicate || result == RankSyncApply.Stale)
        {
            if (ShowDebug)
                Debug.Log($"[ParentUdpRuntimeHost] RANK_SYNC {result} — source='{snap.SourceId}', update={snap.UpdateNo}.");

            if (result == RankSyncApply.Duplicate) _rankSync.NoteSnapshotVerified();

            SendRankSyncAck(snap);
            NotifySyncStateChanged();
            return;
        }

        PlayerPrefs.SetString(PrefRankSyncData, _rankSync.SerializeForSave());
        PlayerPrefs.SetInt(PrefRankSyncAt, (int)System.DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        PlayerPrefs.Save();

        Debug.Log($"[ParentUdpRuntimeHost] RANK_SYNC {result} — source='{_rankSync.StoreSourceId}', update={_rankSync.StoreUpdateNo}, "
                + $"GO(last={_rankSync.StoreGameOverLastScore}, top=[{string.Join(",", _rankSync.StoreGameOverTop)}]), "
                + $"TU(last={_rankSync.StoreTimeUpLastScore}, top=[{string.Join(",", _rankSync.StoreTimeUpTop)}]), "
                + $"latest={_rankSync.StoreLatestResultType}:{_rankSync.StoreLatestResultPlayId}");

        _rankSync.NoteSnapshotVerified();

        RaiseResultDataCommitted("RANK_SYNC applied");
        _rankSync.CommitSyncState();

        TryNavigateForSyncedResult(result, onTitle);

        SendRankSyncAck(snap);
    }

    private void TryNavigateForSyncedResult(RankSyncApply result, bool onTitle)
    {
        bool shouldNavigate = RankSyncNavigation.ShouldNavigate(
            result, onTitle, _rankSync.StoreMatchesConfirmedPeer(),
            _rankSync.StoreLatestResultPlayId, _rankSync.StoreLatestResultType,
            _session.PlaySessionId, _session.CaptureConfirmedPlayId);

        if (!shouldNavigate)
        {
            if (ShowDebug)
                Debug.Log($"[ParentUdpRuntimeHost] RANK_SYNC no screen transition (apply={result}, onTitle={onTitle}, latest={_rankSync.StoreLatestResultType}:{_rankSync.StoreLatestResultPlayId}, current='{_session.PlaySessionId}').");
            return;
        }

        if (_rankSync.StoreLatestResultType == RankingSnapshot.TypeTimeUp &&
            ParentGameTimeManager.Instance != null &&
            ParentGameTimeManager.Instance.IsTimeUp &&
            ParentSceneBoundaryPolicy.IsGameplayScene(SceneManager.GetActiveScene().name, _settings.GameplaySceneName))
        {
            if (ShowDebug)
                Debug.Log("[ParentUdpRuntimeHost] RANK_SYNC (TIME_UP) を保存しました。シーン遷移は親機のクリア閉眼演出完了後に行います。");
            return;
        }

        string targetScene = _rankSync.StoreLatestResultType == RankingSnapshot.TypeGameOver
            ? _settings.GameOverSceneName : _settings.TimeUpSceneName;

        if (SceneManager.GetActiveScene().name != targetScene)
        {
            ParentSceneTransitionRunner.LoadSceneNow(targetScene);
        }
    }

    private void SendRankSyncAck(RankingSnapshot snap)
        => SendState($"RANK_SYNC_ACK:{RankingSnapshot.VersionName}:{snap.SourceId}:{snap.UpdateNo}");

    public void RestoreRankSyncFromPrefs()
    {
        if (_rankSync == null) return;

        if (_rankSync.RestoreFromSaveData(PlayerPrefs.GetString(PrefRankSyncData, "")))
        {
            Debug.Log($"[ParentUdpRuntimeHost] RankSync copy restored: source='{_rankSync.StoreSourceId}', update={_rankSync.StoreUpdateNo}.");
        }
        else if (ShowDebug)
        {
            Debug.Log("[ParentUdpRuntimeHost] RankSync copy: no data (未同期).");
        }
    }

    public int RankSyncReceivedAtUnix => PlayerPrefs.GetInt(PrefRankSyncAt, 0);

    public void OnConnectButtonClicked()
    {
        if (!IsRunning) return;
        ParentConnectionLink.LinkState before = _link.State;
        _link.BeginConnecting();
        Debug.Log($"[ParentUdpRuntimeHost][Diag] OnConnectButtonClicked called — scene='{SceneManager.GetActiveScene().name}', state {before} → Connecting.");
    }

    public void OnCancelButtonClicked()
    {
        if (!IsRunning) return;
        Debug.Log($"[ParentUdpRuntimeHost][Diag] OnCancelButtonClicked called — scene='{SceneManager.GetActiveScene().name}', state {_link.State} → Disconnected.");
        _link.Disconnect();
    }

    public void OnStartButtonClicked()
    {
        if (!IsRunning) return;
        if (_session.GameStarted)
        {
            if (ShowDebug) Debug.Log("[ParentUdpRuntimeHost] OnStartButtonClicked ignored — already started.");
            return;
        }

        _session.GameStarted = true;
        StartTrackedTransition(StartGameRoutinePrepared());
    }

    private IEnumerator StartGameRoutinePrepared()
    {
        _session.PlaySessionId = ParentPlaySession.NewPlaySessionId();
        _session.ClearCaptureConfirmed();
        SetMotherPeakActive(false, forceSend: true);
        GameDifficulty.SendSettingsSyncToPeer();
        SendState($"START_GAME:{_session.PlaySessionId}");
        Debug.Log($"[ParentUdpRuntimeHost] Sent START_GAME (playSessionId={_session.PlaySessionId}) to child at {_link.TargetIP}:{_settings.NormalPort}");
        yield return new WaitForSeconds(0.1f);
        yield return LoadSceneAfterBgmFade(_settings.GameSceneName);
    }

    public void OnSoloStartButtonClicked()
    {
        if (!IsRunning) return;
        if (_session.GameStarted)
        {
            if (ShowDebug) Debug.Log("[ParentUdpRuntimeHost] OnSoloStartButtonClicked ignored — already started.");
            return;
        }

        _session.GameStarted = true;
        _session.PlaySessionId = ParentPlaySession.NewPlaySessionId();
        _session.ClearCaptureConfirmed();
        SetMotherPeakActive(false, forceSend: true);

        Debug.Log($"[ParentUdpRuntimeHost] OnSoloStartButtonClicked — starting without waiting for child connection. playSessionId={_session.PlaySessionId}");

        GameDifficulty.SendSettingsSyncToPeer();
        SendState($"START_GAME:{_session.PlaySessionId}");
        StartTrackedTransition(LoadSceneAfterBgmFade(_settings.SoloGameSceneName));
    }

    public void SendStateSLEEP_LOCK() => SendState("SLEEP_LOCK");
    public void SendStateSLEEP_UNLOCK() => SendState("SLEEP_UNLOCK");

    /// <summary>
    /// 【M-F03】親機の山場状態（接近開始〜ドア前・窓での覗き終了まで）を更新し、
    /// 状態が切り替わったタイミング（または forceSend 指定時）で子機へ MOTHER_PEAK:1 / MOTHER_PEAK:0 を送信する。
    /// </summary>
    public void SetMotherPeakActive(bool active, bool forceSend = false)
    {
        bool changed = _isMotherPeakActive != active;
        _isMotherPeakActive = active;

        if (!changed && !forceSend) return;

        SendMotherPeakStateNow();
    }

    /// <summary>
    /// 【M-F03】現在の親機の山場状態を即座に子機へ送信する。
    /// </summary>
    public void SendMotherPeakStateNow()
    {
        SendState(_isMotherPeakActive ? "MOTHER_PEAK:1" : "MOTHER_PEAK:0");
    }

    public void NotifyTimeUpFromParent()
    {
        if (!IsRunning) return;

        SetMotherPeakActive(false, forceSend: true);

        if (!string.IsNullOrEmpty(_session.PlaySessionId) &&
            _session.PlaySessionId == _session.CaptureConfirmedPlayId)
        {
            if (ShowDebug)
                Debug.Log("[ParentUdpRuntimeHost] NotifyTimeUpFromParent skipped — 既に捕獲が確定しています。");
            return;
        }

        if (_session.ResultProcessed && _timeUpRetryCoroutine != null) return;
        _session.ResultProcessed = true;

        string message = string.IsNullOrEmpty(_session.PlaySessionId)
            ? "TIME_UP"
            : $"TIME_UP:{_session.PlaySessionId}";

        SendState(message);
        Debug.Log($"[ParentUdpRuntimeHost] Sent '{message}' to child (TIME_UP).");

        if (_timeUpRetryCoroutine != null)
            StopCoroutine(_timeUpRetryCoroutine);
        _timeUpRetryCoroutine = StartCoroutine(TimeUpRetryRoutine(message));
    }

    private IEnumerator TimeUpRetryRoutine(string message)
    {
        const int retryCount = 3;
        const float retryInterval = 0.15f;

        for (int i = 0; i < retryCount; i++)
        {
            yield return new WaitForSecondsRealtime(retryInterval);
            SendState(message);
        }

        _timeUpRetryCoroutine = null;
    }

    public void NotifyGameOverFromParentCatchPrepared()
    {
        if (!IsRunning) return;

        SetMotherPeakActive(false, forceSend: true);

        if (_session.ResultProcessed && _caughtRetryCoroutine != null) return;

        _session.ResultProcessed = true;
        _session.ConfirmCaptureForCurrentPlay();

        if (ShowDebug)
            Debug.Log($"[ParentUdpRuntimeHost] NotifyGameOverFromParentCatch: 親機の捕獲によるゲームオーバー確定。CAUGHT送信・再送を開始します。");

        SendCaughtPrepared();

        if (_caughtRetryCoroutine != null)
        {
            StopCoroutine(_caughtRetryCoroutine);
        }
        _caughtRetryCoroutine = StartCoroutine(CaughtRetryRoutine());
    }

    private IEnumerator CaughtRetryRoutine()
    {
        const int retryCount = 3;
        const float retryInterval = 0.1f;

        for (int i = 0; i < retryCount; i++)
        {
            yield return new WaitForSecondsRealtime(retryInterval);
            if (ShowDebug)
                Debug.Log($"[ParentUdpRuntimeHost] Sending CAUGHT retry ({i + 1}/{retryCount})...");
            SendCaughtPrepared();
        }

        _caughtRetryCoroutine = null;
    }

    private void SendCaughtPrepared()
    {
        const string message = "TEAM13_CAUGHT";

        if (string.IsNullOrWhiteSpace(_link.TargetIP) || !System.Net.IPAddress.TryParse(_link.TargetIP, out _))
        {
            Debug.LogWarning($"[ParentUdpRuntimeHost] CAUGHT send skipped: invalid targetIP='{_link.TargetIP}', message='{message}'");
            return;
        }

        if (_channel == null || !_channel.IsOpen)
        {
            Debug.LogError($"[ParentUdpRuntimeHost] CAUGHT send failed: udp channel is not open, targetIP='{_link.TargetIP}', message='{message}'");
            return;
        }

        try
        {
            byte[] data = Encoding.UTF8.GetBytes(message);
            _channel.Send(data, data.Length, _link.TargetIP, _settings.NormalPort);
            Debug.Log($"[ParentUdpRuntimeHost] CAUGHT send succeeded: targetIP='{_link.TargetIP}', targetPort={_settings.NormalPort}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[ParentUdpRuntimeHost] CAUGHT send failed: {e.Message}");
        }
    }

    public ParentConnectionLink.LinkState ConnectionState => _link != null ? _link.State : ParentConnectionLink.LinkState.Disconnected;
    public bool IsConnected => _link != null && _link.IsConnected;
    public bool IsGameStarted => _session != null && _session.GameStarted;
    public bool ChildLoadingComplete => _session != null && _session.ChildLoadingComplete;

    public bool IsGameplayScene(string sceneName)
        => _settings != null && ParentSceneBoundaryPolicy.IsGameplayScene(sceneName, _settings.GameplaySceneName);

    public string ConnectedPeerSourceId => _rankSync != null ? _rankSync.ConfirmedPeerSourceId : "";
    public bool HasRankSyncData => _rankSync != null && _rankSync.HasStoreData;
    public int RankSyncUpdateNo => _rankSync != null ? _rankSync.StoreUpdateNo : 0;
    public string RankSyncSourceId => _rankSync != null ? _rankSync.StoreSourceId : "";
    public RankSyncDisplayState GetSyncDisplayState() => _rankSync != null ? _rankSync.GetDisplayState() : RankSyncDisplayState.NoData;

    public bool TryGetSyncedResult(bool gameOver, out int lastScore, out int[] top5)
    {
        if (_rankSync == null)
        {
            lastScore = 0;
            top5 = null;
            return false;
        }

        return _rankSync.TryGetSyncedResult(gameOver, out lastScore, out top5);
    }

    public enum RuntimeNotification
    {
        ConnectionStateChanged = 0,
        LoadingCompleteReceived = 1,
        ResultDataCommitted = 2,
    }

    public event Action<ParentConnectionLink.LinkState> ConnectionStateChanged;
    public event Action LoadingCompleteReceived;
    public event Action ResultDataCommitted;

    private bool ShowDebug => _showDebugProvider != null ? _showDebugProvider() : (_settings != null && _settings.ShowDebugLogs);

    private void LogNotification(RuntimeNotification kind, string detail)
    {
        if (!ShowDebug) return;
        Debug.Log($"[ParentUdpRuntimeHost] 通知: {kind} ({detail})。");
    }

    private void RaiseConnectionStateChanged(ParentConnectionLink.LinkState state)
    {
        LogNotification(RuntimeNotification.ConnectionStateChanged, state.ToString());
        ConnectionStateChanged?.Invoke(state);
    }

    private void RaiseLoadingCompleteReceived()
    {
        LogNotification(RuntimeNotification.LoadingCompleteReceived, "LOADING_COMPLETE");
        LoadingCompleteReceived?.Invoke();
    }

    private void RaiseResultDataCommitted(string detail)
    {
        LogNotification(RuntimeNotification.ResultDataCommitted, detail);
        ResultDataCommitted?.Invoke();
    }
}

public sealed class ParentUdpRuntimeHostSettings
{
    public int NormalPort = 8000;
    public int BroadcastPort = 8001;
    public int ParentReceivePort = 8002;
    public string TargetIP = "127.0.0.1";
    public float TimeoutLimit = 3.0f;
    public float PingInterval = 1.0f;
    public bool ShowDebugLogs = true;
    public string GameSceneName = "ParentLoading";
    public string SoloGameSceneName = "ParentGameScene";
    public string GameplaySceneName = "ParentGameScene";
    public string MotherTitleSceneName = "ParentTitle";
    public string TitleSceneName = "ChildTitle";
    public string GameOverSceneName = "ParentGameOver";
    public string TimeUpSceneName = "ParentGameClear";
    public bool EnableReturnToTitleKey = true;
    public UnityEngine.InputSystem.Key ReturnToTitleKey = UnityEngine.InputSystem.Key.Escape;
    public float ReturnToTitleHoldSeconds = 1.5f;
}
