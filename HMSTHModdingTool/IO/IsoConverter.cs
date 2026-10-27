using System;
using System.IO;
using System.Text;
using HMSTHModdingTool.IO;

namespace HMSTHModdingTool
{
    /// <summary>
    /// Converts between BIN/CUE and ISO
    /// formats. Handles 2352-byte raw
    /// sectors to 2048-byte ISO sectors
    /// and vice versa.
    /// </summary>
    public static class IsoConverter
    {
        const int SECTOR_2048 = 2048;
        const int SECTOR_2352 = 2352;
        const int SECTOR_2336 = 2336;
        const int SUB_DATA_OFFSET = 8;

        // ═══════════════════════════════
        // MAIN CONVERT
        // ═══════════════════════════════
        public static void Convert(
            string inputPath,
            string outputPath)
        {
            if (string.IsNullOrWhiteSpace(inputPath))
                throw new ArgumentException("An input path is required.", "inputPath");
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("An output path is required.", "outputPath");

            if (!File.Exists(inputPath))
            {
                string ext = Path.GetExtension(inputPath).ToLowerInvariant();
                if (ext == ".cue")
                {
                    string binPath = FindBinFromCue(inputPath);
                    if (binPath != null && File.Exists(binPath))
                        inputPath = binPath;
                    else
                        throw new FileNotFoundException(
                            "BIN file not found from CUE", inputPath);
                }
                else
                {
                    throw new FileNotFoundException("Input not found", inputPath);
                }
            }

            string fullInputPath = Path.GetFullPath(inputPath);
            string fullOutputPath = Path.GetFullPath(outputPath);
            long fileSize = new FileInfo(fullInputPath).Length;
            string outExt = Path.GetExtension(fullOutputPath).ToLowerInvariant();
            int srcSector = DetectSectorSize(fullInputPath, fileSize);
            int dstSector = outExt == ".bin" ? SECTOR_2352 : SECTOR_2048;

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("Converting: " + srcSector + " → " + dstSector);
            Console.ResetColor();

            if (fileSize == 0 || fileSize % srcSector != 0)
            {
                throw new InvalidDataException(
                    "Input image length is not an exact multiple of its detected sector size.");
            }

            if (string.Equals(
                fullInputPath, fullOutputPath,
                StringComparison.OrdinalIgnoreCase))
            {
                if (srcSector == dstSector)
                {
                    Console.WriteLine("  Source and destination are the same file; no copy is needed.");
                    return;
                }

                throw new IOException("Input and output paths must differ for a sector-format conversion.");
            }

            if (srcSector == dstSector)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("  Same format! Copying through a temporary file...");
                Console.ResetColor();

                string copyTemporaryPath =
                    SafeFileReplacement.CreateTemporarySiblingPath(fullOutputPath);
                try
                {
                    File.Copy(fullInputPath, copyTemporaryPath, false);
                    long copiedLength = new FileInfo(copyTemporaryPath).Length;
                    if (copiedLength != fileSize)
                    {
                        throw new IOException(
                            "Copied file size mismatch. Expected " + fileSize +
                            " bytes but wrote " + copiedLength + " bytes.");
                    }

                    string copyBackupPath = SafeFileReplacement.InstallWithBackup(
                        copyTemporaryPath, fullOutputPath);
                    if (!string.IsNullOrEmpty(copyBackupPath))
                        Console.WriteLine("  Previous output preserved as backup: " + copyBackupPath);
                }
                finally
                {
                    SafeFileReplacement.TryDeleteTemporaryFile(copyTemporaryPath);
                }

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("  Done!");
                Console.ResetColor();
                return;
            }

            long totalSectors = fileSize / srcSector;
            long expectedOutputLength = checked(totalSectors * (long)dstSector);
            string temporaryOutputPath =
                SafeFileReplacement.CreateTemporarySiblingPath(fullOutputPath);
            string cuePath = outExt == ".bin"
                ? Path.ChangeExtension(fullOutputPath, ".cue")
                : null;
            string temporaryCuePath = null;
            string outputBackupPath = null;
            string cueBackupPath = null;
            bool outputCommitted = false;
            bool cueCommitted = false;

