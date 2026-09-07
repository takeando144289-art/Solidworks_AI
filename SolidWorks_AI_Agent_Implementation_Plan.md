# SolidWorks AI Agent 実装計画
## Codex向け開発指示書

## 1. 目的

Windows上のSolidWorksをCodexから操作し、最終的に以下の一連の処理を自動化する。

1. ユーザーが文章・手描きスケッチ・参考画像・写真などで設計イメージを伝える
2. AIが機械構造を解釈し、部品構成・主要寸法・機構を設計する
3. SolidWorksで各部品をパラメトリックにモデリングする
4. 各部品をAssemblyとして組み立て、Mateを設定する
5. 干渉・クリアランス・外形寸法・質量などを自動確認する
6. SolidWorks画面でも形状を視覚的に確認する
7. 問題があれば寸法や配置を修正して再生成する
8. Assemblyの構造・Mate・接触関係から部品間の機能を推定する
9. 軸受嵌合、位置決め、摺動、固定などを推定し、公差候補を決定する
10. 部品図・組立図を自動生成する
11. 寸法、公差、幾何公差、表面粗さ、注記を可能な範囲で自動付与する
12. SLDPRT / SLDASM / SLDDRW / STEP / PDF / BOM等を出力する

最終目標の使用イメージ：

> 「この画像のような構造で、外径30 mm程度の小型サーボを設計して。
> BLDCモーター、減速機、出力軸、ベアリング、エンコーダ、基板、ケースを含める。
> SolidWorksアセンブリと製作用図面まで作って。」

この指示から、設計→CAD生成→組立→検証→修正→図面化までを行う。

---

# 2. 開発環境

## 2.1 OS

Windowsで開発する。

WSL上にはこのプロジェクトを置かない。

推奨配置例：

```text
C:\Users\<USER>\Documents\Projects\SolidWorksAgent
```

理由：

- SolidWorksはWindowsアプリケーション
- SolidWorks COM APIを直接利用する
- Windows側の.NET / C#から操作する方が単純
- CodexのWindows Computer Useと併用しやすい
- WSL↔Windows間のパス、プロセス、COM、権限問題を避ける

---

## 2.2 言語・技術

基本技術：

- C#
- .NET
- SolidWorks COM API
- SolidWorks.Interop.sldworks
- SolidWorks.Interop.swconst
- Git

AI側：

- Codexを開発・実行主体として使用
- 初期段階ではMCPは使用しなくてよい
- SolidWorks操作ライブラリが安定した後、必要に応じてMCP Serverを追加する

---

# 3. 基本アーキテクチャ

```text
User
  |
  | 文章 / 画像 / スケッチ / 写真
  v
Codex / GPT
  |
  | 設計判断
  v
SolidWorksAgent.Tools
  |
  v
SolidWorksAgent.Core
  |
  v
SolidWorksAgent.SwApi
  |
  | COM API
  v
SolidWorks
```

将来：

```text
Codex / GPT / Other Client
          |
         MCP
          |
SolidWorksAgent.Mcp
          |
SolidWorksAgent.Core
          |
SolidWorksAgent.SwApi
          |
       SolidWorks
```

MCPは必須ではない。

SolidWorks操作の実装そのものとMCPを分離すること。

---

# 4. 推奨ソリューション構成

