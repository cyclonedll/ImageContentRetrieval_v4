# Vorcyc Image Content Retrieval v4 — 开发日志

> **项目**：ImageContentRetrieval_v4  
> **版本**：4.0.0.0  
> **作者**：cyclone_dll @ Vorcyc  
> **仓库**：<https://github.com/cyclonedll/ImageContentRetrieval_v4>

---

## 1. 我要做什么

我想做一个 **以图搜图** 的桌面工具——给定一张图片，在我本地的图库里找出长得最像的那些图片。

其实这个项目已经是第四版了。上一版（v3）我用的是 Google 的 **InceptionV3** 模型，提取瓶颈层（bottleneck layer）的输出作为图像的 embedding。InceptionV3 当时够用，但它毕竟是分类模型，瓶颈层特征本质上是为 ImageNet 分类优化的，并不是专门为相似度检索设计的。到了 v4，我换成了 Facebook 的 **DINOv2**——这是一个自监督视觉基础模型，它的 CLS token 天生就是图像级别的语义表示，1024 维，不需要额外微调就能用于相似度检索，效果比 InceptionV3 的瓶颈层好了一大截。特别是在语义相似（而非像素级相似）的场景下，DINOv2 的优势非常明显。

除了升级特征提取器，v4 还加了微软的 **Florence2** 给每张图自动生成一段英文描述（Caption），方便在结果列表里一眼看出图片内容——这是 v3 没有的功能。

核心思路还是一样的：用深度学习模型把每张图片变成一个高维向量（embedding），然后在向量空间里做相似度搜索。最终做出来的是一个 WPF 桌面应用，用户选一个文件夹就能自动建库，然后拖张图片进来就能搜。

---

## 2. 工程搭建

### 2.1 项目配置

我用的是 .NET 10 + WPF，目标框架写的 `net10.0-windows7.0`——虽然现在基本没人用 Win7 了，但保留兼容性也没什么成本。

```xml
<TargetFramework>net10.0-windows7.0</TargetFramework>
<Nullable>enable</Nullable>
<UseWPF>true</UseWPF>
```

项目类型是 `WinExe`，启用了 nullable。

### 2.2 我选的 NuGet 包

| 包 | 版本 | 我为什么选它 |
|---|---|---|
| `Florence2` | 25.12.63049 | 微软的多模态大模型，能给图片生成描述，而且有现成的 NuGet 包，不用自己折腾模型加载 |
| `Microsoft.ML.OnnxRuntime.Gpu.Windows` | 1.24.4 | ONNX 推理引擎，我需要 GPU 加速，这个包同时支持 CUDA 和 CPU 回退 |
| `SixLabors.ImageSharp` | 3.1.12 | 跨平台图像处理，我用它做 Center Crop 和 ImageNet 归一化预处理 |
| `Vorcyc.Quiver` | 2.0.0 | 我刚完成的一个轻量级嵌入式向量数据库，专门用来做向量相似度搜索。这个项目正好是 Quiver 的第一个实战场景——我一边开发 Quiver 一边拿这个项目验证，两边互相推动 |
| `Vorcyc.RoundUI` | 1.0.0 | 同样是我们自己的 WPF 控件库，圆角现代化风格，Light 主题 |
| `WindowsAPICodePack` | 8.0.15.1 | 需要一个好看的文件夹选择对话框，WPF 自带的太丑了 |

### 2.3 模型文件

模型全部放在 `model_zoo/` 目录下，csproj 里配了 `CopyToOutputDirectory: PreserveNewest`，构建时自动复制到输出目录。

**DINOv2** 的模型需要手动从 HuggingFace 下载（`domp_v2_q4.onnx`，量化版，体积小推理快）。**Florence2** 的模型则由 NuGet 包内置的 `FlorenceModelDownloader` 在首次运行时自动下载——这个设计我觉得挺好的，用户不需要自己去找模型文件。

---

## 3. 应用入口

`App.xaml` 里我做了三件事：

1. 加载 `Vorcyc.RoundUI` 的 Light 主题（`RoundUI.xaml` + `RoundUI.Light.xaml` + `Icons.xaml`）
2. 把强调色设为 `#556a78`（蓝灰色）——我觉得这个色调比较沉稳，适合工具类应用
3. 全局字体改成 `Microsoft YaHei`

`App.xaml.cs` 是空壳，什么自定义逻辑都没加。

---

## 4. 主窗口 UI 设计

### 4.1 窗口基础

我用了 `RoundNormalWindow`（RoundUI 提供的圆角窗口），尺寸定在 **1200×700**，居中启动。这个尺寸在 1080p 和 2K 屏幕上都比较舒服。标题放在左边：`Vorcyc Image Content Retrieval v4`。

