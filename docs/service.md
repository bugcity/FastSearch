# 索引サービス（2026-10-22）

基本仕様の「Windowsサービスは使用しない」と、以前の管理者子プロセス方式を本変更で置き換えます。
通常権限のUIを維持し、Kanataなどの通常権限の入力変換を利用できます。

## 初回・更新・削除

1. 旧版をトレイから終了し、新しい配布ZIPをフォルダーごと展開します。
2. 通常権限で `FastSearch.exe` を起動し、「設定」→「索引サービスをインストール／更新」を選びます。
3. この操作のUACを許可します。完了後、検索画面は自動的に接続します。

初回だけサービス用の索引を新しく構築します。旧版の索引とユーザー設定は変更しません。
更新ではサービスを停止してWorkerの4ファイルを入れ替え、再開します。失敗時は直前のファイルを復元します。
保存済みのサービス索引は再利用し、停止中のUSN差分を反映します。
通常のUI起動でUACは要求しません。未導入・停止・版の不一致は画面下部で知らせます。

PowerShellからも操作できます。未昇格なら管理者プロセスを一度起動します。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Setup-Service.ps1 -Mode Install
powershell -NoProfile -ExecutionPolicy Bypass -File .\Setup-Service.ps1 -Mode Uninstall
```

## 構成・保存先

| 対象 | 動作・保存先 |
|---|---|
| サービス | `FastSearch.IndexService`、LocalSystem、自動（遅延開始）、異常終了時は再起動 |
| サービスの4ファイル | `%ProgramFiles%\FastSearch\Service` |
| 索引・USN位置 | `%PROGRAMDATA%\FastSearch\index.bin` |
| 利用ユーザーSID | 同じフォルダーの `owner.sid` |
| サービスのエラー・導入ログ | 同じフォルダーの `error.log`／`setup.log` |
| UI設定・UIエラー | `%LOCALAPPDATA%\FastSearch\settings.json`／`error.log` |

「トレイに常駐する」がオフの場合、×／EscはUIだけを終了します。オンの場合はUIをトレイへ隠します。
サービスはどちらでも索引更新を継続します。ログオフでもサービスは継続し、OS終了時は保存待ちを制限します。
UIを終了すると呼び出しショートカットは解除します。UIの自動起動設定とサービスの自動開始は別です。
サービスの停止は管理者のPowerShellで `Stop-Service FastSearch.IndexService`、開始は `Start-Service FastSearch.IndexService` です。
削除では索引と設定を残します。

一台につき一つのサービス、一人の利用ユーザーを対象とします。
別ユーザーへ引き継ぐ場合はサービスを停止・削除し、管理者が保存索引と `owner.sid` を削除してから再導入します。
サービス用のファイル・索引フォルダーはSYSTEM／Administratorsだけがアクセスできます。
既存の導入ディレクトリが管理者以外の所有だった場合や、パスに再解析ポイントがある場合は導入を中止します。

## 通信とメモリ

- 索引・検索・ソート・永続化はサービスだけで行い、UIに索引を複製しません。
- 検索結果はサービスに固定したスナップショットとともに保持し、128件ずつ取得します。
- UIキャッシュは8ページ（最大1,024行）。先頭とソート後の選択ページを優先保持します。
- 未取得行は非同期で読み込みます。UIスレッドは通信完了を待ちません。
- 行オブジェクトのプロパティ更新で表示し、スクロール時の選択を維持します。
- ソートでは選択ファイルのページも先に取得してフォーカスを復元します。
- 検索の中断・結果の破棄・UI非表示で接続を閉じ、サービスの結果参照も解放します。
- 放置された結果セッションは10分で切断し、必要時にUIが再検索します。
- 1秒間隔の状態取得で索引の変更・サービス再起動を検出し、自動再接続します。
- キャッシュの上限で検索結果の総件数を打ち切ることはありません。

サービスの索引メモリは引き続き必要です。合計メモリの上限や実機での削減量は実測後に判断します。
Private BytesはUIとサービスを分けて測定します。UIの `--measure-memory` でサービスの値を読めない場合は空欄とし、管理者のタスクマネージャー／Performance Monitorで補完します。
従来のメモリ比較CSVは旧方式の測定です。

## 権限境界

名前付きパイプはSYSTEMと登録ユーザーSIDだけを許可し、ネットワーク接続を拒否します。
接続元PIDをWindowsから取得し、PROCESS_QUERY_LIMITED_INFORMATIONとTOKEN_QUERYでプロセストークンのSIDを確認します。クライアントの偽装は行いません。
UIはSCMの登録PIDとパイプのサーバーPIDを照合します。クライアントはIdentificationレベルを指定し、サービスはクライアントの権限でファイル操作を実行しません。
APIは状態取得・検索・ページ取得だけです。任意ファイルの実行・書き込み・管理コマンドは公開しません。
文字列長、版、ソート列、ページ位置・件数を検証し、不正な要求は接続を閉じます。
ファイルを開く・DnD・シェルメニューは、通常権限のUIからWindows Shellへ委譲します。
チェックサム付きの原子的保存、ボリューム・USN継続性の検証と再構築は維持します。

参照: [Service Functions](https://learn.microsoft.com/en-us/windows/win32/services/service-functions)、
[GetNamedPipeClientProcessId](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getnamedpipeclientprocessid)、
[OpenProcessToken](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-openprocesstoken)。

## 通信が途中で切れる場合

フロントの更新だけではサービスの実行ファイルは更新されません。新しい配布フォルダーから「設定」→「索引サービスをインストール／更新」を実行してください。
索引の保存形式は変更していないため、`index.bin` の削除や強制再構築は不要です。
診断用の `Get-ServiceDiagnostics.ps1` は状態・接続元と接続先PID・実行ファイルのハッシュ・応答の受信位置を表示する読み取り専用スクリプトです。
必要時に同じWindowsユーザーのPowerShellから実行してください。保護されたファイルの確認は管理者権限が必要です。
