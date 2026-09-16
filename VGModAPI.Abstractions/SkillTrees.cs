namespace VGModAPI;

/// <summary>The game's commander specializations.</summary>
public enum CommanderSpecialization
{
    Leadership = 1, Mining, Drones, Engineering, Industrial, Salvaging, Economy, Offense, Defense
}

/// <summary>Read-only current values for a skill tree; retained values do not follow a replacement game.</summary>
public sealed class SkillTree
{
    public string Identifier { get; }
    public CommanderSpecialization? Specialization { get; }
    public int MasteryLevel { get; }
    public int MaximumLevel { get; }
    internal SkillTree(string identifier, CommanderSpecialization? specialization, int masteryLevel, int maximumLevel)
    { Identifier = identifier; Specialization = specialization; MasteryLevel = masteryLevel; MaximumLevel = maximumLevel; }
}

public interface ISkillTreeService : IServiceStatus
{
    /// <summary>Read the current commander's specialization tree. Returns null without a commander,
    /// when the tree does not exist, or when integration is unavailable. Main-thread only.</summary>
    SkillTree? Get(CommanderSpecialization specialization);
}
