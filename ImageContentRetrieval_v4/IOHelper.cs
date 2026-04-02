using ImageContentRetrieval_v4.QuiverDb;
using System.Runtime.CompilerServices;
using Vorcyc.Quiver;

namespace ImageContentRetrieval_v4;

internal static class IOHelper
{

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string GetExecutionDirectory()
    {
        return System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
    }

    public static string GetFileAbsolutePath(string filename)
    {
        return System.IO.Path.Combine(GetExecutionDirectory(), filename);
    }


    /// <summary>
    ///  从当前特征字典中排除<paramref name="filenames"/>中出现的项，并返回新的序列.
    /// </summary>
    /// <param name="filenames"></param>
    /// <returns></returns>
    public static IEnumerable<string> Except(IEnumerable<string> filenames, QuiverSet<ImageDb> existingImages)
    {
        foreach (var fn in filenames)
        {
            //若不存在则迭代返回
            if (!existingImages.Exists(fn))
                yield return fn;
        }
    }

  
    public static async Task CleanupAsync(ImageDbContext db)
    {
        await Task.Run(async () =>
        {
            var files = from f in db.Images
                        select f.Filename;
            foreach (var file in files)
                if (!File.Exists(file))
                    db.Images.RemoveByKey(file);

            await db.SaveChangesAsync();
        });
    }


}
