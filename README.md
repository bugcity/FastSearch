# FastSearch

Windows 11 / C# / .NET 10 / WPF による C ドライブ専用のファイル名検索アプリです。
基本仕様は [docs/spec-v1.1.txt](docs/spec-v1.1.txt)、追加仕様は [v1.3](docs/spec-v1.3.md) と [v1.4](docs/spec-v1.4.md) に保存しています。
現在の権限・プロセス構成は [索引サービス](docs/service.md) を優先します。旧来の管理者子プロセス方式を置き換えています。

## ビルドと起動

必要環境: Windows 11 x64、.NET 10 SDK、NTFS の C ドライブ。

```powershell
dotnet build src/FastSearch/FastSearch.csproj -c Release
& ./src/FastSearch/bin/Release/net10.0-windows/FastSearch.exe
```

`FastSearch.exe` は通常ユーザー権限で起動してください。初回は「設定」→「索引サービスをインストール／更新」でUACを許可します。
以後の通常起動ではUACは不要です。索引・検索はWindowsサービスが担当し、UIを閉じても更新を続けます。
新しい配布へ更新した場合は「設定」→「索引サービスをインストール／更新」も実行してください。フロントの差し替えだけでは、インストール済みのWorkerは更新されません。
検索画面を「管理者として実行」すると案内を表示して終了します。
配布フォルダー内のワーカーと付属ファイルも必要です。exe 単体のコピーでは動作しません。
初回は MFT を列挙し、ファイル ID でサイズ・更新日時を取得します。進捗は一覧の下部に表示します。
属性取得が完了した後、構築中の USN 差分を反映して検索を開始します。

配布用のフォルダーを作る場合（実行先には .NET 10 Desktop Runtime が必要）:

```powershell
dotnet publish src/FastSearch/FastSearch.csproj -c Release -o artifacts/FastSearch
pwsh -NoProfile -File tools/Verify-Package.ps1
```

## 操作

- 上部の入力欄でファイルを検索します。大文字・小文字は区別しません。空欄では結果を表示しません。フォルダ自体は結果に表示しません。
- `/` がなければファイル名の部分一致です。`var/repo` は `var` で終わるフォルダ直下の `repo` で始まるファイルです。
- `var/data/repo` は最初のフォルダを後方一致、続く `data` を完全一致で評価します。`/var/data/repo` は両方のフォルダを完全一致にします。先頭 `/` はルート指定ではありません。
- フォルダ階層は連続し、最後のフォルダはファイルの直接の親に一致する必要があります。区切りは `/` です。
- `dir/` は条件に合うフォルダ直下の全ファイルを表示します。`/` 単体、`dir//file` など空のフォルダ条件は不一致です。空白はそのまま評価します。
- 「名前」「フォルダ」「サイズ」「更新日時」のヘッダーをクリックするとソートします。同じ列を再クリックすると昇順・降順が切り替わります。
- 検索欄・結果一覧のどちらでも Ctrl+1（名前）、Ctrl+2（フォルダ）、Ctrl+3（サイズ）、Ctrl+4（更新日時）でソートできます。テンキーも対応します。同じ列は方向を切り替え、別の列は昇順で開始します。
- 現在の列・方向は「名前 ↑」「サイズ ↓」のように列名の横に表示します。他の列には矢印を表示しません。
- ソート列・昇順／降順を自動保存し、次回起動時の最初の検索から復元します。検索文字列の変更後も条件を保持し、ソート後は操作中のフォーカスを維持します。一覧にフォーカスがある場合は選択ファイルも維持します。
- 列幅は境界をドラッグして変更できます。Ctrl / Shift で複数の行を選択できます。
- 選択行を Explorer などにドラッグできます。標準の CF_HDROP 形式を使用し、コピー・移動などはドロップ先が処理します。
- 左クリックまたは Enter でファイルを既定のアプリで開きます。Ctrl / Shift を伴う選択操作では開きません。ドラッグ開始後は開きません。
- 右クリック、または Shift+F10 / メニューキーで、そのファイルの従来形式の Windows シェルメニューを表示します。
- 検索欄の ↓ で結果の先頭行へ移動し、一覧の先頭行で ↑、または一覧で `/` を入力すると検索欄へ戻ります。検索文字列は維持します。
- ウィンドウの ×、または検索欄・結果一覧で Esc を押すと非表示にしてトレイへ戻ります。検索文字列・ソート条件を保ち、索引と変更追跡は継続します。
- 「ウィンドウを閉じてもトレイに常駐する」をオフにした場合は、×／EscでUIだけを終了します。トレイアイコンは表示せず、索引サービスは更新を続けます。
- トレイアイコンをダブルクリック、または右クリック →「検索画面を開く」で復帰します。
- 既定では `Win + /` でも表示・前面化して検索欄へフォーカスします。呼び出しキーはトレイの「設定…」から変更できます。登録に失敗した場合は通知とログを残し、トレイ操作を継続します。
- 終了はトレイの右クリック →「終了」。二重起動した場合は既存の検索画面を表示します。