### 4.2 窗口级资源

我注册了三个资源：

- `NullToVisibilityConverter` — 自己写的一个转换器，绑定值为 `null` 时返回 `Visible`，否则 `Collapsed`。用来控制那些「请拖放图片到此处」之类的占位提示文字，图片一加载进来提示就自动消失。
- `ThumbnailConverter`（`FilePathToThumbnailConverter`）— 文件路径转 60px 缩略图，带 `ConcurrentDictionary` 缓存。这个后面会详细说。
- `SubtleBorderBrush` — `AccentColor` + `Opacity=0.3`，做了一个统一的淡色边框画刷，整个界面的边框都用它，视觉上比较一致。

### 4.3 整体布局

三行 Grid：工具栏、内容区、状态栏。

```
┌─────────────────────────────────────────────┐
│  Row 0 — 顶部工具栏（Auto）                   │
├─────────────────────────────────────────────┤
│  Row 1 — 主内容区（*）                         │
│  ┌──────────────┬───┬──────────┐            │
│  │  左栏 (3*)   │ ↔ │ 右栏 (2*) │            │
│  │  查询图 180px │   │ 结果预览  │            │
│  │  结果列表 *   │   │          │            │
│  └──────────────┴───┴──────────┘            │
├─────────────────────────────────────────────┤
│  Row 2 — 底部状态栏（Auto）                   │
└─────────────────────────────────────────────┘
```

### 4.4 顶部工具栏

用 `DockPanel` 做的左右布局：

**左侧**放了三个按钮：
- `📁 构建特征库`（`btnBuild`）— ToolTip 里写了快捷键 `Ctrl+B`
- `⏹ 取消建库`（`btnCancel`）— 默认隐藏，建库时才出现，字体设成了 `OrangeRed` 醒目一点
- `🧹 清理无效数据`（`btnCleanup`）

**右侧**：
- 推理设备下拉框（`cbDevice`）— `CUDA` / `CPU`，默认 CUDA
- 一个竖分隔符
- `🔍 以图搜图`（`btnRetrieval`）— ToolTip 提示 `Ctrl+R`
- 返回数下拉框（`cbReturnCount`）— 可编辑的 ComboBox，预设 10/50/100，默认 10

### 4.5 主内容区

左右分栏比例 **3:2**，中间放了个 `GridSplitter`（5px 宽，透明，`SizeWE` 光标），用户可以自由拖拽调整比例。左栏最小 400px，右栏最小 200px。

**左栏上方**是查询图片的拖放区域（180px 高）。我做了完整的拖放交互：
- `DragEnter` 时边框加粗到 2px + 蓝灰色半透明背景，给用户一个视觉反馈
- `DragLeave` 时恢复原样
- `Drop` 时筛选出第一个图片文件，自动执行检索

区域内部用 Grid 叠了一个 TextBlock 占位提示和一个 Image 控件。提示文字通过 `NullToVisibilityConverter` 绑定 `imgSource.Source`，图片加载后自动消失，这个交互我觉得比较优雅。

**左栏下方**是检索结果 DataGrid（`dg1`），我定义了四列：

| 列 | 类型 | 宽度 | 说明 |
|---|---|---|---|
| 缩略图 | `DataGridTemplateColumn` | 56px | 绑定 `Entity.Filename` → `ThumbnailConverter`，44×44 的小图 |
| 相似度 | `DataGridTextColumn` | 80px | `StringFormat=P2` 显示成百分比 |
| 文件名 | `DataGridTextColumn` | `*` | 完整路径 |
| 描述 | `DataGridTextColumn` | `*` | Florence2 生成的 Caption |

行高固定 50px，单击选中预览，双击定位到资源管理器。

**右栏**就是选中结果的大图预览区，同样有个占位提示「选择左侧结果以预览图片」。

### 4.6 底部状态栏

背景用了一个很淡的渐变（`AccentColor` + `Opacity=0.1`），左边放了个 120×14 的 ProgressBar（建库时显示），右边是状态文字 Label。

---

## 5. 主窗口逻辑——这是最核心的部分

### 5.1 我维护的几个字段

| 字段 | 说明 |
|---|---|
| `_db` | Quiver 向量数据库上下文，数据库文件是运行目录下的 `features.vdb` |
| `_syncContext` | UI 线程的 `SynchronizationContext`，后台线程要更新 UI 就靠它 |
| `_embedder` | **全局** DINOv2 实例，专门给查询检索用的 |
| `_sessionOptions` | 全局 ONNX 会话选项，跟设备切换联动 |
| `_buildCts` | 建库操作的 `CancellationTokenSource`，用来支持取消 |

### 5.2 初始化

窗口 Loaded 事件里我做了这些：

