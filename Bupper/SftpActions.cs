using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace Bupper;

public static class SftpActions
{
    public static async Task CreateSftpDirectoryIfNotExistsAsync(SftpClient client, string targetPath)
    {
        try
        {
            await client.CreateDirectoryAsync(targetPath).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // ignored
        }
    }

    public static async Task UploadFileStreamToSftpAsync(SftpClient client, FileStream tmpFileStream, string remotePath)
    {
        tmpFileStream.Seek(0, SeekOrigin.Begin);

        SftpFileStream outputStream = client.Open(remotePath, FileMode.OpenOrCreate, FileAccess.Write);
        await tmpFileStream.CopyToAsync(outputStream).ConfigureAwait(false);
        await outputStream.FlushAsync().ConfigureAwait(false);
    }

    private static async Task<string> DownloadAndDecompressFileAsync(SftpClient client, string remotePath)
    {
        SftpFileStream inputStream = client.OpenRead(remotePath);
        string tmpFileName = Path.GetTempFileName();
        string decompressedFileName = Path.GetTempFileName();
        await using FileStream tmpFileStream = new(tmpFileName, FileMode.Create, FileAccess.ReadWrite);
        await inputStream.CopyToAsync(tmpFileStream).ConfigureAwait(false);
        tmpFileStream.Seek(0, SeekOrigin.Begin);

        await IoHelper.DecompressFileStreamAsync(tmpFileStream, decompressedFileName).ConfigureAwait(false);
        return decompressedFileName;
    }

    public static async Task TryToReconnectSftpAsync(SftpClient client, SemaphoreSlim semaphore)
    {
        await semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!client.IsConnected)
            {
                await client.ConnectAsync(default).ConfigureAwait(false);
            }
        }
        finally
        {
            semaphore.Release();                        
        }
    }
}