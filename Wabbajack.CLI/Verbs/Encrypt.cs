﻿using System.Threading.Tasks;
using Alphaleonis.Win32.Filesystem;
using CommandLine;
using Wabbajack.Common;

namespace Wabbajack.CLI.Verbs
{
    [Verb("encrypt", HelpText = @"Encrypt local data and store it in AppData\Local\Wabbajack", Hidden = true)]
    public class Encrypt : AVerb
    {
        [Option('n', "name", Required = true, HelpText = @"Credential to encrypt and store in AppData\Local\Wabbajack")]
        public string Name { get; set; } = "";
        
        [IsFile(CustomMessage = "The input file %1 does not exist!")]
        [Option('i', "input", Required = true, HelpText = @"Source data file name")]

        public string Input { get; set; } = "";

        protected override async Task<ExitCode> Run()
        {
            await File.ReadAllBytes(Input).ToEncryptedData(Name);
            return 0;
        }
    }
}
