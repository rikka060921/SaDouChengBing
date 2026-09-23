
namespace ToDo.Domain.AI
{
    public class AITaskParseResult
    {
        public bool Success { get; internal set; }
        public string ErrorMessage { get; internal set; } = string.Empty;
        internal List<TaskParseItem> Items { get; set; } = new();
    }
}
