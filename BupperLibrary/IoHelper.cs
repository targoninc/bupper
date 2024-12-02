using System.IO.Compression;
using BupperLibrary.Models;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace BupperLibrary;

public static class IoHelper
{
    public static string GetConfigDirectory()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TargonInc", "Bupper", "Config");
    }
    
    public static Task EnsureConfigDirectoryExistsAsync()
    {
        string configDirectory = GetConfigDirectory();
        if (!Directory.Exists(configDirectory))
        {
            Directory.CreateDirectory(configDirectory);
        }
        
        string configFile = Path.Combine(configDirectory, "bupper-options.json");
        Console.WriteLine(configFile);
        if (!File.Exists(configFile))
        {
            File.WriteAllText(configFile, "{}");
        }

        return Task.CompletedTask;
    }
    
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

    public static async Task<long> CompressAndGetSizeAsync(string tmpFileName, FileStream fileStream)
    {
        await using FileStream tmpFileStream = new(tmpFileName, FileMode.Create, FileAccess.Write);
        await using GZipStream compressionStream = new(tmpFileStream, CompressionMode.Compress);
        await fileStream.CopyToAsync(compressionStream).ConfigureAwait(false);
            
        await fileStream.FlushAsync().ConfigureAwait(false);
        await tmpFileStream.FlushAsync().ConfigureAwait(false);
        await compressionStream.FlushAsync().ConfigureAwait(false);
        return new FileInfo(tmpFileName).Length;
    }

    public static async Task DecompressFileStreamAsync(FileStream tmpFileStream, string decompressedFileName)
    {
        await using GZipStream decompressionStream = new(tmpFileStream, CompressionMode.Decompress);
        await using FileStream outputStream = new(decompressedFileName, FileMode.Create, FileAccess.ReadWrite);
        await decompressionStream.CopyToAsync(outputStream).ConfigureAwait(false);
        
        await tmpFileStream.FlushAsync().ConfigureAwait(false);
        await decompressionStream.FlushAsync().ConfigureAwait(false);
        await outputStream.FlushAsync().ConfigureAwait(false);
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