```text
SolidWorksAgent/
|
├─ AGENTS.md
├─ README.md
├─ SolidWorksAgent.sln
|
├─ src/
│  |
│  ├─ SolidWorksAgent.Core/
│  │  ├─ Models/
│  │  ├─ Design/
│  │  ├─ Analysis/
│  │  ├─ Tolerances/
│  │  └─ Validation/
│  |
│  ├─ SolidWorksAgent.SwApi/
│  │  ├─ SwConnection.cs
│  │  ├─ Documents/
│  │  ├─ Sketch/
│  │  ├─ Features/
│  │  ├─ Parts/
│  │  ├─ Assembly/
│  │  ├─ Drawing/
│  │  ├─ Measurement/
│  │  └─ Export/
│  |
│  ├─ SolidWorksAgent.Tools/
│  │  ├─ PartTools.cs
│  │  ├─ AssemblyTools.cs
│  │  ├─ DrawingTools.cs
│  │  ├─ AnalysisTools.cs
│  │  └─ Cli/
│  |
│  └─ SolidWorksAgent.Mcp/
│     └─ 将来実装
|
├─ tests/
│  ├─ UnitTests/
│  └─ IntegrationTests/
|
├─ examples/
│  ├─ SimplePart/
│  ├─ SimpleAssembly/
│  └─ Drawing/
|
├─ test_models/
|
├─ workspace/
│  ├─ parts/
│  ├─ assemblies/
│  ├─ drawings/
│  └─ exports/
|
└─ docs/
   ├─ architecture.md
   ├─ solidworks_api_notes.md
   ├─ design_reasoning.md
   └─ tolerance_rules.md
```

---

# 5. 基本方針

## 5.1 SolidWorks APIを低レベルのままAIに使わせない

Codexが毎回SolidWorks APIの複雑な呼び出しを直接組み立てるのではなく、
高レベルの操作関数を実装する。

例：

```text
CreatePart()
CreateSketch()
CreateCircle()
CreateRectangle()
Extrude()
CutExtrude()
Revolve()
Fillet()
Chamfer()
CreateHole()

CreateShaft()
CreateBearingSeat()
CreateSpurGear()
CreateHousing()

CreateAssembly()
InsertComponent()
MateConcentric()
MateCoincident()
MateDistance()

CheckInterference()
MeasureClearance()
GetMassProperties()

CreateDrawing()
AddStandardViews()
AddDimension()
SetTolerance()
AddDatum()
AddGeometricTolerance()
AddSurfaceFinish()

Save()
ExportStep()
ExportPdf()
```

最終的にはCodexが機械設計上の意図を高レベル命令へ変換する。

---

# 6. 実装フェーズ

---

## Phase 0: プロジェクト準備

### 目的

Windows上でSolidWorksAgentプロジェクトを開始できる状態にする。

### 実装

- Git repository作成
- C#/.NET solution作成
- SolidWorks Interop参照設定
- logging導入
- configurationファイル作成
- workspaceディレクトリ作成
- AGENTS.md作成

### 完了条件

- Visual Studioまたはdotnet CLIでビルド成功
- Codexからプロジェクトを編集・ビルドできる
- SolidWorks Interop型を参照できる

---

## Phase 1: SolidWorks接続

### 目的

C#からSolidWorksへ安定して接続する。

### 必須機能

```text
ConnectToRunningSolidWorks()
StartSolidWorks()
GetActiveDocument()
GetDocumentType()
GetActiveDocumentPath()
CloseDocument()
```

### 確認項目

- SolidWorks未起動時
- SolidWorks起動済み時
- ドキュメントなし
- Part
- Assembly
- Drawing

### 完了条件

CLIで以下が取得できる。

```text
SolidWorks connected
Active document: xxx.SLDPRT
Document type: Part
```

---

## Phase 2: 単純Part生成

### 目的

Codexからパラメトリックな部品を作れることを確認する。

### 実装機能

- New Part
- Plane選択
- Sketch開始/終了
- Line
- Circle
- Arc
- Rectangle
- Dimension
- Extrude
- Cut Extrude
- Revolve
- Fillet
- Chamfer
- Hole
- Save

### 最初のテストモデル

1. φ30 mm
2. 長さ20 mm
3. 中央にφ10貫通穴
4. 片側C1
5. ファイル保存

### 完了条件

Codexの指示だけで、
期待寸法のSLDPRTが生成できる。

---

## Phase 3: 既存Partの解析

### 目的

AIが「作る」だけでなく、既存モデルを理解できるようにする。

### 取得対象

- Feature tree
- Sketch
- Dimension
- Body
- Face
- Edge
- Cylinder
- Plane
- Hole
- Bounding box
- Material
- Mass
- Center of mass

