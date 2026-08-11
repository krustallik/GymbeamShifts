using GymBeam.AdminManager.Configuration;
using GymBeam.AdminManager.Auditing;
using GymBeam.AdminManager.Security;
using GymBeam.AdminManager.Storage;
using GymBeam.AdminManager.Registry;
using GymBeam.AdminManager.Dashboard;
using GymBeam.AdminManager.Docker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using GymBeam.AdminManager.Lifecycle;
using GymBeam.AdminManager.Logs;
using GymBeam.AdminManager.Credentials;
using GymBeam.AdminManager.Provisioning;
using GymBeam.AdminManager.Messaging;

namespace GymBeam.AdminManager;

public static class AdminManagerApplication
{
    public static WebApplication Build(
        AdminManagerOptions options,
        string[]? args = null,
        TimeProvider? timeProvider = null,
        IDockerStatusReader? dockerStatusReader = null,
        IDockerLifecycleController? dockerLifecycleController = null,
        IDockerLogReader? dockerLogReader = null,
        ITelegramMessageSender? telegramMessageSender = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        StoragePermissions.EnsureDirectory(options.StoragePath);
        timeProvider ??= TimeProvider.System;

        WebApplicationBuilder builder = WebApplication.CreateBuilder(args ?? Array.Empty<string>());
        builder.WebHost.ConfigureKestrel(serverOptions =>
        {
            serverOptions.AddServerHeader = false;
            serverOptions.Limits.MaxRequestBodySize = 16 * 1024;
            serverOptions.Listen(options.BindAddress, options.Port);
        });
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(timeProvider);
        byte[] sessionSigningKey = SecurityKeyDerivation.Derive(
            options.Security.SessionSigningKey.Span,
            "session-signing-v1");
        byte[] csrfSigningKey = SecurityKeyDerivation.Derive(
            options.Security.SessionSigningKey.Span,
            "csrf-signing-v1");
        builder.Services.AddSingleton(new SessionTokenService(
            sessionSigningKey,
            options.Security.SessionLifetime,
            timeProvider));
        builder.Services.AddSingleton(new CsrfTokenService(
            csrfSigningKey,
            timeProvider));
        builder.Services.AddSingleton(new LoginRateLimiter(
            options.Security.LoginMaxAttempts,
            options.Security.LoginWindow,
            timeProvider));
        builder.Services.AddSingleton(new PersistentSessionStore(
            Path.Combine(options.StoragePath, "sessions.json"),
            timeProvider));
        builder.Services.AddSingleton(new AuditLogger(
            Path.Combine(options.StoragePath, "audit.jsonl"),
            timeProvider));
        var botRegistry = new PersistentBotRegistry(
            Path.Combine(options.StoragePath, "bots.json"),
            timeProvider);
        builder.Services.AddSingleton(botRegistry);
        builder.Services.AddSingleton<IManagedBotRegistry>(botRegistry);
        builder.Services.AddSingleton<IManagedBotRegistryMutations>(botRegistry);
        builder.Services.AddSingleton(serviceProvider => new ExistingBotsImporter(
            options.InstancesPath,
            options.Provisioning.BaseDomain,
            serviceProvider.GetRequiredService<PersistentBotRegistry>(),
            serviceProvider.GetRequiredService<AuditLogger>()));
        builder.Services.AddHostedService<ExistingBotsImportHostedService>();
        builder.Services.AddSingleton<SessionManager>();
        builder.Services.AddSingleton<BotOperationCoordinator>();
        HttpClient dockerHttpClient = DockerHttpClientFactory.Create(options.Docker.SocketPath);
        builder.Services.AddSingleton(dockerHttpClient);

        if (dockerStatusReader is not null)
        {
            builder.Services.AddSingleton<IDockerStatusReader>(dockerStatusReader);
        }
        else
        {
            builder.Services.AddSingleton<IDockerStatusReader>(serviceProvider => new SafeDockerStatusReader(
                serviceProvider.GetRequiredService<HttpClient>(),
                timeProvider,
                options.Docker.RequestTimeout,
                Environment.GetEnvironmentVariable("HOSTNAME")));
        }
        builder.Services.AddSingleton<IDockerLifecycleController>(dockerLifecycleController
            ?? new SafeDockerLifecycleController(
                dockerHttpClient,
                options.Docker.RequestTimeout,
                options.Docker.HealthTimeout,
                options.Docker.HealthPollInterval,
                Environment.GetEnvironmentVariable("HOSTNAME")));
        builder.Services.AddSingleton<BotLifecycleService>();
        builder.Services.AddSingleton<IDockerLogReader>(dockerLogReader
            ?? new SafeDockerLogReader(
                dockerHttpClient!,
                options.Docker.RequestTimeout,
                options.Docker.MaximumLogResponseBytes,
                Environment.GetEnvironmentVariable("HOSTNAME")));
        builder.Services.AddSingleton<BotLogService>();
        builder.Services.AddSingleton<DashboardService>();
        var credentialStore = new AtomicCredentialTransactionStore(
            options.StoragePath,
            options.InstancesPath,
            timeProvider);
        builder.Services.AddSingleton<ICredentialTransactionStore>(credentialStore);
        builder.Services.AddSingleton<CredentialUpdateService>();
        builder.Services.AddSingleton<CredentialRecoveryService>();
        builder.Services.AddHostedService<CredentialRecoveryHostedService>();
        builder.Services.AddSingleton<ITelegramMessageSender>(telegramMessageSender
            ?? new TelegramApiMessageSender(new HttpClient { Timeout = TimeSpan.FromSeconds(15) }));
        builder.Services.AddSingleton(serviceProvider => new TelegramMessagingService(
            serviceProvider.GetRequiredService<IManagedBotRegistry>(),
            serviceProvider.GetRequiredService<ITelegramMessageSender>(),
            serviceProvider.GetRequiredService<AuditLogger>(),
            options.InstancesPath));

        var instanceProvisioner = new SafeInstanceProvisioner(
            options.InstancesPath,
            options.Provisioning.BotTemplatePath);
        var dockerProvisioner = new SafeDockerProvisioner(
            dockerHttpClient,
            options.Docker.RequestTimeout,
            options.Provisioning.DockerImage,
            options.Provisioning.DockerNetwork,
            options.Provisioning.DockerHostInstancesPath,
            Environment.GetEnvironmentVariable("HOSTNAME"));
        HttpClient caddyClient = CreateUnixSocketClient(options.Provisioning.CaddyAdminSocketPath);
        var caddyRoutes = new SafeCaddyRouteManager(
            options.Provisioning.CaddyRoutesPath,
            options.Provisioning.CaddyfilePath,
            options.Provisioning.BaseDomain,
            caddyClient,
            options.Docker.RequestTimeout);
        var externalHealthClient = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = options.Docker.RequestTimeout
        });
        var provisioningResources = new ProvisioningResources(
            instanceProvisioner,
            dockerProvisioner,
            caddyRoutes,
            externalHealthClient,
            options.InstancesPath,
            options.Provisioning.MinimumFreeDiskBytes,
            options.Docker.HealthTimeout,
            options.Docker.HealthPollInterval);
        builder.Services.AddSingleton<IProvisioningResources>(provisioningResources);
        builder.Services.AddSingleton<IProvisioningTransactionStore>(
            new AtomicProvisioningTransactionStore(Path.Combine(options.StoragePath, "provisioning.json")));
        builder.Services.AddSingleton(serviceProvider => new BotProvisioningService(
            serviceProvider.GetRequiredService<IManagedBotRegistryMutations>(),
            serviceProvider.GetRequiredService<IProvisioningResources>(),
            serviceProvider.GetRequiredService<IProvisioningTransactionStore>(),
            serviceProvider.GetRequiredService<AuditLogger>(),
            serviceProvider.GetRequiredService<BotOperationCoordinator>(),
            timeProvider,
            options.Provisioning.BaseDomain));
        builder.Services.AddSingleton<ProvisioningRecoveryService>();
        builder.Services.AddHostedService<ProvisioningRecoveryHostedService>();
        builder.Services.AddSingleton<IBotBackupStore>(new AtomicBotBackupStore(
            options.InstancesPath,
            Path.Combine(options.StoragePath, "backups"),
            timeProvider,
            options.Provisioning.MinimumFreeDiskBytes));
        builder.Services.AddSingleton(serviceProvider => new BotAdministrationService(
            serviceProvider.GetRequiredService<IManagedBotRegistryMutations>(),
            serviceProvider.GetRequiredService<IProvisioningResources>(),
            serviceProvider.GetRequiredService<IDockerLifecycleController>(),
            serviceProvider.GetRequiredService<IBotBackupStore>(),
            serviceProvider.GetRequiredService<BotOperationCoordinator>(),
            serviceProvider.GetRequiredService<AuditLogger>(),
            timeProvider,
            options.Provisioning.BaseDomain));
        builder.Services.AddSingleton<BotAdministrationRecoveryService>();
        builder.Services.AddHostedService<BotAdministrationRecoveryHostedService>();

        WebApplication application = builder.Build();
        application.UseApiSecurityHeaders();
        application.UseAuthentication();
        application.UseCsrfProtection();
        application.MapGet("/healthz", () => Results.Json(new { status = "ok" }));
        application.MapAuthenticationEndpoints();
        application.MapBotRegistryEndpoints();
        application.MapBotLifecycleEndpoints();
        application.MapBotLogEndpoints();
        application.MapCredentialEndpoints();
        application.MapProvisioningEndpoints();
        application.MapTelegramMessagingEndpoints();
        return application;
    }

    private static HttpClient CreateUnixSocketClient(string socketPath)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new System.Net.Sockets.Socket(
                    System.Net.Sockets.AddressFamily.Unix,
                    System.Net.Sockets.SocketType.Stream,
                    System.Net.Sockets.ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(
                        new System.Net.Sockets.UnixDomainSocketEndPoint(socketPath),
                        cancellationToken);
                    return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri("http://localhost")
        };
    }
}
