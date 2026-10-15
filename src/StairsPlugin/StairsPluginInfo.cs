using System;
using Rhino;

[assembly: Rhino.PlugIns.PlugInDescription(Rhino.PlugIns.DescriptionType.Organization, "")]
[assembly: Rhino.PlugIns.PlugInDescription(Rhino.PlugIns.DescriptionType.WebSite, "")]
[assembly: Rhino.PlugIns.PlugInDescription(Rhino.PlugIns.DescriptionType.Icon, "StairsPlugin.ico")]

namespace Stairs
{
    /// <summary>Entry point of the Stairs plug-in.</summary>
    [System.Runtime.InteropServices.Guid("0AB0114C-DB22-424E-9522-9BA4E5602566")]
    public class StairsPlugin : Rhino.PlugIns.PlugIn
    {
        public static StairsPlugin Instance { get; private set; }

        public StairsPlugin()
        {
            Instance = this;
        }
    }
}
