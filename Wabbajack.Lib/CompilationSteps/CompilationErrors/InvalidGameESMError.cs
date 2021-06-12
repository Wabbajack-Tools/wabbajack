﻿using Wabbajack.Common;
using Wabbajack.Common.StatusFeed;

namespace Wabbajack.Lib.CompilationSteps.CompilationErrors
{
    public class InvalidGameESMError : AErrorMessage
    {
        public Hash Hash { get; }
        public AbsolutePath PathToFile { get; }
        private readonly CleanedESM _esm;
        public RelativePath GameFileName => _esm.To.FileName;
        public override string ShortDescription
        {
            get =>
                $"Game file {GameFileName} has a hash of {Hash} which does not match the expected value of {_esm.SourceESMHash}";
        }

        public override string ExtendedDescription
        {
            get =>
                $@"This modlist is setup to perform automatic cleaning of the stock game file {(string)GameFileName} in order to perform this cleaning Wabbajack must first verify that the 
source file is in the correct state. It seems that the file in your game directory has a hash of {Hash} instead of the expect hash of {_esm.SourceESMHash}. This could be caused by
the modlist expecting a different of the game than you currently have installed, or perhaps you have already cleaned the file. You could attempt to fix this error by re-installing
the game, and then attempting to re-install this modlist. Also verify that the version of the game you have installed matches the version expected by this modlist.";
        }

        public InvalidGameESMError(CleanedESM esm, Hash hash, AbsolutePath path)
        {
            Hash = hash;
            PathToFile = path;
            _esm = esm;
        }
    }
}
