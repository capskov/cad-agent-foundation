// Modified for CAD Agent Foundation by capskov (2026); derived from Bimwright DWG MCP pinned at 4d0f75646631186612af926944d7e33affc4757d. See NOTICE.md.
namespace Bimwright.Dwg.Plugin
{
    public static class McpResponsePrivacy
    {
        public static object FilterResult(string commandName, object result)
            => ResponseSizeGuard.ApplyResult(result);

        public static object FilterResult(string commandName, object result, int maxSerializedBytes)
            => ResponseSizeGuard.ApplyResult(result, maxSerializedBytes);

        public static string SanitizeError(string error)
            => ErrorSanitizer.Sanitize(error);
    }
}
