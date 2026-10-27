using System;
using System.IO;
using System.Text;
using HMSTHModdingTool.IO;

namespace HMSTHModdingTool
{
    /// <summary>
    /// ISO Repair — CORRECTED
    ///
    /// Detects if "ISO" file is actually
    /// raw BIN (2352-byte sectors) and
    /// auto-converts to true ISO (2048).
    ///
    /// This is the ACTUAL fix needed for
    /// most "broken" PS2 ISOs — they are
    /// really BIN files in disguise.
    /// </summary>
    public static class IsoRepair
    {
        const int SECTOR_2048 = 2048;
        const int SECTOR_2352 = 2352;
        const int SECTOR_2336 = 2336;
        const int RAW_DATA_OFF = 24;

        // ═══════════════════════════════
        // MAIN FIX
        // ═══════════════════════════════
        public static void FixIso(
            string isoPath)
        {
            if (!File.Exists(isoPath))
                throw new FileNotFoundException(
                    "ISO not found",
                    isoPath);

            Console.ForegroundColor =
                ConsoleColor.Cyan;
            Console.WriteLine(
                "═════════════════════" +
                "════════════════════");
            Console.WriteLine(
                " ISO Auto-Repair" +
                " (BIN→ISO detection)");
            Console.WriteLine(
                "═════════════════════" +
                "════════════════════");
            Console.ResetColor();

            long fileSize =
                new FileInfo(isoPath)
                    .Length;

            int detectedSize =
                DetectSectorSize(
                    isoPath, fileSize);

            Console.WriteLine(
                "  File size:   " +
                fileSize.ToString("N0") +
                " bytes");
            Console.WriteLine(
                "  Detected as: " +
                detectedSize +
                "-byte sectors");

            // ─── Verify PVD signature
            //     at correct location
            bool pvdOk = VerifyPvd(
                isoPath, detectedSize);

            Console.WriteLine(
                "  PVD signature: " +
                (pvdOk
                    ? "OK"
                    : "MISSING"));

            // Never transform an image whose sector layout cannot be
            // corroborated by the primary volume descriptor. The detector
            // has a size-divisibility fallback, which alone is not enough
            // evidence to safely rewrite the source image.
            if (!pvdOk)
            {
                throw new InvalidDataException(
                    "The ISO primary volume descriptor (CD001) could not be verified. " +
                    "The source image was not modified.");
            }

            if (detectedSize ==
                SECTOR_2048)
            {
                Console.ForegroundColor =
                    ConsoleColor.Green;
                Console.WriteLine();
                Console.WriteLine(
                    "  ISO is already" +
                    " in correct" +
                    " 2048-byte format.");
                Console.WriteLine(
                    "  No fix needed!");
                Console.ResetColor();
                return;
            }

            if (detectedSize ==
                SECTOR_2352)
            {
                Console.ForegroundColor =
                    ConsoleColor.Yellow;
                Console.WriteLine();
                Console.WriteLine(
                    "  This file is" +
                    " actually a BIN" +
                    " (2352-byte)!");
                Console.WriteLine(
                    "  Converting" +
                    " in-place to true" +
                    " 2048-byte ISO...");
                Console.ResetColor();

                ConvertInPlace2352to2048(
                    isoPath);

                Console.ForegroundColor =
                    ConsoleColor.Green;
                Console.WriteLine();
                Console.WriteLine(
                    "  ISO fixed" +
                    " successfully!");
                Console.ResetColor();
                return;
            }

            if (detectedSize ==
                SECTOR_2336)
            {
                Console.ForegroundColor =
                    ConsoleColor.Yellow;
                Console.WriteLine();
                Console.WriteLine(
                    "  Mode 2/2336" +
                    " detected." +
                    " Converting...");
                Console.ResetColor();

                ConvertInPlace2336to2048(
                    isoPath);

                Console.ForegroundColor =
                    ConsoleColor.Green;
                Console.WriteLine(
                    "  ISO fixed!");
                Console.ResetColor();
                return;
            }

            Console.ForegroundColor =
                ConsoleColor.Yellow;
            Console.WriteLine();
            Console.WriteLine(
                "  Unknown format." +
                " Cannot auto-repair.");
            Console.ResetColor();
        }

