﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;
using K4os.Hash.Crc;
using Wabbajack.Common;

namespace Wabbajack.VirtualFileSystem
{
    public class VirtualFile
    {
        private static AbsolutePath DBLocation = Consts.LocalAppDataPath.Combine("GlobalVFSCache.sqlite");
        private static string _connectionString;
        private static SQLiteConnection _conn;


        static VirtualFile()
        {
            _connectionString = String.Intern($"URI=file:{DBLocation};Pooling=True;Max Pool Size=100; Journal Mode=Memory;");
            _conn = new SQLiteConnection(_connectionString);
            _conn.Open();

            using var cmd = new SQLiteCommand(_conn);
            cmd.CommandText = @"CREATE TABLE IF NOT EXISTS VFSCache (
            Hash BIGINT PRIMARY KEY,
            Contents BLOB)
            WITHOUT ROWID";
            cmd.ExecuteNonQuery();

        }
        
        private IEnumerable<VirtualFile> _thisAndAllChildren;

        public IPath Name { get; internal set; }

        public RelativePath RelativeName => (RelativePath)Name;

        public AbsolutePath AbsoluteName => (AbsolutePath)Name;


        public FullPath FullPath { get; private set; }

        public Hash Hash { get; internal set; }
        
        public ExtendedHashes ExtendedHashes { get; set; }
        public long Size { get; internal set; }

        public ulong LastModified { get; internal set; }

        public ulong LastAnalyzed { get; internal set; }

        public VirtualFile Parent { get; internal set; }

        public Context Context { get; set; }

        /// <summary>
        ///     Returns the nesting factor for this file. Native files will have a nesting of 1, the factor
        ///     goes up for each nesting of a file in an archive.
        /// </summary>
        public int NestingFactor
        {
            get
            {
                var cnt = 0;
                var cur = this;
                while (cur != null)
                {
                    cnt += 1;
                    cur = cur.Parent;
                }

                return cnt;
            }
        }

        public ImmutableList<VirtualFile> Children { get; internal set; } = ImmutableList<VirtualFile>.Empty;

        public bool IsArchive => Children != null && Children.Count > 0;

        public bool IsNative => Parent == null;

        public IEnumerable<VirtualFile> ThisAndAllChildren
        {
            get
            {
                if (_thisAndAllChildren == null)
                {
                    _thisAndAllChildren = Children.SelectMany(child => child.ThisAndAllChildren).Append(this).ToList();
                }

                return _thisAndAllChildren;
            }
        }


        /// <summary>
        ///     Returns all the virtual files in the path to this file, starting from the root file.
        /// </summary>
        public IEnumerable<VirtualFile> FilesInFullPath
        {
            get
            {
                var stack = ImmutableStack<VirtualFile>.Empty;
                var cur = this;
                while (cur != null)
                {
                    stack = stack.Push(cur);
                    cur = cur.Parent;
                }

                return stack;
            }
        }


        public VirtualFile TopParent => IsNative ? this : Parent.TopParent;


        public T ThisAndAllChildrenReduced<T>(T acc, Func<T, VirtualFile, T> fn)
        {
            acc = fn(acc, this);
            return Children.Aggregate(acc, (current, itm) => itm.ThisAndAllChildrenReduced(current, fn));
        }

        public void ThisAndAllChildrenReduced(Action<VirtualFile> fn)
        {
            fn(this);
            foreach (var itm in Children)
                itm.ThisAndAllChildrenReduced(fn);
        }
        
        private static VirtualFile ConvertFromIndexedFile(Context context, IndexedVirtualFile file, IPath path, VirtualFile vparent, IStreamFactory extractedFile)
        {
            var vself = new VirtualFile
            {
                Context = context,
                Name = path,
                Parent = vparent,
                Size = file.Size,
                LastModified = extractedFile.LastModifiedUtc.AsUnixTime(),
                LastAnalyzed = DateTime.Now.AsUnixTime(),
                Hash = file.Hash
            };
                        
            vself.FillFullPath();

            vself.Children = file.Children.Select(f => ConvertFromIndexedFile(context, f, f.Name, vself, extractedFile)).ToImmutableList();

            return vself;
        }

