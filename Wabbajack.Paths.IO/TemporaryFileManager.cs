using System;
using System.IO;
using System.Threading;

namespace Wabbajack.Paths.IO;

public class TemporaryFileManager : IDisposable
{
    private readonly AbsolutePath _basePath;

    public TemporaryFileManager() : this(KnownFolders.EntryPoint.Combine("temp"))
    {
    }

    public TemporaryFileManager(AbsolutePath basePath)
    {
        _basePath = basePath;
        _basePath.CreateDirectory();
    }

    public void Dispose()
    {
        for (var retries = 0; retries < 10; retries++)
            try
            {
                if (!_basePath.DirectoryExists())
                    return;
                _basePath.DeleteDirectory();
                return;
            }
            catch (IOException ex)
            {
                Thread.Sleep(1000);
            }
    }

    public TemporaryPath CreateFile(Extension? ext = default)
    {
        var path = _basePath.Combine(Guid.NewGuid().ToString());
        if (path.Extension != default)
            path = path.WithExtension(ext);
        return new TemporaryPath(path);
    }

    public TemporaryPath CreateFolder()
    {
        var path = _basePath.Combine(Guid.NewGuid().ToString());
        path.CreateDirectory();
        return new TemporaryPath(path);
    }
}