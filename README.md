# Team Friday The 13th

BitSummit Game Jamで制作した、親機と子機が連携するUnityゲームです。親キャラクターに見つからないよう、子機側のアイテム取得と親機側の寝たふりを組み合わせて遊びます。

## 親機と子機

「親機」は親キャラクター側の3Dシーンと枕入力を扱う端末、「子機」はアイテム取得などのミニゲームを扱う端末です。端末としての親機・子機と、ゲーム内の母親キャラクターは別の意味です。

| 端末 | 担当する処理 |
|---|---|
| 親機 | 母親の接近・検知、ドアや庭のイベント、怪しさメーター、寝たふり、枕入力 |
| 子機 | プレイヤー移動、アイテム取得、スコア、QTE、入力ロック |
| 端末間の連携 | ゲーム開始、寝たふり中の操作制限、大きな音の通知、発見・終了、スコア通知 |

## フォルダの案内

| 場所 | 内容 |
|---|---|
| `Assets/Scenes/` | Unityシーン |
| `Assets/Scenes/MotherGame/GameScene.unity` | 親機のゲームシーン |
| `Assets/Script/MotherGameScene/` | 親機のゲーム処理 |
| `Assets/Script/ChildGameScene/` | 子機のミニゲーム処理 |
| `Assets/Script/UDP/Mother/` | 親機の通信・受信解析・デバイス文字列入力 |
| `Assets/Script/UDP/Child/` | 子機の通信 |
| `Assets/Script/Title/` | タイトル関連 |
| `Assets/Script/SceneChange/` | シーン切り替え関連 |
| `Assets/Script/Editor/` | Editor用検証ツール |
| `Arduino/` | デバイス関連コード |
| `Packages/`・`ProjectSettings/` | Unityのパッケージ・プロジェクト設定 |

## 親機のスクリプト

以下は `Assets/Script/MotherGameScene/` にあります。

| スクリプト | 役割・調べる内容 |
|---|---|
| `ParentDetection.cs` | 検知、接近速度、歩行足音、偽ドア音、母親Animatorの制御 |
| `ParentApproachController.cs` | ルート移動、到着判定、回転、位置補正、通過通知 |
| `ParentWarningSystem.cs` | 親イベントの開始・終了とルートの進行管理 |
| `ParentWarningScheduler.cs` | 親警告のスケジュール関連 |
| `DoorController.cs` | ドア制御 |
| `MotherGauge.cs` | 怪しさメーター関連 |
| `SleepingController.cs`・`SleepingManager.cs` | 寝たふり関連 |
| `SleepVisionEffectController.cs` | 寝たふり時の視覚効果関連 |
| `SuspicionHeartbeat.cs`・`SuspicionVisualFeedback.cs` | 怪しさに関する音・視覚フィードバック |
| `CatFeintController.cs` | 猫フェイントの移動・表示・演出 |
| `CaughtReactionController.cs` | 発見時の反応、ゲーム停止・終了関連 |
| `PillowSensor.cs` | USBシリアル入力、枕センサーの較正・寝たふり判定 |

速度・音・Animatorは `ParentDetection`、位置と経路は `ParentApproachController` が担当します。母親Animatorはモデル側にあり、実行時に取得します。`motherHeightOffset` はWaypointのワールドYに加算する高さ補正、`turnRotation` は回転設定です。

初回接近速度25、大きな音による突入速度140は意図した設定です。通常接近速度1.5とは用途が違います。

## 子機のスクリプト

以下は `Assets/Script/ChildGameScene/` にあります。

| 分類 | 主なスクリプト |
|---|---|
| ゲーム進行 | `GameManager.cs` |
| プレイヤー | `PlayerMove.cs`、`PlayerAnimator.cs`、`PlayerBoost.cs`、`PlayerHealth.cs`、`PlayerInputLock.cs` |
| アイテム | `Item.cs`、`ItemType.cs`、`ItemEffect.cs`、`ItemMove.cs`、`ItemSpawner.cs`、`ItemPool.cs`、`ItemMagnet.cs` |
| QTE | `QTEManager.cs` |
| フィーバー・ゲージ | `FeverGaugeUI.cs`、`FeverLoopEffect.cs`、`GaugeSceneChanger.cs` |
| 音 | `AudioManager.cs`、`BGMController.cs`、`BgmVolumeSync.cs`、`SeVolumeSync.cs` |
| 画面・メニュー | `BackgroundScroll.cs`、`AspectRatioController.cs`、`MenuNavigationController (1).cs` |

