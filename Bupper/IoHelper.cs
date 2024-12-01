using System.IO.Compression;
using Bupper.Models;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace Bupper;

public static class IoHelper
{
    public static bool LocalFileIsNewer(SftpClient client, string localPath, string remotePath)
    {
        DateTime localLastModifiedTime = File.GetLastWriteTime(localPath);
        try
        {
            DateTime remoteLastModifiedTime = client.GetLastWriteTime(remotePath);
            return localLastModifiedTime > remoteLastModifiedTime;
        }
        catch (SftpPathNotFoundException)
        {
            return true;
        }
        catch (SshException)
        {
            return true;
        }
    }

    public static async Task<long> CompressAndGetSize(string tmpFileName, FileStream fileStream)
    {
        await using FileStream tmpFileStream = new(tmpFileName, FileMode.Create, FileAccess.Write);
        await using GZipStream compressionStream = new(tmpFileStream, CompressionMode.Compress);
        await fileStream.CopyToAsync(compressionStream);
            
        await fileStream.FlushAsync();
        await tmpFileStream.FlushAsync();
        await compressionStream.FlushAsync();
        return new FileInfo(tmpFileName).Length;
    }

    public static async Task DecompressFileStreamAsync(FileStream tmpFileStream, string decompressedFileName)
    {
        await using GZipStream decompressionStream = new(tmpFileStream, CompressionMode.Decompress);
        await using FileStream outputStream = new(decompressedFileName, FileMode.Create, FileAccess.ReadWrite);
        await decompressionStream.CopyToAsync(outputStream);
        
        await tmpFileStream.FlushAsync();
        await decompressionStream.FlushAsync();
        await outputStream.FlushAsync();
    }
    
    public static bool FileSizesAreEqual(SftpClient client, long localFileSize, string remotePath)
    {
        try
        {
            ISftpFile file = client.Get(remotePath);
            return file.Attributes.Size == localFileSize;
        } catch (SftpPathNotFoundException)
        {
            return false;
        }
        catch (SshException)
        {
            return true;
        }
    }

    public static BupperDirectory GetDirectory(string folderPath)
    {
        string[] directories = Directory.GetDirectories(folderPath);
        string[] files = Directory.GetFiles(folderPath);
        
        return new BupperDirectory(folderPath, files, directories.Select(GetDirectory).ToList());
    }
}