## 設定と自動起動

検索欄の右の「設定」、またはトレイの右クリック →「設定…」を開きます。

- 「配色」でライトグレー／チャコール（ダーク）を選び、「保存」で検索画面と設定画面に反映します。次回起動時にも復元します。
- 既定はライトグレーです。配色項目のない旧設定もライトグレーを使います。選択だけでは変更せず、キャンセル時は元の配色を維持します。
- 入力欄、一覧・選択行、列ヘッダー、ボタン、チェックボックス、プルダウン、スクロールバーとWindows 11のタイトルバーをそろえます。

- 呼び出しキー欄を選択し、Ctrl・Alt・Winのいずれかを含む組み合わせを押して「保存」します。既定はWin + /です。
- 競合するキーやWindowsが予約しているキーは登録できません。失敗時は元のキーを維持し、設定画面で知らせます。
- 列ソートに使うCtrl+1～4（テンキーを含む）は呼び出しキーに指定できません。
- 「ウィンドウを閉じてもトレイに常駐する」は既定でオンです。オフにすると×／Escで終了し、トレイアイコンも表示しません。検索画面の「設定」から再びオンにできます。
- トレイ常駐中に設定をオフにした場合は、検索画面を表示してからトレイアイコンを消します。
- 「Windowsログイン時に起動する」は初期値オフです。オンの場合、常駐オンならトレイ、常駐オフなら検索画面を表示して開始します。
- 索引サービスはWindows起動時に自動開始します。UIの自動起動・手動起動にUACは不要です。サービス導入・更新・削除時だけ管理者権限を使用します。
- 保存後すぐに反映し、再起動後も使用します。Ctrl+1～4など画面内の操作キーは変更しません。
- 配布フォルダーを移動した場合は新しい場所から起動し、設定を保存すると自動起動の登録先を更新できます。

