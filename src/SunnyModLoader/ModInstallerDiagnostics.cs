using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using BepInEx.Logging;

namespace SunnyModLoader;

internal sealed class ModInstallerDiagnosticReport
{
    internal int Passed;
    internal int Failed;
    internal readonly List<string> Failures = new List<string>();
    internal bool Success => Failed == 0;
}

internal static class ModInstallerDiagnostics
{
    internal static ModInstallerDiagnosticReport Run(ManualLogSource log)
    {
        if (log == null)
        {
            throw new ArgumentNullException(nameof(log));
        }

        ModInstallerDiagnosticReport report = new ModInstallerDiagnosticReport();
        string root = Path.Combine(Path.GetTempPath(), "SunnyModInstallerDiagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            RunCase(report, "install/update/rollback/uninstall/restore", () => RunLifecycle(root, log));
            RunCase(report, "malicious archive rejection", () => RunRejectionMatrix(root, log));
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
            catch (Exception ex)
            {
                report.Failed++;
                report.Failures.Add("temporary diagnostic cleanup: " + ex.Message);
            }
        }

        if (report.Success)
        {
            log.LogInfo("Mod installer diagnostics passed " + report.Passed + " case(s).");
        }
        else
        {
            log.LogError(
                "Mod installer diagnostics failed " + report.Failed + " case(s); passed " + report.Passed + ".");
            foreach (string failure in report.Failures)
            {
                log.LogError("  " + failure);
            }
        }

        return report;
    }