1. 先抓住 UI 线程的 `SynchronizationContext`——后面后台线程更新进度条要用
2. 禁用整个窗口，异步加载向量数据库
3. 根据当前设备选项创建 `SessionOptions` 和全局 `DinoV2Embedder`
4. 完成后启用窗口，状态栏显示「已建模 N 个图像文件（CUDA/CPU）」
5. 如果初始化失败（比如模型文件不存在、CUDA 不可用），直接 MessageBox 报错然后关闭应用——我不想让用户在一个半初始化的状态下操作

`CreateSessionOptions()` 方法会读 `cbDevice` 的值：CUDA 就 `AppendExecutionProvider_CUDA(0)`，CPU 就不追加任何 provider（ORT 默认就是 CPU）。始终开启 `ORT_ENABLE_ALL` 图优化。

### 5.3 构建特征库——最复杂的一个流程

这是我花时间最多的地方，有很多细节要处理。完整流程：

1. 弹出 `CommonOpenFileDialog`（`IsFolderPicker = true`）选择文件夹
2. **在禁用界面之前**读取当前设备选项，创建一个 **局部** 的 `SessionOptions`——这一点很重要，因为一旦 UI 禁用了，ComboBox 也被禁用，读不到值
3. 切换 UI 为建库状态（`SetBuildingUI(true)`）
4. 创建局部的 `DinoV2Embedder` 和 `FlorencCaptioning`（Florence2 那个是异步工厂方法 `CreateAsync`，因为可能要下载模型）
5. 扫描文件夹下所有子目录的图片文件（通过 `SupportedWildcards` 统一管理，当前支持 `*.jpg / *.jpeg / *.jfif / *.png / *.webp / *.bmp / *.gif / *.tif / *.tiff / *.tga`）
6. 用 `IOHelper.Except` 排除已建库的文件，物化成 List
7. 然后 `Task.Run` 进后台线程遍历：
   - 每次循环先检查 `CancellationToken`
   - DINOv2 提取 1024 维向量
   - Florence2 生成文字描述
   - 构造 `ImageDb` 实体，加入 `_db.Images`
   - **每 200 条** `SaveChangesAsync` 一次——增量保存
   - 通过 `SynchronizationContext.Post` 回到 UI 线程更新进度条 + 状态文字 + 任务栏进度

8. 如果用户点了取消，捕获 `OperationCanceledException`
9. 不管成功还是取消，最后都再 `SaveChangesAsync` 一次保存剩余数据
10. 弹窗报告结果（文件数 + 耗时）

**这里有几个我比较得意的设计决策：**

- **建库用局部 Embedder，查询用全局 Embedder**。建库时 DINOv2 + Florence2 两个模型同时加载，显存压力很大；建库结束后局部资源通过 `using` 立即释放——`DinoV2Embedder` 直接 Dispose 其 `InferenceSession`，`FlorencCaptioning` 则通过反射释放 `Florence2Model` 内部的 4 个 `InferenceSession`（因为 `Florence2Model` 本身未实现 `IDisposable`）。查询的全局 Embedder 则一直驻留在显存里，避免每次查询都重新加载模型。两者互不干扰。

- **每 200 条增量保存**。我考虑过 100 条或 500 条，最终选了 200——频繁保存会拖慢建库速度（每次保存都有磁盘 IO），但间隔太大又有数据丢失风险。200 条是我实测下来的一个平衡点。

- **`CancellationToken` 支持**。对着一个几万张图的文件夹建库，可能要跑半小时以上，用户必须能取消。取消后已处理的数据不会丢，下次再选同一个文件夹，跳过已处理的继续。

- **任务栏进度条**（`TaskbarItemInfo.ProgressValue`）。窗口最小化后用户还能从任务栏上看到进度，这个小细节体验提升很大。

### 5.4 取消建库

就一行：`_buildCts?.Cancel()`。简单直接。

### 5.5 UI 状态切换

`SetBuildingUI(bool)` 统一管理建库/空闲两个状态下的 UI：

- 建库时：禁用 Build/Cleanup/Retrieval 按钮和设备下拉框，显示 Cancel 按钮和进度条，任务栏进度设为 Normal
- 空闲时：反过来

### 5.6 以图搜图

两种触发方式：
1. 点按钮（`btnRetrieval_Click`）→ `OpenFileDialog` 选图
2. 拖放图片到查询区（`QueryImage_Drop`）

两种方式最终都走到 `PerformRetrievalAsync`：
1. 用全局 `_embedder` 提取查询图片的 1024 维向量
2. 解析返回数量
3. `_db.Images.SearchAsync` 执行向量相似度搜索（欧氏距离）
4. 结果绑定到 DataGrid

搜索之前我会检查特征库是否为空，空的话直接提示「请先建库」。

