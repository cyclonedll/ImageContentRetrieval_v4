using ImageContentRetrieval_v4.QuiverDb;
using Microsoft.ML.OnnxRuntime;
using Microsoft.Win32;
using Microsoft.WindowsAPICodePack.Dialogs;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Vorcyc.Quiver;
using Vorcyc.RoundUI.Windows.Controls;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace ImageContentRetrieval_v4;

/// <summary>
/// 主窗口 — 提供图像特征库的构建、检索与管理功能。
/// </summary>
public partial class MainWindow : RoundNormalWindow
{
    public MainWindow()
    {
        InitializeComponent();
        this.Loaded += MainWindow_Loaded;
        this.TaskbarItemInfo = new TaskbarItemInfo();
    }

    /// <summary>向量数据库上下文，持久化存储图像特征与描述。</summary>
    private ImageDbContext _db = new(IOHelper.GetFileAbsolutePath("features.vdb"));

    /// <summary>UI 线程同步上下文，用于从后台线程安全地更新界面。</summary>
    private System.Threading.SynchronizationContext _syncContext;

    /// <summary>DINOv2 模型封装（全局，仅用于查询检索）。</summary>
    private DinoV2Embedder _embedder;

    private SessionOptions _sessionOptions;

    /// <summary>用于取消建库操作的令牌源。</summary>
    private CancellationTokenSource? _buildCts;

