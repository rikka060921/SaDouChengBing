public class TaskParseItem
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Deadline { get; set; } = string.Empty;
    public string Priority { get; set; } = "Medium";
    public string Status { get; set; } = "NotStarted";
    public string Group { get; set; } = string.Empty; // 分组字段
}