### 重要機能

円筒面について最低限取得する。

```text
diameter
axis
origin
length
face id
```

### 完了条件

既存のシャフトから、

```text
Cylinder 1: φ12 x 8 mm
Cylinder 2: φ10 x 20 mm
Cylinder 3: φ8 x 5 mm
```

などを抽出できる。

---

# 7. Assembly機能

## Phase 4: Assembly生成

### 目的

複数部品をAssemblyとして構成する。

### 実装

```text
CreateAssembly()
InsertComponent()
FixComponent()
FloatComponent()
MateConcentric()
MateCoincident()
MateDistance()
MateAngle()
SuppressMate()
RebuildAssembly()
```

### テスト

3部品構成：

```text
Housing
Shaft
Bearing
```

を作成し、

- Bearing outer ↔ Housing bore
- Shaft ↔ Bearing inner

を同軸Mateする。

### 完了条件

SolidWorks上で正しい位置に組み上がり、
意図した自由度を持つ。

---

## Phase 5: Assembly解析

### 目的

AIがAssemblyの意味を推定できる情報を取得する。

### 取得情報

- Component一覧
- Component tree
- Mate一覧
- Mate対象face
- Mate type
- 同軸関係
- 接触関係
- 距離
- 部品間の包含関係
- Component transform
- 自由度
- Suppression state

### 特に重要

部品AとBについて以下を出せるようにする。

```json
{
  "componentA": "OutputShaft",
  "componentB": "Bearing_6801",
  "relationship": {
    "concentric": true,
    "diameterA": 12.0,
    "diameterB": 12.0,
    "axial_contact": true,
    "relative_rotation_allowed": true
  }
}
```

---

# 8. 検証機能

## Phase 6: 干渉・寸法チェック

### 必須

```text
CheckInterference()
GetInterferenceVolume()
GetBoundingBox()
MeasureMinimumDistance()
GetMassProperties()
GetCenterOfMass()
```

可能であれば：

```text
CheckClearance()
CheckCollisionDuringMotion()
```

### 自動検証例

```text
最大外径 <= 30 mm
全長 <= 40 mm
干渉 = 0
最小ケース肉厚 >= 1.0 mm
Bearingとshaftの軸が一致
```

### 完了条件

設計変更後に自動再チェックできる。

---

# 9. 視覚確認

## Phase 7: SolidWorks画面確認支援

### 目的

APIの数値情報だけでなく、
実際のモデル形状をAIが視覚的に確認できるようにする。

### 基本方針

SolidWorks APIで表示状態を制御する。

例：

```text
SetIsometricView()
SetFrontView()
SetTopView()
SetRightView()
ZoomToFit()
SetSectionView()
HideComponent()
ShowComponent()
SetTransparency()
```

必要に応じてWindows版CodexのComputer Useを併用する。

### 確認対象

- 全体形状の違和感
- ギア・軸・ベアリングの配置
- ケースの不自然な膨らみ
- PCB配置
- 組立可能性
- 断面構造

### 原則

定量的判断：
→ SolidWorks API

視覚的判断：
→ SolidWorks画面 / screenshot / Computer Use

---

# 10. 機械的関係の推定

## Phase 8: Functional Relationship Analyzer

### 目的

Assemblyから部品間の機能を推定する。

### 分類

最低限以下を分類する。

```text
FIXED
ROTATING_SUPPORT
SLIDING
LOCATING
PRESS_FIT
CLEARANCE_FIT
FASTENED
SEALED
GEAR_MESH
UNKNOWN
```

### 推論入力

- Component名
- Part名
- Feature名
- 円筒面径
- Mate
- 接触
- 相対自由度
- ベアリング等の既製品認識
- 幾何形状
- 周辺部品

### 例

```text
OutputShaft ↔ Bearing inner ring
- concentric
- same diameter
- bearing recognized
- shaft can rotate
=> ROTATING_SUPPORT
```

