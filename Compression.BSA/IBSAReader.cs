﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Compression.BSA
{
    public interface IBSAReader : IDisposable
    {
        /// <summary>
        /// The files defined by the archive
        /// </summary>
        IEnumerable<IFile> Files { get; }

        ArchiveStateObject State { get; }
    }

    public interface IBSABuilder : IDisposable
    {
        Task AddFile(FileStateObject state, Stream src);
        Task Build(string filename);
    }

    public class ArchiveStateObject
    {
        public virtual IBSABuilder MakeBuilder()
        {
            throw new NotImplementedException();
        }
    }

    public class FileStateObject
    {
        public int Index { get; set; }
        public string Path { get; set; }
    }
    
    public interface IFile
    {
        /// <summary>
        /// The path of the file inside the archive
        /// </summary>
        string Path { get; }

        /// <summary>
        /// The uncompressed file size
        /// </summary>
        uint Size { get;  }

        /// <summary>
        /// Get the metadata for the file.
        /// </summary>
        FileStateObject State { get; }

        /// <summary>
        /// Copies this entry to the given stream. 100% thread safe, the .bsa will be opened multiple times
        /// in order to maintain thread-safe access. 
        /// </summary>
        /// <param name="output"></param>
        Task CopyDataToAsync(Stream output);
    }
}
