using Florence2;
using Microsoft.ML.OnnxRuntime;

namespace ImageContentRetrieval_v4;

public class FlorencCaptioning 
{

    private Florence2Model _model;
    private const string modelPath = "./model_zoo/florence2";

    private FlorencCaptioning(Florence2Model model)
    {
        _model = model;
    }

    public static async Task<FlorencCaptioning> CreateAsync()
    {
        var sessionOptions = new SessionOptions();
        //        //sessionOptions.AppendExecutionProvider_DML(0); // 0 = 默认 GPU
        sessionOptions.AppendExecutionProvider_CUDA(0);

        // 1. 下载模型
        var downloader = new FlorenceModelDownloader(modelPath);
        if (!downloader.IsReady)
        {
            await downloader.DownloadModelsAsync(null);
        }

        var model = new Florence2Model(downloader, sessionOptions);
        return new FlorencCaptioning(model);
    }


    public string GetCaption(string imagePath)
    {
        using var imageStream = File.OpenRead(imagePath);
        var streams = new Stream[] { imageStream };
        var results = _model.Run(TaskTypes.CAPTION, streams, null, CancellationToken.None);
        return results[0].PureText;
    }

}
