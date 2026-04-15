using Florence2;
using Microsoft.ML.OnnxRuntime;
using System.Reflection;

namespace ImageContentRetrieval_v4;

public class FlorencCaptioning : IDisposable
{

    private Florence2Model? _model;
    private const string modelPath = "./model_zoo/florence2";

    private FlorencCaptioning(Florence2Model model)
    {
        _model = model;
    }

    public static async Task<FlorencCaptioning> CreateAsync(SessionOptions options)
    {
        // 1. 下载模型
        var downloader = new FlorenceModelDownloader(modelPath);
        if (!downloader.IsReady)
        {
            await downloader.DownloadModelsAsync(null);
        }

        var model = new Florence2Model(downloader, options);
        return new FlorencCaptioning(model);
    }


    public string GetCaption(string imagePath)
    {
        using var imageStream = File.OpenRead(imagePath);
        var streams = new Stream[] { imageStream };
        var results = _model!.Run(TaskTypes.CAPTION, streams, null, CancellationToken.None);
        return results[0].PureText;
    }

    /// <summary>
    /// 释放 Florence2Model 内部持有的 4 个 InferenceSession 以回收显存。
    /// Florence2Model 未实现 IDisposable，因此通过反射访问其私有字段。
    /// </summary>
    public void Dispose()
    {
        if (_model is null) return;

        var sessionFields = typeof(Florence2Model)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(f => f.FieldType == typeof(InferenceSession));

        foreach (var field in sessionFields)
            (field.GetValue(_model) as IDisposable)?.Dispose();

        _model = null;
    }
}
