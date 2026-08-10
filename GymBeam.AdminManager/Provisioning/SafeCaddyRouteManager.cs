using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace GymBeam.AdminManager.Provisioning;

public sealed class SafeCaddyRouteManager(
    string routesDirectory,
    string caddyfilePath,
    string baseDomain,
    HttpClient httpClient,
    TimeSpan requestTimeout)
{
    private const int MaximumResponseBytes = 1024 * 1024;
    private readonly string _routesDirectory = Path.GetFullPath(routesDirectory);
    private readonly string _caddyfilePath = Path.GetFullPath(caddyfilePath);

    public async Task<ResourceResult> AddAsync(
        ProvisioningSpec spec,
        CancellationToken cancellationToken = default)
    {
        if (!HasSafeIdentity(spec))
        {
            return ResourceResult.Failure("identity_mismatch");
        }

        ResourceResult root = ValidateRoot();
        if (!root.Succeeded)
        {
            return root;
        }

        string path = RoutePath(spec);
        if (File.Exists(path) && IsLink(new FileInfo(path)))
        {
            return ResourceResult.Failure("unsafe_symlink");
        }

        string? previous = File.Exists(path)
            ? await File.ReadAllTextAsync(path, cancellationToken)
            : null;
        string route = Render(spec);
        try
        {
            await AtomicWriteAsync(path, route, cancellationToken);
            ResourceResult reload = await ReloadAsync(cancellationToken);
            if (reload.Succeeded)
            {
                return reload;
            }

            await RestoreAsync(path, previous, cancellationToken);
            await ReloadAsync(cancellationToken);
            return reload;
        }
        catch (UnauthorizedAccessException)
        {
            return ResourceResult.Failure("caddy_storage_read_only");
        }
        catch (IOException)
        {
            return ResourceResult.Failure("caddy_storage_failure");
        }
    }

    public async Task<ResourceResult> RemoveAsync(
        ProvisioningSpec spec,
        CancellationToken cancellationToken = default)
    {
        if (!HasSafeIdentity(spec))
        {
            return ResourceResult.Failure("identity_mismatch");
        }

        ResourceResult root = ValidateRoot();
        if (!root.Succeeded)
        {
            return root;
        }

        string path = RoutePath(spec);
        if (!File.Exists(path))
        {
            return ResourceResult.Success();
        }

        if (IsLink(new FileInfo(path)))
        {
            return ResourceResult.Failure("unsafe_symlink");
        }

        try
        {
            string previous = await File.ReadAllTextAsync(path, cancellationToken);
            File.Delete(path);
            ResourceResult reload = await ReloadAsync(cancellationToken);
            if (reload.Succeeded)
            {
                return reload;
            }

            await AtomicWriteAsync(path, previous, cancellationToken);
            await ReloadAsync(cancellationToken);
            return reload;
        }
        catch (UnauthorizedAccessException)
        {
            return ResourceResult.Failure("caddy_storage_read_only");
        }
        catch (IOException)
        {
            return ResourceResult.Failure("caddy_storage_failure");
        }
    }

    private async Task<ResourceResult> ReloadAsync(CancellationToken cancellationToken)
    {
        byte[] caddyfile;
        try
        {
            caddyfile = await File.ReadAllBytesAsync(_caddyfilePath, cancellationToken);
        }
        catch (IOException)
        {
            return ResourceResult.Failure("caddy_config_unavailable");
        }

        HttpCallResult adapted = await SendAsync(
            "/adapt",
            new ByteArrayContent(caddyfile)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("text/caddyfile") }
            },
            readBody: true,
            cancellationToken);
        if (adapted.StatusCode != HttpStatusCode.OK || adapted.Body is null)
        {
            return ResourceResult.Failure(adapted.Outcome == "ok" ? "caddy_validation_failed" : adapted.Outcome);
        }

        HttpCallResult loaded = await SendAsync(
            "/load",
            new ByteArrayContent(adapted.Body)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/json") }
            },
            readBody: false,
            cancellationToken);
        return loaded.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent
            ? ResourceResult.Success()
            : ResourceResult.Failure(loaded.Outcome == "ok" ? "caddy_reload_failed" : loaded.Outcome);
    }

    private async Task<HttpCallResult> SendAsync(
        string path,
        HttpContent content,
        bool readBody,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(requestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
            using HttpResponseMessage response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            byte[]? body = null;
            if (readBody && response.IsSuccessStatusCode)
            {
                if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                {
                    return new HttpCallResult(response.StatusCode, null, "caddy_invalid_response");
                }

                await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var buffer = new MemoryStream();
                byte[] chunk = new byte[8192];
                while (true)
                {
                    int read = await stream.ReadAsync(chunk, timeout.Token);
                    if (read == 0)
                    {
                        break;
                    }

                    if (buffer.Length + read > MaximumResponseBytes)
                    {
                        return new HttpCallResult(response.StatusCode, null, "caddy_invalid_response");
                    }

                    buffer.Write(chunk, 0, read);
                }

                body = buffer.ToArray();
            }

            return new HttpCallResult(response.StatusCode, body,
                response.IsSuccessStatusCode ? "ok" : path == "/adapt" ? "caddy_validation_failed" : "caddy_reload_failed");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new HttpCallResult(null, null, "caddy_timeout");
        }
        catch (HttpRequestException)
        {
            return new HttpCallResult(null, null, "caddy_unavailable");
        }
        catch (IOException)
        {
            return new HttpCallResult(null, null, "caddy_unavailable");
        }
    }

    private ResourceResult ValidateRoot()
    {
        if (!Directory.Exists(_routesDirectory))
        {
            return ResourceResult.Failure("caddy_storage_unavailable");
        }

        return IsLink(new DirectoryInfo(_routesDirectory))
            ? ResourceResult.Failure("unsafe_symlink")
            : ResourceResult.Success();
    }

    private bool HasSafeIdentity(ProvisioningSpec spec) =>
        SafeInstanceProvisioner.HasManagedIdentity(spec)
        && string.Equals(spec.Subdomain, spec.BotId, StringComparison.Ordinal)
        && string.Equals(spec.PublicHost, $"{spec.Subdomain}.{baseDomain}", StringComparison.Ordinal);

    private string RoutePath(ProvisioningSpec spec) => Path.Combine(_routesDirectory, $"{spec.BotId}.caddy");

    private static string Render(ProvisioningSpec spec) => $$"""
        http://{{spec.PublicHost}} {
            redir https://{{spec.PublicHost}}{uri} permanent
        }

        {{spec.PublicHost}} {
            encode zstd gzip
            header {
                -Server
                X-Content-Type-Options "nosniff"
                Referrer-Policy "no-referrer"
            }
            reverse_proxy {{spec.ContainerName}}:8080
        }
        """;

    private static async Task AtomicWriteAsync(string path, string content, CancellationToken cancellationToken)
    {
        string temporary = Path.Combine(
            Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            EnsureRouteFilePermissions(temporary);
            File.Move(temporary, path, overwrite: true);
            EnsureRouteFilePermissions(path);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task RestoreAsync(string path, string? previous, CancellationToken cancellationToken)
    {
        if (previous is null)
        {
            File.Delete(path);
        }
        else
        {
            await AtomicWriteAsync(path, previous, cancellationToken);
        }
    }

    private static bool IsLink(FileSystemInfo info) =>
        info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0;

    private static void EnsureRouteFilePermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite
                    | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    private sealed record HttpCallResult(HttpStatusCode? StatusCode, byte[]? Body, string Outcome);
}
