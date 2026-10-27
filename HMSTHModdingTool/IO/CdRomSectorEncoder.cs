using System;
using System.IO;

namespace HMSTHModdingTool.IO
{
    /// <summary>
    /// Encodes 2048-byte user-data sectors as CD-ROM XA Mode 2 Form 1 raw
    /// sectors. The generated sector contains sync, MSF/header, duplicated
    /// subheader, EDC, and P/Q ECC, so the data is structurally consistent
    /// with a CUE TRACK MODE2/2352 declaration.
    /// </summary>
    public static class CdRomSectorEncoder
    {
        public const int RawSectorSize = 2352;
        public const int UserDataSize = 2048;
        public const int UserDataOffset = 24;
        public const int EdcOffset = 2072;
        public const int EccPOffset = 2076;
        public const int EccQOffset = 2248;

        private const int HeaderOffset = 12;
        private const int SubheaderOffset = 16;
        private const int EdcInputOffset = 16;
        private const int EdcInputLength = 2056;
        private const int EccMajorCountP = 86;
        private const int EccMinorCountP = 24;
        private const int EccMajorCountQ = 52;
        private const int EccMinorCountQ = 43;

        private static readonly byte[] SyncPattern = new byte[]
        {
            0x00, 0xFF, 0xFF, 0xFF,
            0xFF, 0xFF, 0xFF, 0xFF,
            0xFF, 0xFF, 0xFF, 0x00
        };

        private static readonly uint[] EdcTable;
        private static readonly byte[] EccForwardTable;
        private static readonly byte[] EccBackwardTable;

        static CdRomSectorEncoder()
        {
            EdcTable = new uint[256];
            for (int i = 0; i < EdcTable.Length; i++)
            {
                uint value = (uint)i;
                for (int bit = 0; bit < 8; bit++)
                {
                    value = (value & 1U) != 0
                        ? (value >> 1) ^ 0xD8018001U
                        : value >> 1;
                }
                EdcTable[i] = value;
            }

            EccForwardTable = new byte[256];
            EccBackwardTable = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                int doubled = i << 1;
                if ((i & 0x80) != 0)
                    doubled ^= 0x11D;

                byte forward = (byte)doubled;
                EccForwardTable[i] = forward;
                EccBackwardTable[i ^ forward] = (byte)i;
            }
        }

        /// <summary>
        /// Encodes one logical sector. logicalSector is the zero-based sector
        /// index in the image; the 150-sector lead-in is added for the MSF
        /// header. This encoder intentionally rejects MSF minutes above 99
        /// rather than producing an invalid BCD header.
        /// </summary>
        public static void EncodeMode2Form1(
            byte[] rawSector,
            byte[] userData,
            int userDataOffset,
            long logicalSector,
            bool allowMinuteWrap = false)
        {
            EncodeMode2Form1Core(
                rawSector,
                userData,
                userDataOffset,
                null,
                0,
                logicalSector,
                allowMinuteWrap);
        }

        /// <summary>
        /// Encodes a Mode 2 Form 1 sector while preserving the four-byte XA
        /// subheader from a headerless 2336-byte Mode 2 sector.
        /// </summary>
        public static void EncodeMode2Form1(
            byte[] rawSector,
            byte[] userData,
            int userDataOffset,
            byte[] subheaderData,
            int subheaderOffset,
            long logicalSector,
            bool allowMinuteWrap = false)
        {
            if (subheaderData == null)
                throw new ArgumentNullException("subheaderData");
            if (subheaderOffset < 0 || subheaderOffset > subheaderData.Length - 4)
            {
                throw new ArgumentOutOfRangeException(
                    "subheaderOffset",
                    "The source must contain a complete four-byte subheader.");
            }

            EncodeMode2Form1Core(
                rawSector,
                userData,
                userDataOffset,
                subheaderData,
                subheaderOffset,
                logicalSector,
                allowMinuteWrap);
        }