    private static void RunLifecycle(string root, ManualLogSource log)
    {
        string modsRoot = Path.Combine(root, "lifecycle", "Mods");
        string inbox = Path.Combine(modsRoot, "Inbox");
        Directory.CreateDirectory(inbox);
        ModInstallerService service = new ModInstallerService(
            modsRoot, log, string.Empty, ModRegistry.LoaderApiVersion);

        string externalArchive = Path.Combine(root, "external-root.zip");
        CreatePackage(
            externalArchive,
            null,
            "org.test.installer",
            "1.0.0",
            archive => AddText(archive, "old.txt", "old"));
        ModInstallerResult first = service.InstallArchive(externalArchive);
        Require(first.Success && !first.IsUpdate, "root-layout package did not install as a new Mod");
        Require(File.Exists(Path.Combine(first.TargetPath, "old.txt")), "new install omitted its payload");

        CreatePackage(
            Path.Combine(inbox, "wrapper.sunmod"),
            "package",
            "org.test.installer",
            "2.0.0",
            archive => AddText(archive, "package/new.txt", "new"));
        ModInstallerResult update = service.InstallFromInbox("wrapper.sunmod");
        Require(update.Success && update.IsUpdate, "wrapper-layout package did not update the existing Mod");
        Require(File.Exists(Path.Combine(update.TargetPath, "new.txt")), "updated payload is missing");
        Require(!File.Exists(Path.Combine(update.TargetPath, "old.txt")), "update retained a deleted old file");
        Require(update.BackupPath != null && File.Exists(Path.Combine(update.BackupPath, "old.txt")),
            "update did not retain the previous package in backup");

        CreatePackage(
            Path.Combine(inbox, "rollback.zip"),
            null,
            "org.test.installer",
            "3.0.0",
            archive => AddText(archive, "failed.txt", "must not commit"));
        ModInstallerService failingService = new ModInstallerService(
            modsRoot,
            log,
            string.Empty,
            ModRegistry.LoaderApiVersion,
            new ThrowOnceFaultInjector(ModInstallerFaultPoint.AfterExistingMovedToBackup));
        ModInstallerResult failedUpdate = failingService.InstallFromInbox("rollback.zip");
        Require(!failedUpdate.Success, "fault-injected update unexpectedly succeeded");
        Require(File.Exists(Path.Combine(update.TargetPath, "new.txt")), "failed update did not restore the old target");
        Require(!File.Exists(Path.Combine(update.TargetPath, "failed.txt")), "failed update leaked its staged payload");

        ModManagedPackageInfo versionOneBackup = service.EnumerateBackups()
            .FirstOrDefault(item => item.Version == "1.0.0");
        Require(versionOneBackup != null, "version 1 backup was not enumerable");
        ModInstallerResult restoredBackup = service.RestoreBackup(versionOneBackup.EntryName);
        Require(restoredBackup.Success && restoredBackup.IsUpdate, "backup restore failed");
        Require(File.Exists(Path.Combine(restoredBackup.TargetPath, "old.txt")), "backup restore selected the wrong payload");

        ModManagedPackageInfo versionTwoBackup = service.EnumerateBackups()
            .FirstOrDefault(item => item.Version == "2.0.0");
        Require(versionTwoBackup != null, "restoring a backup did not preserve the replaced current version");
        ModInstallerResult restoredCurrent = service.RestoreBackup(versionTwoBackup.EntryName);
        Require(restoredCurrent.Success && File.Exists(Path.Combine(restoredCurrent.TargetPath, "new.txt")),
            "second backup restore did not return to version 2");

        ModManagedPackageInfo cleanupCandidate = service.EnumerateBackups().FirstOrDefault();
        Require(cleanupCandidate != null, "no managed backup was available for cleanup testing");
        Require(service.DeleteBackup(cleanupCandidate.EntryName).Success, "managed backup cleanup failed");

        ModInstallerResult uninstall = service.UninstallManaged("org.test.installer");
        Require(uninstall.Success && !Directory.Exists(update.TargetPath), "managed uninstall did not move the package");
        ModManagedPackageInfo trash = service.EnumerateTrash()
            .FirstOrDefault(item => item.ModId == "org.test.installer");
        Require(trash != null, "uninstalled package was not enumerable in trash");
        ModInstallerResult restoreTrash = service.RestoreTrash(trash.EntryName);
        Require(restoreTrash.Success && Directory.Exists(restoreTrash.TargetPath), "trash restore failed");

        string jpegArchive = Path.Combine(root, "jpeg.zip");
        CreateArchive(jpegArchive, archive =>
        {
            AddText(
                archive,
                "manifest.json",
                "{\"schemaVersion\":2,\"id\":\"org.test.jpeg\",\"name\":\"JPEG Diagnostic\"," +
                "\"version\":\"1.0.0\",\"compatibility\":{\"loaderApi\":2}}");
            AddText(
                archive,
                "story/gallery.sunny",
                "@branch {\n" +
                "  id=\"block-parser\", scene=\"Script/vol1\" label=\"1-1\"\n" +
                "  option { id=\"story\", text=\"Enter\" enter=\"start\", repeat=true }\n" +
                "  option { id=\"continue\" text=\"Continue\", continue=true repeat=true }\n" +
                "}\n" +
                "@gallery { id=\"jpeg\", image=\"@/pixel.jpg\" unlockedByDefault=true }\n" +
                "@text { id=\"text-sugar\" scene=\"Script/vol1\", label=\"1-1\" line=0 value=\"flow validation\" }\n" +
                "@voice id=\"voice-sugar\" scene=\"Script/vol1\" label=\"1-1\" speaker=\"旁白\" " +
                "text=\"flow validation\" source=\"@/tone.ogg\" volume=0.5\n" +
                "@voices { id=\"voice-batch\" scene=\"Script/vol1\" label=\"1-1\" directory=\"@/\" " +
                "line { id=\"exact\" speaker=\"旁白\" text=\"flow validation\" file=\"tone.ogg\" } }\n" +
                "@replace { id=\"bgm-replace\" kind=\"Audio\" target=\"Music/TestMusic1.mp3\" source=\"@/tone.ogg\" }\n" +
                "@sprite { id=\"guest\" name=\"Guest\" source=\"@/pixel.jpg\" width=100 height=100 }\n" +
                "bgm \"@/tone.ogg\" [volume=\"0.5\"]\nstopbgm\n" +
                "effect flash [color=\"#FFFFFF\"] [alpha=0.9] [in=0.02] [hold=0.01] [out=0.08] [wait=false]\n" +
                "label start:\nshow $guest\n$guest \"external sprite\"\n" +
                "voice \"@/tone.ogg\" [volume=\"0.5\"]\n" +
                "旁白 \"flow validation\"\ncall \"./called.sunny\" [enter=\"start\"]\nreturn\n");
            AddText(archive, "story/called.sunny", "label start:\n旁白 \"called flow\"\nreturn\n");
            AddBytes(archive, "tone.ogg", new byte[] { 0 });
            AddBytes(
                archive,
                "pixel.jpg",
                Convert.FromBase64String(
                    "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wAARCAABAAEDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD50ooor8MP9Uz/2Q=="));
        });
        ModInstallerResult jpegInstall = service.InstallArchive(jpegArchive);
        Require(jpegInstall.Success, "valid JPEG package was rejected: " + jpegInstall.Error);
        Require(service.UninstallManaged("org.test.jpeg").Success, "valid JPEG diagnostic cleanup failed");

        string unmanaged = Path.Combine(modsRoot, "org.test.unmanaged");
        Directory.CreateDirectory(unmanaged);
        File.WriteAllText(Path.Combine(unmanaged, "manifest.json"), Manifest("org.test.unmanaged", "1.0.0"));
        Require(!service.UninstallManaged("org.test.unmanaged").Success,
            "installer was willing to uninstall a directory without managed metadata");
    }

