#nullable enable

using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Bimwright.Dwg.Plugin
{
    /// <summary>Bounded single-line framing for the local job-control pipe.</summary>
    public static class PipeControlFrameProtocol
    {
        public const int MaxRequestFrameBytes = 4096;
        public const int MaxResponseFrameBytes = 8 * 1024 * 1024;
        public const int FrameDeadlineMilliseconds = 3000;

        public static bool IsAllowedControlCommand(string? command) =>
            IsControlPipeOnlyCommand(command);

        public static bool IsControlPipeOnlyCommand(string? command) =>
            string.Equals(command, "job_get", StringComparison.Ordinal)
            || string.Equals(command, "job_cancel", StringComparison.Ordinal)
            || string.Equals(command, "job_get_historical", StringComparison.Ordinal)
            || string.Equals(command, "job_write_finalize", StringComparison.Ordinal);

        public static async Task<string?> ReadFrameAsync(PipeStream pipe,
            int maximumFrameBytes = MaxRequestFrameBytes,
            int deadlineMilliseconds = FrameDeadlineMilliseconds,
            CancellationToken cancellationToken = default)
        {
            if (pipe == null)
                throw new ArgumentNullException(nameof(pipe));
            if (maximumFrameBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maximumFrameBytes));
            if (deadlineMilliseconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(deadlineMilliseconds));

            byte[] frame = new byte[maximumFrameBytes];
            byte[] oneByte = new byte[1];
            int length = 0;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                deadline.CancelAfter(deadlineMilliseconds);
                while (true)
                {
                    int read;
                    try
                    {
                        read = await pipe.ReadAsync(oneByte, 0, 1, deadline.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException("The control request exceeded its read deadline.", ex);
                    }

                    if (read == 0)
                    {
                        if (length == 0)
                            return null;
                        break;
                    }

                    if (oneByte[0] == (byte)'\n')
                        break;

                    if (length == frame.Length)
                        throw new InvalidDataException("The control request exceeded the maximum frame size.");
                    frame[length++] = oneByte[0];
                }
            }

            if (length > 0 && frame[length - 1] == (byte)'\r')
                length--;

            return new UTF8Encoding(false, true).GetString(frame, 0, length);
        }

        public static async Task WriteFrameAsync(PipeStream pipe, string frame,
            int maximumFrameBytes = MaxResponseFrameBytes,
            int deadlineMilliseconds = FrameDeadlineMilliseconds,
            CancellationToken cancellationToken = default)
        {
            if (pipe == null)
                throw new ArgumentNullException(nameof(pipe));
            if (frame == null)
                throw new ArgumentNullException(nameof(frame));
            if (maximumFrameBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maximumFrameBytes));
            if (deadlineMilliseconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(deadlineMilliseconds));
            if (frame.IndexOf('\r') >= 0 || frame.IndexOf('\n') >= 0)
                throw new InvalidDataException("The control frame must contain exactly one line.");

            byte[] bytes = new UTF8Encoding(false, true).GetBytes(frame);
            if (bytes.Length > maximumFrameBytes)
                throw new InvalidDataException("The control response exceeded the maximum frame size.");

            Array.Resize(ref bytes, bytes.Length + 1);
            bytes[bytes.Length - 1] = (byte)'\n';

            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                deadline.CancelAfter(deadlineMilliseconds);
                try
                {
                    await pipe.WriteAsync(bytes, 0, bytes.Length, deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("The control response exceeded its write deadline.", ex);
                }
            }
        }
    }
}
