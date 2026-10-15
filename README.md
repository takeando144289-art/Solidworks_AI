# SolidWorks AI Agent

## SolidWorks内のAIチャット（Windows）

Codex・Claude・Geminiを選べるタスクペインアドインを追加しています。
既存CLIの部品・アセンブリ・図面操作を再利用し、日本語の指示から操作案を作成して実行できます。
Windows版の各AI CLIと認証を利用します。ビルド、登録、設定、対応範囲は
[チャットアドインの導入手順](docs/chat-addin.md)を参照してください。
既存の2Dスケッチに駆動寸法と幾何拘束を追加できます。「形状を変えずに完全定義して」と指示し、確認後に「実行して」と返信してください。変更は既定で未保存です。

SolidWorksをAIから安全に操作するための、Windows/.NET 8向けCLI基盤です。現在は実装計画のMilestone 1〜5までを対象に、COM接続、Part生成・解析、Assembly構築・Mate・干渉解析、図面・断面図・寸法公差・PDF生成まで実装しています。

## Build and test

```powershell
dotnet build SolidWorksAgent.sln
dotnet run --project tests/UnitTests
```

## CLI

```powershell
# 起動中のSolidWorksへ接続
dotnet run --project src/SolidWorksAgent.Tools -- info

# SolidWorksを手動で起動・認証した後、直径30 mm、長さ20 mmの円柱を生成
dotnet run --project src/SolidWorksAgent.Tools -- part create-cylinder `
  --diameter 30 --length 20 --output Cylinder30x20.SLDPRT --json

# アクティブなPartのFeature、円筒面、質量特性を解析
dotnet run --project src/SolidWorksAgent.Tools -- part inspect --json

# Ø12×8、Ø10×20、Ø8×5 mmの段付きシャフト
dotnet run --project src/SolidWorksAgent.Tools -- part create-shaft `
  --diameters 12,10,8 --lengths 8,20,5 --output Shaft.SLDPRT --json

# Housing/Bearing/Shaft Assemblyと同軸Mateを生成
dotnet run --project src/SolidWorksAgent.Tools -- assembly create-bearing-test `
  --housing workspace/parts/Housing.SLDPRT `
  --bearing workspace/parts/Bearing.SLDPRT `
  --shaft workspace/parts/Shaft.SLDPRT `
  --output TestAssembly.SLDASM --json

# 等角投影＋全体表示
dotnet run --project src/SolidWorksAgent.Tools -- view isometric --json

# Assemblyの干渉、外形、質量特性を解析
dotnet run --project src/SolidWorksAgent.Tools -- assembly analyze --json

# シャフト図面、断面図、寸法公差、PDFを生成
dotnet run --project src/SolidWorksAgent.Tools -- drawing create-shaft `
  --model workspace/parts/Shaft.SLDPRT `
  --length 33 --diameter 12 --tolerance 0.01 `
  --output Shaft.SLDDRW --pdf Shaft.pdf --json

# 表示中ドキュメントを視覚検証用BMPへ保存
dotnet run --project src/SolidWorksAgent.Tools -- view snapshot `
  --output workspace/exports/drawing-preview.bmp --json
```

出力先は既定で `workspace/parts` です。既存CADは上書きせず、明示的な `--overwrite` が必要です。実行ログは `logs/swagent.log` に残ります。

この環境のSolidWorksは起動時に対話的な認証が必要なため、`swagent`からの自動起動は無効です。先にSolidWorksを手動起動し、認証と初期化が完了してからCLIを実行してください。

Codexの隔離環境から通常実行するとWindowsユーザーごとのCOMオブジェクトが見えないため、実機SolidWorks操作時はユーザー承認付き実行が必要です。ビルドとCoreの単体テストは隔離環境のまま実行できます。

テンプレートを自動検出できない場合は `--template C:\path\to\部品.PRTDOT` を指定してください。

## General part and assembly drawings

```powershell
# Active part/assembly, one sheet, automatic paper and scale, drawing + PDF
dotnet run --project src/SolidWorksAgent.Tools -- drawing create --json
# Explicit model, all six orthographic directions plus isometric
dotnet run --project src/SolidWorksAgent.Tools -- drawing create `
  --model workspace/parts/Part.SLDPRT --views all --paper A3 --scale 0.5 `
  --output Part.SLDDRW --pdf Part.pdf --json
```

No shaft-stage, length or diameter caps. The chat uses this general command.
Detectable visible outer/circular/shaft-shoulder dimensions are added to parts;
assemblies import model dimensions. `--dimensions model` explicitly imports design/sketch
dimensions (dense models may need layout review); `none` creates views only.
Unsaved source edits are preserved using a separate drawing-reference copy.
Keep the reference model files with the drawing. Section/detail views, GD&T and
manufacturing tolerances still require explicit design work; the output reports its
coverage rather than claiming manufacturing completeness.

## Associative shaft drawings

The current `drawing create-shaft` derives dimensions from the existing model;
`--length` and `--diameter` are optional consistency checks, and `--tolerance`
is optional. Plain solid shafts use exterior projections without a section.
The earlier drawing example describes the original milestone implementation;
see [current drawing behavior and validation](docs/shaft_drawing_reconstruction.md)
for the replacement workflow and its supported geometry.

## Document lifecycle

CAD commands now close the input and intermediate documents they opened, including unsaved trials on failure. A successful generation keeps its saved final CAD output visible. The previous generated output is also closed when it remains unmodified. Preexisting user documents, concurrently opened unrelated documents, and edited previous outputs are preserved. These operations close SolidWorks documents; they do not delete disk files. Required model references may remain loaded invisibly for the final drawing or assembly.

The lifecycle tracks documents opened or created by the command using pinned COM identities, so renaming a newly created document while saving does not lose its ownership. Unknown or ambiguous document names are not closed. After closing, the command checks that the target is no longer visible. JSON results contain `DocumentCleanup` with closed documents, remaining documents, and errors. `workspace/.swagent-last-output.json` records the last successful output path. If that history is absent, preexisting documents are protected.

- `documents list --json`: inspect open documents, visibility, and modified state.
- `documents open-final --path <CAD-file> --json`: display the selected final file and retire a clean previous generated output.
- `documents close --title <exact-title> --discard --json`: explicitly close one named document, discarding its in-memory changes.
- `documents verify-cleanup --path <final.SLDDRW> --json`: exercise success/failure cleanup with unsaved temporary drawings, including protection of an existing unsaved control document.

Validation: 17 unit checks, live success/failure/unsaved-preservation checks, and an actual dimensioned-drawing generation/reopen check. The 70 drawing dimensions remain unchanged.

## Extensible chat API operations

The Windows chat can search installed SOLIDWORKS API signatures and propose structured
feature operations beyond the dedicated command catalog. Plate creation, corner
fillets and tapped holes are included. See [API automation and current coverage](docs/api-automation.md).
