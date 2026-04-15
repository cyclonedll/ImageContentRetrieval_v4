# Vorcyc Image Content Retrieval v4

基于深度学习的**图像内容检索**（Content-Based Image Retrieval, CBIR）桌面应用程序。
用户可以对本地图像文件夹建立特征库，然后通过选择一张查询图片，在特征库中检索出视觉上最相似的图像。

这是该项目的第四版。上一版（v3）使用 Google **InceptionV3** 模型，提取瓶颈层（bottleneck layer）输出作为图像 embedding。v4 将特征提取器升级为 Facebook **DINOv2**——一个自监督视觉基础模型，其 CLS token 天生就是图像级语义表示（1024 维），无需微调即可用于相似度检索，在语义相似场景下效果远优于 InceptionV3 的瓶颈层特征。同时 v4 新增了 **Florence2** 图像描述生成能力，为每张图片自动生成英文 Caption。

---

## 功能概览

| 功能 | 说明 |
|---|---|
| **构建特征库** | 选择本地文件夹，自动扫描其中的 JPG / JPEG / JFIF / PNG / WebP / BMP / GIF / TIFF / TGA 图像，提取视觉特征与文字描述并持久化存储 |
| **以图搜图** | 选择一张查询图片，在已建库的图像中按相似度排序返回最匹配的结果（支持自定义返回数量） |
| **拖放搜图** | 将图片文件直接拖放到查询区域，自动执行检索（带高亮反馈） |
| **取消建库** | 支持中途取消建库操作，已处理的部分自动保存至数据库 |
| **清理无效数据** | 自动移除特征库中已不存在的文件记录，保持数据库整洁 |
| **推理设备切换** | 在顶部工具栏可随时切换 CUDA（GPU）或 CPU 推理，建库与查询可分别使用不同设备 |
| **缩略图预览** | 检索结果列表中显示缩略图（带内存缓存，避免滚动时重复读取磁盘） |
| **任务栏进度** | 建库时在 Windows 任务栏显示进度条，方便窗口最小化后监控进度 |
| **键盘快捷键** | `Ctrl+B` 构建特征库，`Ctrl+R` 以图搜图 |
| **资源管理器定位** | 双击检索结果行，直接在 Windows 资源管理器中定位对应文件 |

---

## 技术栈

