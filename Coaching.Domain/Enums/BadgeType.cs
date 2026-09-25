namespace Coaching.Domain.Enums;

/// <summary>
/// The badge a coach gives with praise. Stored as its number, so a new badge goes on the end and
/// no value is ever reused or reordered.
/// </summary>
public enum BadgeType
{
    Star = 0,
    Improvement = 1,
    Teamwork = 2,
    Effort = 3,
    Skill = 4,
    Leadership = 5,
    Consistency = 6,
    Breakthrough = 7,
    Hustle = 8,
    GameIq = 9,
    LoudAndClear = 10,
    FairPlay = 11,
    Clutch = 12,
    GoodEnergy = 13,
    BraveCall = 14,
    Coachable = 15
}
