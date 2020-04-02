﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Threading.Tasks;
using Alphaleonis.Win32.Filesystem;
using Wabbajack.Common;
using Wabbajack.Common.CSP;
using Directory = Alphaleonis.Win32.Filesystem.Directory;
using File = System.IO.File;
using FileInfo = Alphaleonis.Win32.Filesystem.FileInfo;
using Path = Alphaleonis.Win32.Filesystem.Path;

namespace Wabbajack.VirtualFileSystem
{
    public class Context
    {
        static Context()
        {
            Utils.Log("Cleaning VFS, this may take a bit of time");
            Utils.DeleteDirectory(StagingFolder).Wait();
        }
        public const ulong FileVersion = 0x03;
        public const string Magic = "WABBAJACK VFS FILE";

        private static readonly AbsolutePath StagingFolder = ((RelativePath)"vfs_staging").RelativeToWorkingDirectory();
        public IndexRoot Index { get; private set; } = IndexRoot.Empty;

        /// <summary>
        /// A stream of tuples of ("Update Title", 0.25) which represent the name of the current task
        /// and the current progress.
        /// </summary>
        public IObservable<(string, float)> ProgressUpdates => _progressUpdates;
        private readonly Subject<(string, float)> _progressUpdates = new Subject<(string, float)>();

        public StatusUpdateTracker UpdateTracker { get; set; } = new StatusUpdateTracker(1);

        public WorkQueue  Queue { get; }
        public bool UseExtendedHashes { get; set; }

        public Context(WorkQueue queue, bool extendedHashes = false)
        {
            Queue = queue;
            UseExtendedHashes = extendedHashes;
        }
        public static TemporaryDirectory GetTemporaryFolder()
        {
            return new TemporaryDirectory(((RelativePath)Guid.NewGuid().ToString()).RelativeTo(StagingFolder));
        }

        public async Task<IndexRoot> AddRoot(AbsolutePath root)
        {
            var filtered = Index.AllFiles.Where(file => file.IsNative && ((AbsolutePath) file.Name).Exists).ToList();

            var byPath = filtered.ToImmutableDictionary(f => f.Name);

            var filesToIndex = root.EnumerateFiles().Distinct().ToList();
            
            var results = Channel.Create(1024, ProgressUpdater<VirtualFile>($"Indexing {root}", filesToIndex.Count));

            var allFiles = await filesToIndex
                            .PMap(Queue, async f =>
                            {
                                if (byPath.TryGetValue(f, out var found))
                                {
                                    if (found.LastModified == f.LastModifiedUtc.AsUnixTime() && found.Size == f.Size)
                                        return found;
                                }

                                return await VirtualFile.Analyze(this, null, f, f, 0);
                            });

            var newIndex = await IndexRoot.Empty.Integrate(filtered.Concat(allFiles).ToList());

            lock (this)
            {
                Index = newIndex;
            }

            return newIndex;
        }

        public async Task<IndexRoot> AddRoots(List<AbsolutePath> roots)
        {
            var native = Index.AllFiles.Where(file => file.IsNative).ToDictionary(file => file.StagedPath);

            var filtered = Index.AllFiles.Where(file => ((AbsolutePath)file.Name).Exists).ToList();

            var filesToIndex = roots.SelectMany(root => root.EnumerateFiles()).ToList();

            var allFiles = await filesToIndex
                .PMap(Queue, async f =>
                {
                    Utils.Status($"Indexing {Path.GetFileName((string)f)}");
                    if (native.TryGetValue(f, out var found))
                    {
                        if (found.LastModified == f.LastModifiedUtc.AsUnixTime() && found.Size == f.Size)
                            return found;
                    }

                    return await VirtualFile.Analyze(this, null, f, f, 0);
                });

            var newIndex = await IndexRoot.Empty.Integrate(filtered.Concat(allFiles).ToList());

            lock (this)
            {
                Index = newIndex;
            }

            return newIndex;
        }

        class Box<T>
        {
            public T Value { get; set; }
        }

        private Func<IObservable<T>, IObservable<T>> ProgressUpdater<T>(string s, float totalCount)
        {
            if (totalCount == 0)
                totalCount = 1;

            var box = new Box<float>();
            return sub => sub.Select(itm =>
            {
                box.Value += 1;
                _progressUpdates.OnNext((s, box.Value / totalCount));
                return itm;
            });
        }