        // ═══════════════════════════════
        // DETECT SECTOR SIZE
        // (checks CD sync pattern +
        //  looks for CD001 at LBA 16)
        // ═══════════════════════════════
        static int DetectSectorSize(
            string path, long fileSize)
        {
            // CD sync pattern:
            // 00 FF FF FF FF FF FF FF
            // FF FF FF 00
            byte[] sync = new byte[12];
            using (var fs =
                File.OpenRead(path))
                SafeFileReplacement.ReadExactly(fs, sync, sync.Length);

            bool syncOk =
                sync[0] == 0x00 &&
                sync[1] == 0xFF &&
                sync[2] == 0xFF &&
                sync[3] == 0xFF &&
                sync[4] == 0xFF &&
                sync[5] == 0xFF &&
                sync[6] == 0xFF &&
                sync[7] == 0xFF &&
                sync[8] == 0xFF &&
                sync[9] == 0xFF &&
                sync[10] == 0xFF &&
                sync[11] == 0x00;

            if (syncOk)
                return SECTOR_2352;

            // Test: is "CD001" at
            // offset 16*2048 + 1?
            if (fileSize >=
                16 * SECTOR_2048 + 6)
            {
                byte[] test = new byte[5];
                using (var fs =
                    File.OpenRead(path))
                {
                    fs.Position =
                        16 * SECTOR_2048
                        + 1;
                    SafeFileReplacement.ReadExactly(fs, test, test.Length);
                }

                if (test[0] == 0x43 &&
                    test[1] == 0x44 &&
                    test[2] == 0x30 &&
                    test[3] == 0x30 &&
                    test[4] == 0x31)
                    return SECTOR_2048;
            }

            // Test: CD001 at
            // offset 16*2352 + 24 + 1?
            if (fileSize >=
                16 * SECTOR_2352 +
                RAW_DATA_OFF + 6)
            {
                byte[] test = new byte[5];
                using (var fs =
                    File.OpenRead(path))
                {
                    fs.Position =
                        16 * SECTOR_2352
                        + RAW_DATA_OFF
                        + 1;
                    SafeFileReplacement.ReadExactly(fs, test, test.Length);
                }

                if (test[0] == 0x43 &&
                    test[1] == 0x44 &&
                    test[2] == 0x30 &&
                    test[3] == 0x30 &&
                    test[4] == 0x31)
                    return SECTOR_2352;
            }

            // Fallback: divisibility
            if (fileSize %
                SECTOR_2352 == 0)
                return SECTOR_2352;
            if (fileSize %
                SECTOR_2336 == 0 &&
                fileSize %
                SECTOR_2048 != 0)
                return SECTOR_2336;

            return SECTOR_2048;
        }

        // ═══════════════════════════════
        // VERIFY PVD (CD001 at LBA 16)
        // ═══════════════════════════════
        static bool VerifyPvd(
            string path, int sectorSize)
        {
            int dataOff = 0;
            if (sectorSize == SECTOR_2352)
            {
                // Raw Mode 1 data starts at +16; Mode 2 Form 1 data starts
                // at +24 after its XA subheader. Read the PVD sector mode
                // instead of assuming every 2352-byte image is Mode 2.
                long modeOffset = (long)16 * SECTOR_2352 + 15;
                if (modeOffset >= new FileInfo(path).Length)
                    return false;

                using (var modeStream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    modeStream.Position = modeOffset;
                    int mode = modeStream.ReadByte();
                    if (mode == 1)
                    {
                        dataOff = 16;
                    }
                    else if (mode == 2)
                    {
                        byte[] subheader = new byte[8];
                        modeStream.Position = (long)16 * SECTOR_2352 + 16;
                        SafeFileReplacement.ReadExactly(
                            modeStream, subheader, subheader.Length);
                        // Mode 2 Form 2 has 2324-byte payloads, not the
                        // 2048-byte ISO user-data layout expected here.
                        if ((subheader[2] & 0x20) != 0 ||
                            (subheader[6] & 0x20) != 0)
                        {
                            return false;
                        }
                        dataOff = RAW_DATA_OFF;
                    }
                    else
                    {
                        return false;
                    }
                }
            }
            else if (sectorSize == SECTOR_2336)
            {
                dataOff = 8;
            }

            long pvdPos =
                (long)16 * sectorSize +
                dataOff;

            long fileSize =
                new FileInfo(path).Length;

            if (pvdPos + 6 > fileSize)
                return false;

            byte[] check = new byte[6];
            using (var fs =
                File.OpenRead(path))
            {
                fs.Position = pvdPos;
                SafeFileReplacement.ReadExactly(fs, check, check.Length);
            }

            // Type 01 + "CD001"
            return
                check[0] == 0x01 &&
                check[1] == 0x43 &&
                check[2] == 0x44 &&
                check[3] == 0x30 &&
                check[4] == 0x30 &&
                check[5] == 0x31;
        }