### 5.7 结果交互

- **单击** DataGrid 某一行 → 从 `QuiverSearchResult<ImageDb>` 取出文件名，`ReadImage` 加载到右栏预览
- **双击** → 调用 `ShellFolderSelector.LocateFile`，在资源管理器中定位并选中该文件

### 5.8 清理无效数据

用户删了某些图片文件后，数据库里的记录就失效了。点「清理」按钮会调用 `IOHelper.CleanupAsync`：后台遍历所有记录，`File.Exists` 检查，不存在的 `RemoveByKey`，最后保存。

### 5.9 设备切换

`cbDevice_SelectionChanged` 里的逻辑：
1. 初始化没完成就忽略（`_embedder is null`）
2. Dispose 旧的 Embedder 和 SessionOptions
3. 根据新选项重建
4. 状态栏显示当前设备

如果切到 CUDA 但实际没有 GPU，会 catch 异常然后 MessageBox 提示。

### 5.10 拖放搜图

这个功能我觉得是整个应用最提升效率的交互。四个事件：

| 事件 | 我做了什么 |
|---|---|
| `DragOver` | 检查 `DataFormats.FileDrop`，设置合适的 `DragDropEffects` |
| `DragEnter` | 边框加粗到 2px + 半透明蓝灰背景 `Color.FromArgb(25, 85, 106, 120)` |
| `DragLeave` | 恢复原样 |
| `Drop` | 从拖入的文件列表里筛选第一个图片文件（通过 `IsSupportedImage` 统一判断，忽略大小写），加载预览 + 执行检索 |

高亮反馈的颜色我调了好几次，最后用了跟 AccentColor 同色系的半透明色，看起来协调又不突兀。

### 5.11 键盘快捷键

在 `PreviewKeyDown` 里拦截：

- `Ctrl+B` → 建库（等同点击按钮）
- `Ctrl+R` → 检索

两个快捷键都会检查对应按钮是否可用（建库期间是禁用的，不会误触）。

### 5.12 图片读取辅助

`ReadImage` 方法：

```csharp
File.ReadAllBytes → MemoryStream → BitmapImage（CacheOption=OnLoad）→ Freeze
```

这里有三个讲究：
1. **用字节流读**，不用 `UriSource` 直接给路径——后者会锁定文件句柄，导致后续操作（比如删除文件）失败
2. **`CacheOption = OnLoad`** — 确保 MemoryStream 关闭后图片数据仍在内存里
3. **`Freeze()`** — WPF 的 `Freezable` 对象 Freeze 后变成不可变的，可以安全跨线程使用

---

## 6. DINOv2 推理封装

`DinoV2Embedder.cs` 封装了 DINOv2 的 ONNX 推理。

### 6.1 基本参数

- 模型路径：`./model_zoo/DinoV2/domp_v2_q4.onnx`（q4 量化版）
- 输入分辨率：518×518（DINOv2-large 推荐）
- 输出维度：1024（CLS token embedding）

构造函数接收外部传入的 `SessionOptions`——调用方决定用 CUDA 还是 CPU，Embedder 本身不关心。Input/Output 名称从模型元数据自动读取，有兜底默认值。

### 6.2 单张提取流程

`ExtractEmbedding(string imagePath)` 的完整过程：

1. 用 ImageSharp 加载图片为 `Rgb24` 格式
2. 预处理：
   - Resize 到 518×518，用 `ResizeMode.Crop`（Center Crop）
   - 构造 `DenseTensor<float>[1, 3, 518, 518]`
   - 像素值做 ImageNet 标准归一化：
     - R: `(pixel / 255 - 0.485) / 0.229`
     - G: `(pixel / 255 - 0.456) / 0.224`
     - B: `(pixel / 255 - 0.406) / 0.225`
3. `Session.Run` 执行推理
4. 从输出张量 `[1, num_tokens, 1024]` 取 CLS token（第 0 个 token）→ `float[1024]`
5. L2 归一化（`norm = sqrt(Σx²)`，除以 norm，epsilon 防除零 `1e-8`）
6. 返回归一化后的 1024 维向量

我还写了个 `ExtractEmbeddingsBatch` 批量版本，不过现在建库流程里没用到——建库是逐张处理的，因为每张图还要过 Florence2 生成描述，没法单独批量处理 DINOv2。

实现了 `IDisposable`，释放 `InferenceSession`。

---

## 7. Florence2 推理封装

`FlorencCaptioning.cs` 封装了 Florence2 的图像描述生成。

### 7.1 为什么用异步工厂

我用了 **异步工厂方法** + 私有构造函数的模式：

```csharp
public static async Task<FlorencCaptioning> CreateAsync(SessionOptions options)
```

