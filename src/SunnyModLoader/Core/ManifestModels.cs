using System.Runtime.Serialization;

#pragma warning disable CS0649 // Populated by DataContractJsonSerializer.

namespace SunnyModLoader;

[DataContract]
internal sealed class ModManifest
{
    [DataMember] public int schemaVersion;
    [DataMember] public string id;
    [DataMember] public string name;
    [DataMember] public string version;
    [DataMember] public string[] authors;
    [DataMember] public CompatibilityManifest compatibility;
    [DataMember] public DefaultsManifest defaults;
    [DataMember] public BoolSettingManifest[] settings;
    [DataMember] public ModDependencyManifest[] dependencies;
    [DataMember] public ModConflictManifest[] conflicts;

}

[DataContract]
internal sealed class ModDependencyManifest
{
    [DataMember] public string id;
    [DataMember] public string version;
}

[DataContract]
internal sealed class ModConflictManifest
{
    [DataMember] public string id;
    [DataMember] public string version;
}

[DataContract]
internal sealed class CompatibilityManifest
{
    [DataMember] public int loaderApi;
    [DataMember] public string[] gameBuilds;
}

[DataContract]
internal sealed class DefaultsManifest
{
    [DataMember] public bool enabled = true;
    [DataMember] public int priority;

    [OnDeserializing]
    private void SetDefaults(StreamingContext context)
    {
        enabled = true;
    }
}

[DataContract]
internal sealed class BoolSettingManifest
{
    [DataMember] public string id;
    [DataMember] public string label;
    [DataMember] public bool defaultValue;
}

[DataContract]
internal sealed class BranchFramePayload
{
    [DataMember] public BranchFrame[] frames;
}

[DataContract]
internal sealed class BranchFrame
{
    [DataMember] public string modId;
    [DataMember] public string branchId;
    [DataMember] public string frameId;
    [DataMember] public string scene;
    [DataMember] public int returnIndex;
    [DataMember] public bool returnAfterCall;
    [DataMember] public BranchBackgroundState background;
    [DataMember] public BranchMusicState music;
    [DataMember] public BranchCharacterState[] characters;
}

[DataContract]
internal sealed class BranchBackgroundState
{
    [DataMember] public string path;
    [DataMember] public float x;
    [DataMember] public float y;
    [DataMember] public float z;
    [DataMember] public float scale = 1f;

    [OnDeserializing]
    private void SetDefaults(StreamingContext context)
    {
        scale = 1f;
    }
}

[DataContract]
internal sealed class BranchMusicState
{
    [DataMember] public string path;
    [DataMember] public float volume = 1f;
    [DataMember] public bool isPlaying;

    [OnDeserializing]
    private void SetDefaults(StreamingContext context)
    {
        volume = 1f;
    }
}

[DataContract]
internal sealed class BranchCharacterState
{
    [DataMember] public string name;
    [DataMember] public bool visible;
    [DataMember] public float x;
    [DataMember] public float y;
    [DataMember] public float rotation;
    [DataMember] public float scale = 1f;
    [DataMember] public string emotion;
    [DataMember] public string illustration;
    [DataMember] public string animation;

    [OnDeserializing]
    private void SetDefaults(StreamingContext context)
    {
        scale = 1f;
    }
}

#pragma warning restore CS0649
