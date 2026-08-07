namespace FileOp.Windows.Ntfs;

public interface INtfsHardLinkEnumerator
{
    IReadOnlyList<string> Enumerate(string path);
}
