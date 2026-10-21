# FastSearch

Windows 11 / C# / .NET 10 / WPF による C ドライブ専用のファイル名検索アプリです。
基本仕様は [docs/spec-v1.1.txt](docs/spec-v1.1.txt)、追加仕様は [v1.3](docs/spec-v1.3.md) と [v1.4](docs/spec-v1.4.md) に保存しています。

## ビルドと起動

必要環境: Windows 11 x64、.NET 10 SDK、NTFS の C ドライブ。

```powershell
dotnet build src/FastSearch/FastSearch.csproj -c Release
& ./src/FastSearch/bin/Release/net10.0-windows/FastSearch.exe
```

実行ファイルのマニフェストで管理者権限を要求します。起動時の UAC を許可してください。
初回は MFT を列挙し、ファイル ID でサイズ・更新日時を取得します。進捗は一覧の下部に表示します。
属性取得が完了した後、構築中の USN 差分を反映して検索を開始します。

配布用のフォルダーを作る場合（実行先には .NET 10 Desktop Runtime が必要）:

```powershell
dotnet publish src/FastSearch/FastSearch.csproj -c Release -o artifacts/FastSearch
```

## 操作

- 上部の入力欄でファイルを検索します。大文字・小文字は区別しません。空欄では結果を表示しません。フォルダ自体は結果に表示しません。
- `/` がなければファイル名の部分一致です。`var/repo` は `var` で終わるフォルダ直下の `repo` で始まるファイルです。
- `var/data/repo` は最初のフォルダを後方一致、続く `data` を完全一致で評価します。`/var/data/repo` は両方のフォルダを完全一致にします。先頭 `/` はルート指定ではありません。
- フォルダ階層は連続し、最後のフォルダはファイルの直接の親に一致する必要があります。区切りは `/` です。
- `dir/` は条件に合うフォルダ直下の全ファイルを表示します。`/` 単体、`dir//file` など空のフォルダ条件は不一致です。空白はそのまま評価します。
- 「名前」「フォルダ」「サイズ」「更新日時」のヘッダーをクリックするとソートします。同じ列を再クリックすると昇順・降順が切り替わります。
- 検索欄・結果一覧のどちらでも Ctrl+1（名前）、Ctrl+2（フォルダ）、Ctrl+3（サイズ）、Ctrl+4（更新日時）でソートできます。テンキーも対応します。同じ列は方向を切り替え、別の列は昇順で開始します。
- 現在の列・方向はヘッダーの矢印に表示します。検索文字列の変更後も条件を保持し、ソート後は操作中のフォーカスを維持します。一覧にフォーカスがある場合は選択ファイルも維持します。
- 列幅は境界をドラッグして変更できます。Ctrl / Shift で複数の行を選択できます。
- 選択行を Explorer などにドラッグできます。標準の CF_HDROP 形式を使用し、コピー・移動などはドロップ先が処理します。
- 左クリックまたは Enter でファイルを既定のアプリで開きます。Ctrl / Shift を伴う選択操作では開きません。ドラッグ開始後は開きません。
- 右クリック、または Shift+F10 / メニューキーで、そのファイルの従来形式の Windows シェルメニューを表示します。
- 検索欄の ↓ で結果の先頭行へ移動し、一覧の先頭行で ↑、または一覧で `/` を入力すると検索欄へ戻ります。検索文字列は維持します。
- ウィンドウの × は非表示にします。索引と変更追跡は継続します。
- トレイアイコンをダブルクリック、または右クリック →「検索画面を開く」で復帰します。
- `Win + /` でも表示・前面化して検索欄へフォーカスします。登録に失敗した場合は通知とログを残し、トレイ操作を継続します。
- 終了はトレイの右クリック →「終了」。二重起動した場合は既存の検索画面を表示します。

## 索引と復旧

保存先は `%LOCALAPPDATA%\FastSearch\index.bin`、エラーログは同じフォルダーの `error.log` です。
索引と Journal ID・次の USN を一体で保存します。ボリューム GUID、シリアル番号、ルート ID、Journal の有効範囲を照合し、
停止中の変更を再生します。破損・異なるボリューム・Journal の不連続時は再構築します。
Journal が無効な場合は初回に 32 MiB / allocation delta 8 MiB の Journal を作成します。既存の Journal は削除・リセットしません。

通常は変更があるときに約2分おき、および終了時に保存します。SHA-256 で検証し、一時ファイルをディスクへ flush してから
同じディレクトリで原子的に置換します。異常終了時は以前の正常な索引から変更を再生します。
OS の終了・ログオフ時は保存待ちを制限し、最新の保存を完了できない場合は次回起動時に追いつきます。
ログに更新停止が記録された場合は原因を取り除いてアプリを再起動してください。
手動で再構築する場合はアプリを終了してから `index.bin` を削除します。

## 実装

- `Core/SearchQuery.cs` / `SearchEngine.cs`: ファイル限定の部分一致・フォルダ階層検索。親IDで条件を評価し、フォルダごとの判定とパスを検索中だけ共有します。
- `Core/IndexManager.cs`: ウィンドウと独立した索引の寿命、1秒間隔の Journal 追跡、変更の集約、復旧・保存。
- `Native/NtfsVolume.cs`: P/Invoke による MFT 列挙、USN_RECORD_V2 の境界検証、OpenFileById による属性取得。
- `Core/IndexStorage.cs`: バージョン付きバイナリ、チェックサム、原子的置換。
- `MainWindow`: キャンセル付きバックグラウンド検索・ソート、検索世代の検査、キーボード操作、行の UI 仮想化、CF_HDROP、クリックとドラッグの分離。
- `Native/GlobalHotKey.cs`: RegisterHotKey と非表示中も存続するメッセージ専用ウィンドウ。登録解除と競合時の継続動作。
- `Native/ShellFileActions.cs`: ShellExecute 相当の起動、IShellItem / IContextMenu 系によるメニュー取得・メッセージ転送・コマンド実行・COM解放。
- `Diagnostics/MemoryDiagnostics.cs`: 任意で有効にする Private Bytes の CSV 計測。記録キューとファイルサイズを制限します。
- `App`: NotifyIcon、明示的終了、名前付き Mutex と Event による同一ユーザー・セッションの多重起動防止と復帰。

索引は親 ID を保持する immutable dictionary です。更新と検索が同じロックを待たずに動作し、親フォルダーの改名・移動は子孫のパスに反映されます。
検索時にディスクを走査せず、サイズは数値、日時は UTC の日時値で比較します。同一キーはファイル ID の昇順で安定させます。
検索結果の上限やページ切り捨てはありません。全結果の抽出・ソートはバックグラウンドで行います。
アプリ生成時に `RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly` を設定します。描画方式の切り替えはありません。
非表示・最小化時は検索をキャンセルし、選択・検索結果・ドラッグ対象の参照を解放します。索引の通知でUIへ処理を投入せず、復帰時に最新の索引から再検索します。
索引の再構築は行いません。索引・名前・属性は WPF 行オブジェクトとは独立して保持します。

## メモリ計測

通常起動では計測を行いません。次の引数で起動すると `%LOCALAPPDATA%\FastSearch\memory-日時-PID.csv` に記録します。
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
