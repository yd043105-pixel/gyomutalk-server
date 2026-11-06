using System.IO;
using System.IO.Compression;

namespace SchoolMessenger.Shared;

public sealed class FolderAttachment : IDisposable
{
    readonly DirectoryInfo temporary;
    public string Path { get; }
    FolderAttachment(string name)
    {
        temporary=Directory.CreateTempSubdirectory("gyomutalk-folder-");
        Path=System.IO.Path.Combine(temporary.FullName,name+".zip");
    }
    public static FolderAttachment Create(string path,long limit)
    {
        var root=new DirectoryInfo(path);
        var name=EntryName(root.Name);
        if(name.Length>196)throw new IOException("폴더 이름을 196자 이하로 줄여 주세요.");
        if(limit<=0)throw TooLarge();
        var folder=new FolderAttachment(name);
        try
        {
            var available=new DriveInfo(System.IO.Path.GetPathRoot(folder.Path)!).AvailableFreeSpace-500L*1024*1024;
            if(available<=0)throw new IOException("폴더 ZIP을 만들 임시 저장 공간이 부족합니다.");
            folder.Write(root,name,Math.Min(limit,available),available<limit);
            return folder;
        }
        catch { folder.Dispose(); throw; }
    }
    void Write(DirectoryInfo root,string name,long limit,bool diskLimited)
    {
        long copied=0;
        var count=0;
        var buffer=new byte[128*1024];
        using var output=File.Create(Path);
        using(var zip=new ZipArchive(output,ZipArchiveMode.Create,true))
        {
            var directories=new Stack<(DirectoryInfo Directory,string Name)>();directories.Push((root,name));
            while(directories.TryPop(out var item))
            {
                Inspect(item.Directory);
                zip.CreateEntry(item.Name+"/");
                foreach(var entry in item.Directory.EnumerateFileSystemInfos())
                {
                    Inspect(entry);
                    var entryPath=item.Name+"/"+EntryName(entry.Name);
                    if(entry is DirectoryInfo child){directories.Push((child,entryPath));continue;}
                    using var source=File.OpenRead(entry.FullName);
                    if(source.Length>limit-copied)throw TooLarge(diskLimited);
                    using var destination=zip.CreateEntry(entryPath,CompressionLevel.NoCompression).Open();
                    int read;
                    while((read=source.Read(buffer))>0)
                    {
                        copied+=read;
                        if(copied>limit)throw TooLarge(diskLimited);
                        destination.Write(buffer,0,read);
                    }
                    if(output.Length>limit)throw TooLarge(diskLimited);
                }
            }
        }
        if(output.Length>limit)throw TooLarge(diskLimited);

        void Inspect(FileSystemInfo entry)
        {
            if(++count>20_000)throw new IOException("폴더에 항목이 너무 많습니다. 여러 폴더로 나눠 첨부하세요.");
            if((entry.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new IOException($"바로가기나 연결 폴더를 제외해 주세요. {entry.Name}");
        }
    }
    static string EntryName(string name)
    {
        if(string.IsNullOrWhiteSpace(name)||name is "." or ".."||name.Any(c=>char.IsControl(c)||"/\\:<>\"|?*".Contains(c)))
            throw new IOException("폴더 안에 ZIP으로 저장하기 어려운 이름이 있습니다. 이름을 변경해 주세요.");
        return name;
    }
    static IOException TooLarge(bool diskLimited=false)=>new(diskLimited?"폴더 ZIP을 만들 임시 저장 공간이 부족합니다.":"폴더를 ZIP으로 첨부하면 메시지에 남은 첨부 용량을 초과합니다.");
    public void Dispose()
    {
        try{File.Delete(Path);temporary.Delete();}
        catch(Exception error) when(error is IOException or UnauthorizedAccessException) { }
    }
}