        public async Task WriteToFile(AbsolutePath filename)
        {
            await using var fs = filename.Create();
            await using var bw = new BinaryWriter(fs, Encoding.UTF8, true);
            fs.SetLength(0);

            bw.Write(Encoding.ASCII.GetBytes(Magic));
            bw.Write(FileVersion);
            bw.Write((ulong) Index.AllFiles.Count);

            (await Index.AllFiles
                    .PMap(Queue, f =>
                    {
                        var ms = new MemoryStream();
                        using var ibw = new BinaryWriter(ms, Encoding.UTF8, true);
                        f.Write(ibw);
                        return ms;
                    }))
                .Do(ms =>
                {
                    var size = ms.Position;
                    ms.Position = 0;
                    bw.Write((ulong) size);
                    ms.CopyTo(fs);
                });
            Utils.Log($"Wrote {fs.Position.ToFileSizeString()} file as vfs cache file {filename}");
        }

        public async Task IntegrateFromFile(AbsolutePath filename)
        {
            try
            {
                await using var fs = filename.OpenRead();
                using var br = new BinaryReader(fs, Encoding.UTF8, true);
                var magic = Encoding.ASCII.GetString(br.ReadBytes(Encoding.ASCII.GetBytes(Magic).Length));
                var fileVersion = br.ReadUInt64();
                if (fileVersion != FileVersion || magic != Magic)
                    throw new InvalidDataException("Bad Data Format");

                var numFiles = br.ReadUInt64();

                var files = Enumerable.Range(0, (int) numFiles)
                    .Select(idx =>
                    {
                        var size = br.ReadUInt64();
                        var bytes = new byte[size];
                        br.BaseStream.Read(bytes, 0, (int) size);
                        return VirtualFile.Read(this, bytes);
                    }).ToList();
                var newIndex = await Index.Integrate(files);
                lock (this)
                {
                    Index = newIndex;
                }
            }
            catch (IOException)
            {
                filename.Delete();
            }
        }

        public async Task<Action> Stage(IEnumerable<VirtualFile> files)
        {
            var grouped = files.SelectMany(f => f.FilesInFullPath)
                .Distinct()
                .Where(f => f.Parent != null)
                .GroupBy(f => f.Parent)
                .OrderBy(f => f.Key?.NestingFactor ?? 0)
                .ToList();

            var paths = new List<AbsolutePath>();

            foreach (var group in grouped)
            {
                var tmpPath = ((RelativePath)Guid.NewGuid().ToString()).RelativeTo(StagingFolder);
                await FileExtractor.ExtractAll(Queue, group.Key.StagedPath, tmpPath);
                paths.Add(tmpPath);
                foreach (var file in group)
                    file.StagedPath = file.RelativeName.RelativeTo(tmpPath);
            }

            return () =>
            {
                paths.Do(p =>
                {
                    p.DeleteDirectory();
                });
            };
        }

        public async Task<DisposableList<VirtualFile>> StageWith(IEnumerable<VirtualFile> files)
        {
            return new DisposableList<VirtualFile>(await Stage(files), files);
        }


        #region KnownFiles

        private List<HashRelativePath> _knownFiles = new List<HashRelativePath>();
        private Dictionary<Hash, AbsolutePath> _knownArchives = new Dictionary<Hash, AbsolutePath>();
        public void AddKnown(IEnumerable<HashRelativePath> known, Dictionary<Hash, AbsolutePath> archives)
        {
            _knownFiles.AddRange(known);
            foreach (var (key, value) in archives)
                _knownArchives.TryAdd(key, value);
        }

        public async Task BackfillMissing()
        {
            var newFiles = _knownArchives.ToDictionary(kv => kv.Key,
                kv => new VirtualFile {Name = kv.Value, Size = kv.Value.Size, Hash = kv.Key});

            var parentchild = new Dictionary<(VirtualFile, RelativePath), VirtualFile>();

            void BackFillOne(HashRelativePath file)
            {
                var parent = newFiles[file.BaseHash];
                foreach (var path in file.Paths)
                {
                    if (parentchild.TryGetValue((parent, path), out var foundParent))
                    {
                        parent = foundParent;
                        continue;
                    }

                    var nf = new VirtualFile {Name = path, Parent = parent};
                    parent.Children = parent.Children.Add(nf);
                    parentchild.Add((parent, path), nf);
                    parent = nf;
                }
            }
            _knownFiles.Where(f => f.Paths.Length > 0).Do(BackFillOne);

            var newIndex = await Index.Integrate(newFiles.Values.ToList());

            lock (this)
                Index = newIndex;

            _knownFiles = new List<HashRelativePath>();

        }
        
