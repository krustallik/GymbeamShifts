using GymBeam.AdminManager;
using GymBeam.AdminManager.Configuration;
using Microsoft.Extensions.Configuration;

IConfiguration configuration = new ConfigurationBuilder()
    .AddEnvironmentVariables(AdminManagerConfiguration.EnvironmentPrefix)
    .Build();
AdminManagerOptions options = AdminManagerConfiguration.Load(configuration);
WebApplication application = AdminManagerApplication.Build(options, args);

await application.RunAsync();
