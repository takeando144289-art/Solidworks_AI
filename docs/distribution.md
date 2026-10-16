# 配布用ZIP

利用者の手順は [distribution-guide.txt](distribution-guide.txt) を参照してください。
ZIPを展開し、`Setup.cmd` → `Login-Codex.cmd` → SOLIDWORKS起動の順で導入します。
初回セットアップには管理者権限とインターネット接続が必要です。

## 作成

Windows PowerShellから、Windows版Gitと.NET 8 SDKがある環境で実行します。

```powershell
.\scripts\Build-Distribution.ps1
```

`artifacts\releases\<ID>\<ID>.zip` とSHA256ファイルが生成されます。
Windows側にGitがない場合は、WSLでコミット済みソースをエクスポートします。

```bash
git archive --format=zip --output=artifacts/distribution-source.zip HEAD -- src tests AGENTS.md Directory.Build.props SolidWorksAgent.sln .gitignore scripts/Build-ChatAddin.ps1 scripts/Register-ChatAddin.ps1 docs/api-automation.md docs/architecture.md docs/chat-addin.md
git rev-parse HEAD
```

そのZIPのWindowsパスと表示された40桁のSHAを指定します。

```powershell
.\scripts\Build-Distribution.ps1 -SourceArchive .\artifacts\distribution-source.zip -SourceCommit <40桁のSHA>
.\scripts\Test-Distribution.ps1 -PackageDirectory .\artifacts\releases\<ID>\SolidWorksAI
```

アプリはコミット済みソースからビルドします。未コミットのアプリ変更は含まれません。
配布用スクリプトと利用者ガイドは作業ツリーから取り込みます。
外部から受け取ったソースZIPをビルドに使用しないでください。
生成物にはソースと操作ルールを同梱し、AIが必要に応じて操作を拡張できるようにしています。
作業中のCAD、添付資料、認証情報、個人設定、会話ログは配布対象外です。

## 導入と検証の範囲

- アプリはProgram Files内の版ごとのフォルダーに配置し、V7アドインを登録します。
- ユーザー設定と作業フォルダーは元の実行ユーザーのLocalAppDataに作成します。
- SOLIDWORKSのInterop DLLは相手のPCのインストールから取得します。
- Codex CLIがなければnpmから導入します。利用者自身がログインします。
- `Setup.ps1 -CheckOnly` は検出だけを行います。設定・登録・ログインを変更しません。
- テストはファイルハッシュ、不要ファイルの除外、PowerShell構文、診断時の設定・登録の不変性を確認します。
- 初期確認環境はSOLIDWORKS 2026 SP3 x64です。別PCでの初回導入、UAC、winget/npmによる新規依存関係導入、別年版SOLIDWORKSは別途実機検証が必要です。

.NETのCOMホストはフレームワーク依存で配布するため、対象PCに.NET 8 Desktop Runtimeが必要です。
詳細は [MicrosoftのCOMホスト資料](https://learn.microsoft.com/en-us/dotnet/core/native-interop/expose-components-to-com) を参照してください。