        private static void EncodeMode2Form1Core(
            byte[] rawSector,
            byte[] userData,
            int userDataOffset,
            byte[] subheaderData,
            int subheaderOffset,
            long logicalSector,
            bool allowMinuteWrap)
        {
            if (rawSector == null)
                throw new ArgumentNullException("rawSector");
            if (userData == null)
                throw new ArgumentNullException("userData");
            if (rawSector.Length < RawSectorSize)
                throw new ArgumentException(
                    "The raw sector buffer must be at least 2352 bytes.",
                    "rawSector");
            if (userDataOffset < 0 ||
                userDataOffset > userData.Length - UserDataSize)
            {
                throw new ArgumentOutOfRangeException(
                    "userDataOffset",
                    "The source buffer must contain a complete 2048-byte sector at the requested offset.");
            }
            if (logicalSector < 0)
                throw new ArgumentOutOfRangeException("logicalSector");

            long absoluteFrame = checked(logicalSector + 150L);
            long minute = absoluteFrame / (60L * 75L);
            if (minute > 99 && !allowMinuteWrap)
            {
                throw new InvalidDataException(
                    "The image exceeds the two-digit MSF minute range for a CD-ROM raw sector. " +
                    "A MODE2/2352 BIN cannot be generated with a valid sector header for this LBA.");
            }

            // Capture optional source subheader bytes before clearing the
            // destination. This also avoids corruption if the caller supplies
            // a source view backed by the same array.
            byte subFileNumber = 0x00;
            byte subChannelNumber = 0x00;
            byte subMode = 0x08;
            byte subCodingInfo = 0x00;
            if (subheaderData != null)
            {
                if (subheaderOffset < 0 || subheaderOffset > subheaderData.Length - 4)
                {
                    throw new ArgumentOutOfRangeException(
                        "subheaderOffset",
                        "The source must contain a complete four-byte subheader.");
                }

                subFileNumber = subheaderData[subheaderOffset];
                subChannelNumber = subheaderData[subheaderOffset + 1];
                subMode = subheaderData[subheaderOffset + 2];
                subCodingInfo = subheaderData[subheaderOffset + 3];
                if ((subMode & 0x20) != 0)
                {
                    throw new InvalidDataException(
                        "Mode 2 Form 2 subheaders cannot be encoded as 2048-byte Form 1 sectors.");
                }
            }

            // Temporary raw sectors used only to patch an ISO can exceed the
            // CD MSF minute range; their headers are discarded when the image
            // is converted back to 2048-byte sectors. Persistent BIN output
            // keeps the default strict validation above.
            int encodedMinute = (int)(minute % 100L);
            int second = (int)((absoluteFrame / 75L) % 60L);
            int frame = (int)(absoluteFrame % 75L);

            Array.Clear(rawSector, 0, RawSectorSize);
            Array.Copy(SyncPattern, 0, rawSector, 0, SyncPattern.Length);
            rawSector[HeaderOffset] = ToBcd(encodedMinute);
            rawSector[HeaderOffset + 1] = ToBcd(second);
            rawSector[HeaderOffset + 2] = ToBcd(frame);
            rawSector[HeaderOffset + 3] = 0x02;

            // A default data subheader is used for 2048-byte ISO inputs;
            // 2336-byte Mode 2 inputs preserve their original XA subheader.
            rawSector[SubheaderOffset] = subFileNumber;
            rawSector[SubheaderOffset + 1] = subChannelNumber;
            rawSector[SubheaderOffset + 2] = subMode;
            rawSector[SubheaderOffset + 3] = subCodingInfo;
            Array.Copy(
                rawSector, SubheaderOffset,
                rawSector, SubheaderOffset + 4,
                4);

            Array.Copy(
                userData, userDataOffset,
                rawSector, UserDataOffset,
                UserDataSize);

            uint edc = ComputeEdc(
                rawSector,
                EdcInputOffset,
                EdcInputLength);
            WriteUInt32LittleEndian(rawSector, EdcOffset, edc);

            // For Mode 2 Form 1, the address/mode bytes are zero while P/Q
            // parity is calculated. Restore them after both parity passes.
            byte minuteByte = rawSector[HeaderOffset];
            byte secondByte = rawSector[HeaderOffset + 1];
            byte frameByte = rawSector[HeaderOffset + 2];
            byte modeByte = rawSector[HeaderOffset + 3];
            Array.Clear(rawSector, HeaderOffset, 4);

            ComputeEcc(
                rawSector,
                HeaderOffset,
                EccMajorCountP,
                EccMinorCountP,
                2,
                86,
                rawSector,
                EccPOffset);
            ComputeEcc(
                rawSector,
                HeaderOffset,
                EccMajorCountQ,
                EccMinorCountQ,
                86,
                88,
                rawSector,
                EccQOffset);

            rawSector[HeaderOffset] = minuteByte;
            rawSector[HeaderOffset + 1] = secondByte;
            rawSector[HeaderOffset + 2] = frameByte;
            rawSector[HeaderOffset + 3] = modeByte;
        }

        private static uint ComputeEdc(
            byte[] data,
            int offset,
            int count)
        {
            if (offset < 0 || count < 0 || offset > data.Length - count)
                throw new ArgumentOutOfRangeException("count");

            uint edc = 0;
            int end = offset + count;
            for (int i = offset; i < end; i++)
            {
                int tableIndex = (int)((edc ^ data[i]) & 0xFFU);
                edc = (edc >> 8) ^ EdcTable[tableIndex];
            }
            return edc;
        }

        private static void ComputeEcc(
            byte[] source,
            int sourceOffset,
            int majorCount,
            int minorCount,
            int majorMultiplier,
            int minorIncrement,
            byte[] destination,
            int destinationOffset)
        {
            int size = checked(majorCount * minorCount);
            if (sourceOffset < 0 || sourceOffset > source.Length - size)
                throw new ArgumentOutOfRangeException("sourceOffset");
            if (destinationOffset < 0 ||
                destinationOffset > destination.Length - (majorCount * 2))
            {
                throw new ArgumentOutOfRangeException("destinationOffset");
            }

            for (int major = 0; major < majorCount; major++)
            {
                int index = ((major >> 1) * majorMultiplier) + (major & 1);
                int eccA = 0;
                int eccB = 0;

                for (int minor = 0; minor < minorCount; minor++)
                {
                    int value = source[sourceOffset + index];
                    index += minorIncrement;
                    if (index >= size)
                        index -= size;

                    eccA ^= value;
                    eccB ^= value;
                    eccA = EccForwardTable[eccA];
                }

                eccA = EccBackwardTable[EccForwardTable[eccA] ^ eccB];
                destination[destinationOffset + major] = (byte)eccA;
                destination[destinationOffset + major + majorCount] =
                    (byte)(eccA ^ eccB);
            }
        }

        private static void WriteUInt32LittleEndian(
            byte[] destination,
            int offset,
            uint value)
        {
            destination[offset] = (byte)(value & 0xFFU);
            destination[offset + 1] = (byte)((value >> 8) & 0xFFU);
            destination[offset + 2] = (byte)((value >> 16) & 0xFFU);
            destination[offset + 3] = (byte)((value >> 24) & 0xFFU);
        }

        private static byte ToBcd(int value)
        {
            if (value < 0 || value > 99)
                throw new ArgumentOutOfRangeException("value");
            return (byte)(((value / 10) << 4) | (value % 10));
        }
    }
}
