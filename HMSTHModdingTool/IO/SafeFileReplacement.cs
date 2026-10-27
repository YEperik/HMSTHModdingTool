using System;
using System.IO;
using System.Collections.Generic;

namespace HMSTHModdingTool.IO
{
    /// <summary>
    /// Helpers for producing a complete sibling file before replacing an
    /// existing file. Replacement keeps the original as a uniquely named
    /// backup instead of deleting it before the new file is ready.
    /// </summary>
    public static class SafeFileReplacement
    {
        private static readonly object TemporaryPathLock = new object();
        private static readonly HashSet<string> RegisteredTemporaryPaths =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static string CreateTemporarySiblingPath(string destinationPath)
        {
            return CreateTemporarySiblingPath(destinationPath, ".tmp");
        }

        /// <summary>
        /// Creates and registers a unique sibling scratch path with the requested
        /// extension. Keeping the path registered is important because ISO repair,
        /// logo patching, and LBA repair use that registration to avoid creating
        /// backups for intermediate files that are safe to discard.
        /// </summary>
        public static string CreateTemporarySiblingPath(
            string destinationPath,
            string extension)
        {
            if (string.IsNullOrWhiteSpace(destinationPath))
                throw new ArgumentException("A destination path is required.", "destinationPath");
            if (string.IsNullOrWhiteSpace(extension) ||
                extension.Length < 2 ||
                extension[0] != '.' ||
                extension.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                extension.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
                extension.IndexOf(Path.AltDirectorySeparatorChar) >= 0)
            {
                throw new ArgumentException(
                    "A file extension starting with a dot is required.", "extension");
            }

            string fullDestinationPath = Path.GetFullPath(destinationPath);
            string directory = Path.GetDirectoryName(fullDestinationPath);
            if (string.IsNullOrEmpty(directory))
                directory = Directory.GetCurrentDirectory();

            string fileName =
                ".hmsth-tmp-" + Guid.NewGuid().ToString("N") + extension;
            string temporaryPath = Path.GetFullPath(Path.Combine(directory, fileName));
            lock (TemporaryPathLock)
            {
                RegisteredTemporaryPaths.Add(temporaryPath);
            }
            return temporaryPath;
        }

        /// <summary>
        /// Reads exactly count bytes or throws when the stream ends early.
        /// </summary>
        public static void ReadExactly(Stream source, byte[] buffer, int count)
        {
            if (source == null)
                throw new ArgumentNullException("source");
            if (buffer == null)
                throw new ArgumentNullException("buffer");
            if (count < 0 || count > buffer.Length)
                throw new ArgumentOutOfRangeException("count");

            int offset = 0;
            while (offset < count)
            {
                int bytesRead = source.Read(buffer, offset, count - offset);
                if (bytesRead == 0)
                    throw new EndOfStreamException(
                        "Unexpected end of file while reading " + count + " bytes.");

                offset += bytesRead;
            }
        }

        /// <summary>
        /// Replaces an existing destination with a completed temporary file.
        /// Both files must be in the same directory so the replacement can be
        /// performed on the same volume. The original is retained as a backup.
        /// </summary>
        public static string ReplaceExistingWithBackup(
            string temporaryPath,
            string destinationPath)
        {
            if (string.IsNullOrWhiteSpace(temporaryPath))
                throw new ArgumentException("A temporary path is required.", "temporaryPath");
            if (string.IsNullOrWhiteSpace(destinationPath))
                throw new ArgumentException("A destination path is required.", "destinationPath");

            string fullTemporaryPath = Path.GetFullPath(temporaryPath);
            string fullDestinationPath = Path.GetFullPath(destinationPath);
            string temporaryDirectory = Path.GetDirectoryName(fullTemporaryPath);
            string destinationDirectory = Path.GetDirectoryName(fullDestinationPath);

            if (!string.Equals(
                temporaryDirectory,
                destinationDirectory,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    "Temporary and destination files must be in the same directory.");
            }

            if (!File.Exists(fullTemporaryPath))
                throw new FileNotFoundException(
                    "The completed temporary file was not found.",
                    fullTemporaryPath);

            if (!File.Exists(fullDestinationPath))
                throw new FileNotFoundException(
                    "The original file no longer exists; replacement was cancelled.",
                    fullDestinationPath);

            string backupPath = Path.Combine(
                destinationDirectory,
                ".hmsth-backup-" + Guid.NewGuid().ToString("N") + ".bak");

            // File.Replace fails without deleting the destination if the
            // platform cannot perform the replacement. No delete/move fallback
            // is used because that would create a data-loss window.
            File.Replace(
                fullTemporaryPath,
                fullDestinationPath,
                backupPath);

            return backupPath;
        }

        /// <summary>
        /// Returns true only for the exact path generated by
        /// CreateTemporarySiblingPath in this process. A caller-owned filename
        /// cannot bypass backup merely by copying the reserved prefix.
        /// </summary>
        public static bool IsTemporarySiblingPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            string fullPath = Path.GetFullPath(path);
            lock (TemporaryPathLock)
            {
                return RegisteredTemporaryPaths.Contains(fullPath);
            }
        }