        private static bool TryGetFromCache(Context context, VirtualFile parent, IPath path, IStreamFactory extractedFile, Hash hash, out VirtualFile found)
        {
            using var cmd = new SQLiteCommand(_conn);
            cmd.CommandText = @"SELECT Contents FROM VFSCache WHERE Hash = @hash";
            cmd.Parameters.AddWithValue("@hash", (long)hash);

            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                var data = IndexedVirtualFile.Read(rdr.GetStream(0));
                found = ConvertFromIndexedFile(context, data, path, parent, extractedFile);
                found.Name = path;
                found.Hash = hash;
                return true;
            }

            found = default;
            return false;
        }

        private IndexedVirtualFile ToIndexedVirtualFile()
        {
            return new IndexedVirtualFile
            {
                Hash = Hash,
                Name = Name,
                Children = Children.Select(c => c.ToIndexedVirtualFile()).ToList(),
                Size = Size
            };
        }


        public static async Task<VirtualFile> Analyze(Context context, VirtualFile parent, IStreamFactory extractedFile,
            IPath relPath, int depth = 0)
        {
            Hash hash;
            if (extractedFile is NativeFileStreamFactory)
            {
                hash = await ((AbsolutePath)extractedFile.Name).FileHashCachedAsync() ?? Hash.Empty;
            } 
            else
            {
                await using var hstream = await extractedFile.GetStream();
                hash = await hstream.xxHashAsync();
            }

            if (TryGetFromCache(context, parent, relPath, extractedFile, hash, out var vself))
            {
                return vself;
            }

            
            await using var stream = await extractedFile.GetStream();
            var sig = await FileExtractor2.ArchiveSigs.MatchesAsync(stream);
            stream.Position = 0;
            
            var self = new VirtualFile
            {
                Context = context,
                Name = relPath,
                Parent = parent,
                Size = stream.Length,
                LastModified = extractedFile.LastModifiedUtc.AsUnixTime(),
                LastAnalyzed = DateTime.Now.AsUnixTime(),
                Hash = hash
            };

            self.FillFullPath(depth);
            
            if (context.UseExtendedHashes)
                self.ExtendedHashes = await ExtendedHashes.FromStream(stream);

            // Can't extract, so return
            if (!sig.HasValue || !FileExtractor2.ExtractableExtensions.Contains(relPath.FileName.Extension)) return self;

            try
            {

                var list = await FileExtractor2.GatheringExtract(context.Queue, extractedFile,
                    _ => true,
                    async (path, sfactory) => await Analyze(context, self, sfactory, path, depth + 1));

                self.Children = list.Values.ToImmutableList();
            }
            catch (EndOfStreamException)
            {
                return self;
            }
            catch (Exception)
            {
                Utils.Error($"Error while examining the contents of {relPath.FileName}");
                throw;
            }

            await using var ms = new MemoryStream();
            self.ToIndexedVirtualFile().Write(ms);
            ms.Position = 0;
            await InsertIntoVFSCache(self.Hash, ms);
            return self;
        }

        private static async Task InsertIntoVFSCache(Hash hash, MemoryStream data)
        {
            await using var cmd = new SQLiteCommand(_conn);
            cmd.CommandText = @"INSERT INTO VFSCache (Hash, Contents) VALUES (@hash, @contents)";
            cmd.Parameters.AddWithValue("@hash", (long)hash);
            var val = new SQLiteParameter("@contents", DbType.Binary) {Value = data.ToArray()};
            cmd.Parameters.Add(val);
            try
            {
                await cmd.ExecuteNonQueryAsync();
            }
            catch (SQLiteException ex)
            {
                if (ex.Message.StartsWith("constraint failed"))
                    return;
                throw;
            }
        }
        public static void VacuumDatabase()
        {
            using var cmd = new SQLiteCommand(_conn);
            cmd.CommandText = @"VACUUM";
            cmd.PrepareAsync();

            cmd.ExecuteNonQuery();
        }

        internal void FillFullPath()
        {
            int depth = 0;
            var self = this;
            while (self.Parent != null)
            {
                depth += 1;
                self = self.Parent;
            }

            FillFullPath(depth);
        }
        
        internal void FillFullPath(int depth)
        {
            if (depth == 0)
            {
                FullPath = new FullPath((AbsolutePath)Name);
            }
            else
            {
                var paths = new RelativePath[depth];
                var self = this;
                for (var idx = depth; idx != 0; idx -= 1)
                {
                    paths[idx - 1] = self.RelativeName;
                    self = self.Parent;
                }
                FullPath = new FullPath(self.AbsoluteName, paths);
            }
            
        }

        private static async Task<IndexedVirtualFile> TryGetContentsFromServer(Hash hash)
        {
            try
            {
                var client = new HttpClient();
                var response =
                    await client.GetAsync($"http://{Consts.WabbajackCacheHostname}/indexed_files/{hash.ToHex()}");
                if (!response.IsSuccessStatusCode)
                    return null;

                using (var stream = await response.Content.ReadAsStreamAsync())
                {
                    return stream.FromJson<IndexedVirtualFile>();
                }
            }
            catch (Exception)
            {
                return null;
            }
        }


        public void Write(BinaryWriter bw)
        {
            bw.Write(Name);
            bw.Write(Size);
            bw.Write(LastModified);
            bw.Write(LastModified);
            bw.Write(Hash);
            bw.Write(Children.Count);
            foreach (var child in Children)
                child.Write(bw);
        }

        public static VirtualFile Read(Context context, byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);
            return Read(context, null, br);
        }

        private static VirtualFile Read(Context context, VirtualFile parent, BinaryReader br)
        {
            var vf = new VirtualFile
            {
                Name = br.ReadIPath(),
                Size = br.ReadInt64(),
                LastModified = br.ReadUInt64(),
                LastAnalyzed = br.ReadUInt64(),
                Hash = br.ReadHash(),
                Context = context,
                Parent = parent,
                Children = ImmutableList<VirtualFile>.Empty
            };
            vf.FullPath = new FullPath(vf.AbsoluteName, new RelativePath[0]);
            var children = br.ReadInt32();
            for (var i = 0; i < children; i++)
            {
                var child = Read(context, vf, br, (AbsolutePath)vf.Name, new RelativePath[0]);
                vf.Children = vf.Children.Add(child);
            }
            return vf;
        }
        
        private static VirtualFile Read(Context context, VirtualFile parent, BinaryReader br, AbsolutePath top, RelativePath[] subpaths)
        {
            var name = (RelativePath)br.ReadIPath();
            subpaths = subpaths.Add(name);
            var vf = new VirtualFile
            {
                Name = name,
                Size = br.ReadInt64(),
                LastModified = br.ReadUInt64(),
                LastAnalyzed = br.ReadUInt64(),
                Hash = br.ReadHash(),
                Context = context,
                Parent = parent,
                Children = ImmutableList<VirtualFile>.Empty,
                FullPath = new FullPath(top, subpaths)
            };

            var children = br.ReadInt32();
            for (var i = 0; i < children; i++)
            {
                var child = Read(context, vf, br,top, subpaths);
                vf.Children = vf.Children.Add(child);
            }
            return vf;
        }

        public HashRelativePath MakeRelativePaths()
        {
            var paths = new RelativePath[FilesInFullPath.Count() - 1];

            var idx = 0;
            foreach (var itm in FilesInFullPath.Skip(1))
            {
                paths[idx] = (RelativePath)itm.Name;
                idx += 1;
            }

            var path = new HashRelativePath(FilesInFullPath.First().Hash, paths);
            return path;
        }
    }

    public class ExtendedHashes
    {
        public string SHA256 { get; set; }
        public string SHA1 { get; set; }
        public string MD5 { get; set; }
        public string CRC { get; set; }

        public static async ValueTask<ExtendedHashes> FromStream(Stream stream)
        {
            var hashes = new ExtendedHashes();
            stream.Position = 0;
            hashes.SHA256 = System.Security.Cryptography.SHA256.Create().ComputeHash(stream).ToHex();
            stream.Position = 0;
            hashes.SHA1 = System.Security.Cryptography.SHA1.Create().ComputeHash(stream).ToHex();
            stream.Position = 0;
            hashes.MD5 = System.Security.Cryptography.MD5.Create().ComputeHash(stream).ToHex();
            stream.Position = 0;

            var bytes = new byte[1024 * 8];
            var crc = new Crc32();
            while (true)
            {
                var read = stream.Read(bytes, 0, bytes.Length);
                if (read == 0) break;
                crc.Update(bytes, 0, read);
            }

            hashes.CRC = crc.DigestBytes().ToHex();

            return hashes;
        }
    }


    public class CannotStageNativeFile : Exception
    {
        public CannotStageNativeFile(string cannotStageANativeFile) : base(cannotStageANativeFile)
        {
        }
    }

    public class UnstagedFileException : Exception
    {
        private readonly FullPath _fullPath;

        public UnstagedFileException(FullPath fullPath) : base($"File {fullPath} is unstaged, cannot get staged name")
        {
            _fullPath = fullPath;
        }
    }
}