    private static void RunRejectionMatrix(string root, ManualLogSource log)
    {
        string matrixRoot = Path.Combine(root, "rejections");
        Directory.CreateDirectory(matrixRoot);
        int sequence = 0;

        Reject("zip-slip-parent", archive =>
        {
            AddText(archive, "manifest.json", Manifest("org.test.parent", "1.0.0"));
            AddText(archive, "../escaped.txt", "escape");
        });
        Reject("zip-slip-backslash", archive =>
        {
            AddText(archive, "manifest.json", Manifest("org.test.backslash", "1.0.0"));
            AddText(archive, "..\\escaped.txt", "escape");
        });
        Reject("absolute", archive =>
        {
            AddText(archive, "manifest.json", Manifest("org.test.absolute", "1.0.0"));
            AddText(archive, "/absolute.txt", "escape");
        });
        Reject("drive-ads", archive =>
        {
            AddText(archive, "manifest.json", Manifest("org.test.ads", "1.0.0"));
            AddText(archive, "C:/target.txt", "escape");
        });
        Reject("empty-component", archive =>
        {
            AddText(archive, "manifest.json", Manifest("org.test.empty", "1.0.0"));
            AddText(archive, "assets//file.txt", "bad");
        });
        Reject("trailing-dot", archive =>
        {
            AddText(archive, "manifest.json", Manifest("org.test.trailing", "1.0.0"));
            AddText(archive, "assets/name./file.txt", "bad");
        });
        Reject("case-collision", archive =>
        {
            AddText(archive, "manifest.json", Manifest("org.test.case", "1.0.0"));
            AddText(archive, "Assets/File.txt", "one");
            AddText(archive, "assets/file.TXT", "two");
        });
        Reject("nfc-collision", archive =>
        {
            AddText(archive, "manifest.json", Manifest("org.test.nfc", "1.0.0"));
            AddText(archive, "assets/caf\u00e9.txt", "one");
            AddText(archive, "assets/cafe\u0301.txt", "two");
        });
        Reject("file-directory-prefix", archive =>
        {
            AddText(archive, "manifest.json", Manifest("org.test.prefix", "1.0.0"));
            AddText(archive, "node", "file");
            AddText(archive, "node/child.txt", "child");
        });
        Reject("unix-symlink", archive =>
        {
            AddText(archive, "manifest.json", Manifest("org.test.link", "1.0.0"));
            ZipArchiveEntry link = archive.CreateEntry("link");
            link.ExternalAttributes = unchecked((int)(0xA1FFu << 16));
            using StreamWriter writer = new StreamWriter(link.Open(), new UTF8Encoding(false));
            writer.Write("target");
        });
        Reject("dos-reparse", archive =>
        {
            AddText(archive, "manifest.json", Manifest("org.test.reparse", "1.0.0"));
            ZipArchiveEntry link = archive.CreateEntry("link");
            link.ExternalAttributes = 0x400;
            using StreamWriter writer = new StreamWriter(link.Open(), new UTF8Encoding(false));
            writer.Write("target");
        });
        Reject("ratio-bomb", archive =>
        {
            AddText(archive, "manifest.json", Manifest("org.test.ratio", "1.0.0"));
            ZipArchiveEntry bomb = archive.CreateEntry("zeros.bin", CompressionLevel.Optimal);
            byte[] zeros = new byte[2 * 1024 * 1024];
            using Stream stream = bomb.Open();
            stream.Write(zeros, 0, zeros.Length);
        });
        Reject("oversized-manifest", archive =>
        {
            AddText(archive, "manifest.json", new string(' ', (int)ModInstallerService.MaxManifestBytes + 1));
        });
        Reject("oversized-texture-dimensions", archive =>
        {
            AddText(
                archive,
                "manifest.json",
                "{\"schemaVersion\":2,\"id\":\"org.test.huge-texture\",\"name\":\"Diagnostic Mod\"," +
                "\"version\":\"1.0.0\",\"compatibility\":{\"loaderApi\":2}}");
            AddText(archive, "story/gallery.txt", "@gallery id=\"huge\" image=\"@/huge.png\"\nreturn\n");
            AddBytes(
                archive,
                "huge.png",
                new byte[]
                {
                    137, 80, 78, 71, 13, 10, 26, 10,
                    0, 0, 0, 13, 73, 72, 68, 82,
                    0, 0, 78, 32, 0, 0, 78, 32
                });
        });
        Reject("flow-path-escape", archive =>
        {
            AddText(archive, "manifest.json", Manifest("org.test.flow-escape", "1.0.0"));
            AddText(archive, "story/bad.txt", "background \"@/../outside.png\"\nreturn\n");
        });
        Reject("flow-semantic-comment", archive =>
        {
            AddText(archive, "manifest.json", Manifest("org.test.flow-comment", "1.0.0"));
            AddText(archive, "story/bad.txt", "// @sunny gallery id=\"cg\" image=\"@/pic.png\"\nreturn\n");
        });
        Reject("manifest-v1", archive =>
        {
            AddText(
                archive,
                "manifest.json",
                "{\"schemaVersion\":1,\"id\":\"org.test.v1\",\"name\":\"Old Mod\"," +
                "\"version\":\"1.0.0\",\"compatibility\":{\"loaderApi\":1}}");
        });
        Reject("manifest-missing-schema-version", archive =>
        {
            AddText(
                archive,
                "manifest.json",
                "{\"id\":\"org.test.missing-schema\",\"name\":\"Missing Schema\"," +
                "\"version\":\"1.0.0\",\"compatibility\":{\"loaderApi\":2}}");
        });
        Reject("manifest-wrong-loader-api", archive =>
        {
            AddText(
                archive,
                "manifest.json",
                "{\"schemaVersion\":2,\"id\":\"org.test.loader-api\",\"name\":\"Wrong Loader API\"," +
                "\"version\":\"1.0.0\",\"compatibility\":{\"loaderApi\":3}}");
        });
        Reject("manifest-content-field", archive =>
        {
            AddText(
                archive,
                "manifest.json",
                "{\"schemaVersion\":2,\"id\":\"org.test.content-field\",\"name\":\"Bad Metadata\"," +
                "\"version\":\"1.0.0\",\"compatibility\":{\"loaderApi\":2},\"branches\":[]}");
        });
        Reject("manifest-nested-field", archive =>
        {
            AddText(
                archive,
                "manifest.json",
                "{\"schemaVersion\":2,\"id\":\"org.test.nested-field\",\"name\":\"Bad Metadata\"," +
                "\"version\":\"1.0.0\",\"compatibility\":{\"loaderApi\":2,\"branch\":true}}");
        });
        Reject("flow-unknown-field", archive =>
        {
            AddText(archive, "manifest.json", Manifest("org.test.flow-unknown", "1.0.0"));
            AddText(archive, "story/bad.txt", "@gallery id=\"cg\" imag=\"@/pic.png\"\nreturn\n");
        });
        Reject("flow-external-audio-control", archive =>
        {
            AddText(archive, "manifest.json", Manifest("org.test.external-audio-control", "1.0.0"));
            AddText(
                archive,
                "story/bad.txt",
                "@audioControl { id=\"external-control\" }\nreturn\n");
        });
        Reject("flow-voice-without-locator", archive =>
        {
            AddText(archive, "manifest.json", Manifest("org.test.flow-voice-locator", "1.0.0"));
            AddText(
                archive,
                "story/bad.txt",
                "@voice id=\"unsafe\" scene=\"Script/vol1\" label=\"1-1\" source=\"@/missing.ogg\"\n" +
                "label start:\nreturn\n");
        });
        Reject("flow-legacy-branch", archive =>
        {
            AddText(archive, "manifest.json", Manifest("org.test.flow-legacy-branch", "1.0.0"));
            AddText(
                archive,
                "story/bad.txt",
                "@branch id=\"legacy\" scene=\"Script/vol1\" label=\"1-1\" option=\"Old syntax\"\n" +
                "label start:\nreturn\n");
        });
        Reject("flow-late-declaration", archive =>
        {
            AddText(archive, "manifest.json", Manifest("org.test.flow-late", "1.0.0"));
            AddText(
                archive,
                "story/bad.txt",
                "label start:\n@branch { id=\"late\" scene=\"Script/vol1\" label=\"1-1\" " +
                "option { id=\"continue\" text=\"Continue\" continue=true } }\nreturn\n");
        });
        Reject("flow-duplicate-label", archive =>
        {
            AddText(archive, "manifest.json", Manifest("org.test.flow-label", "1.0.0"));
            AddText(archive, "story/bad.txt", "label start:\n旁白 \"one\"\nlabel start:\nreturn\n");
        });
        Reject("flow-missing-entry-label", archive =>
        {
            AddText(archive, "manifest.json", Manifest("org.test.flow-entry", "1.0.0"));
            AddText(
                archive,
                "story/bad.txt",
                "@branch { id=\"entry\" scene=\"Script/vol1\" label=\"1-1\" " +
                "option { id=\"missing\" text=\"Missing\" enter=\"missing\" } }\n" +
                "label start:\nreturn\n");
        });
        Reject("flow-effect-invalid-color", archive =>
        {
            AddText(archive, "manifest.json", Manifest("org.test.effect-color", "1.0.0"));
            AddText(archive, "story/bad.txt", "effect flash [color=\"white\"]\nreturn\n");
        });
        Reject("flow-effect-unknown-attribute", archive =>
        {
            AddText(archive, "manifest.json", Manifest("org.test.effect-attribute", "1.0.0"));
            AddText(archive, "story/bad.txt", "effect flash [speed=1]\nreturn\n");
        });
        Reject("flow-effect-duration-limit", archive =>
        {
            AddText(archive, "manifest.json", Manifest("org.test.effect-duration", "1.0.0"));
            AddText(archive, "story/bad.txt", "effect flash [in=10] [hold=10] [out=10] [count=2]\nreturn\n");
        });
        Reject("reserved-builtin-id", archive =>
        {
            AddText(
                archive,
                "manifest.json",
                Manifest(ModRegistry.BuiltInVoiceControlId, "0.1.0"));
        });
        Reject("too-deep", archive =>
        {
            AddText(archive, "manifest.json", Manifest("org.test.deep", "1.0.0"));
            AddText(archive, string.Join("/", Enumerable.Repeat("d", ModInstallerService.MaxDepth + 1)) + "/file.txt", "bad");
        });
        Reject("entry-count", archive =>
        {
            AddText(archive, "manifest.json", Manifest("org.test.count", "1.0.0"));
            for (int index = 0; index < ModInstallerService.MaxEntries; index++)
            {
                archive.CreateEntry("empty/" + index.ToString("D5"));
            }
        });

        void Reject(string name, Action<ZipArchive> populate)
        {
            string modsRoot = Path.Combine(matrixRoot, (++sequence).ToString("D2") + "-" + name, "Mods");
            string inbox = Path.Combine(modsRoot, "Inbox");
            Directory.CreateDirectory(inbox);
            string fileName = name + ".zip";
            CreateArchive(Path.Combine(inbox, fileName), populate);
            ModInstallerService service = new ModInstallerService(
                modsRoot, log, string.Empty, ModRegistry.LoaderApiVersion);
            ModInstallerResult result = service.InstallFromInbox(fileName);
            Require(!result.Success, "malicious archive was accepted: " + name);
            Require(!File.Exists(Path.Combine(Path.GetDirectoryName(modsRoot), "escaped.txt")),
                "malicious archive wrote outside Mods: " + name);
        }
    }