原因是 Florence2 的模型首次使用需要下载，这是个异步 IO 操作，不适合放在构造函数里。通过工厂方法可以确保调用方拿到实例时模型已经就绪。

### 7.2 具体实现

`CreateAsync` 里做的事：
1. 创建 `FlorenceModelDownloader`，目标目录 `./model_zoo/florence2`
2. 如果模型没准备好（`!IsReady`），异步下载
3. 用 `SessionOptions` + downloader 创建 `Florence2Model`
4. 包装成 `FlorencCaptioning` 返回

`GetCaption` 就很简单了——打开图片流，`Florence2Model.Run(TaskTypes.CAPTION, ...)`，取 `PureText`。

### 7.3 显存释放——为什么要用反射

`Florence2Model` 内部持有 4 个 `InferenceSession`（`_sessionDecoderMerged`、`_sessionEmbedTokens`、`_sessionEncoder`、`_sessionVisionEncoder`），每个都会占用大量显存。但 `Florence2Model` **没有实现 `IDisposable`**，建库结束后这些 session 不会被主动释放，导致显存一直被占着。

我的解决方案是让 `FlorencCaptioning` 实现 `IDisposable`，在 `Dispose()` 里通过反射找到 `Florence2Model` 中所有 `InferenceSession` 类型的私有字段，逐一调用 `Dispose()`：

```csharp
var sessionFields = typeof(Florence2Model)
    .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
    .Where(f => f.FieldType == typeof(InferenceSession));

foreach (var field in sessionFields)
    (field.GetValue(_model) as IDisposable)?.Dispose();
```

这样在 `btnBuild_Click` 里 `using var captioning = await FlorencCaptioning.CreateAsync(...)` 就能在建库结束后立即释放全部 Florence2 显存。虽然用反射不太优雅，但在第三方库不提供 Dispose 的情况下这是最实用的办法。

---

## 8. 向量数据库层

这一层用的就是我刚完成的 **Vorcyc Quiver**。v3 的时候我还在用自己手搓的文件存储方案，向量搜索也是暴力遍历，数据量一大就慢得不行。所以我单独做了 Quiver 这个嵌入式向量数据库——这个以图搜图项目正好是它的第一个实战场景，开发过程中两边互相推动：这边发现 Quiver 的 API 不好用或者性能有瓶颈，就回去改 Quiver；Quiver 加了新功能，这边马上试用验证。

### 8.1 数据实体 `ImageDb`

三个属性：

| 属性 | 特性 | 说明 |
|---|---|---|
| `Filename` | `[QuiverKey]` | 文件绝对路径作为主键，天然唯一 |
| `ImageFeature` | `[QuiverVector(1024)]` | DINOv2 提取的 1024 维向量 |
| `Caption` | 无 | Florence2 生成的文字描述，nullable |

用文件路径做主键有个好处：去重非常简单，`QuiverSet.Exists(filename)` 一查就知道这张图有没有处理过。

### 8.2 数据库上下文 `ImageDbContext`

继承 `QuiverDbContext`，配置项我是这样选的：

| 选项 | 我的选择 | 理由 |
|---|---|---|
| `StorageFormat` | `Binary` | 二进制格式，读写速度快 |
| `DefaultMetric` | `Euclidean` | 欧氏距离，配合 L2 归一化后的向量效果好 |
| `EnableWal` | `true` | 开 WAL 日志，防止写入中断导致数据损坏 |
| `WalCompactionThreshold` | `10000` | 攒够 1 万条 WAL 记录再压缩合并 |
| `WalFlushToDisk` | `false` | 不立即刷盘——建库时要频繁写入，立即刷盘会严重拖慢速度 |

数据库文件路径是运行目录下的 `features.vdb`。

---

## 9. 工具类

### 9.1 `IOHelper.cs`

几个工具方法：

- `GetExecutionDirectory()` / `GetFileAbsolutePath()` — 路径处理，基于 `Assembly.GetExecutingAssembly().Location`
- `Except(filenames, existingImages)` — 迭代器方法，`yield return` 不在数据库中的文件路径。建库时用来去重，只处理新增文件
- `CleanupAsync(db)` — `Task.Run` 后台遍历所有记录，`File.Exists` 检查，`RemoveByKey` 删除无效项

### 9.2 `ShellFolderSelector.cs`

在资源管理器中定位文件，我用的是 P/Invoke 调 Windows Shell API：

```
ILCreateFromPathW → SHOpenFolderAndSelectItems → ILFree
```

为什么不用简单的 `explorer.exe /select,"path"`？因为那个方案 **不兼容特殊字符路径**。比如文件名里有 `?`、`#` 之类的字符，`explorer.exe` 会识别不了。Shell API 走的是 PIDL（Item ID List），不存在这个问题。

