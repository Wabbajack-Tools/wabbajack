﻿using System;
using System.IO;
using System.Threading.Tasks;

namespace Wabbajack.Common
{
    public interface IStreamFactory
    {
        ValueTask<Stream> GetStream();
        
        DateTime LastModifiedUtc { get; }
        
        IPath Name { get; }
        
    }
    public class NativeFileStreamFactory : IStreamFactory
    {
        protected AbsolutePath _file;

        public NativeFileStreamFactory(AbsolutePath file, IPath path)
        {
            _file = file;
            Name = path;
        }
        
        public NativeFileStreamFactory(AbsolutePath file)
        {
            _file = file;
            Name = file;
        }
        public async ValueTask<Stream> GetStream()
        {
            return await _file.OpenRead();
        }

        public DateTime LastModifiedUtc => _file.LastModifiedUtc;
        public IPath Name { get; }
    }
    
}
