using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace OneSTools.EventLog.Exporter.Manager
{
    public class Program
    {
        public static void Main(string[] args)
        {
            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args)
        {
            return Host.CreateDefaultBuilder(args)
                .UseWindowsService()
                .UseSystemd()
                .UseSerilog((context, configuration) =>
                {
                    var logPath = Path.Combine(context.HostingEnvironment.ContentRootPath, "log.txt");
                    configuration
                        .Enrich.FromLogContext()
                        .WriteTo.Console()
                        .WriteTo.File(logPath);
                })
                .ConfigureServices((_, services) => { services.AddHostedService<ExportersManager>(); });
        }
    }
}