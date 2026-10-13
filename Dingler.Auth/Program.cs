using System.Security.Cryptography;
using Dingler.Auth.AuthenticationService;
using Dingler.Auth.Endpoints;
using Dingler.Auth.Logging;
using Dingler.Data.Configuration;
using Dingler.Data.Context;
using Dingler.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Serilog.Events;

namespace Dingler.Auth
{
    public static class Program
    {
        public static async Task Main(string[] args)
        {
#if DEBUG
            Console.Title = "(DEBUG) Dingler.Auth";
#else
            Console.Title = "Dingler.Auth";
#endif

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                ContentRootPath = AppContext.BaseDirectory
            });

            
            builder.Host.UseSerilog((context, services, configuration) =>
            {
              var innerSink = new LoggerConfiguration()
                    .ReadFrom.Configuration(context.Configuration)
                    .ReadFrom.Services(services)
                    .CreateLogger();

              configuration
                  .MinimumLevel.Verbose()
                  .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
                  .MinimumLevel.Override("System", LogEventLevel.Warning)
                  //.MinimumLevel.Override("Microsoft.AspNetCore.Hosting.Diagnostics", LogEventLevel.Fatal)
                  .WriteTo.Sink(new SecureSink(innerSink));
            });

            var connectionString = SqliteConnection.ResolveDataSource(
                builder.Configuration.GetConnectionString("HexCredentials"));
            SqliteConnection.EnsureDirectoryExists(connectionString);

            builder.Services
                .AddSqliteDbContext<HexCredentialsContext>(connectionString);

            var signingKey = LoadOrCreateSigningKey(Path.Combine(builder.Environment.ContentRootPath, "signing.key"));

            builder.Services
                .AddScoped<IAuthenticationService, JwtAuthenticationService>()
                .AddSingleton(signingKey);
            
            var app = builder.Build();

            using (var scope = app.Services.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<HexCredentialsContext>();
                await context.Database.MigrateAsync();
            }

            app.MapAuthEndpoints();
            app.MapOpenIdEndpoints(signingKey);
            app.UseHttpsRedirection();

            await app.RunAsync();
        }

        private static RsaSecurityKey LoadOrCreateSigningKey(string path)
        {
            var rsa = RSA.Create(2048);

            if (File.Exists(path))
            {
                rsa.ImportRSAPrivateKey(File.ReadAllBytes(path), out _);
            }
            else
            {
                File.WriteAllBytes(path, rsa.ExportRSAPrivateKey());
            }

            return new RsaSecurityKey(rsa);
        }
    }
    
    
}
