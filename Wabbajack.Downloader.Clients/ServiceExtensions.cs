using Microsoft.Extensions.DependencyInjection;
using Wabbajack.Configuration;
using Wabbajack.Networking.Http.Interfaces;

namespace Wabbajack.Downloader.Clients;

public static class ServiceExtensions
{
    public static void AddDownloaderService(this IServiceCollection services)
    {
        services.AddHttpClient("SmallFilesClient").ConfigureHttpClient(c => c.Timeout = TimeSpan.FromMinutes(5));
        services.AddSingleton<PerformanceSettings, PerformanceSettings>();
        services.AddSingleton<IDownloadClientFactory, DownloadClientFactory>();
        services.AddSingleton<IHttpDownloader, DownloaderService>();
    }
}