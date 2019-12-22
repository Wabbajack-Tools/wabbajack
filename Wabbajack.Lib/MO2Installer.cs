﻿using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using Alphaleonis.Win32.Filesystem;
using IniParser;
using IniParser.Model.Configuration;
using IniParser.Parser;
using Wabbajack.Common;
using Wabbajack.Lib.CompilationSteps.CompilationErrors;
using Wabbajack.Lib.Downloaders;
using Wabbajack.Lib.NexusApi;
using Wabbajack.Lib.Validation;
using Directory = Alphaleonis.Win32.Filesystem.Directory;
using File = Alphaleonis.Win32.Filesystem.File;
using Path = Alphaleonis.Win32.Filesystem.Path;

namespace Wabbajack.Lib
{
    public class MO2Installer : AInstaller
    {
        public bool WarnOnOverwrite { get; set; } = true;

        public override ModManager ModManager => ModManager.MO2;

        public string GameFolder { get; set; }

        public MO2Installer(string archive, ModList modList, string outputFolder, string downloadFolder)
            : base(
                  archive: archive,
                  modList: modList,
                  outputFolder: outputFolder, 
                  downloadFolder: downloadFolder)
        {
        }

        protected override async Task<bool> _Begin(CancellationToken cancel)
        {
            if (cancel.IsCancellationRequested) return false;
            var metric = Metrics.Send("begin_install", ModList.Name);

            ConfigureProcessor(18, await RecommendQueueSize());
            var game = ModList.GameType.MetaData();

            if (GameFolder == null)
                GameFolder = game.GameLocation();

            if (GameFolder == null)
            {
                MessageBox.Show(
                    $"In order to do a proper install Wabbajack needs to know where your {game.MO2Name} folder resides. We tried looking the" +
                    "game location up in the windows registry but were unable to find it, please make sure you launch the game once before running this installer. ",
                    "Could not find game location", MessageBoxButton.OK);
                Utils.Log("Exiting because we couldn't find the game folder.");
                return false;
            }

            if (cancel.IsCancellationRequested) return false;
            UpdateTracker.NextStep("Validating Game ESMs");
            ValidateGameESMs();

            if (cancel.IsCancellationRequested) return false;
            UpdateTracker.NextStep("Validating Modlist");
            await ValidateModlist.RunValidation(Queue, ModList);

            Directory.CreateDirectory(OutputFolder);
            Directory.CreateDirectory(DownloadFolder);

            if (Directory.Exists(Path.Combine(OutputFolder, "mods")) && WarnOnOverwrite)
            {
                if ((await Utils.Log(new ConfirmUpdateOfExistingInstall { ModListName = ModList.Name, OutputFolder = OutputFolder }).Task) == ConfirmUpdateOfExistingInstall.Choice.Abort)
                {
                    Utils.Log("Existing installation at the request of the user, existing mods folder found.");
                    return false;
                }
            }

            if (cancel.IsCancellationRequested) return false;
            UpdateTracker.NextStep("Optimizing Modlist");
            await OptimizeModlist();

            if (cancel.IsCancellationRequested) return false;
            UpdateTracker.NextStep("Hashing Archives");
            await HashArchives();

            if (cancel.IsCancellationRequested) return false;
            UpdateTracker.NextStep("Downloading Missing Archives");
            await DownloadArchives();

            if (cancel.IsCancellationRequested) return false;
            UpdateTracker.NextStep("Hashing Remaining Archives");
            await HashArchives();

            var missing = ModList.Archives.Where(a => !HashedArchives.ContainsKey(a.Hash)).ToList();
            if (missing.Count > 0)
            {
                foreach (var a in missing)
                    Info($"Unable to download {a.Name}");
                if (IgnoreMissingFiles)
                    Info("Missing some archives, but continuing anyways at the request of the user");
                else
                    Error("Cannot continue, was unable to download one or more archives");
            }

            if (cancel.IsCancellationRequested) return false;
            UpdateTracker.NextStep("Priming VFS");
            await PrimeVFS();

            if (cancel.IsCancellationRequested) return false;
            UpdateTracker.NextStep("Building Folder Structure");
            BuildFolderStructure();

            if (cancel.IsCancellationRequested) return false;
            UpdateTracker.NextStep("Installing Archives");
            await InstallArchives();

            if (cancel.IsCancellationRequested) return false;
            UpdateTracker.NextStep("Installing Included files");
            await InstallIncludedFiles();

            if (cancel.IsCancellationRequested) return false;
            UpdateTracker.NextStep("Installing Archive Metas");
            await InstallIncludedDownloadMetas();

            if (cancel.IsCancellationRequested) return false;
            UpdateTracker.NextStep("Building BSAs");
            await BuildBSAs();

            if (cancel.IsCancellationRequested) return false;
            UpdateTracker.NextStep("Generating Merges");
            await zEditIntegration.GenerateMerges(this);

            UpdateTracker.NextStep("Updating System-specific ini settings");
            SetScreenSizeInPrefs();

            UpdateTracker.NextStep("Installation complete! You may exit the program.");
            var metric2 = Metrics.Send("finish_install", ModList.Name);

            return true;
        }


