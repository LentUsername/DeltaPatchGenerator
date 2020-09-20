using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using System.Xml.Serialization;

namespace DeltaPatchGenerator
{
    public class Program
    {
        static object Writer { get; } = new object();
        static bool UseCache;

        public static void DoPatch(Config config)
        {
            Trace.AutoFlush = true;
#if DEBUG
            {
                var traceFile = new FileInfo("trace.log");
                if (traceFile.Exists && traceFile.Length > 5_000_000)
                {
                    string half;
                    using (var sr = traceFile.OpenText())
                    {
                        var full = sr.ReadToEnd();
                        half = full.Substring(full.IndexOf(Environment.NewLine, full.Length / 2)
                            + Environment.NewLine.Length);
                    }
                    using (var sw = traceFile.CreateText())
                        sw.Write(half);
                }
                Trace.Listeners.Add(new TextWriterTraceListener("trace.log"));
            }
#endif
            var outputDirectory = new DirectoryInfo(config.OutputPath);
            if (outputDirectory.Exists && outputDirectory.EnumerateFileSystemInfos().Count() > 0)
            {
                Trace.WriteLine($"Output directory \"{outputDirectory.FullName}\" must be empty or non-existent.");
                return;
            }
            if (!FileFromConfigExists(config.XdeltaPath, out var xdeltaFile)) return;
            if (!FileFromConfigExists(config.XdeltaLicensePath, out var xdeltaLicenseFile)) return;

            var stopwatch = Stopwatch.StartNew();
            DirectoryInfo sourceDirectory, targetDirectory;
            try
            {
                sourceDirectory = GetDirectoryOrExtractZipArchive(config.SourcePath);
                targetDirectory = GetDirectoryOrExtractZipArchive(config.TargetPath);
            }
            catch (Exception e)
            {
                Trace.WriteLine(e.Message);
                return;
            }

            UseCache = config.UseCache;
            Trace.WriteLine($"Getting hashes of source \"{sourceDirectory.FullName}\".");
            var sourceHashes = LoadFromCache(sourceDirectory.Name + "hashes", () => ComputeHashes(sourceDirectory));
            Trace.WriteLine($"Getting hashes of target \"{targetDirectory.FullName}\".");
            var targetHashes = LoadFromCache(targetDirectory.Name + "hashes", () => ComputeHashes(targetDirectory));

            var newlyAddedFiles = new List<string>();
            var changedFiles = new List<string>();
            var deletedFiles = new List<string>(sourceHashes.Keys);
            {
                var regex = new Regex(config.RejectionRegex, RegexOptions.IgnoreCase);
                var regexNotEmpty = !string.IsNullOrEmpty(config.RejectionRegex);
                foreach (var pair in targetHashes.OrderBy(p => p.Key))
                {
                    if (sourceHashes.TryGetValue(pair.Key, out var bytes))
                    {
                        deletedFiles.Remove(pair.Key);
                        if (!bytes.SequenceEqual(pair.Value))
                            if (regexNotEmpty && regex.IsMatch(pair.Key))
                                newlyAddedFiles.Add(pair.Key);
                            else
                                changedFiles.Add(pair.Key);
                    }
                    else
                        newlyAddedFiles.Add(pair.Key);
                }
            }

            if (newlyAddedFiles.Count + changedFiles.Count + deletedFiles.Count == 0)
            {
                Trace.WriteLine("Source and target are identical.");
                return;
            }

            const string ScriptFile = "ApplyPatch.cmd";
            const string AffectedFilesDirectory = "AffectedFiles";
            const string XdeltaDirectory = "xdelta3";
            const string GameDirectory = "..";
            const string VcdiffExtension = ".vcdiff";
            const string PatchedExtension = ".patched";

            var encodingStats = LoadFromCache(outputDirectory.Name + "stats", () =>
            {
                var scriptFile = new FileInfo(Path.Combine(outputDirectory.FullName, ScriptFile));
                var affectedFilesDirectory = new DirectoryInfo(Path.Combine(outputDirectory.FullName, AffectedFilesDirectory));
                affectedFilesDirectory.Create();
                {
                    var xdeltaDir = new DirectoryInfo(Path.Combine(outputDirectory.FullName, XdeltaDirectory));
                    xdeltaDir.Create();
                    CopyFileToDirectory(xdeltaLicenseFile, xdeltaDir);
                    using (var sw = scriptFile.CreateText())
                    {
                        var xdelta = Path.Combine(XdeltaDirectory,
                            CopyFileToDirectory(xdeltaFile, xdeltaDir).Name);
                        foreach (var relative in changedFiles)
                        {
                            var oldFile = Path.Combine(GameDirectory, relative);
                            var newFile = $"{oldFile}{PatchedExtension}";
                            sw.WriteLine(
                                $"\"{xdelta}\" -d -s \"{oldFile}\" "
                                + $"\"{Path.Combine(AffectedFilesDirectory, relative)}{VcdiffExtension}\" "
                                + $"\"{newFile}\"");
                            sw.WriteLine($"MOVE /Y \"{newFile}\" \"{oldFile}\"");
                        }
                        foreach (var relative in newlyAddedFiles)
                        {
                            sw.WriteLine(
                                    "COPY /Y "
                                + $"\"{Path.Combine(AffectedFilesDirectory, relative)}\" "
                                + $"\"{Path.Combine(GameDirectory, relative)}\"");
                            CopyFileToDirectory(new FileInfo(Path.Combine(targetDirectory.FullName, relative)),
                            affectedFilesDirectory.CreateSubdirectory(Path.GetDirectoryName(relative)));
                        }

                        foreach (var relative in deletedFiles)
                            sw.WriteLine($"DEL \"{Path.Combine(GameDirectory, relative)}\"");
                    }
                }
                {
                    var ps = new Process[Math.Min(changedFiles.Count, Environment.ProcessorCount)];
                    if (ps.Length > 0)
                    {
                        Trace.WriteLine($"Encoding difference.");
                    }
                    var _encodingStats = new ConcurrentBag<object[]>();
                    using (var bagEmpty = new ManualResetEventSlim(changedFiles.Count == 0))
                    {
                        var total = changedFiles.Count;
                        var width = total.ToString().Length;
                        var current = 0;
                        var bag = new ConcurrentBag<string>(changedFiles);
                        for (int i = 0; i < ps.Length; i++)
                        {
                            {
                                bag.TryTake(out var relative);
                                ps[i] = new Process
                                {
                                    StartInfo = new ProcessStartInfo(
                                    config.XdeltaPath,
                                    PrepareXdeltaArguments(sourceDirectory, targetDirectory, VcdiffExtension,
                                        affectedFilesDirectory, relative))
                                    { UseShellExecute = false, CreateNoWindow = true },
                                    EnableRaisingEvents = true
                                };
                            }
                            ps[i].Exited += (sender, e) =>
                            {
                                var p = (Process)sender;
                                var vs = p.StartInfo.Arguments.Split('"');
                                var _relative = vs[3].Substring(targetDirectory.FullName.Length + 1);
                                var oldLength = new FileInfo(vs[3]).Length;
                                var newLength = new FileInfo(vs[5]).Length;
                                _encodingStats.Add(new object[] {
                                _relative,
                                oldLength - newLength,
                                $"{(double)(oldLength - newLength) / oldLength:P5}",
                                (long)((oldLength - newLength) / p.TotalProcessorTime.TotalSeconds ) });
                                lock (Writer) Trace.WriteLine($"{(++current).ToString().PadLeft(width)}/{total} encode \"{_relative}\"");
                                if (!bag.TryTake(out var relative))
                                {
                                    bagEmpty.Set();
                                    return;
                                }
                                p.StartInfo.Arguments = PrepareXdeltaArguments(sourceDirectory, targetDirectory, VcdiffExtension,
                                    affectedFilesDirectory, relative);
                                p.Start();
                            };
                            ps[i].Start();
                        }
                        bagEmpty.Wait();
                    }
                    for (int i = 0; i < ps.Length; i++)
                    {
                        ps[i].WaitForExit();
                    }
                    return _encodingStats;
                }
            });

            if (config.VerifyResults)
            {
                Trace.WriteLine($"Verifying patch.");
                var tempDirectory = new DirectoryInfo("Temp");
                if (tempDirectory.Exists)
                    tempDirectory.Delete(true);
                var backupDirectory = CopyDirectory(sourceDirectory,
                    Path.Combine(tempDirectory.FullName, sourceDirectory.Name));
                var backupOutputDirectory = CopyDirectory(outputDirectory,
                    Path.Combine(backupDirectory.FullName, outputDirectory.Name));
                var fileName = Path.Combine(backupOutputDirectory.FullName, ScriptFile);
                Trace.WriteLine($"Running patch script \"{fileName}\".");
                Process.Start(new ProcessStartInfo(fileName)
                { WorkingDirectory = backupOutputDirectory.FullName }).WaitForExit();
                backupOutputDirectory.Delete(true);
                var patchedHashes = ComputeHashes(backupDirectory);
                var missingFiles = new List<string>(targetHashes.Keys);
                var success = true;
                foreach (var patched in patchedHashes)
                    if (targetHashes.TryGetValue(patched.Key, out var bytes))
                    {
                        missingFiles.Remove(patched.Key);
                        if (!bytes.SequenceEqual(patched.Value))
                        {
                            Trace.WriteLine($"Hash mismatch for file \"{patched.Key}\".");
                            success = false;
                        }
                    }
                    else
                    {
                        Trace.WriteLine($"Extra file \"{patched.Key}\" found.");
                        success = false;
                    }
                if (missingFiles.Count > 0)
                {
                    success = false;
                    foreach (var missingFile in missingFiles)
                        Trace.WriteLine($"Missing file \"{missingFile}\".");
                }
                Trace.WriteLine($"Patch result: {(success ? "SUCCESS" : "FAILURE")}.");
            }
            else
                Trace.WriteLine("Skipping patch verification.");

            stopwatch.Stop();
            Trace.WriteLine($"Total execution time: {stopwatch.Elapsed}.");

            var sorted = encodingStats.OrderByDescending(s => s[1])
                .Select(s => new[] { s[0], SizeSuffix((long)s[1]), s[2], s[3] });

            var lines = GetFormattedLines(sorted.Prepend(new object[] { "Name", "Reduction", "Ratio", "Time effeciency" }));
            var statsFile = new FileInfo("stats.txt");
            using (var sw = statsFile.CreateText())
                foreach (var line in lines)
                {
                    sw.WriteLine(line);
                }
            var stats = lines.Skip(1);
            const int Count = 3;
            var top = stats.Take(Count);
            var bottom = stats.Skip(Math.Max(Count, encodingStats.Count - Count));
            Trace.WriteLine($"Extract from \"{statsFile.FullName}\":");
            Trace.WriteLine(lines.First());
            foreach (var item in top)
                Trace.WriteLine(item);
            if (top.Count() + bottom.Count() < encodingStats.Count)
                Trace.WriteLine("...");
            foreach (var item in bottom)
                Trace.WriteLine(item);
        }

