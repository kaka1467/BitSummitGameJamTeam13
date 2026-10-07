# Team Friday The 13th

BitSummit Game Jamで制作した、親機と子機が連携するUnityゲームの開発リポジトリです。
OIGSでの展示を終え、現在は次の展示イベント「大作戦」に向けて改善を進めています。

## 開発環境

- Unity / C#
- UDP通信による親機・子機の連携
- Arduino・枕コントローラー関連の実装

Unityのバージョンは `ProjectSettings/ProjectVersion.txt` を確認してください。

## プロジェクト構成

- `Assets/`：ゲームのシーン、スクリプト、素材
- `Assets/Script/MotherGameScene/`：親機側のゲーム処理
- `Assets/Script/ChildGameScene/`：子機側のゲーム処理
- `Assets/Script/UDP/`：通信処理
- `Assets/Script/Editor/`：Editor用の検証ツール
- `Arduino/`：デバイス関連
- `Packages/`：Unityパッケージ設定
- `ProjectSettings/`：Unityプロジェクト設定

## 親機スクリプトの役割

| スクリプト | 主な役割 |
|---|---|
| `ParentWarningSystem` | 親イベントの開始・終了、ルート選択、各処理への通知 |
| `ParentDetection` | 親の検知、速度、足音、偽ドア音、Animator制御 |
| `ParentApproachController` | ルート移動、到着判定、回転、位置補正、通過イベント通知 |
| `CatFeintController` | 猫フェイント |
| `CaughtReactionController` | 見つかった時の反応、ゲームオーバー関連処理 |
| `ParentUdpSender` | 親機側のUDP通信 |
| `PillowSensor` | 枕センサーの入力処理 |

`ParentDetectionV2` は `ParentDetection` に名称を変更しました。親の速度・足音・Animator・偽ドア音の管理は `ParentDetection` にまとめています。
母親モデルのAnimatorは実行時に自動取得するため、`ParentDetection` のInspectorにAnimatorの手動指定欄はありません。

## 現在の親機設定

| 項目 | 値 |
|---|---:|
| 通常接近速度 | 1.5 |
| 高疑惑時の通常接近速度 | 1.5 |
| 疑惑による速度ボーナス | 0 |
| 初回接近速度 | 25 |
| 大きな音による突入速度 | 140 |
| 母親の高さ補正 `motherHeightOffset` | 13 |
| 回転設定 `turnRotation` | 200 |
| 通過後の偽ドア音の遅延 | 1秒 |

初回接近と大きな音による突入が速いのは意図した仕様です。通常接近の速度と混同して変更しないでください。
高さ補正は `ParentApproachController` がWaypointのワールドY座標に加算します。

## 最近の整理

- 未使用の猫フェイント用 `footstepClip` を削除
- 親警告の旧1階・2階ライト参照と専用処理を削除
- `ParentApproachController` から速度設定、足音、Animator、偽ドア音の再生処理を移動
- 旧回転フィールドを `turnRotation` に変更し、設定値を維持
- `CaughtReactionController` の未使用フェード処理を削除
- UDP側のXMLコメント、命名規則、未使用コードを整理
- Editor検証ツールを新しい速度参照に対応

上記の整理については、動作テスト完了・コミット済みです。

## 大作戦に向けた改善項目

以下は改善リストです。実装済み機能の一覧ではありません。

- 親イベントと怪しさメーターの不具合修正
- 親機・子機のスコア同期とランキング表示の修正
- 親機・子機それぞれの単体開始操作の整備
- プレイ時間、速度上昇、難易度設定の整備
- チュートリアルとリザルト画面の連携改善
- 部屋の小物、親の見え方、演出、サウンドの調整

部屋の小物は素材完成済みです。親のアニメーション修正版やロード画面の素材は、デザイナーとの連携が必要です。

## 変更後の確認

- Unity Consoleに新しいエラーがない
- Scene・PrefabにMissing Scriptや参照切れがない
- 親イベント、猫フェイント、ゲームオーバーが動く
- 足音、偽ドア音、親のアニメーションが正しく動く
- 高さ補正と回転設定が維持されている
- 通信を変更した場合は、親機・子機の接続と通知を実機で確認する
- センサーを変更した場合は、枕コントローラーの入力を実機で確認する
- タイトルやリザルトから戻った後も再プレイできる

ビルド成功と実際の動作確認は分けて記録してください。生成されたコードも読んで確認してからコミットしてください。
