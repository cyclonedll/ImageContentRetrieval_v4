using ImageContentRetrieval_v4.QuiverDb;
using Microsoft.ML.OnnxRuntime;
using Microsoft.Win32;
using Microsoft.WindowsAPICodePack.Dialogs;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Vorcyc.Quiver;
using Vorcyc.RoundUI.Windows.Controls;

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
    }

    /// <summary>向量数据库上下文，持久化存储图像特征与描述。</summary>
    private ImageDbContext _db = new(IOHelper.GetFileAbsolutePath("features.vdb"));

    /// <summary>UI 线程同步上下文，用于从后台线程安全地更新界面。</summary>
    private System.Threading.SynchronizationContext _syncContext;

    /// <summary>DINOv2 模型封装（全局，仅用于查询检索）。</summary>
    private DinoV2Embedder _embedder;

    private SessionOptions _sessionOptions;

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
    /// </summary>
    private async void btnBuild_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CommonOpenFileDialog
        {
            IsFolderPicker = true
        };

        if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
        {
            // 在禁用界面前读取设备选项
            using var buildOptions = CreateSessionOptions();

            // 禁用界面（含 cbDevice），显示进度条
            this.IsEnabled = btnCleanup.IsEnabled = btnBuild.IsEnabled = btnRetrieval.IsEnabled = false;
            processBar1.Visibility = Visibility.Visible;

            // 构建专用的局部 Embedder，用完即释放
            using var buildEmbedder = new DinoV2Embedder(buildOptions);
            var captioning = await FlorencCaptioning.CreateAsync(buildOptions);

            var sw = System.Diagnostics.Stopwatch.StartNew();

            var folder = dialog.FileName;
            var files = Directory.EnumerateFiles(folder, "*.jpg", SearchOption.AllDirectories);
            files = files.Concat(Directory.EnumerateFiles(folder, "*.jpeg", SearchOption.AllDirectories));
            files = files.Concat(Directory.EnumerateFiles(folder, "*.jfif", SearchOption.AllDirectories));
            files = files.Concat(Directory.EnumerateFiles(folder, "*.png", SearchOption.AllDirectories));

            files = IOHelper.Except(files, _db.Images);

            var totalCount = files.Count();
            int proceedCount = 0;

            processBar1.Maximum = totalCount;

            await Task.Run(async () =>
            {
                foreach (var file in files)
                {
                    var imageDb = new ImageDb
                    {
                        Filename = file,
                        ImageFeature = buildEmbedder.ExtractEmbedding(file),
                        Caption = captioning.GetCaption(file)
                    };
                    _db.Images.Add(imageDb);
                    proceedCount++;

                    if (proceedCount % 200 == 0) await _db.SaveChangesAsync();

                    _syncContext.Post(state =>
                    {
                        processBar1.Value = proceedCount;
                        lblInfo.Content = $"正在对新图像文件建模：({proceedCount}/{totalCount})";
                    }, null);
                }
            });

            await _db.SaveChangesAsync();

            sw.Stop();

            lblInfo.Content = $"已建模 {_db.Images.Count} 个图像文件";

            Vorcyc.RoundUI.Windows.Controls.ModernDialog.ShowMessage($"成功对 {proceedCount} 个文件建库，耗时 {sw.Elapsed}", "建库完成", MessageBoxButton.OK);

            this.IsEnabled = btnCleanup.IsEnabled = btnBuild.IsEnabled = btnRetrieval.IsEnabled = true;
            processBar1.Visibility = Visibility.Collapsed;
        }
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
            // 提取查询图片的特征向量
            var query_feature = _embedder.ExtractEmbedding(ofd.FileName);

            // 校验用户输入的返回数量
            if (!int.TryParse(cbReturnCount.Text, out int return_count))
            {
                MessageBox.Show("请输入有效数字！");
                return;
            }

            // 在向量数据库中执行相似度搜索
            var result = await _db.Images.SearchAsync(e => e.ImageFeature, query_feature, return_count, default);

            dg1.ItemsSource = result;

            // 以字节流方式加载图片，避免文件被锁定
            imgSource.Source = ReadImage(ofd.FileName);
        }
    }

    /// <summary>
    /// DataGrid 选中行变化时，在右侧预览对应的检索结果图片。
    /// </summary>
    private void dg1_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (dg1.SelectedItem != null)
        {
            var row = (QuiverSearchResult<ImageDb>)dg1.SelectedItem;
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
        ShellFolderSelector.LocateFile(row.Entity.Filename);
    }

    /// <summary>
    /// 清理无效数据：移除数据库中文件已不存在的记录并保存。
    /// </summary>
    private async void btnCleanup_Click(object sender, RoutedEventArgs e)
    {
        this.IsEnabled = btnCleanup.IsEnabled = btnBuild.IsEnabled = btnRetrieval.IsEnabled = false;

        await IOHelper.CleanupAsync(_db);

        lblInfo.Content = $"已建模 {_db.Images.Count} 个图像文件";
        this.IsEnabled = btnCleanup.IsEnabled = btnRetrieval.IsEnabled = btnBuild.IsEnabled = true;
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
        return bi;
    }



    #endregion


}