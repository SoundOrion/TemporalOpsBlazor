using Microsoft.Extensions.Options;
using Temporalio.Client;
using TemporalOpsBlazor.Models;

namespace TemporalOpsBlazor.Services;

public sealed class TemporalClientProvider
{
    private readonly TemporalConnectionSettings _settings;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Lazy<Task<TemporalClient>> _lazyClient;

    public TemporalClientProvider(IOptions<TemporalConnectionSettings> options, ILoggerFactory loggerFactory)
    {
        _settings = options.Value;
        _loggerFactory = loggerFactory;
        _lazyClient = new Lazy<Task<TemporalClient>>(ConnectAsync);
    }

    public TemporalConnectionSettings Settings => _settings;

    public Task<TemporalClient> GetClientAsync() => _lazyClient.Value;

    private async Task<TemporalClient> ConnectAsync()
    {
        var connectOptions = new TemporalClientConnectOptions(_settings.TargetHost)
        {
            Namespace = string.IsNullOrWhiteSpace(_settings.Namespace) ? "default" : _settings.Namespace,
            Identity = string.IsNullOrWhiteSpace(_settings.Identity) ? "temporal-ops-blazor" : _settings.Identity,
            LoggerFactory = _loggerFactory,
        };

        if (!string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            connectOptions.ApiKey = _settings.ApiKey;
        }

        var tls = BuildTlsOptions();
        if (tls is not null)
        {
            connectOptions.Tls = tls;
        }

        return await TemporalClient.ConnectAsync(connectOptions);
    }

    private TlsOptions? BuildTlsOptions()
    {
        if (_settings.Tls.Disabled)
        {
            return new TlsOptions { Disabled = true };
        }

        var hasTlsMaterial = _settings.Tls.Enabled
            || !string.IsNullOrWhiteSpace(_settings.Tls.Domain)
            || !string.IsNullOrWhiteSpace(_settings.Tls.ServerRootCaCertPath)
            || !string.IsNullOrWhiteSpace(_settings.Tls.ClientCertPath)
            || !string.IsNullOrWhiteSpace(_settings.Tls.ClientPrivateKeyPath);

        // The SDK automatically enables TLS for Temporal Cloud API Key connections.
        if (!hasTlsMaterial)
        {
            return null;
        }

        return new TlsOptions
        {
            Domain = EmptyToNull(_settings.Tls.Domain),
            ServerRootCACert = ReadOptionalBytes(_settings.Tls.ServerRootCaCertPath),
            ClientCert = ReadOptionalBytes(_settings.Tls.ClientCertPath),
            ClientPrivateKey = ReadOptionalBytes(_settings.Tls.ClientPrivateKeyPath),
        };
    }

    private static byte[]? ReadOptionalBytes(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return File.ReadAllBytes(path);
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
