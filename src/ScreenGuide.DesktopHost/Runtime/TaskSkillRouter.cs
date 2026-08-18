using ScreenGuide.Core.Tasking;
using ScreenGuide.Skills.Abstractions;

namespace ScreenGuide.DesktopHost.Runtime;

public sealed record TaskSkillRoute(
    ISkillAdapter Adapter,
    string Capability);

/// <summary>
/// V0.2 只提供一条确定性路由：本地 Codex 项目任务。
/// </summary>
public sealed class TaskSkillRouter(SkillAdapterRegistry adapters)
{
    public TaskSkillRoute Route(AgentTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return task.Executor switch
        {
            "codex" => new TaskSkillRoute(
                adapters.GetRequired("codex.project-task"),
                "coding.execute"),
            _ => throw new NotSupportedException($"当前版本不支持任务执行器：{task.Executor}")
        };
    }
}