        static bool FileFromConfigExists(string path, out FileInfo fi)
        {
            fi = new FileInfo(path);
            if (!fi.Exists)
                Trace.WriteLine($"Configuration points to a non-exitent file \"{fi.FullName}\".");
            return fi.Exists;
        }

        static readonly string[] SizeSuffixes =
                   { "B", "KB", "MB", "GB", "TB", "PB", "EB", "ZB", "YB" };
        static string SizeSuffix(long value)
        {
            if (value < 0) return "-" + SizeSuffix(-value);
            if (value == 0) return "0 B";
            var mag = (int)Math.Log(value, 1024);
            var adjustedSize = (decimal)value / (1L << (mag * 10));
            if (Math.Round(adjustedSize, 1) >= 1000)
            {
                mag += 1;
                adjustedSize /= 1024;
            }
            return $"{adjustedSize:N1} {SizeSuffixes[mag]}";
        }

        const string FileName = "configuration.xml";
        public static Config LoadConfig()
        {
            Config config;
            var configurationFile = new FileInfo(FileName);
            var serializer = new XmlSerializer(typeof(Config));
            if (configurationFile.Exists)
                using (var sr = configurationFile.OpenText())
                using (var xr = XmlReader.Create(sr, new XmlReaderSettings()))
                    try
                    {
                        config = (Config)serializer.Deserialize(xr);
                    }
                    catch (InvalidOperationException e)
                    {
                        Trace.WriteLine($"Configuration XML is invalid. Loading defaults. {e.Message} {e.InnerException?.Message}.");
                        config = new Config();
                    }
            else
            {
                config = new Config();
                SaveConfig(config);
                Trace.WriteLine($"Created default configuration file at \"{configurationFile.FullName}\".");
            }
            return config;
        }

