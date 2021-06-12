﻿using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Wabbajack.Lib.CompilationSteps
{
    public class IgnoreWabbajackInstallCruft : ACompilationStep
    {
        private readonly HashSet<string> _cruftFiles;

        public IgnoreWabbajackInstallCruft(ACompiler compiler) : base(compiler)
        {
            _cruftFiles = new HashSet<string>
            {
                "7z.dll", "7z.exe", "vfs_staged_files\\", "nexus.key_cache", "patch_cache\\"
            };
        }

        public override async ValueTask<Directive?> Run(RawSourceFile source)
        {
            if (!_cruftFiles.Any(f => source.Path.StartsWith(f))) return null;
            var result = source.EvolveTo<IgnoredDirectly>();
            result.Reason = "Wabbajack Cruft file";
            return result;
        }
    }
}