这个坑我是在处理用户下载的表情包图片时踩到的——文件名里一堆 emoji 和特殊符号。

### 9.3 `NullToVisibilityConverter.cs`

很小的一个转换器：`null` → `Visible`，非 `null` → `Collapsed`。

我用它来控制查询区域和预览区域的占位提示文字——`Image.Source` 为 `null` 时显示「将图片拖放到此处」，图片一加载就自动消失。比手动管理 Visibility 优雅多了。

### 9.4 `FilePathToThumbnailConverter.cs`

这个转换器用在 DataGrid 的缩略图列，把文件路径转成 60px 宽的 `BitmapImage`。

**关键设计**：内部用 `ConcurrentDictionary<string, BitmapImage?>` 做缓存。

为什么需要缓存？因为 WPF 的 DataGrid 有虚拟化机制——滚动时行会被回收复用，每次滚回来转换器都会被重新触发。如果不缓存，滚一下就要重新从磁盘读几十张图，卡得明显。加了缓存后，每张图只读一次磁盘。

加载逻辑里我还做了两个优化：
- `DecodePixelWidth = 60`：只解码 60px 宽度，不把原图整个解码进内存。一张 4000×3000 的照片完整解码要十几 MB，缩略图只要几 KB。
- `Freeze()`：线程安全，WPF 里 Freezable 对象不 Freeze 的话跨线程访问会炸。

文件不存在或加载失败就返回 `null`，不显示缩略图——不会因为一张坏图崩掉整个列表。

### 9.5 `Usings.cs`

全局 using 声明，把常用的命名空间都拉进来了。

---

## 10. 完整文件清单

```
ImageContentRetrieval_v4/
├── App.xaml                          # WPF 应用定义（RoundUI Light 主题、AccentColor、全局字体）
├── App.xaml.cs                       # 应用入口（空壳）
├── MainWindow.xaml                   # 主窗口 UI
├── MainWindow.xaml.cs                # 主窗口逻辑
├── DinoV2Embedder.cs                 # DINOv2 ONNX 推理
├── FlorencCaptioning.cs              # Florence2 推理（异步工厂模式）
├── NullToVisibilityConverter.cs      # WPF 转换器 — null → Visible
├── FilePathToThumbnailConverter.cs   # WPF 转换器 — 文件路径 → 缩略图（带缓存）
├── QuiverDb/
│   ├── ImageDb.cs                    # 数据实体
│   └── ImageDbContext.cs             # Quiver 数据库上下文
├── IOHelper.cs                       # IO 工具
├── ShellFolderSelector.cs            # Windows Shell P/Invoke
├── Usings.cs                         # 全局 using
├── AssemblyInfo.cs                   # 程序集信息（自动生成）
├── model_zoo/
│   ├── DinoV2/
│   │   └── domp_v2_q4.onnx          # DINOv2 量化模型
│   └── florence2/
│       ├── decoder_model_merged.onnx # Florence2 解码器
│       ├── embed_tokens.onnx         # Florence2 Token 嵌入
│       ├── encoder_model.onnx        # Florence2 编码器
│       └── vision_encoder.onnx       # Florence2 视觉编码器
└── ImageContentRetrieval_v4.csproj   # SDK-style 项目文件
```

---

## 11. 我的设计决策与思考

回顾整个项目，这些是我做的关键决策和背后的思考：