    /// <summary>
    /// 根据当前 cbDevice 选择创建 SessionOptions。
    /// </summary>
    private SessionOptions CreateSessionOptions()
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };
        var device = (cbDevice.SelectedItem as ComboBoxItem)?.Content?.ToString();
        if (device == "CUDA")
            options.AppendExecutionProvider_CUDA(0);
        return options;
    }

    /// <summary>
    /// 窗口加载完成后初始化：加载向量数据库并实例化 DINOv2 模型（用于查询）。
    /// 若初始化失败则提示错误并关闭应用。
    /// </summary>
    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _syncContext = System.Threading.SynchronizationContext.Current;

        this.IsEnabled = false;
        processBar1.Visibility = Visibility.Collapsed;

        try
        {
            await _db.LoadAsync();

            _sessionOptions = CreateSessionOptions();
            _embedder = new(_sessionOptions);

            this.IsEnabled = true;
            var device = (cbDevice.SelectedItem as ComboBoxItem)?.Content?.ToString();
            lblInfo.Content = $"已建模 {_db.Images.Count} 个图像文件（{device}）";
        }
        catch (Exception exp)
        {
            MessageBox.Show(exp.Message);
            MessageBox.Show(exp.InnerException?.Message);
            Application.Current.Shutdown();
        }
    }

    /// <summary>
    /// 构建特征库：选择文件夹后，使用局部 Embedder 逐一提取特征与描述，写入数据库。
    /// 支持中途取消，已处理部分自动保存。
    /// </summary>
    private async void btnBuild_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CommonOpenFileDialog { IsFolderPicker = true };
        if (dialog.ShowDialog() != CommonFileDialogResult.Ok) return;

        // 在禁用界面前读取设备选项
        using var buildOptions = CreateSessionOptions();

        SetBuildingUI(true);
        _buildCts = new CancellationTokenSource();

        // 构建专用的局部 Embedder，用完即释放
        using var buildEmbedder = new DinoV2Embedder(buildOptions);
        var captioning = await FlorencCaptioning.CreateAsync(buildOptions);

        var sw = System.Diagnostics.Stopwatch.StartNew();

        var folder = dialog.FileName;
        var extensions = new[] { "*.jpg", "*.jpeg", "*.jfif", "*.png" };
        var files = extensions.SelectMany(ext =>
            Directory.EnumerateFiles(folder, ext, SearchOption.AllDirectories));

        // 一次性物化列表，避免重复枚举
        var filesList = IOHelper.Except(files, _db.Images).ToList();
        var totalCount = filesList.Count;
        int proceedCount = 0;
        processBar1.Maximum = totalCount;

        bool cancelled = false;

        try
        {
            await Task.Run(async () =>
            {
                foreach (var file in filesList)
                {
                    _buildCts.Token.ThrowIfCancellationRequested();

                    var imageDb = new ImageDb
                    {
                        Filename = file,
                        ImageFeature = buildEmbedder.ExtractEmbedding(file),
                        Caption = captioning.GetCaption(file)
                    };
                    _db.Images.Add(imageDb);
                    proceedCount++;

                    if (proceedCount % 200 == 0) await _db.SaveChangesAsync();

                    _syncContext.Post(_ =>
                    {
                        processBar1.Value = proceedCount;
                        lblInfo.Content = $"正在对新图像文件建模：({proceedCount}/{totalCount})";
                        if (totalCount > 0)
                            TaskbarItemInfo.ProgressValue = (double)proceedCount / totalCount;
                    }, null);
                }
            }, _buildCts.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        await _db.SaveChangesAsync();
        sw.Stop();

        SetBuildingUI(false);
        _buildCts?.Dispose();
        _buildCts = null;

        lblInfo.Content = $"已建模 {_db.Images.Count} 个图像文件";

        if (cancelled)
            ModernDialog.ShowMessage($"建库已取消。已处理 {proceedCount} 个文件，耗时 {sw.Elapsed}", "建库取消", MessageBoxButton.OK);
        else
            ModernDialog.ShowMessage($"成功对 {proceedCount} 个文件建库，耗时 {sw.Elapsed}", "建库完成", MessageBoxButton.OK);
    }

    /// <summary>取消正在进行的建库操作。</summary>
    private void btnCancel_Click(object sender, RoutedEventArgs e) => _buildCts?.Cancel();

    /// <summary>切换建库 / 空闲状态的界面元素。</summary>
    private void SetBuildingUI(bool building)
    {
        btnBuild.IsEnabled = btnCleanup.IsEnabled = btnRetrieval.IsEnabled = cbDevice.IsEnabled = !building;
        btnCancel.Visibility = building ? Visibility.Visible : Visibility.Collapsed;
        processBar1.Visibility = building ? Visibility.Visible : Visibility.Collapsed;
        TaskbarItemInfo.ProgressState = building
            ? TaskbarItemProgressState.Normal
            : TaskbarItemProgressState.None;
    }

    /// <summary>
    /// 以图搜图：选择一张查询图片，提取其特征向量，在数据库中按相似度检索 Top-N 结果。
    /// </summary>
    private async void btnRetrieval_Click(object sender, RoutedEventArgs e)
    {
        if (_db.Images.Count == 0)
        {
            MessageBox.Show("特征库无有效项，请先建库！");
            return;
        }

        var ofd = new OpenFileDialog
        {
            Filter = "JPG图像|*.jpg;*.jpeg;*.jfif;*.png|所有文件|*.*"
        };

        if (ofd.ShowDialog() == true)
        {
            imgSource.Source = ReadImage(ofd.FileName);
            await PerformRetrievalAsync(ofd.FileName);
        }
    }

    /// <summary>
    /// 执行以图搜图检索：提取指定图片的特征向量并在数据库中搜索相似结果。
    /// </summary>
    private async Task PerformRetrievalAsync(string imageFilePath)
    {
        if (_db.Images.Count == 0)
        {
            MessageBox.Show("特征库无有效项，请先建库！");
            return;
        }

        var query_feature = _embedder.ExtractEmbedding(imageFilePath);

        if (!int.TryParse(cbReturnCount.Text, out int return_count))
        {
            MessageBox.Show("请输入有效数字！");
            return;
        }

        var result = await _db.Images.SearchAsync(e => e.ImageFeature, query_feature, return_count, default);
        dg1.ItemsSource = result;
    }

    /// <summary>
    /// DataGrid 选中行变化时，在右侧预览对应的检索结果图片。
    /// </summary>
    private void dg1_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (dg1.SelectedItem != null)
        {
            var row = (QuiverSearchResult<ImageDb>)dg1.SelectedItem;
            if (!File.Exists(row.Entity.Filename))
            {
                imgSelected.Source = null;
                MessageBox.Show($"文件已不存在：{row.Entity.Filename}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            imgSelected.Source = ReadImage(row.Entity.Filename);
        }
    }

    /// <summary>
    /// 双击 DataGrid 行时，在 Windows 资源管理器中定位对应文件。
    /// </summary>
    private void dg1_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (dg1.SelectedItem == null) return;
        var row = (QuiverSearchResult<ImageDb>)dg1.SelectedItem;
        if (!File.Exists(row.Entity.Filename))
        {
            MessageBox.Show($"文件已不存在：{row.Entity.Filename}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ShellFolderSelector.LocateFile(row.Entity.Filename);
    }

    /// <summary>
    /// 清理无效数据：移除数据库中文件已不存在的记录并保存。
    /// </summary>
    private async void btnCleanup_Click(object sender, RoutedEventArgs e)
    {
        btnBuild.IsEnabled = btnCleanup.IsEnabled = btnRetrieval.IsEnabled = false;

        await IOHelper.CleanupAsync(_db);

        lblInfo.Content = $"已建模 {_db.Images.Count} 个图像文件";
        btnBuild.IsEnabled = btnCleanup.IsEnabled = btnRetrieval.IsEnabled = true;
    }

    /// <summary>
    /// 切换推理设备时，重建全局 SessionOptions 和 Embedder。
    /// </summary>
    private void cbDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 初始化完成前忽略（Loaded 中会自行创建）
        if (_embedder is null)
            return;

        try
        {
            _embedder.Dispose();
            _sessionOptions.Dispose();

            _sessionOptions = CreateSessionOptions();
            _embedder = new(_sessionOptions);

            var device = (cbDevice.SelectedItem as ComboBoxItem)?.Content?.ToString();
            lblInfo.Content = $"已建模 {_db.Images.Count} 个图像文件（{device}）";
        }
        catch (Exception exp)
        {
            MessageBox.Show($"切换设备失败：{exp.Message}");
        }
    }

    #region Drag & Drop（拖放 + 高亮反馈）

    private void QueryImage_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>拖入时高亮边框。</summary>
    private void QueryImage_DragEnter(object sender, DragEventArgs e)
    {
        if (sender is Border border && e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            border.BorderThickness = new Thickness(2);
            border.Background = new SolidColorBrush(Color.FromArgb(25, 85, 106, 120));
        }
    }

    /// <summary>拖离时恢复边框。</summary>
    private void QueryImage_DragLeave(object sender, DragEventArgs e)
    {
        ResetDropZoneVisual();
    }

    /// <summary>
    /// 将图片文件拖放到查询区域后自动执行检索。
    /// </summary>
    private async void QueryImage_Drop(object sender, DragEventArgs e)
    {
        ResetDropZoneVisual();

        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        var file = files.FirstOrDefault(f =>
            f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".jfif", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".png", StringComparison.OrdinalIgnoreCase));

        if (file is null) return;

        imgSource.Source = ReadImage(file);
        await PerformRetrievalAsync(file);
    }

    private void ResetDropZoneVisual()
    {
        dropZone.BorderThickness = new Thickness(1);
        dropZone.Background = Brushes.Transparent;
    }

    #endregion

    #region 键盘快捷键

    /// <summary>Ctrl+B 建库，Ctrl+R 检索。</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;

        switch (e.Key)
        {
            case Key.B when btnBuild.IsEnabled:
                btnBuild_Click(sender, e);
                e.Handled = true;
                break;
            case Key.R when btnRetrieval.IsEnabled:
                btnRetrieval_Click(sender, e);
                e.Handled = true;
                break;
        }
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// 以字节流方式读取图片文件，返回不锁定文件的 <see cref="BitmapImage"/>。
    /// 使用 <see cref="BitmapCacheOption.OnLoad"/> 确保流关闭后图片仍可使用。
    /// </summary>
    /// <param name="imgFilename">图片文件的绝对路径。</param>
    /// <returns>加载完成的 <see cref="BitmapImage"/> 实例。</returns>
    private static BitmapImage ReadImage(string imgFilename)
    {
        var imageData = File.ReadAllBytes(imgFilename);
        using var imageStream = new MemoryStream(imageData);
        BitmapImage bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = imageStream;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    #endregion


}