        public static void SaveConfig(Config config)
        {
            var configurationFile = new FileInfo(FileName);
            var serializer = new XmlSerializer(typeof(Config));
            using (var sw = configurationFile.CreateText())
            using (var xw = XmlWriter.Create(sw, new XmlWriterSettings() { Indent = true, OmitXmlDeclaration = true }))
                serializer.Serialize(xw, config);
        }

        static IEnumerable<string> GetFormattedLines(IEnumerable<object[]> objects)
        {
            var sizes = new int[objects.First().Length];
            foreach (var item in objects)
                for (int i = 0; i < sizes.Length; i++)
                    sizes[i] = Math.Max(sizes[i], item[i].ToString().Length);
            return objects.Select(item =>
            {
                var sb = new StringBuilder();
                for (int i = 0; i < sizes.Length; i++)
                    sb.Append(item[i].ToString().PadRight(sizes[i] + 2));
                return sb.ToString();
            });
        }

        static string PrepareXdeltaArguments(DirectoryInfo oldDirectory, DirectoryInfo newDirectory,
                                             string vcdiffExtension, DirectoryInfo affectedFilesDirectory,
                                             string relative)
        {
            affectedFilesDirectory.CreateSubdirectory(Path.GetDirectoryName(relative));
            return $"-A= -9 -s \"{Path.Combine(oldDirectory.FullName, relative)}\" "
                + $"\"{Path.Combine(newDirectory.FullName, relative)}\" "
                + $"\"{$"{Path.Combine(affectedFilesDirectory.FullName, relative)}{vcdiffExtension}"}\"";
        }

