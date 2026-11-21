using System.Runtime.Versioning;

// HighPop Core runs on Windows and Linux. Declaring both lets the compiler flag any call to a
// Windows-only API that is not guarded by OperatingSystem.IsWindows() or marked
// [SupportedOSPlatform("windows")], which is how Windows-only code is kept out of the Linux path.
[assembly: SupportedOSPlatform("windows")]
[assembly: SupportedOSPlatform("linux")]