            try
            {
                using (var src = new FileStream(
                    fullInputPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    65536,
                    FileOptions.SequentialScan))
                using (var dst = new FileStream(
                    temporaryOutputPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    65536,
                    FileOptions.SequentialScan))
                {
                    byte[] srcBuf = new byte[srcSector];
                    byte[] dstBuf = new byte[dstSector];

                    for (long sector = 0; sector < totalSectors; sector++)
                    {
                        SafeFileReplacement.ReadExactly(src, srcBuf, srcBuf.Length);

                        if (srcSector == SECTOR_2352 && dstSector == SECTOR_2048)
                        {
                            int userDataOffset = GetRawUserDataOffset(srcBuf, sector);
                            Array.Copy(
                                srcBuf, userDataOffset,
                                dstBuf, 0,
                                SECTOR_2048);
                        }
                        else if (srcSector == SECTOR_2336 && dstSector == SECTOR_2048)
                        {
                            int userDataOffset = GetMode2_2336UserDataOffset(srcBuf, sector);
                            Array.Copy(srcBuf, userDataOffset, dstBuf, 0, SECTOR_2048);
                        }
                        else if (srcSector == SECTOR_2048 && dstSector == SECTOR_2352)
                        {
                            BuildRawSector(srcBuf, dstBuf, sector);
                        }
                        else if (srcSector == SECTOR_2336 && dstSector == SECTOR_2352)
                        {
                            // A 2336-byte Mode 2 sector starts at the subheader.
                            // The user payload begins eight bytes into that block.
                            int userDataOffset = GetMode2_2336UserDataOffset(srcBuf, sector);
                            CdRomSectorEncoder.EncodeMode2Form1(
                                dstBuf,
                                srcBuf,
                                userDataOffset,
                                srcBuf,
                                0,
                                sector);
                        }
                        else
                        {
                            Array.Clear(dstBuf, 0, dstBuf.Length);
                            int copyLength = Math.Min(srcSector, dstSector);
                            Array.Copy(srcBuf, 0, dstBuf, 0, copyLength);
                        }

                        dst.Write(dstBuf, 0, dstBuf.Length);

                        if (sector % 10000 == 0)
                        {
                            double percentage = (double)sector / totalSectors * 100.0;
                            Console.Write("\r  {0:F1}%", percentage);
                        }
                    }
                    dst.Flush();
                }

                long outputLength = new FileInfo(temporaryOutputPath).Length;
                if (outputLength != expectedOutputLength)
                {
                    throw new IOException(
                        "Temporary output size mismatch. Expected " + expectedOutputLength +
                        " bytes but wrote " + outputLength + " bytes.");
                }

                if (cuePath != null)
                {
                    string cueContent =
                        "FILE \"" + Path.GetFileName(fullOutputPath) + "\" BINARY\r\n" +
                        "  TRACK 01 MODE2/2352\r\n" +
                        "    INDEX 01 00:00:00\r\n";
                    temporaryCuePath =
                        SafeFileReplacement.CreateTemporarySiblingPath(cuePath);
                    using (var cueStream = new FileStream(
                        temporaryCuePath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None))
                    using (var cueWriter = new StreamWriter(
                        cueStream,
                        new UTF8Encoding(false)))
                    {
                        cueWriter.Write(cueContent);
                        cueWriter.Flush();
                    }
                }

                outputBackupPath = SafeFileReplacement.InstallWithBackup(
                    temporaryOutputPath, fullOutputPath);
                outputCommitted = true;

                if (temporaryCuePath != null)
                {
                    cueBackupPath = SafeFileReplacement.InstallWithBackup(
                        temporaryCuePath, cuePath);
                    cueCommitted = true;
                }
            }
            catch (Exception operationError)
            {
                if (outputCommitted && temporaryCuePath != null && !cueCommitted)
                {
                    try
                    {
                        string directory = Path.GetDirectoryName(fullOutputPath);
                        string unpairedPath = Path.Combine(
                            directory,
                            ".hmsth-unpaired-" + Guid.NewGuid().ToString("N") + ".bin");

                        if (!string.IsNullOrEmpty(outputBackupPath) && File.Exists(outputBackupPath))
                        {
                            // Restore the pre-existing output, retaining the just-created
                            // but unpaired BIN at a separate path for recovery.
                            File.Replace(outputBackupPath, fullOutputPath, unpairedPath);
                        }
                        else if (File.Exists(fullOutputPath))
                        {
                            // No previous output existed; preserve the generated BIN but
                            // restore the pre-operation state (no output at the target path).
                            File.Move(fullOutputPath, unpairedPath);
                        }

                        Console.Error.WriteLine(
                            "CUE installation failed; BIN target was rolled back. " +
                            "The unpaired generated BIN, if present, is preserved at: " + unpairedPath);
                    }
                    catch (Exception rollbackError)
                    {
                        throw new IOException(
                            "CUE installation failed and BIN rollback also failed. " +
                            "Keep the remaining backup/output files for manual recovery. " +
                            "Original error: " + operationError.Message +
                            ". Rollback error: " + rollbackError.Message,
                            new AggregateException(operationError, rollbackError));
                    }
                }
                throw;
            }
            finally
            {
                SafeFileReplacement.TryDeleteTemporaryFile(temporaryOutputPath);
                if (temporaryCuePath != null)
                    SafeFileReplacement.TryDeleteTemporaryFile(temporaryCuePath);
            }

