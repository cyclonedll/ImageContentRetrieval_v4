using Vorcyc.Quiver;

namespace ImageContentRetrieval_v4.QuiverDb;

public class ImageDb
{

    [QuiverKey]
    public string Filename { get; set; } = string.Empty;

    [QuiverVector(1024/*, DistanceMetric.Euclidean, Optional = true*/)]
    //[QuiverIndex(VectorIndexType.HNSW, M = 32, EfConstruction = 300, EfSearch = 100)]
    public float[] ImageFeature { get; set; } = [];

    public string? Caption { get; set; } = null;

}