        static T LoadFromCache<T>(string path, Func<T> fallback)
        {
            if (!UseCache)
                return fallback();
            const string CacheDirectory = "Cache";
            var fi = new FileInfo(Path.Combine(CacheDirectory, path));
            if (fi.Exists)
            {
                Trace.WriteLine($"Loading \"{path}\" from cache.");
                return (T)new BinaryFormatter().Deserialize(fi.OpenRead());
            }
            var result = fallback();
            fi.Directory.Create();
            new BinaryFormatter().Serialize(fi.Create(), result);
            return result;
        }

        static DirectoryInfo CopyDirectory(DirectoryInfo source, string destination)
        {
            var d = Directory.CreateDirectory(destination);
            var fis = source.EnumerateFiles("*", SearchOption.AllDirectories);
            var total = fis.Count();
            var width = total.ToString().Length;
            var current = 0;
            var startIndex = source.FullName.Length + 1;
            foreach (var fi in fis)
            {
                var relative = fi.FullName.Substring(startIndex);
                var newFile = new FileInfo(Path.Combine(destination, relative));
                newFile.Directory.Create();
                fi.CopyTo(newFile.FullName);
                Trace.WriteLine($"{(++current).ToString().PadLeft(width)}/{total} copy \"{relative}\"");
            }
            return d;
        }

        static IReadOnlyDictionary<string, byte[]> ComputeHashes(DirectoryInfo directory)
        {
            var files = directory.EnumerateFiles("*", SearchOption.AllDirectories);
            var total = files.Count();
            var width = total.ToString().Length;
            var startIndex = directory.FullName.Length + 1;
            var current = 0;
            return files.AsParallel().Select(file =>
            {
                using (var ha = HashAlgorithm.Create())
                using (var fs = file.OpenRead())
                {
                    var relativeName = file.FullName.Substring(startIndex);
                    lock (Writer) Trace.WriteLine($"{(++current).ToString().PadLeft(width)}/{total} hash \"{relativeName}\"");
                    return new KeyValuePair<string, byte[]>(relativeName, ha.ComputeHash(fs));
                }
            }).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        static FileInfo CopyFileToDirectory(FileInfo file, DirectoryInfo directory)
            => file.CopyTo(Path.Combine(directory.FullName, file.Name));

        static DirectoryInfo GetDirectoryOrExtractZipArchive(string path)
        {
            {
                var directory = new DirectoryInfo(path);
                if (directory.Exists)
                    return directory;
            }
            {
                var file = new FileInfo(path);
                if (!file.Exists)
                {
                    throw new FileNotFoundException($"Directory or file \"{file.FullName}\" does not exist.");
                }
                try
                {
                    using (var zip = new ZipArchive(file.OpenRead()))
                    {
                        var destination = new DirectoryInfo(Path.Combine(
                            file.DirectoryName, Path.GetFileNameWithoutExtension(file.Name)));
                        if (destination.Exists)
                        {
                            Trace.WriteLine($"Already extracted \"{file.FullName}\" to \"{destination.FullName}\".");
                            return destination;
                        }
                        Trace.WriteLine($"Extracting \"{file.FullName}\" to \"{destination.FullName}\".");
                        destination.Create();
                        var total = zip.Entries.Count;
                        var width = total.ToString().Length;
                        var current = 0;
                        foreach (var entry in zip.Entries)
                        {
                            if (string.IsNullOrEmpty(entry.Name))
                            {
                                Directory.CreateDirectory(Path.Combine(destination.FullName, entry.FullName));
                                continue;
                            }
                            var fi = new FileInfo(Path.Combine(destination.FullName, entry.FullName));
                            using (var from = entry.Open())
                            using (var to = fi.Create())
                                from.CopyTo(to);
                            Trace.WriteLine($"{(++current).ToString().PadLeft(width)}/{total} extract \"{entry.FullName}\"");
                        }
                        return destination;
                    }
                }
                catch (InvalidDataException e)
                {
                    throw new InvalidDataException($"File \"{file.FullName}\" is not a valid zip archive.", e);
                }
            }
        }
    }
}
/*
    Copyright 2020 LentUsername

    Licensed under the Apache License, Version 2.0 (the "License");
    you may not use this file except in compliance with the License.
    You may obtain a copy of the License at

        https://www.apache.org/licenses/LICENSE-2.0

    Unless required by applicable law or agreed to in writing, software
    distributed under the License is distributed on an "AS IS" BASIS,
    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
    See the License for the specific language governing permissions and
    limitations under the License.
*/