    private static void CreatePackage(
        string path,
        string wrapper,
        string id,
        string version,
        Action<ZipArchive> addPayload)
    {
        CreateArchive(path, archive =>
        {
            string prefix = string.IsNullOrEmpty(wrapper) ? string.Empty : wrapper + "/";
            AddText(archive, prefix + "manifest.json", Manifest(id, version));
            addPayload?.Invoke(archive);
        });
    }

    private static void CreateArchive(string path, Action<ZipArchive> populate)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        using FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Create, false, new UTF8Encoding(false, true));
        populate(archive);
    }

    private static void AddText(ZipArchive archive, string name, string text)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using StreamWriter writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static void AddBytes(ZipArchive archive, string name, byte[] bytes)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string Manifest(string id, string version)
    {
        return "{\"schemaVersion\":2,\"id\":\"" + id + "\",\"name\":\"Diagnostic Mod\",\"version\":\"" +
               version + "\",\"compatibility\":{\"loaderApi\":2},\"defaults\":{\"enabled\":false}}";
    }

    private static void RunCase(ModInstallerDiagnosticReport report, string name, Action action)
    {
        try
        {
            action();
            report.Passed++;
        }
        catch (Exception ex)
        {
            report.Failed++;
            report.Failures.Add(name + ": " + ex.Message);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class ThrowOnceFaultInjector : IModInstallerFaultInjector
    {
        private readonly ModInstallerFaultPoint _target;
        private bool _thrown;

        internal ThrowOnceFaultInjector(ModInstallerFaultPoint target)
        {
            _target = target;
        }

        public void Hit(ModInstallerFaultPoint point)
        {
            if (!_thrown && point == _target)
            {
                _thrown = true;
                throw new IOException("Injected installer failure at " + point + ".");
            }
        }
    }
}
