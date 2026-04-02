using ImageContentRetrieval_v4.QuiverDb;
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

    /// <summary>DINOv2 模型封装，负责将图像提取为 1024 维特征向量。</summary>
    private DinoV2Embedder _embedder;

    /// <summary>
    /// 窗口加载完成后初始化：加载向量数据库并实例化 DINOv2 模型。
    /// 若初始化失败则提示错误并关闭应用。
    /// </summary>
    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _syncContext = System.Threading.SynchronizationContext.Current;

        // 初始化期间禁用交互，隐藏进度条
        this.IsEnabled = false;
        processBar1.Visibility = Visibility.Collapsed;

        try
        {
            await _db.LoadAsync();
            _embedder = new();

            this.IsEnabled = true;
            lblInfo.Content = $"已建模 {_db.Images.Count} 个图像文件";
        }
        catch (Exception exp)
        {
            MessageBox.Show(exp.Message);
            MessageBox.Show(exp.InnerException?.Message);
            Application.Current.Shutdown();
        }
    }

    /// <summary>
    /// 构建特征库：选择文件夹后，扫描图像文件并逐一提取 DINOv2 特征与 Florence2 描述，
    /// 写入向量数据库。已存在的文件会自动跳过，每处理 100 张自动保存一次。
    /// </summary>
    private async void btnBuild_Click(object sender, RoutedEventArgs e)
    {
        // 弹出文件夹选择对话框
        var dialog = new CommonOpenFileDialog
        {
            IsFolderPicker = true
        };

        if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
        {
            // 禁用所有按钮，显示进度条
            this.IsEnabled = btnCleanup.IsEnabled = btnBuild.IsEnabled = btnRetrieval.IsEnabled = false;
            processBar1.Visibility = Visibility.Visible;

            var captioning = await FlorencCaptioning.CreateAsync();

            var sw = System.Diagnostics.Stopwatch.StartNew();

            // 递归扫描文件夹下所有支持格式的图像文件
            var folder = dialog.FileName;
            var files = Directory.EnumerateFiles(folder, "*.jpg", SearchOption.AllDirectories);
            files = files.Concat(Directory.EnumerateFiles(folder, "*.jpeg", SearchOption.AllDirectories));
            files = files.Concat(Directory.EnumerateFiles(folder, "*.jfif", SearchOption.AllDirectories));
            files = files.Concat(Directory.EnumerateFiles(folder, "*.png", SearchOption.AllDirectories));

            // 排除数据库中已存在的文件，避免重复提取特征
            files = IOHelper.Except(files, _db.Images);

            var totalCount = files.Count();
            int proceedCount = 0;

            processBar1.Maximum = totalCount;

            // 在后台线程中逐一处理图像
            await Task.Run(async () =>
            {
                foreach (var file in files)
                {
                    // 提取 DINOv2 特征向量与 Florence2 图像描述，组装为数据实体
                    var imageDb = new ImageDb
                    {
                        Filename = file,
                        ImageFeature = _embedder.ExtractEmbedding(file),
                        Caption = captioning.GetCaption(file)
                    };
                    _db.Images.Add(imageDb);
                    proceedCount++;

                    // 每 100 条批量保存一次，防止意外中断导致大量数据丢失
                    if (proceedCount % 200 == 0) await _db.SaveChangesAsync();

                    // 通过同步上下文回到 UI 线程更新进度
                    _syncContext.Post(state =>
                    {
                        processBar1.Value = proceedCount;
                        lblInfo.Content = $"正在对新图像文件建模：({proceedCount}/{totalCount})";
                    }, null);
                }
            });

            // 最终保存剩余未落盘的数据
            await _db.SaveChangesAsync();

            sw.Stop();

            lblInfo.Content = $"已建模 {_db.Images.Count} 个图像文件";

            var newCount = proceedCount;
            Vorcyc.RoundUI.Windows.Controls.ModernDialog.ShowMessage($"成功对 {proceedCount} 个文件建库，耗时 {sw.Elapsed}", "建库完成", MessageBoxButton.OK);

            // 恢复按钮状态，隐藏进度条
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