            Console.Write("\r  100.0%   \n");
            if (!string.IsNullOrEmpty(outputBackupPath))
                Console.WriteLine("  Previous output preserved as backup: " + outputBackupPath);
            if (!string.IsNullOrEmpty(cueBackupPath))
                Console.WriteLine("  Previous CUE preserved as backup: " + cueBackupPath);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(
                "  Conversion complete! " +
                new FileInfo(fullOutputPath).Length.ToString("N0") + " bytes");
            Console.ResetColor();
        }

        // ═══════════════════════════════
        // DETECT SECTOR SIZE
        // ═══════════════════════════════
        static int DetectSectorSize(
            string path, long fileSize)
        {
            if (fileSize <= 0)
                return SECTOR_2048;

            if (fileSize % SECTOR_2352 == 0)
            {
                byte[] header = new byte[16];
                using (var source = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read))
                {
                    SafeFileReplacement.ReadExactly(
                        source, header, header.Length);
                }

                bool validSync = header[0] == 0x00 && header[11] == 0x00;
                for (int i = 1; validSync && i < 11; i++)
                    validSync = header[i] == 0xFF;

                if (validSync && (header[15] == 0x01 || header[15] == 0x02))
                    return SECTOR_2352;
            }

            // A normal ISO commonly contains a zero-filled system area, so
            // its first eight bytes can superficially resemble a duplicated
            // 2336-byte XA subheader. If the caller supplied an .iso path and
            // its length is sector-aligned, prefer the conventional 2048-byte
            // ISO interpretation after excluding a valid 2352-byte raw header.
            if (string.Equals(
                    Path.GetExtension(path), ".iso",
                    StringComparison.OrdinalIgnoreCase) &&
                fileSize % SECTOR_2048 == 0)
            {
                return SECTOR_2048;
            }

            // A 2336-byte Mode 2 image can also be divisible by 2048 when
            // its sector count is a multiple of 64. Use the duplicated XA
            // subheader as structural evidence instead of relying on modulo
            // arithmetic alone.
            if (fileSize % SECTOR_2336 == 0)
            {
                byte[] subheader = new byte[8];
                using (var source = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read))
                {
                    SafeFileReplacement.ReadExactly(
                        source, subheader, subheader.Length);
                }

                bool duplicatedSubheader = true;
                for (int i = 0; i < 4; i++)
                {
                    if (subheader[i] != subheader[i + 4])
                    {
                        duplicatedSubheader = false;
                        break;
                    }
                }

