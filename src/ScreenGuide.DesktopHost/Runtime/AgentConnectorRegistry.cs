using ScreenGuide.Agent.Abstractions;

namespace ScreenGuide.DesktopHost.Runtime;

public sealed class AgentConnectorRegistry
{
    private readonly IReadOnlyList<IAgentConnector> _configuredConnectors;
    private IReadOnlyDictionary<string, IAgentConnector>? _connectors;

    public AgentConnectorRegistry(IEnumerable<IAgentConnector> connectors)
    {
        ArgumentNullException.ThrowIfNull(connectors);
        _configuredConnectors = connectors.ToArray();
    }

    public void Initialize()
    {
        if (_connectors is not null)
        {
            return;
        }

        var byId = new Dictionary<string, IAgentConnector>(StringComparer.OrdinalIgnoreCase);
        foreach (var connector in _configuredConnectors)
        {
            if (string.IsNullOrWhiteSpace(connector.ConnectorId))
            {
                throw new InvalidOperationException("Agent Connector 必须提供非空 ConnectorId。");
            }

            if (!byId.TryAdd(connector.ConnectorId.Trim(), connector))
            {
                throw new InvalidOperationException(
                    $"Agent Connector ID 重复：{connector.ConnectorId.Trim()}");
            }
        }

        _connectors = byId;
    }

    public IReadOnlyList<string> ConnectorIds
    {
        get
        {
            EnsureInitialized();
            return _connectors!.Keys
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public bool IsInitialized => _connectors is not null;

    public IReadOnlyList<IAgentConnector> Connectors
    {
        get
        {
            EnsureInitialized();
            return _connectors!.Values.ToArray();
        }
    }

    public bool TryGet(string connectorId, out IAgentConnector? connector)
    {
        if (string.IsNullOrWhiteSpace(connectorId))
        {
            connector = null;
            return false;
        }

        EnsureInitialized();
        return _connectors!.TryGetValue(connectorId.Trim(), out connector);
    }

    public IAgentConnector GetRequired(string connectorId) =>
        TryGet(connectorId, out var connector)
            ? connector!
            : throw new KeyNotFoundException($"未注册 Agent Connector：{connectorId}");

    private void EnsureInitialized()
    {
        if (_connectors is null)
        {
            throw new InvalidOperationException("Agent Connector 容器尚未初始化。");
        }
    }
}
