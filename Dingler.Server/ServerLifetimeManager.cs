using Dingler.Server.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Dingler.Server
{
    public sealed class ServerLifetimeManager : IDisposable
    {
        private readonly IServiceProvider _serviceProvider;
        private IServiceScope? _serviceScope;
        private IDinglerServer? _server;

        public bool IsServerRunning => _server?.IsRunning ?? false;

        public ServerLifetimeManager(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public async Task StartServerAsync()
        {
            _serviceScope = _serviceProvider.CreateScope();
            
            _server = _serviceScope.ServiceProvider.GetRequiredService<IDinglerServer>();

            if (_server is null)
                throw new Exception("Could not create new server");

            await _server.StartAsync().ConfigureAwait(false);
        }

        public async Task StopServerAsync()
        {
            if (_server is null)
                return;
            
            await _server.StopAsync().ConfigureAwait(false);
            _serviceScope?.Dispose();
            _serviceScope = null;
            _server = null;
        }

        public T? GetServerService<T>() where T : class
        {
            return _serviceScope?.ServiceProvider.GetService<T>();
        }

        public void Dispose()
        {
            _serviceScope?.Dispose();
            _serviceScope = null;
        }
    }
}
