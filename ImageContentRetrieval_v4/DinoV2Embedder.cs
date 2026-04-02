using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Image = SixLabors.ImageSharp.Image;
using Size = SixLabors.ImageSharp.Size;

public class DinoV2Embedder : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _outputName;
    private const int InputSize = 518;        // DINOv2-large 推荐分辨率
    private const string _modelPath = "./model_zoo/DinoV2/domp_v2_q4.onnx";

    public DinoV2Embedder()
    {
        var options = new SessionOptions();
        options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        // GPU 加速建议（根据你的硬件选择）：
        options.AppendExecutionProvider_CUDA(0);     // NVIDIA GPU
        //options.AppendExecutionProvider_DML(0);      // Windows DirectML

        _session = new InferenceSession(_modelPath, options);

        _inputName = _session.InputMetadata.Keys.FirstOrDefault() ?? "pixel_values";
        _outputName = _session.OutputMetadata.Keys.FirstOrDefault() ?? "last_hidden_state";

        Console.WriteLine($"模型加载成功！");
        Console.WriteLine($"Input Name : {_inputName}");
        Console.WriteLine($"Output Name: {_outputName}");
    }

    /// <summary>
    /// 提取单张图片的 1024 维 embedding（CLS token）
    /// </summary>
    public float[] ExtractEmbedding(string imagePath)
    {
        using var image = Image.Load<Rgb24>(imagePath);
        var tensor = Preprocess(image);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_inputName, tensor)
        };

        using var results = _session.Run(inputs);
        var outputTensor = results.First(r => r.Name == _outputName).AsTensor<float>();

        // 形状通常 [1, num_tokens, 1024]，取 CLS token (index 0)
        var embedding = new float[1024];
        for (int i = 0; i < 1024; i++)
        {
            embedding[i] = outputTensor[0, 0, i];
        }

        Normalize(embedding);   // L2 归一化，适合余弦相似度
        return embedding;
    }

    System.Diagnostics.Stopwatch _stopwatch = new System.Diagnostics.Stopwatch();

    /// <summary>
    /// 批量提取（推荐用于海量图片）
    /// </summary>
    public List<float[]> ExtractEmbeddingsBatch(IEnumerable<string> imagePaths)
    {
        var embeddings = new List<float[]>();
        foreach (var path in imagePaths)
        {
            try
            {
                Console.WriteLine(  path);
                _stopwatch.Restart();
                embeddings.Add(ExtractEmbedding(path));
                _stopwatch.Stop();
                Console.WriteLine(  _stopwatch.Elapsed);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"处理 {path} 失败: {ex.Message}");
            }
        }
        return embeddings;
    }

    private DenseTensor<float> Preprocess(Image<Rgb24> image)
    {
        image.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(InputSize, InputSize),
            Mode = ResizeMode.Crop   // Center crop
        }));

        var tensor = new DenseTensor<float>([1, 3, InputSize, InputSize]);

        for (int y = 0; y < InputSize; y++)
        {
            for (int x = 0; x < InputSize; x++)
            {
                var pixel = image[x, y];
                tensor[0, 0, y, x] = (pixel.R / 255f - 0.485f) / 0.229f;
                tensor[0, 1, y, x] = (pixel.G / 255f - 0.456f) / 0.224f;
                tensor[0, 2, y, x] = (pixel.B / 255f - 0.406f) / 0.225f;
            }
        }
        return tensor;
    }

    private static void Normalize(float[] vector)
    {
        float norm = MathF.Sqrt(vector.Sum(x => x * x));
        if (norm > 1e-8f)
        {
            for (int i = 0; i < vector.Length; i++)
                vector[i] /= norm;
        }
    }

    public void Dispose() => _session?.Dispose();
}