        /// <summary>
        /// Replaces an existing application scratch file without retaining a
        /// backup. This is restricted to a destination path registered by
        /// CreateTemporarySiblingPath in this process; caller-owned paths must
        /// use ReplaceExistingWithBackup instead.
        /// </summary>
        public static void ReplaceTemporaryFileWithoutBackup(
            string temporaryPath,
            string destinationPath)
        {
            if (string.IsNullOrWhiteSpace(temporaryPath))
                throw new ArgumentException("A temporary path is required.", "temporaryPath");
            if (string.IsNullOrWhiteSpace(destinationPath))
                throw new ArgumentException("A destination path is required.", "destinationPath");

            string fullTemporaryPath = Path.GetFullPath(temporaryPath);
            string fullDestinationPath = Path.GetFullPath(destinationPath);
            string temporaryDirectory = Path.GetDirectoryName(fullTemporaryPath);
            string destinationDirectory = Path.GetDirectoryName(fullDestinationPath);

            if (!string.Equals(
                temporaryDirectory,
                destinationDirectory,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    "Temporary and destination files must be in the same directory.");
            }

            if (!IsTemporarySiblingPath(fullDestinationPath))
            {
                throw new InvalidOperationException(
                    "Backup-free replacement is permitted only for application scratch files.");
            }

            if (!File.Exists(fullTemporaryPath))
                throw new FileNotFoundException(
                    "The completed temporary file was not found.",
                    fullTemporaryPath);
            if (!File.Exists(fullDestinationPath))
                throw new FileNotFoundException(
                    "The scratch destination no longer exists; replacement was cancelled.",
                    fullDestinationPath);

            // The destination is an app-owned scratch file, not a user output.
            // If replacement is unsupported, let it fail rather than deleting
            // either path as a fallback.
            File.Replace(
                fullTemporaryPath,
                fullDestinationPath,
                null);
        }

        /// <summary>
        /// Installs a completed temporary file. Existing destinations are
        /// backed up; previously nonexistent destinations are created by move.
        /// </summary>
        public static string InstallWithBackup(
            string temporaryPath,
            string destinationPath)
        {
            if (string.IsNullOrWhiteSpace(temporaryPath))
                throw new ArgumentException("A temporary path is required.", "temporaryPath");
            if (string.IsNullOrWhiteSpace(destinationPath))
                throw new ArgumentException("A destination path is required.", "destinationPath");

            string fullTemporaryPath = Path.GetFullPath(temporaryPath);
            string fullDestinationPath = Path.GetFullPath(destinationPath);
            string temporaryDirectory = Path.GetDirectoryName(fullTemporaryPath);
            string destinationDirectory = Path.GetDirectoryName(fullDestinationPath);

            if (!string.Equals(
                temporaryDirectory,
                destinationDirectory,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    "Temporary and destination files must be in the same directory.");
            }

            if (File.Exists(fullDestinationPath))
            {
                return ReplaceExistingWithBackup(
                    fullTemporaryPath,
                    fullDestinationPath);
            }

            if (!File.Exists(fullTemporaryPath))
                throw new FileNotFoundException(
                    "The completed temporary file was not found.",
                    fullTemporaryPath);

            File.Move(fullTemporaryPath, fullDestinationPath);
            return null;
        }


        public static string WriteAllBytesWithBackup(
            string destinationPath,
            byte[] contents)
        {
            if (contents == null)
                throw new ArgumentNullException("contents");

            string temporaryPath = CreateTemporarySiblingPath(destinationPath);
            try
            {
                using (var destination = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    65536,
                    FileOptions.SequentialScan))
                {
                    destination.Write(contents, 0, contents.Length);
                    destination.Flush();
                }

                long writtenLength = new FileInfo(temporaryPath).Length;
                if (writtenLength != contents.LongLength)
                {
                    throw new IOException(
                        "Temporary file size mismatch. Expected " +
                        contents.LongLength + " bytes but wrote " + writtenLength + " bytes.");
                }

                return InstallWithBackup(temporaryPath, destinationPath);
            }
            finally
            {
                TryDeleteTemporaryFile(temporaryPath);
            }
        }

        public static string WriteAllTextWithBackup(
            string destinationPath,
            string contents)
        {
            if (contents == null)
                throw new ArgumentNullException("contents");

            string temporaryPath = CreateTemporarySiblingPath(destinationPath);
            try
            {
                using (var destination = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
                using (var writer = new StreamWriter(
                    destination,
                    new System.Text.UTF8Encoding(false)))
                {
                    writer.Write(contents);
                }

                return InstallWithBackup(temporaryPath, destinationPath);
            }
            finally
            {
                TryDeleteTemporaryFile(temporaryPath);
            }
        }

        /// <summary>
        /// Best-effort cleanup for a temporary file. Cleanup errors must not
        /// mask the conversion/replacement exception that caused the cleanup.
        /// </summary>
        public static void TryDeleteTemporaryFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            string fullPath = null;
            try
            {
                fullPath = Path.GetFullPath(path);
                if (File.Exists(fullPath))
                    File.Delete(fullPath);
            }
            catch (IOException)
            {
                // Leave the temporary file in place for manual recovery.
            }
            catch (UnauthorizedAccessException)
            {
                // Leave the temporary file in place for manual recovery.
            }
            finally
            {
                // Losing the in-memory registration is safer than continuing
                // to treat a leftover path as a trusted scratch file.
                if (fullPath != null)
                {
                    lock (TemporaryPathLock)
                    {
                        RegisteredTemporaryPaths.Remove(fullPath);
                    }
                }
            }
        }
    }
}
