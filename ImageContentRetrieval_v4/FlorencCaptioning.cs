using Florence2;
using Microsoft.ML.OnnxRuntime;

namespace ImageContentRetrieval_v4;

public class FlorencCaptioning 
{

    private Florence2Model _model;
    private const string modelPath = "./model_zoo/florence2";

    public FlorencCaptioning()
    {
        var sessionOptions = new SessionOptions();
        //        //sessionOptions.AppendExecutionProvider_DML(0); // 0 = 默认 GPU
        sessionOptions.AppendExecutionProvider_CUDA(0);

        // 1. 下载模型
        var downloader = new FlorenceModelDownloader(modelPath);

        _model = new Florence2Model(downloader, sessionOptions);

    }


    public string GetCaption(string imagePath)
    {
        using var imageStream = File.OpenRead(imagePath);
        var streams = new Stream[] { imageStream };
        var results = _model.Run(TaskTypes.CAPTION, streams, null, CancellationToken.None);
        return results[0].PureText;
    }

}
