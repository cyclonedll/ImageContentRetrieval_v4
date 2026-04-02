using Vorcyc.Quiver;

namespace ImageContentRetrieval_v4.QuiverDb;

public class ImageDbContext : Vorcyc.Quiver.QuiverDbContext
{


    public QuiverSet<ImageDb> Images { get; set; } = null!;


    public ImageDbContext(string dbpath) :
        base(new QuiverDbOptions
        {            
            DatabasePath = dbpath,
            StorageFormat = StorageFormat.Binary,
            DefaultMetric = DistanceMetric.Euclidean,           
            EnableWal = true,
            WalCompactionThreshold = 1_0000,
            WalFlushToDisk = false
        })
    {

    }


}
