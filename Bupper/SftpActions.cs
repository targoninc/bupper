using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace Bupper;

public static class SftpActions
{
    public static async Task CreateSftpDirectoryIfNotExists(SftpClient client, string targetPath)
    {
        try
        {
            await client.CreateDirectoryAsync(targetPath);
        }
        catch (Exception e)
        {
            // ignored
        }
    }

    public static async Task UploadFileStreamToSftp(SftpClient client, FileStream tmpFileStream, string remotePath)
    {
        tmpFileStream.Seek(0, SeekOrigin.Begin);

        SftpFileStream outputStream = client.Open(remotePath, FileMode.OpenOrCreate, FileAccess.Write);
        await tmpFileStream.CopyToAsync(outputStream);
        await outputStream.FlushAsync();
    }

    private static async Task<string> DownloadAndDecompressFileAsync(SftpClient client, string remotePath)
    {
        SftpFileStream inputStream = client.OpenRead(remotePath);
        string tmpFileName = Path.GetTempFileName();
        string decompressedFileName = Path.GetTempFileName();
        await using FileStream tmpFileStream = new(tmpFileName, FileMode.Create, FileAccess.ReadWrite);
        await inputStream.CopyToAsync(tmpFileStream);
        tmpFileStream.Seek(0, SeekOrigin.Begin);

        await IoHelper.DecompressFileStreamAsync(tmpFileStream, decompressedFileName);
        return decompressedFileName;
    }
}