        // ═══════════════════════════════
        // CONVERT 2352 → 2048 IN-PLACE
        // ═══════════════════════════════
        static void ConvertInPlace2352to2048(string path)
        {
            string temporaryPath =
                SafeFileReplacement.CreateTemporarySiblingPath(path);

            try
            {
                long totalSectors;
                long expectedLength;

                using (var source = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    65536,
                    FileOptions.SequentialScan))
                {
                    long sourceLength = source.Length;
                    if (sourceLength == 0 || sourceLength % SECTOR_2352 != 0)
                    {
                        throw new InvalidDataException(
                            "The source size is not a complete 2352-byte-sector image.");
                    }

                    totalSectors = sourceLength / SECTOR_2352;
                    expectedLength = checked(totalSectors * (long)SECTOR_2048);

                    Console.WriteLine("  Sectors: " + totalSectors);

                    byte[] raw = new byte[SECTOR_2352];
                    byte[] data = new byte[SECTOR_2048];

                    using (var destination = new FileStream(
                        temporaryPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        65536,
                        FileOptions.SequentialScan))
                    {
                        long report = 0;
                        for (long sector = 0; sector < totalSectors; sector++)
                        {
                            SafeFileReplacement.ReadExactly(
                                source, raw, SECTOR_2352);
                            int dataOffset;
                            if (raw[15] == 0x01)
                            {
                                dataOffset = 16;
                            }
                            else if (raw[15] == 0x02)
                            {
                                // Reject Mode 2 Form 2 sectors: they do not
                                // contain a 2048-byte user-data payload.
                                if ((raw[18] & 0x20) != 0 ||
                                    (raw[22] & 0x20) != 0)
                                {
                                    throw new InvalidDataException(
                                        "Mode 2 Form 2 sector encountered; cannot convert it to a 2048-byte ISO sector.");
                                }
                                dataOffset = RAW_DATA_OFF;
                            }
                            else
                            {
                                throw new InvalidDataException(
                                    "Unsupported raw sector mode " + raw[15] +
                                    " at LBA " + sector + ".");
                            }

                            Array.Copy(
                                raw, dataOffset,
                                data, 0, SECTOR_2048);
                            destination.Write(data, 0, SECTOR_2048);

                            if (sector - report >= 10000)
                            {
                                report = sector;
                                double percentage =
                                    (double)sector / totalSectors * 100.0;
                                Console.Write(
                                    "\r  Progress: {0:F1}%",
                                    percentage);
                            }
                        }

                        destination.Flush();
                    }
                }

                long outputLength = new FileInfo(temporaryPath).Length;
                if (outputLength != expectedLength)
                {
                    throw new IOException(
                        "Converted ISO size mismatch. Expected " +
                        expectedLength + " bytes but wrote " + outputLength + " bytes.");
                }

                string backupPath;
                if (SafeFileReplacement.IsTemporarySiblingPath(path))
                {
                    SafeFileReplacement.ReplaceTemporaryFileWithoutBackup(
                        temporaryPath, path);
                    backupPath = null;
                }
                else
                {
                    backupPath = SafeFileReplacement.ReplaceExistingWithBackup(
                        temporaryPath, path);
                }

                Console.Write("\r  Progress: 100.0%   \n");
                if (!string.IsNullOrEmpty(backupPath))
                {
                    Console.WriteLine(
                        "  Original preserved as backup: " +
                        Path.GetFileName(backupPath));
                }
            }
            finally
            {
                SafeFileReplacement.TryDeleteTemporaryFile(temporaryPath);
            }
        }

        // ═══════════════════════════════
        // CONVERT 2336 → 2048 IN-PLACE
        // ═══════════════════════════════
        static void ConvertInPlace2336to2048(string path)
        {
            string temporaryPath =
                SafeFileReplacement.CreateTemporarySiblingPath(path);

            try
            {
                long expectedLength;

                using (var source = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    65536,
                    FileOptions.SequentialScan))
                {
                    long sourceLength = source.Length;
                    if (sourceLength == 0 || sourceLength % SECTOR_2336 != 0)
                    {
                        throw new InvalidDataException(
                            "The source size is not a complete 2336-byte-sector image.");
                    }

                    long totalSectors = sourceLength / SECTOR_2336;
                    expectedLength = checked(totalSectors * (long)SECTOR_2048);

                    byte[] raw = new byte[SECTOR_2336];
                    byte[] data = new byte[SECTOR_2048];

                    using (var destination = new FileStream(
                        temporaryPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        65536,
                        FileOptions.SequentialScan))
                    {
                        for (long sector = 0; sector < totalSectors; sector++)
                        {
                            SafeFileReplacement.ReadExactly(
                                source, raw, SECTOR_2336);
                            Array.Copy(raw, 8, data, 0, SECTOR_2048);
                            destination.Write(data, 0, SECTOR_2048);
                        }

                        destination.Flush();
                    }
                }

                long outputLength = new FileInfo(temporaryPath).Length;
                if (outputLength != expectedLength)
                {
                    throw new IOException(
                        "Converted ISO size mismatch. Expected " +
                        expectedLength + " bytes but wrote " + outputLength + " bytes.");
                }

                string backupPath;
                if (SafeFileReplacement.IsTemporarySiblingPath(path))
                {
                    SafeFileReplacement.ReplaceTemporaryFileWithoutBackup(
                        temporaryPath, path);
                    backupPath = null;
                }
                else
                {
                    backupPath = SafeFileReplacement.ReplaceExistingWithBackup(
                        temporaryPath, path);
                }

                if (!string.IsNullOrEmpty(backupPath))
                {
                    Console.WriteLine(
                        "  Original preserved as backup: " +
                        Path.GetFileName(backupPath));
                }
            }
            finally
            {
                SafeFileReplacement.TryDeleteTemporaryFile(temporaryPath);
            }
        }

    }
}
