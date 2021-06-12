﻿using System.Threading.Tasks;

namespace Wabbajack.Lib.CompilationSteps
{
    public class IncludeAll : ACompilationStep
    {
        public IncludeAll(ACompiler compiler) : base(compiler)
        {
        }

        public override async ValueTask<Directive?> Run(RawSourceFile source)
        {
            var inline = source.EvolveTo<InlineFile>();
            inline.SourceDataFile = source.File;
            return inline;
        }
    }
}
