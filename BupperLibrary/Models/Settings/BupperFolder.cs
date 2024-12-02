namespace BupperLibrary.Models.Settings;

public record BupperFolder(
    string Path,
    string Name,
    BupperFolderType Type
);