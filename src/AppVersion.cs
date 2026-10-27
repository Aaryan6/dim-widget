using System.Reflection;

[assembly: AssemblyVersion(DimWidget.AppVersion.Current)]
[assembly: AssemblyFileVersion(DimWidget.AppVersion.Current)]
[assembly: AssemblyInformationalVersion(DimWidget.AppVersion.Current)]

namespace DimWidget
{
    internal static class AppVersion
    {
        // Bump this to match the GitHub release tag before building a release.
        public const string Current = "1.0.6";
    }
}
