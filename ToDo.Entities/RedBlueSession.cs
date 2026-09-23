using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ToDo.Entities;

public enum RedBlueSessionStatus
{
    Draft = 0,
    Running = 1,
    Completed = 2,
    Failed = 3
}

[Table("red_blue_sessions")]
public class RedBlueSession
{
    public int Id { get; set; }
    public int? ProjectId { get; set; }
    public int CreatedById { get; set; }

    [Required, MaxLength(200)]
    public string Topic { get; set; } = string.Empty;

    public string Objective { get; set; } = string.Empty;
    public int MaxRounds { get; set; } = 3;
    public int CurrentRound { get; set; }
    public RedBlueSessionStatus Status { get; set; } = RedBlueSessionStatus.Draft;

    [MaxLength(20)]
    public string Winner { get; set; } = string.Empty;

    public string FinalDecision { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = AppTime.Now;
    public DateTime? CompletedAt { get; set; }

    public ICollection<RedBlueRound> Rounds { get; set; } = new List<RedBlueRound>();
}

[Table("red_blue_rounds")]
public class RedBlueRound
{
    public int Id { get; set; }
    public int RedBlueSessionId { get; set; }
    public int RoundNumber { get; set; }
    public string RedArgument { get; set; } = string.Empty;
    public string BlueArgument { get; set; } = string.Empty;

    [MaxLength(20)]
    public string Winner { get; set; } = string.Empty;

    public string JudgeDecision { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = AppTime.Now;
}
