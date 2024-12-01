namespace Bupper.Models;

public record BupperDirectory(string Path, ICollection<string> Files, ICollection<BupperDirectory> Directories);