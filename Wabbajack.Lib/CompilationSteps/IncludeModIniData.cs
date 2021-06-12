﻿using System.Threading.Tasks;
using Wabbajack.Common;

namespace Wabbajack.Lib.CompilationSteps
{
    public class IncludeModIniData : ACompilationStep
    {
        public IncludeModIniData(ACompiler compiler) : base(compiler)
        {
        }

        public override async ValueTask<Directive?> Run(RawSourceFile source)
        {
            if (!source.Path.StartsWith("mods\\") || source.Path.FileName != Consts.MetaIni) return null;
            var e = source.EvolveTo<InlineFile>();
            e.SourceDataID = await _compiler.IncludeFile(await source.AbsolutePath.ReadAllBytesAsync());
            return e;
        }
    }
}
