// .NET Framework 4.x compatibility for the AutoCAD 2022 build.
// The compiler requires this marker type for init-only setters and records;
// net8 supplies it natively, .NET Framework does not.
#if NETFRAMEWORK
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
#endif