```text
Bearing outer ring ↔ Housing
- concentric
- same diameter
- bearing recognized
- relative motion none
=> FIXED_BEARING_OUTER
```

### 重要

判定にはConfidenceを持たせる。

```json
{
  "type": "ROTATING_SUPPORT",
  "confidence": 0.91
}
```

低Confidenceはユーザー確認対象にする。

---

# 11. 公差推定

## Phase 9: Tolerance Analyzer

### 目的

機械的関係から公差候補を作る。

### 基本方針

AIだけに完全自由判断させない。

以下のハイブリッドとする。

```text
Assembly解析
   ↓
機能分類
   ↓
ルールベース候補生成
   ↓
AIによる文脈評価
   ↓
推奨公差
```

### 例

Bearing inner ↔ rotating shaft：

```text
候補:
h6
j6
k6
```

Bearing outer ↔ housing：

```text
候補:
H7
J7
K7
```

最終選択には、

- 荷重方向
- 回転輪
- 材料
- 温度
- 分解性
- 組立方法

などが影響する。

情報不足時は推定し、Confidenceを下げる。

### 出力例

```json
{
  "feature": "OutputShaft.BearingSeat1",
  "nominal": 12.0,
  "recommended_fit": "h6",
  "confidence": 0.82,
  "reason": "Rotating shaft fitted to bearing inner ring"
}
```

---

# 12. GD&T推定

## Phase 10: Datum / Geometric Tolerance Analyzer

### 対象

- 同軸回転部
- ベアリング座
- フランジ
- 位置決め穴
- 複数軸受支持
- ギア取付部

### 推定候補

```text
Runout
Total runout
Position
Perpendicularity
Parallelism
Flatness
Cylindricity
```

### 例

出力軸：

```text
Bearing seat A
Bearing seat B
Output flange
```

から、

```text
Datum axis = bearing supporting axis
Output flange = runout control
```

などを提案する。

100%正解を目標にせず、
候補とConfidenceを出す。

---

# 13. Drawing生成

## Phase 11: Drawing Generator

### 目的

3Dモデルから製作用図面を生成する。

### Part Drawing

自動配置候補：

- Front
- Top
- Right
- Isometric
- Section
- Detail

### Assembly Drawing

- 全体図
- 断面
- BOM
- Balloon
- 必要に応じて分解図

### 寸法

モデル寸法を全部出さない。

以下を優先：

```text
機能寸法
加工寸法
嵌合寸法
取付寸法
検査寸法
```

### 注記

- Dimension tolerance
- Fit tolerance
- Datum
- GD&T
- Surface finish
- Material
- General tolerance
- Notes

### 完了条件

最低限、
単純シャフトとハウジングについて、
人が修正可能なSLDDRWを自動生成できる。

---

# 14. 画像・スケッチからの設計

## Phase 12: Image-to-Design Workflow

### 注意

画像から直接メッシュを生成することを目的としない。

AIが画像を機械設計の参考情報として解釈する。

### 入力

- 写真
- 手描きスケッチ
- CADレンダリング
- 参考製品画像
- 寸法入り簡易図

### 処理

```text
Image
 ↓
Visual interpretation
 ↓
Mechanical concept
 ↓
Component decomposition
 ↓
Parameterized design plan
 ↓
SolidWorks model
```

### 推定する内容

- 外観
- 構造
- 軸位置
- 部品配置
- 対称性
- ケース形状
- モーター位置
- 出力位置
- 基板位置
- 締結構造の候補

### 推定できないもの

写真だけでは以下を確定扱いしない。

- 正確な寸法
- 材料
- 公差
- ギアmodule
- 歯数
- bearing型番
- 強度
- 内部構造

必要に応じてAIが合理的な仮定を置く。

仮定は設計ログに残す。

---

# 15. 自動設計ループ

## Phase 13: Design Iteration Loop

### 目標

一度生成して終了ではなく、
検証結果を使って設計を更新する。

