namespace SolidWorksAgent.Core.Validation;

public static class DocumentCleanupPolicy
{
    public static bool ShouldClose(bool finalOutput,bool openedByCommand,bool existedBefore,
        bool previousGeneratedOutput,bool modifiedBefore,bool modifiedNow,bool succeeded)
    {
        if(finalOutput)return false;
        if(openedByCommand&&!existedBefore)return true;
        return succeeded&&previousGeneratedOutput&&existedBefore&&!modifiedBefore&&!modifiedNow;
    }
}