設定は `%LOCALAPPDATA%\FastSearch\settings.json` に保存し、自動起動は
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` の `FastSearch` 値に登録します。
オフにするとこのアプリの登録だけを削除します。Windows側でスタートアップを無効化した場合は、Windowsの設定も確認してください。
設定保存に失敗した場合は、新しいキーを解除して自動起動の登録を元に戻します。
手動でトレイ常駐から開始する場合は `FastSearch.exe --background` を使えます。常駐オフのときは、この引数でも検索画面を表示します。
初回の表示時は描画・アクティブ化に合わせて検索欄へキーボードフォーカスを設定します。

参照: [Runレジストリキー](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys)、
[RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey)。

## 索引と復旧

サービスの保存先は `%PROGRAMDATA%\FastSearch\index.bin`、サービスのエラーログは同じフォルダーの `error.log` です。
UI設定とUIエラーは `%LOCALAPPDATA%\FastSearch` に保存します。導入先・削除方法は [索引サービス](docs/service.md) を参照してください。
索引と Journal ID・次の USN を一体で保存します。ボリューム GUID、シリアル番号、ルート ID、Journal の有効範囲を照合し、
停止中の変更を再生します。破損・異なるボリューム・Journal の不連続時は再構築します。
Journal が無効な場合は初回に 32 MiB / allocation delta 8 MiB の Journal を作成します。既存の Journal は削除・リセットしません。

通常は変更があるときに約2分おき、および終了時に保存します。SHA-256 で検証し、一時ファイルをディスクへ flush してから
同じディレクトリで原子的に置換します。異常終了時は以前の正常な索引から変更を再生します。
OS の終了・ログオフ時は保存待ちを制限し、最新の保存を完了できない場合は次回起動時に追いつきます。
ログに更新停止が記録された場合は原因を取り除いてサービスを再起動してください。
手動で再構築する場合は管理者権限でサービスを停止してから、サービス用の `index.bin` を削除してサービスを開始します。

## 実装

- `Assets/FastSearch.ico`: exe・ウィンドウ・トレイ共通のフォルダ＋虫眼鏡アイコン。16～256pxの9サイズを収録。
- アイコンの編集元は `Assets/FastSearch.Drawing.xaml`。`pwsh -NoProfile -STA -File tools/Generate-Icon.ps1` で ICO と PNG を再生成できます。
- `Core/SearchQuery.cs` / `SearchEngine.cs`: ファイル限定の部分一致・フォルダ階層検索。親IDで条件を評価し、フォルダごとの判定とパスを検索中だけ共有します。
- `Core/IndexManager.cs`: ウィンドウと独立した索引の寿命、1秒間隔の Journal 追跡、変更の集約、復旧・保存。
- `FastSearch.Worker`: WPFに依存しないWindowsサービス。SCMからLocalSystemで起動し、MFT・USN・属性を取得し、索引と検索結果を保持します。
- `Ipc/ServiceIndexSource.cs` / `ServiceProtocol.cs` / `PagedSearchResults.cs`: SID制限とSCMのPID検証付き名前付きパイプ。結果は128件ずつ非同期取得し、UIは最大8ページを保持します。
- `tools/Setup-Service.ps1`: サービスの保護された配置・登録・更新とロールバック・削除。
- `Core/IndexStorage.cs`: バージョン付きバイナリ、チェックサム、原子的置換。
- `MainWindow`: サービスへのキャンセル付き検索・ソート要求、検索世代の検査、キーボード操作、行のUI仮想化、CF_HDROP、クリックとドラッグの分離。
- `Native/GlobalHotKey.cs`: RegisterHotKey と非表示中も存続するメッセージ専用ウィンドウ。登録解除と競合時の継続動作。
- `Native/ShellFileActions.cs`: ShellExecute 相当の起動、IShellItem / IContextMenu 系によるメニュー取得・メッセージ転送・コマンド実行・COM解放。
- `Diagnostics/MemoryDiagnostics.cs`: 任意で有効にする Private Bytes の CSV 計測。記録キューとファイルサイズを制限します。
- `App`: NotifyIcon、明示的終了、名前付き Mutex と Event による同一ユーザー・セッションの多重起動防止と復帰。

索引は親 ID を保持する不変の分割配列（`IndexTable`）です。区画内をID順に並べ、検索は配列を直接走査します。
ファイルごとのオブジェクトと常設の辞書を廃止し、属性・親ID・名前参照を1件40バイトの値データとしてまとめます（名前文字列などの領域は別途必要です）。
更新時だけ変更区画の辞書を一時的に作り、その区画を配列として公開します。検索と更新が同じロックを待たずに動作します。
初期索引は配列へ直接追記し、保存済み索引の読み込みは件数を使って容量を見積もり、大きな中間辞書を作りません。
同名文字列を容量制限付きで共有します。プロセス寿命まで名前を保持する `string.Intern` は使いません。
親フォルダーの改名・移動は子孫のパスに反映されます。既存の `index.bin` の形式は変更せず、そのまま読み込めます。
検索時にディスクを走査せず、サイズは数値、日時は UTC の日時値で比較します。同一キーはファイル ID の昇順で安定させます。
検索結果の上限やページ切り捨てはありません。全結果の抽出・ソートはバックグラウンドで行います。
サービス内の結果は索引内の位置と親パス参照の配列に保持し、属性全体を結果ごとに複製しません。サービスの行モデルのキャッシュは512件、UIの非同期ページキャッシュは最大8ページ（1,024行）です。
検索時の一時領域は小さなブロックで確保し、大きな配列の拡張・複製によるピークを抑えます。行のUI仮想化と併せて使用します。
アプリ生成時に `RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly` を設定します。描画方式の切り替えはありません。
非表示・最小化時は検索をキャンセルし、選択・検索結果・ドラッグ対象の参照を解放します。索引の通知でUIへ処理を投入せず、復帰時に最新の索引から再検索します。
索引の再構築は行いません。索引・名前・属性は WPF 行オブジェクトとは独立して保持します。
フロントとサービスには `System.GC.ConserveMemory=5` を設定し、使用済みのヒープ領域を保持しすぎないようにします。
GC頻度・停止時間とのトレードオフがあるため、検索・UIのテストとメモリ測定を併せて実施しています。
この設定の説明は [.NET公式資料](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector#conserve-memory) を参照してください。
製品コードでは検索・非表示のたびの強制GCやWorking Setの切り詰めを行いません。

## Kanata との併用

検索文字列は TextBox の文字列から取得し、一覧の `/` は `PreviewTextInput` の生成済み文字で判定します。
スラッシュの元の仮想キー（OemQuestion / Divide）では判定しません。Esc・矢印・Enter、Ctrl+数字はWPFの論理キー、
グローバル呼び出しは仕様どおり RegisterHotKey の仮想キーです。Raw Input や低レベルキーボードフックで物理スキャンコードを読んでいません。

検索画面を通常権限、索引をWindowsサービスに分離しました。Kanata は通常権限のまま併用できます。
以前の「アプリ全体を管理者起動する」構成は、Windowsの入力注入制限により通常権限のKanataの変換が働かない原因になります。
旧版の互換性設定やショートカットで「管理者として実行」が有効な場合は解除してください。

メモ帳で正常なKanata設定は、そのまま使用してください。FastSearchには `_` と `=` を置換する処理はありません。
実際のKanataによる入力確認は、通常権限の検索画面上で行ってください。

参照: [Kanata公式の昇格ウィンドウに関する説明](https://jtroo.github.io/config-1.7.0.html)、
[Windows SendInput の権限制限](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-sendinput)。
権限分離の設計・検証条件は [索引サービス](docs/service.md) に記載しています。

## メモリ計測

通常起動では計測を行いません。次の引数で起動すると `%LOCALAPPDATA%\FastSearch\memory-日時-PID.csv` に記録します。
`private_bytes` はフロント、`worker_private_bytes` はワーカー、`total_private_bytes` は両者の合計です。
ワーカーの値を取得できない場合は、その値と合計を空欄にします。ワーカー未起動時の値は0です。
既に常駐している場合はトレイから一度終了してから起動してください。

```powershell
& ./artifacts/FastSearch/FastSearch.exe --measure-memory
```

起動直後、索引完成、検索中（25ms間隔）、結果表示後、非表示・最小化、終了、および5分おきの状態を記録します。
列は `timestamp,pid,state,index_count,result_count,private_bytes,managed_bytes,realized_rows`。
`result_count` はその時点でUIが保持する結果数、`realized_rows` は表示後に生成済みの WPF 行数です。
主指標は `private_bytes` です。計測中のCPU・I/O負荷は通常起動より増えます。
非表示中の定期計測はバックグラウンドで行い、UIを走査しません。
1セッションのログは8 MiBでローテーションし、現在分と直前分を保持します。

実データによらない比較・回帰評価用のプロファイルもあります。

```powershell
dotnet run --project tests/FastSearch.Tests -c Release -- --memory-profile artifacts/measurements/memory-profile.csv
```

これは30万ファイル＋1,001フォルダの合成索引を作り、索引だけ、検索結果の保持、WPFの行生成、結果解放、30回の検索・解放を別々に計測します。
回収可能な参照を調べるためプロファイル内では強制GCを使用します。製品コードでは強制GCやワーキングセットの切り詰めを行いません。
この短時間の試験は、実際の全ドライブや長時間常駐の測定を代替するものではありません。
測定結果と未確認項目は [docs/validation.md](docs/validation.md) を参照してください。

### 現時点の制約

- 初回の属性取得はファイル数に応じた I/O が必要です。並列数は4に制限しています。
- 保護されたファイルや取得中に削除されたファイルの属性は空欄になります。取得可能なファイルの次の変更で再取得します。
- 1つの NTFS ファイル ID に対して1つの名前・親を保持します。複数のハードリンクの全別名列挙は対象にしていません。
- 実体のファイル ID に基づき索引化するため、ジャンクション経由の別名パスは増やしません。
- 初回の全ドライブ索引時間・常駐メモリ・実データでの検索時間は、対象環境で測定が必要です。

## 検証

外部のテストパッケージやネットワークを必要としない検証用コンソールです。

```powershell
dotnet run --project tests/FastSearch.Tests -c Release
# 実ファイルのシェルメニューの取得だけを確認（コマンドは実行しません）
dotnet run --project tests/FastSearch.Tests -c Release -- --shell-probe
# 非同期ソート後の実際のキーボードフォーカスを確認（テスト用ウィンドウを一時的にアクティブ化）
dotnet run --project tests/FastSearch.Tests -c Release -- --focus-probe
# 管理者権限の PowerShell で NTFS API も検証
dotnet run --project tests/FastSearch.Tests -c Release -- --ntfs
```

部分一致、日本語、4列の双方向ソート、安定順、階層変更、キャンセル、Journal レコード解析・不正データ、
差分適用、継続位置、保存・置換・保存キャンセル・破損検出、および30万件の検索を検証します。
WPF のウィンドウを表示せずに生成・レイアウトし、ソフトウェア描画、4列、実際に生成された行数、キーイベント、結果参照の解放、ホットキー登録・競合も確認します。
`--ntfs` は C ドライブの識別情報、MFT の先頭200件、属性取得、Journal の読み出しも確認します。

実機の GUI 受入確認は [docs/acceptance.md](docs/acceptance.md) を参照してください。

## Windows API の参照

- [MFT_ENUM_DATA_V0](https://learn.microsoft.com/windows/win32/api/winioctl/ns-winioctl-mft_enum_data_v0)
- [READ_USN_JOURNAL_DATA_V0](https://learn.microsoft.com/windows/win32/api/winioctl/ns-winioctl-read_usn_journal_data_v0)
- [USN_RECORD_V2](https://learn.microsoft.com/windows/win32/api/winioctl/ns-winioctl-usn_record_v2)
- [OpenFileById](https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-openfilebyid)
- [RegisterHotKey](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-registerhotkey)
- [IShellItem.BindToHandler](https://learn.microsoft.com/windows/win32/api/shobjidl_core/nf-shobjidl_core-ishellitem-bindtohandler)
- [IContextMenu.QueryContextMenu](https://learn.microsoft.com/windows/win32/api/shobjidl_core/nf-shobjidl_core-icontextmenu-querycontextmenu)
- [CMINVOKECOMMANDINFOEX](https://learn.microsoft.com/windows/win32/api/shobjidl_core/ns-shobjidl_core-cminvokecommandinfoex)
