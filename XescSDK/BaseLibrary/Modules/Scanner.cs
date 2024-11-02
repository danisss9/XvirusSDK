﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using XescSDK.Model;

namespace XescSDK
{
    public class Scanner
    {
        public readonly SettingsDTO settings;
        private readonly DB database;
        private readonly AI ai;

        public Scanner(SettingsDTO settings, DB database, AI ai)
        {
            this.settings = settings;
            this.database = database;
            this.ai = ai;
        }

        public ScanResult ScanFile(string filePath)
        {
            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists)
                return new ScanResult(-1, "File not found!", filePath);

            if (settings.MaxScanLength != null && fileInfo.Length > settings.MaxScanLength)
                return new ScanResult(-1, "File too big!", filePath);

            string? hash = null;
            using (var md5 = MD5.Create())
            {
                using var stream = Utils.ReadFile(filePath);
                var checksum = md5.ComputeHash(stream);
                hash = BitConverter.ToString(checksum).Replace("-", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
            }

            if (hash == null)
                return new ScanResult(-1, "Could not get file hash!", filePath);

            if (database.safeHashList.Contains(hash))
                return new ScanResult(0, "Safe", filePath);

            if (database.malHashList.Contains(hash))
                return new ScanResult(1, "Malware", filePath);

            var certName = Utils.GetCertificateSubjectName(filePath);
            if (certName != null)
            {
                if (database.malVendorList.TryGetValue(certName, out string? value))
                {
                    return new ScanResult(1, value, filePath);
                }
                else
                {
                    return new ScanResult(0, "Safe", filePath);
                }
            }

            if (settings.EnableHeuristics || settings.EnableAIScan)
            {
                bool isExecutable = false;
                using (var stream = File.OpenRead(filePath))
                {
                    using var reader = new BinaryReader(stream);
                    try
                    {
                        var bytes = reader.ReadChars(2);
                        isExecutable = bytes[0] == 'M' && bytes[1] == 'Z';
                    }
                    catch (ArgumentException) { }
                }

                if (settings.EnableHeuristics)
                {
                    if (isExecutable)
                    {
                        using var stream = Utils.ReadFile(filePath);
                        var match = database.heurListPatterns.Search(stream).FirstOrDefault();
                        if (match.Key != null)
                        {
                            var name = database.heurList[match.Key];
                            return new ScanResult(1, name, filePath);
                        }
                    }
                    else if(fileInfo.Length <= 10485760) // 10MBs
                    {
                        using var stream = Utils.ReadFile(filePath);
                        var match = database.heurScriptListPatterns.Search(stream).FirstOrDefault();
                        if (match.Key != null)
                        {
                            var name = database.heurScriptList[match.Key];
                            return new ScanResult(1, name, filePath);
                        }
                    }
                }

                if (isExecutable && settings.EnableAIScan)
                {
                    var aiScore = ai.ScanFile(filePath);
                    return new ScanResult(aiScore, $"AI.{aiScore * 100:F}", filePath);
                }
            }
            return new ScanResult(0, "Safe", filePath);
        }

        public IEnumerable<ScanResult> ScanFolder(string folderPath)
        {
            if (!Directory.Exists(folderPath))
                yield break;

            var filePaths = Directory.GetFiles(folderPath, "*", new EnumerationOptions() { RecurseSubdirectories = true, AttributesToSkip = 0 });

            foreach (var filePath in filePaths)
            {
                yield return ScanFile(filePath);
            }
        }
    }
}
