using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SunnyModLoader;

internal sealed class FlowVoiceBinding
{
    internal int DialogueOrdinal;
    internal string Speaker;
    internal string Text;
    internal string Uri;
    internal float Volume = 1f;
}

internal sealed class OverlayDefinition
{
    internal string id;
    internal string kind;
    internal string target;
    internal string source;
}

internal sealed class DialoguePatchDefinition
{
    internal string id;
    internal DialogueAnchorDefinition anchor;
    internal DialogueSetDefinition set;
}

internal sealed class DialogueAnchorDefinition
{
    internal string scene;
    internal string afterLabel;
    internal int dialogueOrdinal = -1;
    internal string expectedSpeaker;
    internal string expectedText;
}

internal sealed class DialogueSetDefinition
{
    internal string text;
    internal string voice;
    internal float volume = 1f;
}

internal sealed class BranchOptionDefinition
{
    internal string groupId;
    internal string id;
    internal string scene;
    internal string afterLabel;
    internal BranchAnchorDefinition anchor;
    internal string optionText;
    internal string story;
    internal string entryLabel;
    internal string setting;
    internal bool invertSetting;
    internal bool repeatable;
    internal bool continueCurrent;
}

internal sealed class BranchAnchorDefinition
{
    internal string afterLabel;
    internal int dialogueOrdinal;
    internal string expectedSpeaker;
    internal string expectedText;
}

internal sealed class GalleryDefinition
{
    internal string id;
    internal string title;
    internal string image;
    internal string thumbnail;
    internal bool unlockedByDefault;
}

internal sealed class SpriteImageDefinition
{
    internal string id;
    internal string source;
}

internal sealed class SpriteDefinition
{
    internal string id;
    internal string internalName;
    internal string displayName;
    internal string defaultBase;
    internal string defaultEmotion;
    internal float width;
    internal float height;
    internal float pivotX = 0.5f;
    internal float pivotY;
    internal float portraitSize;
    internal float portraitOffsetX;
    internal float portraitOffsetY;
    internal string layer = "front";
    internal int order;
    internal readonly List<SpriteImageDefinition> bases = new List<SpriteImageDefinition>();
    internal readonly List<SpriteImageDefinition> emotions = new List<SpriteImageDefinition>();
}

internal sealed class SpineDefinition
{
    internal string id;
    internal string internalName;
    internal string displayName;
    internal string bundle;
    internal string windowsBundle;
    internal string macosBundle;
    internal string androidBundle;
    internal string prefab;
    internal string defaultEmotionAnimation;
    internal readonly Dictionary<string, string> emotions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

internal sealed class FlowCallReference
{
    internal string target;
    internal string entryLabel;
    internal string location;
}

internal sealed class FlowDefinition
{
    internal string RelativePath;
    internal string SceneUri;
    internal string PreparedText;
    internal readonly List<FlowVoiceBinding> Voices = new List<FlowVoiceBinding>();
    internal readonly HashSet<string> Labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    internal readonly HashSet<string> SpriteReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    internal readonly HashSet<string> AudioResources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    internal readonly List<FlowCallReference> Calls = new List<FlowCallReference>();
}

internal sealed class FlowDeclarationNode
{
    internal string Kind;
    internal int LineNumber;
    internal readonly Dictionary<string, string> Values =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    internal readonly List<FlowDeclarationNode> Children = new List<FlowDeclarationNode>();
}

internal sealed class FlowPackageContent
{
    internal readonly List<FlowDefinition> Flows = new List<FlowDefinition>();
    internal readonly List<DialoguePatchDefinition> DialoguePatches = new List<DialoguePatchDefinition>();
    internal readonly List<BranchOptionDefinition> Branches = new List<BranchOptionDefinition>();
    internal readonly List<GalleryDefinition> Gallery = new List<GalleryDefinition>();
    internal readonly List<OverlayDefinition> Overlays = new List<OverlayDefinition>();
    internal readonly List<SpriteDefinition> Sprites = new List<SpriteDefinition>();
    internal readonly List<SpineDefinition> Spines = new List<SpineDefinition>();
}
