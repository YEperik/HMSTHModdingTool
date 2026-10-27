using System;
using System.IO;
using System.Collections.Generic;
using HMSTHModdingTool.IO;

namespace HMSTHModdingTool
{
    public static class IsoLogoPatcher
    {
        public class PatchOptions
        {
        }

        const int SECTOR_2352 = 2352;
        const int SECTOR_2048 = 2048;
        const int DATA_OFF = 24;
        const int DATA_LEN = 2048;
        const int ECC_START = 2076;
        const int ECC_LEN = 276;
        const byte PADDING = 0xDD;

        static readonly byte[]
        SYNC_PATTERN = new byte[]
        {
            0x00, 0xFF, 0xFF, 0xFF,
            0xFF, 0xFF, 0xFF, 0xFF,
            0xFF, 0xFF, 0xFF, 0x00
        };

        static readonly
        (int offset, byte[] data)[]
        MASTER_PATCHES = new[]
        {
            (98, new byte[] {
                0x30, 0x32 }),
            (101, new byte[] {
                0x39 }),
            (103, new byte[] {
                0x35 }),
            (130, new byte[] {
                0x00 }),
            (265, new byte[] {
                0x93 }),
            (269, new byte[] {
                0xDD }),
            (272, new byte[] {
                0x01, 0x4B, 0x00,
                0x00, 0x00, 0x4A,
                0x10, 0x00, 0x00,
                0x93, 0x69, 0x95,
                0x39, 0xDD }),
            (288, new byte[] {
                0x03 }),
            (297, new byte[] {
                0x00, 0x00, 0x00,
                0x00, 0xC4 }),
            (304, new byte[] {
                0x00, 0x00 }),
            (309, new byte[] {
                0x00 }),
            (317, new byte[] {
                0x00 }),
            (319, new byte[] {
                0x00 }),
            (816, new byte[] {
                0x43, 0x44, 0x56,
                0x44, 0x47, 0x45,
                0x4E, 0x20, 0x31,
                0x2E, 0x32 }),
        };

        // ═══════════════════════════════
        // MAIN COMMAND - bool overload
        // kept for backward compat
        // ═══════════════════════════════
        public static void PatchIso(
            string isoPath,
            PatchOptions opts = null,
            bool isJap = false)
        {
            // Try auto-detect version
            // from ISO filesystem first
            GameVersion version =
                GameVersion.USA;
            if (isJap)
                version =
                    GameVersion.JAP;

            try
            {
                string detElf;
                version =
                    HMSTHModdingTool
                    .IO.HarvestIso
                    .AutoDetectGameVersion(
                        isoPath,
                        out detElf);
            }
            catch (OutOfMemoryException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    "[WARN] Game-version auto-detection failed for '" +
                    isoPath + "'; using " + (isJap ? "JAP" : "USA") +
                    " fallback: " + ex.Message);
                // Auto-detect failed; preserve the existing fallback
                // based on the isJap flag.
                if (isJap)
                    version =
                        GameVersion.JAP;
                else
                    version =
                        GameVersion.USA;
            }

            PatchIso(
                isoPath, opts, version);
        }

        // ═══════════════════════════════
        // MAIN COMMAND - GameVersion
        // ═══════════════════════════════
        public static void PatchIso(
            string isoPath,
            PatchOptions opts,
            GameVersion version)
        {
            if (!File.Exists(isoPath))
                throw new
                    FileNotFoundException(
                    "File not found",
                    isoPath);

            string vLabel;
            switch (version)
            {
                case GameVersion.JAP:
                    vLabel =
                        " fixps2logo [JAP]";
                    break;
                case GameVersion.DEMO:
                    vLabel =
                        " fixps2logo" +
                        " [JAP DEMO]";
                    break;
                default:
                    vLabel =
                        " fixps2logo [USA]";
                    break;
            }

            Console.ForegroundColor =
                ConsoleColor.Cyan;
            Console.WriteLine(
                "═════════════════════" +
                "════════════════════");
            Console.WriteLine(vLabel);
            Console.WriteLine(
                "═════════════════════" +
                "════════════════════");
            Console.ResetColor();

            int sectorSize =
                DetectSectorSize(isoPath);

            Console.WriteLine(
                "  Format: " +
                sectorSize +
                "-byte sectors");

            if (sectorSize == SECTOR_2352)
            {
                PatchBinFile(
                    isoPath, version);
            }
            else if (sectorSize ==
                     SECTOR_2048)
            {
                PatchIsoFile(
                    isoPath, version);
            }
            else
            {
                Console.ForegroundColor =
                    ConsoleColor.Yellow;
                Console.WriteLine(
                    "  ERROR:" +
                    " Unknown format!");
                Console.ResetColor();
                throw new InvalidDataException(
                    "Unknown or unsupported image sector format; no logo patch was applied.");
            }

            Console.ForegroundColor =
                ConsoleColor.Green;
            Console.WriteLine();
            Console.WriteLine("  Done!");
            Console.ResetColor();
        }