| 我做了什么 | 我为什么这么做 |
|---|---|
| 从 InceptionV3 瓶颈层换到 DINOv2 CLS token | InceptionV3 是分类模型，瓶颈层特征为 ImageNet 分类优化，不是为检索设计的；DINOv2 是自监督视觉基础模型，CLS token 天生就是图像语义表示，检索效果好了一大截 |
| 用 Vorcyc Quiver 替代 v3 的手搓文件存储 | v3 的暴力遍历在数据量大时太慢，Quiver 是我刚完成的嵌入式向量数据库，这个项目正好做它的第一个实战验证 |
| 建库用局部 Embedder，查询用全局 Embedder | 建库时 DINOv2 + Florence2 两个大模型同时吃显存，用完必须释放；查询时只需要 DINOv2 一个模型，常驻显存免去反复加载的开销 |
| 每 200 条增量保存 | 实测的平衡点——太频繁会拖慢建库（磁盘 IO），太稀疏万一中断就白干了 |
| 支持 CancellationToken | 几万张图的文件夹建库可能要跑半小时+，用户必须能中途喊停 |
| `SynchronizationContext.Post` 更新 UI | 建库在 `Task.Run` 线程池里跑，WPF 控件只能在 UI 线程上摸，只能 Post 回去 |
| 任务栏进度条 | 建库时窗口经常被最小化，任务栏上能看到进度是刚需 |
| 缩略图转换器用 `ConcurrentDictionary` 缓存 | DataGrid 虚拟化会反复触发转换器，不缓存的话滚动就是幻灯片 |
| `DecodePixelWidth = 60` | 缩略图只显示 44×44，没必要把 4K 原图解码进内存 |
| `BitmapImage.Freeze()` | WPF 里 Freezable 不 Freeze 跨线程就炸 |
| `ReadImage` 用字节流不用路径 | `BitmapImage` 直接绑路径会锁文件句柄，影响后续操作 |
| Shell API 定位文件而非 `explorer.exe /select` | `explorer.exe` 处理不了文件名里有 `?` `#` emoji 的路径，Shell API 走 PIDL 没这个问题 |
| Florence2 用异步工厂 `CreateAsync` | 首次要下载模型，构造函数不能 async，只能用工厂 |
| WAL + `WalFlushToDisk=false` | 要 WAL 保障写入安全，但不要立即刷盘——建库时写入太频繁，刷盘会严重拖慢速度 |
| 支持 10 种图片格式（JPG/JPEG/JFIF/PNG/WebP/BMP/GIF/TIF/TIFF/TGA） | ImageSharp 3.x 原生支持这些格式的解码，覆盖了绝大多数常见图片场景；TGA 在游戏/3D 领域常见。WPF 预览端除 TGA 外均由 WIC 原生支持 |
| 扩展名集中管理（`SupportedExtensions` / `SupportedWildcards` / `IsSupportedImage`） | 建库扫描、文件对话框、拖放过滤三处都要用到相同的扩展名列表，重复维护容易遗漏。抽成静态字段后增删格式只改一处 |
| `FlorencCaptioning` 实现 `IDisposable`，反射释放 `Florence2Model` 内部 session | `Florence2Model` 未实现 `IDisposable`，其 4 个 `InferenceSession` 在建库后不会被释放，导致显存泄漏。通过反射访问私有字段是在第三方库不提供清理接口时的实用兜底方案 |

---

## 12. 详细使用方法

### 12.1 环境准备

#### 你需要什么

| 项目 | 最低要求 | 推荐配置 |
|---|---|---|
| 操作系统 | Windows 7 SP1+ | Windows 10 / 11 |
| 运行时 | .NET 10 Runtime | .NET 10 Runtime |
| GPU（可选） | 任意 NVIDIA CUDA 兼容显卡 | NVIDIA RTX 系列（≥ 4GB 显存） |
| CUDA 工具包 | CUDA 11.8+ / 12.x | CUDA 12.x + cuDNN 9.x |
| 内存 | 4 GB | 8 GB+（大量图片建库时） |

> 没有 NVIDIA GPU 也能用——把推理设备切到 CPU 就行。CPU 建库会慢不少，但日常检索速度差别不大。

#### 获取 DINOv2 模型

1. 去 HuggingFace 下载：<https://huggingface.co/onnx-community/dinov2-large-ONNX/tree/main>
2. 我推荐 **`domp_v2_q4.onnx`**（量化版），体积小推理快。想要更高精度也可以用未量化版本，但需要改 `DinoV2Embedder.cs` 里的模型路径常量。
3. 放到 `ImageContentRetrieval_v4/model_zoo/DinoV2/domp_v2_q4.onnx`

#### Florence2 模型

不用管。首次建库时应用会自动下载到 `model_zoo/florence2/`，后续直接用本地缓存。

#### 编译运行

1. Visual Studio 2026+（装好 .NET 10 SDK）打开解决方案
2. NuGet 包会自动还原
3. 确认 DINOv2 模型文件就位
4. `F5` 跑起来

---

### 12.2 界面一览

启动后的主界面：

```
┌──────────────────────────────────────────────────────────────┐
│  📁 构建特征库  🧹 清理无效数据    推理设备 [CUDA ▼]  🔍 以图搜图  返回数 [10 ▼]  │
├──────────────────────────────────┬───────────────────────────┤
│                                  │                           │
│   ┌───────────────────────────┐  │                           │
│   │   查询图显示区 / 拖放区     │  │     选中结果预览区         │
│   │   "将图片拖放到此处..."     │  │     "选择左侧结果以预览"   │
│   └───────────────────────────┘  │                           │
│                                  │                           │
│   ┌───────────────────────────┐  │                           │
│   │ 缩略图 │ 相似度 │ 文件名 │ 描述 │  │                           │
│   │  ...   │ 95.23% │ ...   │ ... │  │                           │
│   │  ...   │ 91.07% │ ...   │ ... │  │                           │
│   └───────────────────────────┘  │                           │
│                                  │                           │
├──────────────────────────────────┴───────────────────────────┤
│  [████░░░░░░░░]  已建模 1234 个图像文件（CUDA）                │
└──────────────────────────────────────────────────────────────┘
```

