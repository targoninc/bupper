using System.Diagnostics;
using Bupper.Models;
using Bupper.Models.Settings;
using Microsoft.Extensions.Options;
using Renci.SshNet;
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
            
            await Task.Delay(10000, stoppingToken);
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
            foreach (BupperDirectory project in directory.Directories)
            {
                await SyncDirectory(basePath, baseName, project, target);
            }
        }
    }
    
    private async Task SyncDirectory(string baseFolder, string baseName, BupperDirectory directory, BupperTarget target)
    {
        string dirRelative = directory.Path.Replace(baseFolder, "");
        string dir = dirRelative.TrimStart('\\');
        
        if (dir.Length > 0)
        {
            CreateDirectoryIfNotExists(target, baseName + "/" + dir);
            await UploadDirectory(directory.Path, target, baseName + "/" + dir);
        }
        else
        {
            CreateDirectoryIfNotExists(target, baseName);
            await UploadDirectory(directory.Path, target, baseName);
        }
    }
    
    private void CreateDirectoryIfNotExists(BupperTarget target, string targetPath)
    {
        logger.LogInformation("Creating directory: {TargetHost}", target.Host);
        Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ssh",
                Arguments = $"{target.User}@{target.Host} \"mkdir -p \\\"{target.Folder}/{targetPath}\\\"\"", // TODO: Fix shell injection
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        logger.LogInformation("ssh " + process.StartInfo.Arguments);

        process.Start();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            string lastError = process.StandardError.ReadToEnd();
            logger.LogError(lastError);
            string output = process.StandardOutput.ReadToEnd();
            logger.LogError(output);
            throw new Exception($"Failed to create directory on target: {target.Host}");
        }
    }

    private async Task UploadDirectory(string localPath, BupperTarget target, string targetPath)
    {
        logger.LogInformation("Uploading directory: {LocalPath} to {TargetHost}", localPath, target.Host);
        using SftpClient client = new(target.Host, target.User, KeyFile);
        client.Connect();

        string[] files = Directory.GetFiles(localPath, "*", SearchOption.AllDirectories);

        await AnsiConsole.Progress()
            .StartAsync(async ctx =>
            {
                ProgressTask task = ctx.AddTask("[green]Uploading files[/]", maxValue: files.Length);
                SemaphoreSlim semaphore = new(20);

                await Task.WhenAll(files.Select(async file =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        string relativePath = Path.GetRelativePath(localPath, file);
                        string remotePath = Path.Combine(target.Folder, targetPath, relativePath).Replace("\\", "/");

                        await using FileStream fileStream = new(file, FileMode.Open);
                        
                        if (!LocalFileIsNewer(client, file, remotePath) && FileSizesAreEqual(client, file, remotePath))
                        {
                            task.Increment(1);
                            return;
                        }
                        
                        task.Description = $"[green]Uploading file: [/][blue]{Markup.Escape(remotePath)}[/]";
                        IAsyncResult asyncResult = client.BeginUploadFile(fileStream, remotePath);
                        await Task.Factory.FromAsync(asyncResult, ar => client.EndUploadFile(ar));

                        task.Increment(1);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));
            });

        client.Disconnect();
    }

    private static bool LocalFileIsNewer(SftpClient client, string localPath, string remotePath)
    {
        DateTime localLastModifiedTime = File.GetLastWriteTime(localPath);
        DateTime remoteLastModifiedTime = client.GetLastWriteTime(remotePath);
        
        return localLastModifiedTime > remoteLastModifiedTime;
    }
    
    private static bool FileSizesAreEqual(SftpClient client, string localPath, string remotePath)
    {
        long localFileSize = new FileInfo(localPath).Length;
        ISftpFile file = client.Get(remotePath);
        return file.Attributes.Size == localFileSize;
    }

    private BupperDirectory GetDirectory(string folderPath)
    {
        string[] directories = Directory.GetDirectories(folderPath);
        string[] files = Directory.GetFiles(folderPath);
        
        return new BupperDirectory(folderPath, files, directories.Select(GetDirectory).ToList());
    }
}