        private async Task InstallIncludedDownloadMetas()
        {
            await ModList.Directives
                   .OfType<ArchiveMeta>()
                   .PMap(Queue, directive =>
                   {
                       Status($"Writing included .meta file {directive.To}");
                       var outPath = Path.Combine(DownloadFolder, directive.To);
                       if (File.Exists(outPath)) File.Delete(outPath);
                       File.WriteAllBytes(outPath, LoadBytesFromPath(directive.SourceDataID));
                   });
        }

        private void ValidateGameESMs()
        {
            foreach (var esm in ModList.Directives.OfType<CleanedESM>().ToList())
            {
                var filename = Path.GetFileName(esm.To);
                var gameFile = Path.Combine(GameFolder, "Data", filename);
                Utils.Log($"Validating {filename}");
                var hash = gameFile.FileHash();
                if (hash != esm.SourceESMHash)
                {
                    Utils.ErrorThrow(new InvalidGameESMError(esm, hash, gameFile));
                }
            }
        }

        private async Task AskToEndorse()
        {
            var mods = ModList.Archives
                .Select(m => m.State)
                .OfType<NexusDownloader.State>()
                .GroupBy(f => (f.GameName, f.ModID))
                .Select(mod => mod.First())
                .ToArray();

            var result = MessageBox.Show(
                $"Installation has completed, but you have installed {mods.Length} from the Nexus, would you like to" +
                " endorse these mods to show support to the authors? It will only take a few moments.", "Endorse Mods?",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            // Shuffle mods so that if we hit a API limit we don't always miss the same mods
            var r = new Random();
            for (var i = 0; i < mods.Length; i++)
            {
                var a = r.Next(mods.Length);
                var b = r.Next(mods.Length);
                var tmp = mods[a];
                mods[a] = mods[b];
                mods[b] = tmp;
            }

            await mods.PMap(Queue, async mod =>
            {
                var client = await NexusApiClient.Get();
                var er = await client.EndorseMod(mod);
                Utils.Log($"Endorsed {mod.GameName} - {mod.ModID} - Result: {er.message}");
            });
            Info("Done! You may now exit the application!");
        }

        private async Task BuildBSAs()
        {
            var bsas = ModList.Directives.OfType<CreateBSA>().ToList();
            Info($"Building {bsas.Count} bsa files");

            foreach (var bsa in bsas)
            {
                Status($"Building {bsa.To}");
                var sourceDir = Path.Combine(OutputFolder, Consts.BSACreationDir, bsa.TempID);

                using (var a = bsa.State.MakeBuilder())
                {
                    await bsa.FileStates.PMap(Queue, state =>
                    {
                        Status($"Adding {state.Path} to BSA");
                        using (var fs = File.OpenRead(Path.Combine(sourceDir, state.Path)))
                        {
                            a.AddFile(state, fs);
                        }
                    });

                    Info($"Writing {bsa.To}");
                    a.Build(Path.Combine(OutputFolder, bsa.To));
                }
            }

            var bsaDir = Path.Combine(OutputFolder, Consts.BSACreationDir);
            if (Directory.Exists(bsaDir))
            {
                Info($"Removing temp folder {Consts.BSACreationDir}");
                Utils.DeleteDirectory(bsaDir);
            }
        }

        private async Task InstallIncludedFiles()
        {
            Info("Writing inline files");
            await ModList.Directives
                .OfType<InlineFile>()
                .PMap(Queue, directive =>
                {
                    Status($"Writing included file {directive.To}");
                    var outPath = Path.Combine(OutputFolder, directive.To);
                    if (File.Exists(outPath)) File.Delete(outPath);
                    if (directive is RemappedInlineFile)
                        WriteRemappedFile((RemappedInlineFile)directive);
                    else if (directive is CleanedESM)
                        GenerateCleanedESM((CleanedESM)directive);
                    else
                        File.WriteAllBytes(outPath, LoadBytesFromPath(directive.SourceDataID));
                });
        }

        private void GenerateCleanedESM(CleanedESM directive)
        {
            var filename = Path.GetFileName(directive.To);
            var gameFile = Path.Combine(GameFolder, "Data", filename);
            Info($"Generating cleaned ESM for {filename}");
            if (!File.Exists(gameFile)) throw new InvalidDataException($"Missing {filename} at {gameFile}");
            Status($"Hashing game version of {filename}");
            var sha = gameFile.FileHash();
            if (sha != directive.SourceESMHash)
                throw new InvalidDataException(
                    $"Cannot patch {filename} from the game folder hashes don't match have you already cleaned the file?");

            var patchData = LoadBytesFromPath(directive.SourceDataID);
            var toFile = Path.Combine(OutputFolder, directive.To);
            Status($"Patching {filename}");
            using (var output = File.OpenWrite(toFile))
            using (var input = File.OpenRead(gameFile))
            {
                BSDiff.Apply(input, () => new MemoryStream(patchData), output);
            }
        }

        private void SetScreenSizeInPrefs()
        {
            var config = new IniParserConfiguration {AllowDuplicateKeys = true, AllowDuplicateSections = true};
            foreach (var file in Directory.EnumerateFiles(Path.Combine(OutputFolder, "profiles"), "*refs.ini",
                DirectoryEnumerationOptions.Recursive))
            {
                var parser = new FileIniDataParser(new IniDataParser(config));
                var data = parser.ReadFile(file);
                if (data.Sections["Display"]["iSize W"] != null && data.Sections["Display"]["iSize H"] != null)
                {
                    data.Sections["Display"]["iSize W"] = SystemParameters.PrimaryScreenWidth.ToString();
                    data.Sections["Display"]["iSize H"] = SystemParameters.PrimaryScreenHeight.ToString();
                }

                parser.WriteFile(file, data);
            }
        }

        private void WriteRemappedFile(RemappedInlineFile directive)
        {
            var data = Encoding.UTF8.GetString(LoadBytesFromPath(directive.SourceDataID));

            data = data.Replace(Consts.GAME_PATH_MAGIC_BACK, GameFolder);
            data = data.Replace(Consts.GAME_PATH_MAGIC_DOUBLE_BACK, GameFolder.Replace("\\", "\\\\"));
            data = data.Replace(Consts.GAME_PATH_MAGIC_FORWARD, GameFolder.Replace("\\", "/"));

            data = data.Replace(Consts.MO2_PATH_MAGIC_BACK, OutputFolder);
            data = data.Replace(Consts.MO2_PATH_MAGIC_DOUBLE_BACK, OutputFolder.Replace("\\", "\\\\"));
            data = data.Replace(Consts.MO2_PATH_MAGIC_FORWARD, OutputFolder.Replace("\\", "/"));

            data = data.Replace(Consts.DOWNLOAD_PATH_MAGIC_BACK, DownloadFolder);
            data = data.Replace(Consts.DOWNLOAD_PATH_MAGIC_DOUBLE_BACK, DownloadFolder.Replace("\\", "\\\\"));
            data = data.Replace(Consts.DOWNLOAD_PATH_MAGIC_FORWARD, DownloadFolder.Replace("\\", "/"));

            File.WriteAllText(Path.Combine(OutputFolder, directive.To), data);
        }

        public static IErrorResponse CheckValidInstallPath(string path)
        {
            var ret = Utils.IsDirectoryPathValid(path);
            if (!ret.Succeeded) return ret;

            if (!Directory.Exists(path)) return ErrorResponse.Success;

            // Check folder does not have a wabbajack modlist
            foreach (var file in Directory.EnumerateFiles(path, DirectoryEnumerationOptions.Recursive))
            {
                if (!File.Exists(file)) continue;
                if (System.IO.Path.GetExtension(file).Equals(ExtensionManager.Extension))
                {
                    return ErrorResponse.Fail($"Cannot install into a folder with a wabbajack modlist inside of it.");
                }
            }

            // Check folder is either empty, or a likely valid previous install
            if (!Directory.IsEmpty(path))
            {
                // Some probably naive check, but should be a good starting point to improve later
                if (!Directory.EnumerateFiles(path).Any(file =>
                {
                    var fileName = Path.GetFileName(file);
                    if (fileName.Equals("ModOrganizer.exe", StringComparison.OrdinalIgnoreCase)) return true;
                    if (fileName.Equals("ModOrganizer.ini", StringComparison.OrdinalIgnoreCase)) return true;
                    return false;
                }))
                {
                    return ErrorResponse.Fail($"Cannot install into a non-empty folder that does not look like a previous WJ installation.");
                }
            }

            return ErrorResponse.Success;
        }
    }
}