左右栏中间有个分割条，可以拖拽调整比例。

---

### 12.3 建库

**第一步必须先建库，不然没东西可搜。**

1. 工具栏右侧选推理设备（默认 CUDA，没 GPU 就选 CPU）
2. 点「📁 构建特征库」或按 `Ctrl+B`
3. 选一个图片文件夹——会递归扫描所有子目录下的 JPG/JPEG/JFIF/PNG/WebP/BMP/GIF/TIFF/TGA
4. 等着就行：
   - 进度条和状态文字实时更新
   - 任务栏图标上也有进度条
   - 每 200 张自动保存一次
5. 完成后弹窗显示处理数量和耗时

**中途可以取消**——点红色的「⏹ 取消建库」按钮。已处理的数据不会丢，下次对同一文件夹建库会自动跳过。

**可以多次建库**：
- 不同文件夹分别建库，数据都存在同一个 `features.vdb` 里
- 同一个文件夹再次建库，只处理新增文件

---

### 12.4 以图搜图

两种方式，我更推荐拖放：

**方式一：按钮**
1. 设好返回数（10/50/100 或自己输入）
2. 点「🔍 以图搜图」或 `Ctrl+R`
3. 选一张查询图片

**方式二：拖放（推荐）**
1. 直接从资源管理器把图片拖到查询区域
2. 边框会高亮提示
3. 松手自动检索

> 拖放是最快的——不用弹对话框，拖进去就出结果。连续拖不同图片可以快速对比。

结果列表四列：缩略图、相似度（百分比）、文件名、Florence2 生成的描述。

---

### 12.5 浏览结果

- **单击**某行 → 右栏大图预览
- **双击**某行 → 资源管理器定位文件（兼容特殊字符路径）
- 分割条可以拖拽，调整列表和预览区的比例

---

### 12.6 清理无效数据

如果某些图片被手动删除/移动/重命名了，数据库里就有残留记录。

点「🧹 清理无效数据」→ 后台自动检查 → 删掉文件已不存在的记录 → 完成。

我建议大规模整理图片后跑一次。

---

### 12.7 切换推理设备

工具栏下拉框选 `CUDA` / `CPU`，立即生效（会重新加载模型）。

要注意的是：建库时的设备和查询时的设备是 **独立** 的。建库时的设备在点击建库按钮那一刻就确定了，建库期间下拉框是禁用的。查询时的设备跟着下拉框走，可以随时切。

如果切 CUDA 报错，说明 GPU 或驱动不可用，切回 CPU 就行。

---

### 12.8 快捷键

| 快捷键 | 功能 |
|---|---|
| `Ctrl+B` | 构建特征库 |
| `Ctrl+R` | 以图搜图 |

建库期间两个都不可用（按了没反应）。

---

### 12.9 我日常的使用流程

**首次**：启动 → `Ctrl+B` 选文件夹 → 等建库完 → 拖图片进去搜。

**后续**：新增图片后再 `Ctrl+B` 同一个文件夹，只处理新增的。删了图片就点清理。

**连续检索**：不断往查询区拖不同图片，结果秒刷新，对比检索效果非常快。

---

### 12.10 数据库文件

| 文件 | 说明 |
|---|---|
| `features.vdb` | 主数据库文件 |
| `features.vdb.wal` | WAL 写前日志（可能存在） |

都在运行目录下。备份就复制这两个文件，重置就删掉重新建库。

---

### 12.11 常见问题

**Q：启动就报错闪退**  
→ 检查 `model_zoo/DinoV2/domp_v2_q4.onnx` 是否存在。如果用 CUDA 模式，检查 GPU 驱动和 CUDA 环境。

**Q：Florence2 模型下载失败**  
→ 网络问题。检查网络连接，或手动把四个 ONNX 文件放到 `model_zoo/florence2/`。

**Q：切到 CUDA 报错**  
→ 没有 NVIDIA GPU 或驱动版本不对。跑下 `nvidia-smi` 看看，搞不定就用 CPU 模式。

**Q：提示「特征库无有效项，请先建库！」**  
→ 没建过库，或者 `features.vdb` 被删了。建一次库就好。

**Q：缩略图不显示**  
→ 原始图片文件被删了或移走了。点清理去掉无效记录。

**Q：建库太慢**  
→ 确认用的是 CUDA 不是 CPU。关掉其他吃显存的程序。建库是一次性的，后续增量建库只处理新增。

**Q：返回数输入后提示「请输入有效数字！」**  
→ 返回数框里输了非数字的东西，改成正整数就行。

---

*cyclone_dll @ Vorcyc*
