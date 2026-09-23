using System.ComponentModel.DataAnnotations.Schema;

namespace ToDo.Entities;

/// <summary>
/// 会议纪要 ↔ 项目 多对多关联表。
/// 一场会议可以关联多个项目；MeetingMinutes.ProjectId 保留为"首项目"（第一个选中的），
/// 用于列表页筛选、面包屑、简报、权限判断等单项目场景。
/// </summary>
public class MeetingMinutesProject
{
    public int MeetingMinutesId { get; set; }

    public int ProjectId { get; set; }

    /// <summary>是否是首项目（等于 MeetingMinutes.ProjectId）</summary>
    public bool IsPrimary { get; set; }

    [ForeignKey(nameof(MeetingMinutesId))]
    public MeetingMinutes MeetingMinutes { get; set; } = null!;

    [ForeignKey(nameof(ProjectId))]
    public Project Project { get; set; } = null!;
}
