using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DimWidget
{
    internal sealed class AvailableUpdate : EventArgs
    {
        public readonly string Tag;
        public readonly Version Version;
        public readonly Uri DownloadPage;

        public AvailableUpdate(string tag, Version version)
        {
            Tag = tag;
            Version = version;
            // Build the browser destination ourselves instead of launching a URL
            // supplied in the network response.
            DownloadPage = new Uri(UpdateChecker.RepositoryPage + "/releases/tag/" + Uri.EscapeDataString(tag));
        }

        public string DisplayVersion
        {
            get { return "v" + Version.ToString(Version.Revision == 0 ? 3 : 4); }
        }
    }

    internal sealed class UpdateChecker : IDisposable
    {
        public const string RepositoryPage = "https://github.com/Aaryan6/dim-widget";
        public static readonly Uri DownloadPage = new Uri(RepositoryPage + "/releases/latest");
        private static readonly Uri LatestRelease = new Uri("https://api.github.com/repos/Aaryan6/dim-widget/releases/latest");
        private readonly HttpClient _client;
        private readonly Version _currentVersion;

        public UpdateChecker() : this(new HttpClientHandler {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = false
        }, new Version(AppVersion.Current)) { }

        internal UpdateChecker(HttpMessageHandler handler, Version currentVersion)
        {
            // This small app is compiled against Framework reference assemblies
            // whose legacy TLS defaults can otherwise reject GitHub's HTTPS.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            _currentVersion = Normalize(currentVersion);
            _client = new HttpClient(handler);
            _client.Timeout = TimeSpan.FromSeconds(10);
            _client.MaxResponseContentBufferSize = 1024 * 1024;
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("DimWidget/" + AppVersion.Current);
            _client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            _client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
        }

        public async Task<AvailableUpdate> CheckAsync(CancellationToken cancellation)
        {
            using (HttpResponseMessage response = await _client.GetAsync(LatestRelease, cancellation).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                byte[] body = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                using (MemoryStream stream = new MemoryStream(body, false))
                {
                    Release release = (Release)new DataContractJsonSerializer(typeof(Release)).ReadObject(stream);
                    Version latestVersion;
                    if (release == null || release.Draft || release.Prerelease
                        || !TryParseVersion(release.Tag, out latestVersion)
                        || latestVersion <= _currentVersion)
                        return null;

                    // A published tag without the executable is not ready for users.
                    if (release.Assets != null)
                    {
                        foreach (Asset asset in release.Assets)
                        {
                            if (asset != null && string.Equals(asset.Name, "DimWidget.exe", StringComparison.Ordinal)
                                && string.Equals(asset.State, "uploaded", StringComparison.Ordinal))
                                return new AvailableUpdate(release.Tag.Trim(), latestVersion);
                        }
                    }
                    return null;
                }
            }
        }

        internal static bool TryParseVersion(string tag, out Version version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(tag)) return false;
            string value = tag.Trim();
            if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase)) value = value.Substring(1);
            int metadata = value.IndexOf('+');
            if (metadata >= 0) value = value.Substring(0, metadata);
            if (!Version.TryParse(value, out version)) return false;
            version = Normalize(version);
            return true;
        }

        private static Version Normalize(Version value)
        {
            return new Version(value.Major, value.Minor, Math.Max(0, value.Build), Math.Max(0, value.Revision));
        }

        public void Dispose() { _client.Dispose(); }

        [DataContract]
        private sealed class Release
        {
            [DataMember(Name = "tag_name", IsRequired = true)] public string Tag { get; set; }
            [DataMember(Name = "draft", IsRequired = true)] public bool Draft { get; set; }
            [DataMember(Name = "prerelease", IsRequired = true)] public bool Prerelease { get; set; }
            [DataMember(Name = "assets")] public Asset[] Assets { get; set; }
        }

        [DataContract]
        private sealed class Asset
        {
            [DataMember(Name = "name")] public string Name { get; set; }
            [DataMember(Name = "state")] public string State { get; set; }
        }
    }
}
