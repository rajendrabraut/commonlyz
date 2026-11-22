using Kamala.Application;
using Kamala.Application.Abstractions;
using Kamala.Infrastructure.Detection;
using Kamala.Infrastructure.Engines;
using Kamala.Infrastructure.Processes;
using Kamala.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Kamala.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddFilesInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<EngineOptions>(configuration.GetSection(EngineOptions.Section));
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.Section));
        services.Configure<ProcessingLimits>(configuration.GetSection(ProcessingLimits.Section));
        services.AddSingleton(TimeProvider.System);

        services.AddSingleton<ProcessRunner>();
        services.AddSingleton<EngineCapabilities>();
        services.AddSingleton<ICapabilityProvider>(sp => sp.GetRequiredService<EngineCapabilities>());
        services.AddSingleton<IFileTypeDetector, MagicByteFileTypeDetector>();

        services.AddSingleton<DiskJobStore>();
        services.AddSingleton<IJobStore>(sp => sp.GetRequiredService<DiskJobStore>());
        services.AddHostedService<JobJanitor>();

        services.AddSingleton<IImageEditor, RustImageEditor>();
        services.AddSingleton<DocEngineClient>();
        services.AddSingleton<IPdfRasterizer, PythonPdfRasterizer>();
        services.AddSingleton<IPdfComposer, PythonPdfComposer>();
        services.AddSingleton<IWordComposer, PythonWordComposer>();
        services.AddSingleton<IImageUpscaler, PythonImageUpscaler>();
        services.AddSingleton<IOfficeConverter, LibreOfficeConverter>();
        return services;
    }
}
