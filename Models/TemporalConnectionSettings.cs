namespace TemporalOpsBlazor.Models;

public sealed class TemporalConnectionSettings
{
    public bool UseMock { get; set; }
    public string TargetHost { get; set; } = "localhost:7233";
    public string Namespace { get; set; } = "default";
    public string? ApiKey { get; set; }
    public string Identity { get; set; } = "temporal-ops-blazor";
    public int WorkflowPageSize { get; set; } = 100;
    public int DashboardPageSize { get; set; } = 200;
    public int HistoryEventLimit { get; set; } = 120;
    public int StuckWorkflowMinutes { get; set; } = 60;
    public List<string> MonitoredTaskQueues { get; set; } = [];
    public TemporalTlsSettings Tls { get; set; } = new();
}

public sealed class TemporalTlsSettings
{
    public bool Enabled { get; set; }
    public bool Disabled { get; set; }
    public string? Domain { get; set; }
    public string? ServerRootCaCertPath { get; set; }
    public string? ClientCertPath { get; set; }
    public string? ClientPrivateKeyPath { get; set; }
}
