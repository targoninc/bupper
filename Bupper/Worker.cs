using System.Collections.Concurrent;
using System.Diagnostics;
using Bupper.Models;
using Bupper.Models.Settings;
using Microsoft.Extensions.Options;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
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
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("folder count: {FolderCount}", settings.CurrentValue.Folders.Count);
            }
            
            foreach (BupperFolder folder in settings.CurrentValue.Folders)
            {
                await SyncFolder(folder);
            }
            
            await Task.Delay(60000, stoppingToken);
        }
    }

    private async Task SyncFolder(BupperFolder folder)
    {
        BupperDirectory directory = GetDirectory(folder.Path);

        if (folder.Type == BupperFolderType.ProjectsRoot)
        {
            await SyncProjectsRoot(folder.Path, folder.Name, directory);
        }
    }

    private async Task SyncProjectsRoot(string basePath, string baseName, BupperDirectory directory)
    {
        logger.LogInformation("Syncing projects root: {Path}", basePath);
        foreach (BupperTarget target in settings.CurrentValue.Targets)
        {
            using SftpClient client = new(target.Host, target.User, KeyFile);
            await client.ConnectAsync(default);

            await CreateDirectoryIfNotExists(client, target.Folder + "/" + baseName);

            foreach (BupperDirectory project in directory.Directories)
            {
                await SyncDirectory(client, basePath, baseName, project, target);
            }
            client.Disconnect();
        }
    }
    
    private async Task SyncDirectory(SftpClient client, string baseFolder, string baseName, BupperDirectory directory,
        BupperTarget target)
    {
        string dirRelative = directory.Path.Replace(baseFolder, "");
        string dir = dirRelative.TrimStart('\\');
        
        if (dir.Length > 0)
        {
            await UploadDirectory(client, directory.Path, target, baseName + "/" + dir);
        }
        else
        {
            await UploadDirectory(client, directory.Path, target, baseName);
        }
    }
    
    private async Task CreateDirectoryIfNotExists(SftpClient client, string targetPath)
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

    private async Task UploadDirectory(SftpClient client, string localPath, BupperTarget target, string targetPath)
    {
        logger.LogInformation("Uploading directory: {LocalPath} to {TargetHost}", localPath, target.Host);
        await CreateDirectoryIfNotExists(client, target.Folder + "/" + targetPath);

        string[] files = Directory.GetFiles(localPath, "*", SearchOption.AllDirectories);

        await AnsiConsole.Progress()
            .StartAsync(async ctx =>
            {
                ProgressTask task = ctx.AddTask("[green]Uploading files[/]", maxValue: files.Length);
                SemaphoreSlim semaphore = new(20);

                await Task.WhenAll(files.Select(async file =>
                {
                    await semaphore.WaitAsync();
                    task.Description = $"[green]Uploading {20 - semaphore.CurrentCount}/{files.Length} files[/]";
                    try
                    {
                        await TryUploadFile(client, localPath, target, targetPath, file, task);
                    }
                    finally
                    {
                        semaphore.Release();
                        task.Description = $"[green]Uploading {20 - semaphore.CurrentCount}/{files.Length} files[/]";
                    }
                }));
            });
    }

    private async Task TryUploadFile(SftpClient client, string localPath, BupperTarget target,
        string targetPath,
        string file, ProgressTask task)
    {
        string relativePath = Path.GetRelativePath(localPath, file);
        string remotePath = Path.Combine(target.Folder, targetPath, relativePath).Replace("\\", "/");
                        
        string? fileFolder = Path.GetDirectoryName(relativePath);
        string uploadFolder = (target.Folder + "/" + targetPath + "/" + fileFolder).Replace("\\", "/");
        await CreateDirectoryIfNotExists(client, uploadFolder);

        await using FileStream fileStream = new(file, FileMode.Open);
                        
        if (!LocalFileIsNewer(client, file, remotePath) && FileSizesAreEqual(client, file, remotePath))
        {
            task.Increment(1);
            return;
        }
        
        const int maxRetries = 3;
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                IAsyncResult asyncResult = client.BeginUploadFile(fileStream, remotePath);
                await Task.Factory.FromAsync(asyncResult, client.EndUploadFile);
                break;
            } catch (Exception e)
            {
                if (i == maxRetries - 1)
                {
                    logger.LogError(e, "Failed to upload file: {File} to {RemoteFile} into {Folder}", file, remotePath, uploadFolder);
                    throw;
                }
                                
                if (e.Message.Contains("The session is not open."))
                {
                    await client.ConnectAsync(default);
                }
            }
        }

        task.Increment(1);
    }

    private static bool LocalFileIsNewer(SftpClient client, string localPath, string remotePath)
    {
        DateTime localLastModifiedTime = File.GetLastWriteTime(localPath);
        try
        {
            DateTime remoteLastModifiedTime = client.GetLastWriteTime(remotePath);
            return localLastModifiedTime > remoteLastModifiedTime;   
        } catch (SftpPathNotFoundException)
        {
            return true;
        }
    }
    
    private static bool FileSizesAreEqual(SftpClient client, string localPath, string remotePath)
    {
        long localFileSize = new FileInfo(localPath).Length;
        try
        {
            ISftpFile file = client.Get(remotePath);
            return file.Attributes.Size == localFileSize;
        } catch (SftpPathNotFoundException)
        {
            return false;
        }
    }

    private BupperDirectory GetDirectory(string folderPath)
    {
        string[] directories = Directory.GetDirectories(folderPath);
        string[] files = Directory.GetFiles(folderPath);
        
        return new BupperDirectory(folderPath, files, directories.Select(GetDirectory).ToList());
    }
}