namespace Coaching.Domain.Enums;

public enum DrillCategory
{
    Warmup = 0,
    Technical = 1,
    Tactical = 2,
    Game = 3,
    Conditioning = 4,
    Cooldown = 5
}

public enum DrillIntensity
{
    Low = 0,
    Medium = 1,
    High = 2
}

public enum DrillSkill
{
    Serving = 0,
    Passing = 1,
    Setting = 2,
    Attacking = 3,
    Blocking = 4,
    Defense = 5,
    Conditioning = 6,
    Footwork = 7,
    // Shown to coaches as "Eyework/Reading".
    Reading = 8
}

public enum DrillVisibility
{
    Public = 0,
    Private = 1
}

// Source scope for the unified drill list endpoint. Null falls back to the legacy
// public-only listing; callers should send an explicit scope instead.
public enum DrillScope
{
    All = 0,
    Mine = 1,
    Club = 2,
    Saved = 3,
    Liked = 4,
    Public = 5
}

public enum TemplateVisibility
{
    Public = 0,
    Private = 1
}

public enum DrillAttachmentType
{
    Image = 0,
    Video = 1,
    Document = 2
}

public enum DifficultyLevel
{
    Beginner = 0,
    Intermediate = 1,
    Advanced = 2
}
