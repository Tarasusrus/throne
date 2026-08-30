namespace Throne.Application.Terminals;

public abstract record SessionSkillPackage(string Id, string Source);

public sealed record ReviewSessionSkillPackage(ReviewArtifactWriteTarget Target)
    : SessionSkillPackage(SessionSkillPackageIds.Review, SessionSkillPackageSources.Throne);

public sealed record IntentSessionSkillPackage()
    : SessionSkillPackage(SessionSkillPackageIds.Intent, SessionSkillPackageSources.Throne);

public sealed record DreamSessionSkillPackage()
    : SessionSkillPackage(SessionSkillPackageIds.Dream, SessionSkillPackageSources.Throne);

public sealed record OrchestratorSessionSkillPackage()
    : SessionSkillPackage(SessionSkillPackageIds.Orchestrator, SessionSkillPackageSources.Throne);

public static class SessionSkillPackageIds
{
    public const string Intent = "intent";
    public const string Review = "review";
    public const string Dream = "dream";
    public const string Orchestrator = "orchestrator";
}

public static class SessionSkillPackageSources
{
    public const string Throne = "throne";
}
