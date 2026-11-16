using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bimwright.Dwg.Plugin.Cad
{
    /// <summary>
    /// A small, process-local continuation token for the bounded model-space
    /// query. The signature prevents a caller from accidentally changing the
    /// offset or binding metadata; it is not an authorization token.
    /// </summary>
    internal static class CadQueryPageToken
    {
        private const int Version = 1;
        private const int MaxTokenLength = 4096;
        private const int MaxOffset = int.MaxValue - 5000;
        private const string QueryAlgorithm = "modelspace-wcs-extents-overlap-v1";
        private static readonly byte[] SigningKey = CreateSigningKey();

        internal static string ComputeFilterDigest(string entityType, string layer, int? colorIndex,
            bool includeGeometry, double? minX, double? minY, double? maxX, double? maxY)
        {
            bool hasBounds = minX.HasValue || minY.HasValue || maxX.HasValue || maxY.HasValue;
            if (hasBounds && !(minX.HasValue && minY.HasValue && maxX.HasValue && maxY.HasValue))
                throw new ArgumentException("All four rectangle bounds are required.");
            var filter = new JObject
            {
                ["algorithm"] = QueryAlgorithm,
                ["entity_type"] = NormalizeFilter(entityType),
                ["layer"] = NormalizeFilter(layer),
                ["color_index"] = colorIndex.HasValue
                    ? new JValue(colorIndex.Value)
                    : JValue.CreateNull(),
                ["include_geometry"] = includeGeometry,
                ["bounds"] = !hasBounds
                    ? JValue.CreateNull()
                    : new JObject
                    {
                        ["min_x"] = Number(minX.Value),
                        ["min_y"] = Number(minY.Value),
                        ["max_x"] = Number(maxX.Value),
                        ["max_y"] = Number(maxY.Value)
                    }
            };

            byte[] input = Encoding.UTF8.GetBytes(filter.ToString(Formatting.None));
            using (var sha = SHA256.Create())
                return ToHex(sha.ComputeHash(input));
        }

        internal static string Create(string sessionId, string fingerprint, long revision,
            string filterDigest, int offset)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("A document session is required.", nameof(sessionId));
            if (!Guid.TryParse(fingerprint, out Guid parsedFingerprint)) throw new ArgumentException("A drawing fingerprint is required.", nameof(fingerprint));
            if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
            if (string.IsNullOrWhiteSpace(filterDigest)) throw new ArgumentException("A query filter digest is required.", nameof(filterDigest));
            if (offset < 0 || offset > MaxOffset) throw new ArgumentOutOfRangeException(nameof(offset));

            var payload = new JObject
            {
                ["v"] = Version,
                ["session_id"] = sessionId,
                ["fingerprint"] = parsedFingerprint.ToString("D"),
                ["revision"] = revision,
                ["filter_digest"] = filterDigest,
                ["offset"] = offset
            };
            byte[] payloadBytes = Encoding.UTF8.GetBytes(payload.ToString(Formatting.None));
            byte[] signature = Sign(payloadBytes);
            string token = Base64UrlEncode(payloadBytes) + "." + Base64UrlEncode(signature);
            if (token.Length > MaxTokenLength)
                throw new InvalidOperationException("The generated page token exceeds its size limit.");
            return token;
        }

        internal static bool TryRead(string token, string expectedSessionId, string expectedFingerprint,
            long expectedRevision, string expectedFilterDigest, out int offset, out string error)
        {
            offset = 0;
            error = "page_token_invalid";
            if (string.IsNullOrWhiteSpace(token) || token.Length > MaxTokenLength)
                return false;

            int separator = token.IndexOf('.');
            if (separator <= 0 || separator != token.LastIndexOf('.') || separator == token.Length - 1)
                return false;

            if (!TryBase64UrlDecode(token.Substring(0, separator), out byte[] payloadBytes)
                || !TryBase64UrlDecode(token.Substring(separator + 1), out byte[] suppliedSignature)
                || suppliedSignature.Length != 32
                || !FixedTimeEquals(Sign(payloadBytes), suppliedSignature))
                return false;

            JObject payload;
            try
            {
                using (var text = new StringReader(Encoding.UTF8.GetString(payloadBytes)))
                using (var reader = new JsonTextReader(text) { MaxDepth = 4, DateParseHandling = DateParseHandling.None })
                {
                    payload = JObject.Load(reader, new JsonLoadSettings
                    {
                        DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
                    });
                    if (reader.Read()) return false;
                }
            }
            catch
            {
                return false;
            }

            string[] expectedFields = { "v", "session_id", "fingerprint", "revision", "filter_digest", "offset" };
            if (payload.Properties().Count() != expectedFields.Length
                || payload.Properties().Any(property => !expectedFields.Contains(property.Name, StringComparer.Ordinal)))
                return false;

            try
            {
                if (payload["v"]?.Type != JTokenType.Integer || payload.Value<int>("v") != Version
                    || payload["session_id"]?.Type != JTokenType.String
                    || payload["fingerprint"]?.Type != JTokenType.String
                    || payload["revision"]?.Type != JTokenType.Integer
                    || payload["filter_digest"]?.Type != JTokenType.String
                    || payload["offset"]?.Type != JTokenType.Integer)
                    return false;

                string sessionId = payload.Value<string>("session_id");
                string fingerprint = payload.Value<string>("fingerprint");
                long revision = payload.Value<long>("revision");
                string filterDigest = payload.Value<string>("filter_digest");
                long parsedOffset = payload.Value<long>("offset");
                if (revision < 0 || parsedOffset < 0 || parsedOffset > MaxOffset || parsedOffset > int.MaxValue)
                    return false;

                if (!string.Equals(sessionId, expectedSessionId, StringComparison.Ordinal)
                    || !string.Equals(fingerprint, NormalizeFingerprint(expectedFingerprint), StringComparison.Ordinal)
                    || revision != expectedRevision)
                {
                    error = "page_token_stale";
                    return false;
                }

                if (!string.Equals(filterDigest, expectedFilterDigest, StringComparison.Ordinal))
                {
                    error = "page_token_filter_mismatch";
                    return false;
                }

                offset = (int)parsedOffset;
                error = null;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static byte[] CreateSigningKey()
        {
            var key = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(key);
            return key;
        }

        private static byte[] Sign(byte[] payload)
        {
            using (var hmac = new HMACSHA256(SigningKey))
                return hmac.ComputeHash(payload);
        }

        private static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            int difference = 0;
            for (int i = 0; i < left.Length; i++) difference |= left[i] ^ right[i];
            return difference == 0;
        }

        private static bool TryBase64UrlDecode(string value, out byte[] decoded)
        {
            decoded = null;
            if (string.IsNullOrEmpty(value) || value.Any(character =>
                    !(character >= 'A' && character <= 'Z')
                    && !(character >= 'a' && character <= 'z')
                    && !(character >= '0' && character <= '9')
                    && character != '-' && character != '_'))
                return false;

            string base64 = value.Replace('-', '+').Replace('_', '/');
            switch (base64.Length % 4)
            {
                case 0: break;
                case 2: base64 += "=="; break;
                case 3: base64 += "="; break;
                default: return false;
            }

            try
            {
                decoded = Convert.FromBase64String(base64);
                // Convert.FromBase64String accepts non-zero unused trailing
                // bits. Requiring the exact unpadded encoding keeps each
                // signed payload/signature byte sequence canonical.
                return string.Equals(Base64UrlEncode(decoded), value, StringComparison.Ordinal);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static string Base64UrlEncode(byte[] value)
            => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static string NormalizeFilter(string value)
            => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

        private static string NormalizeFingerprint(string value)
            => Guid.TryParse(value, out Guid fingerprint) ? fingerprint.ToString("D") : string.Empty;

        private static string Number(double value)
            => value.ToString("R", CultureInfo.InvariantCulture);

        private static string ToHex(byte[] value)
            => BitConverter.ToString(value).Replace("-", string.Empty).ToLowerInvariant();
    }
}
