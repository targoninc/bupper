using System.Diagnostics;
using Bupper.Models;
using Bupper.Models.Settings;
using Microsoft.Extensions.Options;
using Renci.SshNet;
using Renci.SshNet.Common;
using Spectre.Console;

namespace Bupper;

public class Worker(
    ILogger<Worker> logger,
    IOptionsMonitor<BupperSettings> settings
) : BackgroundService
{
    private static readonly IPrivateKeySource KeyFile =
        new PrivateKeyFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh",
            "id_rsa"));
    private static readonly SemaphoreSlim SftpConnectionSemaphore = new(1);
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("folder count: {FolderCount}", settings.CurrentValue.Folders.Count);
            }
            
            Stopwatch stopwatch = Stopwatch.StartNew();
            foreach (BupperFolder folder in settings.CurrentValue.Folders)
            {
                await SyncFolderAsync(folder).ConfigureAwait(false);
            }
            stopwatch.Stop();
            logger.LogInformation("Synced all folders in {Elapsed}", stopwatch.Elapsed);
            
            await Task.Delay(60000 * 10, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task SyncFolderAsync(BupperFolder folder)
    {
        BupperDirectory directory = IoHelper.GetDirectory(folder.Path);

        if (folder.Type == BupperFolderType.ProjectsRoot)
        {
            await SyncProjectsRootAsync(folder.Path, folder.Name, directory).ConfigureAwait(false);
        }
    }

    private async Task SyncProjectsRootAsync(string basePath, string baseName, BupperDirectory directory)
    {
        logger.LogInformation("Syncing projects root: {Path}", basePath);
        Stopwatch stopwatch = Stopwatch.StartNew();
        foreach (BupperTarget target in settings.CurrentValue.Targets)
        {
            using SftpClient client = new(target.Host, target.User, KeyFile);
            await client.ConnectAsync(default).ConfigureAwait(false);

            await SftpActions.CreateSftpDirectoryIfNotExists(client, target.Folder + "/" + baseName).ConfigureAwait(false);

            foreach (BupperDirectory project in directory.Directories)
            {
                await SyncDirectoryAsync(client, basePath, baseName, project, target).ConfigureAwait(false);
            }
            client.Disconnect();
        }
        stopwatch.Stop();
        logger.LogInformation("Synced projects root: {Path} to all targets in {Elapsed}", basePath, stopwatch.Elapsed);
    }
    
    private async Task SyncDirectoryAsync(SftpClient client, string baseFolder, string baseName, BupperDirectory directory,
        BupperTarget target)
    {
        string dirRelative = directory.Path.Replace(baseFolder, "");
        string dir = dirRelative.TrimStart('\\');
        Stopwatch stopwatch = Stopwatch.StartNew();

        string dirName = baseName;
        if (dir.Length > 0)
        {
            dirName += "/" + dir;
        }
        await UploadDirectoryAsync(client, directory.Path, target, dirName).ConfigureAwait(false);
        
        stopwatch.Stop();
        logger.LogInformation("Uploaded directory: {LocalPath} to {TargetFolder} in {Elapsed}", directory.Path, target.Folder, stopwatch.Elapsed);
    }

    private async Task UploadDirectoryAsync(SftpClient client, string localPath, BupperTarget target, string targetPath)
    {
        await SftpActions.CreateSftpDirectoryIfNotExists(client, target.Folder + "/" + targetPath).ConfigureAwait(false);

        string[] files = Directory.GetFiles(localPath, "*", SearchOption.AllDirectories);

        await AnsiConsole.Progress()
            .StartAsync(ctx =>
            {
                ProgressTask task = ctx.AddTask("[green]Uploading files[/]", maxValue: files.Length);
                SemaphoreSlim semaphore = new(20);
                int done = 0;

                return Task.WhenAll(files.Select(async file =>
                {
                    await semaphore.WaitAsync().ConfigureAwait(false);
                    task.Description = $"[green]Uploading | {20 - semaphore.CurrentCount} RUNNING | {done} / {files.Length} DONE[/]";
                    try
                    {
                        await TryUploadFileAsync(client, localPath, target, targetPath, file, task).ConfigureAwait(false);
                    }
                    finally
                    {
                        semaphore.Release();
                        done++;
                        //task.Description = $"[green]Uploading | {20 - semaphore.CurrentCount} RUNNING | {done} / {files.Length} DONE[/]";
                    }
                }));
            }).ConfigureAwait(false);
    }

    private async Task TryUploadFileAsync(SftpClient client, string localPath, BupperTarget target,
        string targetPath,
        string file, ProgressTask task)
    {
        string relativePath = Path.GetRelativePath(localPath, file);
        string remotePath =
            Path.Combine(target.Folder, targetPath, relativePath).Replace("\\", "/") + ".gz";

        await using FileStream fileStream = new(file, FileMode.Open, FileAccess.Read);
        string tmpFileName = Path.GetTempFileName();
        long compressedSize = await IoHelper.CompressAndGetSize(tmpFileName, fileStream).ConfigureAwait(false);
        
        bool localIsNewer = IoHelper.LocalFileIsNewer(client, file, remotePath);
        if (!localIsNewer && IoHelper.FileSizesAreEqual(client, compressedSize, remotePath))
        {
            task.Increment(1);
            return;
        }

        await using (FileStream readTmpFileStream = new(tmpFileName, FileMode.Open, FileAccess.Read))
        {
            await UploadCompressedFileAsync(client, target, targetPath, file, relativePath, readTmpFileStream,
                remotePath).ConfigureAwait(false);
        }
        
        File.Delete(tmpFileName);
        task.Increment(1);
    }

    private async Task UploadCompressedFileAsync(SftpClient client, BupperTarget target, string targetPath, string file,
        string relativePath, FileStream tmpFileStream, string remotePath)
    {
        string? fileFolder = Path.GetDirectoryName(relativePath);
        string uploadFolder = (target.Folder + "/" + targetPath + "/" + fileFolder).Replace("\\", "/");
        await SftpActions.CreateSftpDirectoryIfNotExists(client, uploadFolder).ConfigureAwait(false);

        const int maxRetries = 3;
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                await SftpActions.UploadFileStreamToSftp(client, tmpFileStream, remotePath).ConfigureAwait(false);
                break;
            }
            catch (SshException)
            {
                await TryToReconnectSftpAsync(client).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                if (i == maxRetries - 1)
                {
                    logger.LogError(e, "Failed to upload file: {File} to {RemoteFile} into {Folder}", file,
                        remotePath,
                        uploadFolder);
                    throw;
                }

                await HandleFileUploadErrorAsync(client, e, remotePath).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleFileUploadErrorAsync(SftpClient client, Exception e, string remotePath)
    {
        if (e.Message.Contains("The session is not open.") ||
            e.Message.Contains("Cannot access a disposed object."))
        {
            await TryToReconnectSftpAsync(client).ConfigureAwait(false);
        }
        else
        {
            logger.LogWarning(e,"Failed to upload file, retrying ({RemotePath})", remotePath);
        }
    }

    private static async Task TryToReconnectSftpAsync(SftpClient client)
    {
        await SftpConnectionSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!client.IsConnected)
            {
                await client.ConnectAsync(default).ConfigureAwait(false);
            }
        }
        finally
        {
            SftpConnectionSemaphore.Release();                        
        }
    }
}