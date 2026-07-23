using System.Collections.Generic;

namespace SunnyModLoader;

internal static class RuntimeContentCoordinator
{
    internal static IReadOnlyList<ModScanIssue> Rebuild(ModRegistry registry)
    {
        StoryPatchService.RebuildBranchPoints(registry);
        SpriteService.Rebuild(registry);
        IReadOnlyList<ModScanIssue> issues = SpineService.Rebuild(registry);
        AudioControlService.Rebuild(registry);
        BacklogVoiceReplayService.InvalidateScriptCache();
        return issues;
    }
}