                if (duplicatedSubheader)
                    return SECTOR_2336;
            }

            return SECTOR_2048;
        }

        // ═══════════════════════════════
        // BUILD RAW SECTOR (2352)
        // ═══════════════════════════════
        static void BuildRawSector(
            byte[] data,
            byte[] raw,
            long lba)
        {
            CdRomSectorEncoder.EncodeMode2Form1(
                raw,
                data,
                0,
                lba);
        }

        private static int GetRawUserDataOffset(
            byte[] rawSector,
            long sectorIndex)
        {
            if (rawSector == null || rawSector.Length < SECTOR_2352)
            {
                throw new InvalidDataException(
                    "Raw sector " + sectorIndex + " is shorter than 2352 bytes.");
            }

            if (rawSector[0] != 0x00 || rawSector[11] != 0x00)
            {
                throw new InvalidDataException(
                    "Raw sector " + sectorIndex + " has an invalid sync pattern.");
            }
            for (int i = 1; i < 11; i++)
            {
                if (rawSector[i] != 0xFF)
                {
                    throw new InvalidDataException(
                        "Raw sector " + sectorIndex + " has an invalid sync pattern.");
                }
            }

            if (rawSector[15] == 0x01)
                return 16;

            if (rawSector[15] == 0x02)
            {
                for (int i = 0; i < 4; i++)
                {
                    if (rawSector[16 + i] != rawSector[20 + i])
                    {
                        throw new InvalidDataException(
                            "Raw sector " + sectorIndex +
                            " has a mismatched duplicated XA subheader.");
                    }
                }

                // XA Form 2 has 2324 user bytes, not a 2048-byte ISO payload.
                if ((rawSector[18] & 0x20) != 0)
                {
                    throw new InvalidDataException(
                        "Raw sector " + sectorIndex + " is Mode 2 Form 2 and cannot be copied as a 2048-byte ISO sector.");
                }
                return 24;
            }

            throw new InvalidDataException(
                "Raw sector " + sectorIndex + " has unsupported mode byte 0x" +
                rawSector[15].ToString("X2") + ". Expected Mode 1 or Mode 2 Form 1.");
        }

        private static int GetMode2_2336UserDataOffset(
            byte[] sectorData,
            long sectorIndex)
        {
            if (sectorData == null || sectorData.Length < SECTOR_2336)
            {
                throw new InvalidDataException(
                    "Mode 2 sector " + sectorIndex + " is shorter than 2336 bytes.");
            }

            for (int i = 0; i < 4; i++)
            {
                if (sectorData[i] != sectorData[i + 4])
                {
                    throw new InvalidDataException(
                        "Mode 2 sector " + sectorIndex +
                        " has a mismatched duplicated subheader; conversion was cancelled.");
                }
            }

            if ((sectorData[2] & 0x20) != 0)
            {
                throw new InvalidDataException(
                    "Mode 2 sector " + sectorIndex +
                    " is Form 2 and cannot be converted to a 2048-byte ISO sector.");
            }

            return SUB_DATA_OFFSET;
        }

        // ═══════════════════════════════
        // FIND BIN FROM CUE
        // ═══════════════════════════════
        static string FindBinFromCue(
            string cuePath)
        {
            try
            {
                string[] lines =
                    File.ReadAllLines(
                        cuePath);

                foreach (string line
                         in lines)
                {
                    string trim =
                        line.Trim()
                            .ToUpper();

                    if (trim.StartsWith(
                            "FILE"))
                    {
                        int q1 =
                            line.IndexOf(
                                '"');
                        int q2 =
                            line.IndexOf(
                                '"',
                                q1 + 1);

                        if (q1 >= 0 &&
                            q2 > q1)
                        {
                            string binName =
                                line
                                    .Substring(
                                        q1 + 1,
                                        q2 - q1
                                        - 1);

                            string dir =
                                Path
                                    .GetDirectoryName(
                                        cuePath);

                            if (string
                                .IsNullOrEmpty(
                                    dir))
                                return
                                    binName;

                            return
                                Path.Combine(
                                    dir,
                                    binName);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    "[WARN] Could not parse the CUE file for '" + cuePath +
                    "'; using the same-name BIN fallback: " + ex.Message);
            }

            // Fallback: same name .bin
            return
                Path.ChangeExtension(
                    cuePath, ".bin");
        }
    }
}