        // ═══════════════════════════════
        // PATCH BIN FILE (2352 bytes)
        // GameVersion overload
        // ═══════════════════════════════
        static void PatchBinFile(
            string path,
            GameVersion version)
        {
            string temporaryPath =
                SafeFileReplacement.CreateTemporarySiblingPath(path);

            try
            {
                long originalLength = new FileInfo(path).Length;
                ValidatePatchImage(path, SECTOR_2352, DATA_OFF);
                if (originalLength % SECTOR_2352 != 0)
                {
                    throw new InvalidDataException(
                        "BIN image length is not an exact multiple of 2352 bytes.");
                }

                // Patch a sibling copy; never edit the user's BIN in place.
                File.Copy(path, temporaryPath);
                if (new FileInfo(temporaryPath).Length != originalLength)
                {
                    throw new IOException(
                        "Temporary BIN copy size does not match the source image.");
                }

                PatchBinFileInPlace(temporaryPath, version);

                long patchedLength = new FileInfo(temporaryPath).Length;
                if (patchedLength != originalLength)
                {
                    throw new IOException(
                        "BIN patch changed the image length unexpectedly.");
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
                        "  Original BIN preserved as backup: " + backupPath);
                }
            }
            finally
            {
                SafeFileReplacement.TryDeleteTemporaryFile(temporaryPath);
            }
        }

        static void PatchBinFileInPlace(
            string path,
            GameVersion version)
        {
            List<Blob> blobs;
            byte padByte;

            switch (version)
            {
                case GameVersion.JAP:
                    blobs = GetEmbeddedBlobsJap();
                    padByte = 0x45;
                    break;
                case GameVersion.DEMO:
                    blobs = GetEmbeddedBlobsDemo();
                    padByte = 0x9D;
                    break;
                default:
                    blobs = GetEmbeddedBlobs();
                    padByte = PADDING;
                    break;
            }

            var eccData = GetEmbeddedEcc();
            Console.WriteLine(
                "  " + blobs.Count + " logo blobs (embedded)");

            using (var fs = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None))
            {
                DoPatchWork(fs, blobs, eccData, padByte);
                fs.Flush();
            }
        }

        // ═══════════════════════════════
        // PATCH BIN FILE (2352 bytes)
        // Legacy bool overload kept
        // ═══════════════════════════════
        static void PatchBinFile(
            string path,
            bool isJap = false)
        {
            PatchBinFile(
                path,
                isJap
                    ? GameVersion.JAP
                    : GameVersion.USA);
        }

        // ═══════════════════════════════
        // PATCH ISO FILE (2048 bytes)
        // GameVersion overload
        // ═══════════════════════════════
        static void PatchIsoFile(
            string isoPath,
            GameVersion version)
        {
            long originalLength = new FileInfo(isoPath).Length;
            ValidatePatchImage(isoPath, SECTOR_2048, 0);

            if (originalLength == 0 || originalLength % SECTOR_2048 != 0)
            {
                throw new InvalidDataException(
                    "ISO image length is not an exact multiple of 2048 bytes.");
            }

            string tempBin =
                SafeFileReplacement.CreateTemporarySiblingPath(isoPath);
            string tempIso =
                SafeFileReplacement.CreateTemporarySiblingPath(isoPath);

            try
            {
                Console.WriteLine("  Converting ISO to temporary BIN...");
                Convert2048To2352(isoPath, tempBin);

                Console.WriteLine("  Patching temporary BIN...");
                PatchBinFileInPlace(tempBin, version);

                Console.WriteLine("  Converting patched BIN to temporary ISO...");
                Convert2352To2048(tempBin, tempIso);

                long convertedLength = new FileInfo(tempIso).Length;
                if (convertedLength != originalLength)
                {
                    throw new IOException(
                        "Patched ISO size mismatch. Expected " + originalLength +
                        " bytes but wrote " + convertedLength + " bytes.");
                }

                ValidatePatchImage(tempIso, SECTOR_2048, 0);
                string backupPath;
                if (SafeFileReplacement.IsTemporarySiblingPath(isoPath))
                {
                    SafeFileReplacement.ReplaceTemporaryFileWithoutBackup(
                        tempIso, isoPath);
                    backupPath = null;
                }
                else
                {
                    backupPath = SafeFileReplacement.ReplaceExistingWithBackup(
                        tempIso, isoPath);
                }

                if (!string.IsNullOrEmpty(backupPath))
                {
                    Console.WriteLine(
                        "  Original ISO preserved as backup: " + backupPath);
                }
                Console.WriteLine(
                    "  ISO format preserved (2048 bytes/sector)");
            }
            finally
            {
                SafeFileReplacement.TryDeleteTemporaryFile(tempBin);
                SafeFileReplacement.TryDeleteTemporaryFile(tempIso);
            }
        }

        // ═══════════════════════════════
        // PATCH ISO FILE (2048 bytes)
        // Legacy bool overload kept
        // ═══════════════════════════════
        static void PatchIsoFile(
            string isoPath,
            bool isJap = false)
        {
            PatchIsoFile(
                isoPath,
                isJap
                    ? GameVersion.JAP
                    : GameVersion.USA);
        }

        // ═══════════════════════════════
        // CORE PATCH WORK
        // Unchanged from your original
        // ═══════════════════════════════
        static void DoPatchWork(
            FileStream fs,
            List<Blob> blobs,
            Dictionary<int, byte[]>
                eccData,
            byte padding = PADDING)
        {
            Console.WriteLine();
            Console.WriteLine(
                "  [1/4] Logo sectors" +
                " 0-11...");

            for (int lba = 0;
                 lba <= 11; lba++)
            {
                byte[] sec =
                    BuildLogoSector(
                        lba,
                        blobs,
                        padding);
                WriteSectorData(
                    fs, lba, sec);
            }

            Console.WriteLine(
                "  [2/4] Master markers" +
                " 14-15...");

            for (int lba = 14;
                 lba <= 15; lba++)
            {
                PatchMasterSector(
                    fs, lba);
            }

            Console.WriteLine(
                "  [3/4] EDC recalc...");

            for (int lba = 0;
                 lba <= 15; lba++)
            {
                if (lba == 12 ||
                    lba == 13)
                    continue;
                ComputeEdc(fs, lba);
            }

            Console.WriteLine(
                "  [4/4] ECC write...");

            foreach (var kvp in eccData)
            {
                WriteEcc(
                    fs,
                    kvp.Key,
                    kvp.Value);
            }
        }

        // ═══════════════════════════════
        // CONVERT 2048 → 2352
        // Unchanged from your original
        // ═══════════════════════════════
        static void Convert2048To2352(
            string inPath,
            string outPath)
        {
            long originalLength = new FileInfo(inPath).Length;
            if (originalLength == 0 || originalLength % SECTOR_2048 != 0)
            {
                throw new InvalidDataException(
                    "ISO source length is not an exact multiple of 2048 bytes.");
            }

            long numSectors = originalLength / SECTOR_2048;
            if (numSectors > int.MaxValue)
            {
                throw new InvalidDataException(
                    "ISO contains too many sectors for the patcher's LBA representation.");
            }
            long expectedLength = checked(numSectors * (long)SECTOR_2352);

            using (var input = new FileStream(
                inPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.SequentialScan))
            using (var output = new FileStream(
                outPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.SequentialScan))
            {
                byte[] dataBuf = new byte[SECTOR_2048];
                byte[] rawBuf = new byte[SECTOR_2352];

                for (long lba = 0; lba < numSectors; lba++)
                {
                    SafeFileReplacement.ReadExactly(
                        input, dataBuf, dataBuf.Length);
                    BuildRawSector(rawBuf, dataBuf, (int)lba);
                    output.Write(rawBuf, 0, SECTOR_2352);
                }
                output.Flush();
            }

            long outputLength = new FileInfo(outPath).Length;
            if (outputLength != expectedLength)
            {
                throw new IOException(
                    "Temporary BIN size mismatch. Expected " + expectedLength +
                    " bytes but wrote " + outputLength + " bytes.");
            }
        }

        // ═══════════════════════════════
        // CONVERT 2352 → 2048
        // Unchanged from your original
        // ═══════════════════════════════
        static void Convert2352To2048(
            string inPath,
            string outPath)
        {
            long originalLength = new FileInfo(inPath).Length;
            if (originalLength == 0 || originalLength % SECTOR_2352 != 0)
            {
                throw new InvalidDataException(
                    "BIN source length is not an exact multiple of 2352 bytes.");
            }

            long numSectors = originalLength / SECTOR_2352;
            long expectedLength = checked(numSectors * (long)SECTOR_2048);

            using (var input = new FileStream(
                inPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.SequentialScan))
            using (var output = new FileStream(
                outPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.SequentialScan))
            {
                byte[] rawBuf = new byte[SECTOR_2352];
                byte[] dataBuf = new byte[SECTOR_2048];

                for (long lba = 0; lba < numSectors; lba++)
                {
                    SafeFileReplacement.ReadExactly(
                        input, rawBuf, rawBuf.Length);
                    Array.Copy(
                        rawBuf, DATA_OFF, dataBuf, 0, SECTOR_2048);
                    output.Write(dataBuf, 0, SECTOR_2048);
                }
                output.Flush();
            }

            long outputLength = new FileInfo(outPath).Length;
            if (outputLength != expectedLength)
            {
                throw new IOException(
                    "Temporary ISO size mismatch. Expected " + expectedLength +
                    " bytes but wrote " + outputLength + " bytes.");
            }
        }

        // ═══════════════════════════════
        // BUILD RAW SECTOR
        // Unchanged from your original
        // ═══════════════════════════════
        static void BuildRawSector(
            byte[] raw,
            byte[] data,
            int lba)
        {
            Array.Clear(
                raw, 0, SECTOR_2352);

            Array.Copy(
                SYNC_PATTERN, 0,
                raw, 0, 12);

            int minutes =
                (lba + 150) / (60 * 75);
            int seconds =
                ((lba + 150) / 75) % 60;
            int frames =
                (lba + 150) % 75;

            raw[12] = ToBcd(minutes);
            raw[13] = ToBcd(seconds);
            raw[14] = ToBcd(frames);
            raw[15] = 0x02;

            Array.Copy(
                data, 0,
                raw, DATA_OFF,
                SECTOR_2048);
        }

        static byte ToBcd(int val)
        {
            return (byte)(
                ((val / 10) << 4) |
                (val % 10));
        }

        // ═══════════════════════════════
        // BLOB CLASS
        // Unchanged from your original
        // ═══════════════════════════════
        class Blob
        {
            public int Sector;
            public int Offset;
            public byte[] Data;
        }

        // ═══════════════════════════════
        // BLOB PARSERS
        // GetEmbeddedBlobs and
        // GetEmbeddedBlobsJap unchanged
        // GetEmbeddedBlobsDemo is NEW
        // ═══════════════════════════════
        static List<Blob>
            GetEmbeddedBlobs()
        {
            byte[] raw =
                Convert.FromBase64String(
                    BLOBS_B64);

            var result =
                new List<Blob>();

            int pos = 0;
            while (pos < raw.Length)
            {
                int sec =
                    raw[pos]
                    | (raw[pos + 1]
                       << 8);
                int off =
                    raw[pos + 2]
                    | (raw[pos + 3]
                       << 8);
                int len =
                    raw[pos + 4]
                    | (raw[pos + 5]
                       << 8);
                pos += 6;

                byte[] data =
                    new byte[len];
                Array.Copy(
                    raw, pos,
                    data, 0, len);
                pos += len;

                result.Add(
                    new Blob
                    {
                        Sector = sec,
                        Offset = off,
                        Data = data
                    });
            }
            return result;
        }

        static List<Blob>
            GetEmbeddedBlobsJap()
        {
            byte[] raw =
                Convert.FromBase64String(
                    BLOBS_B64_JAP);

            var result =
                new List<Blob>();

            int pos = 0;
            while (pos < raw.Length)
            {
                int sec =
                    raw[pos] |
                    (raw[pos + 1] << 8);
                int off =
                    raw[pos + 2] |
                    (raw[pos + 3] << 8);
                int len =
                    raw[pos + 4] |
                    (raw[pos + 5] << 8);
                pos += 6;

                byte[] data =
                    new byte[len];
                Array.Copy(
                    raw, pos,
                    data, 0, len);
                pos += len;

                result.Add(new Blob
                {
                    Sector = sec,
                    Offset = off,
                    Data = data
                });
            }
            return result;
        }

        // ═══════════════════════════════
        // NEW - DEMO BLOB PARSER
        // ═══════════════════════════════
        static List<Blob>
            GetEmbeddedBlobsDemo()
        {
            byte[] raw =
                Convert.FromBase64String(
                    BLOBS_B64_DEMO);

            var result =
                new List<Blob>();

            int pos = 0;
            while (pos < raw.Length)
            {
                int sec =
                    raw[pos] |
                    (raw[pos + 1] << 8);
                int off =
                    raw[pos + 2] |
                    (raw[pos + 3] << 8);
                int len =
                    raw[pos + 4] |
                    (raw[pos + 5] << 8);
                pos += 6;

                byte[] data =
                    new byte[len];
                Array.Copy(
                    raw, pos,
                    data, 0, len);
                pos += len;

                result.Add(new Blob
                {
                    Sector = sec,
                    Offset = off,
                    Data = data
                });
            }
            return result;
        }

        // ═══════════════════════════════
        // ECC PARSER
        // Unchanged from your original
        // ═══════════════════════════════
        static Dictionary<int, byte[]>
            GetEmbeddedEcc()
        {
            byte[] raw =
                Convert.FromBase64String(
                    ECC_B64);

            var result =
                new Dictionary<int,
                    byte[]>();

            int pos = 0;
            while (pos < raw.Length)
            {
                int sec =
                    raw[pos]
                    | (raw[pos + 1]
                       << 8);
                pos += 2;

                byte[] ecc =
                    new byte[ECC_LEN];
                Array.Copy(
                    raw, pos,
                    ecc, 0, ECC_LEN);
                pos += ECC_LEN;

                result[sec] = ecc;
            }
            return result;
        }

        // ═══════════════════════════════
        // BUILD LOGO SECTOR
        // Unchanged from your original
        // ═══════════════════════════════
        static byte[] BuildLogoSector(
            int lba,
            List<Blob> blobs,
            byte padding = PADDING)
        {
            byte[] data =
                new byte[DATA_LEN];

            for (int i = 0;
                 i < DATA_LEN; i++)
                data[i] = padding;

            foreach (var b in blobs)
            {
                if (b.Sector != lba)
                    continue;
                int copyLen = Math.Min(
                    b.Data.Length,
                    DATA_LEN - b.Offset);
                if (copyLen > 0 &&
                    b.Offset >= 0 &&
                    b.Offset < DATA_LEN)
                {
                    Array.Copy(
                        b.Data, 0,
                        data, b.Offset,
                        copyLen);
                }
            }
            return data;
        }

        // ═══════════════════════════════
        // WRITE SECTOR DATA
        // Unchanged from your original
        // ═══════════════════════════════
        static void WriteSectorData(
            FileStream fs,
            int lba,
            byte[] data)
        {
            long pos =
                (long)lba *
                SECTOR_2352 +
                DATA_OFF;
            fs.Position = pos;
            fs.Write(data, 0, DATA_LEN);
        }

        // ═══════════════════════════════
        // PATCH MASTER SECTOR
        // Unchanged from your original
        // ═══════════════════════════════
        static void PatchMasterSector(
            FileStream fs, int lba)
        {
            long dataPos =
                (long)lba *
                SECTOR_2352 +
                DATA_OFF;

            byte[] sec =
                new byte[DATA_LEN];

            fs.Position = dataPos;
            SafeFileReplacement.ReadExactly(fs, sec, DATA_LEN);

            foreach (var patch
                     in MASTER_PATCHES)
            {
                Array.Copy(
                    patch.data, 0,
                    sec,
                    patch.offset,
                    patch.data.Length);
            }

            fs.Position = dataPos;
            fs.Write(sec, 0, DATA_LEN);
        }

        // ═══════════════════════════════
        // WRITE ECC
        // Unchanged from your original
        // ═══════════════════════════════
        static void WriteEcc(
            FileStream fs,
            int lba,
            byte[] ecc)
        {
            long pos =
                (long)lba *
                SECTOR_2352 +
                ECC_START;
            fs.Position = pos;
            fs.Write(ecc, 0, ECC_LEN);
        }

        // ═══════════════════════════════
        // EDC TABLE + COMPUTE EDC
        // Unchanged from your original
        // ═══════════════════════════════
        static uint[] edcTable;

        static void InitEdcTable()
        {
            if (edcTable != null)
                return;
            edcTable = new uint[256];
            for (uint i = 0;
                 i < 256; i++)
            {
                uint e = i;
                for (int j = 0;
                     j < 8; j++)
                {
                    if ((e & 1) != 0)
                        e = (e >> 1)
                            ^ 0xD8018001u;
                    else
                        e = e >> 1;
                }
                edcTable[i] = e;
            }
        }

        static void ComputeEdc(
            FileStream fs, int lba)
        {
            InitEdcTable();

            long secStart =
                (long)lba *
                SECTOR_2352;

            byte[] sector =
                new byte[SECTOR_2352];

            fs.Position = secStart;
            SafeFileReplacement.ReadExactly(
                fs, sector, SECTOR_2352);

            uint edc = 0;
            for (int i = 16;
                 i < 2072; i++)
            {
                edc = (edc >> 8)
                    ^ edcTable[
                        (edc ^
                         sector[i])
                        & 0xFF];
            }

            sector[2072] =
                (byte)(edc & 0xFF);
            sector[2073] =
                (byte)((edc >> 8)
                       & 0xFF);
            sector[2074] =
                (byte)((edc >> 16)
                       & 0xFF);
            sector[2075] =
                (byte)((edc >> 24)
                       & 0xFF);

            fs.Position = secStart;
            fs.Write(sector, 0,
                     SECTOR_2352);
        }

        // ═══════════════════════════════
        // DETECT SECTOR SIZE
        // Unchanged from your original
        // ═══════════════════════════════
        static int DetectSectorSize(
            string path)
        {
            long size = new FileInfo(path).Length;
            if (size >= 12 && size % SECTOR_2352 == 0)
            {
                byte[] sync = new byte[12];
                using (var fs = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    SafeFileReplacement.ReadExactly(fs, sync, sync.Length);
                }

                bool isSync = sync[0] == 0x00 && sync[11] == 0x00;
                for (int i = 1; i < 11 && isSync; i++)
                    isSync = sync[i] == 0xFF;

                if (isSync)
                    return SECTOR_2352;
            }

            if (size > 0 && size % SECTOR_2048 == 0)
                return SECTOR_2048;

            return 0;
        }

        static void ValidatePatchImage(
            string path,
            int sectorSize,
            int dataOffset)
        {
            long pvdOffset = checked(16L * sectorSize + dataOffset);
            byte[] signature = new byte[6];

            using (var source = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (source.Length < pvdOffset + signature.Length ||
                    source.Length % sectorSize != 0)
                {
                    throw new InvalidDataException(
                        "Image is truncated or is not aligned to the detected sector size.");
                }

                source.Position = pvdOffset;
                SafeFileReplacement.ReadExactly(
                    source, signature, signature.Length);
            }

            if (signature[0] != 0x01 ||
                signature[1] != (byte)'C' ||
                signature[2] != (byte)'D' ||
                signature[3] != (byte)'0' ||
                signature[4] != (byte)'0' ||
                signature[5] != (byte)'1')
            {
                throw new InvalidDataException(
                    "Image does not contain a verified CD001 primary volume descriptor at the detected sector size.");
            }
        }
    }
}
