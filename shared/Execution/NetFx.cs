// Cross-target runtime helpers. capskov, 2026-11-15.
// Adds .NET Framework 4.x (AutoCAD 2022) compatibility without changing the
// net8 (AutoCAD 2025/2026) code path: every helper defers to the native API
// on non-Framework targets, so existing behaviour is preserved bit-for-bit.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace Bimwright.Dwg.Shared.Execution
{
    internal static class NetFx
    {
        private static bool IsDirectorySeparator(char c) =>
            c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar;

        public static bool IsFinite(double value)
        {
#if NETFRAMEWORK
            return !double.IsNaN(value) && !double.IsInfinity(value);
#else
            return double.IsFinite(value);
#endif
        }

        public static bool IsPathFullyQualified(string path)
        {
#if NETFRAMEWORK
            if (string.IsNullOrEmpty(path))
                return false;
            string root = Path.GetPathRoot(path);
            // Drive-qualified ("C:\") or UNC ("\\server\share\"); bare "\dir" is not.
            return root != null && root.Length >= 3 && path.Length >= root.Length;
#else
            return Path.IsPathFullyQualified(path);
#endif
        }

        public static bool EndsInDirectorySeparator(string path)
        {
#if NETFRAMEWORK
            if (string.IsNullOrEmpty(path))
                return false;
            return IsDirectorySeparator(path[path.Length - 1]);
#else
            return Path.EndsInDirectorySeparator(path);
#endif
        }

        public static string TrimEndingDirectorySeparator(string path)
        {
#if NETFRAMEWORK
            if (string.IsNullOrEmpty(path))
                return path;
            string root = Path.GetPathRoot(path);
            int rootLength = root == null ? 0 : root.Length;
            int end = path.Length;
            // Never trim past the root itself ("C:\" and "\\server\share\" stay intact).
            while (end > rootLength && IsDirectorySeparator(path[end - 1]))
                end--;
            return end == path.Length ? path : path.Substring(0, end);
#else
            return Path.TrimEndingDirectorySeparator(path);
#endif
        }

        public static bool IsWindows()
        {
#if NETFRAMEWORK
            // .NET Framework only ever runs on Windows.
            return true;
#else
            return OperatingSystem.IsWindows();
#endif
        }

        public static bool IsAsciiLetterOrDigit(char value)
        {
#if NETFRAMEWORK
            return (value >= '0' && value <= '9')
                || (value >= 'A' && value <= 'Z')
                || (value >= 'a' && value <= 'z');
#else
            return char.IsAsciiLetterOrDigit(value);
#endif
        }

        public static string ToHexString(byte[] bytes)
        {
#if NETFRAMEWORK
            // BitConverter renders upper-case, dash-separated hex.
            return BitConverter.ToString(bytes).Replace("-", string.Empty);
#else
            return Convert.ToHexString(bytes);
#endif
        }

        public static byte[] Sha256(byte[] bytes)
        {
#if NETFRAMEWORK
            using (SHA256 sha = SHA256.Create())
                return sha.ComputeHash(bytes);
#else
            return SHA256.HashData(bytes);
#endif
        }

        public static string Sha256Hex(byte[] bytes) => ToHexString(Sha256(bytes));

        public static void ThrowIfNull(object value, string? paramName = null)
        {
#if NETFRAMEWORK
            if (value == null)
                throw new ArgumentNullException(paramName);
#else
            ArgumentNullException.ThrowIfNull(value, paramName);
#endif
        }

        public static void ThrowIfNullOrWhiteSpace(string value, string? paramName = null)
        {
#if NETFRAMEWORK
            if (value == null)
                throw new ArgumentNullException(paramName);
            if (value.Trim().Length == 0)
                throw new ArgumentException(
                    "The value cannot be an empty string or composed entirely of whitespace.", paramName);
#else
            ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
#endif
        }

        public static void MoveFile(string source, string destination, bool overwrite)
        {
#if NETFRAMEWORK
            if (overwrite && File.Exists(destination))
                File.Delete(destination);
            File.Move(source, destination);
#else
            File.Move(source, destination, overwrite);
#endif
        }

        public static System.IO.Pipes.PipeOptions ServerPipeOptions()
        {
#if NETFRAMEWORK
            // PipeOptions.CurrentUserOnly does not exist on .NET Framework. The pipe
            // transport is never used on AutoCAD 2022 (it runs over TCP), so this only
            // keeps the source compiling; see docs/COMPATIBILITY.md.
            return System.IO.Pipes.PipeOptions.Asynchronous;
#else
            return System.IO.Pipes.PipeOptions.Asynchronous
                | System.IO.Pipes.PipeOptions.CurrentUserOnly;
#endif
        }
    }

    /// <summary>
    /// Reference-identity comparer usable as a dictionary key comparer on any target.
    /// Replaces System.Collections.Generic.ReferenceEqualityComparer, which is not
    /// reachable from .NET Framework and is ambiguous against Roslyn's internal type.
    /// </summary>
    internal sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
    {
        public static readonly ReferenceComparer<T> Instance = new ReferenceComparer<T>();

        private ReferenceComparer() { }

        public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

        public int GetHashCode(T obj) =>
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
