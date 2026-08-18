using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.DesktopHost.Runtime;

public sealed class SkillAdapterRegistry
{
    private readonly IReadOnlyList<ISkillAdapter> _configuredAdapters;
    private IReadOnlyDictionary<string, ISkillAdapter>? _adapters;

    public SkillAdapterRegistry(IEnumerable<ISkillAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        _configuredAdapters = adapters.ToArray();
    }

    public void Initialize()
    {
        if (_adapters is not null)
        {
            return;
        }

        var byId = new Dictionary<string, ISkillAdapter>(StringComparer.Ordinal);
        foreach (var adapter in _configuredAdapters)
        {
            if (string.IsNullOrWhiteSpace(adapter.Descriptor.Id))
            {
                throw new InvalidOperationException("Skill Adapter 必须提供非空 Skill ID。");
            }

            if (!byId.TryAdd(adapter.Descriptor.Id.Trim(), adapter))
            {
                throw new InvalidOperationException($"Skill Adapter ID 重复：{adapter.Descriptor.Id.Trim()}");
            }
        }

        _adapters = byId;
    }

    public IReadOnlyList<string> SkillIds
    {
        get
        {
            EnsureInitialized();
            return _adapters!.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        }
    }

    public ISkillAdapter GetRequired(string skillId)
    {
        if (string.IsNullOrWhiteSpace(skillId))
        {
            throw new ArgumentException("Skill ID 不能为空。", nameof(skillId));
        }

        EnsureInitialized();
        return _adapters!.TryGetValue(skillId.Trim(), out var adapter)
            ? adapter
            : throw new KeyNotFoundException($"未注册 Skill Adapter：{skillId.Trim()}");
    }

    private void EnsureInitialized()
    {
        if (_adapters is null)
        {
            throw new InvalidOperationException("Skill Adapter 容器尚未初始化。");
        }
    }
}
