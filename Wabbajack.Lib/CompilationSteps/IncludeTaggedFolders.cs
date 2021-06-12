﻿using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Wabbajack.Common;

namespace Wabbajack.Lib.CompilationSteps
{
    public class IncludeTaggedFolders : ACompilationStep
    {
        private readonly List<AbsolutePath> _includeDirectly;
        private readonly string _tag;
        private readonly ACompiler _aCompiler;
        private readonly AbsolutePath _sourcePath;

        public IncludeTaggedFolders(ACompiler compiler, string tag) : base(compiler)
        {   
            _aCompiler = (ACompiler)compiler;
            _sourcePath = _aCompiler.SourcePath;
            _tag = tag;
            string rootDirectory = (string)_sourcePath;

            _includeDirectly = Directory.EnumerateFiles(rootDirectory, _tag, SearchOption.AllDirectories)
                .Select(str => (AbsolutePath)str.Replace(_tag, ""))
                .ToList();
        }


        public override async ValueTask<Directive?> Run(RawSourceFile source)
        {
            foreach (var folderpath in _includeDirectly)
            {
                if (!source.AbsolutePath.InFolder(folderpath)) continue;
                var result = source.EvolveTo<InlineFile>();
                result.SourceDataID = await _compiler.IncludeFile(source.AbsolutePath);
                return result;
            }

            return null;
        }
    }

}
