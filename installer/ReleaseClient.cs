using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ParalivesAccessInstaller
{
    [DataContract]
    internal sealed class ReleaseAsset
    {
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "browser_download_url")] public string Url { get; set; }
        [DataMember(Name = "digest")] public string Digest { get; set; }
        [DataMember(Name = "size")] public long Size { get; set; }
    }

    [DataContract]
    internal sealed class Release
    {
        [DataMember(Name = "tag_name")] public string Tag { get; set; }
        [DataMember(Name = "draft")] public bool Draft { get; set; }
        [DataMember(Name = "prerelease")] public bool Prerelease { get; set; }
        [DataMember(Name = "assets")] public ReleaseAsset[] Assets { get; set; }
        public ReleaseAsset Package { get; private set; }

        internal void Validate()
        {
            if (Draft || Prerelease || Tag == null || !Regex.IsMatch(Tag, @"^v\d+\.\d+\.\d+$"))
                throw new InvalidDataException("GitHub did not return a supported stable release.");
            var matches = (Assets ?? new ReleaseAsset[0]).Where(a => a.Name == "ParalivesAccess-" + Tag + ".zip").ToArray();
            if (matches.Length != 1) throw new InvalidDataException("The latest release does not have one full installation ZIP. Use the GitHub release page or try again later.");
            Package = matches[0];
            ReleaseClient.CheckDownloadUrl(Package.Url);
            if (Package.Size <= 0 || Package.Size > ReleaseClient.MaximumDownload)
                throw new InvalidDataException("The installation ZIP has an unexpected size.");
            if (Package.Digest == null || !Regex.IsMatch(Package.Digest, @"^sha256:[a-fA-F0-9]{64}$"))
                throw new InvalidDataException("GitHub did not supply a SHA-256 checksum for the installation ZIP. No files have been installed.");
        }
    }

    internal sealed class DownloadProgress
    {
        internal int Percent;
        internal string Message;
    }

    internal sealed class ReleaseClient : IDisposable
    {
        internal const string Repository = "zersiax/ParalivesAccess";
        internal const long MaximumDownload = 128L * 1024 * 1024;
        private readonly HttpClient client;

        internal ReleaseClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            client = new HttpClient { Timeout = TimeSpan.FromMinutes(5), MaxResponseContentBufferSize = 1024 * 1024 };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ParalivesAccess-Installer/1.0.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }

        internal static void CheckDownloadUrl(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com"
                || !uri.AbsolutePath.StartsWith("/" + Repository + "/releases/download/", StringComparison.Ordinal)
                || uri.UserInfo.Length != 0 || !uri.IsDefaultPort || uri.Query.Length != 0)
                throw new InvalidDataException("The release download is not from the ParalivesAccess GitHub repository.");
        }

        internal async Task<Release> Latest(CancellationToken token)
        {
            using (var response = await client.GetAsync("https://api.github.com/repos/" + Repository + "/releases/latest", token).ConfigureAwait(false))
            {
                if ((int)response.StatusCode == 403 || (int)response.StatusCode == 429)
                    throw new IOException("GitHub is temporarily limiting update checks. Wait a while and try again, or download the ZIP from the release page.");
                response.EnsureSuccessStatusCode();
                using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                {
                    var release = (Release)new DataContractJsonSerializer(typeof(Release)).ReadObject(stream);
                    release.Validate();
                    return release;
                }
            }
        }

        internal async Task Download(Release release, string path, IProgress<DownloadProgress> progress, CancellationToken token)
        {
            release.Validate();
            using (var response = await client.GetAsync(release.Package.Url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[65536];
                    long total = 0;
                    int count, lastPercent = -1;
                    while ((count = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) != 0)
                    {
                        total += count;
                        if (total > MaximumDownload || total > release.Package.Size) throw new InvalidDataException("Download size exceeds the reviewed release size.");
                        await output.WriteAsync(buffer, 0, count, token).ConfigureAwait(false);
                        int percent = (int)(total * 100 / release.Package.Size);
                        if (percent != lastPercent) progress?.Report(new DownloadProgress { Percent = percent, Message = "Downloading " + release.Tag + ": " + percent + "%." });
                        lastPercent = percent;
                    }
                    if (total != release.Package.Size) throw new InvalidDataException("The download is incomplete. Try again.");
                }
            }
            token.ThrowIfCancellationRequested();
            if (!string.Equals(InstallEngine.Hash(path), release.Package.Digest.Substring(7), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The download checksum does not match GitHub. Nothing has been installed; please try again.");
        }

        public void Dispose() => client.Dispose();
    }
}
