using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Bimwright.Dwg.Plugin.Export;

namespace Bimwright.Dwg.Plugin.Drawing
{
    internal static class DrawingSaveService
    {
        internal static string Save(Document doc, string outputPath, bool? confirm, bool overwriteExisting, bool allowRepoOutput, out string error)
        {
            error = null;

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                if (confirm != true)
                {
                    error = "saving the active drawing file requires confirm=true";
                    return null;
                }

                string filename = doc.Name;
                // If it is a brand new drawing (Drawing1.dwg) and has no physical path:
                if (string.IsNullOrWhiteSpace(filename) ||
                    filename.StartsWith("Drawing", StringComparison.OrdinalIgnoreCase) && !filename.Contains("\\"))
                {
                    error = "drawing has not been saved yet; please specify an output_path";
                    return null;
                }

                doc.Database.SaveAs(filename, DwgVersion.Current);
                return filename;
            }
            else
            {
                var normalizedPath = ExportPathPolicy.ValidateAndNormalize(
                    outputPath,
                    ".dwg",
                    overwriteExisting,
                    allowRepoOutput,
                    out var pathError);

                if (normalizedPath == null)
                {
                    error = pathError;
                    return null;
                }

                // Saving onto the drawing's own file is the in-place persistence route: the
                // open document already owns that file, and a plain SaveAs to the same path
                // fails with eFilerError. Keeping the document name unchanged also keeps the
                // bound drawing identity stable so later writes are not blocked by a path change.
                bool inPlace = !string.IsNullOrEmpty(doc.Name)
                    && string.Equals(System.IO.Path.GetFullPath(normalizedPath), System.IO.Path.GetFullPath(doc.Name), StringComparison.OrdinalIgnoreCase);
                if (inPlace)
                    doc.Database.SaveAs(normalizedPath, true, DwgVersion.Current, doc.Database.SecurityParameters);
                else
                    doc.Database.SaveAs(normalizedPath, DwgVersion.Current);
                return normalizedPath;
            }
        }
    }
}