        #endregion
    }

    public class DisposableList<T> : List<T>, IDisposable
    {
        private Action _unstage;

        public DisposableList(Action unstage, IEnumerable<T> files) : base(files)
        {
            _unstage = unstage;
        }

        public void Dispose()
        {
            _unstage();
        }
    }

    public class IndexRoot
    {
        public static IndexRoot Empty = new IndexRoot();

        public IndexRoot(ImmutableList<VirtualFile> aFiles,
            Dictionary<FullPath, VirtualFile> byFullPath,
            ImmutableDictionary<Hash, ImmutableStack<VirtualFile>> byHash,
            ImmutableDictionary<AbsolutePath, VirtualFile> byRoot,
            ImmutableDictionary<IPath, ImmutableStack<VirtualFile>> byName)
        {
            AllFiles = aFiles;
            ByFullPath = byFullPath;
            ByHash = byHash;
            ByRootPath = byRoot;
            ByName = byName;
        }

        public IndexRoot()
        {
            AllFiles = ImmutableList<VirtualFile>.Empty;
            ByFullPath = new Dictionary<FullPath, VirtualFile>();
            ByHash = ImmutableDictionary<Hash, ImmutableStack<VirtualFile>>.Empty;
            ByRootPath = ImmutableDictionary<AbsolutePath, VirtualFile>.Empty;
            ByName = ImmutableDictionary<IPath, ImmutableStack<VirtualFile>>.Empty;
        }


        public ImmutableList<VirtualFile> AllFiles { get; }
        public Dictionary<FullPath, VirtualFile> ByFullPath { get; }
        public ImmutableDictionary<Hash, ImmutableStack<VirtualFile>> ByHash { get; }
        public ImmutableDictionary<IPath, ImmutableStack<VirtualFile>> ByName { get; set; }
        public ImmutableDictionary<AbsolutePath, VirtualFile> ByRootPath { get; }

        public async Task<IndexRoot> Integrate(ICollection<VirtualFile> files)
        {
            Utils.Log($"Integrating {files.Count} files");
            var allFiles = AllFiles.Concat(files).GroupBy(f => f.Name).Select(g => g.Last()).ToImmutableList();

            var byFullPath = Task.Run(() => allFiles.SelectMany(f => f.ThisAndAllChildren)
                                     .ToDictionary(f => f.FullPath));

            var byHash = Task.Run(() => allFiles.SelectMany(f => f.ThisAndAllChildren)
                                 .Where(f => f.Hash != Hash.Empty)
                                 .ToGroupedImmutableDictionary(f => f.Hash));

            var byName = Task.Run(() => allFiles.SelectMany(f => f.ThisAndAllChildren)
                                 .ToGroupedImmutableDictionary(f => f.Name));

            var byRootPath = Task.Run(() => allFiles.ToImmutableDictionary(f => f.AbsoluteName));

            var result = new IndexRoot(allFiles,
                await byFullPath,
                await byHash,
                await byRootPath,
                await byName);
            Utils.Log($"Done integrating");
            return result;
        }

        public VirtualFile FileForArchiveHashPath(HashRelativePath argArchiveHashPath)
        {
            var cur = ByHash[argArchiveHashPath.BaseHash].First(f => f.Parent == null);
            return argArchiveHashPath.Paths.Aggregate(cur, (current, itm) => ByName[itm].First(f => f.Parent == current));
        }
    }

    public class TemporaryDirectory : IAsyncDisposable
    {
        public TemporaryDirectory(AbsolutePath name)
        {
            FullName = name;
            if (!FullName.Exists)
                FullName.CreateDirectory();
        }

        public AbsolutePath FullName { get; }

        public async ValueTask DisposeAsync()
        {
            if (FullName.Exists)
                await Utils.DeleteDirectory(FullName);
        }
    }
}
