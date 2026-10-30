using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Eto.Drawing;

namespace Stairs.UI
{
    /// <summary>Icons drawn from the plug-in's embedded SVGs, sharp on high-resolution screens.</summary>
    internal static class SvgIcons
    {
        public static string Read(string resource)
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
            if (stream == null) return null;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        /// <summary>
        /// The icon resource.svg, or resource_dark.svg while Rhino uses its dark theme, rendered for
        /// normal and double-density screens. Null if the resource is missing.
        /// </summary>
        public static Icon Load(string resource, int width, int height)
        {
            string svg = null;
            if (Rhino.Runtime.HostUtils.RunningInDarkMode) svg = Read(resource + "_dark.svg");
            svg ??= Read(resource + ".svg");
            if (svg == null) return null;
            try
            {
                var frames = new List<IconFrame>();
                foreach (int scale in new[] { 1, 2 })
                {
                    using var bmp = Rhino.UI.DrawingUtilities.BitmapFromSvg(svg, width * scale, height * scale);
                    var png = new MemoryStream();
                    bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                    png.Position = 0;
                    frames.Add(new IconFrame(scale, png));
                }
                return new Icon(frames);
            }
            catch (System.Exception)
            {
                return null; // the buttons still have their names
            }
        }
    }
}