```text
Design
 ↓
Generate CAD
 ↓
Build Assembly
 ↓
Check interference
 ↓
Measure
 ↓
Visual check
 ↓
Evaluate
 ↓
Modify parameters
 ↓
Rebuild
```

### Design Stateを持つ

例：

```json
{
  "maxDiameter": 30,
  "length": 35,
  "gearRatio": 20,
  "motorDiameter": 20,
  "outputShaftDiameter": 12,
  "housingWall": 1.5
}
```

SolidWorksモデルを直接場当たり的に変更するのではなく、
可能な限りDesign State → CAD生成という構造にする。

---

# 16. 最終対象：Servo Design Agent

## Phase 14

テスト対象として小型サーボを設計する。

### Assembly例

```text
Servo.SLDASM

├─ Housing_Main
├─ Housing_Cover
├─ Motor
├─ Motor_Shaft
├─ Pinion
├─ Reduction_Gear_1
├─ Reduction_Gear_2
├─ Output_Gear
├─ Output_Shaft
├─ Bearing_1
├─ Bearing_2
├─ Encoder_Magnet
├─ Encoder_PCB
├─ Main_PCB
└─ Fasteners
```

### 初期要求例

```text
外径 <= 35 mm
BLDC motor
減速機
出力軸
2 bearing support
absolute encoder
PCB
housing
```

### 設計処理

```text
Requirement
 ↓
Architecture
 ↓
Component selection
 ↓
Dimension planning
 ↓
Part generation
 ↓
Assembly
 ↓
Interference
 ↓
Visual inspection
 ↓
Parameter adjustment
 ↓
Fit inference
 ↓
Tolerance
 ↓
Drawing
```

---

# 17. CLI

初期開発ではMCPよりCLIを優先する。

例：

```text
swagent connect
swagent info

swagent part create-cylinder --diameter 30 --length 20
swagent part inspect Shaft.SLDPRT

swagent assembly inspect Servo.SLDASM
swagent assembly interference Servo.SLDASM

swagent analyze relationships Servo.SLDASM
swagent analyze fits Servo.SLDASM

swagent drawing create Shaft.SLDPRT
```

CodexはこのCLIを呼び出して結果を見る。

JSON出力モードも実装する。

```text
swagent assembly inspect Servo.SLDASM --json
```

AIが扱いやすいようにする。

---

# 18. MCP Server

## 実装時期

Phase 1〜11程度が安定してから検討する。

初期段階では不要。

### MCP tool候補

```text
get_active_document
inspect_part
inspect_assembly

create_part
modify_dimension

create_assembly
insert_component
add_mate

check_interference
measure_clearance

analyze_relationships
recommend_fits

create_drawing
apply_tolerances
export_pdf
```

MCP ServerはSolidWorksAgent.Coreを呼ぶだけにし、
SolidWorks APIをMCP層へ直接書かない。

---

# 19. エラー処理

SolidWorks COM APIは失敗状態が分かりにくいことがあるため、
必ず操作結果を検証する。

例：

```text
Feature creation requested
 ↓
Rebuild
 ↓
Check returned Feature
 ↓
Check rebuild errors
 ↓
Measure resulting geometry
 ↓
Success
```

単にAPIが例外を出さなかっただけで成功扱いしない。

---

# 20. Logging / Audit

設計判断とSolidWorks変更をログに残す。

例：

```text
[DESIGN]
Output shaft diameter changed: 10 -> 12 mm
Reason: bearing selection

[CAD]
Modified D1@Sketch2: 0.010 -> 0.012

[CHECK]
Interference: Housing / Gear2 = 13.2 mm3

[FIX]
Housing pocket increased by 1.0 mm

[TOLERANCE]
Bearing seat: φ12 h6
Confidence: 0.84
```

後からAIまたは人が設計判断を追えるようにする。

---

# 21. 安全策

AIが既存設計を破壊しないようにする。

初期段階では：

- 元ファイルを直接上書きしない
- workspace以下にコピーして作業
- Save Asを基本とする
- destructive operationには確認可能なログを残す
- SolidWorks操作前後で状態を取得する