- **框架**：.NET 10 / WPF（`net10.0-windows7.0`）
- **UI 库**：[Vorcyc.RoundUI](https://www.nuget.org/packages/Vorcyc.RoundUI) — 圆角现代化 WPF 控件（Light 主题）
- **图像特征提取**：[DINOv2](https://github.com/facebookresearch/dinov2)（量化 ONNX 模型 `domp_v2_q4.onnx`）— 输入 518×518，输出 1024 维 CLS token embedding（L2 归一化）。v4 从 v3 的 InceptionV3 瓶颈层升级而来，语义检索效果显著提升
- **图像描述生成**：[Florence2](https://nuget.org/packages/Florence2) — 微软多模态大模型，自动生成图像文字描述（Caption）
- **ONNX 推理**：[Microsoft.ML.OnnxRuntime.Gpu.Windows](https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime.Gpu.Windows)（支持 CUDA GPU 加速，可在运行时切换 CPU 回退）
- **图像处理**：[SixLabors.ImageSharp](https://www.nuget.org/packages/SixLabors.ImageSharp) — 跨平台图像解码与预处理（Center Crop + ImageNet 归一化）
- **向量数据库**：[Vorcyc.Quiver](https://www.nuget.org/packages/Vorcyc.Quiver) — 作者刚完成的轻量级嵌入式向量数据库，支持向量相似度搜索（Binary 存储，WAL 写前日志）。本项目是 Quiver 的第一个实战场景，两者在开发过程中互相推动迭代
- **系统集成**：[WindowsAPICodePack](https://www.nuget.org/packages/WindowsAPICodePack) — 文件夹选择对话框

### NuGet 依赖版本

| 包 | 版本 |
|---|---|
| Florence2 | 25.12.63049 |
| Microsoft.ML.OnnxRuntime.Gpu.Windows | 1.24.4 |
| SixLabors.ImageSharp | 3.1.12 |
| Vorcyc.Quiver | 2.0.0 |
| Vorcyc.RoundUI | 1.0.0 |
| WindowsAPICodePack | 8.0.15.1 |

---

## 项目结构

```
ImageContentRetrieval_v4/
+-- App.xaml / .cs              # WPF 应用入口（RoundUI Light 主题、全局字体）
+-- MainWindow.xaml / .cs      # WPF 主窗口 - UI 交互与业务流程
+-- DinoV2Embedder.cs          # DINOv2 ONNX 推理封装 - 图像 -> 1024 维向量
+-- FlorencCaptioning.cs       # Florence2 推理封装 - 图像 -> 文字描述
+-- NullToVisibilityConverter.cs       # WPF 值转换器 - null 时显示占位提示文字
+-- FilePathToThumbnailConverter.cs    # WPF 值转换器 - 文件路径转 60px 缩略图（带缓存）
+-- QuiverDb/
|   +-- ImageDb.cs             # 数据实体（文件名、特征向量、描述）
|   +-- ImageDbContext.cs      # Quiver 向量数据库上下文（Binary + WAL）
+-- IOHelper.cs                # IO 工具（路径处理、去重、数据库清理）
+-- ShellFolderSelector.cs     # Windows Shell API - 在资源管理器中定位文件
+-- Usings.cs                  # 全局 using 声明
+-- model_zoo/
|   +-- DinoV2/
|   |   +-- domp_v2_q4.onnx           # DINOv2 量化模型
|   +-- florence2/
|       +-- decoder_model_merged.onnx  # Florence2 解码器
|       +-- embed_tokens.onnx          # Florence2 Token 嵌入
|       +-- encoder_model.onnx         # Florence2 编码器
|       +-- vision_encoder.onnx        # Florence2 视觉编码器
+-- ImageContentRetrieval_v4.csproj    # 项目文件
```

---

## 工作流程

### 建库流程

```
选择文件夹
    |
读取当前推理设备选项（CUDA / CPU）
    |
创建局部 SessionOptions + DinoV2Embedder + FlorencCaptioning
    |
扫描图像文件（JPG / JPEG / JFIF / PNG / WebP / BMP / GIF / TIFF / TGA）
    |
排除已建库文件（去重）
    |
遍历每个新图像文件：
    +---------------------------+
    |  DINOv2 提取 1024 维特征  |
    |  Florence2 生成文字描述    |
    +---------------------------+
    |
    写入 ImageDb 实体
    |
    每 200 条增量保存至数据库
    |
全部完成后最终保存至 Quiver 向量数据库
    |
释放局部 Embedder（DinoV2Embedder + FlorencCaptioning）与 SessionOptions
```

### 检索流程

```
选择一张查询图片（点击按钮 或 拖放图片到查询区域）
    |
DINOv2 提取查询图片的 1024 维特征
    |
在 Quiver 向量数据库中执行相似度搜索（欧氏距离）
    |
按相似度排序，返回 Top-N 结果
    |
在 DataGrid 中展示（缩略图、相似度、文件名、描述）
    |
点击行预览图片 / 双击定位到资源管理器
```

---

## 环境要求

- **操作系统**：Windows 7+
- **运行时**：.NET 10 Runtime
- **GPU 加速**（推荐）：NVIDIA GPU + CUDA（通过 `OnnxRuntime.Gpu` 启用）；无 NVIDIA GPU 时可在状态栏切换为 CPU 模式
- **ONNX 模型**：需将模型文件放置于 `model_zoo/` 目录下（构建时自动复制到输出目录），获取方式见下方「模型获取」

---

## 模型获取

### DINOv2

从 Hugging Face 下载 ONNX 格式的 DINOv2-large 模型：

> <https://huggingface.co/onnx-community/dinov2-large-ONNX/tree/main>

推荐使用 **q4 量化版**（`domp_v2_q4.onnx`），体积更小、推理更快；也可使用未量化的 large 版本以获得更高精度。

下载后将模型文件放入 `model_zoo/DinoV2/` 目录。

### Florence2

Florence2 使用其 NuGet 包内置的 `FlorenceModelDownloader` 自动下载模型，**无需手动下载**。应用在构建特征库时通过异步工厂方法 `FlorencCaptioning.CreateAsync()` 初始化，首次调用会自动将模型文件下载至 `model_zoo/florence2/` 目录。

---

## 快速开始

1. 克隆仓库并用 Visual Studio 2026+ 打开解决方案
2. 按照上方「模型获取」章节准备 DINOv2 模型文件（Florence2 模型会自动下载）
3. 编译并运行项目
4. 点击 **「选择文件夹 && 构建特征库」** 选择图像目录进行建库
5. 点击 **「选择图像以检索」** 选取一张图片执行以图搜图

---

## 作者

**cyclone_dll** @ Vorcyc

---

## 仓库

<https://github.com/cyclonedll/ImageContentRetrieval_v4>