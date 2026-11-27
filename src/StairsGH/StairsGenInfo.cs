using System;
using System.Drawing;
using Grasshopper.Kernel;

namespace Stairs.GH
{
    /// <summary>Grasshopper library info for StairsGen.</summary>
    public class StairsGenInfo : GH_AssemblyInfo
    {
        public override string Name => "StairsGen";
        public override Bitmap Icon => Icons.Load("stairs.png");
        public override string Description => "Stairs along a flat curve: steps, landings, structure, railings and step numbers.";
        public override Guid Id => new Guid("8D085BFE-E2B5-4EE0-A770-652056D27000");
        public override string AuthorName => "Haris";
        public override string Version => typeof(StairsGenInfo).Assembly.GetName().Version?.ToString(3) ?? "";
        public override GH_LibraryLicense License => GH_LibraryLicense.opensource;
    }

    /// <summary>The 24 px component icons, from the plug-in's own icon set.</summary>
    internal static class Icons
    {
        public static Bitmap Load(string name)
        {
            try
            {
                using var stream = typeof(Icons).Assembly.GetManifestResourceStream(name);
                if (stream == null) return null;
                using var full = new Bitmap(stream);
                return new Bitmap(full, 24, 24);
            }
            catch (Exception)
            {
                return null; // the component still works, with Grasshopper's default icon
            }
        }
    }
}
