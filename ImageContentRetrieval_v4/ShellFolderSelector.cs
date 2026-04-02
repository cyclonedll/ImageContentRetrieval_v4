namespace ImageContentRetrieval_v4;

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;


//https://www.cnblogs.com/crwy/p/SHOpenFolderAndSelectItems.html

internal static class ShellFolderSelector
{

    /// <summary>
    /// 打开路径并定位文件...对于@"h:\Bleacher Report - Hardaway with the safe call ??.mp4"这样的，explorer.exe /select,d:xxx不认，用API整它
    /// </summary>
    /// <param name="filePath">文件绝对路径</param>
    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern void ILFree(IntPtr pidlList);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr ILCreateFromPathW(string pszPath);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHOpenFolderAndSelectItems(IntPtr pidlList, uint cild, IntPtr children, uint dwFlags);


    /// <summary>
    /// Locate a file or folder in eplorer.exe
    /// </summary>
    /// <param name="filePath"></param>
    public static void LocateFile(string filePath)
    {
        if (!File.Exists(filePath) && !Directory.Exists(filePath))
            return;

        if (Directory.Exists(filePath))
            Process.Start(@"explorer.exe", "/select,\"" + filePath + "\"");
        else
        {
            IntPtr pidlList = ILCreateFromPathW(filePath);
            if (pidlList != IntPtr.Zero)
            {
                try
                {
                    Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(pidlList, 0, IntPtr.Zero, 0));
                }
                finally
                {
                    ILFree(pidlList);
                }
            }
        }
    }


}