移動入力は `PlayerMove`、寝たふりによる操作制限は `PlayerInputLock`、アイテムの出現と再利用は `ItemSpawner`・`ItemPool` を入口に確認してください。

## UDP通信

| スクリプト | 場所・役割 |
|---|---|
| `ParentUdpSender.cs` | `Assets/Script/UDP/Mother/`：親機の送受信・接続状態管理 |
| `ParentUdpMessageParser.cs` | 同上：受信文字列の解析 |
| `ChildUdpReceiver.cs` | `Assets/Script/UDP/Child/`：子機の送受信・親機探索 |
| `ArduinoInputListener.cs` | `Assets/Script/UDP/Mother/`：`PillowSensor.OnRawLine`の文字列を処理。シリアルポートを別途開かない |

スクリプトの既定ポートは次のとおりです。実際の値はSceneのInspector設定も確認してください。

| 用途 | UDPポート |
|---|---:|
| 親機から子機への通常通信 | 8000 |
| 親機探索用ブロードキャスト | 8001 |
| 子機から親機への通常通信 | 8002 |

子機は `255.255.255.255` に `TEAM13_DISCOVERY_REQUEST` を送信します。親機は送信元IPを取得して `TEAM13_DISCOVERY_ACCEPT` を返し、子機はその応答の送信元から親機IPを取得します。

### 接続手順

1. 親機と子機を、ブロードキャストが届く同じローカルネットワークに接続します。
2. それぞれの端末で対応するゲームを起動します。
3. 親機の接続操作で待ち受け状態にし、子機の接続操作で探索を開始します。
4. 双方の接続状態・ログで接続成功を確認してからゲームを開始します。
5. 接続しない場合は、両端末のポート設定、OSのUDP許可、ネットワークの端末間通信制限、同じポートを使う別プロセスを確認します。

### 主な通信内容

- `TEAM13_START_GAME`：ゲーム開始
- `TEAM13_PING`：接続維持確認
- `SLEEP_LOCK`・`SLEEP_UNLOCK`：寝たふりに応じた子機操作の制限・解除
- `LOUD_ITEM`：子機から大きな音を伴うアイテムの通知
- `TEAM13_CAUGHT`：親に見つかった通知
- `TEAM13_CHILD_SCORE:<val>`：子機のスコア通知

接頭辞と各メッセージの組み立て・解析は通信スクリプトを参照してください。識別子を変える場合は送信側と受信側の両方を合わせる必要があります。

## コントローラーと枕入力

子機の `PlayerMove` はUnity Input Systemの `Gamepad.current` と `Keyboard.current` を使用します。具体的なキー・ボタン対応は `PlayerMove`、`PlayerBoost`、`QTEManager` と関連入力設定を確認してください。

枕コントローラーはUSBシリアル経由でESP32のロードセル値を親機へ送ります。`PillowSensor` が値を読み取り、自動較正した基準値との差から寝たふりを検出します。判定には `onThreshold`・`offThreshold` を使います。

1. デバイスを親機端末へUSB接続します。
2. `PillowSensor` のポート名・ボーレートを接続デバイスに合わせます。
3. シリアルモニターなど、同じポートを占有する別アプリを閉じます。
4. 較正後の入力値と寝たふり判定を確認します。
5. 子機接続中は、寝たふりに連動した操作ロックと解除を確認します。

シリアルポートの所有者は `PillowSensor` です。`ArduinoInputListener` は受信イベントを使うため、同じポートを二重に開かないでください。

## Unityで開く

Unity Hubからリポジトリのルートをプロジェクトとして追加します。Unityのバージョンは `ProjectSettings/ProjectVersion.txt`、必要なパッケージは `Packages/` を参照してください。

親機シーンの入口は `Assets/Scenes/MotherGame/GameScene.unity` です。タイトルからの遷移とビルド対象シーンは `Title`・`SceneChange` のスクリプトとUnityのビルド設定を確認してください。

## 検証用ツール

`Assets/Script/Editor/MotherHeightOffsetVerifier.cs` は、母親の高さ補正と到達位置を調べるEditor用ツールです。ゲーム本体の操作スクリプトではありません。EditModeで使用し、実際の移動・音・通信の確認はPlay Modeと接続した実機で行います。