---

# 22. テスト戦略

各Phaseにテストモデルを用意する。

## Test 1
単純円柱

## Test 2
段付きシャフト

## Test 3
Bearing + Shaft + Housing

## Test 4
2軸ギア機構

## Test 5
簡単な小型サーボ

各テストについて、

```text
Input requirements
Expected dimensions
Expected mate
Expected interference
Expected drawing
```

を記録する。

---

# 23. 最初にCodexが実装する範囲

最初のIterationでは以下だけ実装する。

## Milestone 1

1. Windows C# solution作成
2. SolidWorks API参照
3. 起動中のSolidWorksへ接続
4. Active document取得
5. 新規Part作成
6. φ30 x 20 mm円柱作成
7. 保存
8. CLIから実行

## Milestone 2

1. 段付きシャフト作成
2. Feature / 円筒面解析
3. JSON出力

## Milestone 3

1. Shaft
2. Bearing dummy
3. Housing

の3部品を生成し、
Assemblyを作成してMateする。

## Milestone 4

- interference check
- bounding box
- mass properties

## Milestone 5

シャフト図面の自動作成。

- Front view
- Section view
- 基本寸法
- φ寸法
- 簡単な寸法公差

ここまでを第一開発目標とする。

---

# 24. 第一開発目標のDone条件

以下をCodexから一連で実行できれば、
基盤完成と判断する。

```text
1. SolidWorksへ接続
2. Shaft.SLDPRTを生成
3. Bearing.SLDPRTを生成
4. Housing.SLDPRTを生成
5. TestAssembly.SLDASMを生成
6. 同軸Mate
7. 干渉チェック
8. Assembly情報をJSON取得
9. Bearing嵌合部を認識
10. Shaft.SLDDRWを生成
11. Bearing seatに公差を付ける
12. PDF出力
```

---

# 25. 最終ゴール

ユーザーから以下だけを受け取る。

```text
「この画像を参考に、小型サーボを設計してください。
外径30〜35 mm程度。
モーター、減速機、出力軸、ベアリング、
エンコーダ、基板、ケースを含めてください。」
```

AIが以下を自律的に行う。

```text
画像理解
 ↓
構造案作成
 ↓
要求仕様整理
 ↓
部品構成
 ↓
3D Part生成
 ↓
Assembly
 ↓
Mate
 ↓
干渉確認
 ↓
寸法検証
 ↓
SolidWorks画面による視覚確認
 ↓
設計修正
 ↓
機械的関係推定
 ↓
嵌合推定
 ↓
公差設計
 ↓
GD&T候補
 ↓
Drawing
 ↓
PDF / STEP / BOM出力
```

100%自動で正しい機械設計を保証することは目標としない。

以下を重視する。

- 機械設計として合理的
- パラメトリックで修正可能
- Assemblyとして成立
- AIによる判断理由を追跡可能
- 不確実な判断にはConfidenceを持たせる
- 人間がレビュー・修正可能
- 同じ操作を再現可能

---

# 26. Codexへの実装指示

この文書に従って、Phase 0から順番に実装すること。

重要：

1. 最初から全機能を実装しない
2. 各Phaseで必ず実機SolidWorksによる動作確認を行う
3. SolidWorks API操作を高レベルAPIへラップする
4. COM API固有コードをSwApi projectへ隔離する
5. Core層は可能な限りSolidWorks依存にしない
6. CLIを最初のAIインターフェースとする
7. CLIにはJSON出力を用意する
8. MCPは後回し
9. モデル変更後は必ずRebuildと形状検証を行う
10. 可能な限りテストモデルを自動生成する
11. 元CADファイルを直接破壊しない
12. 実装上の重要な知見はdocs/solidworks_api_notes.mdへ追記する
13. APIで分からない部分を無理に推測せず、SolidWorksの公式API仕様や実際の挙動を確認する
14. 最終目標は「CAD操作」ではなく「AIによる機械設計ループ」である
