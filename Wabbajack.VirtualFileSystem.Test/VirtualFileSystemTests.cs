﻿using System;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Alphaleonis.Win32.Filesystem;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Wabbajack.Common;
using Wabbajack.VirtualFileSystem;

namespace Wabbajack.VirtualFileSystem.Test
{

        [TestClass]
        public class VFSTests
        {
            private static string VFS_TEST_DIR = "vfs_test_dir";
            private static string VFS_TEST_DIR_FULL = Path.Combine(Directory.GetCurrentDirectory(), VFS_TEST_DIR);
            private Context context;

            public TestContext TestContext { get; set; }

            [TestInitialize]
            public void Setup()
            {
                Utils.LogMessages.Subscribe(f => TestContext.WriteLine(f));
                if (Directory.Exists(VFS_TEST_DIR))
                    Directory.Delete(VFS_TEST_DIR, true);
                Directory.CreateDirectory(VFS_TEST_DIR);
                context = new Context();
            }

            [TestMethod]
            public async Task FilesAreIndexed()
            {
                AddFile("test.txt", "This is a test");
                await AddTestRoot();

                var file = context.Index.ByFullPath[Path.Combine(VFS_TEST_DIR_FULL, "test.txt")];
                Assert.IsNotNull(file);

                Assert.AreEqual(file.Size, 14);
                Assert.AreEqual(file.Hash, "qX0GZvIaTKM=");
            }

            private async Task AddTestRoot()
            {
                await context.AddRoot(VFS_TEST_DIR_FULL);
                await context.WriteToFile(Path.Combine(VFS_TEST_DIR_FULL,"vfs_cache.bin"));
                await context.IntegrateFromFile(Path.Combine(VFS_TEST_DIR_FULL, "vfs_cache.bin"));
            }


            [TestMethod]
            public async Task ArchiveContentsAreIndexed()
            {
                AddFile("archive/test.txt", "This is a test");
                ZipUpFolder("archive", "test.zip");
                await AddTestRoot();
            
                var abs_path = Path.Combine(VFS_TEST_DIR_FULL, "test.zip");
                var file = context.Index.ByFullPath[abs_path];
                Assert.IsNotNull(file);

                Assert.AreEqual(128, file.Size);
                Assert.AreEqual(abs_path.FileHash(), file.Hash);

                Assert.IsTrue(file.IsArchive);
                var inner_file = file.Children.First();
                Assert.AreEqual(14, inner_file.Size);
                Assert.AreEqual("qX0GZvIaTKM=", inner_file.Hash);
                Assert.AreSame(file, file.Children.First().Parent);
            }

            [TestMethod]
            public async Task DuplicateFileHashes()
            {
                AddFile("archive/test.txt", "This is a test");
                ZipUpFolder("archive", "test.zip");

                AddFile("test.txt", "This is a test");
                await AddTestRoot();


                var files = context.Index.ByHash["qX0GZvIaTKM="];
                Assert.AreEqual(files.Count(), 2);

            }

        private void AddFile(string filename, string thisIsATest)
            {
                var fullpath = Path.Combine(VFS_TEST_DIR, filename);
                if (!Directory.Exists(Path.GetDirectoryName(fullpath)))
                    Directory.CreateDirectory(Path.GetDirectoryName(fullpath));
                File.WriteAllText(fullpath, thisIsATest);
            }

            private void ZipUpFolder(string folder, string output)
            {
                var path = Path.Combine(VFS_TEST_DIR, folder);
                ZipFile.CreateFromDirectory(path, Path.Combine(VFS_TEST_DIR, output));
                Directory.Delete(path, true);
            }

        }


}
