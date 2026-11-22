using Kamala.Application.Conversion;
using Kamala.Application.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace Kamala.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddFilesApplication(this IServiceCollection services)
    {
        services.AddSingleton<FileIntake>();
        services.AddSingleton<JobRunner>();
        services.AddSingleton<EditImagesUseCase>();
        services.AddSingleton<UpscaleImagesUseCase>();
        services.AddSingleton<ConvertFilesUseCase>();
        services.AddSingleton<DownloadsQuery>();

        services.AddSingleton<IConversionRoute, ImagesToPdfRoute>();
        services.AddSingleton<IConversionRoute, ImagesToWordRoute>();
        services.AddSingleton<IConversionRoute, ImageToImageRoute>();
        services.AddSingleton<IConversionRoute, PdfToImagesRoute>();
        services.AddSingleton<IConversionRoute, PdfToWordRoute>();
        services.AddSingleton<IConversionRoute, WordToPdfRoute>();
        services.AddSingleton<IConversionRoute, WordToImagesRoute>();
        services.AddSingleton<IConversionRoute, WordToDocxRoute>();
        return services;
    }
}
